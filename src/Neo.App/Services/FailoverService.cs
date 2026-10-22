using System.Net;
using System.Net.Http;
using Neo.Core;
using Neo.Core.Core;
using Neo.Core.Models;

namespace Neo.App.Services;

public static class FailoverService
{
    public static event Action<string>? Switched;

    private static CancellationTokenSource? _cts;
    private static Task? _loop;
    private static volatile bool _failoverInProgress;
    private static int _failStreak;
    /// 本次会话是否已经提示过「系统代理被其他程序接管」（避免每 30 秒刷一条）。
    private static bool _reportedForeignProxy;
    private static DateTime _lastSwitchAt = DateTime.MinValue;
    private static DateTime _lastGiveUpAt = DateTime.MinValue;

    public static Action<Action>? RunOnUi;

    public static void Init(Action<Action>? runOnUi = null)
    {
        RunOnUi = runOnUi;
        ConnectionService.StateChanged += () =>
        {
            if (ConnectionService.IsConnected)
            {
                _lastGiveUpAt = DateTime.MinValue;
                Start();
            }
            else if (!_failoverInProgress) Stop();
        };
        HookSystemEvents();
        if (ConnectionService.IsConnected) Start();
    }

    // ---- 休眠唤醒 / 网络切换后立即复查 ----
    // 合盖唤醒、换 Wi-Fi、拔插网线后，内核的长连接可能已经死掉但 neo 并不知道，
    // 而定时循环最长要等 30 秒。这里在这些时刻立刻探一次，把「发现断了」的时间压到最短。
    private static bool _systemEventsHooked;
    private static DateTime _lastWakeCheckAt = DateTime.MinValue;

    private static void HookSystemEvents()
    {
        if (_systemEventsHooked) return;
        _systemEventsHooked = true;

        try
        {
            Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
            {
                if (e.Mode == Microsoft.Win32.PowerModes.Resume)
                    RequestImmediateCheck("系统已从休眠唤醒");
            };
        }
        catch (Exception ex)
        {
            LogBus.App($"注册电源事件失败（不影响其他功能）：{ex.GetType().Name}", "Warning");
        }

        try
        {
            System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += (_, e) =>
            {
                if (e.IsAvailable) RequestImmediateCheck("网络已恢复");
            };
            //   它的出现与消失必然触发 NetworkAddressChanged。旧实现无条件当成
            //   「网络地址已变化」→ 每次连接 TUN 都会立刻打印一条「立即复查节点连通性」，
            //   01:14:45.950 就打了一次，间隔 1.1 秒，正是 TUN 网卡建立时刻）。
            //   现改为：若变更来自 neo-tun（或刚连接/刚断开 TUN 的 5 秒窗口内），不触发复查。
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) =>
            {
                if (IsLikelyOurTunChange()) return;
                RequestImmediateCheck("网络地址已变化");
            };
        }
        catch (Exception ex)
        {
            LogBus.App($"注册网络事件失败（不影响其他功能）：{ex.GetType().Name}", "Warning");
        }
    }

    /// 依据两条独立信号（任一命中即认为与我方 TUN 相关）：
    ///   ① 系统里存在名为 neo-tun 的适配器（TUN 会话进行中）；
    ///   ② 距上次连接/断开 TUN 在 5 秒内（网卡创建/销毁瞬间的窗口）。
    /// 两者都只在「我们刚动过网络」时成立，不会掩盖用户换 WiFi / 插网线等真实变化
    /// （那些情况下 neo-tun 通常不存在，且已过窗口期）。
    /// 用 try/catch 兜底：判定失败时**返回 false**（宁可多探一次，也不要漏掉真实变化）。
    private static bool IsLikelyOurTunChange()
    {
        try
        {
            if ((DateTime.Now - _lastTunEventAt).TotalSeconds < 5) return true;
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (string.Equals(ni.Name, "neo-tun", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { /* 判定失败按「不是我们」处理，宁多探不漏判 */ }
        return false;
    }

    /// <summary>记录最近一次 TUN 网卡相关动作的时刻（由 ConnectionService 调用）。</summary>
    private static DateTime _lastTunEventAt = DateTime.MinValue;

    public static void MarkTunNetworkEvent() => _lastTunEventAt = DateTime.Now;
    /// 请求立刻做一次探活。带 10 秒去抖：唤醒瞬间网络栈往往还没就绪，
    /// 且这几个事件可能连着触发多次，避免同一时刻反复探测。
    private static void RequestImmediateCheck(string reason)
    {
        try
        {
            if (!ConnectionService.IsConnected) return;
            if ((DateTime.Now - _lastWakeCheckAt).TotalSeconds < 10) return;
            _lastWakeCheckAt = DateTime.Now;

            LogBus.App($"{reason} — 立即复查节点连通性");
            _ = Task.Run(async () =>
            {
                try
                {
                    // 给网络栈一点恢复时间，否则刚唤醒必然探测失败、白白消耗一次失败计数
                    await Task.Delay(TimeSpan.FromSeconds(3));
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await CheckOnceAsync(cts.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    LogBus.App($"即时复查异常：[{ex.GetType().Name}] {ex.Message}", "Warning");
                }
            });
        }
        catch { }
    }

    private static Task RunOnUiAsync(Action action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Run()
        {
            try { action(); tcs.TrySetResult(); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }
        // 退出中：不要再往正在销毁的队列里投递（那会 fail-fast 而不是抛异常）。
        if (App.IsShuttingDown) { tcs.TrySetCanceled(); return tcs.Task; }
        if (RunOnUi != null) { try { RunOnUi(Run); } catch { Run(); } } else Run();
        return tcs.Task;
    }

    private static Task<T> RunOnUiAsync<T>(Func<Task<T>> fn)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        async void Run()
        {
            try { tcs.TrySetResult(await fn()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }
        if (App.IsShuttingDown) { tcs.TrySetCanceled(); return tcs.Task; }
        if (RunOnUi != null) { try { RunOnUi(Run); } catch { Run(); } } else Run();
        return tcs.Task;
    }

    private static void Start()
    {
        if (_loop is { IsCompleted: false }) return;
        _cts = new CancellationTokenSource();
        _failStreak = 0;
        _reportedForeignProxy = false;
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        // 一旦接线断了，日志里同样是一片安静——沉默无法区分「正常」和「根本没跑」。
        LogBus.App($"自动故障转移已启动（每 30 秒探活一次，目标 {ProbeTargets.Length} 个）");
    }

    /// 用户主动断开 / 退出程序时调用：清掉「放弃后重连」的待办与计数。
    /// 目的：自愈不能覆盖用户意图 —— 用户刚点断开，不能被 5 分钟后的自动重连连回去。
    public static void CancelPendingReconnect()
    {
        _lastGiveUpAt = DateTime.MinValue;
        _lastSwitchAt = DateTime.MinValue;
        _failStreak = 0;
    }

    /// 停止探活循环（不阻塞等待）。用于无法 await 的同步调用点，如窗口关闭回调。
    /// 安全性依赖 App.IsShuttingDown 门控：退出时该标志先立，此后循环里任何
    /// 「投递回 UI 线程」的动作（RunOnUiAsync）都会直接放弃，不会去碰正在销毁的
    /// DispatcherQueue。因此这里不需要（也绝不能）阻塞等待：
    /// 循环可能正卡在 RunOnUiAsync 上等 UI 线程执行它投递的任务，
    /// 若在 UI 线程上 Wait 这个循环，就成了「UI 等循环、循环等 UI」的死锁。
    public static void Stop()
    {
        var wasRunning = _loop is { IsCompleted: false };
        var cts = _cts;
        var loop = _loop;
        _cts = null;
        _loop = null;
        _failStreak = 0;
        //   旧实现 Cancel 后立刻 Dispose：循环可能还停在 Task.Delay / RunOnUiAsync 上，
        //   下一轮访问 token 时抛 ObjectDisposedException，把「正常停止」变成后台异常。
        //   现在等循环真正退出后再释放（fire-and-forget，不阻塞同步调用方）。
        try { cts?.Cancel(); } catch { }
        DisposeCtsWhenLoopExits(cts, loop);
        if (wasRunning) LogBus.App("自动故障转移已停止（断开连接）");
    }

    /// 循环已结束 → 立即释放；仍在跑 → 挂一个 continuation，退出后释放，
    /// 绝不出现「Cancel 后马上 Dispose、循环还在用 token」的窗口。
    private static void DisposeCtsWhenLoopExits(CancellationTokenSource? cts, Task? loop)
    {
        if (cts == null) return;
        if (loop == null || loop.IsCompleted)
        {
            try { cts.Dispose(); } catch { }
            return;
        }
        _ = loop.ContinueWith(_ => { try { cts.Dispose(); } catch { } }, TaskScheduler.Default);
    }

    /// 停止探活循环并等它真正退出。用于退出流程——必须确保循环已经结束，
    /// 之后才销毁 DispatcherQueue，否则迟到的回调会撞上已销毁的队列，
    /// 以不可捕获的 fail-fast（0xc000027b）终止进程（本机 10-09 崩溃的机理）。
    /// 为什么必须 async：本方法由 UI 线程调用，而循环可能正在等待一个投递到 UI 线程的
    /// 任务完成。await 不占用 UI 线程，那个任务才能被泵执行、循环才能退出；
    /// 若改成同步 Wait 就是死锁。
    public static async Task StopAsync()
    {
        var loop = _loop;
        var cts = _cts;
        var wasRunning = loop is { IsCompleted: false };
        _cts = null;
        _loop = null;
        _failStreak = 0;

        try { cts?.Cancel(); } catch { }
        if (wasRunning && loop != null)
        {
            // 上限 3 秒兜底：Cancel 会立即打断 Task.Delay 与探活，正常远快于此。
            try { await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(3))); } catch { }
        }
        DisposeCtsWhenLoopExits(cts, loop);

        if (wasRunning) LogBus.App("自动故障转移已停止（断开连接）");
    }

    private static async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                if (ct.IsCancellationRequested) break;
                //   放弃切换后连接本来就是断的（TUN 模式会先断开再试备选），旧逻辑在这里 break，
                //   「5 分钟后重试」的承诺永远不会兑现。现在由 CheckOnceAsync 在断开态
                //   负责「满 5 分钟尝试重连当前节点」；用户主动断开时 Stop() 会取消循环。
                await CheckOnceAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogBus.App($"故障转移服务异常：[{ex.GetType().FullName}] {ex.Message}", "Error");
        }
    }

    /// 一次探活判定（含去抖与连续失败计数）。定时循环与「唤醒/网络变化」即时检查共用，
    /// 避免两条路径各写一份逻辑导致行为不一致。
    private static async Task CheckOnceAsync(CancellationToken ct)
    {
        if (!ConnectionService.IsConnected)
        {
            await TryReconnectAfterGiveUpAsync(ct);
            return;
        }
        if (!AppState.State.FailoverEnabled) { _failStreak = 0; return; }
        if ((DateTime.Now - _lastSwitchAt).TotalSeconds < 120) return;
        if ((DateTime.Now - _lastGiveUpAt).TotalSeconds < 300) return;

        // 自动修复系统代理：
        //   · 只在「已连接 + 非 TUN + 设置里开关打开」时检查；
        //   · 只在代理设置本来指向 neo 自己的端口、或被清空时才恢复；
        //     如果用户或别的代理软件把它改成了别的地址，绝不覆盖（自愈不能抢用户的选择）；
        //   · 用户主动断开/退出时循环已先 Stop()，这里不会跑。
        try
        {
            if (!ConnectionService.IsTunSession && !SystemProxy.IsEnabled && AppState.State.AutoRepairSystemProxy)
            {
                var own = "127.0.0.1:" + Neo.Core.Core.XrayConfigBuilder.DefaultSocksPort;
                var cur = SystemProxy.CurrentServer;
                bool isOurs = string.IsNullOrWhiteSpace(cur) || string.Equals(cur, own, StringComparison.OrdinalIgnoreCase);
                if (isOurs)
                {
                    SystemProxy.Enable(own);
                    LogBus.App("自动修复：系统代理被关闭（原设置指向本机 " + own + "），已重新开启；如是你手动关的，可在设置里关掉「自动修复系统代理」", "Warning");
                    IncidentLog.Record("auto-repair", "系统代理被关闭（原设置指向本机），已自动恢复");
                }
                else if (!_reportedForeignProxy)
                {
                    _reportedForeignProxy = true;
                    LogBus.App("检测到系统代理指向其他程序（" + cur + "），自动修复不会覆盖它", "Warning");
                    IncidentLog.Record("auto-repair", "系统代理已被其他程序接管，未覆盖（" + cur + "）");
                }
            }
        }
        catch { }

        if (await ProbeAsync(ct))
        {
            _failStreak = 0;
            return;
        }

        _failStreak++;
        LogBus.App($"故障转移探活失败（{_failStreak}/2）", "Warning");
        IncidentLog.Record("failover", "探活失败（" + _failStreak + "/2）");
        if (_failStreak < 2) return;

        await FailoverAsync(ct);
    }

    /// 循环不再退出，而是每满 5 分钟经 ConnectAsync 重试一次 AppState.CurrentNode；
    /// 成功即恢复并清掉放弃标记，失败则刷新 _lastGiveUpAt 再等 5 分钟。
    /// 并发安全：ConnectAsync 内部有 SessionGate，拿不到会直接拒绝（返回 false/错误），
    /// 本方法不需要额外加锁，也不会与用户手动连接抢内核。
    private static async Task TryReconnectAfterGiveUpAsync(CancellationToken ct)
    {
        // 从未放弃过（MinValue）→ 说明是用户主动断开/尚未连接，不能擅自连回去。
        if (_lastGiveUpAt == DateTime.MinValue) return;
        if ((DateTime.Now - _lastGiveUpAt).TotalSeconds < 300) return;
        if (!AppState.State.FailoverEnabled) return;

        var node = AppState.CurrentNode;
        if (node == null) return;

        // 先刷新时间戳，避免并发的即时复查/定时循环同时发起重连；
        // 失败路径保持「再等 5 分钟」的语义。
        _lastGiveUpAt = DateTime.Now;
        LogBus.App($"故障转移已放弃满 5 分钟，正在重新连接节点「{node.Name}」…", "Warning");

        bool ok;
        string? err;
        //   ConnectAsync 内部的 NotifyStateChanged（开始连接 / 失败收尾）在断开态会触发
        //   Init 的订阅者 Stop()，把正在重试的这个循环取消掉 —— 那样「5 分钟后重试」
        //   只会生效一次。FailoverAsync 本来就靠这个标志保护，这里保持一致。
        _failoverInProgress = true;
        try
        {
            try
            {
                (ok, err) = await RunOnUiAsync(() => ConnectionService.ConnectAsync(node));
            }
            catch (Exception ex)
            {
                ok = false;
                err = $"[{ex.GetType().Name}] {ex.Message}";
            }
        }
        finally
        {
            _failoverInProgress = false;
        }

        if (!ok)
        {
            LogBus.App($"重新连接节点「{node.Name}」失败：{err ?? "未知原因"} —— 5 分钟后重试", "Warning");
            return;
        }

        _lastGiveUpAt = DateTime.MinValue;
        _lastSwitchAt = DateTime.Now;
        _failStreak = 0;
        AppState.SetCurrentNode(node);
        var msg = $"已重新连接节点「{node.Name}」，故障转移恢复待命";
        LogBus.App(msg, "Warning");
        IncidentLog.Record("failover", msg);
        Switched?.Invoke(msg);
    }

    // 多目标探活：任一可达即视为节点健康。
    //   ① 省流量：单次 HTTPS 探活含端到端 TLS 握手约 32 KB（收 26KB + 发 6KB）。
    //      按 30 秒 × 2 目标 = 每小时 240 次，折算 **约 5.2 GB/月**。
    //      明文 HTTP 省掉那层握手，单次降到 KB 以下量级。
    //      注意：仍**穿代理**发起（否则测不出"节点能不能转发流量"），只是不做端到端 TLS。
    //      （例：proxy4 明文HTTP 连续结果 活,死,活,死,活）。说明节点本身在剧烈抖动，
    //      **单次探测结果不可靠** —— 因此必须保留「多目标 + 连续失败 2 次才切换」的现有设计，
    //      并把 google 系目标留在列表里做交叉印证，而不是简化成某一个"最准"的目标。
    // telegram 仍是用户真实常用目标，作为第二判据比再找一个 google 系目标更有意义。
    // 已知局限：候选节点的延迟验证走 RealDelayTester，它仍只打 google/generate_204
    //（为与 v2rayN 口径一致，未改动）；若 google 对整条链路不可达，备选会被全部判死。
    private static readonly string[] ProbeTargets =
    {
        "https://www.google.com/generate_204",
        "http://cp.cloudflare.com/generate_204",
        "https://api.telegram.org/",
    };

    // 并行探测：串行会让两个目标的超时叠加（最坏 20s），并行最坏仍是单次超时。
    private static Task<bool> ProbeAsync(CancellationToken ct) => ProbeAsync(ProbeTargets, ct);

    // 目标列表作为参数，便于对「全部目标都不可达」这一关键分支做验证（该分支决定是否触发切换）。
    private static async Task<bool> ProbeAsync(IReadOnlyList<string> targets, CancellationToken ct)
    {
        var tasks = targets.Select(t => ProbeOneAsync(t, ct)).ToList();
        try
        {
            while (tasks.Count > 0)
            {
                var done = await Task.WhenAny(tasks);
                tasks.Remove(done);
                if (await done) return true;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        return false;
    }

    private static async Task<bool> ProbeOneAsync(string url, CancellationToken ct)
    {
        try
        {
            using var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy($"socks5://127.0.0.1:{XrayConfigBuilder.DefaultSocksPort}"),
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromSeconds(8),
                // 不跟随重定向：telegram 根路径就是 302，跟下去会白拉一次请求、拉长探测时间
                AllowAutoRedirect = false,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            // 204/200 与 telegram 的 302 都算通，只要拿到有效 HTTP 响应即证明链路可用
            return (int)resp.StatusCode is >= 200 and < 400;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static async Task FailoverAsync(CancellationToken loopCt)
    {
        using var own = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = own.Token;
        _failoverInProgress = true;

        try
        {
            if (AppState.State.UseTun && ConnectionService.IsConnected)
            {
                LogBus.App("故障转移：先断开失联节点（TUN 模式避免测试流量绕行），随后直接实测候选", "Warning");
                try { await RunOnUiAsync(() => ConnectionService.Disconnect("故障转移触发断开")); }
                catch (Exception ex) { LogBus.App($"故障转移：断开时异常（忽略继续）：[{ex.GetType().Name}] {ex.Message}", "Warning"); }
                try { await Task.Delay(2500, ct); } catch (OperationCanceledException) { }
            }

            var current = AppState.CurrentNode;
            var candidates = AppState.Nodes
                .Where(n => !SameNode(n, current))
                .OrderBy(n => n.RealDelay > 0 ? n.RealDelay : (n.RealDelay == 0 ? 100_000 : 200_000))
                .ToList();

            _failStreak = 0;
            var tried = 0;

            foreach (var cand in candidates)
            {
                ct.ThrowIfCancellationRequested();
                if (tried >= 3) break;
                tried++;

                int ms;
                try
                {
                    ms = await RealDelayTester.RealPingAsync(cand, NeoPaths.RuntimeDir, ct: ct);
                }
                catch (OperationCanceledException) { throw; }
                catch { continue; }

                if (ms <= 0 || ms > 5000) continue;

                var name = cand.Name;
                bool ok;
                string? err;
                try
                {
                    (ok, err) = await RunOnUiAsync(() => ConnectionService.ConnectAsync(cand));
                }
                catch (Exception ex)
                {
                    ok = false;
                    err = $"[{ex.GetType().Name}] {ex.Message}";
                }
                if (!ok)
                {
                    LogBus.App($"故障转移切换失败（{name}）：{err}", "Error");
                    IncidentLog.Record("failover", "切换备选节点失败：" + name, err);
                    continue;
                }

                AppState.SetCurrentNode(cand);
                _lastSwitchAt = DateTime.Now;
                var msg = $"节点失联，已自动切换至「{name}」";
                LogBus.App(msg, "Warning");
                IncidentLog.Record("failover", msg, "原节点：" + (current?.Name ?? "?"));
                Switched?.Invoke(msg);
                return;
            }

            _lastGiveUpAt = DateTime.Now;
            LogBus.App("当前节点失联，未找到可用备用节点（5 分钟后将重试连接当前节点）", "Error");
            IncidentLog.Record("failover", "当前节点失联且无可用备选（5 分钟后重试）");
            Switched?.Invoke("节点失联，暂无可用备用节点（将每 5 分钟重试）");
        }
        finally
        {
            _failoverInProgress = false;
        }
    }

    private static bool SameNode(NodeItem? a, NodeItem? b) =>
        a != null && b != null && a.EndPoint == b.EndPoint && a.Protocol == b.Protocol && a.Name == b.Name;
}
