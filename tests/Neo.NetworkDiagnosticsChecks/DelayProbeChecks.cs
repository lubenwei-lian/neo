using System.Diagnostics;
using System.Net;
using Neo.Core.Core;

internal static class DelayProbeChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test, Action<bool, string> check)
    {
        string[] sites = ["https://first.invalid/", "https://second.invalid/"];
        await test("displayed latency excludes cold setup and measures a warmed request", async () =>
        {
            var calls = 0;
            using var client = new HttpClient(new Handler(async (_, ct) =>
            {
                await Task.Delay(Interlocked.Increment(ref calls) == 1 ? 180 : 25, ct);
                return new(HttpStatusCode.NoContent);
            }));
            var wall = Stopwatch.StartNew();
            var ms = await DelayHttpProbe.MeasureAsync(client, sites[..1], 3, 1000, default, warmup: true);
            check(calls == 2 && ms >= 15 && ms < 130 && wall.ElapsedMilliseconds >= 195,
                "cold connection setup was reported as displayed node latency");
        });
        await test("a cold response alone never becomes a warmed latency sample", async () =>
        {
            var calls = 0;
            using var client = new HttpClient(new Handler(async (_, ct) =>
            {
                if (Interlocked.Increment(ref calls) > 1) await Task.Delay(Timeout.Infinite, ct);
                return new(HttpStatusCode.NoContent);
            }));
            var wall = Stopwatch.StartNew();
            var ms = await DelayHttpProbe.MeasureAsync(client, sites[..1], 3, 180, default, warmup: true);
            check(ms == -1 && calls == 2 && wall.ElapsedMilliseconds < 1500,
                "cold sample leaked into warmed latency or timeout restarted");
        });
        await test("warmed delay fallback retains one budget and cancels the losing request", async () =>
        {
            var primaryCanceled = false; var alternateCalls = 0;
            using var client = new HttpClient(new Handler(async (request, ct) =>
            {
                if (request.RequestUri!.Host == "first.invalid")
                {
                    try { await Task.Delay(Timeout.Infinite, ct); }
                    catch (OperationCanceledException) { primaryCanceled = true; throw; }
                }
                Interlocked.Increment(ref alternateCalls);
                return new(HttpStatusCode.NoContent);
            }));
            var ms = await DelayHttpProbe.MeasureAsync(client, sites, 3, 300, default, warmup: true);
            check(ms > 0 && alternateCalls == 2 && primaryCanceled,
                "alternate returned a cold sample or left the primary alive");
        });
        await test("warmed probe preserves certificate rejection without measuring again", async () =>
        {
            var calls = 0;
            using var client = new HttpClient(new Handler((_, _) => { calls++; throw new HttpRequestException("synthetic certificate rejection"); }));
            var ms = await DelayHttpProbe.MeasureAsync(client, sites, 3, 1000, default,
                () => true, warmup: true);
            check(ms == -1 && calls == 1, "certificate rejection was retried during warmup");
        });
        await test("budget exhaustion retains an already measured warm sample", async () =>
        {
            var calls = 0;
            using var client = new HttpClient(new Handler(async (_, ct) =>
            {
                var call = Interlocked.Increment(ref calls);
                if (call == 2) await Task.Delay(25, ct);
                if (call > 2) await Task.Delay(Timeout.Infinite, ct);
                return new(HttpStatusCode.NoContent);
            }));
            var ms = await DelayHttpProbe.MeasureAsync(client, sites[..1], 3, 150, default,
                warmup: true, stopAtMs: 1);
            check(ms >= 15 && ms < 130 && calls == 3, "valid warm sample was discarded by a later timeout");
        });
        await test("delay probe returns first real response without an unmeasured warmup", async () =>
        {
            var calls = 0;
            using var client = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); }));
            var ms = await DelayHttpProbe.MeasureAsync(client, sites, 3, 1000, default);
            check(ms > 0 && calls == 1, "successful node issued extra warmup or measurement requests");
        });
        await test("a stalled first delay target preserves time for the alternate target", async () =>
        {
            var first = 0; var second = 0; var watch = Stopwatch.StartNew();
            using var client = new HttpClient(new Handler(async (request, ct) =>
            {
                if (request.RequestUri!.Host == "first.invalid") { first++; await Task.Delay(Timeout.Infinite, ct); }
                second++; return new HttpResponseMessage(HttpStatusCode.NoContent);
            }));
            var ms = await DelayHttpProbe.MeasureAsync(client, sites, 3, 400, default);
            check(ms > 0 && first == 1 && second == 1 && watch.ElapsedMilliseconds < 2000, "first site consumed fallback budget");
        });
        await test("dead delay targets share one total budget rather than multiplying retries", async () =>
        {
            var calls = 0; var watch = Stopwatch.StartNew();
            using var client = new HttpClient(new Handler(async (_, ct) => { calls++; await Task.Delay(Timeout.Infinite, ct); return new(HttpStatusCode.NoContent); }));
            var ms = await DelayHttpProbe.MeasureAsync(client, sites, 3, 240, default);
            check(ms == -1 && calls == 2 && watch.ElapsedMilliseconds < 2000, "per-request timeout multiplied total duration");
        });
        await test("starting the alternate does not cancel a slow but viable primary", async () =>
        {
            var alternate = 0; var alternateCanceled = false;
            using var client = new HttpClient(new Handler(async (request, ct) =>
            {
                if (request.RequestUri!.Host == "first.invalid")
                {
                    await Task.Delay(190, ct); return new(HttpStatusCode.NoContent);
                }
                alternate++;
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { alternateCanceled = true; throw; }
                return new(HttpStatusCode.NoContent);
            }));
            var ms = await DelayHttpProbe.MeasureAsync(client, sites, 1, 320, default);
            check(ms > 0 && alternate == 1 && alternateCanceled, "short target split killed a viable primary or leaked the losing request");
        });
        await test("delay probe propagates user cancellation immediately", async () =>
        {
            using var cancel = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var client = new HttpClient(new Handler(async (_, ct) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return new(HttpStatusCode.NoContent); }));
            var task = DelayHttpProbe.MeasureAsync(client, sites, 3, 15000, cancel.Token);
            await entered.Task; cancel.Cancel();
            try { await task.WaitAsync(TimeSpan.FromSeconds(2)); throw new InvalidOperationException("user cancellation became a failed node"); }
            catch (OperationCanceledException) { }
        });
        await test("delay certificate rejection never retries with weaker security", async () =>
        {
            var calls = 0;
            using var client = new HttpClient(new Handler((_, _) => { calls++; throw new HttpRequestException("synthetic certificate rejection"); }));
            var ms = await DelayHttpProbe.MeasureAsync(client, sites, 3, 1000, default, () => true);
            check(ms == -1 && calls == 1, "certificate failure retried or was accepted");
        });
        await test("delay probe rejects error pages and checks the alternate site", async () =>
        {
            var calls = 0;
            using var client = new HttpClient(new Handler((request, _) => { calls++; return Task.FromResult(new HttpResponseMessage(request.RequestUri!.Host == "first.invalid" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NoContent)); }));
            var ms = await DelayHttpProbe.MeasureAsync(client, sites, 1, 1000, default);
            check(ms > 0 && calls == 2, "HTTP failure page was mistaken for usable proxy traffic");
        });
        await test("availability probes retain their explicit redirect status contract", async () =>
        {
            using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found))));
            check(await DelayHttpProbe.MeasureAsync(client, sites[..1], 1, 1000, default) == -1, "default delay status contract changed");
            check(await DelayHttpProbe.MeasureAsync(client, sites[..1], 1, 1000, default, acceptRedirects: true) > 0, "explicit availability redirect contract lost");
        });
        await test("exhausted delay budget performs no additional requests", async () =>
        {
            var calls = 0;
            using var client = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); }));
            check(await DelayHttpProbe.MeasureAsync(client, sites, 3, 0, default) == -1 && calls == 0, "expired startup budget still made network requests");
        });
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
