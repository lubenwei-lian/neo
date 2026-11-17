using System.Net;
using Neo.Core.Core;

internal static class ExitBatchChecks
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> test, Action<bool, string> check)
    {
        await test("batch exit uses HTTPS and falls back after a rate-limited trace service", async () =>
        {
            var calls = new List<Uri>();
            using var transport = new Transport((r, _) =>
            {
                calls.Add(r.RequestUri!);
                check(r.Headers.CacheControl is { NoCache: true, NoStore: true }, "trace may use stale cache");
                return Task.FromResult(calls.Count == 1 ? Reply(429, "rate limited") : Reply(200, "loc=SG\nip=8.8.8.8\n"));
            });
            var result = await RealDelayTester.QueryExitCountryViaSocksAsync(1, 2000, default, transport);
            check(result.Country == "sg" && result.Ip == "8.8.8.8" && result.Detail == "", "fallback did not return observed exit");
            check(calls.Count == 2 && calls.All(u => u.Scheme == "https") && calls.Select(u => u.Host).Distinct().Count() == 2, "trace fallback targets incorrect");
        });
        await test("batch exit retains HTTP failure reasons without fabricating an exit", async () =>
        {
            using var transport = new Transport((_, _) => Task.FromResult(Reply(403, "forbidden")));
            var result = await RealDelayTester.QueryExitCountryViaSocksAsync(1, 2000, default, transport);
            check(result.Country == "" && result.Ip == "" && result.Detail.Contains("HTTP 403"), "HTTP restriction hidden or treated as success");
        });
        await test("batch exit rejects private IPs and invalid region responses", async () =>
        {
            var i = 0;
            using var transport = new Transport((_, _) => Task.FromResult(Reply(200, ++i == 1 ? "loc=JP\nip=192.168.1.1" : "loc=XX\nip=8.8.8.8")));
            var result = await RealDelayTester.QueryExitCountryViaSocksAsync(1, 2000, default, transport);
            check(i == 2 && result.Country == "" && result.Ip == "" && result.Detail.Length > 0, "invalid trace accepted");
        });
        await test("batch exit retries the alternate target after a timed-out first request", async () =>
        {
            var i = 0;
            using var transport = new Transport(async (_, ct) =>
            {
                if (++i == 1) await Task.Delay(Timeout.Infinite, ct);
                return Reply(200, "loc=JP\nip=1.1.1.1");
            });
            var result = await RealDelayTester.QueryExitCountryViaSocksAsync(1, 1000, default, transport);
            check(i == 2 && result.Country == "jp", "timeout consumed all fallback time");
        });
        await test("batch exit cancellation propagates instead of caching a failed measurement", async () =>
        {
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            using var transport = new Transport((_, _) => throw new InvalidOperationException("cancelled transport must not run"));
            try { await RealDelayTester.QueryExitCountryViaSocksAsync(1, 2000, cancel.Token, transport); }
            catch (OperationCanceledException) { return; }
            throw new InvalidOperationException("caller cancellation was swallowed");
        });
        await test("batch exit reports proxy transport failures rather than empty success", async () =>
        {
            using var transport = new Transport((_, _) => throw new HttpRequestException(HttpRequestError.ProxyTunnelError, "fixture"));
            var result = await RealDelayTester.QueryExitCountryViaSocksAsync(1, 2000, default, transport);
            check(result.Country == "" && result.Detail.Contains("ProxyTunnelError"), "proxy failure reason missing");
        });
    }
    private static HttpResponseMessage Reply(int status, string text) => new((HttpStatusCode)status) { Content = new StringContent(text) };
    private sealed class Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
}
