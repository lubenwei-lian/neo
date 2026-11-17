using Neo.Core.Models;

// All stateful operating-system operations are replaced here. Production source
// files are compiled unchanged; no installed proxy, process, adapter or state is mutated.
namespace Neo.Core
{
    public static class NeoPaths
    {
        public static string DataDir { get { var p = Path.Combine(AppContext.BaseDirectory, "test-data-" + Environment.ProcessId); Directory.CreateDirectory(p); return p; } }
        public static string LogDir { get { var p = Path.Combine(DataDir, "logs"); Directory.CreateDirectory(p); return p; } }
        public static string RuntimeDir => DataDir;
    }
}
namespace Neo.App
{
    public static class App { public static bool IsShuttingDown; }
}
namespace Neo.App.Services
{
    public sealed class TestSettings
    {
        public bool FailoverEnabled;
        public bool AutoRepairSystemProxy;
        public bool AutoRestartCore = true;
        public bool UseTun;
        public string LogLevel = "info";
        public int LogRetentionDays = 7;
    }
    public static class AppState
    {
        public static TestSettings State = new();
        public static List<NodeItem> Nodes = new();
        public static NodeItem? CurrentNode;
        public static void SetCurrentNode(NodeItem n) => CurrentNode = n;
        public static string NodeKey(NodeItem n) => $"{n.Server}|{n.Port}|{(int)n.Protocol}|{n.Name}";
    }
    public static class ConnectionService
    {
        public static readonly List<string> Notices = new();
        public static void NotifyUser(string message) => Notices.Add(message);
        public static NodeItem? ConnectedNode => AppState.CurrentNode;
        public static bool IsConnected;
        public static bool IsTunSession;
        public static int CorePid;
        public static int SessionEpoch;
        public static string CoreKind = "xray";
        public static Task LastTunTeardown => Task.CompletedTask;
        public static event Action? StateChanged;
        public static bool DisconnectIfCurrent(int expectedEpoch, string reason, out int epoch)
        {
            epoch = SessionEpoch;
            if (epoch != expectedEpoch) return false;
            Disconnect(reason);
            epoch = ++SessionEpoch;
            return true;
        }
        public static void Disconnect(string reason) { IsConnected = false; StateChanged?.Invoke(); }
        public static Task<(bool Ok, string? Error)> ConnectAsync(NodeItem n, bool force = false, int? expectedEpoch = null, CancellationToken ct = default, SessionIntent? intent = null) => Task.FromResult((false, (string?)"test stub"));
        public static Task<(bool Ok, string? Err)> SwitchNodeFastAsync(NodeItem n, int? expectedEpoch = null, CancellationToken ct = default, SessionIntent? intent = null) => Task.FromResult((false, (string?)"test stub"));
    }
    public static class SystemProxy
    {
        public static bool? EffectiveOverride;
        public static bool IsEffective => EffectiveOverride ?? IsEnabled;
        public static bool IsEnabled;
        public static string? CurrentServer;
        public static List<string> Enables = new();
        public static Action? OnEnable;
        public static bool EnableSucceeds = true;
        public static bool Enable(string server, bool repair = false) { Enables.Add(server); OnEnable?.Invoke(); return EnableSucceeds; }
    }
    public static class IncidentLog { public static void Record(string kind, string text, string? detail = null) { } }
    public static class CoreState { public static void Report(string state, int pid, string core, string detail, int? sessionEpoch = null) { } }
    public static class CoreStats
    {
        public record struct Snapshot(long Uplink, long Downlink);
        public static Func<Snapshot?> QueryHook = () => null;
        public static Snapshot? Query() => QueryHook();
        public static int ActiveConnections() => 0;
    }
    public static class XrayStats { public static bool Responsive; public static object? Query() => null; public static bool IsResponsive() => Responsive; }
}
namespace Neo.Core.Core
{
    public static class NodeGrouper
    {
        public static string RegionOf(string name) => "";
        public static string RegionDisplay(string region) => region;
    }
    public static class RealDelayTester
    {
        public static Task<int> RealPingAsync(NodeItem n, string runtimeDir, int timeoutMs = 10000, CancellationToken ct = default) => Task.FromResult(-1);
    }
}
namespace Neo.Core.Bridge
{
    public record FakeDecision(int Fails, string Decision, long? LastSwitchMs);
    public static class RustCoreBridge
    {
        public static FakeDecision? TryFailoverDecision(long time, bool ok, bool hadTraffic, int fails, long? lastSwitchMs) => null;
    }
}
namespace Microsoft.Win32
{
    public enum PowerModes { Resume }
    public sealed class PowerModeChangedEventArgs : EventArgs { public PowerModes Mode => PowerModes.Resume; }
    public static class SystemEvents { public static event EventHandler<PowerModeChangedEventArgs>? PowerModeChanged; }
}
namespace Microsoft.UI { }
namespace Windows.UI
{
    public struct Color { public static Color FromArgb(byte a, byte r, byte g, byte b) => new(); }
}
namespace Microsoft.UI.Xaml
{
    public class Application
    {
        public static Application Current { get; } = new();
        public Dictionary<string, object> Resources { get; } = new();
    }
}
namespace Microsoft.UI.Xaml.Media
{
    public class Brush { }
    public class SolidColorBrush(Windows.UI.Color color) : Brush { private readonly Windows.UI.Color _color = color; }
}
