using System.Collections.Concurrent;
using System.Diagnostics;
using Neo.Core.Models;

namespace Neo.Core.Core;

/// <summary>测速阶段：先测节点多的那一类，再自动切到另一类，最后统一重试失败节点。</summary>
public enum DelayTestPhase { Tcp, Udp, Retry, Done }

/// <summary>后台调用可降低并发/重试，并在每次探测前让出前台资源；手动测速沿用默认行为。</summary>
public sealed record DelayTestOptions(int MaxConcurrency = 6, int MaxRetries = DelayTestRunner.DefaultMaxRetries,
    Func<CancellationToken, Task>? BeforeProbe = null,
    Func<CancellationToken, CancellationTokenSource>? CreateProbeCancellation = null);

/// <summary>进度快照：界面只需要读这个，不用自己数节点。</summary>
public sealed record DelayTestProgress(
    DelayTestPhase Phase,
    string Label,
    int Done,
    int Total,
    int RetryRound,
    int Remaining,
    int Ok,
    int Failed,
    int Skipped,
    string Message,
    int RetryLimit = 0);

/// <summary>
/// 进度驱动的真连测速调度器。
///
///   目标：不再用「整轮 120 秒预算」把剩余节点标成未测，而是按节点数推进，直到每个节点都有结论。
///   顺序：TCP 组与 UDP 组比数量，多的先测；数量相同先 TCP；暂时不可达节点默认补测 1 轮。
///   并发：TCP 6、UDP 4；复测并发 4，不通过增加临时内核数量换取速度。
///   预算：单节点启动及探测合计由 DelayTestPlanner 决定（TCP 9s / UDP 15s）。
/// </summary>
public static class DelayTestRunner
{
    private const int TcpConcurrency = 6;
    private const int UdpConcurrency = 4;
    private const int RetryConcurrency = 4;
    public const int DefaultMaxRetries = 1;
    private const int MaxRetryRounds = 4;

    public static async Task<IReadOnlyDictionary<NodeItem, int>> RunAsync(
        IReadOnlyList<NodeItem> nodes, string runtimeDir,
        Action<NodeItem, int>? onResult, CancellationToken ct,
        bool availabilityRecheck,
        Action<IReadOnlyCollection<NodeItem>>? onUsableChecked,
        Action<DelayTestProgress>? onProgress,
        DelayTestOptions? options = null)
    {
        var all = (nodes ?? Array.Empty<NodeItem>()).Where(n => n != null).Distinct().ToList();
        var result = new ConcurrentDictionary<NodeItem, int>();

        static bool IsSkipped(NodeItem n) =>
            !n.IsValid || n.IsClashPassthrough || n.IsCustomConfig;

        var skipped = all.Where(IsSkipped).ToList();
        foreach (var s in skipped) result[s] = 0;
        var testable = all.Where(n => !IsSkipped(n)).ToList();

        if (testable.Count == 0)
        {
            onProgress?.Invoke(new DelayTestProgress(DelayTestPhase.Done, "", 0, 0, 0, 0, 0, 0, skipped.Count,
                $"没有可测节点（跳过 {skipped.Count}）"));
            return result;
        }

        var tcp = testable.Where(n => DelayTestPlanner.TransportOf(n) == DelayTransport.Tcp).ToList();
        var udp = testable.Where(n => DelayTestPlanner.TransportOf(n) == DelayTransport.Udp).ToList();
        var total = testable.Count;
        var retries = Math.Clamp(options?.MaxRetries ?? DefaultMaxRetries, 0, MaxRetryRounds);
        var done = 0;
        var sw = Stopwatch.StartNew();

        int OkCount() => testable.Count(n => result.TryGetValue(n, out var v) && v > 0);
        int FailCount() => testable.Count(n => result.TryGetValue(n, out var v) && v < 0);

        void Report(DelayTestPhase phase, string label, int retryRound, int remaining, string message = "") =>
            onProgress?.Invoke(new DelayTestProgress(phase, label, Volatile.Read(ref done), total, retryRound,
                remaining, OkCount(), FailCount(), skipped.Count, message, retries));

        async Task RunGroupAsync(List<NodeItem> group, DelayTestPhase phase, string label, int concurrency, int round)
        {
            if (group.Count == 0) return;
            var finished = 0;
            Report(phase, label, round, group.Count, round == 0 ? $"{label} 阶段开始（{group.Count} 个）" : "");
            await Parallel.ForEachAsync(group,
                new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Clamp(options?.MaxConcurrency ?? concurrency, 1, concurrency) },
                async (node, ict) =>
                {
                    int ms;
                    while (true)
                    {
                        if (options?.BeforeProbe != null) await options.BeforeProbe(ict).ConfigureAwait(false);
                        using var probeCancellation = options?.CreateProbeCancellation?.Invoke(ict);
                        var probeToken = probeCancellation?.Token ?? ict;
                        try
                        {
                            probeToken.ThrowIfCancellationRequested();
                            ms = (await RealDelayTester.RealPingDetailedAsync(node, runtimeDir, 0, probeToken).ConfigureAwait(false)).Ms;
                            probeToken.ThrowIfCancellationRequested();
                        }
                        catch (OperationCanceledException) when (ict.IsCancellationRequested) { throw; }
                        // Presentation suspension is not a failed node or a retry round.
                        // The probe's finally has released its temporary kernel before we wait.
                        catch (OperationCanceledException) when (probeToken.IsCancellationRequested) { continue; }
                        catch (Exception ex)
                        {
                            RealDelayTester.Logger?.Invoke(
                                $"测速调度：节点「{node.Name}」异常，按本机失败处理：[{ex.GetType().Name}] {ex.Message}", true);
                            ms = RealDelayTester.BatchFailed;
                        }
                        break;
                    }

                    result[node] = ms;
                    if (round == 0) Interlocked.Increment(ref done);
                    onResult?.Invoke(node, ms);
                    var f = Interlocked.Increment(ref finished);
                    Report(phase, label, round, Math.Max(0, group.Count - f));
                });
        }

        Log($"真连测速开始（{testable.Count} 个可测 · TCP {tcp.Count} / UDP {udp.Count} · 跳过 {skipped.Count}）");

        var order = tcp.Count >= udp.Count
            ? new[]
            {
                (Nodes: tcp, Phase: DelayTestPhase.Tcp, Label: "TCP", Concurrency: TcpConcurrency),
                (Nodes: udp, Phase: DelayTestPhase.Udp, Label: "UDP", Concurrency: UdpConcurrency),
            }
            : new[]
            {
                (Nodes: udp, Phase: DelayTestPhase.Udp, Label: "UDP", Concurrency: UdpConcurrency),
                (Nodes: tcp, Phase: DelayTestPhase.Tcp, Label: "TCP", Concurrency: TcpConcurrency),
            };

        foreach (var step in order)
            await RunGroupAsync(step.Nodes, step.Phase, step.Label, step.Concurrency, 0);

        for (var round = 1; round <= retries; round++)
        {
            ct.ThrowIfCancellationRequested();
            // Repeating a missing/broken local kernel cannot establish node health.
            // Certificate failures remain explicit and never trigger relaxed TLS.
            var failing = testable.Where(n => result.TryGetValue(n, out var v) && v == -1
                && n.RealDelayNote != "证书不受信").ToList();
            if (failing.Count == 0) break;
            Log($"真连测速：失败重试 {round}/{retries}（{failing.Count} 个）");
            await RunGroupAsync(failing, DelayTestPhase.Retry, $"重试 {round}/{retries}", RetryConcurrency, round);
        }

        var ok = OkCount();
        var failed = FailCount();
        Log($"真连测速完成：可测 {total} · 可用 {ok} · 超时/失败 {failed} · 跳过 {skipped.Count} · 耗时 {sw.Elapsed.TotalSeconds:F1}s");

        if (availabilityRecheck && !ct.IsCancellationRequested)
        {
            try
            {
                var candidates = testable
                    .Where(n => result.TryGetValue(n, out var v) && v > 0)
                    .OrderBy(n => result[n])
                    .Take(3)
                    .ToList();
                var usable = new ConcurrentBag<NodeItem>();
                if (candidates.Count > 0)
                {
                    Log($"可用性复核 {candidates.Count} 个（x.com，单次 4s）");
                    await Parallel.ForEachAsync(candidates,
                        new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 3 },
                        async (node, ict) =>
                        {
                            var ms = await RealDelayTester.ProbeUrlAsync(node, runtimeDir, "https://x.com/", 4000, ict)
                                .ConfigureAwait(false);
                            if (ms > 0) usable.Add(node);
                            Log($"可用性复核：「{node.Name}」→ {(ms > 0 ? "可用" : "不可用")}");
                        });
                }
                onUsableChecked?.Invoke(usable.ToList());
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Log($"可用性复核失败（不影响延迟结果）：[{ex.GetType().Name}] {ex.Message}", true);
                onUsableChecked?.Invoke(Array.Empty<NodeItem>());
            }
        }

        onProgress?.Invoke(new DelayTestProgress(DelayTestPhase.Done, "", total, total, 0, 0, ok, failed, skipped.Count,
            $"完成 {total}/{total} · 可用 {ok}" + (failed > 0 ? $" · 超时/失败 {failed}" : "")
            + (skipped.Count > 0 ? $" · 跳过 {skipped.Count}" : "")));
        return result;
    }

    private static void Log(string msg, bool isError = false)
    {
        try { RealDelayTester.Logger?.Invoke(msg, isError); } catch { }
    }
}
