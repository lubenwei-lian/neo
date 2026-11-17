using System.Text.Json;
using Neo.App.Services;
using Neo.Core.Core;

var output = Path.GetFullPath(args.Single()); Directory.CreateDirectory(output);
var config = Path.Combine(output, "synthetic-config.json");
void WriteConfig() => File.WriteAllText(config, """{"inbounds":[{"tag":"http-in","protocol":"http","port":32101},{"tag":"api-in","protocol":"dokodemo-door","port":32102}],"outbounds":[{"tag":"direct","protocol":"freedom"}]}""");
WriteConfig();
void Prepare()
{
    ConnectionService.Change(false, null);
    ConnectionService.Change(true, new(ConnectionService.SessionEpoch + 1, "xray", config, false, "rule"));
}
WebsiteDiagnosticResult Healthy(NetworkDiagnosticTarget target, KernelRouteContext context) =>
    new(target, "可达", 204, 10, context.Describe("direct", "synthetic boundary", "no network"), "synthetic boundary");
Task<WebsiteDiagnosticResult> Immediate(NetworkDiagnosticTarget target, KernelRouteContext context, bool tun, CancellationToken ct)
{ ct.ThrowIfCancellationRequested(); return Task.FromResult(Healthy(target, context)); }
var results = new List<object>(); var failures = 0;
void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
async Task Test(string name, Func<Task> action)
{
    try { await action(); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; results.Add(new { name, passed = false, error = ex.ToString() }); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
NetworkDiagnosticsService.Init();
await Test("manual batch publishes all requested targets in original order", async () =>
{
    Prepare(); var epoch = ConnectionService.SessionEpoch;
    await NetworkDiagnosticsService.RunAsync(Immediate);
    var current = NetworkDiagnosticsService.Current;
    Check(!current.IsRunning && current.Results.Count == NetworkDiagnosticTargets.All.Count && current.Results.All(x => x.State == "可达")
        && current.Results.Select(x => x.Target).SequenceEqual(NetworkDiagnosticTargets.All), "batch rows missing/reordered");
    Check(ConnectionService.IsConnected && ConnectionService.SessionEpoch == epoch, "diagnostic altered session");
});
await Test("repeated start coalesces and website concurrency is bounded to three", async () =>
{
    Prepare();
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var active = 0; var maximum = 0; var calls = 0;
    async Task<WebsiteDiagnosticResult> Probe(NetworkDiagnosticTarget target, KernelRouteContext context, bool tun, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref active); Interlocked.Increment(ref calls);
        maximum = Math.Max(maximum, n); if (n == 3) entered.TrySetResult();
        try { await release.Task.WaitAsync(ct); return Healthy(target, context); }
        finally { Interlocked.Decrement(ref active); }
    }
    var first = NetworkDiagnosticsService.RunAsync(Probe);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
    var duplicate = NetworkDiagnosticsService.RunAsync(Probe);
    Check(ReferenceEquals(first, duplicate) && calls == 3, "duplicate detection batch started");
    release.SetResult(); await first;
    Check(maximum == 3 && calls == NetworkDiagnosticTargets.All.Count, "website concurrency/universe incorrect");
});
await Test("cancellation terminates queued and active rows without retaining busy state", async () =>
{
    Prepare(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var task = NetworkDiagnosticsService.RunAsync(async (target, context, tun, ct) =>
    { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return Healthy(target, context); });
    await entered.Task; NetworkDiagnosticsService.Cancel(); await task.WaitAsync(TimeSpan.FromSeconds(2));
    Check(!NetworkDiagnosticsService.Current.IsRunning && NetworkDiagnosticsService.Current.Results.All(x => x.State == "已取消"), "cancelled rows or state retained");
});
await Test("late result cannot overwrite a completed successor session", async () =>
{
    Prepare(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var old = NetworkDiagnosticsService.RunAsync(async (target, context, tun, ct) =>
    { entered.TrySetResult(); await release.Task; return Healthy(target, context); });
    await entered.Task;
    Prepare(); await NetworkDiagnosticsService.RunAsync(Immediate);
    var successor = NetworkDiagnosticsService.Current;
    release.SetResult(); await old;
    Check(NetworkDiagnosticsService.Current == successor, "old rows overwrote successor");
});
await Test("disconnect invalidates all displayed route evidence", async () =>
{
    Prepare(); await NetworkDiagnosticsService.RunAsync(Immediate);
    ConnectionService.Change(false, null);
    Check(NetworkDiagnosticsService.Current.Epoch == -1 && !NetworkDiagnosticsService.Current.IsRunning
        && NetworkDiagnosticsService.Current.Results.All(x => x.State == "未检测" && x.Route.Path == "未确认"), "old session evidence remained current");
});
await Test("broken observer does not break remaining rows", async () =>
{
    Prepare(); void Broken() => throw new InvalidOperationException("synthetic observer");
    NetworkDiagnosticsService.Changed += Broken;
    try { await NetworkDiagnosticsService.RunAsync(Immediate); Check(NetworkDiagnosticsService.Current.Results.All(x => x.State == "可达"), "observer stopped batch"); }
    finally { NetworkDiagnosticsService.Changed -= Broken; }
});
await Test("configuration error remains unconfirmed and next run can recover", async () =>
{
    Prepare(); File.WriteAllText(config, "not json");
    await NetworkDiagnosticsService.RunAsync(Immediate);
    Check(!NetworkDiagnosticsService.Current.IsRunning && NetworkDiagnosticsService.Current.Results.All(x => x.Route.Path == "未确认"), "configuration error invented routes");
    WriteConfig(); await NetworkDiagnosticsService.RunAsync(Immediate);
    Check(NetworkDiagnosticsService.Current.Results.All(x => x.State == "可达"), "failed run poisoned next run");
});
await Test("disconnected start performs no transport work", async () =>
{
    ConnectionService.Change(false, null); var calls = 0;
    await NetworkDiagnosticsService.RunAsync((target, context, tun, ct) => { calls++; return Task.FromResult(Healthy(target, context)); });
    Check(calls == 0 && !NetworkDiagnosticsService.Current.IsRunning, "disconnected batch made requests");
});
await Test("recovered websites remain visibly counted as unstable", async () =>
{
    Prepare();
    await NetworkDiagnosticsService.RunAsync((target, context, tun, ct) =>
    {
        var healthy = Healthy(target, context);
        return Task.FromResult(healthy with { Attempts = [
            new(1, "失败", null, 10, healthy.Route, "synthetic EOF"),
            new(2, "可达", 204, 10, healthy.Route, "synthetic recovery") ] });
    });
    Check(NetworkDiagnosticsService.Current.Summary.Contains($"复测恢复 {NetworkDiagnosticTargets.All.Count}")
        && NetworkDiagnosticsService.Current.Results.All(x => x.RecoveredAfterRetry), "retry recoveries hidden in healthy total");
});
await Test("GitHub checks the actual website without consuming anonymous REST quota", () =>
{
    var target = NetworkDiagnosticTargets.All.Single(t => t.Name == "GitHub");
    Check(target.Url.AbsoluteUri == "https://github.com/", "GitHub points at a quota-limited API instead of the website");
    Check(WebsiteDiagnosticProbe.Classify(target, 200).State == "可达", "website success was rejected");
    foreach (var status in new[] {401, 403, 429})
        Check(WebsiteDiagnosticProbe.Classify(target, status).State == "站点限制", "real HTTP restriction was hidden");
    return Task.CompletedTask;
});
await ProbeChecks.RunAsync(Test, Check, output);
await DelayProbeChecks.RunAsync(Test, Check);
await CurrentExitChecks.RunAsync(Test, Check);
await ExitBatchChecks.RunAsync(Test, Check);
File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { total = results.Count, failures, results }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{results.Count - failures}/{results.Count} passed");
return failures == 0 ? 0 : 1;
