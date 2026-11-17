using System.Diagnostics;
using System.Net;
using System.Reflection;
using Neo.App.Services;
using Neo.Core.Core;

namespace Neo.App.Tests;

internal static class NetworkHealthChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test, Action<bool, string> check)
    {
        var targets = new[] { new Uri("https://first.invalid/generate_204"), new Uri("https://second.invalid/generate_204") };
        NetworkAvailabilityProbe Probe(NetworkProbeHandler handler, int attemptMs = 80, int totalMs = 200) =>
            new([("synthetic HTTPS", handler)], targets, TimeSpan.FromMilliseconds(attemptMs), TimeSpan.FromMilliseconds(totalMs));
        Task AutomaticCheck() => (Task)typeof(FailoverService)
            .GetMethod("CheckOnceAsync", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [CancellationToken.None])!;
        void Prepare(NetworkAvailabilityProbe probe, bool effective = true, bool tun = false)
        {
            NetworkHealthService.Reset();
            ConnectionService.IsConnected = true;
            ConnectionService.SessionEpoch++;
            ConnectionService.IsTunSession = tun;
            AppState.State = new() { FailoverEnabled = false, AutoRepairSystemProxy = false };
            SystemProxy.IsEnabled = effective;
            SystemProxy.EffectiveOverride = effective;
            SystemProxy.CurrentServer = null;
            ConnectionService.Notices.Clear();
            NetworkHealthService.BeginSession(ConnectionService.SessionEpoch, probe);
        }
        await test("HTTPS 204 succeeds without unnecessary fallback", async () =>
        {
            var h = new NetworkProbeHandler();
            using var probe = Probe(h);
            var r = await probe.CheckAsync();
            check(r.AllAvailable && h.Requests.Count == 1, "successful target caused extra probes");
        });
        await test("HTTP redirect and portal page never count as available", async () =>
        {
            var h = new NetworkProbeHandler { Respond = (q, _) => Task.FromResult(new HttpResponseMessage(
                q.RequestUri!.Host == "first.invalid" ? HttpStatusCode.Redirect : HttpStatusCode.OK)) };
            using var probe = Probe(h);
            var r = await probe.CheckAsync();
            check(!r.Available && h.Requests.Count == 2, "portal/redirect was treated as healthy");
        });
        await test("unexpected 204 body length cannot yield healthy", async () =>
        {
            var h = new NetworkProbeHandler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)
                { Content = new StringContent("portal") }) };
            using var probe = Probe(h);
            check(!(await probe.CheckAsync()).Available, "nonempty 204 accepted");
        });
        await test("provider fallback accepts second HTTPS target", async () =>
        {
            var h = new NetworkProbeHandler { Respond = (q, _) => Task.FromResult(new HttpResponseMessage(
                q.RequestUri!.Host == "first.invalid" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NoContent)) };
            using var probe = Probe(h);
            var r = await probe.CheckAsync();
            check(r.AllAvailable && r.Routes[0].Attempts.Count == 2, "fallback was not exercised");
        });
        await test("TLS failure is classified and cannot bypass certificate checking", async () =>
        {
            var h = new NetworkProbeHandler { Respond = (_, _) => throw new HttpRequestException(HttpRequestError.SecureConnectionError, "synthetic TLS failure") };
            using var probe = Probe(h);
            var r = await probe.CheckAsync();
            check(!r.Available && r.Routes[0].Attempts.All(a => a.Detail.Contains("SecureConnectionError")), "TLS failure lost");
        });
        await test("per-target timeout falls back within total budget", async () =>
        {
            var h = new NetworkProbeHandler { Respond = async (q, ct) =>
            {
                if (q.RequestUri!.Host == "first.invalid") await Task.Delay(Timeout.Infinite, ct);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            } };
            using var probe = Probe(h);
            var r = await probe.CheckAsync();
            check(r.AllAvailable && r.Routes[0].Attempts[0].Detail.Contains("超时"), "timeout did not fall back");
        });
        await test("total timeout bounds all failed fallback requests", async () =>
        {
            var h = new NetworkProbeHandler { Respond = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new(HttpStatusCode.NoContent); } };
            using var probe = Probe(h, 1000, 120);
            var sw = Stopwatch.StartNew();
            var r = await probe.CheckAsync();
            check(!r.Available && h.Requests.Count == 1 && sw.ElapsedMilliseconds < 1500, "total timeout unbounded");
        });
        await test("pre-cancelled probe performs no network request", async () =>
        {
            var h = new NetworkProbeHandler();
            using var probe = Probe(h);
            try { await probe.CheckAsync(new CancellationToken(true)); throw new Exception("did not cancel"); }
            catch (OperationCanceledException) { }
            check(h.Requests.Count == 0, "cancelled probe made a request");
        });
        await test("partial HTTP/SOCKS failure is represented separately", async () =>
        {
            using var probe = new NetworkAvailabilityProbe([
                ("HTTP/HTTPS", new NetworkProbeHandler()),
                ("SOCKS5", new NetworkProbeHandler { Respond = (_, _) => throw new HttpRequestException("synthetic refused") })], targets);
            var r = await probe.CheckAsync();
            check(r.Available && !r.AllAvailable && r.Routes.Count == 2 && !r.Routes[1].Available, "partial route failure hidden");
        });
        await test("probe clients remain reusable for periodic checks", async () =>
        {
            var h = new NetworkProbeHandler();
            using var probe = Probe(h);
            check((await probe.CheckAsync()).AllAvailable && (await probe.CheckAsync()).AllAvailable
                && h.Requests.Count == 2, "periodic client lifetime broken");
        });
        await test("plaintext target configuration is rejected", () =>
        {
            var h = new NetworkProbeHandler();
            try { using var p = new NetworkAvailabilityProbe([("test", h)], [new Uri("http://portal.invalid")]); throw new Exception("plaintext accepted"); }
            catch (ArgumentException) { h.Dispose(); }
            return Task.CompletedTask;
        });
        await test("network checks run with automatic switching disabled", async () =>
        {
            var h = new NetworkProbeHandler();
            Prepare(Probe(h));
            var epoch = ConnectionService.SessionEpoch;
            await AutomaticCheck();
            var snapshot = NetworkHealthService.Current;
            check(h.Requests.Count == 1 && snapshot.Status == NetworkHealthStatus.Available
                && snapshot.CheckedAt != null && !snapshot.IsChecking, "disabled switching disabled detection");
            check(ConnectionService.SessionEpoch == epoch && ConnectionService.IsConnected, "manual check altered connection");
        });
        await test("working local proxy does not conceal ineffective Windows settings", async () =>
        {
            Prepare(Probe(new()), effective: false);
            await FailoverService.CheckNetworkNowAsync();
            var snapshot = NetworkHealthService.Current;
            check(snapshot.Status == NetworkHealthStatus.Limited && snapshot.Summary.Contains("系统代理")
                && snapshot.Result!.Available, "Windows setting mismatch was labelled healthy");
        });
        await test("TUN result does not require Windows system proxy", async () =>
        {
            Prepare(Probe(new()), effective: false, tun: true);
            await FailoverService.CheckNetworkNowAsync();
            check(NetworkHealthService.Current.Status == NetworkHealthStatus.Available, "TUN incorrectly requires system proxy");
        });
        await test("consecutive failures warn without reconnecting or notification spam", async () =>
        {
            Prepare(Probe(new() { Respond = (_, _) => throw new HttpRequestException("synthetic offline") }));
            var epoch = ConnectionService.SessionEpoch;
            await FailoverService.CheckNetworkNowAsync();
            check(NetworkHealthService.Current.Status == NetworkHealthStatus.Limited && ConnectionService.Notices.Count == 0, "single failure overreacted");
            await FailoverService.CheckNetworkNowAsync();
            await FailoverService.CheckNetworkNowAsync();
            check(NetworkHealthService.Current.Status == NetworkHealthStatus.Unavailable && ConnectionService.Notices.Count == 1,
                "consecutive failure state or notification dedup broken");
            check(ConnectionService.IsConnected && epoch == ConnectionService.SessionEpoch, "disabled auto switching changed session");
        });
        await test("partial route failure never drives node failover", async () =>
        {
            Prepare(new NetworkAvailabilityProbe([("HTTP", new NetworkProbeHandler()),
                ("SOCKS", new NetworkProbeHandler { Respond = (_, _) => throw new HttpRequestException("synthetic") })], targets));
            AppState.State.FailoverEnabled = true;
            var epoch = ConnectionService.SessionEpoch;
            for (var n = 0; n < 5; n++) await AutomaticCheck();
            check(NetworkHealthService.Current.Status == NetworkHealthStatus.Limited
                && NetworkHealthService.Current.FailureStreak == 0 && ConnectionService.SessionEpoch == epoch,
                "partial route failure triggered switching");
        });
        await test("late cancelled result cannot overwrite successor session", async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Prepare(Probe(new() { Respond = async (_, _) => { entered.SetResult(); await release.Task; return new(HttpStatusCode.NoContent); } }, 3000, 4000));
            var oldEpoch = ConnectionService.SessionEpoch;
            var old = NetworkHealthService.CheckAsync(oldEpoch, CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Prepare(Probe(new()));
            await NetworkHealthService.CheckAsync(ConnectionService.SessionEpoch, CancellationToken.None);
            var successor = NetworkHealthService.Current;
            release.SetResult();
            check(await old == null && NetworkHealthService.Current == successor && successor.Epoch != oldEpoch,
                "old result overwrote successor");
        });
        await test("disconnect clears state and cancels an active probe", async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Prepare(Probe(new() { Respond = async (_, ct) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return new(HttpStatusCode.NoContent); } }));
            var active = NetworkHealthService.CheckAsync(ConnectionService.SessionEpoch, CancellationToken.None);
            await entered.Task;
            ConnectionService.IsConnected = false;
            NetworkHealthService.Reset();
            check(await active == null && NetworkHealthService.Current.Epoch == -1 && !NetworkHealthService.Current.IsChecking,
                "disconnected session retained health");
        });
        await test("cancelled manual detection releases busy flag for next check", async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var h = new NetworkProbeHandler { Respond = async (_, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return new(HttpStatusCode.NoContent); } };
            Prepare(Probe(h));
            using var cts = new CancellationTokenSource();
            var active = NetworkHealthService.CheckAsync(ConnectionService.SessionEpoch, cts.Token);
            await entered.Task;
            cts.Cancel();
            try { await active; throw new Exception("not cancelled"); } catch (OperationCanceledException) { }
            check(!NetworkHealthService.Current.IsChecking && NetworkHealthService.Current.FailureStreak == 0, "cancellation marked network failed");
            h.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            check((await NetworkHealthService.CheckAsync(ConnectionService.SessionEpoch, CancellationToken.None))!.AllAvailable,
                "next check remained blocked");
        });
        await test("queued check cannot run against a new session", async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Prepare(Probe(new() { Respond = async (_, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return new(HttpStatusCode.NoContent); } }));
            var first = FailoverService.CheckNetworkNowAsync();
            await entered.Task;
            var queued = FailoverService.CheckNetworkNowAsync();
            var h = new NetworkProbeHandler();
            Prepare(Probe(h));
            await Task.WhenAll(first, queued).WaitAsync(TimeSpan.FromSeconds(2));
            check(h.Requests.Count == 0 && NetworkHealthService.Current.CheckedAt == null, "queued old-session check probed successor");
        });
        await test("broken subscriber cannot stop network detection", async () =>
        {
            Prepare(Probe(new()));
            void Broken() => throw new InvalidOperationException("synthetic subscriber");
            NetworkHealthService.Changed += Broken;
            try
            {
                var r = await NetworkHealthService.CheckAsync(ConnectionService.SessionEpoch, CancellationToken.None);
                check(r!.AllAvailable && !NetworkHealthService.Current.IsChecking, "subscriber broke detection");
            }
            finally { NetworkHealthService.Changed -= Broken; }
        });
        await test("result callback disconnect cannot cause stale failure notice", async () =>
        {
            Prepare(Probe(new() { Respond = (_, _) => throw new HttpRequestException("synthetic") }));
            await NetworkHealthService.CheckAsync(ConnectionService.SessionEpoch, CancellationToken.None);
            void DisconnectOnResult()
            {
                if (NetworkHealthService.Current.Status != NetworkHealthStatus.Unavailable) return;
                ConnectionService.IsConnected = false;
                NetworkHealthService.Reset();
            }
            NetworkHealthService.Changed += DisconnectOnResult;
            try
            {
                check(await NetworkHealthService.CheckAsync(ConnectionService.SessionEpoch, CancellationToken.None) == null
                    && ConnectionService.Notices.Count == 0, "stale notice escaped after disconnect");
            }
            finally { NetworkHealthService.Changed -= DisconnectOnResult; }
        });
        await test("manual checks never accumulate automatic switching decisions", async () =>
        {
            Prepare(Probe(new() { Respond = (_, _) => throw new HttpRequestException("synthetic") }));
            AppState.State.FailoverEnabled = true;
            var streak = typeof(FailoverService).GetField("_failStreak", BindingFlags.NonPublic | BindingFlags.Static)!;
            streak.SetValue(null, 0);
            var epoch = ConnectionService.SessionEpoch;
            for (var n = 0; n < 5; n++) await FailoverService.CheckNetworkNowAsync();
            check((int)streak.GetValue(null)! == 0 && ConnectionService.SessionEpoch == epoch
                && NetworkHealthService.Current.Status == NetworkHealthStatus.Unavailable, "manual check caused automatic switching");
        });
        await test("network recovery clears failure streak and permits future outage notice", async () =>
        {
            var h = new NetworkProbeHandler { Respond = (_, _) => throw new HttpRequestException("synthetic") };
            Prepare(Probe(h));
            await AutomaticCheck();
            await AutomaticCheck();
            h.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            await AutomaticCheck();
            check(NetworkHealthService.Current.Status == NetworkHealthStatus.Available
                && NetworkHealthService.Current.FailureStreak == 0, "recovery retained failed state");
            h.Respond = (_, _) => throw new HttpRequestException("synthetic second outage");
            await AutomaticCheck();
            await AutomaticCheck();
            check(ConnectionService.Notices.Count == 2, "new outage notification suppressed forever");
        });
        await test("background monitor checks immediately with automatic switching disabled", async () =>
        {
            Prepare(Probe(new()));
            typeof(FailoverService).GetMethod("Start", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
            try
            {
                using var budget = new CancellationTokenSource(2000);
                while (NetworkHealthService.Current.CheckedAt == null) await Task.Delay(5, budget.Token);
                check(NetworkHealthService.Current.Status == NetworkHealthStatus.Available, "first background check was delayed");
            }
            finally { await FailoverService.StopAsync(); }
            check(NetworkHealthService.Current.Epoch == -1, "stopped monitor retained state");
        });
        NetworkHealthService.Reset();
        ConnectionService.IsConnected = false;
        ConnectionService.IsTunSession = false;
    }
}
