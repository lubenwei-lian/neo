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

    private const string Target = "https://www.google.com/generate_204";

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

            var sw = Stopwatch.StartNew();
            using var resp = await http.GetAsync(Target, HttpCompletionOption.ResponseHeadersRead, ct);
            sw.Stop();

            var code = (int)resp.StatusCode;
            return code is 200 or 204 ? (int)sw.ElapsedMilliseconds : -1;
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

                var sw = Stopwatch.StartNew();
                using var resp = await http.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, ct);
                sw.Stop();

                var code = (int)resp.StatusCode;
                if (code is 200 or 204)
                {
                    var ms = (int)sw.ElapsedMilliseconds;
                    if (best < 0 || ms < best) best = ms;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* 单次失败不影响其他轮次 */ }

            if (i < rounds - 1) await Task.Delay(300, ct);
        }
        return best;
    }

    /// 它照样拿到 4/24 份流量 → 浏览时每条连接抽签，抽到就卡。
    /// 做法：复用批量测速那套「一个临时 xray 承载全部节点、每节点独立 socks 端口」，
    /// 每节点经自己的 socks 端口下载 256KB 计时折算 Kbps；0 表示测不出速度。
    /// 只用于连接建立阶段（每个节点结果会缓存，见 ConnectionService）。
    public static async Task<IReadOnlyDictionary<NodeItem, int>> MeasureSpeedBatchAsync(
        IReadOnlyList<NodeItem> nodes, string runtimeDir, int bytesPerNode = 262144,
        int timeoutMs = 12000, CancellationToken ct = default)
    {
        var result = new ConcurrentDictionary<NodeItem, int>();
        var valid = nodes.Where(n => n.IsValid).ToList();
        var xrayNodes = valid.Where(n => n.Protocol is not NodeProtocol.Hysteria2 and not NodeProtocol.TUIC).ToList();
        var sbNodes = valid.Where(n => n.Protocol is NodeProtocol.Hysteria2 or NodeProtocol.TUIC).ToList();

        if (xrayNodes.Count > 0)
        {
            var ports = AllocPorts(xrayNodes.Count);
            var cfgPath = Path.Combine(Path.GetTempPath(), $"neo-speedtest-{ports[0]}-{Guid.NewGuid():N}.json");
            CoreProcess? core = null;
            var tail = new ConcurrentQueue<string>();
            try
            {
                await File.WriteAllTextAsync(cfgPath, XrayConfigBuilder.BuildTestBatch(xrayNodes, ports), ct);
                core = new CoreProcess(Path.Combine(runtimeDir, "xray.exe"), runtimeDir);
                core.LogLine += line => AppendTail(tail, line);
                core.Start(cfgPath);
                await Task.Delay(1000, ct);

                if (!core.IsRunning)
                {
                    Log($"吞吐探测失败（本机原因，非节点问题）：内核启动后立即退出。内核日志尾部：{TailText(tail)}", isError: true);
                }
                else
                {
                    await Parallel.ForEachAsync(Enumerable.Range(0, xrayNodes.Count),
                        new ParallelOptions { CancellationToken = ct },
                        async (i, ict) =>
                        {
                            result[xrayNodes[i]] = await MeasureSpeedViaSocksAsync(ports[i], bytesPerNode, timeoutMs, ict);
                        });
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log($"吞吐探测失败（本机原因，非节点问题）：[{ex.GetType().Name}] {ex.Message}", isError: true);
            }
            finally
            {
                core?.Stop();
                try { File.Delete(cfgPath); } catch { }
            }
        }

        // hy2/tuic（sing-box）节点：逐个起临时内核（数量少）
        foreach (var node in sbNodes)
        {
            ct.ThrowIfCancellationRequested();
            var port = FreePort();
            var cfgPath = Path.Combine(Path.GetTempPath(), $"neo-speedtest-sb-{port}-{Guid.NewGuid():N}.json");
            CoreProcess? core = null;
            var tail = new ConcurrentQueue<string>();
            try
            {
                await File.WriteAllTextAsync(cfgPath, SingBoxConfigBuilder.BuildTest(node, port), ct);
                core = new CoreProcess(Path.Combine(runtimeDir, "sing-box.exe"), runtimeDir);
                core.LogLine += line => AppendTail(tail, line);
                core.Start(cfgPath);
                await Task.Delay(800, ct);
                result[node] = core.IsRunning
                    ? await MeasureSpeedViaSocksAsync(port, bytesPerNode, timeoutMs, ct)
                    : 0;
            }
            catch (OperationCanceledException) { throw; }
            catch { result[node] = 0; }
            finally
            {
                core?.Stop();
                try { File.Delete(cfgPath); } catch { }
            }
        }

        foreach (var n in valid) if (!result.ContainsKey(n)) result[n] = 0;
        return result;
    }

    // 吞吐探测目标：Cloudflare 的定长下载最省流；限流/失败时退到 Hetzner 的测速大文件（只读前 256KB 即断开）。
    private static readonly string[] SpeedTargets =
    {
        "https://speed.cloudflare.com/__down?bytes=262144",
        "https://speed.cloudflare.com/__down?bytes=131072",
        "https://speed.hetzner.de/100MB.bin",
    };

    /// 单个节点测速：同一目标连测两次，取**较小值**作为该节点吞吐。
    ///   只测一次时，抖动节点可能刚好抽到一次快样本，权重被抬高，实际却拖后腿。
    ///   取两次的较小值 = 保守估计，让「忽快忽慢」的节点拿不到高份额，但仍保留最低 1 份
    ///   （用户要求每个节点都参与并发，不能为 0）。
    private static async Task<int> MeasureSpeedViaSocksAsync(int socksPort, int bytes, int timeoutMs, CancellationToken ct)
    {
        var first = await MeasureSpeedOnceAsync(socksPort, bytes, timeoutMs, ct);
        if (first <= 0) return 0;
        var second = await MeasureSpeedOnceAsync(socksPort, bytes, timeoutMs, ct);
        return second > 0 ? Math.Min(first, second) : first;
    }

    private static async Task<int> MeasureSpeedOnceAsync(int socksPort, int bytes, int timeoutMs, CancellationToken ct)
    {
        foreach (var url in SpeedTargets)
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
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeoutMs);

                var sw = Stopwatch.StartNew();
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (!resp.IsSuccessStatusCode) continue;

                await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
                var buf = new byte[16 * 1024];
                long total = 0;
                while (total < bytes)
                {
                    var want = (int)Math.Min(buf.Length, bytes - total);
                    var read = await stream.ReadAsync(buf.AsMemory(0, want), cts.Token);
                    if (read <= 0) break;
                    total += read;
                    if (sw.ElapsedMilliseconds > timeoutMs) break;
                }
                sw.Stop();

                if (total < 16 * 1024) continue;   // 太小不足以判断速度，换下一个目标
                var kbps = (int)Math.Round(total * 8.0 / Math.Max(1, sw.ElapsedMilliseconds));
                return Math.Max(0, kbps);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* 换下一个目标 */ }
        }
        return 0;
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
                    await Parallel.ForEachAsync(Enumerable.Range(0, xrayNodes.Count),
                        new ParallelOptions { CancellationToken = ct },
                        async (i, ict) =>
                        {
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

            // 预热：节点 TCP+TLS 握手发生在这条请求（不计入延迟口径，对齐 v2rayN——
            // 它的计时只覆盖已建连后的 HTTP 往返，节点握手在内核后台完成）
            try
            {
                using var warm = new HttpRequestMessage(HttpMethod.Get, Target);
                using var wr = await http.SendAsync(warm, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch { /* 预热失败不影响正式计时 */ }

            var sw = Stopwatch.StartNew();
            using var resp = await http.GetAsync(Target, HttpCompletionOption.ResponseHeadersRead, ct);
            sw.Stop();
            var code = (int)resp.StatusCode;
            return code is 200 or 204 ? (int)sw.ElapsedMilliseconds : -1;
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

    /// 出口位置检测：每个节点走自己的 socks 入口请求 Cloudflare trace，读出**真实出口国家**（ISO 小写）。
    /// 与批量测速同一套做法（一个临时内核 + 每节点独立 socks 端口），避免逐个起进程。
    /// 只覆盖 xray 承载的协议；hy2/tuic（sing-box）暂不参与（返回空）。
    public static async Task<IReadOnlyDictionary<NodeItem, string>> DetectExitCountriesAsync(
        IReadOnlyList<NodeItem> nodes, string runtimeDir, int timeoutMs = 10000,
        Action<NodeItem, string>? onResult = null, CancellationToken ct = default)
    {
        var result = new ConcurrentDictionary<NodeItem, string>();
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

            await Parallel.ForEachAsync(Enumerable.Range(0, xrayNodes.Count),
                new ParallelOptions { CancellationToken = ct },
                async (i, ict) =>
                {
                    var country = await QueryExitCountryViaSocksAsync(ports[i], timeoutMs, ict);
                    result[xrayNodes[i]] = country;
                    onResult?.Invoke(xrayNodes[i], country);
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
    private static async Task<string> QueryExitCountryViaSocksAsync(int socksPort, int timeoutMs, CancellationToken ct)
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
            foreach (var line in text.Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith("loc=", StringComparison.Ordinal)) return t[4..].Trim().ToLowerInvariant();
            }
        }
        catch { }
        return "";
    }
}
