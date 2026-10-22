using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Neo.Core;

namespace Neo.App.Services;

/// <summary>下载进度快照（供 UI 直接渲染：百分比、总量、速度）。</summary>
public sealed record DownloadProgress(long Received, long Total, double BytesPerSecond)
{
    public int Percent => Total > 0 ? (int)Math.Clamp(Received * 100 / Total, 0, 100) : -1;

    /// <summary>UI 文案：43% · 9.4/22.0 MB · 1.2 MB/s（无总长时省略百分比）。</summary>
    public string Text()
    {
        var recv = FormatBytes(Received);
        var speed = BytesPerSecond > 0 ? $" · {FormatBytes((long)BytesPerSecond)}/s" : "";
        return Total > 0
            ? $"{Percent}% · {recv}/{FormatBytes(Total)}{speed}"
            : $"{recv}{speed}";
    }

    public static string FormatBytes(long b) => b switch
    {
        >= 1024L * 1024 * 1024 => $"{b / 1024.0 / 1024 / 1024:F2} GB",
        >= 1024L * 1024 => $"{b / 1024.0 / 1024:F1} MB",
        >= 1024L => $"{b / 1024.0:F0} KB",
        _ => $"{b} B",
    };
}

/// <summary>
/// 内核/数据文件下载器：**断点续传 + 实时进度 + 哈希校验**。
/// 为什么需要它（2026-10-23 用户反馈）：
///   · 旧实现是 HttpClient.GetByteArrayAsync：下载 20–60 MB 期间**没有任何进度**，
///     UI 只能干等，用户会以为卡死/被误导；
///   · 没有续传：网络一抖/用户取消就要从头再来；校园网/热点下 32KB/s 时等于永远下不完；
///   · 下载完成后没有统一的哈希校验入口（xray 有 .dgst、sing-box/mihomo 只有 API digest）。
/// 设计：
///   · 落盘用 <target>.part，Range 请求续传；服务端忽略 Range（200）时自动从头写；
///   · 每条连接读取 90 秒无数据视为卡死 → 换下一条通道（socks → 直连 → 系统代理），.part 保留；
///   · 取消（用户点取消）**不删 .part**，下次点更新继续；
///   · 完成后由 <see cref="VerifySha256Async"/> 校验，再交给安装流程。
/// </summary>
public static class KernelDownloader
{
    private const int ChunkSize = 128 * 1024;
    private static readonly TimeSpan ReadStallTimeout = TimeSpan.FromSeconds(90);

    public static string PartPath(string targetFile) => targetFile + ".part";

    /// <summary>删除残留的 .part（仅在安装成功或哈希不匹配后调用）。</summary>
    public static void DiscardPart(string targetFile)
    {
        try { if (File.Exists(PartPath(targetFile))) File.Delete(PartPath(targetFile)); } catch { }
    }

    /// <summary>
    /// 下载到 targetFile（可续传）。成功时 targetFile 就位、.part 被移走；
    /// 失败/取消时 .part 保留，返回消息里带已下载字节数。
    /// </summary>
    public static async Task<(bool Ok, string Message)> DownloadAsync(
        string url, string targetFile, Action<DownloadProgress>? onProgress, CancellationToken ct)
    {
        var part = PartPath(targetFile);
        var dir = Path.GetDirectoryName(targetFile);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var lastError = "";
        foreach (var kind in new[] { "socks", "direct", "sys" })
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var ok = await TryOnceAsync(url, part, kind, onProgress, ct);
                if (ok)
                {
                    try
                    {
                        if (File.Exists(targetFile)) File.Delete(targetFile);
                        File.Move(part, targetFile, overwrite: true);
                    }
                    catch (Exception ex)
                    {
                        return (false, "下载完成但落盘失败：" + ex.Message);
                    }
                    return (true, $"已下载 {DownloadProgress.FormatBytes(new FileInfo(targetFile).Length)}");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 用户主动取消：**.part 保留**，把取消原样抛给上层（UI 显示「已暂停」而不是「失败」）。
                // 之前这里 return false 会被上层统一当成失败，界面写「失败：已暂停」——语义自相矛盾。
                throw;
            }
            catch (OperationCanceledException)
            {
                var have = SafeLength(part);
                lastError = $"通道 {kind} 读取超时（{ReadStallTimeout.TotalSeconds:F0}s 无数据）";
                ReportLocal(part, onProgress);
                _ = have;
            }
            catch (Exception ex)
            {
                lastError = $"通道 {kind} 失败：{ex.GetType().Name} {ex.Message}";
            }
        }

        var keep = SafeLength(part);
        return (false, $"下载失败（{lastError}）。"
                       + (keep > 0 ? $" 已保留断点 {DownloadProgress.FormatBytes(keep)}，网络恢复后点更新会续传。" : ""));
    }

    private static async Task<bool> TryOnceAsync(
        string url, string part, string kind, Action<DownloadProgress>? onProgress, CancellationToken ct)
    {
        using var handler = new HttpClientHandler();
        switch (kind)
        {
            case "socks":
                handler.Proxy = new WebProxy($"socks5://127.0.0.1:{Neo.Core.LocalPorts.Socks}");
                handler.UseProxy = true;
                break;
            case "sys":
                handler.UseProxy = true;
                break;
            default:
                handler.UseProxy = false;
                break;
        }

        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("neo-update-check");

        var have = SafeLength(part);
        if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && have > 0)
            return true;   // 已经下完（服务端认为 Range 越界）

        if (!resp.IsSuccessStatusCode) return false;

        var contentLen = resp.Content.Headers.ContentLength;
        long total;
        if (resp.StatusCode == HttpStatusCode.PartialContent)
        {
            total = have + (contentLen ?? 0);
        }
        else
        {
            // 服务端忽略了 Range（或本来就没有断点）→ 重头写
            have = 0;
            total = contentLen ?? 0;
            try
            {
                if (File.Exists(part)) File.Delete(part);
            }
            catch { }
        }

        await using var fs = new FileStream(part, FileMode.Append, FileAccess.Write, FileShare.Read, 64 * 1024, useAsync: true);
        await using var src = await resp.Content.ReadAsStreamAsync(ct);

        var buffer = new byte[ChunkSize];
        long received = have;
        var started = DateTime.UtcNow;
        var lastReport = DateTime.MinValue;

        while (true)
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(ReadStallTimeout);
            int read;
            try
            {
                read = await src.ReadAsync(buffer.AsMemory(), readCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("read stalled");
            }
            if (read <= 0) break;
            await fs.WriteAsync(buffer.AsMemory(0, read), ct);
            received += read;

            var now = DateTime.UtcNow;
            if ((now - lastReport).TotalMilliseconds >= 200)
            {
                lastReport = now;
                var secs = Math.Max(0.001, (now - started).TotalSeconds);
                var speed = (received - have) / secs;
                try { onProgress?.Invoke(new DownloadProgress(received, total, speed)); } catch { }
            }
        }

        try
        {
            onProgress?.Invoke(new DownloadProgress(received, total > 0 ? total : received,
                (received - have) / Math.Max(0.001, (DateTime.UtcNow - started).TotalSeconds)));
        }
        catch { }

        if (total > 0 && received < total) return false;   // 连接提前结束 → 换通道续传
        return true;
    }

    private static void ReportLocal(string part, Action<DownloadProgress>? onProgress)
    {
        try
        {
            var have = SafeLength(part);
            if (have > 0) onProgress?.Invoke(new DownloadProgress(have, 0, 0));
        }
        catch { }
    }

    private static long SafeLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; } catch { return 0; }
    }

    /// <summary>流式校验 SHA-256；expected 为空时只返回实际值（由调用方决定如何提示）。</summary>
    public static async Task<(bool Match, string Actual)> VerifySha256Async(
        string file, string? expectedHex, CancellationToken ct)
    {
        try
        {
            await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
            var hash = await SHA256.HashDataAsync(fs, ct);
            var actual = Convert.ToHexString(hash).ToLowerInvariant();
            var expected = (expectedHex ?? "").Trim().ToLowerInvariant();
            return (expected.Length == 0 || expected == actual, actual);
        }
        catch (Exception ex)
        {
            return (false, "哈希计算失败：" + ex.Message);
        }
    }
}
