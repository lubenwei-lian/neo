using System.Net;
using System.Net.Http;
using Neo.Core;

namespace Neo.App.Services;

/// <summary>一个分流数据文件：展示名 / 下载地址 / 相对 runtime 的目标路径 / 最小合法体积。</summary>
public sealed record GeoTarget(string Display, string Url, string RelativePath, long MinBytes);

/// <summary>
/// 分流数据（geoip.dat / geosite.dat / Country.mmdb / sing-box rule-set）更新。
/// 为什么需要独立于内核更新：
///   · geoip.dat / geosite.dat 只随内核目录一起发布，内核不更新它就永远停在发布时那一天；
///   · 国内直连判定（geosite:cn / geoip:cn）依赖这些文件，数据一旧就会出现
///     「该直连的走了代理 / 该走代理的被直连」。
///   · 复用 <see cref="KernelDownloader"/>：断点续传 + 实时进度（不再是一次性 GetByteArrayAsync）；
///   · **允许连接中更新**：下载走本地代理（否则 GitHub 根本不可达）；
///   · 目标文件被内核占用（Windows 共享冲突）时，先落 *.pending，下次启动内核前自动替换
///     （<see cref="ApplyPending"/>），不丢下载结果；
///   · 旧文件保留 *.bak-prev。
/// </summary>
public static class GeoDataUpdater
{
    // sing-box 官方 rule-set 仓库（rule-set 分支），与 sing-box 官方文档一致。
    private const string SrsGeo = "https://raw.githubusercontent.com/SagerNet/sing-geosite/rule-set/";
    private const string SrsGeoIp = "https://raw.githubusercontent.com/SagerNet/sing-geoip/rule-set/";

    public static readonly GeoTarget[] Targets =
    [
        new("geosite.dat", "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geosite.dat", "geosite.dat", 1_000_000),
        new("geoip.dat", "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geoip.dat", "geoip.dat", 1_000_000),
        new("Country.mmdb", "https://github.com/Loyalsoldier/geoip/releases/latest/download/Country.mmdb", "Country.mmdb", 500_000),
        new("geosite-cn.srs", SrsGeo + "geosite-cn.srs", Path.Combine("rule-sets", "geosite-cn.srs"), 1_000),
        new("geoip-cn.srs", SrsGeoIp + "geoip-cn.srs", Path.Combine("rule-sets", "geoip-cn.srs"), 1_000),
        new("geosite-category-ads-all.srs", SrsGeo + "geosite-category-ads-all.srs", Path.Combine("rule-sets", "geosite-category-ads-all.srs"), 1_000),
    ];

    /// <param name="rootOverride">测试用：指定目标根目录（默认 = runtime 目录）。</param>
    /// <param name="targetsOverride">测试用：替换下载目标清单（默认 = 上表）。正式路径不传。</param>
    public static async Task<(bool Ok, string Message)> UpdateAllAsync(
        Action<string> progress, CancellationToken ct = default,
        string? rootOverride = null, IReadOnlyList<GeoTarget>? targetsOverride = null,
        Action<DownloadProgress>? onProgress = null)
    {
        var root = rootOverride ?? NeoPaths.RuntimeDir;
        var targets = targetsOverride ?? Targets;
        var okCount = 0;
        var pendingCount = 0;
        var fails = new List<string>();
        long total = 0;

        foreach (var t in targets)
        {
            ct.ThrowIfCancellationRequested();
            progress($"下载 {t.Display}…");

            var target = Path.Combine(root, t.RelativePath);
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var downloadPath = target + ".download";

            var (ok, msg) = await KernelDownloader.DownloadAsync(t.Url, downloadPath, onProgress, ct);
            if (!ok) { fails.Add(t.Display + "：" + msg); continue; }

            try
            {
                var len = new FileInfo(downloadPath).Length;
                if (len < t.MinBytes) { fails.Add($"{t.Display}：体积异常（{len} 字节 < {t.MinBytes}）"); continue; }

                //   比随包全量少约 10 万个后缀）。用户点一次「更新分流数据」不能把全量清单换回精简版，
                //   否则 TUN 下大量国内域名会改走代理（表现为国内站变慢/流量暴涨）。
                if (t.RelativePath.EndsWith(".srs", StringComparison.OrdinalIgnoreCase) && File.Exists(target))
                {
                    long existingLen = 0;
                    try { existingLen = new FileInfo(target).Length; } catch { }
                    if (existingLen > 0 && len * 2 < existingLen)
                    {
                        fails.Add($"{t.Display}：上游是精简规则集（{len} 字节，本地全量 {existingLen} 字节），已保留本地全量版本");
                        continue;
                    }
                }

                // 校验：数据文件没有官方哈希；只做「不是网页」的粗检，避免把错误页当数据。
                var head = new byte[4];
                await using (var fs = new FileStream(downloadPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    await fs.ReadAsync(head.AsMemory(0, 4), ct);
                }
                if (head[0] == (byte)'<' || (head[0] == 0xEF && head[1] == 0xBB))
                {
                    fails.Add(t.Display + "：返回的是网页/文本，不是数据文件");
                    continue;
                }

                try
                {
                    KernelInstall.ReplaceAtomically(downloadPath, target, target + ".bak-prev");
                    total += len;
                    okCount++;
                }
                catch (IOException)
                {
                    // 内核正在使用该文件（Windows 共享冲突）：留作 .pending，下次启动内核前应用。
                    var pending = target + ".pending";
                    try
                    {
                        if (File.Exists(pending)) File.Delete(pending);
                        File.Move(downloadPath, pending, overwrite: true);
                        pendingCount++;
                    }
                    catch (Exception ex2)
                    {
                        fails.Add($"{t.Display}：文件被占用且暂存失败（{ex2.GetType().Name}）");
                    }
                }
            }
            catch (Exception ex)
            {
                fails.Add($"{t.Display}：写入失败（{ex.GetType().Name} {ex.Message}）");
            }
            finally
            {
                try { if (File.Exists(downloadPath)) File.Delete(downloadPath); } catch { }
                try { KernelDownloader.DiscardPart(downloadPath); } catch { }
            }
        }

        if (okCount == 0 && pendingCount == 0)
            return (false, "分流数据更新失败：\n" + string.Join("\n", fails));

        try
        {
            AppState.State.GeoUpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            AppState.Save();
        }
        catch { }

        var summary = $"已更新 {okCount}/{targets.Count} 个数据文件（{DownloadProgress.FormatBytes(total)}）"
                    + (pendingCount > 0 ? $"，{pendingCount} 个将在下次连接时生效" : "")
                    + (fails.Count > 0 ? "\n\n失败项：\n" + string.Join("\n", fails) : "")
                    + "\n\n旧文件保留为 *.bak-prev。";
        return (okCount + pendingCount == targets.Count, summary);
    }

    /// 启动内核前调用：把上次因文件被占用而暂存的 *.pending 顶上去。
    /// 返回实际替换的数量（0 = 没有待应用项）。
    public static int ApplyPending(string? rootOverride = null)
    {
        var root = rootOverride ?? NeoPaths.RuntimeDir;
        var applied = 0;
        try
        {
            foreach (var pending in Directory.EnumerateFiles(root, "*.pending", SearchOption.AllDirectories))
            {
                var target = pending[..^".pending".Length];
                try
                {
                    KernelInstall.ReplaceAtomically(pending, target, target + ".bak-prev");
                    applied++;
                }
                catch { /* 下次再试 */ }
            }
        }
        catch { }
        if (applied > 0)
        {
            try { LogBus.App($"已应用 {applied} 个暂存的分流数据文件（上次更新时被内核占用）", "Info"); } catch { }
        }
        return applied;
    }
}
