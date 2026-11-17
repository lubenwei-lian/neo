using Neo.Core;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Neo.Core.Core;

namespace Neo.App.Services;

public sealed record XrayRelease(string Version, string Tag, string DownloadUrl, long SizeBytes, bool Prerelease, string? Sha256 = null);

/// sing-box 内核发布。通道 = **上游官方 SagerNet/sing-box 稳定版**（releases/latest）。
///   Windows 版只用官方发行版；Android 版的自编译内核与本项目无关。
///   官方资产形如 `sing-box-1.14.3-windows-amd64.zip`（zip 内含 sing-box.exe + libcronet.dll + LICENSE），
///   上游不提供 .sha256：安装做「可解压 + 内含 exe + version 版本串自校验」，sha256 仅记录追溯，不作为放行条件。
public sealed record SingBoxRelease(string Version, string Tag, string DownloadUrl, long SizeBytes, bool Prerelease, string AssetName = "", string? Sha256 = null);

/// mihomo（Clash.Meta）内核发布。通道 = 上游 MetaCubeX/mihomo 稳定版（releases/latest）。
/// Windows amd64 的 v1 系列资产名形如 `mihomo-windows-amd64-v1-v1.19.33.zip`（tag 自带 v 前缀）。
/// 上游没有 .sha256 资产：安装做「可解压 + 内含 exe + 版本串自校验」，与 sing-box 同策略。
public sealed record MihomoRelease(string Version, string Tag, string AssetName, string DownloadUrl, long SizeBytes, bool Prerelease, string? Sha256 = null);

public static partial class UpdateChecker
{
    private const string XrayOwner = "XTLS";
    private const string XrayRepo = "Xray-core";

    /// <summary>sing-box 上游官方发布仓库（Windows 版内核只走官方稳定通道）。</summary>
    private const string SingBoxOwner = "SagerNet";
    private const string SingBoxRepo = "sing-box";

    /// <summary>历史自编译内核资产前缀（仅用于解析旧资产名/旧版本显示，不再用于下载）。</summary>
    private const string LegacySingBoxAssetPrefix = "neo-singbox-";

    /// <summary>最近一次 FetchLatestSingBoxAsync 的失败原因，供检查文案区分「网络不可达 / 无 windows 资产」。</summary>
    private static string? _lastSingBoxFetchIssue;

    /// <summary>mihomo 上游仓库（SSR / Clash 辅助内核）。</summary>
    private const string MihomoOwner = "MetaCubeX";
    private const string MihomoRepo = "mihomo";
    private static string? _lastMihomoFetchIssue;

    /// 取 xray 官方发布；默认使用 /latest 稳定版，显式开启预发布后读取官方标记。
    public static Task<XrayRelease?> FetchLatestAsync(CancellationToken ct = default)
        => FetchLatestAsync(false, ct);

    /// <param name="includePreRelease">
    /// true = 把上游标记为 prerelease 的版本也作为候选（走 REST API 取标记，一次请求；
    /// 被匿名配额限流时自动退回 atom 通道，此时拿不到预发布标记、行为退化为稳定优先）。
    /// </param>
    public static async Task<XrayRelease?> FetchLatestAsync(bool includePreRelease, CancellationToken ct = default)
    {
        var preferProxy = ConnectionService.IsConnected;
        List<(string Tag, bool Prerelease)> candidates;
        if (includePreRelease)
        {
            var flags = await GitHubChannel.GetReleaseFlagsAsync(XrayOwner, XrayRepo, preferProxy, ct);
            candidates = flags ?? [];
        }
        else candidates = [];
        // A numeric tag can still be marked prerelease by GitHub. Atom does not
        // carry that flag; /latest is the stable fallback even during API limits.
        if (candidates.Count == 0)
        {
            var stable = await GitHubChannel.GetLatestTagAsync(XrayOwner, XrayRepo, preferProxy, ct);
            if (stable == null) return null;
            candidates.Add((stable, false));
        }

        // atom 里最新在前。官方每个正式版都带 Xray-windows-64.zip，
        // 因此最多试前 3 个即可（预发布偶尔缺资产，翻再多也几乎不会是有效发布）。
        var tried = 0;
        foreach (var (tag, prerelease) in candidates)
        {
            if (tried >= 3) break;
            //   原实现取第一个 tag ⇒ 可能把预发布内核装给用户，且 UI 里 Prerelease 恒 false 不作提示。
            var ver = tag.TrimStart('v', 'V');
            // 未开启「检查预发布内核」时跳过带预发布后缀的 tag；开启后不再跳过。
            if (!includePreRelease && prerelease) continue;

            tried++;
            var assets = await GitHubChannel.GetAssetsAsync(XrayOwner, XrayRepo, tag, preferProxy, ct);
            var asset = assets.FirstOrDefault(a =>
                string.Equals(a.Name, "Xray-windows-64.zip", StringComparison.OrdinalIgnoreCase));
            if (asset == null) continue;   // 该版本无 x64 资产，看下一个
            // 官方 SHA-256：GitHub API 的 asset.digest（一次请求）；失败留给安装阶段用 .dgst 兜底。
            var sha = asset.Sha256 ?? await GitHubChannel.GetAssetDigestAsync(XrayOwner, XrayRepo, tag, asset.Name, preferProxy, ct);
            if (!string.IsNullOrEmpty(sha)) KernelHashCache.Put(XrayOwner, XrayRepo, tag, asset.Name, sha);
            return new XrayRelease(ver, tag, asset.DownloadUrl, asset.SizeBytes, prerelease, sha);
        }
        return null;
    }

    public static async Task<(bool Ok, string Message, XrayRelease? Release)> CheckXrayAsync(
        string? currentVersion, bool includePreRelease = false)
    {
        var rel = await FetchLatestAsync(includePreRelease);
        if (rel == null) return (false, "检查失败：网络不可达（可先连接代理后再试）", null);

        var decision = DescribeKernelUpdate(currentVersion, rel.Version, KernelCatalog.IsInstalled(CoreEngine.Xray));

        var preTag = rel.Prerelease ? " · 预发布" : "";
        var mode = includePreRelease ? "\n（已开启「检查预发布内核」：候选含上游标记为预发布的版本）" : "";
        return (true, $"当前内核：xray {decision.Current}\n最新发布：xray {rel.Version}（{rel.Tag}{preTag}）\n\n{decision.Verdict}{mode}", decision.CanInstall ? rel : null);
    }

    /// 从上游官方 SagerNet/sing-box 取最新**稳定版**的 Windows amd64 内核资产。
    /// 用 releases/latest（稳定版）而不是 atom（atom 会把 alpha 排在前面）；
    /// 官方资产名形如 `sing-box-1.14.3-windows-amd64.zip`（大小写不敏感匹配）。
    /// 找不到资产时返回 null，并把原因写进 _lastSingBoxFetchIssue 供检查文案使用。
    public static async Task<SingBoxRelease?> FetchLatestSingBoxAsync(CancellationToken ct = default)
    {
        _lastSingBoxFetchIssue = null;
        var preferProxy = ConnectionService.IsConnected;
        var latest = await GitHubChannel.GetLatestTagAsync(SingBoxOwner, SingBoxRepo, preferProxy, ct);
        if (latest == null)
        {
            _lastSingBoxFetchIssue = "网络不可达（已尝试当前代理 / 系统代理 / 直连）";
            return null;
        }

        var ver = latest.TrimStart('v', 'V');
        var expected = $"sing-box-{ver}-windows-amd64.zip";
        var assets = await GitHubChannel.GetAssetsAsync(SingBoxOwner, SingBoxRepo, latest, preferProxy, ct);
        var asset = assets.FirstOrDefault(a => string.Equals(a.Name, expected, StringComparison.OrdinalIgnoreCase));
        if (asset == null)
        {
            _lastSingBoxFetchIssue = $"上游 {latest} 没有 windows-amd64 资产（找不到 {expected}）";
            LogBus.App($"sing-box 内核更新：{_lastSingBoxFetchIssue ?? ""}", "Warning");
            return null;
        }

        var coreVer = ExtractCoreVersion(asset.Name);
        if (coreVer == "unknown")
        {
            _lastSingBoxFetchIssue = $"上游资产名无法解析版本：{asset.Name}";
            LogBus.App(_lastSingBoxFetchIssue ?? "", "Warning");
            return null;
        }
        var sbSha = asset.Sha256 ?? await GitHubChannel.GetAssetDigestAsync(SingBoxOwner, SingBoxRepo, latest, asset.Name, preferProxy, ct);
        if (!string.IsNullOrEmpty(sbSha)) KernelHashCache.Put(SingBoxOwner, SingBoxRepo, latest, asset.Name, sbSha);
        return new SingBoxRelease(coreVer, latest, asset.DownloadUrl, asset.SizeBytes, false, asset.Name, sbSha);
    }

    /// 从内核资产名里抽出内核版本号。
    /// 官方命名：`sing-box-1.14.3-windows-amd64.zip` → `1.14.3`
    /// 旧自编译命名（仅兼容显示/比较，不再用于下载）：
    ///   `neo-singbox-1.14.2-noGate-windows-amd64.zip` → `1.14.2-noGate`
    /// 规则：去掉官方的 `sing-box-` 或历史的 `neo-singbox-` 前缀 → 去 `.zip` → 取到 `-windows-` 之前。
    /// 抽不出来（命名不符合约定）时返回 "unknown"，让上层提示而不是崩溃。
    public static string ExtractCoreVersion(string assetName)
    {
        var s = assetName ?? "";
        if (s.StartsWith(LegacySingBoxAssetPrefix, StringComparison.OrdinalIgnoreCase))
            s = s[LegacySingBoxAssetPrefix.Length..];
        else if (s.StartsWith("sing-box-", StringComparison.OrdinalIgnoreCase))
            s = s["sing-box-".Length..];
        if (s.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            s = s[..^4];
        var cut = s.IndexOf("-windows", StringComparison.OrdinalIgnoreCase);
        if (cut > 0) s = s[..cut];
        else if (s.EndsWith("-amd64", StringComparison.OrdinalIgnoreCase)) s = s[..^6];
        return s.Length > 0 ? s : "unknown";
    }

    public static async Task<(bool Ok, string Message, SingBoxRelease? Release)> CheckSingBoxAsync(string? currentVersion)
    {
        var rel = await FetchLatestSingBoxAsync();
        if (rel == null)
            return (false,
                $"检查 sing-box 内核更新失败：{_lastSingBoxFetchIssue ?? "网络不可达"}。\n\n" +
                "说明：Windows 版使用上游官方 SagerNet/sing-box **稳定版**（releases/latest），" +
                "只从 Windows amd64 资产更新；若上游该版本没有 windows-amd64 资产，或当前网络不可达，请稍后再试。",
                null);

        var decision = DescribeKernelUpdate(currentVersion, rel.Version, KernelCatalog.IsInstalled(CoreEngine.SingBox));

        return (true,
            $"当前内核：sing-box {decision.Current}\n上游最新稳定：sing-box {rel.Version}（{rel.Tag}）\n\n{decision.Verdict}",
            decision.CanInstall ? rel : null);
    }


    /// 取 mihomo 最新**稳定版**（releases/latest 天然排除 prerelease）。
    /// 资产固定取 Windows amd64 的 v1 系列：`mihomo-windows-amd64-v1-{tag}.zip`。
    public static async Task<MihomoRelease?> FetchLatestMihomoAsync(CancellationToken ct = default)
    {
        _lastMihomoFetchIssue = null;
        var preferProxy = ConnectionService.IsConnected;
        var tag = await GitHubChannel.GetLatestTagAsync(MihomoOwner, MihomoRepo, preferProxy, ct);
        if (tag == null)
        {
            _lastMihomoFetchIssue = "网络不可达（已尝试当前代理 / 系统代理 / 直连）";
            return null;
        }

        var ver = tag.TrimStart('v', 'V');
        var assets = await GitHubChannel.GetAssetsAsync(MihomoOwner, MihomoRepo, tag, preferProxy, ct);
        var expected = $"mihomo-windows-amd64-v1-{tag}.zip";
        var asset = assets.FirstOrDefault(a => string.Equals(a.Name, expected, StringComparison.OrdinalIgnoreCase))
                    ?? assets.FirstOrDefault(a =>
                        a.Name.StartsWith("mihomo-windows-amd64-v1-", StringComparison.OrdinalIgnoreCase)
                        && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                        && !a.Name.Contains("-go1", StringComparison.OrdinalIgnoreCase));
        if (asset == null)
        {
            _lastMihomoFetchIssue = $"上游 {tag} 没有 windows-amd64 资产（找不到 {expected}）";
            LogBus.App($"mihomo 内核更新：{_lastMihomoFetchIssue}", "Warning");
            return null;
        }
        var mhSha = asset.Sha256 ?? await GitHubChannel.GetAssetDigestAsync(MihomoOwner, MihomoRepo, tag, asset.Name, preferProxy, ct);
        if (!string.IsNullOrEmpty(mhSha)) KernelHashCache.Put(MihomoOwner, MihomoRepo, tag, asset.Name, mhSha);
        return new MihomoRelease(ver, tag, asset.Name, asset.DownloadUrl, asset.SizeBytes, false, mhSha);
    }

    public static async Task<(bool Ok, string Message, MihomoRelease? Release)> CheckMihomoAsync(string? currentVersion)
    {
        var rel = await FetchLatestMihomoAsync();
        if (rel == null)
            return (false,
                $"检查 mihomo 内核更新失败：{_lastMihomoFetchIssue ?? "网络不可达"}\n\n" +
                "说明：mihomo 只从上游 MetaCubeX/mihomo 的稳定版（releases/latest）更新，" +
                "只取 Windows amd64 的 v1 系列资产。若上游该版本没有对应资产，请稍后再试。",
                null);

        var decision = DescribeKernelUpdate(currentVersion, rel.Version, KernelCatalog.IsInstalled(CoreEngine.Mihomo));
        return (true, $"当前内核：mihomo {decision.Current}\n上游最新稳定：mihomo {rel.Version}（{rel.Tag}）\n\n{decision.Verdict}", decision.CanInstall ? rel : null);
    }

    /// 下载并安装 mihomo：下载 zip → 解压找 exe → 版本自校验 → 备份 → 覆盖 runtime\mihomo.exe → 复核。
    /// 更新辅助内核也要等待当前会话退出，避免占用文件及恢复竞态。
    public static Task<(bool Ok, string Message)> DownloadAndInstallMihomoAsync(
        MihomoRelease rel, Action<string> progress, CancellationToken ct = default,
        Action<DownloadProgress>? onProgress = null)
        => InstallReleaseAsync(CoreEngine.Mihomo, rel.Version, rel.Tag, rel.DownloadUrl,
            rel.AssetName, rel.Sha256, rel.SizeBytes, progress, onProgress, ct);

    /// 版本号去掉 `-noGate` / `-neo` 这类后缀，只留数字主干。
    /// 保留原因：兼容用户机器上已装的旧自编译内核（如 `1.14.2-noGate`）与历史资产名，
    /// 否则 `CompareVersions("1.14.3", "1.14.2-noGate")` 里 `"2-noGate"` 会被 int.TryParse
    /// 当成 0 → 每次检查都误报"有新版本"。
    public static string StripSuffix(string v)
    {
        var i = v.IndexOf('-');
        return i > 0 ? v[..i] : v;
    }

    /// <summary>
    /// expectedSha 为 null 表示上游没给官方哈希（API 被限流且无 sidecar）：此时只记录实际值并如实提示，
    /// 不假装「校验通过」——宁可告诉用户「无官方哈希」，也不做虚假保证。
    /// </summary>
    private static async Task<(bool Ok, string Message)> FetchKernelZipAsync(
        string url, string zipPath, string displayName, string? expectedSha,
        Action<string> progress, Action<DownloadProgress>? onProgress, CancellationToken ct,
        string? cacheOwner = null, string? cacheRepo = null, string? cacheTag = null, string? cacheAsset = null)
    {
        // 官方哈希来源优先级：本次检查拿到的 API digest → 本地缓存（之前拿到的官方值）→ xray .dgst。
        if (string.IsNullOrEmpty(expectedSha)
            && cacheOwner != null && cacheRepo != null && cacheTag != null && cacheAsset != null)
        {
            expectedSha = KernelHashCache.TryGet(cacheOwner, cacheRepo, cacheTag, cacheAsset);
        }

        progress($"下载 {displayName}…");
        var (ok, msg) = await KernelDownloader.DownloadAsync(url, zipPath, onProgress, ct, progress);
        if (!ok) return (false, msg);

        progress("校验 SHA-256…");
        var (match, actual) = await KernelDownloader.VerifySha256Async(zipPath, expectedSha, ct);
        if (!match)
        {
            KernelDownloader.DiscardPart(zipPath);
            try { File.Delete(zipPath); } catch { }
            return (false, $"SHA-256 校验失败，安装中止（已删除下载包）。\n期望 {expectedSha}\n实际 {actual}");
        }

        if (string.IsNullOrEmpty(expectedSha))
        {
            progress($"校验完成（上游未提供官方哈希，已记录实际 SHA-256：{actual}）…");
            LogBus.App($"内核更新包 {displayName} 上游未提供官方哈希，实际 SHA-256：{actual}", "Warning");
            IncidentLog.Record("kernel-install", "内核更新包无官方哈希（已记录实际值）", $"{displayName} · {actual}");
        }
        else
        {
            progress("SHA-256 校验通过…");
            LogBus.App($"内核更新包 {displayName} SHA-256 校验通过：{actual}", "Debug");
            // 只有「已经用官方值验过」的哈希才写缓存；无官方值时缓存的只是自算值，再拿来当依据
            // 等于自我循环校验（假安全），所以绝不写入。
            if (cacheOwner != null && cacheRepo != null && cacheTag != null && cacheAsset != null)
                KernelHashCache.Put(cacheOwner, cacheRepo, cacheTag, cacheAsset, actual);
        }
        return (true, msg);
    }

    /// 下载并覆盖安装内核：下载 zip → 解压校验 → （必要时自动断开连接）→ 备份 → 覆盖 → 运行验证，失败自动回滚。
    public static Task<(bool Ok, string Message)> DownloadAndInstallAsync(
        XrayRelease rel, Action<string> progress, CancellationToken ct = default,
        Action<DownloadProgress>? onProgress = null)
        => InstallReleaseAsync(CoreEngine.Xray, rel.Version, rel.Tag, rel.DownloadUrl,
            "Xray-windows-64.zip", rel.Sha256, rel.SizeBytes, progress, onProgress, ct);

    /// <summary>
    /// 安装内核前用「当前配置」预检新 exe：跑一次 `xray run -test` / `sing-box check`。
    /// 当前没有该内核配置时跳过；有配置但预检失败或超时则保留旧内核。
    /// 预检明确失败（配置被新内核拒绝）→ 中止安装并保留旧内核。
    /// </summary>
    private static async Task<(bool Ok, string Message)> PreflightWithCurrentConfigAsync(string newExe, KernelKind kind, CancellationToken ct)
    {
        var kindName = kind == KernelKind.Xray ? "xray" : "sing-box";
        try
        {
            var cfgPath = Path.Combine(Neo.Core.NeoPaths.DataDir, "config.json");
            if (!File.Exists(cfgPath))
            {
                LogBus.App($"新内核预检：当前没有 config.json，跳过（{kindName}）", "Debug");
                return (true, "");
            }
            var cfgText = File.ReadAllText(cfgPath);
            if (!KernelCompat.LooksLikeConfig(cfgText, kind))
            {
                LogBus.App($"新内核预检：当前 config.json 不是 {kindName} 的配置，跳过", "Debug");
                return (true, "");
            }

            var r = await Task.Run(() => KernelCompat.RunCheck(newExe, kind, cfgPath, 8000, NeoPaths.RuntimeDir), ct);
            if (r.Ok)
            {
                LogBus.App($"新内核预检通过：当前配置可被新 {kindName} 接受", "Debug");
                return (true, "");
            }
            if (r.InfraFailed)
            {
                return (false, $"新内核预检无法完成，已保留原内核：{KernelCompat.FirstLines(r.Output, 3)}");
            }

            var lines = KernelCompat.FirstLines(r.Output, 3);
            LogBus.App($"新内核预检失败，已中止安装（保留旧内核）：{lines}", "Error");
            IncidentLog.Record("compat", $"新 {kindName} 内核预检失败，已保留旧内核", lines);
            return (false, $"新内核无法通过当前配置的预检，已中止安装并保留旧内核。\n\n{lines}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return (false, "新内核预检异常，已保留原内核：" + ex.Message);
        }
    }

    /// 下载并覆盖安装 **sing-box 内核**（上游官方稳定版资产）。
    /// 流程：官方 SHA-256 → 解压/版本预检 → 等待会话退出 → exe/DLL 事务替换 → 复核/回滚。
    public static Task<(bool Ok, string Message)> DownloadAndInstallSingBoxAsync(
        SingBoxRelease rel, Action<string> progress, CancellationToken ct = default,
        Action<DownloadProgress>? onProgress = null)
        => InstallReleaseAsync(CoreEngine.SingBox, rel.Version, rel.Tag, rel.DownloadUrl,
            string.IsNullOrEmpty(rel.AssetName) ? $"sing-box-{rel.Version}-windows-amd64.zip" : rel.AssetName,
            rel.Sha256, rel.SizeBytes, progress, onProgress, ct);

    /// 取同一 tag 下的小文本资产（目前只用于 xray 官方 .dgst）。
    /// 三路（socks / 系统代理 / 直连）依次尝试；取不到返回 null
    /// （xray 安装按「缺少校验文件」中止，不静默跳过；sing-box 官方通道没有该文件）。
    private static async Task<string?> FetchReleaseTextAsync(string owner, string repo, string tag,
        string assetName, CancellationToken ct)
    {
        try
        {
            var preferProxy = ConnectionService.IsConnected;
            var assets = await GitHubChannel.GetAssetsAsync(owner, repo, tag, preferProxy, ct);
            var asset = assets.FirstOrDefault(a => string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase));
            if (asset == null) return null;

            var kinds = ConnectionService.IsConnected
                ? new[] { "socks", "sys", "direct" }
                : new[] { "sys", "direct", "socks" };

            foreach (var kind in kinds)
            {
                try
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

                    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
                    client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "neo-update-check");
                    var text = await client.GetStringAsync(asset.DownloadUrl, ct);
                    if (text.Length > 0) return text;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }
        return null;
    }

    internal static (string Current, string Verdict, bool CanInstall) DescribeKernelUpdate(
        string? currentVersion, string releaseVersion, bool installed)
    {
        if (!installed) return ("未安装", "尚未安装，可下载安装。", true);
        var current = currentVersion?.Trim();
        if (string.IsNullOrEmpty(current) || !System.Text.RegularExpressions.Regex.IsMatch(current,
            @"^[vV]?\d+(?:\.\d+){2,3}(?:-[0-9A-Za-z.]+)?(?:\+[0-9A-Za-z.]+)?$"))
            return ("版本未识别", "本地内核存在，但版本读取失败，暂时无法判断是否需要更新。请重新检查。", false);
        var compared = CompareVersions(releaseVersion, current);
        return (current, compared > 0 ? "发现新版本，可一键覆盖安装。" : compared == 0
            ? "已是最新版本，无需更新。" : "本地版本比发布版本更新，无需更新。", compared > 0);
    }

    public static int CompareVersions(string a, string b)
    {
        var pa = StripSuffix(a.Trim().TrimStart('v', 'V')).Split('.', StringSplitOptions.RemoveEmptyEntries);
        var pb = StripSuffix(b.Trim().TrimStart('v', 'V')).Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            int va = i < pa.Length && int.TryParse(pa[i], out var x) ? x : 0;
            int vb = i < pb.Length && int.TryParse(pb[i], out var y) ? y : 0;
            if (va != vb) return va.CompareTo(vb);
        }
        return 0;
    }


    private const string NaiveOwner = "klzgrad";
    private const string NaiveRepo = "naiveproxy";

    public sealed record NaiveRelease(string Version, string Tag, string AssetName, string DownloadUrl, long SizeBytes, string? Sha256);

    private static string? _lastNaiveFetchIssue;

    /// <summary>取 naiveproxy 最新 Windows x64 资产（naiveproxy-&lt;ver&gt;-win-x64.zip）。</summary>
    public static async Task<NaiveRelease?> FetchLatestNaiveAsync(CancellationToken ct = default)
    {
        _lastNaiveFetchIssue = null;
        var preferProxy = ConnectionService.IsConnected;
        var tag = await GitHubChannel.GetLatestTagAsync(NaiveOwner, NaiveRepo, preferProxy, ct);
        if (tag == null)
        {
            _lastNaiveFetchIssue = "网络不可达（已尝试当前代理 / 系统代理 / 直连）";
            return null;
        }
        var ver = tag.TrimStart('v', 'V');
        var assets = await GitHubChannel.GetAssetsAsync(NaiveOwner, NaiveRepo, tag, preferProxy, ct);
        var asset = assets.FirstOrDefault(a =>
            a.Name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase));
        if (asset == null)
        {
            _lastNaiveFetchIssue = $"上游 {tag} 没有 win-x64 资产";
            LogBus.App($"naive 内核更新：{_lastNaiveFetchIssue}", "Warning");
            return null;
        }
        var sha = asset.Sha256 ?? await GitHubChannel.GetAssetDigestAsync(NaiveOwner, NaiveRepo, tag, asset.Name, preferProxy, ct);
        if (!string.IsNullOrEmpty(sha)) KernelHashCache.Put(NaiveOwner, NaiveRepo, tag, asset.Name, sha);
        return new NaiveRelease(ver, tag, asset.Name, asset.DownloadUrl, asset.SizeBytes, sha);
    }

    public static async Task<(bool Ok, string Message, NaiveRelease? Release)> CheckNaiveAsync(string? currentVersion)
    {
        var rel = await FetchLatestNaiveAsync();
        if (rel == null)
            return (false, $"检查 naive 内核失败：{_lastNaiveFetchIssue ?? "网络不可达"}", null);
        var decision = DescribeKernelUpdate(currentVersion, rel.Version, KernelCatalog.IsInstalled(CoreEngine.Naive));
        return (true, $"当前内核：naive {decision.Current}\n上游最新：naive {rel.Version}（{rel.Tag}）\n\n{decision.Verdict}", decision.CanInstall ? rel : null);
    }

    /// <summary>naive 使用与其他内核相同的校验、会话交接与事务替换流程。</summary>
    public static Task<(bool Ok, string Message)> DownloadAndInstallNaiveAsync(
        NaiveRelease rel, Action<string> progress, CancellationToken ct = default,
        Action<DownloadProgress>? onProgress = null)
        => InstallReleaseAsync(CoreEngine.Naive, rel.Version, rel.Tag, rel.DownloadUrl,
            rel.AssetName, rel.Sha256, rel.SizeBytes, progress, onProgress, ct);

    /// <summary>naive --version 的输出（Chromium 风格，可能是 "naive 154.0.x" 或只有版本号）。</summary>
    public static string RunNaiveVersion(string exe) => ProbeCoreVersion(CoreEngine.Naive, exe);

}
