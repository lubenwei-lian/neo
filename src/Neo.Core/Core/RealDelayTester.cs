using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Neo.Core.Models;

namespace Neo.Core.Core;

public static class RealDelayTester
{
    ///   原先把 -1（= 真超时）当唯一失败语义，导致内核起不来时所有节点被显示成「超时」。
    ///   现在 -2 明确表示「本机没能跑起来这次测速」，UI 应据此提示「本机问题」而非「节点超时」。
    public const int BatchFailed = -2;
    ///   批量测速原先用 `catch { }` 把整批异常吞掉，导致「本机原因」造成的失败
    ///   在日志里零痕迹、而界面上全部节点显示「超时」→ 用户误以为是节点的问题。
    public static Action<string, bool>? Logger { get; set; }   // (message, isError)

    private static void Log(string msg, bool isError = false)
    {
        try { Logger?.Invoke(msg, isError); } catch { }
    }
    public const int DefaultTimeoutMs = 10000;

    // ★ 2026-10-22 保守模式（用户反馈批量测速会被商家限流）：
    //   RealPingBatchAsync 原先把全部节点（最多 68 个）一次性打出去 → 容易被判异常流量。
    //   改为：同时最多 BatchMaxConcurrency 条 + 相邻节点启动错峰 BatchStaggerBaseMs ± 抖动。
    //
    // ★ 2026-10-23 提速（用户抱怨 68 节点一次要 1.5–2 分钟）：并发 4→8→10、错峰 200±50→100±30，
    //   批量路径再去掉「预热请求」（只发一次计时请求，延迟口径按首字节时间）。
    //   连接数仍受控，预期总耗时降到原来的 1/3–1/4。
    /// <summary>批量真连测速时，同时在跑的节点探测数上限（10，保守但比 4 快）。</summary>
    private const int BatchMaxConcurrency = 10;

    /// <summary>相邻两个节点开始探测的基准间隔（毫秒）；实际为 100±30ms，即 70–130ms。</summary>
    private const int BatchStaggerBaseMs = 100;

    /// <summary>错峰间隔的随机抖动（±毫秒）：避免整齐的机器节奏被识别成扫描。</summary>
    private const int BatchStaggerJitterMs = 30;

    // ★ 2026-10-22 可用性复核：延迟最低 ≠ 能用。
    //   实测 AWS日本02 延迟 115ms（全场最低）但 x.com 12s 超时，而延迟更高的 AWS日本06 正常。
    //   所以批量延迟测完后，对延迟最低的前 3 名节点，用**同一个临时内核实例**的 socks 端口
    //   **并发**各请求一次 x.com（每个候选独立 socks 端口与连接）：拿到 2xx/3xx 才算可用。
    //   任一成功立即返回，全部失败则等全部结束；4s 单次超时、6s 总预算兜底。
    /// <summary>可用性复核目标：能刷推才算「可用」（2xx/3xx 即可，不要求正文）。</summary>
    private const string AvailabilityProbeUrl = "https://x.com/";

    /// <summary>可用性复核取延迟最低的前 N 名。</summary>
    private const int AvailabilityRecheckTop = 3;

    /// <summary>单次可用性复核请求的超时（毫秒）；并发探测，3 个同时跑。</summary>
    private const int AvailabilityRecheckTimeoutMs = 4000;

    /// <summary>
    /// 复核阶段的总预算（秒），仅作兜底：3 个并发、单次 4s，正常最多等约 4s；
    /// 超预算就停掉剩余候选（已完成的结论照用，已测延迟不受影响）。
    /// </summary>
    private const int AvailabilityRecheckBudgetSeconds = 6;

    /// <summary>复核请求的 UA：x.com 对空 UA 可能直接 4xx，会把所有节点误判成不可用。</summary>
    private const string AvailabilityProbeUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36";

    private const string Target = "https://www.google.com/generate_204";

    /// 延迟/可用性探测的目标序列（按顺序退让，第一个成功即采用）。
    /// 为什么不止 google：2026-10-22 在本机实测同一节点 108 秒 36 次采样里
    ///   x.com 36/36 成功，而 google/generate_204 只有 24/36（其余 5s 超时）——
    ///   单目标会把「google 这条线被限速/抖动」误判成「节点死了」，
    ///   而故障转移挑备选时正是用这个结果判定存活，会把整组备选判死。
    /// cloudflare 204 是明文 HTTP、开销比 TLS 小，且 xray observatory 已在用（同口径）。
    private static readonly string[] ProbeUrls =
    {
        // ★ 2026-10-23 提速：cloudflare 204 放第一位 —— 明文 HTTP、无 TLS 握手，
        //   单次开销比 google 的 https/generate_204 小一大截（实测节点上 0.2–0.5s vs 0.6–2s），
        //   68 节点批量测速时这一段直接决定总耗时。google 退到第二位做交叉印证。
        "http://cp.cloudflare.com/generate_204",
        Target,
    };

    public static async Task<int> RealPingAsync(NodeItem node, string runtimeDir,
        int timeoutMs = DefaultTimeoutMs, CancellationToken ct = default)
    {
        if (!node.IsValid) return -1;
        //   Clash 直通节点没有可单独拨号的出站（整份 YAML 交给 mihomo），不支持单点测速。
        if (node.IsClashPassthrough) return 0;

        // ★ 2026-10-23 第二批：内核选择改为「协议 + 能力」双判定（NodeItem.RequiresSingBox）：
        //   anytls / ss-plugin / h2 / hy2 gecko / 明文 vless 都只能走 sing-box；
        //   旧实现只看 hy2/tuic → 这些节点用 xray 测速必失败（配置被内核拒绝），界面显示「超时」。
        bool useSingBox = node.Protocol is NodeProtocol.Hysteria2 or NodeProtocol.TUIC
            || (node.RequiresSingBox && !node.RequiresXray);
        var exe = Path.Combine(runtimeDir, useSingBox ? "sing-box.exe" : "xray.exe");
        if (!File.Exists(exe))
        {
            Log($"单节点测速失败（本机原因，非节点问题）：内核不存在 {exe}", isError: true);
            return BatchFailed;
        }

        var port = FreePort();
        var cfgPath = Path.Combine(Path.GetTempPath(), $"neo-realtest-{port}-{Guid.NewGuid():N}.json");
        CoreProcess? core = null;
        var tail = new ConcurrentQueue<string>();
        var started = false;
        try
        {
            var cfg = useSingBox
                ? SingBoxConfigBuilder.BuildTest(node, port)
                : XrayConfigBuilder.BuildTest(node, port);
            await File.WriteAllTextAsync(cfgPath, cfg, ct);
            core = new CoreProcess(exe, runtimeDir);
            core.LogLine += line => AppendTail(tail, line);
            core.Start(cfgPath);

            var ready = false;
            for (var i = 0; i < 40 && !ready; i++)
            {
                ct.ThrowIfCancellationRequested();
                //   CoreProcess.Start() 不会因此抛异常，随后所有 socks 探测只会「连接被拒」
                //   → 全部 -1，界面把本机故障显示成节点「超时」。这里先看内核是否还活着。
                if (!core.IsRunning)
                {
                    Log($"单节点测速失败（本机原因，非节点问题）：内核启动后立即退出。内核日志尾部：{TailText(tail)}", isError: true);
                    return BatchFailed;
                }
                try
                {
                    using var probe = new TcpClient();
                    using var pcts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    pcts.CancelAfter(250);
                    await probe.ConnectAsync("127.0.0.1", port, pcts.Token);
                    ready = true;
                }
                catch when (!ct.IsCancellationRequested)
                {
                    await Task.Delay(120, ct);
                }
            }
            if (!core.IsRunning)
            {
                Log($"单节点测速失败（本机原因，非节点问题）：内核启动后立即退出。内核日志尾部：{TailText(tail)}", isError: true);
                return BatchFailed;
            }
            if (!ready)
            {
                Log($"单节点测速失败（本机原因，非节点问题）：内核在等待窗口内未监听测速端口。内核日志尾部：{TailText(tail)}", isError: true);
                return BatchFailed;
            }
            started = true;

            using var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy(new Uri($"socks5://127.0.0.1:{port}")),
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromMilliseconds(timeoutMs),
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };

            // 多目标退让：google 204 失败（超时/限速）时再用 cloudflare 204 判一次，
            // 避免「google 抖一下」被当成节点不可用。
            foreach (var url in ProbeUrls)
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                    sw.Stop();
                    var code = (int)resp.StatusCode;
                    if (code is 200 or 204) return (int)sw.ElapsedMilliseconds;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { /* 换下一个目标 */ }
            }
            return -1;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return -1;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (!started)
        {
            //   原先一律 return -1（界面显示「超时」），用户会误判节点坏。
            Log($"单节点测速失败（本机原因，非节点问题）：[{ex.GetType().Name}] {ex.Message}", isError: true);
            return BatchFailed;
        }
        catch
        {
            return -1;
        }
        finally
        {
            core?.Stop();
            try { File.Delete(cfgPath); } catch { }
        }
    }

    /// 通过「已连接的本地代理」测延迟。用于连接后自动补测当前节点。
    /// 只成功 3 次（-1/-1/-1/4093/587/4205），因为它要新起一个内核进程、
    /// 等它监听随机端口、再打真实链路，任何一步慢一点就判 -1。
    ///   ① 测的就是用户当前实际在走的链路，比另起内核更贴近真实体验；
    ///   ② 不额外起进程，更轻；
    ///   ③ 不会因为「临时内核没起来」而误报超时。
    public static async Task<int> PingViaSocksAsync(int socksPort, string target = Target,
        int timeoutMs = DefaultTimeoutMs, int rounds = 3, CancellationToken ct = default)
    {
        var best = -1;
        for (var i = 0; i < rounds; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var handler = new SocketsHttpHandler
                {
                    Proxy = new WebProxy(new Uri($"socks5://127.0.0.1:{socksPort}")),
                    UseProxy = true,
                    ConnectTimeout = TimeSpan.FromMilliseconds(timeoutMs),
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                };
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };

                // 默认目标时按 ProbeUrls 退让（google 抖动时换 cloudflare 再判一次）；
                // 调用方显式指定 target 时只用它，保持接口语义不变。
                foreach (var url in target == Target ? ProbeUrls : new[] { target })
                {
                    try
                    {
                        var sw = Stopwatch.StartNew();
                        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                        sw.Stop();
                        var code = (int)resp.StatusCode;
                        if (code is 200 or 204)
                        {
                            var ms = (int)sw.ElapsedMilliseconds;
                            if (best < 0 || ms < best) best = ms;
                            break;
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch { /* 换下一个目标 */ }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* 单次失败不影响其他轮次 */ }

            if (i < rounds - 1) await Task.Delay(300, ct);
        }
        return best;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>留存内核日志尾部（最多 40 行），供「内核秒退」时写 Error 日志用。</summary>
    private static void AppendTail(ConcurrentQueue<string> tail, string line)
    {
        tail.Enqueue(line);
        while (tail.Count > 40) tail.TryDequeue(out _);
    }

    private static string TailText(ConcurrentQueue<string> tail)
    {
        var lines = tail.ToArray();
        if (lines.Length == 0) return "(内核未输出日志)";
        var text = string.Join(" | ", lines.TakeLast(8).Select(l => l.Length > 300 ? l[..300] + "…" : l));
        return text.Replace("\r", " ").Replace("\n", " ");
    }

    /// 单实例批量测速：xray 系节点编入单实例（每节点独立 socks 端口），一次进程启动全并行测完；
    /// hy2/tuic（sing-box）节点数量少，沿用逐节点单测本地并行。返回各节点延迟（-1=不通）。
    ///
    /// ★ 2026-10-22 可用性复核（默认关闭，仅全量真连测速的调用方开启）：
    ///   批量延迟测完后，按延迟从低到高取前 3 名，用**同一个临时内核实例**的 socks 端口
    ///   **并发**请求一次 https://x.com/（每个候选独立 socks 端口/连接，单次超时 4s；
    ///   任一成功立即返回，全部失败则等全部结束），2xx/3xx 才算「可用」。
    ///   整个复核阶段有 6s 总预算兜底：超预算就停止剩余候选，已完成的结论照用。
    ///   可用节点集合通过 onUsableChecked 回传（异步方法不能声明 out 参数；回调等价于 out）。
    ///   onUsableChecked 在复核执行结束时调用一次（无论通过几个）；未开启复核或批量失败时不调用。
    ///
    /// ★ 2026-10-23 总预算（budgetSeconds，默认 45s）：覆盖节点探测阶段（xray 批量 + sb 单测）。
    ///   超预算立即停止剩余探测，并对还没出结果的节点补一次 onResult(node, -1)，
    ///   避免弱网/大量死节点时拖到 1 分半、UI 停在半路。可用性复核有自己独立的 6s 预算，不共用。
    public static async Task<IReadOnlyDictionary<NodeItem, int>> RealPingBatchAsync(
        IReadOnlyList<NodeItem> nodes, string runtimeDir,
        int timeoutMs = DefaultTimeoutMs, Action<NodeItem, int>? onResult = null, CancellationToken ct = default,
        bool availabilityRecheck = false,
        Action<IReadOnlyCollection<NodeItem>>? onUsableChecked = null,
        int budgetSeconds = 45)
    {
        var result = new ConcurrentDictionary<NodeItem, int>();
        var valid = nodes.Where(n => n.IsValid).ToList();
        //   Clash 直通节点是「整份 YAML 交给 mihomo」，没有独立可测的出站 → 跳过（保持未测）。
        valid.RemoveAll(n => n.IsClashPassthrough);
        var sbNodes = valid.Where(n =>
            n.Protocol is NodeProtocol.Hysteria2 or NodeProtocol.TUIC
            || (n.RequiresSingBox && !n.RequiresXray)).ToList();
        var xrayNodes = valid.Where(n => !sbNodes.Contains(n)).ToList();
        var done = 0;

        // ★ 2026-10-23 真连测速总预算：弱网/大量死节点时避免拖到 1 分半。
        //   本 token 只覆盖「节点探测阶段」（xray 批量 + sb 单测）；可用性复核另有自己的 6s 预算，
        //   单独用 ct 派生，不混用本 token（避免复核被总预算提前掐掉）。budgetSeconds <= 0 视为不设预算。
        using var batchBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (budgetSeconds > 0) batchBudget.CancelAfter(TimeSpan.FromSeconds(budgetSeconds));
        var batchToken = batchBudget.Token;
        var budgetExpired = false;
        // 已回调过的节点（xray + sb）：预算耗尽补 -1 时用于去重，保证每个节点只回调一次。
        var reported = new ConcurrentDictionary<NodeItem, byte>();

        if (xrayNodes.Count > 0)
        {
            var batchFailed = false;
            var ports = AllocPorts(xrayNodes.Count);
            var cfgPath = Path.Combine(Path.GetTempPath(), $"neo-realtest-batch-{ports[0]}-{Guid.NewGuid():N}.json");
            CoreProcess? core = null;
            var batchResult = new int[xrayNodes.Count];
            Array.Fill(batchResult, -1);
            var tail = new ConcurrentQueue<string>();
            try
            {
                await File.WriteAllTextAsync(cfgPath, XrayConfigBuilder.BuildTestBatch(xrayNodes, ports), ct);
                core = new CoreProcess(Path.Combine(runtimeDir, "xray.exe"), runtimeDir);
                core.LogLine += line => AppendTail(tail, line);
                core.Start(cfgPath);
                await Task.Delay(1000, batchToken); // 给内核 1 秒初始化（计入总预算）

                //   CoreProcess.Start() 不会抛异常；原实现继续对已关闭的 socks 端口探测，
                //   于是整批都得到 -1「超时」而不是 -2「本机失败」。这里主动检测秒退。
                if (!core.IsRunning)
                {
                    batchFailed = true;
                    Log($"批量测速失败（本机原因，非节点问题）：内核启动后立即退出。内核日志尾部：{TailText(tail)}", isError: true);
                }

                if (!batchFailed)
                {
                    // 整批开始的 INFO 日志：让人一眼看出这次是「保守但更快」的模式。
                    // 批量路径已去掉预热请求：每节点只发一次计时请求（延迟口径含建连+TLS，见 PingViaSocksAsync 注释）。
                    Log($"真连测速：并发 {BatchMaxConcurrency}、错峰 {BatchStaggerBaseMs}ms、单请求计时（保守但更快，避免触发商家限流）");

                    // 节拍器负责「启动有间隔」，MaxDegreeOfParallelism 负责「同时最多 BatchMaxConcurrency（10）条在跑」。
                    // 两者都接收 ict：取消时 Task.Delay/WaitAsync 立即抛出，不会卡住整批。
                    var startGate = new BatchStartGate();
                    await Parallel.ForEachAsync(Enumerable.Range(0, xrayNodes.Count),
                        new ParallelOptions
                        {
                            CancellationToken = batchToken,
                            MaxDegreeOfParallelism = BatchMaxConcurrency,
                        },
                        async (i, ict) =>
                        {
                            await startGate.WaitTurnAsync(ict);
                            batchResult[i] = await PingViaSocksAsync(ports[i], timeoutMs, ict);
                            result[xrayNodes[i]] = batchResult[i];
                            reported[xrayNodes[i]] = 0;
                            // 每个节点测完立即回调（上层按节点更新进度；「每 10 个」只是上层刷页脚文本的节流）。
                            onResult?.Invoke(xrayNodes[i], batchResult[i]);
                            Interlocked.Increment(ref done);
                        });
                }

                // ★ 2026-10-22 可用性复核：延迟最低 ≠ 能用（实测 AWS日本02 延迟最低但 x.com 超时）。
                //   必须在 core.Stop() 之前做 —— 用的就是同一个临时内核实例的 socks 端口。
                //   只复核 xray 节点（sing-box 节点在各自临时内核里测，且内核已停，无端口可复用）。
                if (!batchFailed && availabilityRecheck)
                {
                    var usable = new List<NodeItem>();
                    var checkedCount = 0;
                    // 提到 try 外：预算耗尽分支要打「已复核 N/候选数」的日志。
                    var candidates = new List<int>();
                    // ★ 2026-10-23 复核改为**并发**：前 3 名同时各打一次 x.com（各自独立 socks 端口），
                    //   任一成功立即返回、不再等其余；全部失败则等全部结束。
                    //   总预算 6s 只作兜底（单次 4s，正常最多等约 4s）。
                    //   用 linked CTS：外部取消（用户点停止）仍原样抛出；预算耗尽只停掉剩余候选，
                    //   已完成的「可用/不可用」结论照用（不把未复核节点误判成不可用）。
                    using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    budget.CancelAfter(TimeSpan.FromSeconds(AvailabilityRecheckBudgetSeconds));
                    var budgetToken = budget.Token;
                    try
                    {
                        // 按延迟从低到高取前 3 名（并列按端口索引稳定排序）。
                        candidates = Enumerable.Range(0, xrayNodes.Count)
                            .Where(i => batchResult[i] > 0)
                            .OrderBy(i => batchResult[i])
                            .Take(AvailabilityRecheckTop)
                            .ToList();
                        Log($"可用性复核 {candidates.Count} 个（并发），避免选到延迟低但用不了的节点" +
                            $"（单次超时 {AvailabilityRecheckTimeoutMs}ms、总预算 {AvailabilityRecheckBudgetSeconds}s）");
                        if (candidates.Count > 0)
                        {
                            // 每个候选用自己的 socks 端口并发发请求（本来就是独立连接）；
                            // recheckCts 用于「任一成功即取消其余」。
                            using var recheckCts = CancellationTokenSource.CreateLinkedTokenSource(budgetToken);
                            var pending = new List<Task<(int Index, bool Ok)>>();
                            async Task<(int Index, bool Ok)> ProbeOneAsync(int index, CancellationToken token)
                            {
                                var ok = await IsUsableViaSocksAsync(ports[index], token).ConfigureAwait(false);
                                return (index, ok);
                            }
                            try
                            {
                                foreach (var i in candidates) pending.Add(ProbeOneAsync(i, recheckCts.Token));
                                while (pending.Count > 0)
                                {
                                    await Task.WhenAny(pending);
                                    var finished = pending.First(t => t.IsCompleted);
                                    pending.Remove(finished);
                                    var (i, ok) = await finished;
                                    checkedCount++;
                                    if (ok) usable.Add(xrayNodes[i]);
                                    Log($"可用性复核：「{xrayNodes[i].Name}」（{batchResult[i]} ms）→ {(ok ? "可用" : "不可用")}");
                                    if (ok)
                                    {
                                        // 任一成功就够判定「有可用节点」：不等其余候选，取消在飞请求后返回。
                                        recheckCts.Cancel();
                                        break;
                                    }
                                }
                            }
                            finally
                            {
                                // 成功提前退出/预算耗尽/用户取消时，把仍在飞的请求取消并收尾，避免未观察异常。
                                if (pending.Count > 0)
                                {
                                    recheckCts.Cancel();
                                    try { await Task.WhenAll(pending); } catch { }
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        // 用户取消整批测速：维持原有语义（向上抛，由调用方按「已取消」处理）。
                        throw;
                    }
                    catch (OperationCanceledException)
                    {
                        // 预算耗尽：停止剩余候选，已复核完的结论照用。
                        Log($"可用性复核超出 {AvailabilityRecheckBudgetSeconds}s 预算，已复核 {checkedCount}/{candidates.Count}");
                    }
                    catch (Exception ex)
                    {
                        // 复核本身失败不能影响已经测到的延迟结果：按「无结果」上交，由上层退回按延迟选择。
                        Log($"可用性复核整体失败（不影响延迟结果）：[{ex.GetType().Name}] {ex.Message}", isError: true);
                    }
                    onUsableChecked?.Invoke(usable);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                // ★ 2026-10-23 总预算耗尽：不向上抛，剩余节点按「超时(-1)」处理。
                //   日志与 onResult(-1) 在方法末尾统一补（那里能覆盖 sb 节点，也能准确统计剩余数）。
                budgetExpired = true;
            }
            //   被静默吞掉，随后 batchResult 的 -1 初值被当成「确定超时」写进结果，
            //   界面于是把「本机故障」显示成满屏节点「超时」，且日志里零痕迹。
            //   现在：留 Error 痕 + 标记本批为「本机失败」，不再冒充测速结果。
            catch (Exception ex)
            {
                batchFailed = true;
                Log($"批量测速失败（本机原因，非节点问题）：[{ex.GetType().Name}] {ex.Message}", isError: true);
            }
            finally
            {
                core?.Stop();
                try { File.Delete(cfgPath); } catch { }
            }

            // 只有正常跑完才回填结果；本机失败时**不写** -1 冒充超时，
            // 改为写入明确的失败标记值（见下方约定），并把该批的 -2 也通知给上层，
            // 避免界面停在旧状态。
            for (var i = 0; i < xrayNodes.Count; i++)
            {
                var value = batchFailed ? BatchFailed : batchResult[i];
                result[xrayNodes[i]] = value;
                if (batchFailed && reported.TryAdd(xrayNodes[i], 0))
                    onResult?.Invoke(xrayNodes[i], value);
            }
        }

        if (sbNodes.Count > 0 && !budgetExpired)
        {
            try
            {
                await Parallel.ForEachAsync(sbNodes,
                    new ParallelOptions { CancellationToken = batchToken, MaxDegreeOfParallelism = 4 },
                    async (n, ict) =>
                    {
                        var ms = await RealPingAsync(n, runtimeDir, timeoutMs, ict);
                        result[n] = ms;
                        reported[n] = 0;
                        onResult?.Invoke(n, ms);
                        Interlocked.Increment(ref done);
                    });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                // 总预算在 sb 阶段耗尽：同样停止剩余节点，交给末尾统一补 -1 + 日志。
                budgetExpired = true;
            }
        }

        if (budgetExpired)
        {
            // ★ 2026-10-23 总预算耗尽：停止剩余探测后，对所有还没出结果的节点补一次 onResult(-1)，
            //   保证 UI 不会停在半路（-1 = 超时，上层会据此显示超时并允许下次重测）。
            var remaining = valid.Where(n => !reported.ContainsKey(n)).ToList();
            Log($"真连测速超出 {budgetSeconds}s 预算，剩余 {remaining.Count} 个未测（按超时处理）");
            foreach (var n in remaining)
            {
                result[n] = -1;
                reported[n] = 0;
                onResult?.Invoke(n, -1);
            }
        }

        return result;
    }

    private static async Task<int> PingViaSocksAsync(int port, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy(new Uri($"socks5://127.0.0.1:{port}")),
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromMilliseconds(timeoutMs),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };

            // ★ 2026-10-23 修正：恢复「预热 + 计时」两步，但**复用同一条 HTTP 连接**。
            //   为什么必须热：去掉预热后计时把 TCP + TLS 握手（校园无线/手机热点的建连经常 1–3 秒）
            //   也算进了「延迟」，节点页一片 1000ms+、p50 1718ms，用户直接看不出哪个能用
            //   （实测 00:13 那轮：<200ms 0 个、≥1000ms 147 个）。
            //   现在：先发一次不计时的预热请求（SocketsHttpHandler 会复用到同一节点的连接），
            //   再计时第二次请求 —— 口径回到「已建连后的 HTTP 往返」，与旧版可比。
            //   代价：每节点多 1 次请求（原来就是这样），总耗时只回升几秒。
            foreach (var url in ProbeUrls)
            {
                try { using var warm = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { /* 预热失败不影响下面正式计时（可能是目标抖动，换下一个目标） */ }

                try
                {
                    var sw = Stopwatch.StartNew();
                    using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                    sw.Stop();
                    var code = (int)resp.StatusCode;
                    if (code is 200 or 204) return (int)sw.ElapsedMilliseconds;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { /* 换下一个目标 */ }
            }
            return -1;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return -1;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// 可用性复核：经指定 socks 端口请求一次 x.com，拿到 2xx/3xx 即算「能用」。
    /// 只判通不通，不计延迟、不做预热；超时/连接失败/其它状态码都返回 false。
    /// 取消（外部 ct）会原样抛出，不吞掉。
    /// </summary>
    private static async Task<bool> IsUsableViaSocksAsync(int socksPort, CancellationToken ct)
    {
        try
        {
            using var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy(new Uri($"socks5://127.0.0.1:{socksPort}")),
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromMilliseconds(AvailabilityRecheckTimeoutMs),
                AllowAutoRedirect = false, // 3xx 本身就算可用，不必跟到最终页
                PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(AvailabilityRecheckTimeoutMs) };
            using var req = new HttpRequestMessage(HttpMethod.Get, AvailabilityProbeUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", AvailabilityProbeUserAgent);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var code = (int)resp.StatusCode;
            return code is >= 200 and < 400;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    private static int[] AllocPorts(int count)
    {
        var set = new HashSet<int>();
        while (set.Count < count) set.Add(FreePort());
        return set.OrderBy(p => p).ToArray();
    }

    /// <summary>
    /// 「真连测速」保守模式的启动节拍器（2026-10-22）。
    ///
    /// 与 Parallel.ForEachAsync 的 MaxDegreeOfParallelism = BatchMaxConcurrency 配合，
    /// 保证「同时最多 BatchMaxConcurrency（10）条在跑」；本类只负责「启动有间隔」：用信号量
    /// 串行分配下一次允许启动的时刻，相邻节点启动间隔 = BatchStaggerBaseMs ± BatchStaggerJitterMs
    /// （70–130ms），避免整齐的机器节奏被识别成扫描。
    /// 所有等待都走 CancellationToken：取消时立即抛出，不会卡住整批。
    /// </summary>
    private sealed class BatchStartGate
    {
        private readonly SemaphoreSlim _turn = new(1, 1);
        private long _nextStartTick;

        public async Task WaitTurnAsync(CancellationToken ct)
        {
            long target;
            await _turn.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var now = Environment.TickCount64;
                var jitter = Random.Shared.Next(-BatchStaggerJitterMs, BatchStaggerJitterMs + 1);
                var interval = Math.Max(0, BatchStaggerBaseMs + jitter);
                target = Math.Max(now, _nextStartTick + interval);
                _nextStartTick = target;
            }
            finally
            {
                _turn.Release();
            }

            var delayMs = target - Environment.TickCount64;
            if (delayMs > 0)
                await Task.Delay((int)delayMs, ct).ConfigureAwait(false);
            else
                ct.ThrowIfCancellationRequested();
        }
    }

    /// 出口位置检测：每个节点走自己的 socks 入口请求 Cloudflare trace，读出**真实出口国家**（ISO 小写）。
    /// 与批量测速同一套做法（一个临时内核 + 每节点独立 socks 端口），避免逐个起进程。
    /// 只覆盖 xray 承载的协议；hy2/tuic（sing-box）暂不参与（返回空）。
    public static async Task<IReadOnlyDictionary<NodeItem, (string Country, string Ip)>> DetectExitLocationsAsync(
        IReadOnlyList<NodeItem> nodes, string runtimeDir, int timeoutMs = 10000,
        Action<NodeItem, string, string>? onResult = null, CancellationToken ct = default)
    {
        var result = new ConcurrentDictionary<NodeItem, (string Country, string Ip)>();
        var xrayNodes = nodes.Where(n => n.IsValid
            && n.Protocol is not (NodeProtocol.Hysteria2 or NodeProtocol.TUIC)
            && !(n.RequiresSingBox && !n.RequiresXray)).ToList();
        if (xrayNodes.Count == 0) return result;

        var ports = AllocPorts(xrayNodes.Count);
        var cfgPath = Path.Combine(Path.GetTempPath(), $"neo-geotest-{ports[0]}-{Guid.NewGuid():N}.json");
        CoreProcess? core = null;
        var tail = new ConcurrentQueue<string>();
        try
        {
            await File.WriteAllTextAsync(cfgPath, XrayConfigBuilder.BuildTestBatch(xrayNodes, ports), ct);
            core = new CoreProcess(Path.Combine(runtimeDir, "xray.exe"), runtimeDir);
            core.LogLine += line => AppendTail(tail, line);
            core.Start(cfgPath);
            await Task.Delay(1200, ct);
            if (!core.IsRunning)
            {
                Log($"出口位置检测失败（本机原因，非节点问题）：内核启动后立即退出。内核日志尾部：{TailText(tail)}", isError: true);
                return result;
            }

            // 出口位置检测同样是「一个临时内核 + 每节点独立 socks 端口」的批量路径，
            // 一并限制并发上限（不做 200ms 错峰：它是静默后台任务、且有 90s 总超时预算）。
            await Parallel.ForEachAsync(Enumerable.Range(0, xrayNodes.Count),
                new ParallelOptions
                {
                    CancellationToken = ct,
                    MaxDegreeOfParallelism = BatchMaxConcurrency,
                },
                async (i, ict) =>
                {
                    var (country, ip) = await QueryExitCountryViaSocksAsync(ports[i], timeoutMs, ict);
                    result[xrayNodes[i]] = (country, ip);
                    onResult?.Invoke(xrayNodes[i], country, ip);
                });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log($"出口位置检测失败（本机原因）：[{ex.GetType().Name}] {ex.Message}", isError: true);
        }
        finally
        {
            core?.Stop();
            try { File.Delete(cfgPath); } catch { }
        }
        return result;
    }

    /// Cloudflare trace 的 loc= 行就是 Cloudflare 判定的出口国家代码。
    private static async Task<(string Country, string Ip)> QueryExitCountryViaSocksAsync(int socksPort, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy(new Uri($"socks5://127.0.0.1:{socksPort}")),
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromMilliseconds(timeoutMs),
                PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            var text = await http.GetStringAsync("http://cp.cloudflare.com/cdn-cgi/trace", ct);
            var country = "";
            var ip = "";
            foreach (var line in text.Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith("loc=", StringComparison.Ordinal)) country = t[4..].Trim().ToLowerInvariant();
                else if (t.StartsWith("ip=", StringComparison.Ordinal)) ip = t[3..].Trim();
            }
            return (country, ip);
        }
        catch { }
        return ("", "");
    }
}
