using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Neo.Core.Core;

namespace Neo.App.Services;

public sealed record XrayRelease(string Version, string Tag, string DownloadUrl, long SizeBytes, bool Prerelease);

/// sing-box 内核发布。通道 = **上游官方 SagerNet/sing-box 稳定版**（releases/latest）。
///   电脑版只用官方发行版；手机版的自编译内核与本项目无关。
///   官方资产形如 `sing-box-1.14.3-windows-amd64.zip`（zip 内含 sing-box.exe + libcronet.dll + LICENSE），
///   上游不提供 .sha256：安装做「可解压 + 内含 exe + version 版本串自校验」，sha256 仅记录追溯，不作为放行条件。
public sealed record SingBoxRelease(string Version, string Tag, string DownloadUrl, long SizeBytes, bool Prerelease, string AssetName = "");

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

    /// 取 xray 官方最新 release（**非 API 通道**，避开匿名 60次/小时 配额）。
    /// 官方从不把新版本转正，`releases/latest` 会停在最后一个 stable，
    /// 因此这里走 atom 列表取**第一个**版本（含预发布），
    /// 再按固定资产名 `Xray-windows-64.zip` 取下载地址。
    public static async Task<XrayRelease?> FetchLatestAsync(CancellationToken ct = default)
    {
        var preferProxy = ConnectionService.IsConnected;
        var tags = await GitHubChannel.GetTagsFromAtomAsync(XrayOwner, XrayRepo, preferProxy, ct);
        if (tags.Count == 0) return null;

        // atom 里最新在前。官方每个正式版都带 Xray-windows-64.zip，
        // 因此最多试前 3 个即可（预发布偶尔缺资产，翻再多也几乎不会是有效发布）。
        var tried = 0;
        foreach (var tag in tags)
        {
            if (tried >= 3) break;
            //   原实现取第一个 tag ⇒ 可能把预发布内核装给用户，且 UI 里 Prerelease 恒 false 不作提示。
            var ver = tag.TrimStart('v', 'V');
            if (ver.Contains('-')) continue;

            tried++;
            var assets = await GitHubChannel.GetAssetsAsync(XrayOwner, XrayRepo, tag, preferProxy, ct);
            var asset = assets.FirstOrDefault(a =>
                string.Equals(a.Name, "Xray-windows-64.zip", StringComparison.OrdinalIgnoreCase));
            if (asset == null) continue;   // 该版本无 x64 资产，看下一个
            return new XrayRelease(ver, tag, asset.DownloadUrl, asset.SizeBytes, false);
        }
        return null;
    }

    public static async Task<(bool Ok, string Message, XrayRelease? Release)> CheckXrayAsync(string? currentVersion)
    {
        var rel = await FetchLatestAsync();
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
        return (true, $"当前内核：xray {cur}\n最新发布：xray {rel.Version}（{rel.Tag}{preTag}）\n\n{verdict}", rel);
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
        return new SingBoxRelease(coreVer, latest, asset.DownloadUrl, asset.SizeBytes, false, asset.Name);
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

    /// 版本号去掉 `-noGate` / `-neo` 这类后缀，只留数字主干。
    /// 保留原因：兼容用户机器上已装的旧自编译内核（如 `1.14.2-noGate`）与历史资产名，
    /// 否则 `CompareVersions("1.14.3", "1.14.2-noGate")` 里 `"2-noGate"` 会被 int.TryParse
    /// 当成 0 → 每次检查都误报"有新版本"。
    public static string StripSuffix(string v)
    {
        var i = v.IndexOf('-');
        return i > 0 ? v[..i] : v;
    }

    /// 下载并覆盖安装内核：下载 zip → 解压校验 → （必要时自动断开连接）→ 备份 → 覆盖 → 运行验证，失败自动回滚。
    public static async Task<(bool Ok, string Message)> DownloadAndInstallAsync(
        XrayRelease rel, Action<string> progress, CancellationToken ct = default)
    {
        var zipPath = Path.Combine(Path.GetTempPath(), "neo-xray-update.zip");
        var extractDir = Path.Combine(Path.GetTempPath(), "neo-xray-update");

        var attempts = ConnectionService.IsConnected
            ? new[] { "socks", "sys", "direct" }
            : new[] { "sys", "direct", "socks" };

        byte[]? data = null;
        foreach (var kind in attempts)
        {
            try
            {
                progress(kind == "direct" ? "直连下载源…" : "经代理连接下载源…");
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

                //   内核压缩包 <100 MB，10 分钟足够；真取消由调用方传进来的 ct 负责。
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "neo-update-check");

                progress($"下载 Xray-windows-64.zip（{rel.SizeBytes / 1024 / 1024}MB）…");
                data = await client.GetByteArrayAsync(rel.DownloadUrl, ct);
                if (data.Length < 1024 * 1024) { data = null; continue; } // 过小=被劫持/错误页，换路径重试
                break;
            }
            catch (OperationCanceledException) { return (false, "下载已取消。"); }
            catch { }
        }

        if (data == null) return (false, "下载失败：网络不可达（可先连接代理后再试）");

        try
        {
            progress("校验 SHA-256（官方 .dgst）…");
            var dgstText = await FetchReleaseTextAsync(XrayOwner, XrayRepo, rel.Tag, "Xray-windows-64.zip.dgst", ct);
            if (dgstText == null)
                return (false, "未找到官方 .dgst 校验文件，安装中止（不安装未经验证的内核）。");
            if (!KernelInstall.TryParseSha256(dgstText, out var expectHex))
                return (false, "官方 .dgst 解析失败，安装中止。");
            if (!KernelInstall.VerifySha256(data, expectHex, out var actualHex))
                return (false, $"SHA-256 校验失败，安装中止。\n期望 {expectHex}\n实际 {actualHex}");

            progress("解压校验…");
            await File.WriteAllBytesAsync(zipPath, data, ct);
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
        SingBoxRelease rel, Action<string> progress, CancellationToken ct = default)
    {
        var zipPath = Path.Combine(Path.GetTempPath(), "neo-singbox-update.zip");
        var extractDir = Path.Combine(Path.GetTempPath(), "neo-singbox-update");

        var attempts = ConnectionService.IsConnected
            ? new[] { "socks", "sys", "direct" }
            : new[] { "sys", "direct", "socks" };

        byte[]? data = null;
        foreach (var kind in attempts)
        {
            try
            {
                progress(kind == "direct" ? "直连下载源…" : "经代理连接下载源…");
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

                //   内核压缩包 <100 MB，10 分钟足够；真取消由调用方传进来的 ct 负责。
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "neo-update-check");

                progress($"下载 sing-box 内核（{rel.SizeBytes / 1024 / 1024}MB）…");
                data = await client.GetByteArrayAsync(rel.DownloadUrl, ct);
                if (data.Length < 1024 * 1024) { data = null; continue; }
                break;
            }
            catch (OperationCanceledException) { return (false, "下载已取消。"); }
            catch { }
        }

        if (data == null) return (false, "下载失败：网络不可达（可先连接代理后再试）");

        string? backup = null;
        var target = Path.Combine(Neo.Core.NeoPaths.RuntimeDir, "sing-box.exe");
        try
        {
            // 上游官方发行版不提供 .sha256 资产：不再用「缺校验文件」中止。
            // 完整性 = 可解压且内含 sing-box.exe + 新 exe `version` 版本串自校验；
            // sha256 只作为可追溯信息记录（日志 + IncidentLog），不作为放行条件。
            var actualSha = KernelInstall.Sha256Hex(data);
            LogBus.App($"sing-box 内核包 SHA-256（仅追溯）：{actualSha} · {rel.AssetName}", "Debug");
            IncidentLog.Record("kernel-install", $"sing-box 内核包 SHA-256：{actualSha}", rel.AssetName);

            progress("解压校验…");
            try
            {
                await File.WriteAllBytesAsync(zipPath, data, ct);
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
