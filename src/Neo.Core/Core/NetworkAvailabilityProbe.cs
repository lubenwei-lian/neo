using System.Diagnostics;
using System.Net;

namespace Neo.Core.Core;

public sealed record NetworkProbeAttempt(string Target, bool Available, long ElapsedMs, string Detail);
public sealed record NetworkRouteResult(string Route, IReadOnlyList<NetworkProbeAttempt> Attempts)
{
    public bool Available => Attempts.Any(x => x.Available);
    public long ElapsedMs => Attempts.FirstOrDefault(x => x.Available)?.ElapsedMs ?? 0;
}
public sealed record NetworkAvailabilityResult(IReadOnlyList<NetworkRouteResult> Routes)
{
    public bool Available => Routes.Any(x => x.Available);
    public bool AllAvailable => Routes.Count > 0 && Routes.All(x => x.Available);
}

/// <summary>
/// 检测真实 HTTPS 转发，不把监听端口打开、HTTP 重定向或登录页当成外网可用。
/// 每条路径先请求一个轻量 204 目标，失败才使用其他服务商；连接池随会话复用。
/// TUN 走系统路由，普通模式分别检测应用对外提供的 HTTP 和 SOCKS 端口。
/// </summary>
public sealed class NetworkAvailabilityProbe : IDisposable
{
    private static readonly Uri[] DefaultTargets =
    [
        new("https://www.gstatic.com/generate_204"),
        new("https://cp.cloudflare.com/generate_204"),
        new("https://www.google.com/generate_204"),
    ];
    private readonly (string Name, HttpClient Client)[] _routes;
    private readonly IReadOnlyList<Uri> _targets;
    private readonly TimeSpan _attemptTimeout;
    private readonly TimeSpan _totalTimeout;

    public NetworkAvailabilityProbe(int httpPort, int socksPort, bool tun)
        : this(tun
            ? [("TUN 系统路由", CreateHandler(null))]
            : [("HTTP/HTTPS", CreateHandler(new Uri($"http://127.0.0.1:{httpPort}"))),
               ("SOCKS5", CreateHandler(new Uri($"socks5://127.0.0.1:{socksPort}")))]) { }

    // 传输边界可注入以验证超时、重定向、取消与回退；生产使用上面的固定 HTTPS 目标。
    public NetworkAvailabilityProbe((string Name, HttpMessageHandler Handler)[] routes,
        IReadOnlyList<Uri>? targets = null, TimeSpan? attemptTimeout = null, TimeSpan? totalTimeout = null)
    {
        if (routes.Length == 0) throw new ArgumentException("至少需要一条检测路径", nameof(routes));
        _targets = targets ?? DefaultTargets;
        if (_targets.Count == 0 || _targets.Any(x => x.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("检测目标必须为 HTTPS", nameof(targets));
        _attemptTimeout = attemptTimeout ?? TimeSpan.FromSeconds(3);
        _totalTimeout = totalTimeout ?? TimeSpan.FromSeconds(8);
        if (_attemptTimeout <= TimeSpan.Zero || _totalTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(attemptTimeout));
        _routes = routes.Select(x => (x.Name, new HttpClient(x.Handler) { Timeout = Timeout.InfiniteTimeSpan })).ToArray();
    }

    private static SocketsHttpHandler CreateHandler(Uri? proxy) => new()
    {
        UseProxy = proxy != null,
        Proxy = proxy == null ? null : new WebProxy(proxy),
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(3),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(45),
        MaxConnectionsPerServer = 1,
        MaxResponseHeadersLength = 16,
    };

    public async Task<NetworkAvailabilityResult> CheckAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var results = await Task.WhenAll(_routes.Select(x => CheckRouteAsync(x.Name, x.Client, ct)))
            .ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return new(results);
    }

    private async Task<NetworkRouteResult> CheckRouteAsync(string name, HttpClient client, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(_totalTimeout);
        var attempts = new List<NetworkProbeAttempt>();
        foreach (var target in _targets)
        {
            ct.ThrowIfCancellationRequested();
            if (budget.IsCancellationRequested) break;
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
            attempt.CancelAfter(_attemptTimeout);
            var sw = Stopwatch.StartNew();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, target);
                request.Headers.CacheControl = new() { NoCache = true, NoStore = true };
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attempt.Token)
                    .ConfigureAwait(false);
                var ok = response.StatusCode == HttpStatusCode.NoContent
                    && response.Content.Headers.ContentLength is null or 0;
                attempts.Add(new(target.Host, ok, sw.ElapsedMilliseconds,
                    ok ? "HTTPS 204" : $"非预期响应 HTTP {(int)response.StatusCode}（可能受限或被重定向）"));
                if (ok) break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                attempts.Add(new(target.Host, false, sw.ElapsedMilliseconds, "检测超时"));
            }
            catch (HttpRequestException ex)
            {
                // 不记录节点凭据、代理 URL 或响应正文；错误分类足以用于诊断 TLS/连接问题。
                attempts.Add(new(target.Host, false, sw.ElapsedMilliseconds, $"请求失败：{ex.HttpRequestError}"));
            }
            catch (IOException)
            {
                attempts.Add(new(target.Host, false, sw.ElapsedMilliseconds, "连接提前关闭"));
            }
        }
        ct.ThrowIfCancellationRequested();
        return new(name, attempts.AsReadOnly());
    }

    public void Dispose()
    {
        foreach (var route in _routes) route.Client.Dispose();
    }
}
