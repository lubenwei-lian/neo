using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Neo.Core.Core;

internal static class ProbeChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test, Action<bool, string> check, string output)
    {
        var target = NetworkDiagnosticTargets.All[0];
        var proxy = new NetworkRouteEvidence("代理", "proxy", "synthetic boundary", "not a live route");
        WebsiteDiagnosticProbe.AttemptOutcome Result(int number, bool success, NetworkRouteEvidence? route = null) =>
            new(new(target, success ? "可达" : "失败", success ? 204 : null, 12,
                route ?? proxy, success ? "HTTPS 204" : $"synthetic EOF {number}"), !success);

        await test("transient recovery preserves every attempt, same target and route evidence", async () =>
        {
            var calls = 0;
            var result = await WebsiteDiagnosticProbe.CheckWithRetriesAsync(target,
                ct => Task.FromResult(Result(++calls, calls == 3)));
            check(calls == 3 && result.Target == target && result.HttpStatus == 204 && result.RecoveredAfterRetry
                && result.Attempts.Select(x => x.Number).SequenceEqual(new[] { 1, 2, 3 })
                && result.Attempts.Take(2).All(x => x.HttpStatus == null)
                && result.Detail.Contains("synthetic EOF 1") && result.Detail.Contains("有波动")
                && result.Attempts.All(x => x.Route == proxy), "recovery hid failures or changed identity/evidence");
        });
        await test("exhausted retries stay failed and do not borrow an earlier route", async () =>
        {
            var calls = 0; var unknown = NetworkRouteEvidence.Unknown("synthetic missing final observation");
            var result = await WebsiteDiagnosticProbe.CheckWithRetriesAsync(target,
                ct => Task.FromResult(Result(++calls, false, calls == 3 ? unknown : proxy)));
            check(calls == 3 && result.State == "失败" && !result.RecoveredAfterRetry && result.Attempts.Count == 3
                && result.Route == unknown && result.HttpStatus == null && result.Detail.Contains("不等于浏览器"),
                "retry cap/final failure/route evidence incorrect");
        });
        await test("first successful response is not retried", async () =>
        {
            var calls = 0;
            var result = await WebsiteDiagnosticProbe.CheckWithRetriesAsync(target, ct => Task.FromResult(Result(++calls, true)));
            check(calls == 1 && result.Attempts.Count == 1 && !result.RecoveredAfterRetry, "healthy site made extra requests");
        });
        await test("TLS EOF is retryable while authentication and certificate failures remain rejected", () =>
        {
            check(WebsiteDiagnosticProbe.IsRetryable(new(HttpRequestError.SecureConnectionError, "TLS", new IOException("EOF"))), "TLS EOF not retryable");
            check(!WebsiteDiagnosticProbe.IsRetryable(new(HttpRequestError.SecureConnectionError, "TLS", new AuthenticationException("certificate")))
                && !WebsiteDiagnosticProbe.IsRetryable(new(HttpRequestError.SecureConnectionError, "TLS", new IOException("transport", new AuthenticationException("certificate"))))
                && !WebsiteDiagnosticProbe.IsRetryable(new(HttpRequestError.ProxyTunnelError, "proxy authentication")), "security or proxy errors treated as transient EOF");
            return Task.CompletedTask;
        });
        await test("pre-cancelled diagnostic makes no attempt", async () =>
        {
            using var cts = new CancellationTokenSource(); cts.Cancel(); var calls = 0;
            try { await WebsiteDiagnosticProbe.CheckWithRetriesAsync(target, ct => Task.FromResult(Result(++calls, true)), cts.Token); throw new Exception("cancel ignored"); }
            catch (OperationCanceledException) { }
            check(calls == 0, "cancelled request reached transport");
        });
        await test("cancel during retry delay does not publish recovery or start another attempt", async () =>
        {
            using var cts = new CancellationTokenSource(); var calls = 0;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var running = WebsiteDiagnosticProbe.CheckWithRetriesAsync(target, ct =>
            { calls++; entered.TrySetResult(); return Task.FromResult(Result(calls, false)); }, cts.Token);
            await entered.Task; cts.Cancel();
            try { await running; throw new Exception("cancel became failure result"); } catch (OperationCanceledException) { }
            check(calls == 1, "retry continued after cancellation");
        });
        await test("attempts share one total deadline and retain the timed out final attempt", async () =>
        {
            var calls = 0; var watch = Stopwatch.StartNew();
            var result = await WebsiteDiagnosticProbe.CheckWithRetriesAsync(target, async ct =>
            {
                if (++calls == 1) return Result(calls, false);
                await Task.Delay(Timeout.Infinite, ct); return Result(calls, true);
            }, totalBudget: TimeSpan.FromMilliseconds(450));
            check(calls == 2 && result.State == "失败" && result.Attempts.Count == 2 && result.Route.Path == "未确认"
                && result.Attempts[1].Detail.Contains("总检测时间") && watch.Elapsed < TimeSpan.FromSeconds(2), "budget reset or evidence invented");
        });

        KernelRouteContext Context(int port)
        {
            var path = Path.Combine(output, "probe-synthetic-config.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new {
                inbounds = new[] { new { tag = "http-in", protocol = "http", port } },
                outbounds = new[] { new { tag = "direct", protocol = "freedom" } }
            }));
            return KernelRouteContext.Load(new(1, "xray", path, false, "rule"));
        }
        await test("real transport does not retry HTTP restrictions, server errors or wrong 204 responses", async () =>
        {
            foreach (var (status, requires204, expected) in new[] { (403, false, "站点限制"), (429, false, "站点限制"), (503, false, "站点异常"), (200, true, "响应异常") })
            {
                var calls = 0;
                await using var server = new Server(async (peer, ct) => {
                    await ReadHeaders(peer.GetStream(), ct); Interlocked.Increment(ref calls);
                    await peer.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} fixture\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), ct);
                });
                var result = await WebsiteDiagnosticProbe.CheckAsync(new("local fixture", "test", new($"http://127.0.0.1:{server.Port}/"), requires204), Context(server.Port), true);
                check(calls == 1 && result.Attempts.Count == 1 && result.State == expected && result.HttpStatus == status, "HTTP response was retried or marked healthy");
            }
        });
        await test("real TLS EOF performs at most three fresh same-host connections", async () =>
        {
            var requests = new ConcurrentBag<string>(); var ports = new ConcurrentBag<int>();
            await using var server = new Server(async (peer, ct) => {
                requests.Add(await ReadHeaders(peer.GetStream(), ct)); ports.Add(((IPEndPoint)peer.Client.RemoteEndPoint!).Port);
                await peer.GetStream().WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), ct);
                var bytes = new byte[4096]; var received = await peer.GetStream().ReadAsync(bytes, ct);
                if (received == 0) throw new EndOfStreamException(); // Close during TLS handshake.
            });
            var result = await WebsiteDiagnosticProbe.CheckAsync(target, Context(server.Port), false);
            check(result.State == "失败" && result.Attempts.Count == 3 && requests.Count == 3 && ports.Distinct().Count() == 3
                && requests.All(x => x.StartsWith("CONNECT www.google.com:443 ", StringComparison.Ordinal))
                && result.Attempts.All(x => x.Detail.Contains("TLS")), "EOF retry changed host, reused failed socket or lost failure");
        });
        await test("real untrusted HTTPS certificate remains rejected without retries or trust changes", async () =>
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder(); san.AddIpAddress(IPAddress.Loopback); request.CertificateExtensions.Add(san.Build());
            using var generated = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
            // Match the existing Windows TLS fixture: usable Schannel key handle, no trust-store import.
            using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
            var calls = 0;
            await using var server = new Server(async (peer, ct) => {
                Interlocked.Increment(ref calls); using var ssl = new SslStream(peer.GetStream());
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, ct);
                await ReadHeaders(ssl, ct); await ssl.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\n\r\n"), ct);
            });
            var result = await WebsiteDiagnosticProbe.CheckAsync(new("untrusted fixture", "test", new($"https://127.0.0.1:{server.Port}/"), true), Context(server.Port), true);
            check(calls == 1 && result.State == "失败" && result.Attempts.Count == 1 && result.Detail.Contains("证书"), "invalid certificate accepted or retried: " + JsonSerializer.Serialize(new { calls, result, serverErrors = server.Errors.Select(x => x.ToString()) }));
        });
        await test("real headers-only success does not wait for page body", async () =>
        {
            await using var server = new Server(async (peer, ct) => {
                await ReadHeaders(peer.GetStream(), ct);
                await peer.GetStream().WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 999999\r\n\r\n"), ct);
                await Task.Delay(Timeout.Infinite, ct);
            });
            var result = await WebsiteDiagnosticProbe.CheckAsync(new("headers fixture", "test", new($"http://127.0.0.1:{server.Port}/")), Context(server.Port), true).WaitAsync(TimeSpan.FromSeconds(2));
            check(result.State == "可达" && result.Attempts.Count == 1, "headers-only response consumed page body or retried");
        });
    }

    private static async Task<string> ReadHeaders(Stream stream, CancellationToken ct)
    {
        var bytes = new List<byte>(); var next = new byte[1];
        while (bytes.Count < 8192)
        {
            if (await stream.ReadAsync(next, ct) == 0) throw new EndOfStreamException();
            bytes.Add(next[0]);
            if (bytes.Count >= 4 && bytes.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) return Encoding.ASCII.GetString(bytes.ToArray());
        }
        throw new InvalidDataException("fixture header too large");
    }

    private sealed class Server : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentBag<Task> _clients = [];
        private readonly Task _accept;
        public ConcurrentBag<Exception> Errors { get; } = [];
        public int Port { get; }
        public Server(Func<TcpClient, CancellationToken, Task> handle)
        {
            _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _accept = Task.Run(async () => {
                try
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        var peer = await _listener.AcceptTcpClientAsync(_cts.Token);
                        _clients.Add(Task.Run(async () => { using (peer) { try { await handle(peer, _cts.Token); } catch (Exception ex) { Errors.Add(ex); } } }));
                    }
                }
                catch (OperationCanceledException) { }
                catch (SocketException) when (_cts.IsCancellationRequested) { }
            });
        }
        public async ValueTask DisposeAsync()
        {
            _cts.Cancel(); _listener.Stop(); await _accept;
            await Task.WhenAll(_clients).WaitAsync(TimeSpan.FromSeconds(3)); _cts.Dispose();
        }
    }
}
