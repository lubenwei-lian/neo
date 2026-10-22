using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Neo.Core.Core;

namespace Neo.App.Services;

public sealed record XrayRelease(string Version, string Tag, string DownloadUrl, long SizeBytes, bool Prerelease, string? Sha256 = null);

/// sing-box 内核发布。通道 = **上游官方 SagerNet/sing-box 稳定版**（releases/latest）。
///   电脑版只用官方发行版；手机版的自编译内核与本项目无关。
///   官方资产形如 `sing-box-1.14.3-windows-amd64.zip`（zip 内含 sing-box.exe + libcronet.dll + LICENSE），
///   上游不提供 .sha256：安装做「可解压 + 内含 exe + version 版本串自校验」，sha256 仅记录追溯，不作为放行条件。
public sealed record SingBoxRelease(string Version, string Tag, string DownloadUrl, long SizeBytes, bool Prerelease, string AssetName = "", string? Sha256 = null);

/// mihomo（Clash.Meta）内核发布。通道 = 上游 MetaCubeX/mihomo 稳定版（releases/latest）。
/// Windows amd64 的 v1 系列资产名形如 `mihomo-windows-amd64-v1-v1.19.33.zip`（tag 自带 v 前缀）。
/// 上游没有 .sha256 资产：安装做「可解压 + 内含 exe + 版本串自校验」，与 sing-box 同策略。
public sealed record MihomoRelease(string Version, string Tag, string AssetName, string DownloadUrl, long SizeBytes, bool Prerelease, string? Sha256 = null);

public static class UpdateChecker
{
    private const string XrayOwner = "XTLS";
    private const string XrayRepo = "Xray-core";

    /// <summary>sing-box 上游官方发布仓库（电脑版内核只走官方稳定通道）。</summary>
    private const string SingBoxOwner = "SagerNet";
    private const string SingBoxRepo = "sing-box";

    /// <summary>历史自编译内核资产前缀（仅用于解析旧资产名/旧版本显示，不再用于下载）。</summary>
    private const string LegacySingBoxAssetPrefix = "neo-singbox-";

    /// <summary>最近一次 FetchLatestSingBoxAsync 的失败原因，供检查文案区分「网络不可达 / 无 windows 资产」。</summary>
    private static string? _lastSingBoxFetchIssue;

    /// <summary>mihomo 上游仓库（第三内核；只纳入安装/版本/更新管理，连接层暂未启用）。</summary>
    private const string MihomoOwner = "MetaCubeX";
    private const string MihomoRepo = "mihomo";
    private static string? _lastMihomoFetchIssue;

    /// 取 xray 官方最新 release（**非 API 通道**，避开匿名 60次/小时 配额）。
    /// 官方从不把新版本转正，`releases/latest` 会停在最后一个 stable，
    /// 因此这里走 atom 列表取**第一个**版本（含预发布），
    /// 再按固定资产名 `Xray-windows-64.zip` 取下载地址。
    public static Task<XrayRelease?> FetchLatestAsync(CancellationToken ct = default)
        => FetchLatestAsync(false, ct);

    /// <param name="includePreRelease">
    /// true = 把上游标记为 prerelease 的版本也作为候选（走 REST API 取标记，一次请求；
    /// 被匿名配额限流时自动退回 atom 通道，此时拿不到预发布标记、行为退化为稳定优先）。
    /// </param>
    public static async Task<XrayRelease?> FetchLatestAsync(bool includePreRelease, CancellationToken ct = default)
    {
        var preferProxy = ConnectionService.IsConnected;
        List<string> tags;
        if (includePreRelease)
        {
            var flags = await GitHubChannel.GetReleaseFlagsAsync(XrayOwner, XrayRepo, preferProxy, ct);
            tags = flags is { Count: > 0 }
                ? flags.Select(f => f.Tag).ToList()
                : await GitHubChannel.GetTagsFromAtomAsync(XrayOwner, XrayRepo, preferProxy, ct);
        }
        else
        {
            tags = await GitHubChannel.GetTagsFromAtomAsync(XrayOwner, XrayRepo, preferProxy, ct);
        }
        if (tags.Count == 0) return null;

        // atom 里最新在前。官方每个正式版都带 Xray-windows-64.zip，
        // 因此最多试前 3 个即可（预发布偶尔缺资产，翻再多也几乎不会是有效发布）。
        var tried = 0;
        foreach (var tag in tags)
        {
            if (tried >= 3) break;
            //   原实现取第一个 tag ⇒ 可能把预发布内核装给用户，且 UI 里 Prerelease 恒 false 不作提示。
            var ver = tag.TrimStart('v', 'V');
            // 未开启「检查预发布内核」时跳过带预发布后缀的 tag；开启后不再跳过。
            if (!includePreRelease && ver.Contains('-')) continue;

            tried++;
            var assets = await GitHubChannel.GetAssetsAsync(XrayOwner, XrayRepo, tag, preferProxy, ct);
            var asset = assets.FirstOrDefault(a =>
                string.Equals(a.Name, "Xray-windows-64.zip", StringComparison.OrdinalIgnoreCase));
            if (asset == null) continue;   // 该版本无 x64 资产，看下一个
            // 官方 SHA-256：GitHub API 的 asset.digest（一次请求）；失败留给安装阶段用 .dgst 兜底。
            var sha = await GitHubChannel.GetAssetDigestAsync(XrayOwner, XrayRepo, tag, asset.Name, preferProxy, ct);
            if (!string.IsNullOrEmpty(sha)) KernelHashCache.Put(XrayOwner, XrayRepo, tag, asset.Name, sha);
            return new XrayRelease(ver, tag, asset.DownloadUrl, asset.SizeBytes, false, sha);
        }
        return null;
    }

    public static async Task<(bool Ok, string Message, XrayRelease? Release)> CheckXrayAsync(
        string? currentVersion, bool includePreRelease = false)
    {
        var rel = await FetchLatestAsync(includePreRelease);
        if (rel == null) return (false, "检查失败：网络不可达（可先连接代理后再试）", null);

        var cur = (currentVersion ?? "").Trim();
        string verdict;
        if (cur.Length == 0)
            verdict = "无法读取本地内核版本。";
        else if (CompareVersions(rel.Version, cur) > 0)
            verdict = "发现新版本，可一键覆盖安装。";
        else
            verdict = "已是最新版本，无需更新。";

        var preTag = rel.Prerelease ? " · 预发布" : "";
        var mode = includePreRelease ? "\n（已开启「检查预发布内核」：候选含上游标记为预发布的版本）" : "";
        return (true, $"当前内核：xray {cur}\n最新发布：xray {rel.Version}（{rel.Tag}{preTag}）\n\n{verdict}{mode}", rel);
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
        var sbSha = await GitHubChannel.GetAssetDigestAsync(SingBoxOwner, SingBoxRepo, latest, asset.Name, preferProxy, ct);
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
                "说明：电脑版使用上游官方 SagerNet/sing-box **稳定版**（releases/latest），" +
                "只从 Windows amd64 资产更新；若上游该版本没有 windows-amd64 资产，或当前网络不可达，请稍后再试。",
                null);

        var cur = (currentVersion ?? "").Trim();
        string verdict;
        if (cur.Length == 0)
            verdict = "无法读取本地内核版本。";
        else if (CompareVersions(StripSuffix(rel.Version), StripSuffix(cur)) > 0)
            verdict = "发现新版本，可一键覆盖安装。";
        else if (CompareVersions(StripSuffix(rel.Version), StripSuffix(cur)) == 0)
            verdict = "已是最新版本，无需更新。";
        else
            verdict = "本地版本比发布版本更新（可能是手工替换的测试版），无需更新。";

        return (true,
            $"当前内核：sing-box {cur}\n上游最新稳定：sing-box {rel.Version}（{rel.Tag}）\n\n{verdict}",
            rel);
    }

    // ================= 2026-10-23 第三批：mihomo 内核（安装/版本/更新） =================

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
        var mhSha = await GitHubChannel.GetAssetDigestAsync(MihomoOwner, MihomoRepo, tag, asset.Name, preferProxy, ct);
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

        var cur = (currentVersion ?? "").Trim();
        string verdict;
        if (cur.Length == 0) verdict = "本机未安装 mihomo 内核，可一键下载安装。";
        else if (CompareVersions(rel.Version, cur) > 0) verdict = "发现新版本，可一键覆盖安装。";
        else if (CompareVersions(rel.Version, cur) == 0) verdict = "已是最新版本，无需更新。";
        else verdict = "本地版本比发布版本更新（可能是手工替换的测试版），无需更新。";

        var curText = cur.Length == 0 ? "未安装" : cur;
        return (true, $"当前内核：mihomo {curText}\n上游最新稳定：mihomo {rel.Version}（{rel.Tag}）\n\n{verdict}", rel);
    }

    /// 下载并安装 mihomo：下载 zip → 解压找 exe → 版本自校验 → 备份 → 覆盖 runtime\mihomo.exe → 复核。
    /// mihomo 目前不参与拨号（TUN/系统代理仍由 xray / sing-box 承载），所以无需先断开连接。
    public static async Task<(bool Ok, string Message)> DownloadAndInstallMihomoAsync(
        MihomoRelease rel, Action<string> progress, CancellationToken ct = default,
        Action<DownloadProgress>? onProgress = null)
    {
        var zipPath = Path.Combine(Path.GetTempPath(), "neo-mihomo-update.zip");
        var extractDir = Path.Combine(Path.GetTempPath(), "neo-mihomo-update");

        var mhSha = rel.Sha256 ?? KernelHashCache.TryGet(MihomoOwner, MihomoRepo, rel.Tag, rel.AssetName);
        var fetched = await FetchKernelZipAsync(rel.DownloadUrl, zipPath, rel.AssetName,
            mhSha, progress, onProgress, ct, MihomoOwner, MihomoRepo, rel.Tag, rel.AssetName);
        if (!fetched.Ok) return (false, fetched.Message);

        try
        {
            progress("解压校验…");
                // zip 已由 FetchKernelZipAsync 落盘并校验，这里只解压
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
            ZipFile.ExtractToDirectory(zipPath, extractDir);

            var newExe = Directory.GetFiles(extractDir, "*.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (newExe == null) return (false, "解压后未找到可执行文件，安装中止。");

            var verOut = RunMihomoVersion(newExe);
            if (!MihomoVersionMatches(rel.Version, verOut))
                return (false, $"新内核版本自校验不符（期望 {rel.Version}，实得 {(verOut.Trim().Length > 0 ? verOut.Trim() : "无输出")}），安装中止，未改动现有文件。");

            var target = Path.Combine(Neo.Core.NeoPaths.RuntimeDir, "mihomo.exe");
            var backup = target + ".bak-prev";

            progress("覆盖安装…");
            KernelInstall.ReplaceAtomically(newExe, target, backup);

            var installed = RunMihomoVersion(target);
            if (!MihomoVersionMatches(rel.Version, installed))
            {
                try { File.Copy(backup, target, true); } catch { }
                return (false, "安装后校验失败，已回滚到旧内核。");
            }

            KernelCatalog.ResetVersion(CoreEngine.Mihomo);

            progress("清理临时文件…");
            try { Directory.Delete(extractDir, true); } catch { }
            try { File.Delete(zipPath); } catch { }

            return (true, $"mihomo 已更新到 {rel.Version}。\n\n说明：mihomo 目前只作为「可选内核」纳入安装与更新管理，"
                          + "连接仍由 xray / sing-box 承载（TUN 稳定优先）；它不会自动参与拨号。");
        }
        catch (Exception ex)
        {
            return (false, "安装失败：" + ex.Message);
        }
    }

    /// <summary>
    /// mihomo 的版本输出形如 "Mihomo Meta v1.19.33 windows amd64 with go1.25.4 …"，
    /// 不是 sing-box 的 "sing-box version x" 格式，必须单独抽 semver 比较（否则安装后自校验会永远失败）。
    /// </summary>
    private static bool MihomoVersionMatches(string expected, string output)
    {
        static string Semver(string s)
        {
            var m = System.Text.RegularExpressions.Regex.Match(s ?? "", @"(\d+)\.(\d+)\.(\d+)");
            return m.Success ? $"{m.Groups[1].Value}.{m.Groups[2].Value}.{m.Groups[3].Value}" : "";
        }
        var e = Semver(expected);
        return e.Length > 0 && e == Semver(output);
    }

    private static string RunMihomoVersion(string exe)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "-v",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return "";
            var so = p.StandardOutput.ReadToEnd();
            var se = p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            return (so + "\n" + se).Trim();
        }
        catch { return ""; }
    }

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
    /// 下载内核更新包：断点续传 + 实时进度 + SHA-256 校验（2026-10-23 改造）。
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
        var (ok, msg) = await KernelDownloader.DownloadAsync(url, zipPath, onProgress, ct);
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
    public static async Task<(bool Ok, string Message)> DownloadAndInstallAsync(
        XrayRelease rel, Action<string> progress, CancellationToken ct = default,
        Action<DownloadProgress>? onProgress = null)
    {
        var zipPath = Path.Combine(Path.GetTempPath(), "neo-xray-update.zip");
        var extractDir = Path.Combine(Path.GetTempPath(), "neo-xray-update");

        // 官方哈希：优先用检查更新时拿到的 API digest；拿不到再退回 xray 官方 .dgst。
        var expectedSha = rel.Sha256;
        if (string.IsNullOrEmpty(expectedSha))
        {
            var dgstText = await FetchReleaseTextAsync(XrayOwner, XrayRepo, rel.Tag, "Xray-windows-64.zip.dgst", ct);
            if (dgstText != null && KernelInstall.TryParseSha256(dgstText, out var fromDgst)) expectedSha = fromDgst;
        }

        // 断点续传 + 实时进度 + SHA-256 校验（失败/取消保留 .part，可续传）。
        // 缓存里可能已有上次拿到的官方哈希（API 限流时仍能校验同一个包）。
        expectedSha ??= KernelHashCache.TryGet(XrayOwner, XrayRepo, rel.Tag, "Xray-windows-64.zip");
        var fetched = await FetchKernelZipAsync(rel.DownloadUrl, zipPath, "Xray-windows-64.zip",
            expectedSha, progress, onProgress, ct, XrayOwner, XrayRepo, rel.Tag, "Xray-windows-64.zip");
        if (!fetched.Ok) return (false, fetched.Message);

        try
        {
            progress("解压校验…");
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
            ZipFile.ExtractToDirectory(zipPath, extractDir);

            var newExe = Path.Combine(extractDir, "xray.exe");
            if (!File.Exists(newExe)) return (false, "解压后未找到 xray.exe，安装中止。");

            var verOut = RunXrayVersion(newExe);
            if (!verOut.Contains(rel.Version, StringComparison.OrdinalIgnoreCase))
                return (false, $"新内核自校验不符（期望 {rel.Version}），安装中止，未改动现有文件。");

            progress("用当前配置预检新内核…");
            var preflight = await PreflightWithCurrentConfigAsync(newExe, KernelKind.Xray, ct);
            if (!preflight.Ok) return (false, preflight.Message);

            if (ConnectionService.IsConnected)
            {
                progress("断开当前连接（内核文件被占用）…");
                ConnectionService.Disconnect("安装新内核前断开");
            }

            var target = Path.Combine(Neo.Core.NeoPaths.RuntimeDir, "xray.exe");
            var backup = target + ".bak-prev";

            progress("覆盖安装…");
            KernelInstall.ReplaceAtomically(newExe, target, backup);

            var installed = RunXrayVersion(target);
            if (!installed.Contains(rel.Version, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(backup, target, true);
                return (false, "安装后校验失败，已回滚到旧内核。");
            }

            ConnectionService.ResetXrayVersion();

            progress("清理临时文件…");
            try { Directory.Delete(extractDir, true); } catch { }
            try { File.Delete(zipPath); } catch { }

            return (true, $"xray 已更新到 {rel.Version}！重新连接即使用新内核。");
        }
        catch (Exception ex)
        {
            return (false, "安装失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 安装内核前用「当前配置」预检新 exe：跑一次 `xray run -test` / `sing-box check`。
    /// 当前没有 config.json、或它不是该内核的配置、或预检本身跑不起来（超时/缺 exe）→ 不阻断安装，只记 Warning。
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

            var r = await Task.Run(() => KernelCompat.RunCheck(newExe, kind, cfgPath, 8000), ct);
            if (r.Ok)
            {
                LogBus.App($"新内核预检通过：当前配置可被新 {kindName} 接受", "Debug");
                return (true, "");
            }
            if (r.InfraFailed)
            {
                LogBus.App($"新内核预检无法执行（不阻断安装）：{r.Output}", "Warning");
                return (true, "");
            }

            var lines = KernelCompat.FirstLines(r.Output, 3);
            LogBus.App($"新内核预检失败，已中止安装（保留旧内核）：{lines}", "Error");
            IncidentLog.Record("compat", $"新 {kindName} 内核预检失败，已保留旧内核", lines);
            return (false, $"新内核无法通过当前配置的预检，已中止安装并保留旧内核。\n\n{lines}");
        }
        catch (OperationCanceledException) { return (true, ""); }
        catch (Exception ex)
        {
            LogBus.App($"新内核预检异常（不阻断安装）：[{ex.GetType().Name}] {ex.Message}", "Warning");
            return (true, "");
        }
    }

    private static string RunXrayVersion(string exe)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                ArgumentList = { "version" },
                RedirectStandardOutput = true,
                // 不重定向 stderr：重定向却不读会因管道缓冲写满而死锁（只需 stdout）。
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p == null) return "";
            var txt = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return txt;
        }
        catch { return ""; }
    }

    /// 下载并覆盖安装 **sing-box 内核**（上游官方稳定版资产）。
    /// 流程：下载 →（sha256 仅记录追溯）→ 解压并确认含 sing-box.exe → 新 exe `version` 版本号自校验
    /// → 用当前配置预检 → 断开 → KernelInstall 原子替换（.bak-prev 备份）→ 安装后版本自校验（失败回滚）。
    /// 上游不提供 .sha256，不再用「缺校验文件」中止。
    public static async Task<(bool Ok, string Message)> DownloadAndInstallSingBoxAsync(
        SingBoxRelease rel, Action<string> progress, CancellationToken ct = default,
        Action<DownloadProgress>? onProgress = null)
    {
        var zipPath = Path.Combine(Path.GetTempPath(), "neo-singbox-update.zip");
        var extractDir = Path.Combine(Path.GetTempPath(), "neo-singbox-update");

        var assetName = string.IsNullOrEmpty(rel.AssetName) ? "sing-box.exe" : rel.AssetName;
        var sboxSha = rel.Sha256 ?? KernelHashCache.TryGet(SingBoxOwner, SingBoxRepo, rel.Tag, assetName);
        var fetched = await FetchKernelZipAsync(rel.DownloadUrl, zipPath,
            string.IsNullOrEmpty(rel.AssetName) ? "sing-box 内核" : rel.AssetName,
            sboxSha, progress, onProgress, ct, SingBoxOwner, SingBoxRepo, rel.Tag, assetName);
        if (!fetched.Ok) return (false, fetched.Message);

        string? backup = null;
        var target = Path.Combine(Neo.Core.NeoPaths.RuntimeDir, "sing-box.exe");
        try
        {
            // 上游官方发行版不提供 .sha256 资产：不再用「缺校验文件」中止。
            // 完整性 = 可解压且内含 sing-box.exe + 新 exe `version` 版本串自校验；
            // sha256 只作为可追溯信息记录（日志 + IncidentLog），不作为放行条件。
            var actualSha = KernelInstall.Sha256Hex(await File.ReadAllBytesAsync(zipPath, ct));
            LogBus.App($"sing-box 内核包 SHA-256（仅追溯）：{actualSha} · {rel.AssetName}", "Debug");
            IncidentLog.Record("kernel-install", $"sing-box 内核包 SHA-256：{actualSha}", rel.AssetName);

            progress("解压校验…");
            try
            {
            // zip 已由 FetchKernelZipAsync 落盘并校验，这里只解压
                if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
                ZipFile.ExtractToDirectory(zipPath, extractDir);
            }
            catch (Exception ex)
            {
                return (false, "更新包解压失败（下载可能不完整），安装中止，未改动现有文件：" + ex.Message);
            }

            // zip 内可能带一层目录（上游发布包布局），递归找一个 sing-box.exe。
            var newExe = Directory.EnumerateFiles(extractDir, "sing-box.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (newExe == null) return (false, "解压后未找到 sing-box.exe，安装中止。");

            progress("校验内核版本…");
            var verOut = RunSingBoxVersion(newExe);
            if (!VersionMatches(rel.Version, verOut))
                return (false, $"新内核版本自校验不符（期望 {rel.Version}，实得 {(verOut.Trim().Length > 0 ? verOut.Trim() : "无输出")}），安装中止，未改动现有文件。");

            progress("用当前配置预检新内核…");
            var preflight2 = await PreflightWithCurrentConfigAsync(newExe, KernelKind.SingBox, ct);
            if (!preflight2.Ok) return (false, preflight2.Message);

            if (ConnectionService.IsConnected)
            {
                progress("断开当前连接（内核文件被占用）…");
                ConnectionService.Disconnect("安装新内核前断开");
            }

            backup = target + ".bak-prev";
            progress("覆盖安装…");
            KernelInstall.ReplaceAtomically(newExe, target, backup);

            var installed = RunSingBoxVersion(target);
            if (!VersionMatches(rel.Version, installed))
            {
                File.Copy(backup, target, true);
                return (false, "安装后版本校验失败，已回滚到旧内核。");
            }
            ConnectionService.ResetSingBoxVersion();

            progress("清理临时文件…");
            try { Directory.Delete(extractDir, true); } catch { }
            try { File.Delete(zipPath); } catch { }

            return (true, $"sing-box 已更新到 {rel.Version}（官方发行版）！重新连接即使用新内核。");
        }
        catch (Exception ex)
        {
            // 中途失败：文件可能已覆盖但校验没过，尽力回滚。
            try
            {
                if (backup != null && File.Exists(backup) && File.Exists(target))
                {
                    var iv = RunSingBoxVersion(target);
                    if (!VersionMatches(rel.Version, iv))
                        File.Copy(backup, target, true);
                }
            }
            catch { }
            return (false, "安装失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 校验 `sing-box version` 输出与期望版本一致：按解析后的 Version 比较，
    /// 避免 "1.14.3" 子串命中 "1.14.30" 之类的误判；任一侧解析失败一律视为不一致。
    /// </summary>
    private static bool VersionMatches(string expected, string versionOutput)
    {
        var exp = KernelCompat.ParseSingBoxVersion(expected);
        var got = KernelCompat.ParseSingBoxVersion(versionOutput);
        return exp != null && got != null && got == exp;
    }

    private static string RunSingBoxVersion(string exe)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                ArgumentList = { "version" },
                RedirectStandardOutput = true,
                RedirectStandardError = false,   // 重定向却不读 = 死锁风险（本项目已踩过）
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p == null) return "";
            var txt = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return txt;
        }
        catch { return ""; }
    }

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

    public static int CompareVersions(string a, string b)
    {
        var pa = a.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var pb = b.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            int va = i < pa.Length && int.TryParse(pa[i], out var x) ? x : 0;
            int vb = i < pb.Length && int.TryParse(pb[i], out var y) ? y : 0;
            if (va != vb) return va.CompareTo(vb);
        }
        return 0;
    }
}
