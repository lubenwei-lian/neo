using System.Net;
using System.Net.Http;

namespace Neo.App.Services;

/// <summary>一个 GitHub release 资产（文件名 + 下载 URL + 体积）。</summary>
public sealed record GhAsset(string Name, string DownloadUrl, long SizeBytes);

/// GitHub 发布信息获取 —— **走非 API 通道，避开匿名 REST 配额**。
///   匿名的 `api.github.com` 只有 **60 次/小时** 配额，用尽后一律
///   `HTTP 403 rate limit exceeded`（`X-RateLimit-Remaining: 0`）。
///   限流按出口 IP 算，换代理也没用。用户看到的就是
///   「明明开着代理，点检查更新却说网络不可达」。
///   · `releases/latest`                        —— 302 重定向，最终 URL 末尾即最新 tag
///   · `releases.atom`                          —— Atom feed，含全部版本 tag 与标题
///   · `releases/expanded_assets/{tag}`         —— 前端资产列表分片，含**真实资产清单**
///   · `releases/download/{tag}/{asset}`        —— 资产本体，重定向到 release-assets
/// 性能：三路（socks 代理 / 系统代理 / 直连）**并发竞速**，谁先成功用谁 ——
///   原先串行且各自 15s 超时，最坏 45s；现在最坏只等一路超时。
public static class GitHubChannel
{
    /// <summary>请求超时。竞速下这是「最坏等待」上限，不是每路之和。</summary>
    private static TimeSpan Timeout => TimeSpan.FromSeconds(12);

    /// <summary>构造一路 HTTP handler。</summary>
    private static HttpClientHandler MakeHandler(string kind)
    {
        var h = new HttpClientHandler();
        switch (kind)
        {
            case "socks":
                // neo 自己的本地 socks 端口；socks5:// = 本地解析域名。
                h.Proxy = new WebProxy($"socks5://127.0.0.1:{Neo.Core.Core.XrayConfigBuilder.DefaultSocksPort}");
                h.UseProxy = true;
                break;
            case "sys":
                h.UseProxy = true;    // 系统（IE）代理；未开启时等价直连
                break;
            default:
                h.UseProxy = false;   // 强制直连，绕过一切代理
                break;
        }
        return h;
    }

    /// <summary>尝试顺序：连着代理就先把代理放前面（省一轮直连超时），否则反之。</summary>
    private static string[] Order(bool preferProxy) =>
        preferProxy ? new[] { "socks", "sys", "direct" } : new[] { "direct", "sys", "socks" };

    /// 三路并发竞速：第一个返回非 null 的结果胜出，其余取消。
    /// 这是「检查更新很慢」的修复核心：串行三路 × 各自超时 = 最坏 45s；
    /// 并发后最坏只等最快的那条失败路径。失败路径（如代理未开）通常是**立即**被拒，
    /// 因此实际耗时接近单条成功路径。
    private static async Task<T?> RaceAsync<T>(
        bool preferProxy,
        Func<HttpClientHandler, string, CancellationToken, Task<T?>> attempt,
        CancellationToken ct) where T : class
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = new List<(Task<T?> Task, string Kind)>();
        foreach (var kind in Order(preferProxy))
        {
            var k = kind;
            tasks.Add((Task.Run(() => attempt(MakeHandler(k), k, linked.Token), linked.Token), k));
        }

        while (tasks.Count > 0)
        {
            var done = await Task.WhenAny(tasks.Select(t => t.Task));
            var idx = tasks.FindIndex(t => ReferenceEquals(t.Task, done));
            var (task, _) = tasks[idx];
            tasks.RemoveAt(idx);
            try
            {
                var r = await task;
                if (r != null)
                {
                    try { linked.Cancel(); } catch { }
                    return r;
                }
            }
            catch { }
        }
        return null;
    }

    /// 取最新 release 的 tag：优先 `releases/latest` 重定向（一次请求），
    /// 失败退 `releases.atom`。全程不碰 REST API。
    public static Task<string?> GetLatestTagAsync(string owner, string repo, bool preferProxy, CancellationToken ct = default)
        => RaceAsync<string>(preferProxy, async (handler, _, token) =>
        {
            using (handler)
            {
                try
                {
                    using var client = new HttpClient(handler) { Timeout = Timeout };
                    client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "neo-update-check");
                    using var req = new HttpRequestMessage(HttpMethod.Get, $"https://github.com/{owner}/{repo}/releases/latest");
                    using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token);
                    if (!resp.IsSuccessStatusCode) return null;
                    var final = resp.RequestMessage?.RequestUri?.ToString() ?? "";
                    var tag = TagFromUrl(final);
                    return tag.Length > 0 ? tag : null;
                }
                catch (OperationCanceledException) { return null; }
                catch { return null; }
            }
        }, ct);

    /// <summary>
    /// 用 GitHub REST API 取「按发布时间倒序」的发布列表及 **prerelease 标记**。
    /// 为什么只在「检查预发布内核」时用：匿名 API 有 60 次/小时/IP 的配额，
    /// 而 atom / releases/latest 通道不暴露 prerelease 标记，无法区分预发布。
    /// 失败（含被限流）返回 null，调用方自动退回 atom 通道（只是拿不到预发布）。
    /// </summary>
    public static Task<List<(string Tag, bool Prerelease)>?> GetReleaseFlagsAsync(
        string owner, string repo, bool preferProxy, CancellationToken ct = default)
        => RaceAsync<List<(string Tag, bool Prerelease)>>(preferProxy, async (handler, _, token) =>
        {
            using (handler)
            {
                try
                {
                    using var client = new HttpClient(handler) { Timeout = Timeout };
                    client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "neo-update-check");
                    client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
                    using var resp = await client.GetAsync(
                        $"https://api.github.com/repos/{owner}/{repo}/releases?per_page=20", token);
                    if (!resp.IsSuccessStatusCode) return null;
                    var json = await resp.Content.ReadAsStringAsync(token);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
                    var list = new List<(string Tag, bool Prerelease)>();
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        if (!el.TryGetProperty("tag_name", out var t)) continue;
                        var tag = t.GetString();
                        if (string.IsNullOrWhiteSpace(tag)) continue;
                        var pre = el.TryGetProperty("prerelease", out var p)
                                  && p.ValueKind == System.Text.Json.JsonValueKind.True;
                        list.Add((tag, pre));
                    }
                    return list.Count > 0 ? list : null;
                }
                catch (OperationCanceledException) { return null; }
                catch { return null; }
            }
        }, ct);

    /// <summary>从 releases.atom 取版本 tag 列表（最新在前）。取不到时返回空表（调用方无需判 null）。</summary>
    public static async Task<List<string>> GetTagsFromAtomAsync(string owner, string repo, bool preferProxy, CancellationToken ct = default)
    {
        var r = await RaceAsync<List<string>>(preferProxy, async (handler, _, token) =>
        {
            using (handler)
            {
                try
                {
                    using var client = new HttpClient(handler) { Timeout = Timeout };
                    client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "neo-update-check");
                    using var resp = await client.GetAsync($"https://github.com/{owner}/{repo}/releases.atom", token);
                    if (!resp.IsSuccessStatusCode) return null;
                    var xml = await resp.Content.ReadAsStringAsync(token);
                    var list = ParseAtomTags(xml);
                    return list.Count > 0 ? list : null;
                }
                catch (OperationCanceledException) { return null; }
                catch { return null; }
            }
        }, ct);
        return r ?? new List<string>();
    }

    /// <summary>解析 atom XML 里的 releases/tag/&lt;tag&gt; 链接（去重、保序）。</summary>
    public static List<string> ParseAtomTags(string xml)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(xml)) return list;
        const string marker = "/releases/tag/";
        var idx = 0;
        while (true)
        {
            var hit = xml.IndexOf(marker, idx, StringComparison.OrdinalIgnoreCase);
            if (hit < 0) break;
            var s = hit + marker.Length;
            var e = s;
            while (e < xml.Length && xml[e] != '"' && xml[e] != '<' && xml[e] != '&' && !char.IsWhiteSpace(xml[e])) e++;
            if (e > s)
            {
                var tag = xml[s..e];
                if (!list.Contains(tag)) list.Add(tag);
            }
            idx = e > hit ? e : hit + marker.Length;
        }
        return list;
    }

    /// <summary>从任意 GitHub URL 里抽 tag（.../releases/tag/&lt;tag&gt;）。</summary>
    public static string TagFromUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        const string marker = "/releases/tag/";
        var i = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return "";
        var s = i + marker.Length;
        var e = s;
        while (e < url.Length && url[e] != '?' && url[e] != '#' && url[e] != '/') e++;
        return url[s..e];
    }

    /// 取某个 tag 下的小文本资产（build-info.json / .sha256 之类）。
    /// 三路（socks / 系统代理 / 直连）依次尝试，取不到返回 null。
    public static async Task<string?> FetchTextAssetAsync(string owner, string repo, string tag,
        string assetName, bool preferProxy, CancellationToken ct = default)
    {
        try
        {
            var assets = await GetAssetsAsync(owner, repo, tag, preferProxy, ct);
            var asset = assets.FirstOrDefault(a => string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase));
            if (asset == null) return null;
            foreach (var kind in Order(preferProxy))
            {
                try
                {
                    using var handler = MakeHandler(kind);
                    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
                    client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "neo-update-check");
                    var text = await client.GetStringAsync(asset.DownloadUrl, ct);
                    if (!string.IsNullOrEmpty(text)) return text;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }
        return null;
    }

    /// 取某 tag 下的**真实资产清单**。端点 `releases/expanded_assets/{tag}` 是
    /// GitHub 前端渲染 release 资产列表的分片 HTML，**不是 REST API、不受配额限制**。
    /// 取不到时返回空表（调用方无需判 null）。
    public static async Task<List<GhAsset>> GetAssetsAsync(string owner, string repo, string tag, bool preferProxy, CancellationToken ct = default)
    {
        var r = await RaceAsync<List<GhAsset>>(preferProxy, async (handler, _, token) =>
        {
            using (handler)
            {
                try
                {
                    using var client = new HttpClient(handler) { Timeout = Timeout };
                    client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "neo-update-check");
                    using var resp = await client.GetAsync($"https://github.com/{owner}/{repo}/releases/expanded_assets/{tag}", token);
                    if (!resp.IsSuccessStatusCode) return null;
                    var html = await resp.Content.ReadAsStringAsync(token);
                    var list = ParseExpandedAssets(html, owner, repo, tag);
                    return list.Count > 0 ? list : null;
                }
                catch (OperationCanceledException) { return null; }
                catch { return null; }
            }
        }, ct);
        return r ?? new List<GhAsset>();
    }

    /// <summary>解析 expanded_assets 的 HTML，抽出资产名与下载 URL（体积由下载流程统计）。</summary>
    public static List<GhAsset> ParseExpandedAssets(string html, string owner, string repo, string tag)
    {
        var list = new List<GhAsset>();
        if (string.IsNullOrEmpty(html)) return list;
        var prefix = $"/{owner}/{repo}/releases/download/{tag}/";
        var idx = 0;
        while (true)
        {
            var hit = html.IndexOf(prefix, idx, StringComparison.OrdinalIgnoreCase);
            if (hit < 0) break;
            var s = hit + prefix.Length;
            var e = s;
            while (e < html.Length && html[e] != '"' && html[e] != '\'' && html[e] != '<') e++;
            var raw = html[s..e];
            if (raw.Length > 0)
            {
                var name = Uri.UnescapeDataString(raw);
                if (!list.Any(a => a.Name == name))
                    list.Add(new GhAsset(name, $"https://github.com/{owner}/{repo}/releases/download/{tag}/{raw}", 0));
            }
            idx = e > hit ? e : hit + prefix.Length;
        }
        return list;
    }

    /// 找一个含指定资产的 release。
    /// 因此策略是：
    ///   ① 先取最新 tag，看它有没有目标资产 —— 有就立刻返回（一次请求，最快路径）；
    ///   ② 只有当最新版**确实没有**该资产时，才在 atom 列表里**有限地**往前翻
    ///      （最多 <see cref="MaxFallbackTags"/> 个），避免长尾卡顿。
    private const int MaxFallbackTags = 3;

    public static async Task<(string Tag, GhAsset Asset)?> FindReleaseWithAssetAsync(
        string owner, string repo, Func<IReadOnlyList<GhAsset>, GhAsset?> pick,
        bool preferProxy, CancellationToken ct = default)
    {
        var latest = await GetLatestTagAsync(owner, repo, preferProxy, ct);
        if (latest == null) return null;

        var assets = await GetAssetsAsync(owner, repo, latest, preferProxy, ct);
        var hit = pick(assets);
        if (hit != null) return (latest, hit);

        // 最新版没有目标资产 → 有限度地往前翻（最多 MaxFallbackTags 个）
        var all = await GetTagsFromAtomAsync(owner, repo, preferProxy, ct);
        var tried = 0;
        foreach (var t in all)
        {
            if (t == latest) continue;
            if (tried >= MaxFallbackTags) break;
            tried++;
            var a2 = await GetAssetsAsync(owner, repo, t, preferProxy, ct);
            var h2 = pick(a2);
            if (h2 != null) return (t, h2);
        }
        return null;
    }
}
