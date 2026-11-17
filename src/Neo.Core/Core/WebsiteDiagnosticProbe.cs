using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;

namespace Neo.Core.Core;

public static class WebsiteDiagnosticProbe
{
    internal sealed record AttemptOutcome(WebsiteDiagnosticResult Result, bool Retryable);

    public static Task<WebsiteDiagnosticResult> CheckAsync(NetworkDiagnosticTarget target,
        KernelRouteContext context, bool tun, CancellationToken ct = default)
        => CheckWithRetriesAsync(target, token => CheckOnceAsync(target, context, tun, token), ct);

    // Each retry uses a fresh socket to the SAME URL and current rules. Never substitute a different
    // Google host, weaken certificate checks, or convert an HTTP restriction into a healthy result.
    internal static async Task<WebsiteDiagnosticResult> CheckWithRetriesAsync(NetworkDiagnosticTarget target,
        Func<CancellationToken, Task<AttemptOutcome>> probe, CancellationToken ct = default,
        TimeSpan? totalBudget = null)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(totalBudget ?? TimeSpan.FromSeconds(10));
        var total = Stopwatch.StartNew();
        var attempts = new List<WebsiteDiagnosticAttempt>(3);
        WebsiteDiagnosticResult? last = null;
        for (var number = 1; number <= 3; number++)
        {
            ct.ThrowIfCancellationRequested();
            var watch = Stopwatch.StartNew();
            AttemptOutcome outcome;
            try { outcome = await probe(budget.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && budget.IsCancellationRequested)
            {
                outcome = new(new(target, "失败", null, watch.ElapsedMilliseconds,
                    NetworkRouteEvidence.Unknown("本轮检测预算用尽，未取得本次出口证据"), "该网址的总检测时间已用尽（含复测）"), false);
            }
            ct.ThrowIfCancellationRequested();
            last = outcome.Result;
            attempts.Add(new(number, last.State, last.HttpStatus, last.ElapsedMs, last.Route, last.Detail));
            if (!outcome.Retryable || number == 3 || budget.IsCancellationRequested) break;
            try { await Task.Delay(200, budget.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
        }
        ct.ThrowIfCancellationRequested();
        var detail = last!.Detail;
        if (attempts.Count > 1)
        {
            detail = (last.State == "可达" ? "复测恢复（连接有波动），不代表持续稳定。" : "已进行有限复测；结果仅对应列出的同一网址。")
                + "\n" + string.Join("\n", attempts.Select(x => $"第 {x.Number} 次：{x.State}"
                    + (x.HttpStatus is { } status ? $" · HTTP {status}" : "") + $" · {x.ElapsedMs} ms · {x.Detail}"
                    + $" · 出口：{x.Route.Path} / {x.Route.Outbound}（{x.Route.Source}）"));
        }
        if (last.State == "失败") detail += "\n本轮检测未成功，不等于浏览器一定无法访问；请结合每次请求和出口证据判断。";
        return last with { ElapsedMs = total.ElapsedMilliseconds, Detail = detail, Attempts = attempts.AsReadOnly() };
    }

    private static async Task<AttemptOutcome> CheckOnceAsync(NetworkDiagnosticTarget target,
        KernelRouteContext context, bool tun, CancellationToken ct)
    {
        var source = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new SocketsHttpHandler
        {
            UseProxy = !tun,
            Proxy = tun ? null : new WebProxy($"http://127.0.0.1:{context.HttpPort}"),
            AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(5),
            ConnectCallback = async (connection, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(connection.DnsEndPoint, token).ConfigureAwait(false);
                    source.TrySetResult(((IPEndPoint)socket.LocalEndPoint!).Port);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            },
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        var watch = Stopwatch.StartNew();
        using var request = new HttpRequestMessage(HttpMethod.Get, target.Url);
        request.Headers.CacheControl = new() { NoCache = true, NoStore = true };
        request.Headers.UserAgent.ParseAdd("Neo-Network-Diagnostics/0.2.8");
        var send = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var evidence = ObserveRouteAsync(context, target.Url, source.Task, send, ct);
        int? status = null; var state = "失败"; var detail = "";
        var retryable = false;
        HttpResponseMessage? response = null;
        try
        {
            try
            {
                response = await send.ConfigureAwait(false);
                watch.Stop();
                status = (int)response.StatusCode;
                (state, detail) = Classify(target, status.Value);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { watch.Stop(); retryable = true; detail = "请求超时（8 秒请求预算，未收到完整响应头）"; }
            catch (HttpRequestException ex)
            {
                watch.Stop(); retryable = IsRetryable(ex);
                detail = ex.HttpRequestError == HttpRequestError.SecureConnectionError
                    ? HasInner<IOException>(ex) && !HasInner<AuthenticationException>(ex)
                        ? "TLS 握手时连接提前关闭或传输中断（SecureConnectionError）"
                        : "TLS 握手或证书验证失败（SecureConnectionError）；保留系统证书校验"
                    : $"HTTPS 请求失败：{ex.HttpRequestError}";
            }
            catch (IOException) { watch.Stop(); retryable = true; detail = "连接提前关闭或传输中断"; }
            NetworkRouteEvidence route;
            try { route = await evidence.ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { route = NetworkRouteEvidence.Unknown("路由观测超时，未取得可靠出口证据"); }
            ct.ThrowIfCancellationRequested();
            return new(new(target, state, status, watch.ElapsedMilliseconds, route, detail), retryable);
        }
        finally
        {
            // Cancellation must also join/observe the route task before its source/handler is disposed.
            try { await evidence.ConfigureAwait(false); } catch { }
            response?.Dispose();
        }
    }

    internal static bool IsRetryable(HttpRequestException error) => error.HttpRequestError switch
    {
        HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.ResponseEnded => true,
        HttpRequestError.SecureConnectionError => HasInner<IOException>(error) && !HasInner<AuthenticationException>(error),
        _ => false,
    };

    private static bool HasInner<T>(Exception error) where T : Exception
    {
        for (Exception? current = error; current != null; current = current.InnerException)
            if (current is T) return true;
        return false;
    }

    internal static async Task<NetworkRouteEvidence> ObserveRouteAsync(KernelRouteContext context, Uri target,
        Task<int> source, Task send, CancellationToken ct)
    {
        await Task.WhenAny(source, send).ConfigureAwait(false);
        if (context.Kind == "xray") return await context.QueryXrayAsync(target,
            source.IsCompletedSuccessfully ? source.Result : 0, ct).ConfigureAwait(false);
        if (!source.IsCompletedSuccessfully) return NetworkRouteEvidence.Unknown("未建立连接，无法关联实际出口");
        // Keep the response/connection alive until this observer has completed. No access logging is enabled globally.
        for (var i = 0; i < 12; i++)
        {
            var result = await context.ObserveClashAsync(target, source.Result, ct).ConfigureAwait(false);
            if (result != null) return result;
            if (send.IsCompleted && i >= 2) break;
            await Task.Delay(80, ct).ConfigureAwait(false);
        }
        return NetworkRouteEvidence.Unknown("连接列表未保留该请求的匹配记录；没有根据站点名称猜测出口");
    }

    public static (string State, string Detail) Classify(NetworkDiagnosticTarget target, int status)
    {
        if (target.Requires204) return status == 204 ? ("可达", "HTTPS 204 检测成功")
            : ("响应异常", $"预期 HTTP 204，实际 {status}；不能据此判定网络正常");
        if (status is >= 200 and < 300) return ("可达", "已收到站点 HTTPS 响应头（未下载完整网页）");
        if (status is >= 300 and < 400) return ("可达", "已收到 HTTPS 重定向；未跟随跳转，结果仅对应列出的网址");
        if (status is 401 or 403 or 429) return ("站点限制", "已完成 TLS 并收到站点响应，可能受登录、风控或频率限制；不等于整条网络失联");
        return ("站点异常", "已收到 HTTPS 响应，但该站点返回错误状态");
    }
}
