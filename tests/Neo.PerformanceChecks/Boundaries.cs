using Neo.Core.Core;
using Neo.Core.Models;

namespace Neo.App { public static class App { public static bool IsShuttingDown; } }
namespace Neo.App.Services
{
    public static class AppState
    {
        public static TestSettings State { get; } = new();
        public static List<NodeItem> Nodes = new();
        public static NodeItem? CurrentNode;
        public static string NodeKey(NodeItem node) => NodeIdentity.Fingerprint(node);
        public static int Notifications;
        public static void NotifyNodesChanged() => Interlocked.Increment(ref Notifications);
    }
    public sealed class TestSettings
    {
        public int LogRetentionDays = 7;
        public string LogLevel = "warning";
        public bool StartupSpeedTest = true;
    }
    public static class ConnectionService
    {
        public static bool IsConnecting;
        public static bool IsSessionBusy;
        public static FakeDispatcher? UiDispatcher;
    }
    public sealed class FakeDispatcher
    {
        public bool HasThreadAccess => true;
        public bool TryEnqueue(Action action) { action(); return true; }
    }
    public static class StatsSampler
    {
        public readonly record struct Snapshot(long Uplink, long Downlink);
        public static Snapshot? Latest;
        public static DateTime SampledAt;
    }
}
namespace Neo.Core.Core
{
    // Keep the production scheduler. Only node network operations are controlled here.
    public static class RealDelayTester
    {
        public const int BatchFailed = -2;
        public static Action<string, bool>? Logger;
        public static Func<NodeItem, CancellationToken, Task<int>> Probe = (_, _) => Task.FromResult(100);
        public static List<NodeItem[]> Batches = new();
        public static async Task<(int Ms, string? Note)> RealPingDetailedAsync(NodeItem node, string runtime, int timeout, CancellationToken ct)
            => (await Probe(node, ct), null);
        public static Task<int> ProbeUrlAsync(NodeItem node, string runtime, string url, int timeout, CancellationToken ct) => Probe(node, ct);
        public static Task<IReadOnlyDictionary<NodeItem, int>> RealPingBatchAsync(
            IReadOnlyList<NodeItem> nodes, string runtimeDir, int timeoutMs = 0, Action<NodeItem, int>? onResult = null,
            CancellationToken ct = default, bool availabilityRecheck = false,
            Action<IReadOnlyCollection<NodeItem>>? onUsableChecked = null, Action<DelayTestProgress>? onProgress = null,
            DelayTestOptions? options = null)
        {
            Batches.Add(nodes.ToArray());
            return DelayTestRunner.RunAsync(nodes, runtimeDir, onResult, ct, availabilityRecheck, onUsableChecked, onProgress, options);
        }
    }
}
namespace Microsoft.UI { }
namespace Windows.UI { public struct Color { public static Color FromArgb(byte a, byte r, byte g, byte b) => new(); } }
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
    public class SolidColorBrush(Windows.UI.Color color) : Brush { }
}
