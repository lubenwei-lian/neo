using System.Net.Http;
using Neo.Core.Models;

namespace Neo.Core.Parsing;

public sealed class SubscriptionFetchResult
{
    public bool Success { get; private set; }
    public string? Error { get; private set; }
    public List<NodeItem> Nodes { get; } = new();
    public string? RawText { get; private set; }
    public string? TrafficInfo { get; internal set; }
    public string? SourceUrl { get; set; }

    public long SubUpload { get; internal set; }
    public long SubDownload { get; internal set; }
    public long SubTotal { get; internal set; }
    public long SubExpireUnix { get; internal set; }

    internal static SubscriptionFetchResult Fail(string error) =>
        new() { Success = false, Error = error };

    internal static SubscriptionFetchResult Ok(string content) =>
        new() { Success = true, RawText = content };
}

public static class SubscriptionFetcher
{
    public const string UserAgent = "v2rayN/7.24.8";

    /// 可选的「代理兜底」HTTP 处理器供给器，由上层（Neo.App）注入。
    /// 为什么用回调注入而不是直接引用 Neo.App 的代理代码：本类在 Neo.Core，
    /// 依赖方向是 App → Core，Core 不能反向依赖 App（会成环）。
    /// 语义：**直连优先，代理兜底**。订阅服务器通常在国内、直连更快且更省流量；
    /// 只有在直连失败（超时/DNS 被污染/被墙）时，才让上层提供走代理的 handler 重试。
    /// 上层返回 null 表示它也没有可用代理，那就只剩直连一条路。
    public static Func<HttpMessageHandler>? ProxyFallbackFactory { get; set; }

    public static async Task<SubscriptionFetchResult> FetchAsync(string urlOrPath, CancellationToken ct = default)
    {
        try
        {
            if (urlOrPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                urlOrPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                // ① 直连优先（handler 传 null → 内部自建 UseProxy=false 的处理器）
                var (ok, resp, body, ex) = await TryFetchAsync(urlOrPath, null, ct);

                // ② 直连失败 → 代理兜底重试一次
                if (!ok && ProxyFallbackFactory is { } factory)
                {
                    HttpMessageHandler? ph = null;
                    try { ph = factory(); } catch { }
                    if (ph != null)
                    {
                        LogBus("订阅直连失败，改用代理重试");
                        using (ph)
                        {
                            var (ok2, resp2, body2, _) = await TryFetchAsync(urlOrPath, ph, ct);
                            if (ok2)
                            {
                                ok = true; resp = resp2; body = body2;
                            }
                        }
                    }
                }

                if (!ok) return SubscriptionFetchResult.Fail(ex ?? "请求失败");

                if (resp != null && !resp.IsSuccessStatusCode)
                    return SubscriptionFetchResult.Fail($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");

                var r = ParseContent(body ?? "");
                if (r.Success) r.SourceUrl = urlOrPath;
                if (resp != null) ParseUserInfo(resp, r);
                if (r.Success && r.SubTotal == 0)
                    await FillUsageByProbeAsync(urlOrPath, r, ct);
                return r;
            }
            else
            {
                if (!File.Exists(urlOrPath))
                    return SubscriptionFetchResult.Fail("文件不存在: " + urlOrPath);
                var body = await File.ReadAllTextAsync(urlOrPath, ct);
                var r = ParseContent(body);
                if (r.Success) r.SourceUrl = urlOrPath;
                return r;
            }
        }
        catch (TaskCanceledException)
        {
            return SubscriptionFetchResult.Fail("请求超时（12 秒）");
        }
        catch (Exception ex)
        {
            return SubscriptionFetchResult.Fail(ex.Message);
        }
    }

    /// <summary>单次拉取。返回 (成功?, 响应, 正文, 直连失败原因)。</summary>
    private static async Task<(bool Ok, HttpResponseMessage? Resp, string? Body, string? Err)> TryFetchAsync(
        string url, HttpMessageHandler? handler, CancellationToken ct)
    {
        HttpClientHandler? owned = null;
        HttpMessageHandler use = handler ?? (owned = new HttpClientHandler { Proxy = null, UseProxy = false });
        try
        {
            //   原为 30s，加上「直连失败→代理兜底」是**串行**两步，
            //   正是卡在直连这一步的 30s 上）。订阅服务器多数在国内，
            //   12s 足够完成一次正常拉取；失败后尽快进代理兜底，总上限 24s。
            using var client = new HttpClient(use, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(12) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            var resp = await client.GetAsync(url, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            return (resp.IsSuccessStatusCode, resp, body, null);
        }
        catch (TaskCanceledException) { return (false, null, null, "请求超时（12 秒）"); }
        catch (Exception ex) { return (false, null, null, ex.Message); }
        finally { owned?.Dispose(); }
    }

    /// <summary>写日志。Core 层没有 LogBus（在 Neo.App），用事件让上层接。</summary>
    public static Action<string>? Logger { get; set; }
    private static void LogBus(string msg) { try { Logger?.Invoke(msg); } catch { } }

    public static SubscriptionFetchResult ParseContent(string body)
    {
        var text = body.Trim();
        if (text.Length == 0) return SubscriptionFetchResult.Fail("订阅内容为空");

        if (text.StartsWith("<") || text.Contains("<html", StringComparison.OrdinalIgnoreCase))
            return SubscriptionFetchResult.Fail("订阅返回的是网页而非节点内容（可能已失效/需要续费）");

        if (LooksLikeClashYaml(text))
        {
            var yamlNodes = ClashYamlParser.Parse(text);
            if (yamlNodes.Count == 0)
                return SubscriptionFetchResult.Fail("检测到 Clash YAML 格式，但未解析出可用节点（格式可能不受支持）");
            var yr = SubscriptionFetchResult.Ok(text);
            yr.Nodes.AddRange(yamlNodes);
            return yr;
        }

        string content = text;
        var compact = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        var decoded = ShareLinkParser.TryB64Decode(compact);
        if (decoded != null && decoded.Contains("://")) content = decoded;

        var result = SubscriptionFetchResult.Ok(content);
        var lines = content.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        var meta = lines.Where(l => !l.Contains("://")).Take(3).ToList();

        foreach (var line in lines)
        {
            var node = ShareLinkParser.Parse(line);
            if (node != null && node.IsValid) result.Nodes.Add(node);
        }

        var kept = new List<NodeItem>();
        foreach (var n in result.Nodes)
        {
            if (IsMetaName(n.Name)) meta.Add(n.Name.Trim());
            else kept.Add(n);
        }
        if (kept.Count > 0 && kept.Count != result.Nodes.Count)
        {
            result.Nodes.Clear();
            result.Nodes.AddRange(kept);
        }
        if (meta.Count > 0) result.TrafficInfo = string.Join(" | ", meta);

        if (result.Nodes.Count == 0)
            return SubscriptionFetchResult.Fail("未解析出任何有效节点（格式可能不支持）");

        return result;
    }

    private static bool LooksLikeClashYaml(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var l = raw.TrimEnd();
            if (l.StartsWith("proxies:", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static readonly System.Text.RegularExpressions.Regex MetaNameRe = new(
        @"^\s*(剩余流量|已用流量|已用|套餐到期|到期时间|到期|距离下次重置|重置时间|官网|订阅地址)\s*[：:]",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool IsMetaName(string name) =>
        !string.IsNullOrWhiteSpace(name) && MetaNameRe.IsMatch(name.Trim());

    private const string UsageProbeUserAgent = "ClashforWindows/0.20.39";
    private const string SingboxProbeUserAgent = "sing-box/1.14.2";

    private static void ParseUserInfo(HttpResponseMessage resp, SubscriptionFetchResult r)
    {
        try
        {
            if (!resp.Headers.TryGetValues("subscription-userinfo", out var vals)) return;
            var info = vals.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(info)) return;
            foreach (var part in info.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                if (kv.Length != 2 || !long.TryParse(kv[1].Trim(), out var v)) continue;
                switch (kv[0].Trim().ToLowerInvariant())
                {
                    case "upload": r.SubUpload = v; break;
                    case "download": r.SubDownload = v; break;
                    case "total": r.SubTotal = v; break;
                    case "expire": r.SubExpireUnix = v; break;
                }
            }
        }
        catch { }
    }

    /// 用不同 UA 探测订阅额度（subscription-userinfo 响应头）。
    /// 代理策略与主拉取一致：**直连优先**；若上层注入了代理兜底且直连整轮没拿到额度，
    /// 再用代理重试一轮（有些面板会按来源 IP 拒绝直连）。
    private static async Task FillUsageByProbeAsync(string url, SubscriptionFetchResult r, CancellationToken ct)
    {
        foreach (var ua in new[] { UsageProbeUserAgent, SingboxProbeUserAgent })
        {
            if (await TryUsageProbeAsync(url, ua, r, null, ct)) return;
        }

        if (ProxyFallbackFactory is { } factory)
        {
            HttpMessageHandler? ph = null;
            try { ph = factory(); } catch { }
            if (ph != null)
            {
                using (ph)
                {
                    foreach (var ua in new[] { UsageProbeUserAgent, SingboxProbeUserAgent })
                    {
                        if (await TryUsageProbeAsync(url, ua, r, ph, ct)) return;
                    }
                }
            }
        }
    }

    /// <summary>单次额度探测。拿到 subscription-userinfo 头返回 true。</summary>
    private static async Task<bool> TryUsageProbeAsync(
        string url, string ua, SubscriptionFetchResult r, HttpMessageHandler? handler, CancellationToken ct)
    {
        try
        {
            HttpClientHandler? owned = null;
            HttpMessageHandler use = handler ?? (owned = new HttpClientHandler { Proxy = null, UseProxy = false });
            try
            {
                using var client = new HttpClient(use, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ua);
                using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode) return false;
                if (resp.Headers.TryGetValues("subscription-userinfo", out var vals) &&
                    vals.Any(v => !string.IsNullOrWhiteSpace(v)))
                {
                    ParseUserInfo(resp, r);
                    return true;
                }
            }
            finally { owned?.Dispose(); }
        }
        catch { }
        return false;
    }
}
