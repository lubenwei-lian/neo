using Neo.App.Services;
using Neo.Core.Core;

internal static class CurrentExitChecks
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> test, Action<bool, string> check)
    {
        await test("exit trace validates public IP and country rather than a node label", () =>
        {
            check(CurrentExitProbe.ParseTrace("ip=1.1.1.1\nloc=JP\n") == ("jp", "1.1.1.1"), "valid trace rejected");
            check(CurrentExitProbe.ParseTrace("ip=2606:4700:4700::1111\r\nloc=US\r\n")?.Country == "us", "valid IPv6 rejected");
            foreach (var text in new[] { "loc=Japan\nip=1.1.1.1", "loc=JP\nip=garbage", "loc=XX\nip=1.1.1.1", "<html>loc=JP ip=1.1.1.1</html>" })
                check(CurrentExitProbe.ParseTrace(text) == null, "invalid trace accepted");
            foreach (var ip in new[] { "127.0.0.1", "10.0.0.1", "192.168.1.1", "172.20.0.1", "169.254.1.1", "100.64.0.1", "::1", "fd00::1" })
                check(CurrentExitProbe.ParseTrace("loc=JP\nip=" + ip) == null, "private IP shown as node exit");
            return Task.CompletedTask;
        });

        var calls = 0;
        Func<NetworkDiagnosticSession, CancellationToken, Task<CurrentExitResult>> transport = (_, _) => Task.FromResult(Exit("jp", "1.1.1.1"));
        Task<CurrentExitResult> Probe(NetworkDiagnosticSession session, CancellationToken token)
        { Interlocked.Increment(ref calls); return transport(session, token); }
        ConnectionService.Change(false, null);
        CurrentExitService.Init(Probe);
        void Connect()
        { ConnectionService.Change(true, new(ConnectionService.SessionEpoch + 1, "xray", "synthetic-unused", false, "rule")); }
        await test("connection checks its own exit once and page visits reuse it", async () =>
        {
            var before = calls; Connect(); await CurrentExitService.RunAsync();
            var result = CurrentExitService.Current;
            check(result.Epoch == ConnectionService.SessionEpoch && result.Result.Country == "jp" && !result.IsRunning, "committed session exit absent");
            await CurrentExitService.RunAsync(); await CurrentExitService.RunAsync();
            check(calls == before + 1, "page visits re-probed every node or repeated same session");
        });
        await test("late exit from previous node cannot replace new node", async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            transport = async (_, _) => { entered.TrySetResult(); await release.Task; return Exit("us", "8.8.8.8"); };
            Connect(); var old = CurrentExitService.RunAsync(); await entered.Task;
            transport = (_, _) => Task.FromResult(Exit("jp", "1.1.1.1"));
            Connect(); await CurrentExitService.RunAsync(); var successor = CurrentExitService.Current;
            release.SetResult(); await old;
            check(CurrentExitService.Current == successor && successor.Result.Country == "jp", "old node's exit overwrote current node");
        });
        await test("exit lookup failure clears cached success and disconnect clears all evidence", async () =>
        {
            transport = (_, _) => Task.FromResult(CurrentExitResult.Unknown("timeout"));
            await CurrentExitService.RunAsync(refresh: true);
            check(!CurrentExitService.Current.Result.Confirmed && !CurrentExitService.Current.IsRunning, "failed lookup reused old country");
            ConnectionService.Change(false, null);
            check(CurrentExitService.Current.Epoch == -1 && !CurrentExitService.Current.Result.Confirmed, "disconnect retained old exit");
            var before = calls; await CurrentExitService.RunAsync(); check(calls == before, "disconnected lookup performed transport");
        });
        await test("stopping exit lookup releases busy state without declaring a fake location", async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            transport = async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return Exit("jp", "1.1.1.1"); };
            Connect(); var running = CurrentExitService.RunAsync(); await entered.Task;
            CurrentExitService.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(2));
            check(!CurrentExitService.Current.IsRunning && !CurrentExitService.Current.Result.Confirmed, "cancel retained busy state or location");
            ConnectionService.Change(false, null);
        });
        await test("manual refresh joins in-flight lookup and never changes the selected session", async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            transport = async (_, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); return Exit("jp", "1.1.1.1"); };
            Connect(); var epoch = ConnectionService.SessionEpoch; var running = CurrentExitService.RunAsync(); await entered.Task;
            var duplicate = CurrentExitService.RunAsync(refresh: true);
            check(ReferenceEquals(running, duplicate), "refresh started a second lookup");
            release.SetResult(); await running;
            check(ConnectionService.IsConnected && ConnectionService.SessionEpoch == epoch, "exit lookup switched or reconnected node");
            ConnectionService.Change(false, null);
        });
    }
    private static CurrentExitResult Exit(string country, string ip) => new(country, ip, "synthetic trace", new("代理", "proxy", "synthetic", "no network"), "fixture");
}
