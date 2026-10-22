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
///     「该直连的走了代理 / 该走代理的被直连」，用户只能换内核绕，成本极高。
/// 安全策略与内核安装一致：先写 *.new → 原子替换 → 旧文件留 .bak-prev；
/// 并且**连接中拒绝更新**（内核可能正打开着这些文件）。
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
    /// <param name="allowWhileConnected">测试用：跳过「连接中禁止更新」检查。</param>
    /// <param name="targetsOverride">测试用：替换下载目标清单（默认 = 上表）。正式路径不传。</param>
    public static async Task<(bool Ok, string Message)> UpdateAllAsync(
        Action<string> progress, CancellationToken ct = default,
        string? rootOverride = null, bool allowWhileConnected = false,
        IReadOnlyList<GeoTarget>? targetsOverride = null)
    {
        if (!allowWhileConnected && ConnectionService.IsConnected)
            return (false, "当前已连接：内核正在使用这些数据文件，请先断开连接再更新（避免读到写了一半的文件）。");

        var root = rootOverride ?? NeoPaths.RuntimeDir;
        var targets = targetsOverride ?? Targets;
        var okCount = 0;
        var fails = new List<string>();
        long total = 0;

        foreach (var t in targets)
        {
            ct.ThrowIfCancellationRequested();
            progress($"下载 {t.Display}…");
            byte[]? data;
            try { data = await DownloadAsync(t.Url, ct); }
            catch (OperationCanceledException) { throw; }
            catch { data = null; }

            if (data == null) { fails.Add(t.Display + "：网络不可达（已尝试代理 / 系统代理 / 直连）"); continue; }
            if (data.LongLength < t.MinBytes) { fails.Add($"{t.Display}：体积异常（{data.Length} 字节 < {t.MinBytes}）"); continue; }
            if (data.Length > 4 && (data[0] == (byte)'<' || (data[0] == 0xEF && data[1] == 0xBB))) { fails.Add(t.Display + "：返回的是网页/文本，不是数据文件"); continue; }

            try
            {
                var target = Path.Combine(root, t.RelativePath);
                var dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                // 先落一个独立下载文件，再交给 ReplaceAtomically 复制成 target.new + File.Replace。
                //   不要把下载文件直接命名为 target + ".new"：ReplaceAtomically 内部也写 target.new，
                //   同路径会变成「自我复制后被 finally 删掉」，实测报 FileNotFoundException（已由本测试抓到）。
                var downloadPath = target + ".download-" + Guid.NewGuid().ToString("N")[..8];
                await File.WriteAllBytesAsync(downloadPath, data, ct);
                try
                {
                    KernelInstall.ReplaceAtomically(downloadPath, target, target + ".bak-prev");
                }
                finally
                {
                    try { if (File.Exists(downloadPath)) File.Delete(downloadPath); } catch { }
                }
                okCount++;
                total += data.LongLength;
            }
            catch (Exception ex)
            {
                fails.Add($"{t.Display}：写入失败（{ex.GetType().Name} {ex.Message}）");
            }
        }

        if (okCount == 0)
            return (false, "分流数据更新失败：\n" + string.Join("\n", fails));

        var summary = $"已更新 {okCount}/{targets.Count} 个数据文件（{total / 1024 / 1024} MB）"
                    + (fails.Count > 0 ? "\n\n失败项：\n" + string.Join("\n", fails) : "")
                    + "\n\n新数据在下次连接（内核重新启动）时生效；旧文件保留为 *.bak-prev。";
        return (okCount == targets.Count, summary);
    }

    private static async Task<byte[]?> DownloadAsync(string url, CancellationToken ct)
    {
        //   与内核下载同一原则：本地 socks 优先（直连在部分网络会挂住不报错）。
        var attempts = new[] { "socks", "direct", "sys" };

        foreach (var kind in attempts)
        {
            try
            {
                using var handler = new HttpClientHandler();
                switch (kind)
                {
                    case "socks":
                        handler.Proxy = new WebProxy($"socks5://127.0.0.1:{Neo.Core.Core.XrayConfigBuilder.DefaultSocksPort}");
                        handler.UseProxy = true;
                        break;
                    case "sys":
                        handler.UseProxy = true;
                        break;
                    default:
                        handler.UseProxy = false;
                        break;
                }
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "neo-geo-update");
                var data = await client.GetByteArrayAsync(url, ct);
                if (data.Length > 0) return data;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }
        }
        return null;
    }
}
