extern alias RealCore;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Neo.Core;
using Neo.Core.Models;

namespace Neo.App
{
    public static class App { public static bool IsShuttingDown; }
}

namespace Neo.App.Services
{
    public static class ProxyNativeBoundary
    {
        public static uint Flags = 1;
        public static bool RejectApply;
        public static bool RejectRead;
        public static bool IgnoreApply;
        public static string? AlteredServer;
        public static string? Server;
        public static string? Bypass;
        public static string? Pac;
        public static int Applications;
        public static int ApplyAttempts;
        public static int RejectApplyCount;
        public static bool ForeignOwnerAlive;
    }
    public enum CoreEngine { Mihomo, Naive }
    public static class KernelCatalog
    {
        public static bool IsInstalled(CoreEngine engine) => false;
        public static string ExePath(CoreEngine engine) => Path.Combine(NeoPaths.RuntimeDir, "never-executed.exe");
    }
    public static class GeoDataUpdater { public static void ApplyPending() { } }
    public static class RustConfigBridge { public static Task<string?> TryBuildAsync(NodeItem node, bool tun) => Task.FromResult<string?>(null); }
    public static class ConfigShadow { public static void QueueIfAvailable(string json, NodeItem node, bool tun) { } }
    public sealed class FakeDispatcher
    {
        public bool HasThreadAccess => true;
        public bool TryEnqueue(Action action) { action(); return true; }
    }
    public static class LogBus
    {
        public static Action<string>? Hook;
        public static readonly List<string> Messages = new();
        public static void App(string text, string level = "Info") { lock (Messages) Messages.Add(text); Hook?.Invoke(text); }
        public static void Exception(string text, Exception ex) => App(text + ": " + ex.Message);
        public static void Core(string text) => App(text);
    }
    public static class IncidentLog { public static void Record(string kind, string text, string? detail = null) { } }
    public static class CoreState
    {
        public sealed class Snapshot { public int NeoPid; public int CorePid; public string State = "idle"; }
        public static Snapshot? Current;
        public static Snapshot? Read() => Current;
        public static void Report(string state, int pid = 0, string core = "", string? reason = null, int? sessionEpoch = null) { }
    }
    public static class SubscriptionLogService
    {
        public static string Begin(params object?[] args) => "synthetic-log";
        public static void Finish(params object?[] args) { }
        public static string HostOf(string url) => "synthetic.invalid";
    }
    public static class LocationCache { public static void Apply(NodeItem n) { } }
    public static class SystemProxy
    {
        public static bool IsEffective => IsEnabled;
        public static bool RejectEnable;
        public static bool IsEnabled;
        public static string? CurrentServer;
        public static bool Enable(string server, bool repair = false) { if (RejectEnable) return false; IsEnabled = true; CurrentServer = server; return true; }
        public static bool Disable(bool force = false) { IsEnabled = false; return true; }
        public static int RestoreCalls;
        public static Task<bool> RestoreAfterDisconnectAsync() { RestoreCalls++; Disable(); return Task.FromResult(true); }
        public static int CleanupCalls;
        public static bool CleanupOrphaned(params int[] ports) { CleanupCalls++; return false; }
        public static bool CleanupStaleSnapshot() => false;
    }
    public static class XrayStats
    {
        public record struct Snapshot(long Uplink, long Downlink);
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<int, Snapshot> Counters = new();
        public static Snapshot? Query(int? apiPort = null) => Counters.TryGetValue(apiPort ?? LocalPorts.CoreStatsApi, out var s) ? s : null;
    }
    public static class SingBoxStats
    {
        public record struct Snapshot(long Uplink, long Downlink, int ActiveConnections);
        public static Snapshot? Query(int? apiPort = null) => null;
    }
    public static class TcpConnCounter { public static int CountEstablished(int port) => 0; }

    // These fakes never launch/terminate a process or write a Windows proxy setting.
    public sealed class CoreProcess : IDisposable
    {
        public static int Starts;
        public static bool RejectAuxiliary;
        public static CoreProcess? LastAuxiliary;
        public static Func<CancellationToken, Task>? BeforeHandshake;
        public static Func<int, string?>? ListenerFailure;
        public static bool RejectTun;
        public static bool TunReady = true;
        public static CoreProcess? LastTunBridge;
        private static int _nextPid = 1000000;
        private readonly List<TcpListener> _listeners = new();
        private readonly CancellationTokenSource _listenerStop = new();
        public int Pid { get; } = Interlocked.Increment(ref _nextPid);
        public bool IsRunning { get; private set; } = true;
        public string? ConfigPath;
        public event Action<string>? LogLine;
        public event Action<int>? Exited;
        public CoreProcess(string path = "synthetic", string working = "synthetic") { }
        public void Start(string config, IReadOnlyList<string>? args = null)
        {
            Interlocked.Increment(ref Starts);
            ConfigPath = config;
            using var doc = JsonDocument.Parse(File.ReadAllText(config));
            var root = doc.RootElement;
            var inputs = root.TryGetProperty("inbounds", out var ins) ? ins.EnumerateArray().ToArray() : Array.Empty<JsonElement>();
            if (inputs.Any(i => i.TryGetProperty("type", out var t) && t.GetString() == "tun"))
            {
                LastTunBridge = this;
                if (RejectTun) { IsRunning = false; LogLine?.Invoke("FATAL synthetic TUN adapter failure"); return; }
            }
            if (inputs.Length == 0)
            {
                LastAuxiliary = this;
                if (RejectAuxiliary) throw new IOException("synthetic auxiliary startup failure");
                var port = root.TryGetProperty("socks-port", out var value) ? value.GetInt32() : new Uri(root.GetProperty("listen").GetString()!).Port;
                var listener = new TcpListener(IPAddress.Loopback, port); listener.Start(); _listeners.Add(listener);
                _ = AcceptAsync(listener, true, _listenerStop.Token);
            }
            foreach (var input in inputs)
            {
                var protocol = input.TryGetProperty("protocol", out var p) ? p.GetString() : input.GetProperty("type").GetString();
                if (protocol is not ("socks" or "http" or "mixed")) continue;
                var port = input.TryGetProperty("port", out var q) ? q.GetInt32() : input.GetProperty("listen_port").GetInt32();
                if (ListenerFailure?.Invoke(port) is { } failure)
                {
                    IsRunning = false; LogLine?.Invoke(failure); return;
                }
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start(); _listeners.Add(listener);
                _ = AcceptAsync(listener, protocol is "socks" or "mixed", _listenerStop.Token);
            }
        }
        private static async Task AcceptAsync(TcpListener listener, bool socks, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    using var peer = await listener.AcceptTcpClientAsync(ct);
                    if (!socks) continue;
                    using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    budget.CancelAfter(TimeSpan.FromSeconds(1));
                    try
                    {
                        var request = new byte[3]; var stream = peer.GetStream();
                        await stream.ReadExactlyAsync(request, budget.Token);
                        if (BeforeHandshake != null) await BeforeHandshake(budget.Token);
                        if (request.SequenceEqual(new byte[] {5,1,0}))
                            await stream.WriteAsync(new byte[] {5,0}, budget.Token);
                    }
                    catch (IOException) { }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) when (ct.IsCancellationRequested) { }
        }
        public void Stop() { IsRunning = false; _listenerStop.Cancel(); foreach (var l in _listeners) l.Stop(); _listeners.Clear(); }
        public bool StopAndVerify() { Stop(); return true; }
        public void Crash(int code = 1) { Stop(); Exited?.Invoke(code); }
        public void LateExit(int code = 1) => Exited?.Invoke(code);
        public void Dispose() => Stop();
    }
    public sealed class PortRelay(int port, int backend)
    {
        public static Func<int, int>? ActiveHook;
        public int ListenPort = port;
        public int Backend = backend;
        public bool IsRunning = true;
        public int ActiveFor(int backend) => ActiveHook?.Invoke(backend) ?? 0;
        public void SwitchBackend(int backend) => Backend = backend;
        public void Dispose() => IsRunning = false;
    }
    public static partial class ConnectionService
    {
        public static void NotifyUser(string message) { }
        private static bool _selfCheckSwitching, _tunPreStartGuardDone;
        private static int _selfCheckSwitchCount;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _selfCheckTried = new();
        public static string ConfigPath => Path.Combine(NeoPaths.DataDir, "config.json");
        public static bool IsElevated => true;
        public static bool ActiveTunResidue;
        public static bool HasActiveTunResidueForGuard() => ActiveTunResidue;
        private static bool HasTunAdapter() => ActiveTunResidue || CoreProcess.LastTunBridge is { IsRunning: true };
        private static bool IsTunAdapterActive() => CoreProcess.TunReady && HasTunAdapter();
        // Statistics/controller listeners are synthetic, unlike the fixture SOCKS/HTTP sockets.
        // Never let the installed app's fixed API ports decide an isolated test's retry path.
        private static bool PortFree(int port) => new[] { LocalPorts.SlotStats(0), LocalPorts.SlotStats(1),
            LocalPorts.SlotClashApi(0), LocalPorts.SlotClashApi(1) }.Contains(port) || RealPortFree(port);
        private static bool SameNode(NodeItem a, NodeItem b) => AppState.NodeKey(a) == AppState.NodeKey(b);
        public static int OrphanCleanupCalls;
        public static Action? OrphanCleanupHook;
        public static void CleanupOrphanCores() { OrphanCleanupCalls++; OrphanCleanupHook?.Invoke(); }
        public static bool IsRecordedGuardCore(int pid, int watchedNeoPid) => false;
        public static bool IsProtectedCore(int pid) => IsActiveCorePid(pid);
        private static Task<bool> WaitPortsFreeAsync() => Task.FromResult(true);
        private static int FirstBusyPort() => 0;
        private static string XrayOnlyReason(NodeItem node) => "synthetic capability conflict";
        private static NodeItem? ResolveFixedExitNode() => null;
        private static List<NodeItem> PickFixedExitEntries(NodeItem exit, int count) => new();
        private static (bool Ok, string Message) RunMihomoCheck(string path) => (true, "synthetic check");
        private static string KernelTailText() => string.Join(Environment.NewLine, _kernelTail);
        private static string ModeName(string mode) => mode;
        private static void EnsureTunAdapterCleared() { }
        private static Task WarmupDnsAsync() => Task.CompletedTask;
        private static void StartPostConnectSelfCheck(NodeItem node) { }
        public static Task<(bool Ok, string? Error)> RunFullCreation(NodeItem node, CancellationToken ct = default, SessionIntent? intent = null)
            => Task.Run(() => ConnectSessionAsync(node, true, intent?.Epoch ?? SessionEpoch, ct, intent));
        private static CoreProcess? _xray;
        private static readonly object SessionStateGate = new();
        private static readonly SemaphoreSlim TeardownGate = new(1, 1);
        private static Task _lastTeardown = Task.CompletedTask;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<CoreProcess, string> CoreConfigs = new();
        public static Func<Task>? TunHook;
        private static Guid? CaptureTunAdapterId() => Guid.Parse("a659d842-43b6-45de-b7bf-55a2db276c14");
        private static bool HasRunningRuntimeSingBox() => false;
        private static CancellationTokenSource? _selfCheckCts, _watchdogCts;
        private static void TeardownTunNetwork(Guid? adapterId) { TunHook?.Invoke().GetAwaiter().GetResult(); }
        public static Task HoldLifecycle() => TeardownGate.WaitAsync();
        public static void ReleaseLifecycle() => TeardownGate.Release();
        public static void MarkTun() => _tunSessionActive = true;
        public static void ScheduleTunCleanup() => StartBackgroundTunTeardown("test");

        private static CoreProcess? _retiringCore;
        private static CoreProcess? _naive;
        private static PortRelay? _relaySocks;
        private static PortRelay? _relayHttp;
        private static int _switchGate;
        private static int _sessionEpoch;
        private static readonly SemaphoreSlim SessionGate = new(1, 1);
        private static readonly System.Collections.Concurrent.ConcurrentQueue<string> _kernelTail = new();
        private static bool _watchdogRestarting;
        private static int _watchdogTriggerCount;
        private static bool _tunSessionActive;
        private static CancellationTokenSource? _coreRestartCts;
        private static DateTime _lastCoreStartAt;
        private static int _autoMeasureInFlight;
        private const int AutoMeasureAttempts = 3;
        private const string ConnectionCancelled = "连接已取消：会话已改变或程序正在退出";
        public static FakeDispatcher? UiDispatcher;
        public static bool IsConnected;
        public static bool IsConnecting;
        public static bool IsSessionBusy => SessionGate.CurrentCount == 0;
        public static NodeItem? ConnectedNode;
        public static DateTime? ConnectedAt;
        public static string CoreKind = "xray";
        public static int CorePid => _xray is { IsRunning: true } ? _xray.Pid : 0;
        public static bool IsTunSession => _tunSessionActive;
        public static int SessionEpoch => Volatile.Read(ref _sessionEpoch);
        public static bool FixedExitActive;
        public static int FixedExitEntries;
        public static string? FixedExitName;
        public static Task LastTunTeardown => _lastTeardown;
        public static event Action? StateChanged;
        public static event Action<string>? LogLine;
        public static event Action<string>? SwitchProgress;
        public static Action<NodeItem, int?>? SwitchHook;
        public static int ReconnectCalls;
        public static Func<NodeItem, bool>? FullConnectPolicy;
        public static bool UseFullCreation;
        public static string? CurrentConfig => _xray?.ConfigPath;
        public static int TunBridgePid => _tunBridge?.Pid ?? 0;
        public static bool RetiringIsRunning => _retiringCore is { IsRunning: true };
        public static Task Measure(NodeItem node) => TryAutoMeasureDelayAsync(node);
        public static Task ApplyTls(NodeItem node) => ApplyTlsPolicyAsync(node);
        private static void EnsureRelays()
        {
            _relaySocks ??= new(LocalPorts.Socks, LocalPorts.CoreSocks);
            _relayHttp ??= new(LocalPorts.Http, LocalPorts.CoreHttp);
            _relaySocks.SwitchBackend(LocalPorts.CoreSocks);
            _relayHttp.SwitchBackend(LocalPorts.CoreHttp);
        }
        private static void StopRelays() { _relaySocks?.Dispose(); _relayHttp?.Dispose(); _relaySocks = null; _relayHttp = null; }
        private static void CancelPostConnectSelfCheck() { }
        private static void CancelWatchdog() { }
        private static void NotifyStateChanged() { }
        private static void NotifyNodesChangedSafe() { }
        private static void StartWatchdog(NodeItem node) { }
        public static int AutoRestartCalls;
        public static NodeItem? AutoRestartNode;
        private static Task TryAutoRestartCoreAsync(NodeItem node) { AutoRestartCalls++; AutoRestartNode = node; return Task.CompletedTask; }
        private static string XrayVersion => "26.10.21";
        private static string SingBoxVersion => "1.14.3";
        public static Task<string> Preflight(string json, NodeItem node, bool singbox) => EnsureCompatibleConfigAsync(json, node, singbox);
        public static Task<(bool Ok, string? Error)> ConnectAsync(NodeItem node, bool force = false,
            int? expectedEpoch = null, CancellationToken ct = default, SessionIntent? intent = null)
        {
            ReconnectCalls++;
            if (UseFullCreation) return RunFullCreation(node, ct, intent);
            if (ct.IsCancellationRequested || (expectedEpoch.HasValue && expectedEpoch != SessionEpoch))
                return Task.FromResult((false, (string?)"cancelled"));
            if (FullConnectPolicy != null)
            {
                var before = _sessionEpoch;
                var next = Interlocked.Increment(ref _sessionEpoch);
                intent?.Advance(before, next);
                var ok = FullConnectPolicy(node);
                if (ok) { _xray = new(); ConnectedNode = node; IsConnected = true; AppState.SetCurrentNode(node); }
                return Task.FromResult((ok, ok ? (string?)null : "synthetic connect rejection"));
            }
            SwitchHook?.Invoke(node, expectedEpoch);
            ConnectedNode = node; IsConnected = true;
            return Task.FromResult((true, (string?)null));
        }
        public static void Reset(NodeItem node, int epoch = 0)
        {
            Disconnect("test reset"); _lastTeardown.GetAwaiter().GetResult();
            _xray = new(); _retiringCore = null; _naive = null;
            CoreStats.Register(_xray, "xray", LocalPorts.CoreStatsApi, LocalPorts.CoreClashApi, LocalPorts.Socks);
            _relaySocks = null; _relayHttp = null;
            ConnectedNode = node; IsConnected = true; IsConnecting = false;
            _sessionEpoch = epoch; _switchGate = 0; _tunSessionActive = false;
            FixedExitActive = false; FixedExitEntries = 0; FixedExitName = null;
            AppState.SetCurrentNode(node); SwitchHook = null; ReconnectCalls = 0;
            LogBus.Hook = null;
        }
        public static void UserReconnected(NodeItem node)
        {
            _sessionEpoch++; ConnectedNode = node; IsConnected = true; AppState.SetCurrentNode(node);
        }
    }
    public static class KernelCompat
    {
        public record CheckResult(bool Ok, bool InfraFailed, string Output);
        public static Func<string, bool>? CheckPolicy;
        public static string? RealExecutablePath;
        public static readonly List<CheckResult> ActualChecks = new();
        public static Version? ParseVersion(string text, Neo.Core.Core.KernelKind kind)
            => Neo.Core.Core.KernelCompat.ParseVersion(text, kind);
        public static string Adapt(string json, Neo.Core.Core.KernelKind kind, Version version, out List<string> notes)
            => Neo.Core.Core.KernelCompat.Adapt(json, kind, version, out notes);
        public static string StripOptionalFields(string json, Neo.Core.Core.KernelKind kind, out List<string> notes)
            => Neo.Core.Core.KernelCompat.StripOptionalFields(json, kind, out notes);
        public static string FirstLines(string output, int count) => Neo.Core.Core.KernelCompat.FirstLines(output, count);
        public static string DescribeStartupFailure(string? output) => Neo.Core.Core.KernelCompat.DescribeStartupFailure(output);
        public static int? ListenerBindFailurePort(string? output) => Neo.Core.Core.KernelCompat.ListenerBindFailurePort(output);
        public static CheckResult RunCheck(string exe, Neo.Core.Core.KernelKind kind, string path, int timeout)
        {
            if (RealExecutablePath is { } installedExecutable)
            {
                var result = Neo.Core.Core.KernelCompat.RunCheck(installedExecutable, kind, path, timeout);
                var actual = new CheckResult(result.Ok, result.InfraFailed, result.Output);
                ActualChecks.Add(actual);
                return actual;
            }
            var ok = CheckPolicy?.Invoke(File.ReadAllText(path)) ?? true;
            return new(ok, false, ok ? "synthetic check success" : "synthetic configuration validation rejection");
        }
    }
}

namespace Neo.Core.Core
{
    public static class RealDelayTester
    {
        public static Func<NodeItem, Task<int>>? RealPingHook;
        public static Func<int, string?, Task<int>>? PingHook;
        public static Task<int> RealPingAsync(NodeItem node, string runtimeDir, int timeoutMs = 10000, CancellationToken ct = default)
            => RealPingHook?.Invoke(node) ?? Task.FromResult(-1);
        public static Task<int> PingViaSocksAsync(int socksPort, string? target = null, int timeoutMs = 10000, int rounds = 1, CancellationToken ct = default)
            => PingHook?.Invoke(socksPort, target) ?? Task.FromResult(100);
        public static Task<bool> CertUntrustedAsync(NodeItem node, string runtime, int timeout = 6000) => Task.FromResult(false);
    }
}
namespace Neo.Core.Bridge
{
    public record FakeDecision(int Fails, string Decision, long? LastSwitchMs);
    public record FakePlaceholder(bool Placeholder, bool Parsed, string Reason);
    public static class RustCoreBridge
    {
        public static FakeDecision? TryFailoverDecision(long time, bool ok, bool hadTraffic, int fails, long? lastSwitchMs) => null;
        public static FakePlaceholder? TryDetectPlaceholder(string raw, IReadOnlyList<NodeItem> nodes) => null;
    }
}
namespace Neo.Core.Parsing
{
    public sealed class SubscriptionFetchResult
    {
        public bool Success = true;
        public string? Error;
        public string? RawText;
        public string? TrafficInfo;
        public long SubUpload, SubDownload, SubTotal, SubExpireUnix;
        public List<NodeItem> Nodes = new();
    }
    public static class SubscriptionFetcher
    {
        public static Func<string, Task<SubscriptionFetchResult>>? FetchHook;
        public static Task<SubscriptionFetchResult> FetchAsync(string url) => FetchHook!(url);
        public static SubscriptionFetchResult ParseContent(string raw) => UseProductionParser ? ParseImportedContent(raw) : new() { RawText = raw };
        public static bool UseProductionParser;
        public static SubscriptionFetchResult ParseImportedContent(string raw)
        {
            var r = RealCore::Neo.Core.Parsing.SubscriptionFetcher.ParseContent(raw);
            return new() { Success = r.Success, Error = r.Error, RawText = r.RawText, Nodes = r.Nodes.ToList(), TrafficInfo = r.TrafficInfo };
        }
    }
}
namespace Microsoft.Win32
{
    public enum PowerModes { Resume }
    public sealed class PowerModeChangedEventArgs : EventArgs { public PowerModes Mode => PowerModes.Resume; }
    public static class SystemEvents { public static event EventHandler<PowerModeChangedEventArgs>? PowerModeChanged; }
}

namespace Neo.DeepAudit
{
    public sealed class MemoryRegistryKey : IDisposable
    {
        public readonly Dictionary<string, object> Values = new();
        public Action<string>? WriteHook;
        public MemoryRegistryKey? CreateSubKey(string name) => this;
        public MemoryRegistryKey? OpenSubKey(string name) => this;
        public void SetValue(string name, object value, Microsoft.Win32.RegistryValueKind kind) { WriteHook?.Invoke(name); Values[name] = value; }
        public object? GetValue(string name) => Values.GetValueOrDefault(name);
        public void DeleteValue(string name, bool throwOnMissingValue) => Values.Remove(name);
        public void Dispose() { }
    }
    public static class MemoryRegistry { public static readonly MemoryRegistryKey CurrentUser = new(); }
}
