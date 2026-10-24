using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Neo.Core.Models;

namespace Neo.Core.Core;

/// <summary>
/// 「TCP 握手延迟」快速测速（Tcping）：**不启内核、不发 HTTP**，
/// 只对节点 (地址, 端口) 做 TcpClient.ConnectAsync 并计时，几秒内可测完全部节点。
///
///   0  = 未测（跳过）：hy2/TUIC 等 UDP 协议没有 TCP 端口；或节点地址/端口无效。
///   -1 = 超时 / 连不上 / 域名解析失败 / 总预算耗尽仍未测。
///   >0 = 实际 TCP 建连耗时（毫秒，不含 DNS 解析；DNS 在测试前单独预热解析）。
///
/// 设计约束：
///   · 不写 <see cref="NodeItem.RealDelay"/>。本类只返回结果，写哪个字段由上层决定；
///   · 不依赖 LogBus/Logger，也不在类内打日志（保持纯函数，跳过/预算原因可由 0/-1 区分，上层记录）；
///   · 计时只覆盖 TCP 建连（Stopwatch 在 DNS 解析完成后才启动）。
/// </summary>
public static class TcpDelayTester
{
    /// <summary>默认总预算（秒）：防止大量死节点把整批拖长；超预算的未测节点按 -1 处理。</summary>
    public const int DefaultBudgetSeconds = 15;

    /// <summary>
    /// 批量 TCP 握手测速。
    /// </summary>
    /// <param name="nodes">待测节点（同一节点重复传入只会测一次）。</param>
    /// <param name="concurrency">并发上限，默认 32。</param>
    /// <param name="timeoutMs">单次 TCP 建连超时，默认 3000ms。</param>
    /// <param name="onResult">每完成一个节点立即回调（成功毫秒 / -1 超时或解析失败 / 预算耗尽补的 -1）；
    /// 跳过（0）的 UDP/无效节点不回调。</param>
    /// <param name="ct">外部取消；取消时抛出 OperationCanceledException。</param>
    /// <param name="budgetSeconds">本批总预算（秒），默认 15；&lt;=0 表示不设总预算。</param>
    public static async Task<Dictionary<NodeItem, int>> TestBatchAsync(
        IReadOnlyList<NodeItem> nodes,
        int concurrency = 32,
        int timeoutMs = 3000,
        Action<NodeItem, int>? onResult = null,
        CancellationToken ct = default,
        int budgetSeconds = DefaultBudgetSeconds)
    {
        var result = new ConcurrentDictionary<NodeItem, int>();
        // 已回调过的节点：DNS 失败与预算补 -1 都走这里去重，保证每个节点最多回调一次。
        var reported = new ConcurrentDictionary<NodeItem, byte>();
        if (nodes == null || nodes.Count == 0) return new Dictionary<NodeItem, int>();

        var maxDop = concurrency > 0 ? concurrency : 32;
        var effectiveTimeoutMs = timeoutMs > 0 ? timeoutMs : 1;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (budgetSeconds > 0) budget.CancelAfter(TimeSpan.FromSeconds(budgetSeconds));
        var budgetToken = budget.Token;

        // 1) 先填「跳过」：UDP 协议没有 TCP 端口；地址/端口无效也没法连。
        //    这两类结果 = 0（未测），不回调（否则 UI 会显示成 0ms）。
        var tcpNodes = new List<NodeItem>(nodes.Count);
        foreach (var node in nodes)
        {
            if (node == null) continue;
            //   测出来的「超时」不代表节点坏（UDP 类协议本来就没有 TCP 端口）。
            if (!node.IsValid
                || node.Protocol is NodeProtocol.Hysteria2 or NodeProtocol.TUIC
                || node.IsClashPassthrough
                || node.Protocol is NodeProtocol.WireGuard
                || node.IsCustomConfig
                || (node.Network ?? "").ToLowerInvariant() is "kcp" or "mkcp")
            {
                result[node] = 0;
                continue;
            }
            tcpNodes.Add(node);
        }

        // 2) DNS 预热：先把域名统一解析成 IPv4（不计入握手耗时）；解析失败 → -1。
        //    节点域名不在墙内，走系统 DNS 即可。
        var targets = new ConcurrentDictionary<NodeItem, IPAddress>();
        try
        {
            await Parallel.ForEachAsync(tcpNodes,
                new ParallelOptions { CancellationToken = budgetToken, MaxDegreeOfParallelism = maxDop },
                async (node, token) =>
                {
                    var ip = await ResolveIpv4Async(node.Server, token).ConfigureAwait(false);
                    if (ip == null)
                    {
                        result[node] = -1;
                        reported[node] = 0;
                        onResult?.Invoke(node, -1);
                    }
                    else
                    {
                        targets[node] = ip;
                    }
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { /* 总预算在 DNS 阶段耗尽：剩余节点在末尾统一补 -1 */ }

        // 3) TCP 握手计时：只对已解析出 IP 的节点做；每完成一个立刻回调（UI 实时）。
        try
        {
            await Parallel.ForEachAsync(targets.Keys,
                new ParallelOptions { CancellationToken = budgetToken, MaxDegreeOfParallelism = maxDop },
                async (node, token) =>
                {
                    var ms = await ConnectOnceAsync(targets[node], node.Port, effectiveTimeoutMs, token).ConfigureAwait(false);
                    result[node] = ms;
                    reported[node] = 0;
                    onResult?.Invoke(node, ms);
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { /* 总预算在 TCP 阶段耗尽：剩余节点在末尾统一补 -1 */ }

        // 4) 总预算耗尽后的兜底：还没出结果的节点补 -1 并各回调一次，保证上层不会停在半路。
        foreach (var node in tcpNodes)
        {
            if (result.ContainsKey(node)) continue;
            result[node] = -1;
            if (reported.TryAdd(node, 0)) onResult?.Invoke(node, -1);
        }

        return new Dictionary<NodeItem, int>(result);
    }

    /// <summary>
    /// 解析域名为第一个 IPv4 地址；IP 字面量直接返回（IPv4/IPv6 都不查 DNS）。
    /// 解析失败/没有 IPv4 → null（由调用方按 -1 处理）。超时通过 token 控制。
    /// </summary>
    private static async Task<IPAddress?> ResolveIpv4Async(string host, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        var trimmed = host.Trim();
        if (IPAddress.TryParse(trimmed.Trim('[', ']'), out var literal)) return literal;

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(trimmed).WaitAsync(ct).ConfigureAwait(false);
            return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    /// <summary>
    /// 单节点 TCP 建连计时：Stopwatch 只包住 ConnectAsync（DNS 已完成，不计入）。
    /// 返回 &gt;0 成功毫秒；超时/连不上 → -1；外部取消除外（原样抛）。
    /// </summary>
    private static async Task<int> ConnectOnceAsync(IPAddress ip, int port, int timeoutMs, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);
            await client.ConnectAsync(ip, port, timeoutCts.Token).ConfigureAwait(false);
            sw.Stop();
            return (int)sw.ElapsedMilliseconds;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            // 超时、连接被拒、网络不可达等：统一按「连不上」-1。
            return -1;
        }
    }
}
