using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Neo.App.Services;
using Neo.Core;
using Neo.Core.Core;
using Neo.Core.Models;
using Neo.App.Tests;

var results = new List<object>();
var failures = 0;
async Task Test(string name, Func<Task> run)
{
    var sw = Stopwatch.StartNew();
    try { await run(); results.Add(new { name, passed = true, ms = sw.ElapsedMilliseconds }); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; results.Add(new { name, passed = false, error = ex.ToString() }); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
Task Sync(Action run) { run(); return Task.CompletedTask; }
async Task Until(Func<bool> done, int ms = 4000)
{
    using var limit = new CancellationTokenSource(ms);
    while (!done()) await Task.Delay(10, limit.Token);
}
var first = new NodeItem { Name = "test-a", Server = "127.0.0.1", Port = 9, Protocol = NodeProtocol.VLESS, Security = "tls", RealDelay = 100 };
var other = new NodeItem { Name = "test-b", Server = "127.0.0.1", Port = 10, Protocol = NodeProtocol.VLESS, Security = "tls", RealDelay = 110 };
AppState.Nodes = [first, other];

var checkOnce = typeof(FailoverService).GetMethod("CheckOnceAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
await NetworkHealthChecks.RunAsync(Test, Check);
async Task Repair(bool failover, string? prior, bool expected, bool tun = false, bool enabled = false, bool repair = true, bool? effective = null)
{
    AppState.State = new() { FailoverEnabled = failover, AutoRepairSystemProxy = repair };
    ConnectionService.IsConnected = true; ConnectionService.IsTunSession = tun;
    SystemProxy.IsEnabled = enabled; SystemProxy.CurrentServer = prior; SystemProxy.Enables.Clear();
    SystemProxy.EffectiveOverride = effective;
    SystemProxy.EnableSucceeds = true;
    NetworkHealthService.Reset();
    NetworkHealthService.BeginSession(ConnectionService.SessionEpoch,
        new NetworkAvailabilityProbe([("synthetic", new NetworkProbeHandler())]));
    using var cts = new CancellationTokenSource();
    SystemProxy.OnEnable = cts.Cancel;
    try { await (Task)checkOnce.Invoke(null, [cts.Token])!; }
    catch (OperationCanceledException) { }
    Check(SystemProxy.Enables.Count == (expected ? 1 : 0), "unexpected proxy repair count");
    if (expected) Check(SystemProxy.Enables.Single() == LocalProxySettings.Server, "repair must use HTTP/HTTPS/SOCKS string");
    SystemProxy.OnEnable = null;
    NetworkHealthService.Reset();
}
await Test("proxy repair independent of failover", () => Repair(false, null, true));
await Test("recognize own protocol-specific string", () => Repair(true, LocalProxySettings.Server, true));
await Test("repair legacy bare SOCKS setting", () => Repair(false, "localhost:21808", true));
await Test("preserve foreign proxy", () => Repair(false, "http=127.0.0.1:7890;https=127.0.0.1:7890", false));
await Test("preserve mixed foreign/Neo proxy", () => Repair(false, "http=127.0.0.1:21809;https=foreign.invalid:443", false));
await Test("TUN never repairs system proxy", () => Repair(false, null, false, tun: true));
await Test("disabled repair stays disabled", () => Repair(false, null, false, repair: false));
await Test("enabled proxy is left alone", () => Repair(false, LocalProxySettings.Server, false, enabled: true));
await Test("repair registry-enabled but ineffective Windows proxy", () => Repair(false, LocalProxySettings.Server, true, enabled: true, effective: false));
await Test("ineffective system proxy still respects disabled automatic repair", () => Repair(false, LocalProxySettings.Server, false, enabled: true, effective: false, repair: false));
await Test("queued repair cannot resurrect disconnected session", async () =>
{
    FailoverService.RunOnUi = action => { ConnectionService.IsConnected = false; action(); };
    try { await Repair(false, null, false); } finally { FailoverService.RunOnUi = null; }
});
await Test("queued repair from a superseded session cannot write Windows settings", async () =>
{
    FailoverService.RunOnUi = action => { ConnectionService.SessionEpoch++; action(); };
    try { await Repair(false, LocalProxySettings.Server, false, enabled: true, effective: false); }
    finally { FailoverService.RunOnUi = null; }
});
await Test("cancelled check performs no registry operation", async () =>
{
    SystemProxy.Enables.Clear();
    try { await (Task)checkOnce.Invoke(null, [new CancellationToken(true)])!; } catch (OperationCanceledException) { }
    Check(SystemProxy.Enables.Count == 0, "cancelled repair wrote proxy");
});
await Test("TUN adapter presence cannot suppress mature network changes", () => Sync(() =>
{
    var type = typeof(FailoverService);
    var stamp = type.GetField("_lastTunEventAt", BindingFlags.NonPublic | BindingFlags.Static)!;
    var isOurChange = type.GetMethod("IsLikelyOurTunChange", BindingFlags.NonPublic | BindingFlags.Static)!;
    stamp.SetValue(null, DateTime.Now.AddMinutes(-1));
    Check(!(bool)isOurChange.Invoke(null, null)!, "old TUN event suppressed network change");
    FailoverService.MarkTunNetworkEvent();
    Check((bool)isOurChange.Invoke(null, null)!, "immediate own TUN event not debounced");
}));

await Test("selfcheck honors disabled automatic switching", async () =>
{
    PolicyUnderTest.Reset(first); AppState.State.FailoverEnabled = false;
    await PolicyUnderTest.RunSelfCheck(first);
    Check(PolicyUnderTest.AutomaticConnectCalls == 0, "selfcheck switched despite disabled setting");
});
await Test("enabled selfcheck can switch eligible candidate", async () =>
{
    PolicyUnderTest.Reset(first); AppState.State.FailoverEnabled = true;
    await PolicyUnderTest.RunSelfCheck(first);
    Check(PolicyUnderTest.AutomaticConnectCalls == 1, "enabled switch regression");
});
await Test("watchdog forces recovery of live but stalled core", async () =>
{
    PolicyUnderTest.Reset(first);
    await PolicyUnderTest.RunWatchdog(first).WaitAsync(TimeSpan.FromSeconds(10));
    Check(PolicyUnderTest.AutomaticConnectCalls == 1 && PolicyUnderTest.LastForce, "stalled core was not force-restarted");
    Check(PolicyUnderTest.SafeModeCalls == 0, "healthy restart must not enter safe mode");
});
await Test("watchdog keeps supervising after unsuccessful no-op recovery", async () =>
{
    PolicyUnderTest.Reset(first);
    PolicyUnderTest.ConnectHook = () => Task.FromResult((false, (string?)"busy"));
    await PolicyUnderTest.RunWatchdog(first).WaitAsync(TimeSpan.FromSeconds(15));
    Check(PolicyUnderTest.AutomaticConnectCalls == 2 && PolicyUnderTest.SafeModeCalls == 1, "watchdog silently retired before bounded failure state");
});
await Test("watchdog honors disabled restart and remains alive", async () =>
{
    PolicyUnderTest.Reset(first); AppState.State.AutoRestartCore = false;
    using var cts = new CancellationTokenSource(120);
    try { await PolicyUnderTest.RunWatchdog(first, cts.Token); } catch (OperationCanceledException) { }
    Check(cts.IsCancellationRequested && PolicyUnderTest.AutomaticConnectCalls == 0 && PolicyUnderTest.SafeModeCalls == 0,
        "disabled restart changed session or terminated supervision");
});
await Test("manual disconnect cancels delayed recovery", async () =>
{
    PolicyUnderTest.Reset(first);
    var pending = PolicyUnderTest.Recover(first, true);
    await Task.Delay(50); PolicyUnderTest.ManualDisconnect();
    Check(!await pending && PolicyUnderTest.AutomaticConnectCalls == 0, "disconnect resurrected by recovery");
});
await Test("responsive API without user stats never triggers watchdog recovery", async () =>
{
    PolicyUnderTest.Reset(first); XrayStats.Responsive = true;
    using var cts = new CancellationTokenSource(120);
    try { await PolicyUnderTest.RunWatchdog(first, cts.Token); } catch (OperationCanceledException) { }
    Check(cts.IsCancellationRequested && PolicyUnderTest.AutomaticConnectCalls == 0 && PolicyUnderTest.SafeModeCalls == 0,
        "watchdog confused missing user counters with unresponsive API");
});
await Test("replacement session invalidates delayed recovery", async () =>
{
    PolicyUnderTest.Reset(first);
    var pending = PolicyUnderTest.Recover(first, true);
    await Task.Delay(50); PolicyUnderTest._sessionEpoch++; PolicyUnderTest.ConnectedNode = other;
    Check(!await pending && PolicyUnderTest.AutomaticConnectCalls == 0, "old recovery replaced newer session");
});
await Test("recovery gate rejects concurrent restarts", async () =>
{
    PolicyUnderTest.Reset(first);
    var pending = PolicyUnderTest.Recover(first, true);
    Check(!await PolicyUnderTest.Recover(first, true), "concurrent recovery was admitted");
    PolicyUnderTest.ManualDisconnect(); await pending;
    Check(PolicyUnderTest.AutomaticConnectCalls == 0, "concurrent recovery ignored cancellation");
});
await Test("restart toggle is rechecked after countdown", async () =>
{
    PolicyUnderTest.Reset(first);
    var pending = PolicyUnderTest.Recover(first, true);
    await Task.Delay(50); AppState.State.AutoRestartCore = false;
    Check(!await pending && PolicyUnderTest.AutomaticConnectCalls == 0, "restart ignored changed setting");
});

var usageType = typeof(UsageTracker);
await Test("all-subscription refresh excludes concurrent single refresh", async () => {
    SubscriptionPolicyUnderTest.Reset();
    var fetching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    SubscriptionFetcher.FetchHook = async url => { fetching.TrySetResult(); await release.Task; return new() {Success = true, RawText = url}; };
    var all = SubscriptionPolicyUnderTest.RefreshSubscriptionAsync(); await fetching.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Check(!(await SubscriptionPolicyUnderTest.RefreshOneAsync("a")).Ok, "single refresh entered active all-refresh transaction");
    release.TrySetResult(); Check((await all).Ok, "all refresh failed");
    Check(SubscriptionPolicyUnderTest.Cache.Count == 2, "all refresh lost a cache entry");
    Check((await SubscriptionPolicyUnderTest.RefreshOneAsync("a")).Ok && SubscriptionPolicyUnderTest.Cache.Count == 2, "gate not released or unrelated cache overwritten");
});
await Test("single-subscription refresh excludes all refresh", async () => {
    SubscriptionPolicyUnderTest.Reset();
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    SubscriptionFetcher.FetchHook = async url => { await release.Task; return new() {Success = true, RawText = url}; };
    var single = SubscriptionPolicyUnderTest.RefreshOneAsync("a");
    Check(!(await SubscriptionPolicyUnderTest.RefreshSubscriptionAsync()).Ok, "all refresh entered active single transaction");
    release.TrySetResult(); Check((await single).Ok, "single refresh failed");
    Check((await SubscriptionPolicyUnderTest.RefreshSubscriptionAsync()).Ok, "single transaction did not release gate");
});
await Test("missing subscription and failed fetch release refresh gate", async () => {
    SubscriptionPolicyUnderTest.Reset();
    Check(!(await SubscriptionPolicyUnderTest.RefreshOneAsync("missing")).Ok, "missing subscription accepted");
    SubscriptionFetcher.FetchHook = url => Task.FromResult(new SubscriptionFetcher.Result {Success = false, Error = "synthetic failure"});
    Check(!(await SubscriptionPolicyUnderTest.RefreshOneAsync("a")).Ok, "failed fetch accepted");
    SubscriptionPolicyUnderTest.Reset(); Check((await SubscriptionPolicyUnderTest.RefreshOneAsync("a")).Ok, "error leaked semaphore");
});
void SetUsage(string field, object value) => usageType.GetField(field, BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, value);
void SeedYesterday(bool firstSample = false)
{
    ((Dictionary<string, long>)usageType.GetField("_daily", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Clear();
    SetUsage("_date", DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd"));
    SetUsage("_today", 12345L); SetUsage("_total", 99999L);
    SetUsage("_lastUp", firstSample ? -1L : 100L); SetUsage("_lastDown", firstSample ? -1L : 100L);
}
await Test("zero-traffic midnight archives yesterday", () => Sync(() =>
{
    SeedYesterday(); UsageTracker.Sample(100, 100);
    Check(UsageTracker.TodayBytes == 0 && UsageTracker.TotalBytes == 99999, "idle rollover changed totals or retained yesterday");
    Check(UsageTracker.WeekBytes == 12345 && UsageTracker.MonthBytes == 12345, "history lost during rollover");
}));
await Test("first sample after midnight still rolls over", () => Sync(() =>
{
    SeedYesterday(true); UsageTracker.Sample(100, 100); Check(UsageTracker.TodayBytes == 0, "baseline early return bypassed rollover");
}));
await Test("reading aggregates without sampling rolls over", () => Sync(() =>
{
    SeedYesterday(); Check(UsageTracker.WeekBytes == 12345, "week omitted yesterday");
    SeedYesterday(); var series = UsageTracker.DailySeries(7); Check(series[^1] == 0 && series[^2] == 12345, "chart labels and date out of sync");
    SeedYesterday(); Check(UsageTracker.TodayBytes == 0, "getter retained previous day");
}));
await Test("ordinary usage increments and counter resets remain correct", () => Sync(() =>
{
    UsageTracker.SessionBreak(); var initial = UsageTracker.TotalBytes;
    UsageTracker.Sample(10, 20); UsageTracker.Sample(20, 40);
    Check(UsageTracker.TotalBytes == initial + 30, "ordinary delta incorrect");
    UsageTracker.Sample(0, 0); UsageTracker.Sample(5, 8);
    Check(UsageTracker.TotalBytes == initial + 43, "reset counters double-counted");
}));
await Test("all repeated fatal errors survive disabled log level", () => Sync(() =>
{
    LogBus.Clear(); AppState.State.LogLevel = "none";
    for (var i = 0; i < 12; i++) LogBus.Core("FATAL[0000] repeated_test_failure");
    Check(LogBus.Snapshot().Count == 12 && LogBus.Snapshot().All(x => x.Level == "Error"), "fatal messages dropped");
}));
await Test("warning dedup remains bounded", () => Sync(() =>
{
    LogBus.Clear(); AppState.State.LogLevel = "warning";
    for (var i = 0; i < 12; i++) LogBus.Core("[Warning] repeated_test_warning");
    Check(LogBus.Snapshot().Count == 4, "warning dedup regression");
}));

await Test("engine routing matrix preserves app implementation limits", () => Sync(() =>
{
    var cases = new[] {
        (new NodeItem { Protocol = NodeProtocol.Hysteria2 }, KernelSupportKind.SingBoxOnly),
        (new NodeItem { Protocol = NodeProtocol.WireGuard }, KernelSupportKind.SingBoxOnly),
        (new NodeItem { Protocol = NodeProtocol.TUIC }, KernelSupportKind.SingBoxOnly),
        (new NodeItem { Protocol = NodeProtocol.AnyTls }, KernelSupportKind.SingBoxOnly),
        (new NodeItem { Protocol = NodeProtocol.Trojan, Security = "tls", AllowInsecure = true }, KernelSupportKind.SingBoxOnly),
        (new NodeItem { Protocol = NodeProtocol.VLESS, Security = "tls", ForceInsecure = true }, KernelSupportKind.SingBoxOnly),
        (new NodeItem { Protocol = NodeProtocol.VLESS, Network = "xhttp", Security = "tls" }, KernelSupportKind.XrayOnly),
        (first, KernelSupportKind.Both),
    };
    foreach (var (node, kind) in cases)
    {
        Check(KernelSupport.Of(node) == kind, "wrong app capability: " + node.Protocol);
        KernelRouter.Clear(); KernelRouter.Set(node.Protocol.ToString(), "xray");
        Check(KernelRouter.ResolveFor(node) == (kind == KernelSupportKind.SingBoxOnly ? KernelRouter.Choice.Auto : KernelRouter.Choice.Xray), "unsafe engine override");
        Check(!KernelSupport.Describe(node).Contains("xray 不支持"), "description claims universal binary limitation");
    }
    KernelRouter.Clear();
}));

await Test("old stats generation cannot overwrite new sampling", async () =>
{
    StatsSampler.Stop(); ConnectionService.IsConnected = true; ConnectionService.CorePid = 100;
    using var oldStarted = new ManualResetEventSlim(); using var releaseOld = new ManualResetEventSlim();
    CoreStats.QueryHook = () => { oldStarted.Set(); releaseOld.Wait(TimeSpan.FromSeconds(5)); return new(999, 999); };
    StatsSampler.Start(); Check(oldStarted.Wait(2000), "old query not started");
    StatsSampler.Stop(); ConnectionService.SessionEpoch++;
    CoreStats.QueryHook = () => new(10, 20); StatsSampler.Start();
    await Until(() => StatsSampler.Latest is { Uplink: 10 });
    releaseOld.Set(); await Task.Delay(150);
    Check(StatsSampler.Latest is { Uplink: 10 }, "old generation overwrote new sample");
    StatsSampler.Stop();
});
await Test("failed stats retain last successful sample and timestamp", async () =>
{
    ConnectionService.IsConnected = true; CoreStats.QueryHook = () => new(30, 40); StatsSampler.Start();
    await Until(() => StatsSampler.Latest is { Uplink: 30 }); var stamp = StatsSampler.SampledAt;
    CoreStats.QueryHook = () => null; await Task.Delay(1150);
    Check(StatsSampler.Latest is { Uplink: 30 } && StatsSampler.SampledAt == stamp, "failure erased sample or faked freshness");
    StatsSampler.Stop();
});

async Task HalfClose(bool relayEnabled, int closeDelay, int responseDelay)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    var backendPort = ((IPEndPoint)listener.LocalEndpoint).Port;
    var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start(); var port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
    using var relay = relayEnabled ? new PortRelay(port, backendPort) : null; relay?.Start();
    var server = Task.Run(async () => {
        using var peer = await listener.AcceptTcpClientAsync(timeout.Token); var stream = peer.GetStream();
        var buf = new byte[64]; while (await stream.ReadAsync(buf, timeout.Token) > 0) { }
        await Task.Delay(responseDelay, timeout.Token); await stream.WriteAsync("complete-response"u8.ToArray(), timeout.Token);
    });
    try
    {
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, relayEnabled ? port : backendPort, timeout.Token);
        var stream = client.GetStream(); await stream.WriteAsync("request"u8.ToArray(), timeout.Token);
        if (closeDelay > 0) await Task.Delay(closeDelay, timeout.Token); client.Client.Shutdown(SocketShutdown.Send);
        using var data = new MemoryStream(); await stream.CopyToAsync(data, timeout.Token); await server;
        Check(Encoding.ASCII.GetString(data.ToArray()) == "complete-response", "valid half-close response truncated");
        if (relay != null) await Until(() => relay.ActiveFor(backendPort) == 0);
    }
    finally { listener.Stop(); }
}
await Test("direct loopback half-close positive control", () => HalfClose(false, 0, 6200));
await Test("immediate half-close preserves slow response", () => HalfClose(true, 0, 6200));
await Test("delayed half-close preserves fast response", () => HalfClose(true, 200, 1000));
await Test("delayed half-close preserves response beyond five seconds", () => HalfClose(true, 200, 6200));
await Test("relay stop releases half-open connections", async () =>
{
    using var timeout = new CancellationTokenSource(4000);
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    var backendPort = ((IPEndPoint)listener.LocalEndpoint).Port;
    var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start(); var port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
    using var relay = new PortRelay(port, backendPort); relay.Start();
    using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
    await client.GetStream().WriteAsync("r"u8.ToArray(), timeout.Token);
    using var backend = await listener.AcceptTcpClientAsync(timeout.Token);
    await Until(() => relay.ActiveFor(backendPort) == 1); relay.Stop();
    await Until(() => relay.ActiveFor(backendPort) == 0); listener.Stop();
});

StatsSampler.Stop();
var report = new { passed = failures == 0, total = results.Count, failures, results };
await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "../../../results.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{results.Count - failures}/{results.Count} passed");
return failures == 0 ? 0 : 1;
