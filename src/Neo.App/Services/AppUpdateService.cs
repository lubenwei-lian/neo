using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Neo.Core;
using Neo.Core.Core;

namespace Neo.App.Services;

public static class AppUpdateService
{
    public const string RepoOwner = "lubenwei-lian";
    public const string RepoName = "neo-desktop-release";

    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    /// 本机安装的构建时间（= neo.dll 的最后写入时间）。
    /// 同版本号换资产时，版本比较看不出差别，需要靠它与 release 里的 build-info.json 比较。
    public static DateTime LocalBuildTime
    {
        get
        {
            try { return File.GetLastWriteTime(Assembly.GetExecutingAssembly().Location); }
            catch { return DateTime.MinValue; }
        }
    }

    public sealed record UpdateInfo(
        bool Ok,
        bool HasNew,
        string LatestTag,
        string Notes,
        string AssetUrl,
        string AssetName,
        long AssetSize,
        string Error,
        // 发布方提供的安装包 SHA-256（.sha256 资产或 build-info.json）；null = 本次发布未提供，只校验体积。
        string? Sha256 = null);

    public static async Task<UpdateInfo> CheckAsync(CancellationToken ct = default)
    {
        // 走 GitHubChannel（**非 API 通道**）：releases/latest 拿 tag → expanded_assets 取真实资产清单，
        // 全程不碰 REST API，因此不再受匿名 60 次/小时配额影响。
        var preferProxy = ConnectionService.IsConnected;

        var hit = await GitHubChannel.FindReleaseWithAssetAsync(
            RepoOwner, RepoName,
            assets => assets.FirstOrDefault(a =>
                a.Name.StartsWith("neo-setup-", StringComparison.OrdinalIgnoreCase)
                && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)),
            preferProxy, ct);

        if (hit is not { } r)
        {
            // 失败分类（旧实现一律返回「网络不可达或暂无发布」，用户不知道下一步做什么）：
            //   ① 网络不可达 → 提示开代理/换网络；② 无发布资产 → 等待发布方补齐；③ 资产列表解析失败 → 反馈。
            var latestTag = await GitHubChannel.GetLatestTagAsync(RepoOwner, RepoName, preferProxy, ct);
            if (latestTag == null)
            {
                LogBus.App("更新检查失败：网络不可达（当前代理 / 系统代理 / 直连均失败）", "Warning");
                return new UpdateInfo(false, false, "", "", "", "", 0,
                    "网络不可达：已自动尝试「当前代理 → 系统代理 → 直连」三条路径均失败。\n"
                    + "请先确认代理可用（可先连接节点）后再点「检查更新」；若网络本身正常，请稍后重试。");
            }

            var assets = await GitHubChannel.GetAssetsAsync(RepoOwner, RepoName, latestTag, preferProxy, ct);
            if (assets.Count == 0)
            {
                LogBus.App($"更新检查失败：未能解析 {latestTag} 的发布资产列表", "Warning");
                return new UpdateInfo(false, false, latestTag, "", "", "", 0,
                    $"发布信息解析失败：未能取到最新发布 {latestTag} 的资产列表（可能网络被劫持或 GitHub 页面结构变化）。\n"
                    + "请稍后重试；若持续出现，请导出诊断包反馈。");
            }

            LogBus.App($"更新检查失败：最新发布 {latestTag} 没有 neo-setup-*.exe 安装包资产", "Warning");
            return new UpdateInfo(false, false, latestTag, "", "", "", 0,
                $"最新发布 {latestTag} 没有 Windows 安装包资产（neo-setup-*.exe）。\n"
                + "请等待发布方补齐资产后重试；若确认资产已存在，请重新检查或反馈日志。");
        }

        var (tag, asset) = r;
        var latest = tag.Replace("desktop-", "", StringComparison.OrdinalIgnoreCase).TrimStart('v', 'V');
        if (string.IsNullOrWhiteSpace(latest))
        {
            LogBus.App($"更新检查失败：发布 tag 格式异常（{tag}）", "Warning");
            return new UpdateInfo(false, false, tag, "", "", "", 0,
                $"发布版本号解析失败（tag「{tag}」格式异常）。\n请稍后重试；若持续出现，请反馈该 tag。");
        }

        var hasNew = CompareVersions(latest, CurrentVersion) > 0;
        var notes = "";

        // build-info.json 只拉一次：既用于「同版本新构建」判断，也用于哈希缺失时的回退。
        string? buildInfoText = null;
        async Task<string?> GetBuildInfoAsync()
        {
            if (buildInfoText != null) return buildInfoText;
            try
            {
                buildInfoText = await GitHubChannel.FetchTextAssetAsync(
                    RepoOwner, RepoName, tag, "build-info.json", preferProxy, ct);
            }
            catch { buildInfoText = null; }
            return buildInfoText;
        }

        // 同版本号换资产：版本比较看不出来，再读 release 里的 build-info.json 比构建时间。
        // 只有「构建时间明显更新（>2 分钟）」才算新构建，避免时区/精度抖动误报。
        if (!hasNew)
        {
            try
            {
                var biText = await GetBuildInfoAsync();
                if (!string.IsNullOrWhiteSpace(biText))
                {
                    using var doc = JsonDocument.Parse(biText);
                    if (doc.RootElement.TryGetProperty("build", out var be)
                        && DateTime.TryParse(be.GetString(), null,
                            System.Globalization.DateTimeStyles.RoundtripKind, out var buildAt))
                    {
                        if (buildAt.ToUniversalTime() > LocalBuildTime.ToUniversalTime().AddMinutes(2))
                        {
                            hasNew = true;
                            notes = "同版本新构建";
                        }
                    }
                }
            }
            catch { }
        }

        // 哈希只在「确实要下载」时解析（避免每次自动检查都多两次网络请求）。
        // 优先同 tag 的 <安装包>.sha256；没有则读 build-info.json 的 assets.sha256[安装包名]；
        // 两者都没有 → 旧 release 兼容：仅体积校验并留 Warning（UI 也会提示）。
        string? sha256 = null;
        if (hasNew)
        {
            sha256 = await TryGetAssetHashAsync(tag, asset.Name, GetBuildInfoAsync);
            if (sha256 == null)
                LogBus.App($"发布未提供 {asset.Name} 的哈希（.sha256 与 build-info.json 均无），安装时仅校验体积", "Warning");
            else
                LogBus.App($"更新包哈希已获取（{asset.Name}）：{sha256}", "Debug");
        }

        LogBus.App($"更新检查完成：最新 {tag}（当前 {CurrentVersion}，构建 {LocalBuildTime:yyyy-MM-dd HH:mm}）→ "
                   + (hasNew ? (notes.Length > 0 ? "发现同版本新构建" : "发现新版本") : "已是最新版本"));
        return new UpdateInfo(true, hasNew, tag, notes, asset.DownloadUrl, asset.Name, asset.SizeBytes, "", sha256);
    }

    /// <summary>
    /// 取安装包哈希：① 同 tag 的 `<安装包名>.sha256` 资产（发布链路生成，格式 `<hex>  <file>`）；
    /// ② 回退 build-info.json 的可选 `assets.sha256[安装包名]`；都没有返回 null（调用方按体积放行并提示）。
    /// 注意：哈希取不到/格式不对**不是**检查失败 —— 旧 release 没有该资产也要能正常更新。
    /// </summary>
    private static async Task<string?> TryGetAssetHashAsync(string tag, string assetName, Func<Task<string?>> getBuildInfo)
    {
        try
        {
            var shaText = await GitHubChannel.FetchTextAssetAsync(
                RepoOwner, RepoName, tag, assetName + ".sha256", ConnectionService.IsConnected);
            if (!string.IsNullOrWhiteSpace(shaText) && KernelInstall.TryParseSha256(shaText, out var hex))
                return hex;
        }
        catch { }

        try
        {
            var biText = await getBuildInfo();
            if (!string.IsNullOrWhiteSpace(biText))
            {
                using var doc = JsonDocument.Parse(biText);
                if (doc.RootElement.TryGetProperty("assets", out var assetsEl)
                    && assetsEl.ValueKind == JsonValueKind.Object
                    && assetsEl.TryGetProperty("sha256", out var shaMap)
                    && shaMap.ValueKind == JsonValueKind.Object
                    && shaMap.TryGetProperty(assetName, out var shaEl)
                    && shaEl.ValueKind == JsonValueKind.String
                    && KernelInstall.TryParseSha256(shaEl.GetString(), out var hex2))
                    return hex2;
            }
        }
        catch (Exception ex)
        {
            LogBus.App($"build-info.json 哈希读取失败（继续按体积校验）：{ex.GetType().Name} {ex.Message}", "Warning");
        }
        return null;
    }

    /// 旧签名（无体积/哈希参数）：等价于 expectedSize=0、无哈希校验（保持调用点兼容）。
    public static Task<string?> DownloadAsync(
        string assetUrl, string assetName, Action<double>? progress, CancellationToken ct)
        => DownloadAsync(assetUrl, assetName, progress, 0, null, ct);

    /// 旧签名（仅体积）：等价于无哈希校验（保持调用点兼容）。
    public static Task<string?> DownloadAsync(
        string assetUrl, string assetName, Action<double>? progress, long expectedSize, CancellationToken ct)
        => DownloadAsync(assetUrl, assetName, progress, expectedSize, expectedSha256: null, ct);

    /// 下载更新包。
    /// <paramref name="expectedSize"/> &gt; 0 时，除 Content-Length 外再与「发布信息里的体积」核对；
    /// <paramref name="expectedSha256"/> 非空时边下边算 SHA-256，不符则删除并失败；
    /// 为 null 表示发布方未提供哈希（旧 release），按体积放行但会留 Warning。
    public static async Task<string?> DownloadAsync(
        string assetUrl, string assetName, Action<double>? progress, long expectedSize,
        string? expectedSha256, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "neo-update");
        var dest = Path.Combine(dir, assetName);
        LogBus.App($"开始下载更新包 {assetName}");

        // 目录创建放在 try 里：原先它在方法的 try 之外，磁盘满 / 权限 / 临时目录被清理时
        // 异常会直接逃逸到调用方（那里只 catch OperationCanceledException），
        // 导致对话框 Hide() 被跳过 —— 进而让 DialogGuard 的门闩永不释放。
        try { Directory.CreateDirectory(dir); }
        catch (Exception ex)
        {
            LogBus.Exception("创建更新包下载目录失败", ex);
            return null;
        }

        var attempts = ConnectionService.IsConnected
            ? new[] { "socks", "sys", "direct" }
            : new[] { "sys", "direct", "socks" };

        foreach (var kind in attempts)
        {
            try
            {
                using var handler = new HttpClientHandler();
                switch (kind)
                {
                    case "socks":
                        handler.Proxy = new WebProxy($"socks5://127.0.0.1:{XrayConfigBuilder.DefaultSocksPort}");
                        handler.UseProxy = true;
                        break;
                    case "sys":
                        handler.UseProxy = true;
                        break;
                    default:
                        handler.UseProxy = false;
                        break;
                }

                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "neo-update-check");
                if (assetUrl.Contains("api.github.com", StringComparison.OrdinalIgnoreCase))
                {
                    client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/octet-stream");
                }

                using var resp = await client.GetAsync(assetUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode) continue;

                var total = resp.Content.Headers.ContentLength ?? -1;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(dest);
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                var buf = new byte[81920];
                long got = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    hasher.AppendData(buf, 0, n);
                    got += n;
                    if (total > 0) progress?.Invoke((double)got / total);
                }

                //   ReadAsync 可能提前返回 0 而不抛异常，半截 exe 会被当成成功交给安装程序。
                if (total > 0 && got != total)
                {
                    LogBus.App($"更新包下载不完整（{got}/{total} B，{assetName}）——本次尝试作废，换通道重试", "Error");
                    try { await dst.DisposeAsync(); } catch { }
                    try { File.Delete(dest); } catch { }
                    continue;
                }
                if (expectedSize > 0 && got != expectedSize)
                {
                    LogBus.App($"更新包体积与发布信息不符（实得 {got} B，预期 {expectedSize} B，{assetName}）——本次尝试作废，换通道重试", "Error");
                    try { await dst.DisposeAsync(); } catch { }
                    try { File.Delete(dest); } catch { }
                    continue;
                }

                // 哈希校验：发布方提供时是硬闸门（不符=删除文件+失败，不换通道重试，避免把坏包当网络问题）；
                // 未提供时（旧 release）按体积放行，但留 Warning 便于追溯。
                var actualSha = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(expectedSha256))
                {
                    if (!string.Equals(actualSha, expectedSha256.Trim().ToLowerInvariant(), StringComparison.Ordinal))
                    {
                        LogBus.App($"更新包哈希校验失败（期望 {expectedSha256}，实际 {actualSha}，{assetName}）——已删除本次下载", "Error");
                        IncidentLog.Record("update", "更新包哈希校验失败", $"{assetName} 期望 {expectedSha256} 实际 {actualSha}");
                        try { await dst.DisposeAsync(); } catch { }
                        try { File.Delete(dest); } catch { }
                        return null;
                    }
                    LogBus.App($"更新包哈希校验通过：{actualSha}（{assetName}）", "Debug");
                }
                else
                {
                    LogBus.App($"更新包未提供哈希，仅校验体积：{assetName}（{got} B，SHA-256 {actualSha}）", "Warning");
                }

                return dest;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch { }
        }

        // 大小校验失败的分支已在循环内记过 Error；这里统一收尾（网络不可达或所有通道校验均失败）。
        LogBus.App("更新包下载失败（直连与代理均不可用，或下载内容校验未通过）", "Warning");
        return null;
    }

    /// <summary>
    /// 安装前收尾并安排「先退出、再安装」：
    ///   ① 若已连接先 Disconnect("安装更新") 并等 1.5s；
    ///   ② 写 %TEMP%\neo-pending-update.json（下次启动据此判断更新是否真的完成）；
    ///   ③ 启动一个脱离本进程的 PowerShell 等待器：它 Wait-Process 本 PID（最多 20s），
    ///      等 neo 退出、AppMutex 释放后再 Start-Process 安装器 —— 避免 Inno 的 AppMutex 检查
    ///      在 /SILENT + SUPPRESSMSGBOXES 下默认取消（实测 exit=1，什么都不装）。
    /// 调用方拿到 Ok 后应立即 ExitForUpdate()。
    /// </summary>
    /// <summary>
    /// 2026-10-23：更新包下载改走 KernelDownloader——**断点续传 + 实时进度 + SHA-256 校验**，
    /// 与内核/分流数据同一套链路（用户要求「不只是这次更新，都能借鉴的都做」）。
    /// 旧实现在这里用一次性流拷贝：没有续传、进度只有百分比、且下载进度只能塞进弹窗。
    /// </summary>
    public static async Task<(bool Ok, string? Path, string Message)> DownloadResumableAsync(
        string assetUrl, string assetName, Action<DownloadProgress>? progress,
        string? expectedSha256, CancellationToken ct)
    {
        string dest;
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "neo-update");
            Directory.CreateDirectory(dir);
            dest = Path.Combine(dir, assetName);
        }
        catch (Exception ex)
        {
            return (false, null, "无法创建更新包下载目录：" + ex.Message);
        }

        var (ok, msg) = await KernelDownloader.DownloadAsync(assetUrl, dest, progress, ct);
        if (!ok) return (false, null, msg);

        var (match, actual) = await KernelDownloader.VerifySha256Async(dest, expectedSha256, ct);
        if (!match)
        {
            KernelDownloader.DiscardPart(dest);
            try { File.Delete(dest); } catch { }
            LogBus.App($"更新包哈希校验失败（期望 {expectedSha256}，实际 {actual}，{assetName}）——已删除本次下载", "Error");
            IncidentLog.Record("update", "更新包哈希校验失败", $"{assetName} 期望 {expectedSha256} 实际 {actual}");
            return (false, null, $"更新包 SHA-256 校验失败，已删除下载文件。\n期望 {expectedSha256}\n实际 {actual}");
        }

        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            LogBus.App($"更新包未提供官方哈希，已记录实际 SHA-256：{actual}（{assetName}）", "Warning");
            return (true, dest, $"已下载（发布方未提供哈希，实际 SHA-256：{actual}）");
        }

        LogBus.App($"更新包哈希校验通过：{actual}（{assetName}）", "Debug");
        return (true, dest, $"已下载并通过 SHA-256 校验（{actual}）");
    }

    public static async Task<(bool Ok, string? Error)> LaunchInstallerAndExitAsync(string setupPath, string? expectedVersion = null)
    {
        try
        {
            if (ConnectionService.IsConnected)
            {
                LogBus.App("安装更新：先断开当前连接…");
                ConnectionService.Disconnect("安装更新");
                await Task.Delay(1500);
            }

            // 退出前先写「待确认更新」标记；下次启动时由 CheckPendingUpdateResult() 对照版本/build 判断结果。
            var pendingPath = Path.Combine(Path.GetTempPath(), "neo-pending-update.json");
            var pending = new PendingUpdate
            {
                ExpectedVersion = expectedVersion ?? "",
                ExpectedBuild = "",
                PreviousVersion = CurrentVersion,
                PreviousBuild = LocalBuildTime.ToString("O"),
                SetupPath = setupPath,
                At = DateTimeOffset.Now.ToString("O"),
            };
            try { File.WriteAllText(pendingPath, JsonSerializer.Serialize(pending)); }
            catch (Exception ex)
            {
                LogBus.App($"写更新状态标记失败（不影响安装，但下次启动无法确认结果）：{ex.GetType().Name} {ex.Message}", "Warning");
            }

            // 不能直接 Process.Start 安装器：Inno 的 AppMutex 检查在 /SILENT+SUPPRESSMSGBOXES 下会默认取消。
            // 用脱离本进程的 PowerShell 等待器：等本 PID 退出（AppMutex 释放）后再启动安装器。
            var script =
                $"Wait-Process -Id {Environment.ProcessId} -Timeout 20 -ErrorAction SilentlyContinue; " +
                $"Start-Process -FilePath {PsQuote(setupPath)} -ArgumentList '/SILENT','/SUPPRESSMSGBOXES','/NORESTART'";
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(setupPath) ?? Path.GetTempPath(),
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-WindowStyle");
            psi.ArgumentList.Add("Hidden");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);

            Process.Start(psi);
            LogBus.App($"已安排「退出后安装」：等待器已启动，neo 退出后将安装 {setupPath}");
            IncidentLog.Record("update", "已安排退出后安装更新", setupPath);
            return (true, null);
        }
        catch (Exception ex)
        {
            LogBus.Exception("安排更新安装失败", ex);
            IncidentLog.Record("update", "安排更新安装失败", ex.Message);
            return (false, $"{ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>PowerShell 单引号转义：路径里的单引号写成两个单引号。</summary>
    private static string PsQuote(string value) => "'" + (value ?? "").Replace("'", "''") + "'";

    /// <summary>
    /// 「更新未完成」探测：模块加载时（neo 启动）检查 %TEMP%\neo-pending-update.json。
    ///   文件存在 + 当前版本/build 已变 → INFO「更新成功」；
    ///   文件存在但版本/build 没变 → Warning + IncidentLog("update","更新未完成",原因)；
    /// 无论结果都删除标记，避免下次重复报告。
    /// </summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void InitPendingUpdateCheck()
    {
        try { _ = Task.Run(CheckPendingUpdateResult); } catch { }
    }

    /// <summary>供模块初始化/调试调用：核对上次「退出后安装」是否真的成功。</summary>
    public static void CheckPendingUpdateResult()
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "neo-pending-update.json");
            if (!File.Exists(path)) return;

            PendingUpdate? pending = null;
            // 大小写不敏感：手工写的标记文件（调试/排障）用 camelCase 也能正确识别。
            try { pending = JsonSerializer.Deserialize<PendingUpdate>(File.ReadAllText(path), PendingJsonOptions); }
            catch (Exception ex) { LogBus.App($"更新状态文件解析失败，已清理：{ex.GetType().Name}", "Warning"); }
            try { File.Delete(path); } catch { }
            if (pending == null) return;

            var nowVersion = CurrentVersion;
            var nowBuild = LocalBuildTime;
            var prevVersion = pending.PreviousVersion ?? "";
            var prevBuildText = pending.PreviousBuild ?? "";
            DateTime.TryParse(prevBuildText, null, System.Globalization.DateTimeStyles.RoundtripKind, out var prevBuild);

            var versionChanged = !string.Equals(nowVersion, prevVersion, StringComparison.Ordinal);
            // 版本号不变但换了构建（同版本新构建）：neo.dll 的 mtime 会明显变化（>2 分钟）。
            var buildChanged = prevBuild != DateTime.MinValue && nowBuild != DateTime.MinValue
                && Math.Abs((nowBuild - prevBuild).TotalMinutes) >= 2;

            // 判定成功必须以「达到预期版本」为准，而不是「只要有变化就算成功」——
            // 否则用户取消安装、只是应用重启/手动换了二进制，也会被误报成「更新成功」。
            var expectedVersion = pending.ExpectedVersion ?? "";
            var reachedExpected = expectedVersion.Length > 0
                ? CompareVersions(nowVersion, expectedVersion) >= 0
                : (versionChanged || buildChanged);
            if (reachedExpected)
            {
                LogBus.App($"更新成功：{prevVersion}（{prevBuild:yyyy-MM-dd HH:mm}）→ {nowVersion}（{nowBuild:yyyy-MM-dd HH:mm}）");
                IncidentLog.Record("update", "更新成功",
                    $"目标 {pending.ExpectedVersion}；{prevVersion}/{prevBuildText} → {nowVersion}/{nowBuild:O}");
                return;
            }

            var reason = string.IsNullOrWhiteSpace(pending.SetupPath) || !File.Exists(pending.SetupPath)
                ? "安装包已不存在（可能被清理/杀软拦截）"
                : "版本与构建时间均未变化（安装器可能被取消或安装失败）";
            LogBus.App($"更新未完成：上次已安排安装 {pending.ExpectedVersion}，但当前仍为 {nowVersion}（{reason}）", "Warning");
            IncidentLog.Record("update", "更新未完成",
                $"{reason}；目标 {pending.ExpectedVersion}，当前 {nowVersion}，安装包 {pending.SetupPath}");
        }
        catch (Exception ex)
        {
            LogBus.App($"检查更新结果失败（已忽略）：{ex.GetType().Name} {ex.Message}", "Warning");
        }
    }

    private static readonly JsonSerializerOptions PendingJsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>「退出后安装」的状态标记（%TEMP%\neo-pending-update.json）。</summary>
    private sealed class PendingUpdate
    {
        public PendingUpdate() { }
        public string ExpectedVersion { get; set; } = "";
        public string ExpectedBuild { get; set; } = "";
        public string PreviousVersion { get; set; } = "";
        public string PreviousBuild { get; set; } = "";
        public string SetupPath { get; set; } = "";
        public string At { get; set; } = "";
    }

    public static int CompareVersions(string a, string b)
    {
        //   否则 "0.2.26-beta" 的 "26-beta" 会被 int.TryParse 当 0，导致预发布版被误判成旧版本。
        a = StripSuffix(a ?? "");
        b = StripSuffix(b ?? "");
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

    private static string StripSuffix(string v)
    {
        var i = v.IndexOf('-');
        return i > 0 ? v[..i] : v;
    }
}
