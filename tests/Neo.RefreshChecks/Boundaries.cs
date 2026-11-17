using System.Collections.Concurrent;
using Neo.App.Services;

namespace Neo.App { public static class App { public static bool IsShuttingDown; } }
// WinUI presentation boundary. The retention tests inspect ordering and counts;
// brush construction and XAML layout are checked in the native application.
namespace Neo.App.Ui
{
    public sealed class LogDisplayRow
    {
        public string HeadText { get; }
        public LogDisplayRow(LogEntry entry) => HeadText = entry.Text;
    }
}
namespace Neo.App.Services
{
    public sealed class LogEntry { public string Text { get; init; } = ""; public string Level { get; init; } = "Info"; }
    public static class LogBus
    {
        public static event Action<LogEntry>? Appended;
        public static void App(string message, string level = "Info") { }
        public static void Clear() { }
        public static void Emit(LogEntry entry) => Appended?.Invoke(entry);
    }
    public sealed class FakeCore { public bool IsRunning => true; public int Pid => Environment.ProcessId; }
    public static partial class ConnectionService
    {
        public static bool IsConnected;
        public static int SessionEpoch;
        public static int CorePid = Environment.ProcessId;
    }
    public static class CoreStats
    {
        public readonly record struct Snapshot(long Uplink, long Downlink);
        public static Func<Snapshot?> Read = () => new Snapshot(1000, 2000);
        public static Snapshot? Query() => Read();
        public static int ActiveConnections() => 3;
    }
    public static class UsageTracker { public static void SessionBreak() { } }
}
namespace Neo.Core
{
    public static class NeoPaths { public static string RuntimeDir = Path.Combine(Path.GetTempPath(), "neo-refresh-no-runtime"); }
    public static class LocalPorts { public const int CoreStatsApi = 39001; }
}
namespace Neo.Core.Core
{
    public static class XrayStatsClient
    {
        public readonly record struct Counter(string Name, long Value);
        public static IReadOnlyList<Counter> Counters = Array.Empty<Counter>();
        public static Task<IReadOnlyList<Counter>> QueryAsync(int port) => Task.FromResult(Counters);
    }
}
namespace Microsoft.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }
    public sealed class RoutedEventArgs : EventArgs { }
}
namespace Microsoft.UI.Dispatching
{
    public sealed class DispatcherQueue
    {
        private readonly ConcurrentQueue<Action> _queue = new();
        public int Posts;
        public bool Reject;
        public bool TryEnqueue(Action action)
        {
            if (Reject) return false;
            Interlocked.Increment(ref Posts); _queue.Enqueue(action); return true;
        }
        public void Pump() { while (_queue.TryDequeue(out var action)) action(); }
    }
    public sealed class DispatcherQueueTimer
    {
        public bool IsRunning { get; private set; }
        public void Start() => IsRunning = true;
        public void Stop() => IsRunning = false;
    }
}
namespace Microsoft.UI.Xaml.Shapes
{
    public sealed class Rectangle
    {
        public double RadiusX { get; set; }
        public double RadiusY { get; set; }
        public double Width { set => Neo.RefreshChecks.UiWrites.Count++; }
        public double Height { set => Neo.RefreshChecks.UiWrites.Count++; }
        public Microsoft.UI.Xaml.Visibility Visibility { set => Neo.RefreshChecks.UiWrites.Count++; }
    }
}
namespace Microsoft.UI.Xaml.Controls
{
    public static class Canvas
    {
        public static void SetZIndex(object element, int value) => Neo.RefreshChecks.UiWrites.Count++;
        public static void SetLeft(object element, double value) => Neo.RefreshChecks.UiWrites.Count++;
        public static void SetTop(object element, double value) => Neo.RefreshChecks.UiWrites.Count++;
    }
}
namespace Neo.RefreshChecks
{
    public static class UiWrites { public static long Count; }
    public sealed class ChartHost { public List<object> Children { get; } = new(); }
    public sealed class FakeLogList
    {
        public List<object> Items { get; } = new();
        public Action? OnScroll;
        public void ScrollIntoView(object item) => OnScroll?.Invoke();
    }
}
