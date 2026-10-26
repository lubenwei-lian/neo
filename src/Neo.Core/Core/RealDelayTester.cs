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

    /// <summary>测到不超过这个毫秒数就认为「已经足够快」，不再做第 2/3 次计时（每节点最多 3 次）。</summary>
    private const int SlowEnoughMs = 1000;

    //   RealPingBatchAsync 原先把全部节点（最多 68 个）一次性打出去 → 容易被判异常流量。
    //   改为：同时最多 BatchMaxConcurrency 条 + 相邻节点启动错峰 BatchStaggerBaseMs ± 抖动。
    //
    //   批量路径再去掉「预热请求」（只发一次计时请求，延迟口径按首字节时间）。
    //   连接数仍受控，预期总耗时降到原来的 1/3–1/4。
    /// <summary>批量真连测速时，同时在跑的节点探测数上限（10，保守但比 4 快）。</summary>
    //   数字放大（实测同一节点 110ms 与 1385ms），排序因此不可信。降到 6 后总耗时可接受
    //   （68 节点约 30～40s），换来与常见客户端（v2rayN）可比的稳定读数。
    private const int BatchMaxConcurrency = 6;

    /// <summary>相邻两个节点开始探测的基准间隔（毫秒）；实际为 100±30ms，即 70–130ms。</summary>
    private const int BatchStaggerBaseMs = 100;

    /// <summary>错峰间隔的随机抖动（±毫秒）：避免整齐的机器节奏被识别成扫描。</summary>
    private const int BatchStaggerJitterMs = 30;

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
    ///   x.com 36/36 成功，而 google/generate_204 只有 24/36（其余 5s 超时）——
    ///   单目标会把「google 这条线被限速/抖动」误判成「节点死了」，
    ///   而故障转移挑备选时正是用这个结果判定存活，会把整组备选判死。
    /// cloudflare 204 是明文 HTTP、开销比 TLS 小，且 xray observatory 已在用（同口径）。
    private static readonly string[] ProbeUrls =
    {
        //   单次开销比 google 的 https/generate_204 小一大截（实测节点上 0.2–0.5s vs 0.6–2s），
        //   68 节点批量测速时这一段直接决定总耗时。google 退到第二位做交叉印证。
        "http://cp.cloudflare.com/generate_204",
        Target,
    };

    /// <summary>真连测速结果：毫秒值 + 补充说明（证书放宽 / UDP 限制等）。</summary>
    public readonly record struct RealPingResult(int Ms, string? Note);

    public static async Task<int> RealPingAsync(NodeItem node, string runtimeDir,
        int timeoutMs = DefaultTimeoutMs, CancellationToken ct = default)
        => (await RealPingDetailedAsync(node, runtimeDir, timeoutMs, ct).ConfigureAwait(false)).Ms;

    /// <summary>
    /// 两阶段真连测速：
    ///   1) 按节点声明的证书策略严格测；
    ///   2) 只有内核明确报证书不受信、且该方案允许放宽（UDP 类）时，才放宽一次重测，并写明原因。
    /// 超时/失败时根据传输层给出可解释的说明（UDP 被限、证书不受信等）。
    /// </summary>
    public static async Task<RealPingResult> RealPingDetailedAsync(NodeItem node, string runtimeDir,
        int timeoutMs = DefaultTimeoutMs, CancellationToken ct = default)
    {
        var plan = DelayTestPlanner.PlanFor(node);
        var effectiveTimeout = timeoutMs > 0 ? timeoutMs : plan.TimeoutMs;
        var first = await RealPingOnceAsync(node, runtimeDir, effectiveTimeout, plan.Attempts, relaxTls: false, ct).ConfigureAwait(false);
        var ms = first.Ms;
        string? note = null;

        if (ms > 0)
        {
            note = null;
        }
        else if (first.CertFailed && plan.RelaxTlsOnCertFailure && !node.AllowInsecure)
        {
            Log($"测速：节点「{node.Name}」证书不受信（{plan.Reason}），放宽校验后重测");
            var second = await RealPingOnceAsync(node, runtimeDir, effectiveTimeout, plan.Attempts, relaxTls: true, ct).ConfigureAwait(false);
            ms = second.Ms;
            note = ms > 0
                ? "证书放宽"
                : plan.Transport == DelayTransport.Udp ? "证书放宽后仍超时（UDP 可能被限制）" : "证书放宽后仍失败";
        }
        else if (first.CertFailed)
        {
            note = "证书不受信";
        }
        else if (plan.Transport == DelayTransport.Udp && ms <= 0)
        {
            note = "UDP 可能被网络限制";
        }

        node.RealDelayNote = note;
        return new RealPingResult(ms, note);
    }

    /// <summary>严格校验证书做一次握手，只回答「证书是否不受信」。</summary>
    public static async Task<bool> CertUntrustedAsync(NodeItem node, string runtimeDir,
        int timeoutMs = 6000, CancellationToken ct = default)
    {
        var r = await RealPingOnceAsync(node, runtimeDir, timeoutMs, 1, relaxTls: false, ct)
            .ConfigureAwait(false);
        return r.CertFailed;
    }

    /// <summary>用指定 URL 探测「这个节点实际能不能用」（可用性复核）。返回 ms，<=0 表示不可用。</summary>
    public static async Task<int> ProbeUrlAsync(NodeItem node, string runtimeDir, string url,
        int timeoutMs = 4000, CancellationToken ct = default)
    {
        var plan = DelayTestPlanner.PlanFor(node);
        var r = await RealPingOnceAsync(node, runtimeDir, timeoutMs, 1, plan.RelaxTlsOnCertFailure, ct, url)
            .ConfigureAwait(false);
        return r.Ms;
    }

    private static async Task<(int Ms, bool CertFailed)> RealPingOnceAsync(NodeItem node, string runtimeDir,
        int timeoutMs, int attempts, bool relaxTls, CancellationToken ct, string? urlOverride = null)
    {
        if (!node.IsValid) return (-1, false);
        if (node.IsClashPassthrough || node.IsCustomConfig || node.IsNaive) return (0, false);

        bool useSingBox = KernelRouter.Apply(node, node.Protocol is NodeProtocol.Hysteria2 or NodeProtocol.TUIC
            || (node.RequiresSingBox && !node.RequiresXray));
        var exe = Path.Combine(runtimeDir, useSingBox ? "sing-box.exe" : "xray.exe");
        if (!File.Exists(exe))
        {
            Log($"单节点测速失败（本机原因，非节点问题）：内核不存在 {exe}", isError: true);
            return (BatchFailed, false);
        }

        var port = FreePort();
        var cfgPath = Path.Combine(Path.GetTempPath(), $"neo-realtest-{port}-{Guid.NewGuid():N}.json");
        CoreProcess? core = null;
        var tail = new ConcurrentQueue<string>();
        var started = false;
        try
        {
            var cfg = useSingBox
                ? SingBoxConfigBuilder.BuildTest(node, port, relaxTls)
                : XrayConfigBuilder.BuildTest(node, port);
            await File.WriteAllTextAsync(cfgPath, cfg, ct);
            core = new CoreProcess(exe, runtimeDir);
            core.LogLine += line => AppendTail(tail, line);
            core.Start(cfgPath);

            var ready = false;
            for (var i = 0; i < 40 && !ready; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (!core.IsRunning)
                {
                    Log($"单节点测速失败（本机原因，非节点问题）：内核启动后立即退出。内核日志尾部：{TailText(tail)}", isError: true);
                    return (BatchFailed, false);
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
                return (BatchFailed, false);
            }
            if (!ready)
            {
                Log($"单节点测速失败（本机原因，非节点问题）：内核在等待窗口内未监听测速端口。内核日志尾部：{TailText(tail)}", isError: true);
                return (BatchFailed, false);
            }
            started = true;

            using var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy(new Uri($"socks5://127.0.0.1:{port}")),
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromMilliseconds(timeoutMs),
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };

            if (urlOverride != null)
            {
                try
                {
                    var swUrl = Stopwatch.StartNew();
                    using var respUrl = await http.GetAsync(urlOverride, HttpCompletionOption.ResponseHeadersRead, ct);
                    swUrl.Stop();
                    var codeUrl = (int)respUrl.StatusCode;
                    return (codeUrl is >= 200 and < 400 ? (int)swUrl.ElapsedMilliseconds : -1, TailHasCertError(tail));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { return (-1, TailHasCertError(tail)); }
            }

            var best = int.MaxValue;
            foreach (var url in ProbeUrls)
            {
                try { using var warm = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { }

                for (var attempt = 0; attempt < Math.Max(1, attempts); attempt++)
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
                            if (ms < best) best = ms;
                            if (best <= SlowEnoughMs) return (best, false);
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch { }
                }
                if (best < int.MaxValue) return (best, false);
            }
            return (-1, TailHasCertError(tail));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (-1, TailHasCertError(tail));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (!started)
        {
            Log($"单节点测速失败（本机原因，非节点问题）：[{ex.GetType().Name}] {ex.Message}", isError: true);
            return (BatchFailed, false);
        }
        catch
        {
            return (-1, TailHasCertError(tail));
        }
        finally
        {
            core?.Stop();
            try { File.Delete(cfgPath); } catch { }
        }
    }

    /// <summary>内核日志里是否出现「证书不受信」类错误（x509 / unknown authority）。</summary>
    private static bool TailHasCertError(ConcurrentQueue<string> tail)
    {
        foreach (var line in tail)
        {
            if (line.Contains("certificate signed by unknown authority", StringComparison.OrdinalIgnoreCase)
                || line.Contains("x509:", StringComparison.OrdinalIgnoreCase)
                || line.Contains("failed to verify certificate", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
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

    /// <summary>
    /// 清理上次进程被强杀后残留的测速临时配置（%TEMP%\neo-realtest-*.json）。
    /// 判据：只删「默认 5 分钟前」的文件，避免误删正在进行的测速配置。
    /// 返回删除个数。
    /// </summary>
    public static int CleanupStaleTempConfigs(TimeSpan? olderThan = null)
    {
        var removed = 0;
        try
        {
            var cutoff = DateTime.Now - (olderThan ?? TimeSpan.FromMinutes(5));
            foreach (var file in Directory.EnumerateFiles(Path.GetTempPath(), "neo-realtest-*.json"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) >= cutoff) continue;
                    File.Delete(file);
                    removed++;
                }
                catch { }
            }
        }
        catch { }
        return removed;
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
    ///   批量延迟测完后，按延迟从低到高取前 3 名，用**同一个临时内核实例**的 socks 端口
    ///   **并发**请求一次 https://x.com/（每个候选独立 socks 端口/连接，单次超时 4s；
    ///   任一成功立即返回，全部失败则等全部结束），2xx/3xx 才算「可用」。
    ///   整个复核阶段有 6s 总预算兜底：超预算就停止剩余候选，已完成的结论照用。
    ///   可用节点集合通过 onUsableChecked 回传（异步方法不能声明 out 参数；回调等价于 out）。
    ///   onUsableChecked 在复核执行结束时调用一次（无论通过几个）；未开启复核或批量失败时不调用。
    ///
    ///   超预算立即停止剩余探测，并对还没出结果的节点补一次 onResult(node, -1)，
    ///   避免弱网/大量死节点时拖到 1 分半、UI 停在半路。可用性复核有自己独立的 6s 预算，不共用。
    /// <summary>
    /// 真连测速入口（兼容旧签名）。实际调度在 DelayTestRunner：
    /// 进度驱动、按 TCP/UDP 数量决定先后、两组测完再统一重试失败节点（首测 + 4 次重试）。
    /// timeoutMs 参数已废弃（单节点超时由 DelayTestPlanner 决定：TCP 9s / UDP 15s）。
    /// </summary>
    public static Task<IReadOnlyDictionary<NodeItem, int>> RealPingBatchAsync(
        IReadOnlyList<NodeItem> nodes, string runtimeDir,
        int timeoutMs = 0, Action<NodeItem, int>? onResult = null, CancellationToken ct = default,
        bool availabilityRecheck = false,
        Action<IReadOnlyCollection<NodeItem>>? onUsableChecked = null,
        Action<DelayTestProgress>? onProgress = null)
        => DelayTestRunner.RunAsync(nodes, runtimeDir, onResult, ct, availabilityRecheck, onUsableChecked, onProgress);

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

            //   为什么必须热：去掉预热后计时把 TCP + TLS 握手（校园无线/手机热点的建连经常 1–3 秒）
            //   也算进了「延迟」，节点页一片 1000ms+、p50 1718ms，用户直接看不出哪个能用
            //   （实测 00:13 那轮：<200ms 0 个、≥1000ms 147 个）。
            //   现在：先发一次不计时的预热请求（SocketsHttpHandler 会复用到同一节点的连接），
            //   再计时第二次请求 —— 口径回到「已建连后的 HTTP 往返」，与旧版可比。
            //   代价：每节点多 1 次请求（原来就是这样），总耗时只回升几秒。
            var best = int.MaxValue;
            foreach (var url in ProbeUrls)
            {
                try { using var warm = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { /* 预热失败不影响下面正式计时（可能是目标抖动，换下一个目标） */ }

                //   只测一次时，若预热恰好失败/连接被服务端关掉，计时会退化成冷连接
                //   （实测同一节点 130ms 与 3012ms 并存，排序不可信）。
                for (var attempt = 0; attempt < 3; attempt++)
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
                            if (ms < best) best = ms;
                            if (best <= SlowEnoughMs) return best;
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch { /* 换下一次尝试 */ }
                }
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
