using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;
using System.Net.Sockets;
using Neo.Core;
using Neo.Core.Core;
using Neo.Core.Models;
using Neo.App.Services;
using CoreProcess = Neo.App.Services.CoreProcess;

var here = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../"));
if (!File.Exists(Path.Combine(here, "source-binding.json"))) throw new Exception("Missing source binding");
var isolated = Path.Combine(here, "isolated-data");
var runtime = Path.Combine(here, "isolated-runtime");
Directory.CreateDirectory(isolated); Directory.CreateDirectory(runtime);
foreach (var pair in new[] { ("_rootDir", here), ("_dataDir", isolated), ("_runtimeDir", runtime) })
    typeof(NeoPaths).GetField(pair.Item1, BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, pair.Item2);
if (NeoPaths.DataDir != isolated) throw new Exception("Isolation failed");
File.WriteAllText(Path.Combine(runtime, "xray.exe"), "never executed fixture");
File.WriteAllText(Path.Combine(runtime, "sing-box.exe"), "never executed fixture");
for (var port = 41000; port < 60000; port += 200)
    if (new[] { port, port + 1, port + 100, port + 101, port + 110, port + 111 }.All(Free)
        && LocalPorts.TryApply(port, port + 1) == null) break;
AppState.State.SocksPort = LocalPorts.Socks; AppState.State.HttpPort = LocalPorts.Http;
AppState.State.FailoverEnabled = true; AppState.State.GameDirectEnabled = false;
AppState.State.AutoSpeedTest = true;
var results = new List<object>();
var failed = 0;
void Check(string id, string name, bool passed, object? evidence = null)
{
    if (!passed) failed++;
    results.Add(new { id, name, passed, evidence });
    Console.WriteLine($"{id} {(passed ? "PASS" : "FAIL")}: {name}");
    File.WriteAllText(Path.Combine(here, "acceptance-results.json"), JsonSerializer.Serialize(new
        { capturedAt = DateTimeOffset.Now, failed, results }, new JsonSerializerOptions { WriteIndented = true }));
}
NodeItem Node(string name, string password = "synthetic-only") => new()
{
    Name = name, Protocol = NodeProtocol.Trojan, Server = "synthetic.invalid", Port = 443,
    Security = "tls", Sni = "synthetic.invalid", Password = password,
};
var a = Node("audit-A", "A"); var b = Node("audit-B", "B"); var c = Node("audit-C", "C");

var identityNode = Node("identity");
var identity = NodeIdentity.Fingerprint(identityNode);
identityNode.Name = "renamed"; identityNode.RealDelay = 123; identityNode.RealDelayNote = "display only";
Check("identity-display", "display and measurement changes preserve configuration identity",
    NodeIdentity.Fingerprint(identityNode) == identity);
var identityChanges = new Dictionary<string, Action<NodeItem>>
{
    ["password"] = n => n.Password = "changed", ["uuid"] = n => n.Uuid = "synthetic-uuid",
    ["sni"] = n => n.Sni = "changed.invalid", ["network"] = n => n.Network = "ws",
    ["path"] = n => n.Path = "/changed", ["allowInsecure"] = n => n.AllowInsecure = true,
    ["certificatePin"] = n => n.CertSha256 = new string('a', 64), ["extras"] = n => n.Extras["audit"] = "changed",
};
var invalidated = identityChanges.ToDictionary(x => x.Key, x =>
{
    var node = Node("identity"); x.Value(node); return NodeIdentity.Fingerprint(node) != identity;
});
Check("identity-policy", "authentication transport and TLS changes invalidate configuration identity",
    invalidated.Values.All(x => x), invalidated);

// Stale automatic decisions cannot acquire a newer session's authority.
ConnectionService.Reset(a, 10);
AppState.Nodes.Clear(); AppState.Nodes.AddRange(new[] { a, b });
var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var resume = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
RealDelayTester.RealPingHook = n => { entered.TrySetResult(); return resume.Task; };
var failover = (Task)typeof(FailoverService).GetMethod("FailoverAsync", BindingFlags.NonPublic | BindingFlags.Static)!
    .Invoke(null, new object[] { CancellationToken.None })!;
await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
ConnectionService.UserReconnected(c); resume.SetResult(100);
await failover.WaitAsync(TimeSpan.FromSeconds(5));
Check("D01", "stale probe preserves newer manual selection", ReferenceEquals(ConnectionService.ConnectedNode, c));

// An automatic TUN decision can advance only through its own failed attempts.
ConnectionService.Reset(a, 12);
AppState.State.UseTun = true;
AppState.Nodes.Clear(); AppState.Nodes.AddRange(new[] { a, b, c });
RealDelayTester.RealPingHook = _ => Task.FromResult(100);
ConnectionService.FullConnectPolicy = node => ReferenceEquals(node, c);
await (Task)typeof(FailoverService).GetMethod("FailoverAsync", BindingFlags.NonPublic | BindingFlags.Static)!
    .Invoke(null, new object[] { CancellationToken.None })!;
Check("D01-own-retry", "failed owned attempts retain authority for the next candidate",
    ConnectionService.ReconnectCalls == 2 && ReferenceEquals(ConnectionService.ConnectedNode, c));
ConnectionService.FullConnectPolicy = null; AppState.State.UseTun = false;

ConnectionService.Reset(a, 20);
var before = ConnectionService.SessionEpoch;
var fast = await ConnectionService.SwitchNodeFastAsync(b);
var config = ConnectionService.CurrentConfig;
Check("D03", "fast switch advances active revision", fast.Ok && ConnectionService.SessionEpoch == before + 1);
await Task.Delay(2400);
Check("D02", "retirement preserves active configuration", config != null && File.Exists(config) && ConnectionService.CorePid > 0);

// Pause exactly at the persistence/logging boundary of the real switch method.
ConnectionService.Reset(a, 30);
entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
using (var allowCommit = new ManualResetEventSlim())
{
    LogBus.Hook = message =>
    {
        if (!message.StartsWith("切换节点 → audit-B", StringComparison.Ordinal)) return;
        entered.TrySetResult();
        if (!allowCommit.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("scheduler barrier");
    };
    var switching = Task.Run(() => ConnectionService.SwitchNodeFastAsync(b));
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    ConnectionService.Disconnect("用户点击圆环断开"); allowCommit.Set();
    var result = await switching.WaitAsync(TimeSpan.FromSeconds(5));
    await ConnectionService.LastTunTeardown.WaitAsync(TimeSpan.FromSeconds(5));
    LogBus.Hook = null;
    Check("D04", "disconnect cannot be undone by switch continuation",
        !result.Ok && !ConnectionService.IsConnected && ConnectionService.CorePid == 0 && ConnectionService.ConnectedNode == null);
}

ConnectionService.Reset(a, 40); a.RealDelay = 0; c.RealDelay = 0;
entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
var calls = 0;
RealDelayTester.PingHook = (port, target) =>
{
    if (Interlocked.Increment(ref calls) == 1) { entered.TrySetResult(); return resume.Task; }
    return Task.FromResult(321);
};
var measurement = ConnectionService.Measure(a);
await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
ConnectionService.UserReconnected(c); resume.SetResult(100); await measurement;
Check("D05", "stale auto measurement is discarded", a.RealDelay == 0 && c.RealDelay == 0 && calls == 1);

var old = Node("policy", "old"); var fresh = Node("policy", "new"); fresh.Sni = "new.synthetic.invalid";
DelayCache.Set(old, 45, "证书放宽"); DelayCache.Apply(fresh); await ConnectionService.ApplyTls(fresh);
Check("D06a", "configuration changes invalidate measurement identity",
    AppState.NodeKey(old) != AppState.NodeKey(fresh) && fresh.RealDelay == 0 && !fresh.ForceInsecure);
fresh.RealDelayNote = "证书放宽 / 证书不受信"; await ConnectionService.ApplyTls(fresh);
var strictJson = SingBoxConfigBuilder.Build(fresh, LocalPorts.CoreSocks, LocalPorts.CoreHttp, "rule", true, true, false, false, null, "warning", "223.5.5.5");
Check("D06b", "display text never authorizes insecure TLS", !fresh.ForceInsecure && !HasInsecure(strictJson));
fresh.AllowInsecure = true;
Check("D06c", "explicit insecure policy is retained", HasInsecure(SingBoxConfigBuilder.Build(fresh, LocalPorts.CoreSocks, LocalPorts.CoreHttp, "rule", true, true, false, false, null, "warning", "223.5.5.5")));
Check("D06d", "automatic UDP probing obeys certificate policy", !DelayTestPlanner.PlanFor(new NodeItem
    { Protocol = NodeProtocol.Hysteria2, Server = "synthetic.invalid", Port = 443 }).RelaxTlsOnCertFailure);

// Check failures cannot be made successful by silently replacing routing/DNS/TLS intent.
foreach (var mode in new[] { "rule", "direct" })
{
    var input = JsonNode.Parse(XrayConfigBuilder.Build(b, LocalPorts.CoreSocks, LocalPorts.CoreHttp, mode,
        customRules: new List<RouteRule> { new() { Kind = "domain", Value = "blocked.audit.invalid", Action = "block" } }))!;
    input["auditInvalidFixture"] = true;
    Neo.App.Services.KernelCompat.CheckPolicy = json => !json.Contains("auditInvalidFixture", StringComparison.Ordinal);
    var rejected = false;
    try { await ConnectionService.Preflight(input.ToJsonString(), b, false); } catch (InvalidOperationException) { rejected = true; }
    Check("D07-" + mode, "rejected config preserves " + mode + " intent", rejected);
}
Neo.App.Services.KernelCompat.CheckPolicy = null;
const string chain = """{"outbounds":[{"streamSettings":{"sockopt":{"dialerProxy":"entry","tcpFastOpen":true}},"mux":{"enabled":true}}],"observatory":{"subjectSelector":["entry"]},"routing":{"balancers":[{"selector":["entry"]}]},"dns":{"servers":["synthetic.invalid"]}}""";
var safe = Neo.Core.Core.KernelCompat.StripOptionalFields(chain, KernelKind.Xray, out _);
Check("D07-chain", "optional stripping preserves chain and routing dependencies",
    safe.Contains("dialerProxy") && safe.Contains("observatory") && safe.Contains("balancers") && safe.Contains("synthetic.invalid") && !safe.Contains("tcpFastOpen"));

// Actual binary checks only, no listener or installed app mutation.
Neo.App.Services.KernelCompat.RealExecutablePath = args.FirstOrDefault() ?? "E:/Program Files/neo/app/runtime/xray.exe";
foreach (var mode in new[] { "rule", "direct" })
{
    var input = JsonNode.Parse(XrayConfigBuilder.Build(b, LocalPorts.CoreSocks, LocalPorts.CoreHttp, mode,
        customRules: new List<RouteRule> { new() { Kind = "domain", Value = "blocked.audit.invalid", Action = "block" },
            new() { Kind = "ip", Value = "192.0.2.1/999", Action = "block" } }))!;
    if (mode == "direct") input["inbounds"]!.AsArray()[0]!["protocol"] = "unsupported-synthetic-fixture";
    var rejected = false;
    try { await ConnectionService.Preflight(input.ToJsonString(), b, false); } catch (InvalidOperationException) { rejected = true; }
    Check("D07-real-" + mode, "actual Xray rejection never changes " + mode + " intent",
        rejected && Neo.App.Services.KernelCompat.ActualChecks.Count > 0
        && Neo.App.Services.KernelCompat.ActualChecks.All(x => !x.InfraFailed),
        Neo.App.Services.KernelCompat.ActualChecks.Select(x => new { x.Ok, x.InfraFailed }).ToArray());
    Neo.App.Services.KernelCompat.ActualChecks.Clear();
}
Neo.App.Services.KernelCompat.RealExecutablePath = null;

AppState.State.CoreMap.Clear(); AppState.State.CoreMap["Trojan"] = "sing-box"; AppState.SyncCoreMap();
long observations = 0, unexpected = 0;
using (var monitoring = new CancellationTokenSource())
{
    var monitor = Task.Factory.StartNew(() =>
    {
        while (!monitoring.IsCancellationRequested)
        {
            if (KernelRouter.Resolve("Trojan") != KernelRouter.Choice.SingBox) Interlocked.Increment(ref unexpected);
            Interlocked.Increment(ref observations);
        }
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    for (var i = 0; i < 300; i++) { AppState.State.WinX = i; AppState.Save(); AppState.SyncCoreMap(); }
    monitoring.Cancel(); await monitor;
}
Check("D08a", "save and atomic map publication expose no partial map", observations > 0 && unexpected == 0, new { observations, unexpected });
var appliedSocks = LocalPorts.Socks;
AppState.State.SocksPort = appliedSocks + 200; AppState.State.HttpPort = LocalPorts.Http + 200; AppState.Save();
Check("D08b", "saving desired ports leaves active listeners unchanged", LocalPorts.Socks == appliedSocks);
AppState.State.SocksPort = appliedSocks; AppState.State.HttpPort = LocalPorts.Http;
AppState.State.CoreMap.Clear(); AppState.SyncCoreMap();

// All-refresh deletion and single-refresh enable/re-enable invalidate pending responses.
foreach (var single in new[] { false, true })
{
    AppState.State.Subscriptions.Clear();
    AppState.State.Subscriptions.Add(new() { Id = "a", Name = "a", Url = "https://synthetic.invalid/a", Enabled = true });
    AppState.State.Subscriptions.Add(new() { Id = "b", Name = "b", Url = "https://synthetic.invalid/b", Enabled = true });
    var path = Path.Combine(isolated, "subscription-cache.json");
    File.WriteAllText(path, "{\"a\":\"old-a\",\"b\":\"old-b\"}");
    entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    var fetched = new List<string>();
    var fetchResume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Neo.Core.Parsing.SubscriptionFetcher.FetchHook = async url =>
    {
        fetched.Add(url); entered.TrySetResult(); await fetchResume.Task;
        return new Neo.Core.Parsing.SubscriptionFetchResult { RawText = "new-synthetic-cache", Nodes = new() { Node("sub") } };
    };
    var refresh = single ? AppState.RefreshOneAsync("a") : AppState.RefreshSubscriptionAsync();
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    AppState.RemoveSubscriptionById(single ? "a" : "b");
    fetchResume.SetResult(); await refresh;
    var deleted = single ? "a" : "b";
    Check(single ? "D09-single" : "D09-all", "deleted subscription cache cannot be resurrected",
        !JsonNode.Parse(File.ReadAllText(path))!.AsObject().ContainsKey(deleted)
        && !AppState.State.Subscriptions.Any(s => s.Id == deleted));
}
AppState.State.Subscriptions.Clear();
AppState.State.Subscriptions.Add(new() { Id = "toggle", Name = "toggle", Url = "https://synthetic.invalid/toggle", Enabled = true });
entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
var toggleResume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
Neo.Core.Parsing.SubscriptionFetcher.FetchHook = async _ =>
{ entered.TrySetResult(); await toggleResume.Task; return new() { RawText = "stale-toggle" }; };
var toggling = AppState.RefreshOneAsync("toggle"); await entered.Task;
AppState.SetSubscriptionEnabled("toggle", false); AppState.SetSubscriptionEnabled("toggle", true);
toggleResume.SetResult();
Check("D09-toggle", "disable/re-enable invalidates an in-flight response", !(await toggling).Ok);

// Full production proxy code with memory registry aliases and a controlled WinINet boundary.
var memory = Neo.DeepAudit.MemoryRegistry.CurrentUser;
var backupPath = Path.Combine(isolated, "proxy-backup.json");
if (File.Exists(backupPath)) File.Delete(backupPath);
void OriginalProxy()
{
    ProxyNativeBoundary.Flags = 15; // DIRECT + manual proxy + PAC + auto-detect.
    ProxyNativeBoundary.RejectApply = false;
    ProxyNativeBoundary.RejectRead = false;
    ProxyNativeBoundary.IgnoreApply = false;
    ProxyNativeBoundary.AlteredServer = null;
    ProxyNativeBoundary.RejectApplyCount = 0;
    ProxyNativeBoundary.ForeignOwnerAlive = false;
    ProxyNativeBoundary.Server = "old.synthetic.invalid:8080";
    ProxyNativeBoundary.Bypass = "old-bypass";
    ProxyNativeBoundary.Pac = "https://old.synthetic.invalid/pac";
    memory.Values.Clear(); memory.Values["ProxyEnable"] = 1;
    memory.Values["ProxyServer"] = "old.synthetic.invalid:8080";
    memory.Values["ProxyOverride"] = "old-bypass"; memory.Values["AutoConfigURL"] = "https://old.synthetic.invalid/pac";
}
OriginalProxy();
Check("D10-enable", "proxy takeover persists a lease", ProxyOwnershipUnderTest.Enable(LocalProxySettings.Server));
memory.Values["ProxyServer"] = "127.0.0.1:49000";
memory.Values["AutoConfigURL"] = "https://foreign.synthetic.invalid/pac";
Check("D10-foreign", "foreign proxy and PAC survive disconnect", !ProxyOwnershipUnderTest.Disable()
    && memory.GetValue("ProxyServer") as string == "127.0.0.1:49000"
    && memory.GetValue("AutoConfigURL") as string == "https://foreign.synthetic.invalid/pac");
OriginalProxy(); ProxyOwnershipUnderTest.Enable(LocalProxySettings.Server);
Check("D10-restore", "owned lease restores all original settings", ProxyOwnershipUnderTest.Disable()
    && memory.GetValue("ProxyServer") as string == "old.synthetic.invalid:8080"
    && memory.GetValue("ProxyOverride") as string == "old-bypass"
    && memory.GetValue("AutoConfigURL") as string == "https://old.synthetic.invalid/pac"
    && ProxyNativeBoundary.Flags == 15);
Check("D10-repeat", "repeated disable cannot mutate an unowned proxy", !ProxyOwnershipUnderTest.Disable());

OriginalProxy(); ProxyOwnershipUnderTest.Enable(LocalProxySettings.Server);
memory.Values["ProxyEnable"] = 0;
Check("D10-repair", "enable-only repair retains the original lease",
    ProxyOwnershipUnderTest.Enable(LocalProxySettings.Server, repair: true)
    && ProxyOwnershipUnderTest.Disable()
    && memory.GetValue("ProxyServer") as string == "old.synthetic.invalid:8080");
OriginalProxy(); ProxyOwnershipUnderTest.Enable(LocalProxySettings.Server);
memory.Values["ProxyEnable"] = 0; memory.Values["AutoConfigURL"] = "https://foreign.synthetic.invalid/pac";
Check("D10-repair-foreign", "automatic repair preserves a foreign PAC",
    !ProxyOwnershipUnderTest.Enable(LocalProxySettings.Server, repair: true)
    && memory.GetValue("AutoConfigURL") as string == "https://foreign.synthetic.invalid/pac");
ProxyOwnershipUnderTest.Disable();
OriginalProxy();
var injected = false;
memory.WriteHook = key => { if (key == "ProxyOverride" && !injected) { injected = true; throw new IOException("synthetic partial takeover"); } };
var partialTakeover = ProxyOwnershipUnderTest.Enable(LocalProxySettings.Server);
memory.WriteHook = null;
Check("D10-partial-enable", "partial takeover can restore the original snapshot",
    !partialTakeover && injected && ProxyOwnershipUnderTest.Disable()
    && memory.GetValue("ProxyServer") as string == "old.synthetic.invalid:8080");
OriginalProxy(); ProxyOwnershipUnderTest.Enable(LocalProxySettings.Server);
injected = false;
memory.WriteHook = key => { if (key == "ProxyOverride" && !injected) { injected = true; throw new IOException("synthetic partial restore"); } };
var partialRestore = ProxyOwnershipUnderTest.Disable(); memory.WriteHook = null;
Check("D10-partial-restore", "interrupted restoration retains a retryable lease",
    !partialRestore && injected && ProxyOwnershipUnderTest.Disable()
    && memory.GetValue("ProxyOverride") as string == "old-bypass");
OriginalProxy();
for (var i = 0; i < 100; i++) ProxyOwnershipUnderTest.Enable(LocalProxySettings.Server);
using (var lease = JsonDocument.Parse(File.ReadAllText(backupPath)))
    Check("D10-bounded", "repeated repair does not grow lease history", lease.RootElement.GetProperty("OwnedStates").GetArrayLength() <= 5);
ProxyOwnershipUnderTest.Disable();

OriginalProxy(); ProxyNativeBoundary.Flags = 1;
Check("D10-effective-enable", "registry-enabled but Windows-DIRECT state becomes an effective proxy",
    ProxyOwnershipUnderTest.Enable(LocalProxySettings.Server) && ProxyNativeBoundary.Flags == 3);
Check("D10-original-flags", "disconnect restores the saved Windows connection flags",
    ProxyOwnershipUnderTest.Disable() && ProxyNativeBoundary.Flags == 1);
OriginalProxy(); ProxyNativeBoundary.RejectApply = true;
var nativeEnable = ProxyOwnershipUnderTest.Enable(LocalProxySettings.Server);
ProxyNativeBoundary.RejectApply = false;
Check("D10-native-enable-failure", "native takeover failure is reported and leaves a recoverable lease",
    !nativeEnable && File.Exists(backupPath) && ProxyOwnershipUnderTest.Disable()
    && ProxyNativeBoundary.Flags == 15);
OriginalProxy(); ProxyOwnershipUnderTest.Enable(LocalProxySettings.Server);
ProxyNativeBoundary.RejectApply = true;
var nativeRestore = ProxyOwnershipUnderTest.Disable();
var retainedNativeLease = File.Exists(backupPath);
ProxyNativeBoundary.RejectApply = false;
Check("D10-native-restore-retry", "failed native restoration retains its lease until Windows accepts the retry",
    !nativeRestore && retainedNativeLease && ProxyOwnershipUnderTest.Disable()
    && !File.Exists(backupPath) && ProxyNativeBoundary.Flags == 15);

OriginalProxy(); ProxyOwnershipUnderTest.Enable(LocalProxySettings.Server);
ProxyNativeBoundary.Flags = 1;
Check("D10-effective-drift", "an enabled registry cannot hide an effective Windows DIRECT configuration",
    ProxyOwnershipUnderTest.IsEnabled && !ProxyOwnershipUnderTest.IsEffective);
Check("D10-effective-repair", "native-only DIRECT drift can be repaired without replacing the original lease",
    ProxyOwnershipUnderTest.Enable(LocalProxySettings.Server, repair: true) && ProxyOwnershipUnderTest.IsEffective
    && ProxyOwnershipUnderTest.Disable() && ProxyNativeBoundary.Flags == 15);

OriginalProxy(); ProxyOwnershipUnderTest.Enable(LocalProxySettings.Server);
ProxyNativeBoundary.Server = "http=foreign.synthetic.invalid:7890;https=foreign.synthetic.invalid:7890";
Check("D10-native-foreign", "native-only foreign endpoint changes survive automatic repair and disconnect",
    !ProxyOwnershipUnderTest.IsEffective && !ProxyOwnershipUnderTest.Enable(LocalProxySettings.Server, repair: true)
    && !ProxyOwnershipUnderTest.Disable() && ProxyNativeBoundary.Server.Contains("foreign.synthetic.invalid"));

OriginalProxy(); ProxyOwnershipUnderTest.Enable(LocalProxySettings.Server);
ProxyNativeBoundary.Flags |= 4; ProxyNativeBoundary.Pac = "https://foreign.synthetic.invalid/pac";
Check("D10-native-pac", "native-only PAC takeover is detected and left untouched",
    !ProxyOwnershipUnderTest.IsEffective && !ProxyOwnershipUnderTest.Enable(LocalProxySettings.Server, repair: true)
    && !ProxyOwnershipUnderTest.Disable() && ProxyNativeBoundary.Pac == "https://foreign.synthetic.invalid/pac");

OriginalProxy(); ProxyOwnershipUnderTest.Enable(LocalProxySettings.Server);
ProxyNativeBoundary.Bypass = "*";
Check("D10-native-bypass", "a native bypass-all change cannot masquerade as a working system proxy",
    !ProxyOwnershipUnderTest.IsEffective && !ProxyOwnershipUnderTest.Enable(LocalProxySettings.Server, repair: true)
    && !ProxyOwnershipUnderTest.Disable() && ProxyNativeBoundary.Bypass == "*");

OriginalProxy(); ProxyNativeBoundary.AlteredServer = "foreign.synthetic.invalid:7890";
Check("D10-native-endpoint-validation", "native apply success and correct flags cannot hide a wrong effective endpoint",
    !ProxyOwnershipUnderTest.Enable(LocalProxySettings.Server) && !ProxyOwnershipUnderTest.IsEffective);
ProxyOwnershipUnderTest.Disable();

OriginalProxy(); ProxyNativeBoundary.RejectRead = true;
Check("D10-native-query-failure", "failure to read Windows ownership performs no takeover writes",
    !ProxyOwnershipUnderTest.Enable(LocalProxySettings.Server) && !File.Exists(backupPath)
    && memory.GetValue("ProxyServer") as string == "old.synthetic.invalid:8080");
ProxyNativeBoundary.RejectRead = false;

OriginalProxy(); ProxyOwnershipUnderTest.Enable(LocalProxySettings.Server);
var legacyLease = JsonNode.Parse(File.ReadAllText(backupPath))!.AsObject();
legacyLease.Remove("NativeOriginal"); legacyLease.Remove("NativeOwnedStates"); legacyLease.Remove("ConnectionFlags");
File.WriteAllText(backupPath, legacyLease.ToJsonString());
Check("D10-legacy-native-lease", "older leases without native snapshots restore their saved manual proxy and PAC",
    ProxyOwnershipUnderTest.Disable() && ProxyNativeBoundary.Flags == 7
    && ProxyNativeBoundary.Pac == "https://old.synthetic.invalid/pac");

OriginalProxy(); ProxyOwnershipUnderTest.Enable(LocalProxySettings.Server);
ProxyNativeBoundary.RejectApplyCount = 1;
Check("D10-disconnect-retry", "transient Windows restoration failure recovers before listener teardown",
    await ProxyOwnershipUnderTest.RestoreAfterDisconnectAsync()
    && !File.Exists(backupPath) && ProxyNativeBoundary.Flags == 15);
OriginalProxy(); ProxyOwnershipUnderTest.Enable(LocalProxySettings.Server);
ProxyNativeBoundary.RejectApply = true;
var attemptsBefore = ProxyNativeBoundary.ApplyAttempts;
var restoreFailed = await ProxyOwnershipUnderTest.RestoreAfterDisconnectAsync();
Check("D10-disconnect-persistent", "persistent restoration failure is reported and keeps its recoverable snapshot",
    !restoreFailed && File.Exists(backupPath) && ProxyNativeBoundary.ApplyAttempts - attemptsBefore == 3);
ProxyNativeBoundary.RejectApply = false; ProxyOwnershipUnderTest.Disable();

// Exact production orphan cleanup: mismatched registry/WinINet, no live Windows settings changed.
void Orphan(int enabled, string registryServer, uint flags, string nativeServer)
{
    OriginalProxy(); File.Delete(backupPath);
    memory.Values["ProxyEnable"] = enabled; memory.Values["ProxyServer"] = registryServer;
    memory.Values.Remove("AutoConfigURL");
    ProxyNativeBoundary.Flags = flags; ProxyNativeBoundary.Server = nativeServer; ProxyNativeBoundary.Pac = null;
}
var ownedServer = LocalProxySettings.Server;
Orphan(0, "", 3, ownedServer);
Check("exit-native-only", "dead effective Windows proxy is cleared even when registry says disabled",
    ProxyOwnershipUnderTest.CleanupOrphaned(LocalPorts.Socks, LocalPorts.Http)
    && ProxyNativeBoundary.Flags == 1 && !ProxyOwnershipUnderTest.IsEnabled);
Orphan(1, ownedServer, 1, "");
Check("exit-registry-only", "dead registry proxy is cleared when effective Windows mode already differs",
    ProxyOwnershipUnderTest.CleanupOrphaned(LocalPorts.Socks, LocalPorts.Http)
    && ProxyNativeBoundary.Flags == 1 && !ProxyOwnershipUnderTest.IsEnabled);
Orphan(0, "foreign-inactive.synthetic.invalid:8080", 3, ownedServer);
Check("exit-inactive-foreign", "inactive saved foreign endpoint survives clearing only Neo's active native proxy",
    ProxyOwnershipUnderTest.CleanupOrphaned(LocalPorts.Socks, LocalPorts.Http)
    && memory.GetValue("ProxyServer") as string == "foreign-inactive.synthetic.invalid:8080"
    && ProxyNativeBoundary.Flags == 1);
Orphan(1, "foreign.synthetic.invalid:8080", 3, ownedServer);
Check("exit-active-foreign-registry", "an active foreign registry proxy is not overwritten by native orphan cleanup",
    !ProxyOwnershipUnderTest.CleanupOrphaned(LocalPorts.Socks, LocalPorts.Http)
    && memory.GetValue("ProxyServer") as string == "foreign.synthetic.invalid:8080" && ProxyNativeBoundary.Flags == 3);
Orphan(1, ownedServer, 3, "foreign.synthetic.invalid:8080");
Check("exit-active-foreign-native", "an active foreign native proxy is not overwritten by registry orphan cleanup",
    !ProxyOwnershipUnderTest.CleanupOrphaned(LocalPorts.Socks, LocalPorts.Http)
    && ProxyNativeBoundary.Server == "foreign.synthetic.invalid:8080" && ProxyNativeBoundary.Flags == 3);
Orphan(0, "", 5, ownedServer); ProxyNativeBoundary.Pac = "https://foreign.synthetic.invalid/pac";
Check("exit-native-pac", "a native PAC remains intact even when registry PAC is missing",
    !ProxyOwnershipUnderTest.CleanupOrphaned(LocalPorts.Socks, LocalPorts.Http)
    && ProxyNativeBoundary.Pac == "https://foreign.synthetic.invalid/pac" && ProxyNativeBoundary.Flags == 5);
Orphan(1, ownedServer, 3, ownedServer);
var liveHttp = new TcpListener(IPAddress.Loopback, LocalPorts.Http); liveHttp.Start();
try
{
    Check("exit-second-endpoint-live", "a live second HTTP endpoint prevents cleanup even when first SOCKS endpoint is dead",
        !ProxyOwnershipUnderTest.CleanupOrphaned(LocalPorts.Socks, LocalPorts.Http)
        && ProxyNativeBoundary.Flags == 3 && ProxyOwnershipUnderTest.IsEnabled);
}
finally { liveHttp.Stop(); }
Check("exit-all-endpoints-dead", "all dead public endpoints permit verified native and registry cleanup",
    ProxyOwnershipUnderTest.CleanupOrphaned(LocalPorts.Socks, LocalPorts.Http)
    && ProxyNativeBoundary.Flags == 1 && !ProxyOwnershipUnderTest.IsEnabled);
if (Socket.OSSupportsIPv6)
{
    var ipv6Server = $"[::1]:{LocalPorts.Http}"; Orphan(0, "", 3, ipv6Server);
    var liveIpv6 = new TcpListener(IPAddress.IPv6Loopback, LocalPorts.Http); liveIpv6.Server.DualMode = false; liveIpv6.Start();
    try
    {
        Check("exit-ipv6-live", "an IPv6-only listener protects its effective proxy ownership",
            !ProxyOwnershipUnderTest.CleanupOrphaned(LocalPorts.Socks, LocalPorts.Http) && ProxyNativeBoundary.Flags == 3);
    }
    finally { liveIpv6.Stop(); }
    Check("exit-ipv6-dead", "dead IPv6-loopback proxy is also removed from effective Windows settings",
        ProxyOwnershipUnderTest.CleanupOrphaned(LocalPorts.Socks, LocalPorts.Http) && ProxyNativeBoundary.Flags == 1);
}

OriginalProxy(); ProxyOwnershipUnderTest.Enable(LocalProxySettings.Server);
var successorLease = JsonNode.Parse(File.ReadAllText(backupPath))!.AsObject();
successorLease["OwnerPid"] = 1000001; File.WriteAllText(backupPath, successorLease.ToJsonString());
ProxyNativeBoundary.ForeignOwnerAlive = true;
Check("D10-live-lease-owner", "an old process cannot repair or restore a live successor's proxy lease",
    !ProxyOwnershipUnderTest.Enable(LocalProxySettings.Server, repair: true)
    && !ProxyOwnershipUnderTest.Disable() && File.Exists(backupPath) && ProxyNativeBoundary.Flags == 3);
ProxyNativeBoundary.ForeignOwnerAlive = false;
Check("D10-dead-lease-owner", "an exited lease owner does not block crash recovery",
    ProxyOwnershipUnderTest.Disable() && !File.Exists(backupPath) && ProxyNativeBoundary.Flags == 15);
OriginalProxy(); ProxyOwnershipUnderTest.Enable(LocalProxySettings.Server);
successorLease = JsonNode.Parse(File.ReadAllText(backupPath))!.AsObject();
successorLease["OwnerPid"] = 1000001; File.WriteAllText(backupPath, successorLease.ToJsonString());
ProxyNativeBoundary.Flags = 1;
var adoptedLease = ProxyOwnershipUnderTest.Enable(LocalProxySettings.Server, repair: true);
Check("D10-lease-adoption", "repair adopts an exited owner's lease without losing original restoration settings",
    adoptedLease && JsonNode.Parse(File.ReadAllText(backupPath))!["OwnerPid"]!.GetValue<int>() == Environment.ProcessId
    && ProxyOwnershipUnderTest.Disable() && ProxyNativeBoundary.Flags == 15);

CoreState.Current = new() { NeoPid = 20, CorePid = 1000000, State = "running" };
GuardUnderTest.SuccessorAlive = true; GuardUnderTest.TunCleanups = 0;
ConnectionService.OrphanCleanupCalls = SystemProxy.CleanupCalls = 0;
SystemProxy.RestoreCalls = 0;
GuardUnderTest.Cleanup();
Check("guard-successor", "an old guard leaves a live successor's cores, proxy and TUN untouched",
    GuardUnderTest.Defer() && ConnectionService.OrphanCleanupCalls == 0
    && SystemProxy.CleanupCalls == 0 && SystemProxy.RestoreCalls == 0 && GuardUnderTest.TunCleanups == 0);
Check("guard-snapshot-owner", "an old guard never attaches to a successor's recorded core",
    GuardUnderTest.Sync(CoreState.Current) == 0);
CoreState.Current = new() { NeoPid = 10, State = "idle" };
ConnectionService.OrphanCleanupHook = () => CoreState.Current = new() { NeoPid = 20, State = "connecting" };
GuardUnderTest.Cleanup();
Check("guard-successor-during-cleanup", "a successor appearing during cleanup prevents later network restoration",
    ConnectionService.OrphanCleanupCalls == 1 && SystemProxy.CleanupCalls == 0 && GuardUnderTest.TunCleanups == 0);
ConnectionService.OrphanCleanupHook = null; GuardUnderTest.SuccessorAlive = false;
GuardUnderTest.Cleanup();
Check("guard-dead-successor", "a stale snapshot of an exited successor does not prevent orphan recovery",
    !GuardUnderTest.Defer() && SystemProxy.CleanupCalls == 1 && GuardUnderTest.TunCleanups == 1);
Check("guard-restore-retry", "guard gives an exited owner's saved lease another bounded restoration window",
    SystemProxy.RestoreCalls == 1);
CoreState.Current = null;
ConnectionService.Reset(a, 49);
Check("cleanup-active-core", "the current live kernel is never classified as an orphan",
    ConnectionService.IsProtectedCore(ConnectionService.CorePid));

// Full production statistics chain samples both active and retiring instances exactly once.
ConnectionService.Reset(a, 50);
var oldStats = LocalPorts.CoreStatsApi; var newStats = LocalPorts.SlotStats(1 - LocalPorts.ActiveSlot);
XrayStats.Counters[oldStats] = new(1000, 1000); XrayStats.Counters[newStats] = new(0, 0);
PortRelay.ActiveHook = _ => 1;
CoreStats.Query(); var totalBefore = UsageTracker.TotalBytes;
fast = await ConnectionService.SwitchNodeFastAsync(b);
XrayStats.Counters[oldStats] = new(51000, 51000);
StatsSampler.Start(); await WaitUntil(() => UsageTracker.TotalBytes >= totalBefore + 100000);
StatsSampler.Stop(); CoreStats.Query();
Check("D12a", "retiring stream traffic is counted once", fast.Ok && UsageTracker.TotalBytes - totalBefore == 100000,
    new { addedBytes = UsageTracker.TotalBytes - totalBefore });
XrayStats.Counters[oldStats] = new(51500, 51500);
ConnectionService.Disconnect("final accounting"); await ConnectionService.LastTunTeardown;
Check("D12b", "shutdown settles the final unsampled traffic", UsageTracker.TotalBytes - totalBefore == 101000,
    new { addedBytes = UsageTracker.TotalBytes - totalBefore });
PortRelay.ActiveHook = null; XrayStats.Counters.Clear();

// TUN destruction and creation share the actual production semaphore; no real adapter is touched.
ConnectionService.Reset(a, 60); ConnectionService.MarkTun();
entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
var tunResume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
ConnectionService.TunHook = () => { entered.TrySetResult(); return tunResume.Task; };
ConnectionService.Disconnect("TUN fixture"); await entered.Task;
AppState.State.AutoSpeedTest = false; AppState.State.UseRustConfig = false;
var nextCreation = ConnectionService.RunFullCreation(b);
await Task.Delay(5200);
Check("TUN-lifecycle", "cleanup exceeding five seconds still blocks new creation", !nextCreation.IsCompleted);
tunResume.SetResult(); var createdAfterCleanup = await nextCreation.WaitAsync(TimeSpan.FromSeconds(5)); ConnectionService.TunHook = null;
Check("TUN-next-created", "full connection starts only after prior owner cleanup finishes", createdAfterCleanup.Ok && ConnectionService.IsConnected);

var plan = TunCleanupPlan.Build(Guid.Parse("b4ff7243-43b5-495a-b8fa-063f98b0b572"));
File.WriteAllText(Path.Combine(here, "tun-cleanup-plan.ps1"), plan);
Check("TUN-identity", "OS cleanup plan targets captured GUID and interface routes",
    plan.Contains("b4ff7243-43b5-495a-b8fa-063f98b0b572") && plan.Contains("-InterfaceIndex $owned.InterfaceIndex")
    && plan.Contains("/remove-device $owned.PNPDeviceID") && !plan.Contains("NextHop") && !plan.Contains("-Name 'neo-tun'"));
ConnectionService.Reset(a, 70); await ConnectionService.HoldLifecycle();
Neo.App.App.IsShuttingDown = true;
var exiting = Task.Run(() => ConnectionService.Disconnect("shutdown fixture"));
await Task.Delay(100);
Check("shutdown-join", "process exit waits for owner cleanup", !exiting.IsCompleted);
ConnectionService.ReleaseLifecycle(); await exiting.WaitAsync(TimeSpan.FromSeconds(5)); Neo.App.App.IsShuttingDown = false;

OrderedCoreStateUnderTest.Report("running", 1001, "xray", "current", sessionEpoch: 80);
OrderedCoreStateUnderTest.Report("idle", 0, "xray", "stale disconnect", sessionEpoch: 79);
Check("state-order", "late status writes cannot overwrite newer session state",
    OrderedCoreStateUnderTest.Read() is { State: "running", SessionEpoch: 80, CorePid: 1001 });

// Execute the full current production connection method with OS/process/UI boundaries isolated.
ConnectionService.Disconnect("full creation fixture"); await ConnectionService.LastTunTeardown;
AppState.State.UseTun = false; AppState.State.FixedExitEnabled = false; AppState.State.UseRustConfig = false;
AppState.State.AutoSpeedTest = false;
var startsBefore = CoreProcess.Starts;
var fullCreated = await ConnectionService.RunFullCreation(b);
Check("full-connect", "full connection commits owned process config proxy and node",
    fullCreated.Ok && CoreProcess.Starts == startsBefore + 1 && ConnectionService.IsConnected
    && ReferenceEquals(ConnectionService.ConnectedNode, b) && SystemProxy.IsEnabled
    && ConnectionService.CurrentConfig is { } fullConfig && File.Exists(fullConfig));

LogBus.Hook = message => { if (message.StartsWith("切换节点 → audit-C", StringComparison.Ordinal)) ConnectionService.Disconnect("reentrant disconnect"); };
var reentrantFull = await ConnectionService.RunFullCreation(c);
LogBus.Hook = null; await ConnectionService.LastTunTeardown;
Check("full-disconnect", "full connection continuation cannot revive a reentrant disconnect",
    !reentrantFull.Ok && !ConnectionService.IsConnected && ConnectionService.CorePid == 0 && !SystemProxy.IsEnabled);

var retryIntent = new SessionIntent(ConnectionService.SessionEpoch);
startsBefore = CoreProcess.Starts;
Neo.App.Services.KernelCompat.CheckPolicy = _ => false;
var fullRejected = await ConnectionService.RunFullCreation(a, intent: retryIntent);
await ConnectionService.LastTunTeardown;
Check("full-preflight-reject", "full preflight rejection starts no process and releases session gates",
    !fullRejected.Ok && CoreProcess.Starts == startsBefore && !ConnectionService.IsConnected
    && !ConnectionService.IsSessionBusy && retryIntent.Epoch == ConnectionService.SessionEpoch);
Neo.App.Services.KernelCompat.CheckPolicy = null;
var fullRetried = await ConnectionService.RunFullCreation(b, intent: retryIntent);
Check("full-owned-retry", "an owned failed full attempt can proceed to its next candidate",
    fullRetried.Ok && retryIntent.Epoch == ConnectionService.SessionEpoch && ReferenceEquals(ConnectionService.ConnectedNode, b));
ConnectionService.Disconnect("retry fixture end"); await ConnectionService.LastTunTeardown;

SystemProxy.RejectEnable = true;
var proxyRejected = await ConnectionService.RunFullCreation(c);
SystemProxy.RejectEnable = false; await ConnectionService.LastTunTeardown;
Check("full-proxy-reject", "proxy acquisition failure tears down the created process and config",
    !proxyRejected.Ok && !ConnectionService.IsConnected && ConnectionService.CorePid == 0
    && !ConnectionService.IsSessionBusy && !SystemProxy.IsEnabled && !File.Exists(ConnectionService.ConfigPath));

startsBefore = CoreProcess.Starts;
entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
using (var releasePreflight = new ManualResetEventSlim())
{
    Neo.App.Services.KernelCompat.CheckPolicy = _ =>
    {
        entered.TrySetResult();
        if (!releasePreflight.Wait(TimeSpan.FromSeconds(6))) throw new TimeoutException("full preflight barrier");
        return true;
    };
    var staleFull = ConnectionService.RunFullCreation(a);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    ConnectionService.Disconnect("manual disconnect during full preflight"); releasePreflight.Set();
    var staleFullResult = await staleFull.WaitAsync(TimeSpan.FromSeconds(5));
    Neo.App.Services.KernelCompat.CheckPolicy = null;
    await ConnectionService.LastTunTeardown;
    Check("full-stale", "disconnect during full preflight prevents later process creation",
        !staleFullResult.Ok && CoreProcess.Starts == startsBefore && !ConnectionService.IsConnected && !SystemProxy.IsEnabled);
}

foreach (var manualDisconnect in new[] {false,true})
{
    var handshakeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    CoreProcess.BeforeHandshake = async token => { handshakeEntered.TrySetResult(); await Task.Delay(Timeout.Infinite,token); };
    using var startupCancel = new CancellationTokenSource();
    try
    {
        var waitingFull = ConnectionService.RunFullCreation(a, ct: startupCancel.Token);
        await handshakeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (manualDisconnect) ConnectionService.Disconnect("manual disconnect during readiness");
        else startupCancel.Cancel();
        var waitingResult = await waitingFull.WaitAsync(TimeSpan.FromSeconds(5));
        await ConnectionService.LastTunTeardown;
        Check(manualDisconnect ? "full-readiness-stale" : "full-readiness-cancel",
            manualDisconnect ? "disconnect during readiness never publishes a proxy" : "canceled readiness releases created resources and gates",
            !waitingResult.Ok && !ConnectionService.IsConnected && ConnectionService.CorePid == 0
            && !ConnectionService.IsSessionBusy && !SystemProxy.IsEnabled && !File.Exists(ConnectionService.ConfigPath));
    }
    finally { CoreProcess.BeforeHandshake = null; }
}

AppState.State.UseTun = true; ConnectionService.ActiveTunResidue = true;
startsBefore = CoreProcess.Starts;
var residueRejected = await ConnectionService.RunFullCreation(b);
ConnectionService.ActiveTunResidue = false; AppState.State.UseTun = false;
await ConnectionService.LastTunTeardown;
Check("full-tun-residue", "full connection rejects still-active TUN residue before starting a core",
    !residueRejected.Ok && CoreProcess.Starts == startsBefore && !ConnectionService.IsConnected);

var bindLog = "FATAL start inbound/socks[socks-in]: listen tcp 127.0.0.1:21918: bind: An attempt was made to access a socket in a way forbidden by its access permissions.";
Check("bind-error-identity", "recognizes local listener errors without treating remote failures as port collisions",
    Neo.Core.Core.KernelCompat.ListenerBindFailurePort(bindLog) == 21918
    && Neo.Core.Core.KernelCompat.ListenerBindFailurePort("dial tcp 127.0.0.1:21918: connect: refused") == null
    && Neo.Core.Core.KernelCompat.ListenerBindFailurePort("unknown field listen_port") == null);
AppState.State.UseTun = true;
ConnectionService.Reset(a, 115);
var failedSlot = LocalPorts.ActiveSlot; var bindAttempts = 0;
CoreProcess.ListenerFailure = port => ++bindAttempts == 1
    ? $"FATAL start inbound/socks[socks-in]: listen tcp 127.0.0.1:{port}: bind: forbidden by its access permissions." : null;
startsBefore = CoreProcess.Starts;
var recoveredBind = await ConnectionService.RunPortRecovery(b, force:true);
Check("tun-bind-recovery", "a TUN bind failure retries one alternate local slot with the same selected node",
    recoveredBind.Ok && CoreProcess.Starts == startsBefore + 3 && LocalPorts.ActiveSlot == 1 - failedSlot
    && ReferenceEquals(ConnectionService.ConnectedNode, b) && ConnectionService.IsTunSession && SystemProxy.IsEnabled
    && ConnectionService.CoreKind == "xray", new { recoveredBind.Ok, recoveredBind.Error,
        starts = CoreProcess.Starts - startsBefore, LocalPorts.ActiveSlot, failedSlot,
        ConnectionService.IsTunSession, SystemProxy.IsEnabled, ConnectionService.CoreKind,
        tail = LogBus.Messages.TakeLast(8).ToArray() });
ConnectionService.Disconnect("bind recovery complete"); await ConnectionService.LastTunTeardown;
CoreProcess.ListenerFailure = port => $"FATAL listen tcp 127.0.0.1:{port}: bind: forbidden by its access permissions.";
startsBefore = CoreProcess.Starts;
var bothSlotsFailed = await ConnectionService.RunPortRecovery(b, force:true);
await ConnectionService.LastTunTeardown;
Check("tun-bind-bounded", "two failing slots stop without an active core, proxy lease, or retry loop",
    !bothSlotsFailed.Ok && CoreProcess.Starts == startsBefore + 2 && !ConnectionService.IsConnected
    && !SystemProxy.IsEnabled && !ConnectionService.IsSessionBusy, new { bothSlotsFailed.Ok, bothSlotsFailed.Error,
        starts = CoreProcess.Starts - startsBefore, ConnectionService.IsConnected, SystemProxy.IsEnabled,
        tail = LogBus.Messages.TakeLast(8).ToArray() });
LogBus.Hook = message => { if (message.StartsWith("本地监听端口绑定失败，改用", StringComparison.Ordinal)) ConnectionService.Disconnect("manual cancel before bind retry"); };
startsBefore = CoreProcess.Starts;
var cancelledBindRetry = await ConnectionService.RunPortRecovery(b, force:true);
await ConnectionService.LastTunTeardown; LogBus.Hook = null;
Check("tun-bind-cancel", "manual disconnect between attempts cancels recovery without reconnecting",
    !cancelledBindRetry.Ok && CoreProcess.Starts == startsBefore + 1 && !ConnectionService.IsConnected);
CoreProcess.ListenerFailure = _ => "FATAL unknown field broken_config";
startsBefore = CoreProcess.Starts;
var configFailure = await ConnectionService.RunPortRecovery(b, force:true);
await ConnectionService.LastTunTeardown;
Check("tun-bind-config-boundary", "configuration errors do not trigger alternate-port recovery",
    !configFailure.Ok && CoreProcess.Starts == startsBefore + 1 && !ConnectionService.IsConnected);
CoreProcess.ListenerFailure = null; AppState.State.UseTun = false;

var bridgeJson = JsonNode.Parse(SingBoxConfigBuilder.BuildTunBridge(LocalPorts.Socks, "warning", "223.5.5.5",
    [Path.Combine(runtime, "xray.exe"), Path.Combine(runtime, "sing-box.exe")], true, ["game.exe"]))!.AsObject();
Check("tun-ingress-config", "TUN bridge opens only TUN and forwards to the existing stable SOCKS relay",
    bridgeJson["inbounds"]!.AsArray().Count == 1 && bridgeJson["inbounds"]![0]!["type"]!.GetValue<string>() == "tun"
    && bridgeJson["experimental"] == null
    && bridgeJson["outbounds"]!.AsArray().Any(o => o?["type"]?.GetValue<string>() == "socks"
        && o["server_port"]!.GetValue<int>() == LocalPorts.Socks)
    && bridgeJson["route"]!["rules"]![0]!["process_path"]!.AsArray().Count == 2
    && bridgeJson["route"]!["rules"]!.AsArray().Any(o => o?["process_name"] != null));
var installedRuntime = Path.GetDirectoryName(args.FirstOrDefault() ?? "E:/Program Files/neo/app/runtime/xray.exe")!;
var installedSingBox = Path.Combine(installedRuntime, "sing-box.exe");
if (File.Exists(installedSingBox))
{
    var checkedBridge = bridgeJson.DeepClone();
    foreach (var set in checkedBridge["route"]!["rule_set"]!.AsArray())
        set!["path"] = Path.Combine(installedRuntime, "rule-sets", Path.GetFileName(set["path"]!.GetValue<string>()));
    var bridgeCheckPath = Path.Combine(isolated, "tun-bridge-preflight.json");
    File.WriteAllText(bridgeCheckPath, checkedBridge.ToJsonString());
    var actualBridgeCheck = Neo.Core.Core.KernelCompat.RunCheck(installedSingBox, KernelKind.SingBox, bridgeCheckPath, 8000);
    Check("tun-ingress-real-preflight", "installed sing-box accepts the exact bridge schema without changing any routes",
        actualBridgeCheck.Ok && !actualBridgeCheck.InfraFailed, actualBridgeCheck.Output);
}
ConnectionService.UseFullCreation = true;
var bridgeBase = await ConnectionService.RunFullCreation(b);
var bridgeMainPid = ConnectionService.CorePid;
var bridgeEpoch = ConnectionService.SessionEpoch;
var bridgeConnectedAt = ConnectionService.ConnectedAt;
var bridgeMainConfig = ConnectionService.CurrentConfig;
startsBefore = CoreProcess.Starts;
var bridgeOn = await ConnectionService.SetTunEnabledAsync(true);
var firstBridge = CoreProcess.LastTunBridge!;
var firstBridgeConfig = firstBridge.ConfigPath!;
Check("tun-toggle-preserves-session", "enabling TUN preserves main PID, epoch, timer, node, config and proxy lease",
    bridgeBase.Ok && bridgeOn.Ok && CoreProcess.Starts == startsBefore + 1
    && ConnectionService.CorePid == bridgeMainPid && ConnectionService.SessionEpoch == bridgeEpoch
    && ConnectionService.ConnectedAt == bridgeConnectedAt && ConnectionService.CurrentConfig == bridgeMainConfig
    && ReferenceEquals(ConnectionService.ConnectedNode, b) && SystemProxy.IsEnabled
    && ConnectionService.IsTunSession && AppState.State.UseTun && ConnectionService.IsProtectedCore(firstBridge.Pid));
NetworkHealthService.BeginSession(bridgeEpoch);
var bridgeOff = await ConnectionService.SetTunEnabledAsync(false);
Check("tun-toggle-off-preserves-session", "disabling TUN stops only ingress and removes its owned config",
    bridgeOff.Ok && !firstBridge.IsRunning && !File.Exists(firstBridgeConfig)
    && ConnectionService.CorePid == bridgeMainPid && ConnectionService.SessionEpoch == bridgeEpoch
    && ConnectionService.ConnectedAt == bridgeConnectedAt && ConnectionService.IsConnected
    && !ConnectionService.IsTunSession && !AppState.State.UseTun && SystemProxy.IsEnabled
    && CoreProcess.Starts == startsBefore + 1);
Check("tun-health-same-epoch", "mode change replaces the health probe without creating a new node session",
    NetworkHealthService.BeginSession(bridgeEpoch));
NetworkHealthService.Reset();
startsBefore = CoreProcess.Starts;
CoreProcess.RejectTun = true;
var rejectedBridge = await ConnectionService.SetTunEnabledAsync(true);
CoreProcess.RejectTun = false;
Check("tun-toggle-failure-isolated", "TUN startup failure leaves the original live session intact",
    !rejectedBridge.Ok && !CoreProcess.LastTunBridge!.IsRunning && ConnectionService.TunBridgePid == 0
    && ConnectionService.CorePid == bridgeMainPid && ConnectionService.SessionEpoch == bridgeEpoch
    && ConnectionService.ConnectedAt == bridgeConnectedAt && ConnectionService.IsConnected
    && !AppState.State.UseTun && SystemProxy.IsEnabled && CoreProcess.Starts == startsBefore + 1);
Neo.App.Services.KernelCompat.CheckPolicy = json => !json.Contains("neo-tun");
startsBefore = CoreProcess.Starts;
var rejectedBridgeCheck = await ConnectionService.SetTunEnabledAsync(true);
Neo.App.Services.KernelCompat.CheckPolicy = null;
Check("tun-preflight-isolated", "TUN preflight rejection starts nothing and preserves the node",
    !rejectedBridgeCheck.Ok && CoreProcess.Starts == startsBefore && ConnectionService.CorePid == bridgeMainPid
    && ConnectionService.IsConnected && !ConnectionService.IsTunSession && !AppState.State.UseTun);
using (var bridgeCancel = new CancellationTokenSource())
{
    CoreProcess.TunReady = false;
    var startingBridge = ConnectionService.SetTunEnabledAsync(true, bridgeCancel.Token);
    await WaitUntil(() => CoreProcess.LastTunBridge is { IsRunning: true });
    var duplicateToggle = await ConnectionService.SetTunEnabledAsync(false);
    bridgeCancel.Cancel();
    var canceledBridge = await startingBridge.WaitAsync(TimeSpan.FromSeconds(3));
    CoreProcess.TunReady = true;
    Check("tun-cancel-isolated", "cancel and rapid duplicate toggle cannot reset or replace the active node",
        !canceledBridge.Ok && !duplicateToggle.Ok && !CoreProcess.LastTunBridge!.IsRunning
        && ConnectionService.CorePid == bridgeMainPid && ConnectionService.SessionEpoch == bridgeEpoch
        && ConnectionService.ConnectedAt == bridgeConnectedAt && ConnectionService.IsConnected
        && !AppState.State.UseTun && !ConnectionService.IsSessionBusy);
}
AppState.State.AutoRestartCore = true;
await ConnectionService.SetTunEnabledAsync(true);
var crashedBridge = CoreProcess.LastTunBridge!;
startsBefore = CoreProcess.Starts;
crashedBridge.Crash();
await WaitUntil(() => CoreProcess.LastTunBridge != crashedBridge && ConnectionService.IsTunSession && !ConnectionService.IsSessionBusy);
Check("tun-crash-local-recovery", "TUN crash restarts only ingress once and retains the original node session",
    CoreProcess.Starts == startsBefore + 1 && ConnectionService.CorePid == bridgeMainPid
    && ConnectionService.SessionEpoch == bridgeEpoch && ConnectionService.ConnectedAt == bridgeConnectedAt
    && ReferenceEquals(ConnectionService.ConnectedNode, b) && AppState.State.UseTun && SystemProxy.IsEnabled);
var recoveredBridge = CoreProcess.LastTunBridge!;
crashedBridge.LateExit();
await Task.Delay(30);
Check("tun-stale-exit", "a delayed old ingress exit cannot stop its successor",
    recoveredBridge.IsRunning && ConnectionService.TunBridgePid == recoveredBridge.Pid && ConnectionService.CorePid == bridgeMainPid);
recoveredBridge.Crash();
await WaitUntil(() => !ConnectionService.IsTunSession && !ConnectionService.IsSessionBusy);
Check("tun-crash-bounded", "second ingress crash falls back to the existing proxy without reconnecting or looping",
    !AppState.State.UseTun && SystemProxy.IsEnabled && ConnectionService.IsConnected
    && ConnectionService.CorePid == bridgeMainPid && CoreProcess.Starts == startsBefore + 1);
CoreProcess.TunReady = false;
var revokedBridgeTask = ConnectionService.SetTunEnabledAsync(true);
await WaitUntil(() => CoreProcess.LastTunBridge is { IsRunning: true });
ConnectionService.Disconnect("user disconnect during TUN ingress startup");
var revokedBridge = await revokedBridgeTask.WaitAsync(TimeSpan.FromSeconds(3));
await ConnectionService.LastTunTeardown;
CoreProcess.TunReady = true;
Check("tun-disconnect-not-revived", "manual disconnect during ingress startup tears down both owners and never reconnects",
    !revokedBridge.Ok && !ConnectionService.IsConnected && !CoreProcess.LastTunBridge!.IsRunning
    && !SystemProxy.IsEnabled && !ConnectionService.IsSessionBusy);
AppState.State.UseTun = false;

ConnectionService.UseFullCreation = true;
ConnectionService.Reset(a, 120); ConnectionService.MarkTun();
entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
Task<(bool Ok, string? Error)>? successor = null;
using (var allowSuccessor = new ManualResetEventSlim())
{
    void OnProgress(string progress)
    {
        if (progress.Length != 0) return;
        Neo.App.Services.KernelCompat.CheckPolicy = _ =>
        {
            entered.TrySetResult();
            if (!allowSuccessor.Wait(TimeSpan.FromSeconds(6))) throw new TimeoutException("successor barrier");
            return true;
        };
        successor = ConnectionService.RunFullCreation(c);
        if (!entered.Task.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("successor not entered");
    }
    ConnectionService.SwitchProgress += OnProgress;
    var fallbackFull = await ConnectionService.SwitchNodeFastAsync(b);
    ConnectionService.SwitchProgress -= OnProgress;
    var successorStillConnecting = ConnectionService.IsConnecting;
    allowSuccessor.Set();
    var successorResult = successor == null ? (false, (string?)"not started") : await successor;
    Neo.App.Services.KernelCompat.CheckPolicy = null; ConnectionService.UseFullCreation = false;
    Check("fallback-successor", "fallback switch finalizer preserves a newer in-progress full connection",
        fallbackFull.Ok && successorStillConnecting && successorResult.Item1 && ReferenceEquals(ConnectionService.ConnectedNode, c));
}
ConnectionService.Disconnect("full fixtures complete"); await ConnectionService.LastTunTeardown;

// The whole production connect/cleanup/exit methods also own auxiliary engines.
File.WriteAllText(Path.Combine(runtime, "mihomo.exe"), "never executed fixture");
var ssrNode = new NodeItem { Protocol = NodeProtocol.ShadowsocksR, Name = "audit SSR", Server = "127.0.0.1", Port = 443, Method = "aes-128-cfb", Password = "synthetic" };
AppState.State.AutoRestartCore = false;
var ssrCreated = await ConnectionService.RunFullCreation(ssrNode);
var ownedAux = CoreProcess.LastAuxiliary!; var ownedAuxConfig = ownedAux.ConfigPath!;
Check("aux-connect", "full connection owns frontend and auxiliary together", ssrCreated.Ok && ownedAux.IsRunning && ConnectionService.IsProtectedCore(ownedAux.Pid) && ReferenceEquals(ConnectionService.ConnectedNode, ssrNode));
ownedAux.Crash(); await ConnectionService.LastTunTeardown;
Check("aux-crash", "auxiliary crash revokes proxy frontend and config", !ConnectionService.IsConnected && !SystemProxy.IsEnabled && !File.Exists(ownedAuxConfig) && !ConnectionService.IsProtectedCore(ownedAux.Pid));
await ConnectionService.RunFullCreation(b); ownedAux.LateExit();
Check("aux-stale-exit", "old auxiliary exit cannot stop a newer hand-selected node", ConnectionService.IsConnected && ReferenceEquals(ConnectionService.ConnectedNode, b));
ConnectionService.Disconnect("aux test next"); await ConnectionService.LastTunTeardown;
CoreProcess.RejectAuxiliary = true;
var auxFailure = await ConnectionService.RunFullCreation(ssrNode); CoreProcess.RejectAuxiliary = false; await ConnectionService.LastTunTeardown;
Check("aux-start-failure", "auxiliary startup failure cannot leave proxy ownership or an active session", !auxFailure.Ok && !ConnectionService.IsConnected && !SystemProxy.IsEnabled && !ConnectionService.IsSessionBusy && !CoreProcess.LastAuxiliary!.IsRunning);
var auxDirectory = Path.Combine(isolated,"auxiliary","mihomo");
var configsBefore = Directory.GetFiles(auxDirectory,"session-*.json").ToHashSet();
Neo.App.Services.KernelCompat.CheckPolicy = _ => false;
var auxPreflightFailure = await ConnectionService.RunFullCreation(ssrNode);
Neo.App.Services.KernelCompat.CheckPolicy = null; await ConnectionService.LastTunTeardown;
Check("aux-preflight-file", "preflight rejection removes the unstarted auxiliary session config", !auxPreflightFailure.Ok && Directory.GetFiles(auxDirectory,"session-*.json").ToHashSet().SetEquals(configsBefore));
AppState.State.AutoRestartCore = true;
await ConnectionService.RunFullCreation(ssrNode); var restarts = ConnectionService.AutoRestartCalls;
CoreProcess.LastAuxiliary!.Crash(); await ConnectionService.LastTunTeardown;
Check("aux-restart", "enabled auxiliary recovery requests only the same selected node", ConnectionService.AutoRestartCalls == restarts + 1 && ReferenceEquals(ConnectionService.AutoRestartNode, ssrNode));
AppState.State.AutoRestartCore = false;
var auxHandshakeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
CoreProcess.BeforeHandshake = async token => { auxHandshakeEntered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); };
using (var auxCancel = new CancellationTokenSource())
{
    var pendingAux = ConnectionService.RunFullCreation(ssrNode, ct: auxCancel.Token);
    await auxHandshakeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); auxCancel.Cancel();
    var canceledAux = await pendingAux.WaitAsync(TimeSpan.FromSeconds(5)); await ConnectionService.LastTunTeardown;
    Check("aux-cancel", "canceling auxiliary readiness cleans the owned process and never enables proxy", !canceledAux.Ok && !CoreProcess.LastAuxiliary!.IsRunning && !SystemProxy.IsEnabled && !ConnectionService.IsSessionBusy);
}
CoreProcess.BeforeHandshake = null;

// Production AppState import uses the real parser with isolated data and a controlled refresh boundary.
Neo.Core.Parsing.SubscriptionFetcher.UseProductionParser = true;
AppState.State.Subscriptions.Clear(); AppState.SetCurrentNode(b);
var originalNodeKey = AppState.State.LastNodeKey;
var importText = "socks5://user:pass@127.0.0.1:39001#first";
var imported = await AppState.ImportLocalContentAsync(importText, "local");
Check("import-first", "first pasted import creates an enabled refreshable local subscription", imported.Enabled && imported.Count == 1 && File.Exists(AppState.Subscriptions.Single().Url) && AppState.Subscriptions.Single().NodeCount == 1);
var importedAgain = await AppState.ImportLocalContentAsync(importText, "duplicate");
Check("import-repeat", "identical content import reuses subscription and keeps selection", importedAgain.Id == imported.Id && AppState.Subscriptions.Count == 1 && AppState.State.LastNodeKey == originalNodeKey && AppState.NodeKey(AppState.CurrentNode!) == originalNodeKey);
var chosenFile = Path.Combine(isolated, "chosen-local.txt"); File.WriteAllText(chosenFile, "socks5://user:pass@127.0.0.1:39002#file");
var importedFile = await AppState.ImportLocalContentAsync(File.ReadAllText(chosenFile), "file", chosenFile);
Check("import-file", "subsequent file import stays disabled and retains its original source path", !importedFile.Enabled && AppState.Subscriptions.Single(s => s.Id == importedFile.Id).Url == chosenFile && AppState.State.LastNodeKey == originalNodeKey);
var gate = (SemaphoreSlim)typeof(AppState).GetField("SubRefreshGate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
await gate.WaitAsync(); var refusedWhileRefreshing = false;
try { await AppState.ImportLocalContentAsync("socks5://u:p@127.0.0.1:39003", "busy"); } catch (InvalidOperationException) { refusedWhileRefreshing = true; } finally { gate.Release(); }
Check("import-refresh-gate", "import cannot overwrite an in-flight subscription refresh", refusedWhileRefreshing && AppState.Subscriptions.Count == 2);
var invalidImport = false; try { await AppState.ImportLocalContentAsync("not a supported config", "bad"); } catch (ArgumentException) { invalidImport = true; }
Check("import-invalid", "invalid content never creates an empty subscription", invalidImport && AppState.Subscriptions.Count == 2);
using (var importCancel = new CancellationTokenSource()) { importCancel.Cancel(); var canceled = false; try { await AppState.ImportLocalContentAsync(importText, "canceled", ct: importCancel.Token); } catch (OperationCanceledException) { canceled = true; } Check("import-cancel", "canceled import cannot mutate subscriptions", canceled && AppState.Subscriptions.Count == 2); }
Neo.Core.Parsing.SubscriptionFetcher.UseProductionParser = false;

// The response headers arrive, then the body stalls. Execute the exact bounded read method.
using (var socksListener = new TcpListener(IPAddress.Loopback, LocalPorts.Socks))
using (var throughputCancel = new CancellationTokenSource())
using (var fixtureCancel = new CancellationTokenSource(TimeSpan.FromSeconds(22)))
{
    socksListener.Start();
    var headerSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var fixture = Task.Run(async () =>
    {
        try
        {
            using var socket = await socksListener.AcceptTcpClientAsync(fixtureCancel.Token);
            await using var stream = socket.GetStream();
            var greeting = new byte[2]; await stream.ReadExactlyAsync(greeting, fixtureCancel.Token);
            await stream.ReadExactlyAsync(new byte[greeting[1]], fixtureCancel.Token);
            await stream.WriteAsync(new byte[] { 5, 0 }, fixtureCancel.Token);
            var request = new byte[4]; await stream.ReadExactlyAsync(request, fixtureCancel.Token);
            var addressSize = request[3] switch
            {
                1 => 4, 4 => 16,
                3 => await OneByte(stream, fixtureCancel.Token),
                _ => throw new InvalidOperationException("Unexpected SOCKS address")
            };
            await stream.ReadExactlyAsync(new byte[addressSize + 2], fixtureCancel.Token);
            await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }, fixtureCancel.Token);
            var header = new List<byte>();
            while (header.Count < 8192)
            {
                header.Add((byte)await OneByte(stream, fixtureCancel.Token));
                if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
            }
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 131072\r\nConnection: close\r\n\r\nx"), fixtureCancel.Token);
            headerSent.TrySetResult();
            await Task.Delay(Timeout.Infinite, fixtureCancel.Token);
        }
        catch (OperationCanceledException) { }
    });
    var throughput = ThroughputUnderTest.Run(throughputCancel.Token);
    await headerSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    await throughput.WaitAsync(TimeSpan.FromSeconds(11));
    var finishedWithinBudget = throughput.IsCompleted;
    throughputCancel.Cancel();
    var canceledOnRequest = false;
    try { await throughput.WaitAsync(TimeSpan.FromSeconds(3)); }
    catch (OperationCanceledException) { canceledOnRequest = true; }
    fixtureCancel.Cancel(); await fixture;
    Check("D11", "stalled body terminates within its read budget", finishedWithinBudget && !canceledOnRequest,
        new { responseHeadersAndOneByteDelivered = true, declaredHttpTimeoutSeconds = 12, declaredReadLoopBudgetMs = 8000,
            observedStallSeconds = stopwatch.Elapsed.TotalSeconds, finishedWithinBudget, canceledOnRequest,
            onlySourceAdaptation = "Target URL changed to HTTP loopback SOCKS fixture" });
}


using (var alreadyCanceled = new CancellationTokenSource())
{
    alreadyCanceled.Cancel();
    var propagated = false;
    try { await ThroughputUnderTest.Run(alreadyCanceled.Token); }
    catch (OperationCanceledException) { propagated = true; }
    Check("D11-cancel", "caller cancellation propagates rather than becoming a timeout result", propagated);
}

// Execute the production kernel-update session coordinator with controlled process boundaries.
ConnectionService.UseFullCreation = false;
ConnectionService.FullConnectPolicy = null;
Neo.App.App.IsShuttingDown = false;
ConnectionService.Reset(a, 200);
var commitSawStopped = false;
var kernelUpdate = await ConnectionService.ReplaceKernelFilesAsync(200, () => {
    commitSawStopped = !ConnectionService.IsConnected && ConnectionService.CorePid == 0;
    return (true, "synthetic installed files");
}, default);
Check("update-session-restored", "kernel commit waits for teardown and restores exact original node",
    kernelUpdate.Ok && commitSawStopped && ReferenceEquals(ConnectionService.ConnectedNode, a));
ConnectionService.Reset(a, 210);
var ranStaleCommit = false;
var staleKernel = await ConnectionService.ReplaceKernelFilesAsync(209, () => { ranStaleCommit = true; return (true, ""); }, default);
Check("update-stale-session", "stale download never disconnects a newer session", !staleKernel.Ok && !ranStaleCommit && ReferenceEquals(ConnectionService.ConnectedNode, a));
ConnectionService.Reset(a, 220);
var failedKernel = await ConnectionService.ReplaceKernelFilesAsync(220, () => throw new IOException("synthetic file-lock failure"), default);
Check("update-failure-restores", "failed file commit restores original node", !failedKernel.Ok && ReferenceEquals(ConnectionService.ConnectedNode, a));
ConnectionService.Reset(a, 230);
var userCancelledKernel = await ConnectionService.ReplaceKernelFilesAsync(230, () => { ConnectionService.Disconnect("用户点击圆环断开"); return (true, ""); }, default);
Check("update-user-disconnect", "user disconnect during update is never undone", userCancelledKernel.Ok && !ConnectionService.IsConnected);
ConnectionService.Reset(a, 240);
var userSelectedKernel = await ConnectionService.ReplaceKernelFilesAsync(240, () => { AppState.SetCurrentNode(b); return (true, ""); }, default);
Check("update-user-selection", "selection made during update is preserved", userSelectedKernel.Ok && !ConnectionService.IsConnected && ReferenceEquals(AppState.CurrentNode, b));
ConnectionService.Reset(a, 250);
await ConnectionService.HoldLifecycle();
using (var updateCancel = new CancellationTokenSource()) {
    var cancelledUpdate = ConnectionService.ReplaceKernelFilesAsync(250, () => (true, "must not commit"), updateCancel.Token);
    await Task.Delay(30); updateCancel.Cancel(); ConnectionService.ReleaseLifecycle();
    var observedCancel = false; try { await cancelledUpdate; } catch(OperationCanceledException) { observedCancel = true; }
    Check("update-cancel-restores", "cancel while waiting for teardown restores same node", observedCancel && ReferenceEquals(ConnectionService.ConnectedNode, a));
}
ConnectionService.Disconnect("update tests complete"); await ConnectionService.LastTunTeardown;

Console.WriteLine($"RESULT: {results.Count - failed}/{results.Count} passed");
Environment.ExitCode = failed == 0 ? 0 : 1;

static bool HasInsecure(string json) => JsonDocument.Parse(json).RootElement.GetProperty("outbounds").EnumerateArray()
    .Any(o => o.TryGetProperty("tls", out var tls) && tls.TryGetProperty("insecure", out var insecure) && insecure.GetBoolean());
static bool Free(int port)
{
    try { var listener = new TcpListener(IPAddress.Loopback, port); listener.Start(); listener.Stop(); return true; } catch { return false; }
}
static async Task<int> OneByte(Stream stream, CancellationToken ct)
{
    var value = new byte[1]; await stream.ReadExactlyAsync(value, ct); return value[0];
}
static async Task WaitUntil(Func<bool> predicate)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
    while (!predicate()) await Task.Delay(20, timeout.Token);
}
