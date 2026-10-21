using System.Diagnostics;
using Neo.Core;
using Neo.Core.Core;

namespace Neo.App.Services;

/// 启动自动测速（设置 → 通用 →「启动自动测速」）。
///   应用启动后等节点就绪，在后台跑一次「真连测速」，与节点页的「真连测速」按钮
///   走同一套实现（RealDelayTester.RealPingBatchAsync：单实例批量、每节点独立 socks 端口）。
/// 约束：
///   · 只在启动时跑一次（进程内幂等），开关关闭时不启动；
///   · 失败（-1 / 本机失败）不写回节点，保持「未测速」，避免把「没测到」显示成「超时」；
///   · 结果同时写 DelayCache，订阅刷新重建节点时不会丢；
///   · 每测出一批结果通知 UI 一次（节流），避免 68 个节点逐个重绘。
public static class StartupSpeedTest
{
    private static int _started;

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
            LogBus.App($"启动自动测速调度失败（不影响使用）：[{ex.GetType().Name}] {ex.Message}", "Warning");
        }
    }

    private static async Task RunAsync()
    {
        try
        {
            var deadline = DateTime.UtcNow + MaxWaitForNodes;
            while (AppState.Nodes.Count == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(500);

            if (AppState.Nodes.Count == 0)
            {
                LogBus.App("启动自动测速跳过：当前没有节点", "Debug");
                return;
            }

            await Task.Delay(SettleDelay);
            if (!AppState.State.StartupSpeedTest) return;   // 等待期间用户关掉了开关
            if (App.IsShuttingDown) return;

            var nodes = AppState.Nodes.ToList();
            var sw = Stopwatch.StartNew();
            LogBus.App($"启动自动测速开始（{nodes.Count} 个节点 · 后台）");

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var ok = 0;
            var reported = 0;
            await RealDelayTester.RealPingBatchAsync(
                nodes, NeoPaths.RuntimeDir,
                onResult: (node, ms) =>
                {
                    if (ms > 0)
                    {
                        node.RealDelay = ms;
                        DelayCache.Set(node, ms);
                        Interlocked.Increment(ref ok);
                    }
                    // 其余（-1 超时 / BatchFailed 本机失败）不写回：保持「未测速」，下次再测

                    if (Interlocked.Increment(ref reported) % NotifyEvery == 0) NotifyNodesChanged();
                },
                ct: cts.Token);

            NotifyNodesChanged();
            LogBus.App($"启动自动测速完成：{ok}/{nodes.Count} 可达 · 耗时 {sw.Elapsed.TotalSeconds:F1}s");
        }
        catch (OperationCanceledException)
        {
            LogBus.App("启动自动测速超时（不影响使用）", "Warning");
            NotifyNodesChanged();
        }
        catch (Exception ex)
        {
            LogBus.App($"启动自动测速失败（不影响使用）：[{ex.GetType().Name}] {ex.Message}", "Warning");
        }
    }

    /// 把 NodesChanged 通知 marshal 回 UI 线程（订阅者里有直接改 XAML 的）。
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
