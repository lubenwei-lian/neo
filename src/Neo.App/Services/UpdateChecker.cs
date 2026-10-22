using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Neo.App.Services;

public sealed record XrayRelease(string Version, string Tag, string DownloadUrl, long SizeBytes, bool Prerelease);

/// sing-box 内核发布。通道指向 **neo 自己的发布仓库**，不是上游 SagerNet。
///   资产内容是**上游官方发行版**（zip 内含 sing-box.exe + libcronet.dll），
///   与同名 .sha256 一起上传；应用只做 SHA-256、版本串与可执行性校验，不再要求任何自编译补丁。
public sealed record SingBoxRelease(string Version, string Tag, string DownloadUrl, long SizeBytes, bool Prerelease, string AssetName = "");

public static class UpdateChecker
{
    private const string XrayOwner = "XTLS";
    private const string XrayRepo = "Xray-core";

    /// <summary>neo 自己的发布仓库（内核 + 安装包同仓，用资产名前缀区分）。</summary>
    private const string NeoOwner = "lubenwei-lian";
    private const string NeoRepo = "neo-desktop-release";

    /// <summary>内核资产名前缀：只认这个前缀，避免把安装包当成内核。</summary>
    public const string SingBoxAssetPrefix = "neo-singbox-";

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

    /// 从 neo 自己的发布仓库取最新的 **sing-box 内核**资产（上游官方发行版重新打包）。
    /// 走 <see cref="GitHubChannel"/>（非 API 通道）——原先用 `api.github.com/repos/.../releases`
    /// 只认资产名前缀 <see cref="SingBoxAssetPrefix"/> 且扩展名 `.zip`，与同仓库安装包区分。
    /// 用户看到的是长时间转圈。而内核资产按约定与最新版一起发，
    /// 最新版没有就等于「还没发内核」——直接如实报告比慢慢翻旧版更有用。
    public static async Task<SingBoxRelease?> FetchLatestSingBoxAsync(CancellationToken ct = default)
    {
        var preferProxy = ConnectionService.IsConnected;
        var latest = await GitHubChannel.GetLatestTagAsync(NeoOwner, NeoRepo, preferProxy, ct);
        if (latest == null) return null;

        var assets = await GitHubChannel.GetAssetsAsync(NeoOwner, NeoRepo, latest, preferProxy, ct);
        var asset = assets.FirstOrDefault(a =>
            a.Name.StartsWith(SingBoxAssetPrefix, StringComparison.OrdinalIgnoreCase)
            && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        if (asset == null) return null;

        var coreVer = ExtractCoreVersion(asset.Name);
        return new SingBoxRelease(coreVer, latest, asset.DownloadUrl, asset.SizeBytes, false, asset.Name);
    }

    /// 从内核资产名里抽出内核版本号。
    /// `neo-singbox-1.14.3-windows-amd64.zip` → `1.14.3`
    /// 兼容旧命名：`neo-singbox-1.14.2-noGate-windows-amd64.zip` → `1.14.2-noGate`
    /// 规则：去掉前缀 → 取到 `-windows-` 之前 → 去掉结尾平台的 `-amd64` 等。
    /// 抽不出来（命名不符合约定）时返回 "unknown"，让上层提示而不是崩溃。
    public static string ExtractCoreVersion(string assetName)
    {
        var s = assetName ?? "";
        if (s.StartsWith(SingBoxAssetPrefix, StringComparison.OrdinalIgnoreCase))
            s = s[SingBoxAssetPrefix.Length..];
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
                "发布仓库里暂无 sing-box 内核资产（若网络不通，请确认代理可用后重试）。\n\n" +
                $"说明：sing-box 内核与电脑版安装包在同一个发布仓库里，" +
                $"只有当作者发布了名为 {SingBoxAssetPrefix}… 的内核压缩包后才能从这里更新。",
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

        var preTag = rel.Prerelease ? " · 预发布" : "";
        return (true,
            $"当前内核：sing-box {cur}\n最新发布：sing-box {rel.Version}（{rel.Tag}{preTag}）\n\n{verdict}",
            rel);
    }

    /// 版本号去掉 `-noGate` / `-neo` 这类后缀，只留数字主干。
    /// 保留原因：兼容历史资产名（`neo-singbox-1.14.2-noGate-…`）与用户机器上已装的
    /// `1.14.2-noGate` 内核，否则 `CompareVersions("1.14.3", "1.14.2-noGate")` 里
    /// `"2-noGate"` 会被 int.TryParse 当成 0 → 每次检查都误报"有新版本"。
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

    /// 下载并覆盖安装 **sing-box 内核**（上游官方发行版）。
    /// 与 xray 的差异：
    /// ① **自校验版本**：跑 `sing-box version` 确认拿到的是期望版本；
    /// ② 安装成功后同时重置版本缓存（下次连接重新探测内核版本）。
    /// 不再要求 balancer 出站能力：TUN 配置只产出单节点 + direct，上游官方版即可。
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
            //   缺失就中止 —— 发布方 .sha256 是唯一的下载完整性依据，不能静默跳过。
            progress("校验 SHA-256（发布方 .sha256）…");
            var shaAsset = string.IsNullOrEmpty(rel.AssetName) ? "" : rel.AssetName + ".sha256";
            var shaText = shaAsset.Length == 0
                ? null
                : await FetchReleaseTextAsync(NeoOwner, NeoRepo, rel.Tag, shaAsset, ct);
            if (shaText == null)
                return (false, $"发布仓库里没有 {shaAsset}，安装中止（不安装未经验证的内核）。\n"
                             + "请在 release 里随内核 zip 一起上传同名 .sha256 文件（内容为 SHA-256 十六进制）。");
            if (!KernelInstall.TryParseSha256(shaText, out var expectHex2))
                return (false, $"{shaAsset} 解析失败，安装中止。");
            if (!KernelInstall.VerifySha256(data, expectHex2, out var actualHex2))
                return (false, $"SHA-256 校验失败，安装中止。\n期望 {expectHex2}\n实际 {actualHex2}");

            progress("解压校验…");
            await File.WriteAllBytesAsync(zipPath, data, ct);
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
            ZipFile.ExtractToDirectory(zipPath, extractDir);

            // zip 内可能带一层目录（上游发布包布局），递归找一个 sing-box.exe。
            var newExe = Directory.EnumerateFiles(extractDir, "sing-box.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (newExe == null) return (false, "解压后未找到 sing-box.exe，安装中止。");

            progress("校验内核版本…");
            var verOut = RunSingBoxVersion(newExe);
            if (!verOut.Contains(rel.Version, StringComparison.OrdinalIgnoreCase))
                return (false, $"新内核自校验不符（期望 {rel.Version}，实得 {(verOut.Trim().Length > 0 ? verOut.Trim() : "无输出")}），安装中止，未改动现有文件。");

            if (ConnectionService.IsConnected)
            {
                progress("断开当前连接（内核文件被占用）…");
                ConnectionService.Disconnect("安装新内核前断开");
            }

            backup = target + ".bak-prev";
            progress("覆盖安装…");
            KernelInstall.ReplaceAtomically(newExe, target, backup);

            var installed = RunSingBoxVersion(target);
            if (!installed.Contains(rel.Version, StringComparison.OrdinalIgnoreCase))
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
                    if (!iv.Contains(rel.Version, StringComparison.OrdinalIgnoreCase))
                        File.Copy(backup, target, true);
                }
            }
            catch { }
            return (false, "安装失败：" + ex.Message);
        }
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

    /// 取同一 tag 下的小文本资产（xray 的 .dgst / sing-box 的 .sha256）。
    /// 三路（socks / 系统代理 / 直连）依次尝试；取不到返回 null
    /// （调用方按「缺少校验文件」中止安装，不静默跳过）。
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
