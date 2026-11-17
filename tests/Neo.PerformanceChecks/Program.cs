using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Neo.App.Services;
using Neo.Core;
using Neo.Core.Core;
using Neo.Core.Diagnostics;
using Neo.Core.Models;

if (args.SequenceEqual(new[] { "-v" }))
{
    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "kernel-version-fixture-pid.txt"), Environment.ProcessId.ToString());
    if (File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "kernel-version-fixture-mode.txt")) == "hang")
    { Console.Write("version probe still running"); await Task.Delay(Timeout.Infinite); return 0; }
    var stderr = Task.Run(() => Console.Error.Write(new string('e', 128 * 1024)));
    Console.Write(new string('o', 128 * 1024) + "\nmihomo v1.2.3\n"); await stderr; return 0;
}
if (args.Length != 1) throw new ArgumentException("Usage: Neo.PerformanceChecks <isolated-output-directory>");
var output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
foreach (var field in new[] { "_rootDir", "_dataDir" })
    typeof(NeoPaths).GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, output);
var results = new List<object>(); var failures = 0;
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
async Task Test(string name, Func<Task> action)
{
    try { await action().WaitAsync(TimeSpan.FromSeconds(15)); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; results.Add(new { name, passed = false, error = ex.ToString() }); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
Task Sync(Action action) { action(); return Task.CompletedTask; }
NodeItem Node(int id) => new() { Name = "synthetic-" + id, Server = "example.invalid", Port = 10000 + id, Protocol = NodeProtocol.Trojan, Password = "synthetic" };
async Task Canceled(Func<Task> action)
{
    try { await action(); throw new InvalidOperationException("cancellation was swallowed"); }
    catch (OperationCanceledException) { }
}
Task InvokeStartup(string name, CancellationToken ct) => (Task)typeof(StartupSpeedTest)
    .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { ct })!;
void ResetStartup()
{
    Neo.App.App.IsShuttingDown = false; AppState.State.StartupSpeedTest = true;
    UiActivity.SetVisible(true);
    ConnectionService.IsConnecting = ConnectionService.IsSessionBusy = false;
    AppState.Nodes.Clear(); AppState.CurrentNode = null; RealDelayTester.Batches.Clear();
    RealDelayTester.Probe = (_, _) => Task.FromResult(100);
    StatsSampler.Latest = null; StatsSampler.SampledAt = default;
    typeof(StartupSpeedTest).GetField("_lastTrafficSample", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);
    typeof(StartupSpeedTest).GetField("_trafficBusy", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, false);
    NodeQualityHistory.Shared.Clear();
}

await Test("ready proxies pass SOCKS negotiation without fixed startup sleep", async () => {
    await using var socks = new Server(async (stream, ct) => {
        var greeting = new byte[3]; await stream.ReadExactlyAsync(greeting, ct);
        Check(greeting.SequenceEqual(new byte[] {5,1,0}), "unexpected greeting");
        await stream.WriteAsync(new byte[] {5,0}, ct);
    });
    await using var http = new Server((_, _) => Task.CompletedTask);
    var timer = Stopwatch.StartNew();
    Check(await ProxyReadiness.WaitAsync(() => true, socks.Port, http.Port, TimeSpan.FromSeconds(2)), "ready proxy rejected");
    Check(timer.Elapsed < TimeSpan.FromMilliseconds(750), "fixed startup delay remained");
});
await Test("readiness waits for delayed listener", async () => {
    var port = Server.FreePort();
    await using var http = new Server((_, _) => Task.CompletedTask);
    var waiting = ProxyReadiness.WaitAsync(() => true, port, http.Port, TimeSpan.FromSeconds(2));
    await Task.Delay(120);
    await using var socks = new Server(async (stream, ct) => { var data = new byte[3]; await stream.ReadExactlyAsync(data, ct); await stream.WriteAsync(new byte[] {5,0}, ct); }, port);
    Check(await waiting, "late listener rejected");
});
await Test("HTTP listener must also be ready", async () => {
    await using var socks = new Server((_, _) => Task.CompletedTask);
    Check(!await ProxyReadiness.WaitAsync(() => true, socks.Port, Server.FreePort(), TimeSpan.FromMilliseconds(130)), "missing HTTP listener accepted");
});
await Test("open TCP port without SOCKS protocol is not ready", async () => {
    await using var socks = new Server(async (stream, ct) => { var data = new byte[3]; await stream.ReadExactlyAsync(data, ct); await stream.WriteAsync(new byte[] {4,0}, ct); });
    await using var http = new Server((_, _) => Task.CompletedTask);
    Check(!await ProxyReadiness.WaitAsync(() => true, socks.Port, http.Port, TimeSpan.FromSeconds(1)), "invalid SOCKS reply accepted");
});
await Test("stalled SOCKS handshake respects total timeout", async () => {
    await using var socks = new Server(async (_, ct) => await Task.Delay(Timeout.Infinite, ct));
    await using var http = new Server((_, _) => Task.CompletedTask);
    var timer = Stopwatch.StartNew();
    Check(!await ProxyReadiness.WaitAsync(() => true, socks.Port, http.Port, TimeSpan.FromMilliseconds(120)), "stalled handshake accepted");
    Check(timer.Elapsed < TimeSpan.FromSeconds(1), "handshake timeout unbounded");
});
await Test("readiness propagates caller cancellation", async () => {
    using var cancel = new CancellationTokenSource(60);
    await using var socks = new Server(async (_, ct) => await Task.Delay(Timeout.Infinite, ct));
    await using var http = new Server((_, _) => Task.CompletedTask);
    await Canceled(async () => { await ProxyReadiness.WaitAsync(() => true, socks.Port, http.Port, TimeSpan.FromSeconds(8), cancel.Token); });
});
await Test("dead process and stale session reject readiness", async () => {
    Check(!await ProxyReadiness.WaitAsync(() => false, 1, 2, TimeSpan.FromSeconds(1)), "dead process accepted");
    Check(!await ProxyReadiness.WaitAsync(() => true, 1, 2, TimeSpan.FromSeconds(1), isCurrent: () => false), "stale session accepted");
});
await Test("session invalidated during handshake cannot commit readiness", async () => {
    var current = true;
    await using var socks = new Server(async (stream, ct) => { var data = new byte[3]; await stream.ReadExactlyAsync(data, ct); current = false; await stream.WriteAsync(new byte[] {5,0}, ct); });
    await using var http = new Server((_, _) => Task.CompletedTask);
    Check(!await ProxyReadiness.WaitAsync(() => true, socks.Port, http.Port, TimeSpan.FromSeconds(1), isCurrent: () => current), "successor session was overwritten");
});
await Test("custom authenticated configurations can use listener-only readiness", async () => {
    await using var socks = new Server((_, _) => Task.CompletedTask);
    await using var http = new Server((_, _) => Task.CompletedTask);
    Check(await ProxyReadiness.WaitAsync(() => true, socks.Port, http.Port, TimeSpan.FromSeconds(1), negotiateSocks: false), "custom listener rejected");
});

await Test("adaptive relay preserves complete random payload in both directions", async () => {
    var payload = System.Security.Cryptography.RandomNumberGenerator.GetBytes(2*1024*1024); payload[0] = 0x42;
    var expected = payload.Reverse().ToArray();
    await using var backend = new Server(async (stream, ct) => {
        var received = new byte[payload.Length]; await stream.ReadExactlyAsync(received,ct);
        Check(received.SequenceEqual(payload), "upstream payload corrupted across buffer growth");
        await stream.WriteAsync(expected,ct);
    });
    using var relay = new PortRelay(Server.FreePort(),backend.Port); relay.Start();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    using var peer = new TcpClient(); await peer.ConnectAsync(IPAddress.Loopback,relay.ListenPort,timeout.Token);
    var clientStream = peer.GetStream(); await clientStream.WriteAsync(payload,timeout.Token);
    peer.Client.Shutdown(SocketShutdown.Send);
    var response = new byte[payload.Length]; await clientStream.ReadExactlyAsync(response,timeout.Token);
    Check(response.SequenceEqual(expected), "downstream payload corrupted across buffer growth or half-close");
});

await Test("quality without observations preserves original ordering", () => Sync(() => {
    var history = new NodeQualityHistory();
    Check(history.Score("a", 100) < history.Score("b", 200), "delay order changed");
    Check(history.Score("a", 0) == 100000 && history.Score("b", -1) == 200000, "unknown/failed fallback changed");
}));
await Test("local test failures do not penalize remote node", () => Sync(() => {
    var history = new NodeQualityHistory(); history.RecordLatency("a", -2);
    Check(history.Count == 0 && history.Score("a", 100) == 100, "local failure treated as remote failure");
}));
await Test("repeated remote failures outweigh a small latency advantage", () => Sync(() => {
    var history = new NodeQualityHistory(); history.RecordLatency("a", 50);
    for (var i = 0; i < 3; i++) history.RecordHealth("a", false);
    Check(history.Score("a", 50) > history.Score("b", 150), "unreliable low-delay node preferred");
}));
await Test("jitter penalizes inconsistent latency", () => Sync(() => {
    var history = new NodeQualityHistory();
    foreach (var delay in new[] {50,100,150}) history.RecordLatency("a", delay);
    foreach (var delay in new[] {100,100,100}) history.RecordLatency("b", delay);
    Check(history.Score("a", 100) > history.Score("b", 100), "jitter ignored");
}));
await Test("small throughput samples penalize degradation without inventing capacity", () => Sync(() => {
    var history = new NodeQualityHistory(); history.RecordThroughput("a", 20 * 1024);
    history.RecordThroughput("b", 1024 * 1024 * 1024.0);
    Check(history.Score("a", 50) > history.Score("c", 150), "low throughput ignored");
    Check(history.Score("b", 100) == history.Score("c", 100), "tiny sample rewarded as measured capacity");
    history.RecordThroughput("a", double.NaN); history.RecordThroughput("a", -1);
    Check(history.Score("a", 50) > 1000, "invalid samples overwrote degradation");
}));
await Test("quality history expires and is bounded", () => Sync(() => {
    var clock = new ManualClock(); var history = new NodeQualityHistory(clock);
    history.RecordLatency("old", 500); clock.Now += TimeSpan.FromMinutes(21);
    Check(history.Score("old", 50) == 50, "stale quality survived");
    for (var i = 0; i < 2050; i++) history.RecordHealth("key-" + i, false);
    Check(history.Count == 2048, "history grew without a limit"); history.Clear(); Check(history.Count == 0, "network reset did not clear history");
}));
await Test("recovery eventually displaces old failures", () => Sync(() => {
    var history = new NodeQualityHistory(); history.RecordLatency("a", -1);
    for (var i = 0; i < 12; i++) history.RecordLatency("a", 100);
    Check(history.Score("a", 100) == 100, "old failure poisoned recovered node");
}));

await Test("batched logging preserves order and flush barrier", async () => {
    var lines = new List<string>(); var batches = 0;
    await using var writer = new BufferedLogWriter<string>(items => { batches++; lines.AddRange(items); });
    for (var i = 0; i < 500; i++) Check(writer.Write(i.ToString()), "unexpected drop");
    Check(await writer.FlushAsync(TimeSpan.FromSeconds(2)), "flush failed");
    Check(lines.SequenceEqual(Enumerable.Range(0,500).Select(i => i.ToString())), "lines missing or out of order");
    Check(batches < 50, "logs were not batched");
});
await Test("blocked disk does not block ordinary log producers and queue remains bounded", async () => {
    using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
    var lines = new List<string>();
    await using var writer = new BufferedLogWriter<string>(items => { entered.Set(); release.Wait(TimeSpan.FromSeconds(4)); lines.AddRange(items); }, 4);
    writer.Write("first"); Check(entered.Wait(1000), "sink never started");
    try
    {
        var producers = Task.Run(() => { for (var i = 0; i < 100; i++) writer.Write("queued-" + i); });
        await producers.WaitAsync(TimeSpan.FromMilliseconds(500));
        Check(writer.Dropped > 0, "overflow was not bounded");
        Check(!await writer.FlushAsync(TimeSpan.FromMilliseconds(50)), "blocked flush unexpectedly succeeded");
    }
    finally { release.Set(); }
    Check(writer.Write("critical-error", critical: true), "critical message lost at capacity");
    Check(lines.Contains("critical-error"), "critical error not persisted before return");
    Check(await writer.FlushAsync(TimeSpan.FromSeconds(2)), "flush did not recover after timeout");
});
await Test("concurrent logging loses no accepted messages", async () => {
    var lines = new List<string>(); await using var writer = new BufferedLogWriter<string>(items => lines.AddRange(items));
    await Task.WhenAll(Enumerable.Range(0,4).Select(p => Task.Run(() => { for(var i=0;i<200;i++) writer.Write($"{p}:{i}"); })));
    Check(await writer.FlushAsync(TimeSpan.FromSeconds(2)), "flush failed");
    Check(lines.Count == 800 && lines.Distinct().Count() == 800 && writer.Dropped == 0, "concurrent logs lost/duplicated");
});
await Test("write failure is reported and does not kill log worker", async () => {
    var fail = true; var lines = new List<string>();
    await using var writer = new BufferedLogWriter<string>(items => { if (fail) throw new IOException("synthetic disk failure"); lines.AddRange(items); });
    writer.Write("failed"); Check(!await writer.FlushAsync(TimeSpan.FromSeconds(2)), "write failure hidden");
    fail = false; writer.Write("recovered"); Check(await writer.FlushAsync(TimeSpan.FromSeconds(2)), "recovered writer stayed permanently unhealthy");
    Check(lines.Contains("recovered") && writer.FailedBatches == 1, "worker did not survive disk failure");
});
await Test("log disposal drains accepted writes", async () => {
    var lines = new List<string>(); var writer = new BufferedLogWriter<string>(items => lines.AddRange(items));
    for (var i=0;i<150;i++) writer.Write(i.ToString());
    await writer.DisposeAsync(); Check(lines.Count == 150, "shutdown lost accepted logs");
});
await Test("production log errors stay synchronous and ordinary logs flush", async () => {
    LogBus.App("synthetic-critical-check", "Error");
    Check(Directory.GetFiles(NeoPaths.LogDir, "neo-*.log").Any(file => File.ReadAllText(file).Contains("synthetic-critical-check")), "error was deferred");
    for(var i=0;i<500;i++) LogBus.App("synthetic-buffered-check-" + i);
    Check(await LogBus.FlushAsync(TimeSpan.FromSeconds(3)), "production flush failed");
    var text = string.Join("\n", Directory.GetFiles(NeoPaths.LogDir, "neo-*.log").Select(File.ReadAllText));
    Check(text.Contains("synthetic-buffered-check-499"), "last queued log lost");
});

await Test("strict fresh cache is configuration-bound and rename-safe", () => Sync(() => {
    var node = Node(100); DelayCache.Set(node, 100); Check(DelayCache.IsFresh(node, TimeSpan.FromMinutes(10)), "fresh cache missed");
    node.Name = "renamed"; Check(DelayCache.IsFresh(node, TimeSpan.FromMinutes(10)), "rename invalidated identity");
    node.Password = "different"; Check(!DelayCache.IsFresh(node, TimeSpan.FromMinutes(10)), "credentials change reused old measurement");
}));
await Test("relaxed certificate measurements cannot skip strict background probes", () => Sync(() => {
    var node = Node(101); DelayCache.Set(node, 100, "证书放宽");
    Check(!DelayCache.IsFresh(node, TimeSpan.FromMinutes(10)), "relaxed measurement accepted as strict");
    Check(NodeQualityHistory.Shared.Score(AppState.NodeKey(node), 150) == 150, "relaxed measurement entered quality ranking");
}));
await Test("startup probes current candidates first and skips fresh strict cache", async () => {
    ResetStartup(); var nodes = Enumerable.Range(200,8).Select(Node).ToList();
    AppState.Nodes.AddRange(nodes); AppState.CurrentNode = nodes[^1]; DelayCache.Set(nodes[0], 100);
    await InvokeStartup("RunLatencyTestAsync", CancellationToken.None);
    Check(RealDelayTester.Batches[0].Contains(nodes[^1]), "current node blocked by full list");
    Check(!RealDelayTester.Batches.SelectMany(x=>x).Contains(nodes[0]), "fresh node retested");
    Check(nodes.Skip(1).All(n=>n.RealDelay == 100), "background results missing");
});
await Test("background scheduler limits concurrency to two", async () => {
    ResetStartup(); AppState.Nodes.AddRange(Enumerable.Range(300,8).Select(Node));
    var active = 0; var maximum = 0;
    RealDelayTester.Probe = async (_, ct) => { var n = Interlocked.Increment(ref active); int old; do { old = maximum; if(n<=old)break; } while(Interlocked.CompareExchange(ref maximum,n,old)!=old); try { await Task.Delay(20,ct);return 100; } finally { Interlocked.Decrement(ref active); } };
    await InvokeStartup("RunLatencyTestAsync", CancellationToken.None);
    Check(maximum == 2, "background concurrency exceeded limit or serialized all probes");
});
await Test("background and manual probes retry transient failures once by default", async () => {
    ResetStartup(); var node = Node(400); AppState.Nodes.Add(node); var calls = 0;
    RealDelayTester.Probe = (_,_) => { calls++; return Task.FromResult(-1); };
    await InvokeStartup("RunLatencyTestAsync", CancellationToken.None); Check(calls == 2, "background retry budget changed");
    calls = 0; await DelayTestRunner.RunAsync(new[]{node}, "synthetic", null, CancellationToken.None, false, null, null);
    Check(calls == 2, "manual default exceeded the fast retry budget");
});
await Test("explicit deep retry budget remains available and progress reports its limit", async () => {
    ResetStartup(); var node = Node(401); var calls = 0; var progress = new List<DelayTestProgress>();
    RealDelayTester.Probe = (_,_) => { calls++; return Task.FromResult(-1); };
    await DelayTestRunner.RunAsync(new[]{node}, "synthetic", null, CancellationToken.None, false, null,
        p => progress.Add(p), new DelayTestOptions(MaxRetries:4));
    Check(calls == 5 && progress.Where(p => p.Phase == DelayTestPhase.Retry).All(p => p.RetryLimit == 4), "explicit retry budget or progress limit lost");
});
await Test("local kernel failures are not retried as if the remote node were dead", async () => {
    ResetStartup(); var node = Node(402); var calls = 0;
    RealDelayTester.Probe = (_,_) => { calls++; return Task.FromResult(RealDelayTester.BatchFailed); };
    var result = await DelayTestRunner.RunAsync(new[]{node}, "synthetic", null, CancellationToken.None, false, null, null);
    Check(calls == 1 && result[node] == RealDelayTester.BatchFailed, "local failure was repeated or relabeled as a remote timeout");
});
await Test("certificate failures stay explicit without another outer retry", async () => {
    ResetStartup(); var node = Node(403); var calls = 0;
    RealDelayTester.Probe = (n,_) => { calls++; n.RealDelayNote="证书不受信";return Task.FromResult(-1); };
    await DelayTestRunner.RunAsync(new[]{node}, "synthetic", null, CancellationToken.None, false, null, null);
    Check(calls == 1 && node.RealDelayNote == "证书不受信", "certificate failure was retried or its explanation disappeared");
});
await Test("one transient node failure recovers without retesting already successful nodes", async () => {
    ResetStartup(); var good=Node(404);var unstable=Node(405);var counts=new System.Collections.Concurrent.ConcurrentDictionary<NodeItem,int>();
    RealDelayTester.Probe = (n,_) => {var count=counts.AddOrUpdate(n,1,(_,v)=>v+1);return Task.FromResult(n==unstable && count==1 ? -1 : 120);};
    var result=await DelayTestRunner.RunAsync(new[]{good,unstable},"synthetic",null,CancellationToken.None,false,null,null);
    Check(counts[good]==1 && counts[unstable]==2 && result.Values.All(v=>v>0),"transient failure recovery repeated good nodes or failed to recover");
});
await Test("connection activity pauses background work and cancellation wakes it", async () => {
    ResetStartup(); ConnectionService.IsConnecting = true;
    using var cancel = new CancellationTokenSource();
    var waiting = InvokeStartup("WaitForIdleAsync", cancel.Token); await Task.Delay(50);
    Check(!waiting.IsCompleted, "background work ignored connection activity"); cancel.Cancel();
    await Canceled(() => waiting);
});
await Test("background resumes after foreground connection completes", async () => {
    ResetStartup(); ConnectionService.IsSessionBusy = true;
    var waiting = InvokeStartup("WaitForIdleAsync", CancellationToken.None); await Task.Delay(30);
    ConnectionService.IsSessionBusy = false; await waiting.WaitAsync(TimeSpan.FromSeconds(2));
});
await Test("hidden UI pauses automatic probes and restoring UI resumes the same work", async () => {
    ResetStartup(); UiActivity.SetVisible(false);
    var waiting = InvokeStartup("WaitForIdleAsync", CancellationToken.None); await Task.Delay(80);
    Check(!waiting.IsCompleted, "minimized UI still starts optional node probes");
    UiActivity.SetVisible(true); await waiting.WaitAsync(TimeSpan.FromSeconds(2));
});
await Test("paused hidden startup work cancels immediately on exit", async () => {
    ResetStartup(); UiActivity.SetVisible(false); using var cancel = new CancellationTokenSource();
    var waiting = InvokeStartup("WaitForIdleAsync", cancel.Token); cancel.Cancel(); await Canceled(() => waiting);
    UiActivity.SetVisible(true);
});
await Test("hiding cancels in-flight automatic work and resumes the same node without recording failure", async () => {
    ResetStartup(); var node = Node(450); AppState.Nodes.Add(node);
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var calls = 0;
    RealDelayTester.Probe = async (_, ct) => {
        if (Interlocked.Increment(ref calls) == 1) {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { stopped.TrySetResult(); }
        }
        return 87;
    };
    using var exit = new CancellationTokenSource();
    var running = InvokeStartup("RunLatencyTestAsync", exit.Token);
    try {
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2)); UiActivity.SetVisible(false);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(1)); await Task.Delay(80);
        Check(!running.IsCompleted && calls == 1, "hidden probe was restarted or batch discarded");
        Check(!DelayCache.IsFresh(node, TimeSpan.FromMinutes(10)), "suspension recorded a node result");
        UiActivity.SetVisible(true); await running.WaitAsync(TimeSpan.FromSeconds(2));
        Check(calls == 2 && node.RealDelay == 87, "same pending node was not resumed");
    } finally { exit.Cancel(); UiActivity.SetVisible(true); }
});
await Test("visibility lifetime isolates old work and retains caller cancellation", () => Sync(() => {
    ResetStartup(); using var caller = new CancellationTokenSource();
    using var old = UiActivity.CreateOptionalProbeCancellation(caller.Token);
    UiActivity.SetVisible(false); Check(old.IsCancellationRequested, "hidden lifetime survived");
    using var hidden = UiActivity.CreateOptionalProbeCancellation(caller.Token);
    Check(hidden.IsCancellationRequested, "hidden race started a probe");
    UiActivity.SetVisible(true); using var current = UiActivity.CreateOptionalProbeCancellation(caller.Token);
    Check(!current.IsCancellationRequested, "restored lifetime reused canceled source");
    caller.Cancel(); Check(current.IsCancellationRequested, "exit token was detached");
}));
await Test("manual probes are not canceled by presentation visibility", async () => {
    ResetStartup(); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    RealDelayTester.Probe = async (_, ct) => { started.TrySetResult(); await release.Task.WaitAsync(ct); return 91; };
    var node = Node(451);
    var running = DelayTestRunner.RunAsync(new[]{node}, "synthetic", null, CancellationToken.None, false, null, null);
    try {
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1)); UiActivity.SetVisible(false);
        release.TrySetResult(); var result = await running.WaitAsync(TimeSpan.FromSeconds(1));
        Check(result[node] == 91, "manual measurement was changed by hiding UI");
    } finally { release.TrySetResult(); UiActivity.SetVisible(true); }
});
await Test("recent sustained traffic pauses probes and stale samples do not", () => Sync(() => {
    ResetStartup(); var method = typeof(StartupSpeedTest).GetMethod("HasForegroundTraffic",BindingFlags.NonPublic|BindingFlags.Static)!;
    StatsSampler.SampledAt = DateTime.Now.AddSeconds(-2); StatsSampler.Latest = new(0,0); method.Invoke(null,null);
    StatsSampler.SampledAt = StatsSampler.SampledAt.AddSeconds(1); StatsSampler.Latest = new(0,512*1024);
    Check((bool)method.Invoke(null,null)!, "busy network not detected");
    StatsSampler.SampledAt = DateTime.Now.AddSeconds(-10);
    Check(!(bool)method.Invoke(null,null)!, "stale activity paused work forever");
}));
await Test("disabling background tests starts no further probes", async () => {
    ResetStartup(); AppState.State.StartupSpeedTest = false; AppState.Nodes.Add(Node(500));
    var calls = 0; RealDelayTester.Probe = (_,_) => { calls++;return Task.FromResult(100); };
    await Canceled(() => InvokeStartup("RunLatencyTestAsync",CancellationToken.None));
    Check(calls == 0, "disabled scheduler still measured nodes");
});
await Test("startup service stop cancels its settling delay", async () => {
    ResetStartup(); AppState.Nodes.Add(Node(600));
    var task = (Task)typeof(StartupSpeedTest).GetMethod("RunAsync", BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,null)!;
    StartupSpeedTest.Stop(); await task.WaitAsync(TimeSpan.FromSeconds(1));
    Check(RealDelayTester.Batches.Count == 0, "canceled startup still probed nodes");
});

// Run only a copied test apphost; no installed kernel or user data is modified.
var runtimeField = typeof(NeoPaths).GetField("_runtimeDir", BindingFlags.NonPublic | BindingFlags.Static)!;
runtimeField.SetValue(null, AppContext.BaseDirectory);
var fakeKernel = Path.Combine(AppContext.BaseDirectory, "mihomo.exe");
File.Copy(Environment.ProcessPath!, fakeKernel, true);
var modeFile = Path.Combine(AppContext.BaseDirectory, "kernel-version-fixture-mode.txt");
var pidFile = Path.Combine(AppContext.BaseDirectory, "kernel-version-fixture-pid.txt");
File.Delete(pidFile); File.WriteAllText(modeFile, "flood"); KernelCatalog.ResetVersion(CoreEngine.Mihomo);
await Test("cached kernel display lookup performs no process probe", () => Sync(() => {
    Check(KernelCatalog.CachedVersion(CoreEngine.Mihomo) == null && !File.Exists(pidFile), "UI cache lookup started a process");
}));
await Test("kernel version probe drains both large output pipes without deadlock", () => Sync(() => {
    Check(KernelCatalog.ReadVersion(CoreEngine.Mihomo) == "1.2.3" && File.Exists(pidFile), "dual-pipe fixture did not complete");
}));
await Test("kernel version cache handles concurrent display reads without new processes", async () => {
    var pid = File.ReadAllText(pidFile);
    await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => {
        for (var n = 0; n < 100; n++) Check(KernelCatalog.CachedVersion(CoreEngine.Mihomo) == "1.2.3", "cached version disappeared");
    })));
    Check(File.ReadAllText(pidFile) == pid, "display reads spawned another probe");
});
await Test("hung kernel version probe is bounded and leaves no child process", () => Sync(() => {
    File.WriteAllText(modeFile, "hang"); KernelCatalog.ResetVersion(CoreEngine.Mihomo);
    var watch = Stopwatch.StartNew(); var result = KernelCatalog.ReadVersion(CoreEngine.Mihomo);
    var pid = int.Parse(File.ReadAllText(pidFile)); var gone = false;
    try { using var child = Process.GetProcessById(pid); gone = child.HasExited; } catch (ArgumentException) { gone = true; }
    Check(result == null && watch.Elapsed < TimeSpan.FromSeconds(7) && gone, "probe timeout leaked a child or did not bound reads");
}));
await Test("failed kernel probe is cached until explicit update invalidation", () => Sync(() => {
    var pid = File.ReadAllText(pidFile); File.WriteAllText(modeFile, "flood");
    Check(KernelCatalog.ReadVersion(CoreEngine.Mihomo) == null && File.ReadAllText(pidFile) == pid, "failed probe retried on every UI read");
    KernelCatalog.ResetVersion(CoreEngine.Mihomo);
    Check(KernelCatalog.ReadVersion(CoreEngine.Mihomo) == "1.2.3" && File.ReadAllText(pidFile) != pid, "updated kernel version did not refresh");
}));

Check(await LogBus.FlushAsync(TimeSpan.FromSeconds(3)), "final log flush failed");
await File.WriteAllTextAsync(Path.Combine(output,"results.json"), JsonSerializer.Serialize(new { total = results.Count, failures, results },new JsonSerializerOptions {WriteIndented=true}));
Console.WriteLine($"{results.Count-failures}/{results.Count} passed"); return failures == 0 ? 0 : 1;

sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now = new(2026,11,7,0,0,0,TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}
sealed class Server : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<Task> _clients = new();
    private readonly Func<NetworkStream,CancellationToken,Task> _handle; private readonly Task _accept;
    public int Port { get; }
    public Server(Func<NetworkStream,CancellationToken,Task> handle,int port=0)
    {
        _handle=handle;_listener=new(IPAddress.Loopback,port);_listener.Start();Port=((IPEndPoint)_listener.LocalEndpoint).Port;_accept=Accept();
    }
    private async Task Accept()
    {
        try { while(!_stop.IsCancellationRequested) { var peer=await _listener.AcceptTcpClientAsync(_stop.Token);_clients.Add(Serve(peer)); } }
        catch(OperationCanceledException) { }
        catch(SocketException) when(_stop.IsCancellationRequested) { }
    }
    private async Task Serve(TcpClient peer)
    {
        using(peer) { try { await _handle(peer.GetStream(),_stop.Token); } catch(OperationCanceledException) when(_stop.IsCancellationRequested) { } catch(IOException) { } }
    }
    public static int FreePort() { var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();return port; }
    public async ValueTask DisposeAsync() { _stop.Cancel();_listener.Stop();await _accept;await Task.WhenAll(_clients);_stop.Dispose(); }
}
