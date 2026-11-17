using System.Diagnostics;
using Neo.Core;
using Neo.Core.Core;
using Neo.Core.Models;

namespace Neo.App.Services;

/// 启动后台任务（设置 → 通用）：
///   · 「启动自动测速」：启动后跑一次全部节点的真连延迟，节点列表打开即实测值。
/// 开关关闭则整个服务不启动。
/// 约束：
///   · 进程内幂等，只跑一次；
///   · 测速失败（-1 / 本机失败）不写回节点，保持「未测速」；结果同时写 DelayCache；
///   · 每测出一批结果通知 UI 一次（节流），避免几十个节点逐个重绘。
public static class StartupSpeedTest
{
    /// <summary>Each stored latency result, on the UI thread; list membership is unchanged.</summary>
    internal static event Action<NodeItem>? LatencyChanged;
    private static int _started;
    private static readonly object Gate = new();
    private static CancellationTokenSource? _cts;
    private static readonly object TrafficGate = new();
    private static (DateTime At, long Total)? _lastTrafficSample;
    private static bool _trafficBusy;

    public static void Stop()
    {
        lock (Gate) { try { _cts?.Cancel(); } catch (ObjectDisposedException) { } }
    }

    /// 节点就绪的最长等待（启动时订阅缓存一般同步就有，慢的情况兜底）。
    private static readonly TimeSpan MaxWaitForNodes = TimeSpan.FromSeconds(25);

    /// 首屏几秒内先不抢带宽（订阅刷新 / 窗口首帧都在这个窗口里）。
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(4);

    /// 通知节流：每得到这么多个结果刷新一次节点列表。
    private const int NotifyEvery = 8;

    public static void Schedule()
    {
        try
        {
            if (!AppState.State.StartupSpeedTest) return;
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            _ = Task.Run(RunAsync);
        }
        catch (Exception ex)
        {
            LogBus.App($"启动后台任务调度失败（不影响使用）：[{ex.GetType().Name}] {ex.Message}", "Warning");
        }
    }

    private static async Task RunAsync()
    {
        using var cts = new CancellationTokenSource();
        lock (Gate) _cts = cts;
        var ct = cts.Token;
        try
        {
            var deadline = DateTime.UtcNow + MaxWaitForNodes;
            while (AppState.Nodes.Count == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(500, ct);

            if (AppState.Nodes.Count == 0)
            {
                LogBus.App("启动后台任务跳过：当前没有节点", "Debug");
                return;
            }

            await Task.Delay(SettleDelay, ct);
            if (App.IsShuttingDown) return;

            if (AppState.State.StartupSpeedTest && !App.IsShuttingDown)
                await RunLatencyTestAsync(ct);
        }
        catch (OperationCanceledException)
        {
            LogBus.App("启动后台测速已取消", "Debug");
            NotifyNodesChanged();
        }
        catch (Exception ex)
        {
            LogBus.App($"启动后台任务失败（不影响使用）：[{ex.GetType().Name}] {ex.Message}", "Warning");
        }
        finally { lock (Gate) { if (ReferenceEquals(_cts, cts)) _cts = null; } }
    }

    private static async Task RunLatencyTestAsync(CancellationToken ct)
    {
        var nodes = AppState.Nodes.Distinct().Where(n => n.IsValid && !DelayCache.IsFresh(n, TimeSpan.FromMinutes(10)))
            .OrderBy(n => ReferenceEquals(n, AppState.CurrentNode) ? 0 : 1)
            .ThenBy(n => NodeQualityHistory.Shared.Score(AppState.NodeKey(n), n.RealDelay)).ToList();
        var sw = Stopwatch.StartNew();
        LogBus.App($"启动自动测速开始（{nodes.Count} 个待测节点 · 后台低并发 · 忙时暂停）");

        // 进度驱动：不再用 3 分钟总预算截断；只保留用户/应用退出时的取消。
        var ok = 0;
        var reported = 0;
        var identities = nodes.ToDictionary(n => n, AppState.NodeKey);
        // 先完成当前/最优的少量候选，避免被按协议分组后的全量队列挡住。
        foreach (var group in new[] { nodes.Take(3).ToList(), nodes.Skip(3).ToList() })
        {
            if (group.Count == 0) continue;
            await RealDelayTester.RealPingBatchAsync(
            group, NeoPaths.RuntimeDir,
            onResult: (node, ms) =>
            {
                if (App.IsShuttingDown || ct.IsCancellationRequested || !AppState.State.StartupSpeedTest
                    || !identities.TryGetValue(node, out var identity) || identity != AppState.NodeKey(node)) return;
                if (ms > 0)
                {
                    node.RealDelay = ms;
                    DelayCache.Set(node, ms, node.RealDelayNote);
                    NotifyLatencyChanged(node);
                    Interlocked.Increment(ref ok);
                }
                // 其余（-1 超时 / BatchFailed 本机失败）不写回：保持「未测速」，下次再测

                if (Interlocked.Increment(ref reported) % NotifyEvery == 0) NotifyNodesChanged();
            },
            ct: ct, options: new DelayTestOptions(MaxConcurrency: 2, MaxRetries: 1, BeforeProbe: WaitForIdleAsync,
                CreateProbeCancellation: UiActivity.CreateOptionalProbeCancellation));
        }

        NotifyNodesChanged();
        LogBus.App($"启动自动测速完成：{ok}/{nodes.Count} 可达 · 耗时 {sw.Elapsed.TotalSeconds:F1}s");
    }

    private static async Task WaitForIdleAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (App.IsShuttingDown || !AppState.State.StartupSpeedTest) throw new OperationCanceledException(ct);
            if (UiActivity.IsVisible && !ConnectionService.IsConnecting && !ConnectionService.IsSessionBusy && !HasForegroundTraffic()) return;
            await Task.Delay(1000, ct);
        }
    }

    private static bool HasForegroundTraffic()
    {
        var at = StatsSampler.SampledAt;
        if (DateTime.Now - at > TimeSpan.FromSeconds(4) || StatsSampler.Latest is not { } sample) return false;
        var total = sample.Uplink + sample.Downlink;
        lock (TrafficGate)
        {
            if (_lastTrafficSample is { } previous && at > previous.At)
            {
                var delta = total - previous.Total;
                _trafficBusy = delta >= 0 && delta / (at - previous.At).TotalSeconds > 128 * 1024;
            }
            if (_lastTrafficSample == null || at > _lastTrafficSample.Value.At)
                _lastTrafficSample = (at, total);
            return _trafficBusy;
        }
    }

    /// <summary>Publish individual results without rebuilding the sidebar and subscription page.</summary>
    internal static void NotifyLatencyChanged(NodeItem node)
    {
        if (App.IsShuttingDown) return;
        var dq = ConnectionService.UiDispatcher;
        if (dq == null || dq.HasThreadAccess) LatencyChanged?.Invoke(node);
        else dq.TryEnqueue(() => { if (!App.IsShuttingDown) LatencyChanged?.Invoke(node); });
    }

    /// 把批次汇总通知 marshal 回 UI 线程；逐节点进度使用轻量 LatencyChanged。
    private static void NotifyNodesChanged()
    {
        if (App.IsShuttingDown) return;
        var dq = ConnectionService.UiDispatcher;
        if (dq == null || dq.HasThreadAccess)
        {
            AppState.NotifyNodesChanged();
            return;
        }
        try { dq.TryEnqueue(AppState.NotifyNodesChanged); }
        catch { }
    }
}
