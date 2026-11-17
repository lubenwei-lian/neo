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
    private static readonly SemaphoreSlim CheckGate = new(1, 1);
    private static volatile bool _failoverInProgress;
    //   否则 failover 可能在几秒后把会话自动连回来。
    private static CancellationTokenSource? _inflight;
    private static int _failStreak;
    private static bool _reportedForeignProxy;
    private static DateTime _lastSwitchAt = DateTime.MinValue;
    private static DateTime _lastGiveUpAt = DateTime.MinValue;
    //   缩短到 60 秒（TUN 场景还会先尝试把原节点连回来，见 FailoverAsync 末尾）。
    private const int GiveUpRetrySeconds = 60;

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
            else
            {
                NetworkHealthService.Reset();
                if (!_failoverInProgress) Stop();
            }
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
                RequestImmediateCheck(e.IsAvailable ? "网络已恢复" : "网络已断开");
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

    /// 只忽略刚创建/销毁 TUN 的短窗口；网卡长期存在不代表真实网络没有变化。
    private static bool IsLikelyOurTunChange()
    {
        try
        {
            if ((DateTime.Now - _lastTunEventAt).TotalSeconds < 5) return true;
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
            NodeQualityHistory.Shared.Clear();
            var epoch = ConnectionService.SessionEpoch;
            var sessionToken = _cts?.Token ?? CancellationToken.None;

            LogBus.App($"{reason} — 立即复查节点连通性");
            _ = Task.Run(async () =>
            {
                try
                {
                    // 给网络栈一点恢复时间，否则刚唤醒必然探测失败、白白消耗一次失败计数
                    await Task.Delay(TimeSpan.FromSeconds(3), sessionToken);
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
                    cts.CancelAfter(TimeSpan.FromSeconds(30));
                    await CheckQueuedAsync(cts.Token, expectedEpoch: epoch, waitForGate: true);
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
        var epoch = ConnectionService.SessionEpoch;
        var fresh = NetworkHealthService.BeginSession(epoch);
        if (_loop is { IsCompleted: false })
        {
            if (fresh)
            {
                _failStreak = 0;
                _lastTrafficAt = DateTime.MinValue;
                var token = _cts?.Token ?? CancellationToken.None;
                _ = CheckNewSessionAsync(epoch, token);
            }
            return;
        }
        var cts = new CancellationTokenSource();
        _cts = cts;
        _failStreak = 0;
        _reportedForeignProxy = false;
        _lastTrafficAt = DateTime.MinValue;
        _loop = Task.Run(() => LoopAsync(cts.Token));
        // 一旦接线断了，日志里同样是一片安静——沉默无法区分「正常」和「根本没跑」。
        LogBus.App("网络可用检测已启动（连接后立即检测；正常每 30 秒复查，异常时 8 秒复测；自动切换遵循设置）");
    }

    private static async Task CheckNewSessionAsync(int epoch, CancellationToken ct)
    {
        try { await Task.Run(() => CheckQueuedAsync(ct, expectedEpoch: epoch, waitForGate: true), ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogBus.App($"连接后网络检测异常：{ex.GetType().Name}", "Warning"); }
    }

    /// <summary>与自动检查共用串行入口；手动检测只更新结果，不累计自动切换次数。</summary>
    public static async Task CheckNetworkNowAsync(CancellationToken ct = default)
    {
        if (!ConnectionService.IsConnected || App.IsShuttingDown) return;
        var epoch = ConnectionService.SessionEpoch;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts?.Token ?? CancellationToken.None);
        budget.CancelAfter(TimeSpan.FromSeconds(15));
        await Task.Run(() => CheckQueuedAsync(budget.Token, expectedEpoch: epoch, waitForGate: true,
            allowFailover: false), budget.Token).ConfigureAwait(false);
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
        NetworkHealthService.Reset();
        //   旧实现 Cancel 后立刻 Dispose：循环可能还停在 Task.Delay / RunOnUiAsync 上，
        //   下一轮访问 token 时抛 ObjectDisposedException，把「正常停止」变成后台异常。
        //   现在等循环真正退出后再释放（fire-and-forget，不阻塞同步调用方）。
        try { cts?.Cancel(); } catch { }
        DisposeCtsWhenLoopExits(cts, loop);
        if (wasRunning) LogBus.App("网络检测与自动故障转移已停止（断开连接）");
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
        NetworkHealthService.Reset();

        try { cts?.Cancel(); } catch { }
        if (wasRunning && loop != null)
        {
            // 上限 3 秒兜底：Cancel 会立即打断 Task.Delay 与探活，正常远快于此。
            try { await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(3))); } catch { }
        }
        DisposeCtsWhenLoopExits(cts, loop);

        if (wasRunning) LogBus.App("网络检测与自动故障转移已停止（断开连接）");
    }

    private static async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            await CheckOnceAsync(ct);
            while (!ct.IsCancellationRequested)
            {
                // 两次失败之间的 30 秒 + 10 秒超时，用户实际要卡 40～50 秒才能等到切换；
                // 失败一次后 8 秒就复测，真断线时总等待缩到 15～20 秒，误判也能更快被纠正。
                var health = NetworkHealthService.Current;
                var wait = _failStreak > 0 || health.Status is NetworkHealthStatus.Limited or NetworkHealthStatus.Unavailable ? 8 : 30;
                await Task.Delay(TimeSpan.FromSeconds(wait), ct);
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
        try { await CheckQueuedAsync(ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // 单轮检测异常不能让整个会话从此停止检测。
            LogBus.App($"网络检测本轮异常，下轮继续：[{ex.GetType().Name}] {ex.Message}", "Warning");
        }
    }

    private static async Task CheckQueuedAsync(CancellationToken ct, int? expectedEpoch = null,
        bool waitForGate = false, bool allowFailover = true)
    {
        if (waitForGate) await CheckGate.WaitAsync(ct);
        else if (!await CheckGate.WaitAsync(0, ct)) return;
        try
        {
            if (expectedEpoch.HasValue && expectedEpoch.Value != ConnectionService.SessionEpoch) return;
            await CheckCoreOnceAsync(ct, allowFailover);
        }
        finally { CheckGate.Release(); }
    }

    private static async Task CheckCoreOnceAsync(CancellationToken ct, bool allowFailover)
    {
        if (ct.IsCancellationRequested || App.IsShuttingDown) return;
        if (!ConnectionService.IsConnected)
        {
            if (allowFailover) await TryReconnectAfterGiveUpAsync(ct);
            return;
        }
        var epoch = ConnectionService.SessionEpoch;
        // 自动修复系统代理：
        //   · 只在「已连接 + 非 TUN + 设置里开关打开」时检查；
        //   · 只在代理设置本来指向 neo 自己的端口、或被清空时才恢复；
        //     如果用户或别的代理软件把它改成了别的地址，绝不覆盖（自愈不能抢用户的选择）；
        //   · 用户主动断开/退出时循环已先 Stop()，这里不会跑。
        try
        {
            if (!ConnectionService.IsTunSession && !SystemProxy.IsEffective && AppState.State.AutoRepairSystemProxy)
            {
                var cur = SystemProxy.CurrentServer;
                bool isOurs = string.IsNullOrWhiteSpace(cur)
                    || LocalProxySettings.IsOwnedServer(cur, LocalPorts.Socks, LocalPorts.Http);
                if (isOurs)
                {
                    var repairEpoch = ConnectionService.SessionEpoch;
                    await RunOnUiAsync(() =>
                    {
                        // 投递之后可能已经断开/切换，必须在实际写入前再检查。
                        if (ct.IsCancellationRequested || repairEpoch != ConnectionService.SessionEpoch
                            || !ConnectionService.IsConnected || ConnectionService.IsTunSession || App.IsShuttingDown
                            || !AppState.State.AutoRepairSystemProxy || SystemProxy.IsEffective) return;
                        var current = SystemProxy.CurrentServer;
                        if (!string.IsNullOrWhiteSpace(current)
                            && !LocalProxySettings.IsOwnedServer(current, LocalPorts.Socks, LocalPorts.Http)) return;
                        if (!SystemProxy.Enable(LocalProxySettings.Server, repair: true))
                        {
                            if (!_reportedForeignProxy)
                            {
                                _reportedForeignProxy = true;
                                LogBus.App("系统代理未实际生效，自动修复未获 Windows/代理所有权确认；请检查其他代理程序或手动重连", "Warning");
                                ConnectionService.NotifyUser("系统代理未实际生效，自动修复失败；请检查其他代理程序或手动重连");
                            }
                            return;
                        }
                        _reportedForeignProxy = false;
                        LogBus.App("自动修复：Windows 实际系统代理未生效，已恢复并核验 neo 的 HTTP/HTTPS/SOCKS 配置", "Warning");
                        IncidentLog.Record("auto-repair", "系统代理实际配置不一致，已恢复并核验");
                    });
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

        if (ct.IsCancellationRequested || !ConnectionService.IsConnected || App.IsShuttingDown
            || epoch != ConnectionService.SessionEpoch) return;

        //   多服务商 HTTPS 204 回退；普通模式分别验证 HTTP 与 SOCKS，TUN 验证系统路由。
        //   单个目标或单条路径失败不等于整个节点失联。
        //   用户正在刷网页（有明显流量）却被切走，就是「刷着刷着突然断网几秒」的来源之一。
        //   有流量时阈值放宽到 4 次、无流量时 3 次（见下方阈值说明）；按 8–10 秒探活周期，
        //   真断流约 24–32 秒触发切换 —— 换来的是一天几十次误切的大幅减少。
        var recentBytes = TakeTrafficDelta();
        var hadTraffic = recentBytes > 60 * 1024;
        var measuredKey = ConnectionService.ConnectedNode is { } activeNode ? AppState.NodeKey(activeNode) : null;

        var result = await NetworkHealthService.CheckAsync(epoch, ct);
        if (result == null) return;
        if (!allowFailover) return;
        var healthy = result.Available;
        if (ct.IsCancellationRequested || !AppState.State.FailoverEnabled || !ConnectionService.IsConnected
            || epoch != ConnectionService.SessionEpoch)
        {
            if (!AppState.State.FailoverEnabled) _failStreak = 0;
            return;
        }
        if ((DateTime.Now - _lastSwitchAt).TotalSeconds < 60) return;
        if ((DateTime.Now - _lastGiveUpAt).TotalSeconds < GiveUpRetrySeconds) return;
        if (measuredKey != null) NodeQualityHistory.Shared.RecordHealth(measuredKey, healthy);
        if (healthy)
        {
            _failStreak = 0;
            await MaybeCheckThroughputAsync(recentBytes, ct);
            return;
        }

        //   依据（10-23 日志实测，旧阈值 3/2 时期）：切换节点相关日志 77 条、探活失败 51 行，
        //   即一天几十次切换/误切；探活只打 3 个目标，个别目标抖动就会触发断流式切换，
        //   用户体感就是「刷着刷着卡一下」。放宽到 4/3 后误切大幅减少。
        //   恢复时间变长；若体感变成「断得久」，优先调这里的阈值而不是退回 2/3。
        var threshold = hadTraffic ? 4 : 3;
        //   桥不可用/异常时回退下方原 C# 判定。
        var rust = Neo.Core.Bridge.RustCoreBridge.TryFailoverDecision(
            DateTimeOffset.Now.ToUnixTimeMilliseconds(),
            ok: false,
            hadTraffic: hadTraffic,
            fails: _failStreak,
            lastSwitchMs: _lastSwitchAt == DateTime.MinValue
                ? null
                : new DateTimeOffset(DateTime.SpecifyKind(_lastSwitchAt, DateTimeKind.Local)).ToUnixTimeMilliseconds());
        if (rust != null)
        {
            _failStreak = rust.Fails;
            var shownFails = rust.Decision == "switch" ? threshold : _failStreak;
            LogBus.App($"故障转移探活失败（{shownFails}/{threshold}）· Rust 判定 {rust.Decision}", "Warning");
            IncidentLog.Record("failover", "探活失败（" + shownFails + "/" + threshold + "，Rust " + rust.Decision + "）");
            if (rust.Decision == "cooldown") return;
            if (hadTraffic && rust.Decision == "keep")
            {
                LogBus.App("探活失败但最近 30 秒仍有真实流量，判定为「个别站点不可达」，暂不切换", "Warning");
                return;
            }
            if (rust.Decision == "keep") return;
            if (rust.LastSwitchMs is { } switchMs)
                _lastSwitchAt = DateTimeOffset.FromUnixTimeMilliseconds(switchMs).LocalDateTime;
            await FailoverAsync(ct);
            return;
        }

        _failStreak++;
        LogBus.App($"故障转移探活失败（{_failStreak}/{threshold}）", "Warning");
        IncidentLog.Record("failover", "探活失败（" + _failStreak + "/" + threshold + "）");
        if (hadTraffic && _failStreak < threshold)
        {
            LogBus.App("探活失败但最近 30 秒仍有真实流量，判定为「个别站点不可达」，暂不切换", "Warning");
            return;
        }
        if (_failStreak < threshold) return;

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
        if ((DateTime.Now - _lastGiveUpAt).TotalSeconds < GiveUpRetrySeconds) return;
        if (!AppState.State.FailoverEnabled) return;

        var epoch = ConnectionService.SessionEpoch;
        var node = AppState.CurrentNode;
        if (node == null) return;

        // 先刷新时间戳，避免并发的即时复查/定时循环同时发起重连；
        // 失败路径保持「再等 5 分钟」的语义。
        _lastGiveUpAt = DateTime.Now;
        LogBus.App($"故障转移已放弃满 {GiveUpRetrySeconds} 秒，正在重新连接节点「{node.Name}」…", "Warning");

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
                (ok, err) = await RunOnUiAsync(() =>
{
    if (ct.IsCancellationRequested || !AppState.State.FailoverEnabled || App.IsShuttingDown
        || _lastGiveUpAt == DateTime.MinValue)
        return Task.FromResult((false, (string?)"自动重连已取消"));
    return ConnectionService.ConnectAsync(node, expectedEpoch: epoch, ct: ct);
});
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
        var msg = $"已重新连接节点「{node.Name}」，故障转移恢复待命";
        LogBus.App(msg, "Warning");
        IncidentLog.Record("failover", msg);
        Switched?.Invoke(msg);
    }

    private static DateTime _lastTrafficAt = DateTime.MinValue;
    private static long _lastTrafficBytes;

    /// <summary>
    /// 「节点真的断了」与「只是探活目标不可达」，以及判断用户是否正在用网。
    /// 首次调用只建立基线，返回 -1（无数据）。
    /// </summary>
    private static long TakeTrafficDelta()
    {
        try
        {
            if (StatsSampler.Latest is not { } snap) return -1;
            var total = snap.Uplink + snap.Downlink;
            var now = DateTime.Now;
            if (_lastTrafficAt == DateTime.MinValue)
            {
                _lastTrafficAt = now;
                _lastTrafficBytes = total;
                return -1;
            }
            var dt = (now - _lastTrafficAt).TotalSeconds;
            if (dt < 3) return -1;   // 采样间隔太短，差值没意义
            var delta = total - _lastTrafficBytes;
            _lastTrafficAt = now;
            _lastTrafficBytes = total;
            return delta;
        }
        catch { return -1; }
    }

    /// <summary>吞吐抽检计数（每 10 轮探活做一次，约 5 分钟）。</summary>
    private static int _probeTick;
    private static int _slowThroughputStreak;
    private static int _throughputEpoch = -1;

    /// <summary>
    /// 定期「塞一点流量」测真实吞吐：探活只能证明节点还能转发，证明不了「有带宽」。
    /// 实测存在「延迟 114ms 但只有几十 KB/s」的节点，浏览网页表现为「能打开但一直转圈」。
    /// 判据：128 KB 下载平均吞吐 &lt; 30 KB/s 连续 2 次 → 判定节点劣化并触发切换。
    /// 跳过 —— 那时测到的是共享带宽，会误判。
    /// </summary>
    private static async Task MaybeCheckThroughputAsync(long recentBytes, CancellationToken ct)
    {
        var epoch = ConnectionService.SessionEpoch;
        var measuredKey = ConnectionService.ConnectedNode is { } activeNode ? AppState.NodeKey(activeNode) : null;
        if (_throughputEpoch != epoch)
        {
            _throughputEpoch = epoch; _probeTick = 0; _slowThroughputStreak = 0;
        }
        if (++_probeTick < 10) return;
        if (_probeTick % 10 != 0) return;
        if (recentBytes > 300 * 1024)
        {
            LogBus.App("吞吐抽检：用户正在使用网络，跳过本轮（避免共享带宽干扰判断）", "Debug");
            return;
        }

        var bps = await MeasureThroughputAsync(ct);
        if (ct.IsCancellationRequested || epoch != ConnectionService.SessionEpoch
            || !ConnectionService.IsConnected || !AppState.State.FailoverEnabled) return;
        if (bps < 0) return;   // 测量失败：探活已经判过，不重复叠加
        if (measuredKey != null) NodeQualityHistory.Shared.RecordThroughput(measuredKey, bps);
        var kb = bps / 1024.0;
        if (bps < 30 * 1024)
        {
            _slowThroughputStreak++;
            LogBus.App($"吞吐抽检：{kb:F0} KB/s（低于 30 KB/s，{_slowThroughputStreak}/2）", "Warning");
            if (_slowThroughputStreak >= 2)
            {
                _slowThroughputStreak = 0;
                LogBus.App("吞吐抽检：节点持续低吞吐，触发故障转移", "Warning");
                IncidentLog.Record("failover", "吞吐抽检判定节点低吞吐", $"{kb:F0} KB/s");
                await FailoverAsync(ct);
            }
        }
        else
        {
            _slowThroughputStreak = 0;
            LogBus.App($"吞吐抽检：{kb:F0} KB/s（正常）", "Debug");
        }
    }

    /// <summary>经本地 socks 端口下载 128 KB 测真实吞吐（B/s）；失败返回 -1。</summary>
    private static async Task<double> MeasureThroughputAsync(CancellationToken ct)
    {
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(12));
            using var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy($"socks5://127.0.0.1:{LocalPorts.Socks}"),
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromSeconds(4),
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
            using var resp = await http.GetAsync(
                "https://speed.cloudflare.com/__down?bytes=131072",
                HttpCompletionOption.ResponseHeadersRead, budget.Token);
            if (!resp.IsSuccessStatusCode) return -1;

            using var bodyBudget = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
            bodyBudget.CancelAfter(TimeSpan.FromSeconds(8));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long total = 0;
            var buf = new byte[16 * 1024];
            await using var s = await resp.Content.ReadAsStreamAsync(bodyBudget.Token);
            while (sw.ElapsedMilliseconds < 8000)
            {
                var n = await s.ReadAsync(buf, bodyBudget.Token);
                if (n <= 0) break;
                total += n;
            }
            sw.Stop();
            if (total == 0) return -1;
            return total / Math.Max(0.001, sw.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return -1; }
    }

    public static void CancelInFlight()
    {
        try { _inflight?.Cancel(); } catch { }
    }

    private static async Task FailoverAsync(CancellationToken loopCt)
    {
        if (loopCt.IsCancellationRequested || !AppState.State.FailoverEnabled || App.IsShuttingDown) return;
        var intent = new SessionIntent(ConnectionService.SessionEpoch);
        using var own = CancellationTokenSource.CreateLinkedTokenSource(loopCt);
        own.CancelAfter(TimeSpan.FromMinutes(3));
        _inflight = own;
        var ct = own.Token;
        _failoverInProgress = true;
        var disconnectedForTunTest = false;

        try
        {
            if (AppState.State.UseTun && ConnectionService.IsConnected)
            {
                disconnectedForTunTest = true;
                LogBus.App("故障转移：先断开失联节点（TUN 模式避免测试流量绕行），随后直接实测候选", "Warning");
                var reset = await RunOnUiAsync(() =>
                {
                    if (!ConnectionService.DisconnectIfCurrent(intent.Epoch, "failover TUN probe", out var next))
                        return Task.FromResult((false, (string?)null));
                    intent.Advance(intent.Epoch, next);
                    return Task.FromResult((true, (string?)null));
                });
                if (!reset.Item1) return;
                //   可能在网卡/路由还在时测候选，3 个候选会被残留 TUN 路由全部判死。
                //   这里等拆除任务结束（最多 12 秒），再做候选探测。
                try
                {
                    var teardown = ConnectionService.LastTunTeardown;
                    if (teardown != null && !teardown.IsCompleted)
                        await teardown.WaitAsync(TimeSpan.FromSeconds(12), ct);
                    else
                        await Task.Delay(2500, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (TimeoutException) { LogBus.App("TUN 拆除尚未完成，本轮故障转移已取消", "Warning"); return; }
            }

            var current = AppState.CurrentNode;
            //   原因：校园网/手机热点对 **UDP 流量（hysteria2 / TUIC）限速极严** —— 同一时刻实测
            //   hy2 台北节点下载 0.3 MB/s，而 vless TCP 节点 2.5–3.9 MB/s（相差 8–10 倍），
            //   表现为「能开网页但速度连 1MB/s 都上不去」。故障转移原先只按延迟排序，
            //   hy2 节点延迟好看（UDP 握手快）就容易被选中，用户于是长期跑在限速的 UDP 上。
            //   这里把 UDP 类节点排到 TCP 类节点之后，仅在没有任何可用 TCP 节点时才会退给它。
            // 切到别的地区虽然能用，但出口 IP 变了，用户会察觉「怎么跳去美国了」。
            var currentRegion = NodeGrouper.RegionOf(current?.Name ?? "");
            var candidates = AppState.Nodes
                .Where(n => n.IsValid)
                .Where(n => !SameNode(n, current))
                //   这些节点在 TUN 下会被 ConnectionService 直接拒绝，留在候选里只会白白消耗 3 次尝试机会。
                .Where(n => !(AppState.State.UseTun && n.RequiresXray))
                .OrderBy(n => !string.IsNullOrEmpty(currentRegion)
                              && string.Equals(NodeGrouper.RegionOf(n.Name), currentRegion, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(n => n.Protocol is NodeProtocol.Hysteria2 or NodeProtocol.TUIC or NodeProtocol.AnyTls ? 1 : 0)
                .ThenBy(n => NodeQualityHistory.Shared.Score(AppState.NodeKey(n), n.RealDelay))
                .ToList();

            _failStreak = 0;
            var tried = 0;

            foreach (var cand in candidates)
            {
                ct.ThrowIfCancellationRequested();
                if (!AppState.State.FailoverEnabled || intent.Epoch != ConnectionService.SessionEpoch) return;
                if (!disconnectedForTunTest && !ConnectionService.IsConnected)
                {
                    LogBus.App("故障转移：会话已被断开，放弃本次切换", "Debug");
                    return;
                }
                if (tried >= 3) break;
                tried++;

                int ms;
                try
                {
                    // 候选测试超时 3s：故障转移要快，测不通立刻换下一个（默认 10s 会让切换卡很久）。
                    ms = await RealDelayTester.RealPingAsync(cand, NeoPaths.RuntimeDir, timeoutMs: 3000, ct: ct);
                }
                catch (OperationCanceledException) { throw; }
                catch { continue; }

                if (intent.Epoch != ConnectionService.SessionEpoch || ct.IsCancellationRequested) return;
                NodeQualityHistory.Shared.RecordLatency(AppState.NodeKey(cand), ms);
                if (ms <= 0 || ms > 5000) continue;

                var name = cand.Name;
                bool ok;
                string? err;
                try
                {
                    // 失败或场景不支持时 SwitchNodeFastAsync 内部自动回退普通重连。
                    CoreState.Report("failover", ConnectionService.CorePid, ConnectionService.CoreKind, "failover to " + cand.Name, sessionEpoch: intent.Epoch);
                    var epoch = intent.Epoch;
                    (ok, err) = await RunOnUiAsync(() =>
                    {
                        if (ct.IsCancellationRequested || !AppState.State.FailoverEnabled || App.IsShuttingDown)
                            return Task.FromResult((false, (string?)"自动切换已取消"));
                        return ConnectionService.SwitchNodeFastAsync(cand, expectedEpoch: epoch, ct: ct, intent: intent);
                    });
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

                // The connection controller owns selection persistence.
                _lastSwitchAt = DateTime.Now;
                var msg = $"节点失联，已自动切换至「{name}」";
                LogBus.App(msg, "Warning");
                IncidentLog.Record("failover", msg, "原节点：" + (current?.Name ?? "?"));
                Switched?.Invoke(msg);
                return;
            }

            //   不能干等下一轮 —— 先把原节点连回来恢复网络，下一轮探活再继续找备选。
            if (disconnectedForTunTest && current != null && !ct.IsCancellationRequested && AppState.State.FailoverEnabled)
            {
                try
                {
                    var epoch = intent.Epoch;
                    var (rok, rerr) = await RunOnUiAsync(() => ConnectionService.ConnectAsync(current, force: true,
                        expectedEpoch: epoch, ct: ct));
                    if (rok)
                    {
                        LogBus.App("故障转移：备选节点均不可用，已先连回原节点（网络恢复）", "Warning");
                        IncidentLog.Record("failover", "备选均不可用，已连回原节点", current.Name);
                        return;
                    }
                    LogBus.App($"故障转移：连回原节点也失败：{rerr}", "Error");
                }
                catch (Exception ex)
                {
                    LogBus.App($"故障转移：连回原节点异常：[{ex.GetType().Name}] {ex.Message}", "Warning");
                }
            }

            if (intent.Epoch != ConnectionService.SessionEpoch) return;
            _lastGiveUpAt = DateTime.Now;
            LogBus.App($"当前节点失联，未找到可用备用节点（{GiveUpRetrySeconds} 秒后将重试连接当前节点）", "Error");
            IncidentLog.Record("failover", $"当前节点失联且无可用备选（{GiveUpRetrySeconds} 秒后重试）");
            Switched?.Invoke($"节点失联，暂无可用备用节点（将每 {GiveUpRetrySeconds} 秒重试）");
        }
        catch (OperationCanceledException) when (!loopCt.IsCancellationRequested)
        {
            // 那里的 catch 会让整个探活循环退出，本会话再也不自动切换。
            if (ct.IsCancellationRequested)
                LogBus.App("故障转移：已被取消（用户断开或退出）", "Debug");
            else
                LogBus.App("故障转移：3 分钟预算用尽，已放弃本次（探活循环继续）", "Warning");
        }
        finally
        {
            _failoverInProgress = false;
            if (ReferenceEquals(_inflight, own)) _inflight = null;
        }
    }

    private static bool SameNode(NodeItem? a, NodeItem? b) =>
        a != null && b != null && a.EndPoint == b.EndPoint && a.Protocol == b.Protocol && a.Name == b.Name;
}
