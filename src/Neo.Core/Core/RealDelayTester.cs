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
    //   RealPingBatchAsync 原先把全部节点（最多 68 个）一次性打出去，且每节点还有
    //   「预热 + 计时」两次请求 → 短时间内同一出口发出大量 TLS 连接，容易被机场判定为
    //   异常流量。改为：同时最多 BatchMaxConcurrency 条 + 相邻节点启动错峰
    //   BatchStaggerBaseMs ± BatchStaggerJitterMs。三个参数集中在这里，以后单点调整即可。
    /// <summary>批量真连测速时，同时在跑的节点探测数上限（保守值，避免触发商家限流）。</summary>
    private const int BatchMaxConcurrency = 4;

    /// <summary>相邻两个节点开始探测的基准间隔（毫秒）；实际为 200±50ms，即 150–250ms。</summary>
    private const int BatchStaggerBaseMs = 200;

    /// <summary>错峰间隔的随机抖动（±毫秒）：避免整齐的机器节奏被识别成扫描。</summary>
    private const int BatchStaggerJitterMs = 50;

    private const string Target = "https://www.google.com/generate_204";

    /// 延迟/可用性探测的目标序列（按顺序退让，第一个成功即采用）。
    /// 为什么不止 google：2026-10-22 在本机实测同一节点 108 秒 36 次采样里
    ///   x.com 36/36 成功，而 google/generate_204 只有 24/36（其余 5s 超时）——
    ///   单目标会把「google 这条线被限速/抖动」误判成「节点死了」，
    ///   而故障转移挑备选时正是用这个结果判定存活，会把整组备选判死。
    /// cloudflare 204 是明文 HTTP、开销比 TLS 小，且 xray observatory 已在用（同口径）。
    private static readonly string[] ProbeUrls =
    {
        Target,
        "http://cp.cloudflare.com/generate_204",
    };

    public static async Task<int> RealPingAsync(NodeItem node, string runtimeDir,
        int timeoutMs = DefaultTimeoutMs, CancellationToken ct = default)
    {
        if (!node.IsValid) return -1;

        bool useSingBox = node.Protocol is NodeProtocol.Hysteria2 or NodeProtocol.TUIC;
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

    /// v2rayN 式批量测速：xray 系节点编入单实例（每节点独立 socks 端口），一次进程启动全并行测完；
    /// hy2/tuic（sing-box）节点数量少，沿用逐节点单测本地并行。返回各节点延迟（-1=不通）。
    public static async Task<IReadOnlyDictionary<NodeItem, int>> RealPingBatchAsync(
        IReadOnlyList<NodeItem> nodes, string runtimeDir,
        int timeoutMs = DefaultTimeoutMs, Action<NodeItem, int>? onResult = null, CancellationToken ct = default)
    {
        var result = new ConcurrentDictionary<NodeItem, int>();
        var valid = nodes.Where(n => n.IsValid).ToList();
        var xrayNodes = valid.Where(n => n.Protocol is not NodeProtocol.Hysteria2 and not NodeProtocol.TUIC).ToList();
        var sbNodes = valid.Where(n => n.Protocol is NodeProtocol.Hysteria2 or NodeProtocol.TUIC).ToList();
        var done = 0;

        if (xrayNodes.Count > 0)
        {
            var batchFailed = false;
            var ports = AllocPorts(xrayNodes.Count);
            var cfgPath = Path.Combine(Path.GetTempPath(), $"neo-realtest-batch-{ports[0]}-{Guid.NewGuid():N}.json");
            CoreProcess? core = null;
            var batchResult = new int[xrayNodes.Count];
            Array.Fill(batchResult, -1);
            var tail = new ConcurrentQueue<string>();
            // 已通过 onResult 上报过的节点（异常分支下避免对同一节点重复回调）。
            var reported = new ConcurrentDictionary<NodeItem, byte>();
            try
            {
                await File.WriteAllTextAsync(cfgPath, XrayConfigBuilder.BuildTestBatch(xrayNodes, ports), ct);
                core = new CoreProcess(Path.Combine(runtimeDir, "xray.exe"), runtimeDir);
                core.LogLine += line => AppendTail(tail, line);
                core.Start(cfgPath);
                await Task.Delay(1000, ct); // 与 v2rayN 相同：给内核 1 秒初始化

                //   CoreProcess.Start() 不会抛异常；原实现继续对已关闭的 socks 端口探测，
                //   于是整批都得到 -1「超时」而不是 -2「本机失败」。这里主动检测秒退。
                if (!core.IsRunning)
                {
                    batchFailed = true;
                    Log($"批量测速失败（本机原因，非节点问题）：内核启动后立即退出。内核日志尾部：{TailText(tail)}", isError: true);
                }

                if (!batchFailed)
                {
                    // 整批开始的 INFO 日志：让人一眼看出这次是保守模式；
                    // 每节点「预热 + 计时」两次请求是延迟口径的一部分（预热不计时），保留。
                    Log($"真连测速：并发 {BatchMaxConcurrency}、错峰 {BatchStaggerBaseMs}ms（保守模式，避免触发商家限流）；" +
                        "每个节点先预热再计时（成功通常共 2 次请求，延迟口径需要；目标抖动时会换目标重试）");

                    // 节拍器负责「启动有间隔」，MaxDegreeOfParallelism 负责「同时最多 4 条在跑」。
                    // 两者都接收 ict：取消时 Task.Delay/WaitAsync 立即抛出，不会卡住整批。
                    var startGate = new BatchStartGate();
                    await Parallel.ForEachAsync(Enumerable.Range(0, xrayNodes.Count),
                        new ParallelOptions
                        {
                            CancellationToken = ct,
                            MaxDegreeOfParallelism = BatchMaxConcurrency,
                        },
                        async (i, ict) =>
                        {
                            await startGate.WaitTurnAsync(ict);
                            batchResult[i] = await PingViaSocksAsync(ports[i], timeoutMs, ict);
                            result[xrayNodes[i]] = batchResult[i];
                            reported[xrayNodes[i]] = 0;
                            onResult?.Invoke(xrayNodes[i], batchResult[i]);
                            Interlocked.Increment(ref done);
                        });
                }
            }
            catch (OperationCanceledException) { throw; }
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

        if (sbNodes.Count > 0)
        {
            await Parallel.ForEachAsync(sbNodes,
                new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 4 },
                async (n, ict) =>
                {
                    var ms = await RealPingAsync(n, runtimeDir, timeoutMs, ict);
                    result[n] = ms;
                    onResult?.Invoke(n, ms);
                    Interlocked.Increment(ref done);
                });
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

            foreach (var url in ProbeUrls)
            {
                // 预热：节点 TCP+TLS 握手发生在这条请求（不计入延迟口径，对齐 v2rayN——
                // 它的计时只覆盖已建连后的 HTTP 往返，节点握手在内核后台完成）
                try
                {
                    using var warm = new HttpRequestMessage(HttpMethod.Get, url);
                    using var wr = await http.SendAsync(warm, HttpCompletionOption.ResponseHeadersRead, ct);
                }
                catch { /* 预热失败不影响正式计时，也不换目标：正式请求会再试 */ }

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
    /// 保证「同时最多 4 条在跑」；本类只负责「启动有间隔」：用信号量串行分配下一次
    /// 允许启动的时刻，相邻节点启动间隔 = BatchStaggerBaseMs ± BatchStaggerJitterMs
    /// （150–250ms），避免整齐的机器节奏被识别成扫描。
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
            && n.Protocol is not NodeProtocol.Hysteria2 and not NodeProtocol.TUIC).ToList();
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
