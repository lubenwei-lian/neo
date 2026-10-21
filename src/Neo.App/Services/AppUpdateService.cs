using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.IO;
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
        string Error);

    public static async Task<UpdateInfo> CheckAsync(CancellationToken ct = default)
    {
        // 走 GitHubChannel（**非 API 通道**）：原实现查 api.github.com/repos/.../releases，
        // 现在改为：releases/latest 拿 tag → expanded_assets 取真实资产清单，
        // 全程不碰 REST API，因此不再受该配额影响。
        var preferProxy = ConnectionService.IsConnected;

        var hit = await GitHubChannel.FindReleaseWithAssetAsync(
            RepoOwner, RepoName,
            assets => assets.FirstOrDefault(a =>
                a.Name.StartsWith("neo-setup-", StringComparison.OrdinalIgnoreCase)
                && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)),
            preferProxy, ct);

        if (hit is not { } r)
        {
            //   旧实现返回 Ok=true / HasNew=false → 界面显示「已是最新版本」，
            //   把网络故障伪装成正常结果，用户永远不会去检查代理。
            LogBus.App("更新检查失败：网络不可达或暂无电脑版发布", "Warning");
            return new UpdateInfo(false, false, "", "", "", "", 0, "网络不可达或暂无发布");
        }

        var (tag, asset) = r;
        var latest = tag.Replace("desktop-", "", StringComparison.OrdinalIgnoreCase).TrimStart('v', 'V');
        var hasNew = CompareVersions(latest, CurrentVersion) > 0;
        var notes = "";

        // 同版本号换资产：版本比较看不出来，再读 release 里的 build-info.json 比构建时间。
        // 只有「构建时间明显更新（>2 分钟）」才算新构建，避免时区/精度抖动误报。
        if (!hasNew)
        {
            try
            {
                var biText = await GitHubChannel.FetchTextAssetAsync(
                    RepoOwner, RepoName, tag, "build-info.json", preferProxy, ct);
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

        LogBus.App($"更新检查完成：最新 {tag}（当前 {CurrentVersion}，构建 {LocalBuildTime:yyyy-MM-dd HH:mm}）→ "
                   + (hasNew ? (notes.Length > 0 ? "发现同版本新构建" : "发现新版本") : "已是最新版本"));
        return new UpdateInfo(true, hasNew, tag, notes, asset.DownloadUrl, asset.Name, asset.SizeBytes, "");
    }

    /// 需要按发布资产的体积做二次校验时改用下面带 expectedSize 的重载。
    public static Task<string?> DownloadAsync(
        string assetUrl, string assetName, Action<double>? progress, CancellationToken ct)
        => DownloadAsync(assetUrl, assetName, progress, 0, ct);

    /// 下载更新包。
    /// <paramref name="expectedSize"/> &gt; 0 时，除 Content-Length 外再与「发布信息里的体积」核对。
    public static async Task<string?> DownloadAsync(
        string assetUrl, string assetName, Action<double>? progress, long expectedSize, CancellationToken ct)
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

                var buf = new byte[81920];
                long got = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    got += n;
                    if (total > 0) progress?.Invoke((double)got / total);
                }

                //   ReadAsync 可能提前返回 0 而不抛异常，半截 exe 会被当成成功交给安装程序。
                if (total > 0 && got != total)
                {
                    LogBus.App($"更新包下载不完整（{got}/{total} B，{assetName}）——本次尝试作废，换通道重试", "Error");
                    try { File.Delete(dest); } catch { }
                    continue;
                }
                if (expectedSize > 0 && got != expectedSize)
                {
                    LogBus.App($"更新包体积与发布信息不符（实得 {got} B，预期 {expectedSize} B，{assetName}）——本次尝试作废，换通道重试", "Error");
                    try { File.Delete(dest); } catch { }
                    continue;
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

    public static void LaunchInstallerAndExit(string setupPath)
    {
        LogBus.App($"启动更新安装程序：{setupPath}");
        Process.Start(new ProcessStartInfo
        {
            FileName = setupPath,
            Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS",
            UseShellExecute = true,
        });
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
