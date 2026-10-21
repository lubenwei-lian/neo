using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Neo.Core;
using Neo.Core.Core;
using Neo.Core.Models;

namespace Neo.App.Services;

public static class ConnectionService
{
    private static CoreProcess? _xray;

    /// 内核最近输出（仅内存，最近 60 行）。用途：内核秒退/崩溃时，把 FATAL 原因写进 Error 日志。
    /// 因为 sing-box 的 FATAL 行被判成 Info 后被级别门控吞掉（LogBus.Core 已同步修）。
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> _kernelTail = new();

    /// TUN 启动前「清残留网卡」只在本进程第一次 TUN 连接时做。
    /// 为什么只做一次：每次断开后，NetworkInterface 里会短暂残留已删除的 neo-tun 幽灵条目，
    /// 「上次崩溃留下的残留」——那只会出现在进程启动后的第一次连接。
    private static bool _tunPreStartGuardDone;

    /// 本次会话是否以 TUN 模式启动（用于断开时决定是否清理网卡与路由）。
    ///   若断开时不显式清理，内核进程即便退出，网卡与路由也可能残留，
    ///   导致流量仍被接管 —— 用户报的「TUN 打开就关闭不掉」。
    private static bool _tunSessionActive;

    ///   会话门 —— 保证**同一时刻只有一个** ConnectAsync / Disconnect 在改内核与网络状态。
    /// 为什么需要：ConnectAsync 原先**没有入口互斥**（第 88 行只判断「同一节点已连接」）。
    ///   而它有 7 个调用点，其中两条在**非 UI 线程**：
    ///     · FailoverService.cs:383  故障转移（后台探活线程）
    ///     · ConnectionService.cs:1021  内核自动重启（Process.Exited 线程池）
    ///   这两条与「用户手动点连接」可同时进入 → 各自 new CoreProcess 并 Start
    ///   → 双内核抢 10808，其中一个绑定失败成为**无句柄幽灵进程**，
    ///   Disconnect 停不掉（_xray 只记录了最后一个）→ 表现为「端口被占用 / 断不干净」。
    /// 为什么不能用 lock(_xray)：CoreProcess 内部订阅了 Process 事件，
    ///   在事件回调路径上加锁会与 Start/Stop 的锁形成交叉死锁（子代理提醒，我未独立验证，
    ///   故采用**独立哨兵**而非复用对象锁）。
    /// 语义：`WaitAsync(0)` 拿不到就**明确拒绝**并留痕，不排队 ——
    ///   排队会让「用户点连接」被一次后台自动重启阻塞数秒，体感更差；
    ///   调用方拿到 false 会正常走错误通道（如故障转移会判定本次候选失败）。
    private static readonly SemaphoreSlim SessionGate = new(1, 1);

    ///   **不成立**，因为 _tunSessionActive 只在所有 early return 之后赋值；
    ///   但它指出的**另一半**是真问题，我确认并在此修复）：
    ///   拆除动作（TeardownTunNetwork）是按**名字/地址**匹配的：
    ///     · 网卡：Get-NetAdapter -Name neo-tun → Remove-NetAdapter -Name neo-tun
    ///     · 路由：NextHop -eq 172.19.0.1 或 InterfaceAlias -eq neo-tun
    ///   **不区分是哪一次会话的对象**。而它是 fire-and-forget（Task.Run），
    ///   与紧随其后的 ConnectAsync 无同步 —— 快速「断 → 连」时，
    ///   后台的拆除可能删掉**新会话刚建的** neo-tun 网卡/路由，
    ///   导致新会话失去 TUN 接管（流量泄漏 / strict_route 下直接失败）。
    /// 修法：用一把**独立的拆除锁**把 TeardownTunNetwork 串行化，
    ///   并记录每次拆除时的**会话序号**；启动新会话时先等上一次拆除完成。
    ///   不能复用 SessionGate（ConnectAsync 内部会调 Disconnect，会自死锁）。
    private static readonly SemaphoreSlim TeardownGate = new(1, 1);

    /// <summary>已完成/进行中的拆除任务，供新会话在启动前等待。</summary>
    private static Task _lastTeardown = Task.CompletedTask;

    /// <summary>当前是否已有会话操作在进行（供 UI 判断与展示）。</summary>
    public static bool IsSessionBusy => SessionGate.CurrentCount == 0;

    public static bool IsConnected { get; private set; }

    public static bool IsConnecting { get; private set; }
    public static NodeItem? ConnectedNode { get; private set; }
    public static DateTime? ConnectedAt { get; private set; }

    public static string CoreKind { get; private set; } = "xray";

    /// 本次连接实际参与加速的节点数（1 = 未启用多节点加速）。
    public static int MultiNodeCount { get; private set; } = 1;

    /// 参与加速的地区显示名（未启用时为 null）。
    public static string? MultiNodeRegion { get; private set; }

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    public static void RelaunchElevated()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--takeover",
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            };
            System.Diagnostics.Process.Start(psi);
            LogBus.App("正在以管理员身份重启…");

            // 必须走统一的「有序退出」而不是直接 Application.Current.Exit()。
            // 直接 Exit 会跳过全部收尾（IsShuttingDown 未立、统计采样 / 故障转移探活 /
            // 各页计时器仍在跑、连接未断开使 xray 变孤儿且 10808 被占、托盘图标未释放），
            // 而 Exit 之后这些后台活动会继续去碰正在销毁的 DispatcherQueue ——
            // 钩子为 null 时（理论上不该发生）退回直接 Exit，不能让用户卡在这里。
            var exit = App.ExitRequested;
            if (exit != null) exit();
            else Microsoft.UI.Xaml.Application.Current.Exit();
        }
        catch (Exception ex) { LogBus.Exception("提权重启失败", ex); }
    }

    public static event Action? StateChanged;

    /// 以前 ReloadForSettingsAsync 把 ConnectAsync 的失败结果直接丢掉 —— 用户改完设置
    /// 内核没重载成功，界面却毫无提示。现在失败时先写 Error 日志，再 marshal 回 UI 线程触发本事件；
    /// 订阅/取消订阅由页面负责（UI 面），服务侧不持有订阅者生命周期。
    public static event Action<string>? SettingsReloadFailed;

    public static event Action<string>? LogLine;

    public static string ConfigPath => Path.Combine(NeoPaths.DataDir, "config.json");

    public static async Task<(bool Ok, string? Error)> ConnectAsync(NodeItem node, bool force = false)
    {
        if (!force && IsConnected && ConnectedNode != null && SameNode(ConnectedNode, node))
            return (true, null);

        //   正在建会话 —— 直接拒绝，避免双内核抢端口。
        if (!await SessionGate.WaitAsync(0))
        {
            LogBus.App($"连接请求被拒：已有会话操作在进行中（{node.Name}）", "Warning");
            return (false, "已有连接操作正在进行，请稍候重试");
        }
        try
        {
            LogBus.App($"连接请求 → {node.Name}（{AppState.ProtocolName(node.Protocol)} · {node.EndPoint}）· TUN={(AppState.State.UseTun ? "开" : "关")}", "Debug");

            IsConnecting = true;
            NotifyStateChanged();
        if (AppState.State.UseTun && !IsElevated)
            return (false, "TUN 模式需要管理员权限 — 请点击 TUN 开关并确认「以管理员重启」");

        Disconnect("连接前重置上一次会话");

        CleanupOrphanCores();

        //   否则后台的 Remove-NetAdapter / Remove-NetRoute 可能落在**新会话刚建好**
        //   网卡之后，把新会话的 TUN 接管拆掉（旧代码是 fire-and-forget，无同步）。
        //   最多等 5 秒：拆除本身会 3 秒超时，超时不阻断新会话（宁可留残留也不能连不上）。
        try
        {
            var pending = _lastTeardown;
            if (pending != null && !pending.IsCompleted)
            {
                var finished = await Task.WhenAny(pending, Task.Delay(5000)).ConfigureAwait(false);
                if (!ReferenceEquals(finished, pending))
                    LogBus.App("上一次 TUN 拆除仍未完成（已等 5 秒）——继续建立新会话", "Warning");
            }
        }
        catch (Exception ex) { LogBus.App($"等待上次 TUN 拆除异常：{ex.GetType().Name}", "Warning"); }

        //   等满 2 秒（10×200ms）就继续。若端口仍被占着，照样去启动内核，
        //   用户只会看到一个笼统的「内核启动失败」。现改为：等不到就明确报错并留 Error 痕。
        if (!await WaitPortsFreeAsync())
        {
            var busyPort = FirstBusyPort();   
            LogBus.App($"连接中止：本地端口 {busyPort} 仍被占用（等待 2 秒后仍未释放）", "Error");
            return (false, $"本地端口 {busyPort} 被占用 —— 可能有残留内核或其它程序在用，请稍后重试");
        }

        if (!node.IsValid) return (false, "节点无效（缺地址或端口）");

        bool useTun = AppState.State.UseTun;
        bool useSingBox = useTun || node.Protocol is NodeProtocol.Hysteria2 or NodeProtocol.TUIC;

        // 多节点加速：xray 路径与 TUN（sing-box）路径都支持。
        // 于是 TUN 模式永远单节点。现在 TUN 也能并发 —— 前提是使用**自编译的 sing-box**
        // 内核是否支持由探测决定，不支持时自动退回单节点，不会让 TUN 直接连不上（见下方探测）。
        var group = new List<NodeItem> { node };
        if (AppState.State.MultiNodeEnabled)
        {
            try
            {
                group = NodeGrouper.PickGroup(AppState.Nodes, node,
                    Math.Clamp(AppState.State.MultiNodeMax, 2, 64));
            }
            catch (Exception ex)
            {
                LogBus.App($"多节点加速：组队失败，退回单节点（{ex.Message}）", "Warning");
                group = new List<NodeItem> { node };
            }
        }
        MultiNodeCount = group.Count;
        MultiNodeRegion = group.Count > 1 ? NodeGrouper.RegionDisplay(NodeGrouper.RegionOf(node.Name)) : null;

        // TUN 下若组到多个节点，但当前 sing-box 是官方版（无 balancer 出站），
        // 必须退回单节点 —— 否则内核会因 unknown outbound type 直接启动失败、TUN 连不上。
        if (useTun && group.Count > 1 && !SingBoxSupportsBalancerInternal())
        {
            LogBus.App($"TUN 多节点并发需要自编译的 sing-box（当前内核不支持 balancer 出站），本次按单节点运行", "Warning");
            group = new List<NodeItem> { node };
            MultiNodeCount = 1;
            MultiNodeRegion = null;
        }

        // 「冷启动有延迟兜底测速，但实际只测速一个节点，其他的不测速」。
        // 原实现是 TryAutoMeasureDelayAsync(node) —— 只测**当前那一个**节点，
        // 而它是走「已连接的代理」测的；经 balancer 时流量落到哪个节点是随机的，
        // 根本无法定向测某一个节点。于是池里其余节点的 RealDelay 恒为 0。
        // 后果直接打在加权模式上：ComputeWeights 拿到的 delaysMs 全是 0，
        // 只能全部给 1 份 ⇒ 用户选的「加权」实际退化成「均衡」。
        //   且生成的配置里 balancer 成员恰好 = 8 份（= 8 节点 × 1 份额，一名一份）。
        // 修法：复用 RealDelayTester.RealPingBatchAsync（v2rayN 式批量测速 ——
        // 一个临时内核实例承载全部节点、每节点独立 socks 端口、并行测完）。
        // 它用的是**随机空闲端口**、不占 10085 统计端口，因此与已连接会话不冲突。
        // 只测「RealDelay == 0」的节点，已有结果的跳过 ⇒ 同一会话内第二次连接几乎是瞬时的。
        // 位置检测跑过之后：把「实际出口国家与名称标记地区不符」的节点踢出并发组。
        //   用户明确不接受「标着日本、实际从别处出去」；这类节点混在池里会让出口 IP 跨国跳，
        //   登录态、风控、流媒体解锁都会出问题。没检测过的节点（ExitCountry 为空）保持原样，
        //   避免把「没测」当成「不符」。
        if (group.Count > 1)
        {
            var claimedRegion = NodeGrouper.RegionOf(node.Name);
            if (claimedRegion != "other")
            {
                var bad = group.Where(n => !ReferenceEquals(n, node)
                    && !string.IsNullOrEmpty(n.ExitCountry)
                    && !string.Equals(n.ExitCountry, claimedRegion, StringComparison.OrdinalIgnoreCase)).ToList();
                if (bad.Count > 0)
                {
                    group = group.Where(n => !bad.Contains(n)).ToList();
                    MultiNodeCount = group.Count;
                    LogBus.App($"多节点并发：已排除 {bad.Count} 个「标记与实际出口不符」的节点（{string.Join("、", bad.Select(b => b.Name))}）", "Warning");
                }
            }
        }

        if (group.Count > 1)
            await FillGroupDelaysAsync(group);

        //   只按延迟加权时，池里「延迟好看但没速度」的节点会拿到大量份额，
        //   浏览时每条连接抽签，抽到它就卡。这里在多节点且加权模式时补一次吞吐探测。
        List<int>? groupSpeeds = null;
        if (group.Count > 1
            && !string.Equals(AppState.State.MultiNodeMode, "balanced", StringComparison.OrdinalIgnoreCase))
        {
            try { groupSpeeds = await GetGroupSpeedsAsync(group); }
            catch (Exception ex)
            {
                LogBus.App($"多节点并发：吞吐探测异常，本次退回按延迟加权（[{ex.GetType().Name}] {ex.Message}）", "Warning");
                groupSpeeds = null;
            }
        }

        string json;
        try
        {
            // 曾经的写法是 `if (!useSingBox && group.Count > 1) { ... else if (useTun) ... }`，
            // **TUN 分支永远进不去**：因为 useTun 为真时 useSingBox 也为真（见上面
            // `useSingBox = useTun || hy2/tuic`），外层 `!useSingBox` 直接把它挡掉了。
            // 只有单个 `proxy` 出站、没有 balancer —— 界面/日志说并发，实际单节点。
            var wantMulti = group.Count > 1 && (!useSingBox || useTun);
            if (wantMulti)
            {
                // ComputeWeights 会给它中间份额；已测过（>0）的按延迟倍数分配份额。
                // 队列顺序必须与 group 一致：BuildMulti 是按 group 的下标给 proxy{i} 建出站的。
                var delays = group.Select(n => n.RealDelay).ToList();
                var mode = string.Equals(AppState.State.MultiNodeMode, "balanced", StringComparison.OrdinalIgnoreCase)
                    ? XrayConfigBuilder.ConcurrencyMode.Balanced
                    : XrayConfigBuilder.ConcurrencyMode.Weighted;
                var modeName = mode == XrayConfigBuilder.ConcurrencyMode.Weighted ? "加权" : "均衡";
                var delayed = group.Count(n => n.RealDelay > 0);

                if (!useSingBox)
                {
                    json = XrayConfigBuilder.BuildMulti(group,
                        XrayConfigBuilder.DefaultSocksPort, XrayConfigBuilder.DefaultHttpPort,
                        AppState.State.ProxyMode, AppState.State.BypassPrivate, AppState.State.BypassCn,
                        AppState.State.BlockAds, AppState.State.AllowLan, AppState.State.CustomRules,
                        AppState.State.LogLevel, AppState.State.DnsServer,
                        AppState.State.MultiNodeStrategy, mode, delays, groupSpeeds);
                }
                else
                {
                    // TUN + 多节点：走自编译 sing-box 的 balancer 出站。
                    json = SingBoxConfigBuilder.BuildTunMulti(group, delays,
                        XrayConfigBuilder.DefaultSocksPort, XrayConfigBuilder.DefaultHttpPort,
                        AppState.State.ProxyMode, AppState.State.BypassPrivate, AppState.State.BypassCn,
                        AppState.State.BlockAds, AppState.State.AllowLan, AppState.State.CustomRules,
                        AppState.State.LogLevel, AppState.State.DnsServer,
                        AppState.State.GameDirectEnabled, AppState.GameDirectProcessList(), groupSpeeds);
                }
                var speeded = groupSpeeds?.Count(s => s > 0) ?? 0;
                LogBus.App($"多节点加速已启用：{MultiNodeRegion} 共 {group.Count} 个节点参与（策略 {AppState.State.MultiNodeStrategy} · 模式 {modeName} · 权重依据 实测吞吐 {speeded}/{group.Count}（每节点至少 1 份，延迟门槛 {delayed}/{group.Count}）· 内核 {(useSingBox ? "sing-box" : "xray")}）");
            }
            else
            {
                json = useSingBox
                    ? (useTun
                        ? SingBoxConfigBuilder.BuildTun(node, XrayConfigBuilder.DefaultSocksPort, XrayConfigBuilder.DefaultHttpPort,
                            AppState.State.ProxyMode, AppState.State.BypassPrivate, AppState.State.BypassCn,
                            AppState.State.BlockAds, AppState.State.AllowLan, AppState.State.CustomRules,
                            AppState.State.LogLevel, AppState.State.DnsServer,
                            AppState.State.GameDirectEnabled, AppState.GameDirectProcessList())
                        : SingBoxConfigBuilder.Build(node, XrayConfigBuilder.DefaultSocksPort, XrayConfigBuilder.DefaultHttpPort,
                            AppState.State.ProxyMode, AppState.State.BypassPrivate, AppState.State.BypassCn,
                            AppState.State.BlockAds, AppState.State.AllowLan, AppState.State.CustomRules,
                            AppState.State.LogLevel, AppState.State.DnsServer,
                            AppState.State.GameDirectEnabled, AppState.GameDirectProcessList()))
                    : XrayConfigBuilder.Build(node,
                        XrayConfigBuilder.DefaultSocksPort, XrayConfigBuilder.DefaultHttpPort,
                        AppState.State.ProxyMode, AppState.State.BypassPrivate, AppState.State.BypassCn,
                        AppState.State.BlockAds, AppState.State.AllowLan, AppState.State.CustomRules,
                        AppState.State.LogLevel, AppState.State.DnsServer);
            }
            CoreKind = useSingBox ? "sing-box" : "xray";
        }
        catch (Exception ex) { return (false, "配置生成失败：" + ex.Message); }

        // CN 分流清单完整性护栏：清单被换成上游精简版时，TUN 下国内域名会静默
        // 走代理（见 RuleSetIntegrity 注释）。只告警、不阻断启动。
        if (useSingBox)
        {
            try
            {
                var findings = RuleSetIntegrity.Check();
                foreach (var f in findings)
                {
                    LogBus.App(
                        f.ActualBytes < 0
                            ? $"⚠ 分流清单缺失：{f.File}（{f.Note}）"
                            : $"⚠ 分流清单体量异常：{f.File} 仅 {f.ActualBytes:N0} B（应 ≥ {f.MinimumBytes:N0} B）—— {f.Note}",
                        "Warn");
                }
                if (findings.Count > 0)
                    LogBus.App("提示：请执行 python tools\\gen-rulesets.py 重新生成全量清单，否则国内直连会退化。", "Warn");
            }
            catch { }
        }

        //   原先是 File.WriteAllText(ConfigPath, json) **整文件覆盖**。
        //   该文件写完后会被内核进程读取；覆盖面虽小，但「写一半被读」
        //   （或进程在写一半被终止）会留下**半截 JSON** → 内核启动失败且难归因。
        //   改为 tmp + rename 原子写（与本项目 state.json / subscription-cache.json 同款）。
        try
        {
            var cfgTmp = ConfigPath + ".tmp";
            File.WriteAllText(cfgTmp, json);
            File.Move(cfgTmp, ConfigPath, true);
        }
        catch (Exception ex) { return (false, "配置写入失败：" + ex.Message); }

        var exe = Path.Combine(NeoPaths.RuntimeDir, useSingBox ? "sing-box.exe" : "xray.exe");

        // 「内核已启动（PID …）」，然后 800ms 后才检查 IsRunning。
        // 若端口被别人占着，进程会秒退，于是日志出现自相矛盾的两行：
        //   「xray 内核已启动（PID 45580 · 端口 10808/10809）」
        //   「内核启动失败：本地端口被占用…」
        // 前一行是假的（进程根本没起来），却最容易被当成"启动成功"的依据。
        // 这里把检查提前：端口不空就当场失败并给出准确原因，不再打那条假日志。
        try
        {
            if (FirstBusyPort() != 0)
            {
                // 再清一次残留（可能刚被别的东西拉起），仍不空才判失败
                CleanupOrphanCores();
                await Task.Delay(300);
            }
            var busyAtConnect = FirstBusyPort();
            if (busyAtConnect != 0)
            {
                LogBus.App($"内核启动被拒：本地端口 {busyAtConnect} 仍被占用，未启动内核", "Error");
                return (false, $"内核启动失败：本地端口 {busyAtConnect} 被占用。可能有其他代理程序（或残留内核）在用，请先关闭后重试");
            }
        }
        catch { }

        //   只在本进程第一次 TUN 连接时做（见 _tunPreStartGuardDone 注释）。
        if (useTun && !_tunPreStartGuardDone)
        {
            _tunPreStartGuardDone = true;
            EnsureTunAdapterCleared();
        }

        try
        {
            _kernelTail.Clear();
            _xray = new CoreProcess(exe, NeoPaths.RuntimeDir);
            _xray.LogLine += line =>
            {
                LogBus.Core(line);
                _kernelTail.Enqueue(line);
                while (_kernelTail.Count > 60) _kernelTail.TryDequeue(out _);
                LogLine?.Invoke(line);
            };
            _xray.Exited += code => OnCoreExited(code);
            _xray.Start(ConfigPath);
        }
        catch (Exception ex)
        {
            _xray = null;
            LogBus.Exception($"内核启动失败（{CoreKind}）", ex);
            return (false, "内核启动失败：" + ex.Message);
        }

        await Task.Delay(800);
        if (_xray == null || !_xray.IsRunning)
        {
            Disconnect("内核启动失败");
            //   原先只查 10808/10085 —— 而这两个正是刚被门禁放行的，于是 10809 冲突
            var busyPort = FirstBusyPort();
            bool busy = busyPort != 0;
            var bootTail = KernelTailText();
            LogBus.App(busy
                ? $"内核启动失败：本地端口 {busyPort} 被占用（残留内核已尝试清理，请重试）"
                : "内核启动后立即退出（配置或参数错误）" + (bootTail.Length > 0 ? $"{Environment.NewLine}内核最后输出：{bootTail}" : ""), "Error");
            return (false, busy
                ? $"内核启动失败：本地端口 {busyPort} 被占用（残留内核已尝试清理，请重试）"
                : "内核启动后立即退出（配置或参数错误，详见内核日志）");
        }

        LogBus.App($"{CoreKind} 内核已启动（PID {_xray.Pid} · 端口 {XrayConfigBuilder.DefaultSocksPort}/{XrayConfigBuilder.DefaultHttpPort}）");

        // 系统代理：非 TUN 时连上就自动写入 Windows。
        //   Enable()，开关的两种状态落到同一结果，纯属误导）。因此这里不再有任何开关判断，
        //   行为与旧版一致：连上即写、断开即清。
        //   TUN 模式走虚拟网卡接管全部流量，不需要也不应该写系统代理。
        if (!useTun)
            SystemProxy.Enable($"127.0.0.1:{XrayConfigBuilder.DefaultSocksPort}");

        // 记录本次会话是否用了 TUN：断开时据此决定是否清理 neo-tun 网卡与路由
        // （TUN 打开就"关闭不掉"的修复，见 TeardownTunNetwork 注释）
        _tunSessionActive = useTun;
        //   neo-tun 建立/销毁会触发 NetworkAddressChanged，FailoverService 据此
        //   过滤掉「我们自己造成的网络变化」，避免每次连 TUN 都误报「立即复查」。
        if (useTun) FailoverService.MarkTunNetworkEvent();

        IsConnected = true;
        ConnectedNode = node;
        ConnectedAt = DateTime.Now;
        _lastCoreStartAt = DateTime.Now;   // 供「内核是否稳定运行过」判断使用
        _ = TryAutoMeasureDelayAsync(node);   // 后台补测本节点延迟，让「未测速」自愈
        var gdCount = (useSingBox && AppState.State.GameDirectEnabled) ? AppState.GameDirectProcessList().Count : 0;
        LogBus.App($"=== 会话开始 · 节点：{node.Name} · 模式：{ModeName(AppState.State.ProxyMode)} · TUN={(useTun ? "开" : "关")} · 游戏直连={(gdCount > 0 ? $"开（{gdCount} 个进程）" : "关")}" +
                   (MultiNodeCount > 1 ? $" · 多节点加速={MultiNodeRegion}×{MultiNodeCount}" : "") + " ===");
        NotifyStateChanged();
        return (true, null);
        }
        finally
        {
            IsConnecting = false;
            //   否则一次失败会把门永久占住，此后所有连接请求都被拒。
            try { SessionGate.Release(); } catch { }
            NotifyStateChanged();
        }
    }

    public static void Disconnect(string? reason = null)
    {
        bool was = IsConnected;
        var startedAt = ConnectedAt;
        IsConnected = false;
        ConnectedNode = null;
        ConnectedAt = null;
        MultiNodeCount = 1;
        MultiNodeRegion = null;

        // 用户主动断开时，取消尚未执行的内核自动重启，否则会「断开后又自己连回来」
        try { _coreRestartCts?.Cancel(); } catch { }

        try { SystemProxy.Disable(); } catch { }

        //   旧实现只调 _xray.Stop()（丢弃 WaitForExit 返回值），内核若没死透，
        //   neo-tun 网卡与 auto_route 写入的路由会留在系统里 —— 用户看到的就是
        //   「TUN 打开后关闭不掉」（流量仍被接管）。
        bool wasTun = _tunSessionActive;
        _tunSessionActive = false;
        if (wasTun) FailoverService.MarkTunNetworkEvent();
        try
        {
            if (_xray != null)
            {
                bool exited = _xray.StopAndVerify();
                if (!exited)
                {
                    LogBus.App("内核未能在超时内退出，正在按进程名兜底清理（TUN 残留会导致路由被继续接管）", "Warning");
                }
            }
        }
        catch (Exception ex) { LogBus.Exception("停止内核失败", ex); }
        _xray = null;

        //   本方法是在 **UI 线程**上被同步调用的（MainWindow.StopTimersAndDisconnect / 关窗口 /
        //   退出流程都会走到它），而 TeardownTunNetwork 内部会串行启动两条 PowerShell
        //   （Get-NetAdapter / Get-NetRoute），每条冷启动数百毫秒、最坏各等 8 秒超时。
        //   把它们放在调用线程上跑 = 界面卡住最长十几秒，用户体感就是「关闭很卡」。
        //   现改为：先同步做完必要的收尾（停进程、清系统代理），把**只影响系统网络状态**的
        //   网卡/路由清理丢到后台线程 —— 断开立刻返回，界面不卡，残留清理随后完成。
        //   注意用 try/catch 包住后台任务，避免未观察异常。
        if (wasTun) StartBackgroundTunTeardown("断开连接");

        if (was)
        {
            var dur = startedAt == null ? "" : $"（时长 {(DateTime.Now - startedAt.Value).TotalMinutes:F0} 分钟）";
            //   既无错误也无故障转移失败计数，完全无法判断是谁断的。
            //   现在把调用方传进来的原因一并落盘。
            LogBus.App($"=== 会话结束{dur}（原因：{reason ?? "未标注"}） ===");
            NotifyStateChanged();
        }
    }

    /// 抽成一个方法是因为三条路径（用户断开 / 内核意外退出 / 启动时清理上次残留）
    /// 必须走**同一套**闸门：
    ///   · <see cref="TeardownGate"/> 串行化拆除，避免两次拆除互相穿插；
    ///     否则后台 Remove-NetAdapter 可能删掉新会话刚建的 neo-tun。
    /// 本方法不阻塞调用方：拆除动作在 Task.Run 里执行。
    private static void StartBackgroundTunTeardown(string reason)
    {
        try
        {
            _lastTeardown = RunTunTeardownAsync(reason);
        }
        catch (Exception ex)
        {
            LogBus.App($"安排 TUN 后台拆除失败（{reason}）：{ex.GetType().Name} {ex.Message}", "Warning");
        }
    }

    private static Task RunTunTeardownAsync(string reason) => Task.Run(async () =>
    {
        await TeardownGate.WaitAsync().ConfigureAwait(false);
        try
        {
            LogBus.App($"TUN 拆除开始（{reason}）", "Debug");
            TeardownTunNetwork();
        }
        catch (Exception ex) { LogBus.Exception("TUN 后台拆除失败", ex); }
        finally { try { TeardownGate.Release(); } catch { } }
    });

    /// 为什么必须有这一步：TUN 模式用 `auto_route=true` + `strict_route=true`
    /// 创建 `neo-tun` 网卡并把默认路由指向它。正常退出时 sing-box 会自己撤销，
    /// 但若进程被强杀 / 卡死 / 超时未退出，这些系统状态就会残留 ——
    /// 流量继续被已经不存在的内核"接管"，表现为关不掉 TUN。
    /// 系统代理早有 `CleanupOrphanedSystemProxy` 兜底，TUN 此前**完全没有**。
    /// 清理策略（保守，只动自己命名的对象）：
    ///   ① 内核进程兜底：按名清 runtime 目录下的 sing-box（复用 CleanupOrphanCores 的路径校验）
    ///   ② 网卡：删除名为 neo-tun 的适配器（只在它确实存在时动）
    ///   ③ 路由：删除指向 172.19.0.1 的残留路由（精确匹配我们自己的地址段）
    /// 全部 try/catch 包裹：清理失败不能影响断开流程本身。
    private static void TeardownTunNetwork()
    {
        // ① 内核兜底：确认 runtime 目录下的 sing-box 都已退出
        try
        {
            foreach (var p in Process.GetProcessesByName("sing-box"))
            {
                try
                {
                    string? path = null;
                    try { path = p.MainModule?.FileName; } catch { }
                    var prefix = NeoPaths.RuntimeDir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                    // 只清属于本程序 runtime 的进程，绝不误杀用户自己的别的 sing-box
                    if (path == null || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        continue;
                    p.Kill(entireProcessTree: true);
                    if (!p.WaitForExit(5000))
                        LogBus.App($"TUN 拆除：sing-box（PID {p.Id}）未能退出，网卡/路由可能残留", "Warning");
                    else
                        LogBus.App($"TUN 拆除：已清理残留 sing-box（PID {p.Id}）", "Warning");
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
        }
        catch { }

        // ② 网卡：删除 neo-tun
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(
                "$a = Get-NetAdapter -Name 'neo-tun' -ErrorAction SilentlyContinue; " +
                "if ($a) { Disable-NetAdapter -Name 'neo-tun' -Confirm:$false -ErrorAction SilentlyContinue; " +
                "Remove-NetAdapter -Name 'neo-tun' -Confirm:$false -ErrorAction SilentlyContinue; 'REMOVED' } else { 'ABSENT' }");
            using var p = Process.Start(psi);
            if (p != null)
            {
                var so = p.StandardOutput.ReadToEndAsync();
                var se = p.StandardError.ReadToEndAsync();
                // 超时 8s → 3s：本方法现在跑在后台线程，但过长的等待会拖住后台任务；
                // 网卡移除是幂等操作，本次没删掉下次启动还会再清一次。
                if (Task.WaitAll(new[] { so, se }, TimeSpan.FromSeconds(3)))
                {
                    try { p.WaitForExit(2000); } catch { }
                    var outp = (so.Result ?? "").Trim();
                    if (outp.Contains("REMOVED"))
                        LogBus.App("TUN 拆除：已移除 neo-tun 虚拟网卡", "Warning");
                }
                else
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    LogBus.App("TUN 拆除：移除网卡超时，已放弃（不影响断开本身）", "Warning");
                }
            }
        }
        catch (Exception ex) { LogBus.App($"TUN 拆除：移除网卡失败（{ex.GetType().Name}）", "Warning"); }

        // ③ 路由：删除指向本程序 TUN 网关的残留路由
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(
                "$r = Get-NetRoute -ErrorAction SilentlyContinue | " +
                "Where-Object { $_.NextHop -eq '172.19.0.1' -or $_.InterfaceAlias -eq 'neo-tun' }; " +
                "if ($r) { $r | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue; 'REMOVED' } else { 'ABSENT' }");
            using var p = Process.Start(psi);
            if (p != null)
            {
                var so = p.StandardOutput.ReadToEndAsync();
                var se = p.StandardError.ReadToEndAsync();
                // 超时 8s → 3s（同网卡那处，避免拖住后台清理任务）
                if (Task.WaitAll(new[] { so, se }, TimeSpan.FromSeconds(3)))
                {
                    try { p.WaitForExit(2000); } catch { }
                    if ((so.Result ?? "").Contains("REMOVED"))
                        LogBus.App("TUN 拆除：已清理 neo-tun 残留路由", "Warning");
                }
                else
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                }
            }
        }
        catch (Exception ex) { LogBus.App($"TUN 拆除：清理路由失败（{ex.GetType().Name}）", "Warning"); }
    }

    /// 清理「孤儿内核」：上次 neo 被强杀/崩溃后残留的 xray / sing-box 子进程。
    /// `GetProcessesByName` 之后看「可执行文件路径前缀 == 本程序 runtime 目录」，
    /// 命中就 `Kill(entireProcessTree: true)`。缺陷在于：用户只要把**另一份**
    /// xray.exe 放进 neo 的 runtime 目录（手动拷贝、或把 neo 的内核复用给别的工具），
    /// 它同样满足「路径前缀」条件，于是被连带杀掉。日志实证确实在跑这条路径：
    ///   neo-20261016.log:571/1364/1505/2323（4 条）、neo-20261017.log:438（1 条）
    /// 共 5 条「已清理残留内核进程（xray.exe · PID …）」。
    /// 现改为**两级判定**：
    ///   ① 定向清理（首选）：若 `_xray` 仍记录着自己那个子进程的 PID，直接按 PID 清。
    ///      这是 100% 无歧义的 —— 我们杀的就是自己启动的那个进程。
    ///   ② 名字匹配（兜底）：仅在 ① 拿不到 PID 时才逐个核对**二次确认**：
    ///      要么父进程就是本程序（neo 的子进程），要么命令行里含本程序生成的配置路径
    ///     （`config.json` = 主会话配置；`neo-realtest*.json` = 延迟测试用的临时配置）。
    /// 三条硬性安全约束（缺一不可）：
    ///   · 「必须位于本程序 runtime 目录下」这条原保护**原样保留**，取不到路径一律跳过；
    ///   · **fail-open**：父进程/命令行任一取不到 → **跳过该进程**，绝不退回「仅凭名字就杀」。
    ///     宁可漏清残留，也不能误杀别人的进程（与下方 TeardownTunNetwork 同一取向）；
    ///   · P/Invoke 全部 try/catch：受保护进程（反作弊/系统进程）上必然失败，
    ///     失败走 fail-open，且**不写日志**（本方法是高频路径，避免刷屏）。
    public static void CleanupOrphanCores()
    {
        // ① 定向清理：优先清「我们自己记录着 PID」的那个子进程。
        //    _xray 还活着（IsRunning）说明它就是当前会话的活跃内核，不属于「孤儿」，
        //    因此这里只在它已不在运行、却仍能报出有效 PID 时才动手 —— 那正是
        //    「进程没死透但句柄还在」的场景，按 PID 杀掉最准确。
        try
        {
            var core = _xray;
            if (core != null && !core.IsRunning)
            {
                int pid = core.Pid;
                if (pid > 0)
                {
                    try
                    {
                        using var target = Process.GetProcessById(pid);
                        // 再确认一次可执行路径仍在 runtime 目录下：PID 可能已被系统复用，
                        // 复用后的新进程未必还是我们的内核。
                        string? tpath = null;
                        try { tpath = target.MainModule?.FileName; } catch { }
                        if (tpath != null && IsUnderRuntimeDir(tpath))
                        {
                            target.Kill(entireProcessTree: true);
                            target.WaitForExit(3000);
                            LogBus.App($"已按记录的 PID 清理残留内核进程（{CoreKind}.exe · PID {pid}）", "Warning");
                        }
                    }
                    catch { }   // 进程已退出 / 拒绝访问：正常，忽略
                }
            }
        }
        catch { }

        // ② 名字匹配兜底（带二次确认）
        foreach (var name in new[] { "xray", "sing-box" })
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); } catch { continue; }
            foreach (var p in procs)
            {
                try
                {
                    string? path = null;
                    try { path = p.MainModule?.FileName; } catch { }

                    // 保护线之一：必须在本程序 runtime 目录下（原样保留，不削弱）
                    if (path == null || !IsUnderRuntimeDir(path))
                        continue;

                    // 已经按 ① 清掉的不再重复处理
                    if (_xray != null && !_xray.IsRunning && p.Id == _xray.Pid) continue;

                    // 保护线之二：二次确认。父进程是 neo，或命令行含本程序生成的配置路径。
                    // fail-open：两项都取不到就跳过，绝不「凭名字杀」。
                    if (!IsConfirmedOwnCore(p.Id))
                        continue;

                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(3000);
                    LogBus.App($"已清理残留内核进程（{name}.exe · PID {p.Id}）", "Warning");
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
        }
        //   CleanupOrphanedTun 内部把「检查 + 必要时拆除」整体丢进 Task.Run（不阻塞启动），
        //   并在方法内**同步**登记 _lastTeardown —— 于是紧随其后的 ConnectAsync
        try { CleanupOrphanedTun(); } catch { }
    }

    /// 判据刻意保守（只在「确定是自己留下的」时才动）：
    ///   ① 系统里存在名为 neo-tun 的适配器；
    ///   ② runtime 目录下没有正在运行的 sing-box 进程（有 = 当前/新会话正在用，绝不能拆）。
    /// 任一不满足就什么都不做。检查与拆除整体放进 Task.Run（不阻塞启动），
    /// 并**同步**登记到 _lastTeardown：这样紧随其后的 ConnectAsync 会等它做完再建新会话，
    public static void CleanupOrphanedTun()
    {
        try
        {
            // 已有拆除任务在跑（上一次断开的后台拆除）→ 不重复入队，
            // 避免覆盖 _lastTeardown 让 ConnectAsync 等错任务。
            if (!_lastTeardown.IsCompleted) return;

            _lastTeardown = Task.Run(async () =>
            {
                try
                {
                    if (!HasTunAdapter()) return;
                    if (HasRunningRuntimeSingBox())
                    {
                        LogBus.App("检测到 neo-tun 网卡，但 runtime 下仍有运行中的 sing-box —— 跳过 TUN 残留清理", "Debug");
                        return;
                    }
                    LogBus.App("检测到上次异常退出残留的 neo-tun 网卡（runtime 下无运行中的 sing-box），正在后台清理 TUN 网络状态", "Warning");
                    await RunTunTeardownAsync("启动时清理残留").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogBus.App($"TUN 残留清理失败（不影响启动）：{ex.GetType().Name} {ex.Message}", "Warning");
                }
            });
        }
        catch (Exception ex)
        {
            LogBus.App($"TUN 残留清理检查失败（不影响启动）：{ex.GetType().Name} {ex.Message}", "Debug");
        }
    }

    /// TUN 会话启动前的兜底：确保没有残留的 neo-tun 网卡。
    /// 残留网卡/路由是「我们能主动消掉的变量」之一，所以在启动内核前同步清一次并等它消失（最多 4 秒）。
    /// 失败不阻断连接（宁可可能残一次，也不能连不上）。
    private static void EnsureTunAdapterCleared()
    {
        try
        {
            if (!HasTunAdapter()) return;
            LogBus.App("TUN 启动前发现残留 neo-tun 网卡 —— 先清除再启动内核", "Warning");
            TeardownTunNetwork();      // 内部同步执行 Remove-NetAdapter 并等待（最多 3s）
            Thread.Sleep(200);
            LogBus.App("TUN 启动前残留网卡清理已执行", "Debug");
        }
        catch (Exception ex)
        {
            LogBus.App($"TUN 启动前清理残留网卡异常：{ex.GetType().Name} {ex.Message}", "Warning");
        }
    }

    /// <summary>内核最后若干行输出（单行化），用于崩溃日志。</summary>
    private static string KernelTailText()
    {
        try
        {
            var lines = _kernelTail.ToArray();
            if (lines.Length == 0) return "";
            static string Cut(string s) => s.Length > 240 ? s[..240] + "…" : s;
            return string.Join(" | ", lines.TakeLast(8).Select(Cut)).Replace("\r", " ").Replace("\n", " ");
        }
        catch { return ""; }
    }

    /// <summary>系统里是否还有名为 neo-tun 的虚拟网卡（与 FailoverService 的判定口径保持一致）。</summary>
    private static bool HasTunAdapter()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (string.Equals(ni.Name, "neo-tun", StringComparison.OrdinalIgnoreCase))
                    return true;
                // 网卡描述名也认：部分系统上 Name 是「本地连接* 12」，Description 才带 neo-tun。
                if ((ni.Description ?? "").Contains("neo-tun", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }
        return false;
    }

    /// runtime 目录下是否有正在运行的 sing-box 进程。
    /// 取不到路径时按「有」处理（fail-open）：宁可漏清一次残留，也不能误杀正在工作的内核。
    private static bool HasRunningRuntimeSingBox()
    {
        Process[] procs;
        try { procs = Process.GetProcessesByName("sing-box"); }
        catch { return true; }
        try
        {
            foreach (var p in procs)
            {
                try
                {
                    string? path = null;
                    try { path = p.MainModule?.FileName; } catch { }
                    if (path == null || IsUnderRuntimeDir(path)) return true;
                }
                catch { return true; }
                finally { try { p.Dispose(); } catch { } }
            }
        }
        catch { return true; }
        return false;
    }

    /// <summary>本程序 runtime 目录下的路径判定（不区分大小写，带目录分隔符前缀）。</summary>
    private static bool IsUnderRuntimeDir(string path)
    {
        try
        {
            var prefix = NeoPaths.RuntimeDir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// 二次确认「这个同名进程确实是我们自己的内核」。两个条件满足其一即可：
    ///   ① 父进程是本程序（neo）：neo 通过 CoreProcess 启动的内核，父进程必为 neo；
    ///   ② 命令行里含本程序生成的配置路径：`config.json`（主会话配置），
    ///      或 `neo-realtest*.json`（RealDelayTester 的临时测速配置，落在 %TEMP%）。
    /// 全部 try/catch + fail-open：受保护进程上取父进程/命令行会失败，
    /// 不写日志：本方法是高频路径，且「取不到」是受保护进程的常态，不是异常。
    private static bool IsConfirmedOwnCore(int pid)
    {
        // ① 父进程是 neo
        try
        {
            if (TryGetParentProcessId(pid, out int parentPid) && parentPid > 0)
            {
                // 父进程 PID 直接就是本进程 → 铁证
                if (parentPid == Environment.ProcessId) return true;
                // 否则看父进程名（父进程可能是一条提权/代理链路，名字仍是 neo）
                try
                {
                    using var parent = Process.GetProcessById(parentPid);
                    var parentName = parent.ProcessName;
                    if (string.Equals(parentName, "neo", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { }
            }
        }
        catch { }

        // ② 命令行里含本程序生成的配置路径
        // 为什么只认「配置路径」而不是「exe 路径里含 neo」：后者**会重新引入误杀** ——
        // 用户把另一份 xray.exe 拷进 neo 的 runtime 目录后，它的命令行里天然就含
        // 真正的判别特征是**冷启动配置**：neo 启动内核时命令行固定是
        //   "<runtime>\xray.exe" run -c "<DataDir>\config.json"
        // 第三方工具即便复用同一份 exe，用的也是它自己的配置，不会命中下面两条。
        try
        {
            var cmd = TryGetCommandLine(pid);
            if (!string.IsNullOrEmpty(cmd))
            {
                // 主会话配置：<DataDir>\config.json —— 这是 neo 自己的配置，铁证。
                // 顺带覆盖「neo 上次崩溃后留下的孤儿」：父进程虽已消失，
                // 但它的命令行仍指向这份配置，所以仍能被准确识别（不会漏清）。
                if (cmd.Contains(ConfigPath, StringComparison.OrdinalIgnoreCase)) return true;

                // 延迟测试临时配置：%TEMP%\neo-realtest*.json（RealDelayTester 生成，
                // 见 RealDelayTester.cs 的 neo-realtest-{port}-{guid}.json / batch 版本）
                if (cmd.Contains("neo-realtest", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { }

        return false;   
    }

    // ---- 用于「二次确认」的自包含 Win32 探查（刻意不引 System.Management）----
    // 为什么自己写 P/Invoke 而不用 `System.Management`（WMI 查 ParentProcessId/CommandLine）：
    //   ① 项目**没有**引用 System.Management（已核 project.assets.json → 0 命中），
    //      为一个清理兜底去加 NuGet 依赖不划算；
    //   ② WMI 首次调用会加载整个 WMI 栈（明显更慢），而本方法是连接路径上的高频调用；
    //   ③ WMI 在受保护进程上同样会失败，换不来更可靠的结果。
    // 两个 API 的分工：
    //   · Toolhelp32 快照：一次枚举拿到全系统「PID → 父 PID」，无需逐个进程开句柄；
    //   · NtQueryInformationProcess(60)：取命令行。60 = ProcessCommandLineInformation
    //     （Win8.1+）。它把命令行**就地写进我们提供的缓冲区**（该缓冲区在本进程地址空间内），
    //     因此不需要对目标进程 ReadProcessMemory，也不需要 PROCESS_VM_READ 权限。

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr hProcess, int infoClass, IntPtr info, int infoLength, out int returnLength);

    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const int ProcessCommandLineInformation = 60;

    /// <summary>取指定进程的父进程 PID；失败返回 false（调用方按 fail-open 处理）。</summary>
    private static bool TryGetParentProcessId(int pid, out int parentPid)
    {
        parentPid = 0;
        IntPtr snap = IntPtr.Zero;
        try
        {
            snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return false;

            var e = new PROCESSENTRY32W();
            e.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>();

            if (!Process32FirstW(snap, ref e)) return false;
            do
            {
                if ((int)e.th32ProcessID == pid)
                {
                    parentPid = (int)e.th32ParentProcessID;
                    return true;
                }
                // 每次循环都要重置 dwSize：API 在某些实现里会改写该字段。
                e.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>();
            } while (Process32NextW(snap, ref e));
            return false;
        }
        catch { return false; }
        finally
        {
            // 句柄绝不泄漏
            if (snap != IntPtr.Zero && snap != new IntPtr(-1))
            {
                try { CloseHandle(snap); } catch { }
            }
        }
    }

    /// <summary>取指定进程的命令行；失败返回 null（调用方按 fail-open 处理）。</summary>
    private static string? TryGetCommandLine(int pid)
    {
        IntPtr buf = IntPtr.Zero;
        try
        {
            using var p = Process.GetProcessById(pid);
            IntPtr h = p.Handle;

            // 第一次：只探所需长度（预期返回 STATUS_INFO_LENGTH_MISMATCH）
            int need = 0;
            int st = NtQueryInformationProcess(h, ProcessCommandLineInformation,
                IntPtr.Zero, 0, out need);
            if (need <= 0) return null;

            buf = Marshal.AllocHGlobal(need);
            int got = 0;
            st = NtQueryInformationProcess(h, ProcessCommandLineInformation, buf, need, out got);
            if (st != 0) return null;   // 受保护进程等：失败即返回 null（上层 fail-open）

            var us = Marshal.PtrToStructure<UNICODE_STRING>(buf);
            if (us.Buffer == IntPtr.Zero || us.Length == 0) return null;
            return Marshal.PtrToStringUni(us.Buffer, us.Length / 2);
        }
        catch { return null; }
        finally
        {
            if (buf != IntPtr.Zero) { try { Marshal.FreeHGlobal(buf); } catch { } }
        }
    }

    /// 启动时清理「孤儿系统代理」：上次 neo 被强杀/崩溃时 Disable() 没执行，
    /// 注册表残留指向本机端口的代理设置但已无人监听 → 所有程序断网。
    /// 只在「确实是 neo 端口」且「端口无人监听」时才清，避免误伤第三方代理（如 v2rayN 同端口）。
    public static void CleanupOrphanedSystemProxy()
    {
        try
        {
            if (SystemProxy.CleanupOrphaned(XrayConfigBuilder.DefaultSocksPort))
            {
                LogBus.App("检测到上次异常退出残留的系统代理（端口无监听），已自动清理恢复网络", "Warning");
            }
        }
        catch { }
    }

    private static async Task<bool> WaitPortsFreeAsync()
    {
        for (int i = 0; i < 10; i++)
        {
            if (FirstBusyPort() == 0) return true;   
            await Task.Delay(200);
        }
        return FirstBusyPort() == 0;
    }

    private static bool PortFree(int port)
    {
        try
        {
            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            l.Start();
            l.Stop();
            return true;
        }
        catch { return false; }
    }

    /// 门禁此前只查 10808(socks) 与 10085(stats)，而内核实际还会绑定
    /// 10809(http 入站) 与 10086（sing-box 的 clash_api external_controller）。
    /// 10809 被第三方占用时：门禁放行 → 内核启动即退 → 事后诊断重查的仍是
    /// **已放行的** 10808/10085 → busy=false → 报「配置或参数错误」，
    /// 把**端口冲突误报成配置错误**，用户排查方向被带偏。
    ///   ERROR 内核启动后立即退出（配置或参数错误，详见上方内核日志）
    private static (int Port, bool Busy)[] CheckAllBoundPorts() => new[]
    {
        (XrayConfigBuilder.DefaultSocksPort, !PortFree(XrayConfigBuilder.DefaultSocksPort)),
        (XrayConfigBuilder.DefaultHttpPort, !PortFree(XrayConfigBuilder.DefaultHttpPort)),
        (XrayConfigBuilder.StatsApiPort, !PortFree(XrayConfigBuilder.StatsApiPort)),
        (SingBoxConfigBuilder.ClashApiPort, !PortFree(SingBoxConfigBuilder.ClashApiPort)),
    };

    /// <summary>返回第一个被占用的端口号；全空闲返回 0。</summary>
    private static int FirstBusyPort()
    {
        foreach (var (port, busy) in CheckAllBoundPorts()) if (busy) return port;
        return 0;
    }

    public static async Task ReloadForSettingsAsync()
    {
        if (IsConnected && ConnectedNode is { } node)
        {
            LogBus.App("配置已更新 — 正在重载内核…");
            try
            {
                var (ok, err) = await ConnectAsync(node, force: true);
                //   用户改完设置后内核没重载成功、连接已断，界面却毫无提示。
                if (!ok)
                    RaiseSettingsReloadFailed($"配置重载失败：{err ?? "未知原因"}");
            }
            catch (Exception ex)
            {
                RaiseSettingsReloadFailed($"配置重载异常：[{ex.GetType().Name}] {ex.Message}");
            }
        }
    }

    /// <see cref="SettingsReloadFailed"/>。事件订阅者异常单独吞掉：重载流程已经失败，
    /// 不能因为页面弹提示出错再抛一次。
    private static void RaiseSettingsReloadFailed(string message)
    {
        LogBus.App(message, "Error");
        if (App.IsShuttingDown) return;

        void Raise()
        {
            try { SettingsReloadFailed?.Invoke(message); }
            catch (Exception ex)
            {
                LogBus.App($"设置重载失败通知回调异常（已忽略）：[{ex.GetType().Name}] {ex.Message}", "Warning");
            }
        }

        var dq = UiDispatcher;
        if (dq == null || dq.HasThreadAccess) { Raise(); return; }
        try { dq.TryEnqueue(Raise); } catch { Raise(); }
    }

    private static void OnCoreExited(int code)
    {
        if (!IsConnected) return;
        var node = ConnectedNode;
        IsConnected = false;
        ConnectedNode = null;
        //   导致「会话结束」日志里的时长永远为空（这条是内核退出路径的既有小缺陷）。
        var endedAt = ConnectedAt;
        ConnectedAt = null;
        try { SystemProxy.Disable(); } catch { }

        //   若不在这里拆除，neo-tun 网卡与 auto_route 写入的路由会继续把流量接走。
        //   复用 Disconnect 的后台拆除路径（TeardownGate + _lastTeardown），不复制逻辑。
        if (_tunSessionActive)
        {
            _tunSessionActive = false;
            FailoverService.MarkTunNetworkEvent();
            LogBus.App("内核意外退出时 TUN 会话仍为活动状态 —— 正在后台拆除 neo-tun 网卡与路由", "Warning");
            StartBackgroundTunTeardown("内核意外退出");
        }

        _xray = null;
        var crashTail = KernelTailText();
        LogBus.App($"内核意外退出（exit code {code}）— 已自动断开并清理系统代理"
                   + (crashTail.Length > 0 ? $"{Environment.NewLine}内核最后输出：{crashTail}" : ""), "Error");

        //   所以「=== 会话结束」那行日志根本不会打印 —— 内核自己退出时，
        //   那次 18.6 秒瞬断「查不出是谁断的」的原因之一。这里补上同格式的收尾记录
        //   （含时长与原因），使三条断开路径（用户/故障转移/内核退出）在日志里可区分。
        var durCore = endedAt == null ? "" : $"（时长 {(DateTime.Now - endedAt.Value).TotalMinutes:F0} 分钟）";
        LogBus.App($"=== 会话结束{durCore}（原因：内核意外退出） ===");

        // 必须先把自动重启安排出去：本方法运行在 Process.Exited 的线程池线程上，
        // 而 StateChanged 的订阅者里有直接操作 XAML 的（MainWindow.UpdateConnUi），
        // 跨线程访问 UI 会抛异常，且该异常会被 CoreProcess 的 try/catch 静默吞掉——
        if (node != null) _ = TryAutoRestartCoreAsync(node);

        NotifyStateChanged();
    }

    /// 状态变更通知。订阅者包含直接操作 XAML 的 UI 回调，而本类多处从后台线程
    /// （如 Process.Exited）调用它 —— 跨线程碰 UI 会抛异常，且该异常会被
    /// 因此这里：① 必要时 marshal 到 UI 线程；② 每个订阅者单独 try/catch，互不牵连；③ 失败留痕不再静默。
    public static Microsoft.UI.Dispatching.DispatcherQueue? UiDispatcher;

    private static void NotifyStateChanged()
    {
        // 退出中：直接放弃。此时 DispatcherQueue 正在销毁，投递会换来
        // RPC_E_WRONG_THREAD 的 fail-fast（不可捕获），而不是一个能忽略的异常。
        if (App.IsShuttingDown) return;

        var dq = UiDispatcher;
        if (dq == null || dq.HasThreadAccess)
        {
            InvokeStateChanged();
            return;
        }
        try { dq.TryEnqueue(InvokeStateChanged); }
        catch { InvokeStateChanged(); }
    }

    private static void InvokeStateChanged()
    {
        var handlers = StateChanged;
        if (handlers == null) return;
        foreach (var h in handlers.GetInvocationList())
        {
            try { ((Action)h)(); }
            catch (Exception ex)
            {
                LogBus.App($"状态通知回调异常（已忽略，不影响连接）：[{ex.GetType().Name}] {ex.Message}", "Warning");
            }
        }
    }

    /// 把 NodesChanged 通知 marshal 回 UI 线程。与 NotifyStateChanged 同理：
    /// 订阅者会直接操作 XAML，后台线程触发会抛异常。
    private static void NotifyNodesChangedSafe()
    {
        if (App.IsShuttingDown) return;
        var dq = UiDispatcher;
        if (dq == null || dq.HasThreadAccess)
        {
            AppState.NotifyNodesChanged();
            return;
        }
        try { dq.TryEnqueue(AppState.NotifyNodesChanged); }
        catch { }
    }

    // ---- 内核崩溃自动重启 ----
    // 只处理「内核进程本身挂了」，与节点失联（由 FailoverService 处理）是两回事：
    // 内核一挂 neo 就处于断开态，故障转移服务此时不工作，只能靠这里兜底。
    private static int _coreRestartAttempts;
    private const int MaxCoreRestartAttempts = 3;
    /// 「一启动就崩」的独立计数：这类情况更可能是环境瞬时问题，给少量重试机会但不无限重试。
    private static int _earlyCrashCount;
    private const int MaxEarlyCrashAttempts = 2;
    private static DateTime _lastCoreStartAt = DateTime.MinValue;
    private static bool _coreRestartInProgress;
    private static CancellationTokenSource? _coreRestartCts;

    /// 内核稳定运行超过这个时长后再次崩溃，视为「新事件」，重置重试计数。
    private const int StableUptimeSeconds = 60;

    private static async Task TryAutoRestartCoreAsync(NodeItem node)
    {
        if (_coreRestartInProgress) return;
        _coreRestartInProgress = true;
        var cts = new CancellationTokenSource();
        _coreRestartCts = cts;
        try
        {
            var uptime = DateTime.Now - _lastCoreStartAt;

            // 启动后立刻崩溃 = 大概率配置或环境问题，重试意义不大。
            // 但不能永久放弃：端口被瞬时占用、内核文件正被替换等都会造成「一启动就挂」，
            // 过后往往自愈。所以这里允许有限次尝试（比正常崩溃更少），超过才彻底停手。
            if (uptime.TotalSeconds < 10)
            {
                _earlyCrashCount++;
                if (_earlyCrashCount > MaxEarlyCrashAttempts)
                {
                    //   「已停止自动重启，请手动重连或检查内核日志」整句。
                    //   「判定为配置或环境问题，不自动重启（请检查内核日志）」。
                    //   三条路径本来就有 Error 日志，缺的只是**一致的措辞**；
                    //   再补一条只会重复刷屏（当年正是「反复刷日志」引发的整改）。
                    LogBus.App($"内核连续 {_earlyCrashCount} 次启动即崩溃，判定为配置或环境问题：已停止自动重启，请手动重连或检查内核日志", "Error");
                    return;
                }
                _coreRestartAttempts++;
                if (_coreRestartAttempts > MaxCoreRestartAttempts)
                {
                    LogBus.App($"内核已连续异常退出 {_coreRestartAttempts} 次：已停止自动重启，请手动重连或检查内核日志", "Error");
                    return;
                }
                var retryDelay = TimeSpan.FromSeconds(5 * _earlyCrashCount);
                LogBus.App($"内核启动后立即崩溃，{retryDelay.TotalSeconds:F0} 秒后重试（第 {_earlyCrashCount}/{MaxEarlyCrashAttempts} 次）", "Warning");

                await Task.Delay(retryDelay, cts.Token);
                if (cts.IsCancellationRequested || IsConnected) return;
                if (ReferenceEquals(_coreRestartCts, cts)) _coreRestartCts = null;

                var (rok, rerr) = await ConnectAsync(node);
                if (rok) { _earlyCrashCount = 0; LogBus.App($"内核已自动重启并恢复连接（{node.Name}）", "Warning"); }
                else LogBus.App($"内核自动重启失败：{rerr}", "Error");
                return;
            }

            // 稳定运行过再崩 → 是新事件，重新计数
            if (uptime.TotalSeconds > StableUptimeSeconds)
            {
                _coreRestartAttempts = 0;
                _earlyCrashCount = 0;
            }

            if (_coreRestartAttempts >= MaxCoreRestartAttempts)
            {
                // 与上面两条放弃路径同一句文案（参见 earlyCrash 分支的注释）：
                LogBus.App($"内核已连续异常退出 {_coreRestartAttempts} 次：已停止自动重启，请手动重连或检查内核日志", "Error");
                return;
            }

            _coreRestartAttempts++;
            var delay = TimeSpan.FromSeconds(_coreRestartAttempts switch { 1 => 3, 2 => 10, _ => 30 });
            LogBus.App($"内核异常退出，{delay.TotalSeconds:F0} 秒后自动重启（第 {_coreRestartAttempts}/{MaxCoreRestartAttempts} 次）", "Warning");

            await Task.Delay(delay, cts.Token);
            if (cts.IsCancellationRequested || IsConnected) return;   // 用户已手动连上或已断开

            // 交接给 ConnectAsync：它内部会调 Disconnect()，若 _coreRestartCts 仍指向本 cts 会被误取消
            if (ReferenceEquals(_coreRestartCts, cts)) _coreRestartCts = null;

            var (ok, err) = await ConnectAsync(node);
            if (ok) LogBus.App($"内核已自动重启并恢复连接（{node.Name}）", "Warning");
            else LogBus.App($"内核自动重启失败：{err}", "Error");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogBus.App($"内核自动重启异常：[{ex.GetType().Name}] {ex.Message}", "Error");
        }
        finally
        {
            if (ReferenceEquals(_coreRestartCts, cts)) _coreRestartCts = null;
            _coreRestartInProgress = false;
        }
    }

    /// 为什么不能用 TryAutoMeasureDelayAsync 代替：那个方法测的是「已连接代理」，
    /// 而多节点会话里经 balancer 的流量落到哪个成员是随机的 —— 无法定向测某个节点，
    /// 所以它天然只能测到「当前节点」这一个。要测全组必须**每节点起独立入口**，
    /// 即 RealDelayTester.RealPingBatchAsync 那套（单实例 + 每节点独立 socks 端口）。
    /// 安全性：
    ///  · 用随机空闲端口，不占 10085 统计端口、不占 10808/10809 ⇒ 与已连接会话不冲突；
    ///  · 只测 RealDelay == 0 的节点：已有结果的跳过，同会话内重复连接几乎免费；
    ///  · 失败（-1）**不写回**节点 —— 保持 0「未测速」而不是假超时（与既有约定一致）；
    ///  · 整体限时，避免把连接流程拖长。
    private static async Task FillGroupDelaysAsync(IReadOnlyList<NodeItem> group)
    {
        var pending = group.Where(n => n.RealDelay == 0).ToList();
        if (pending.Count == 0) return;

        // 已有一定数量测速结果时，剩余未测的通常只是少数；反之冷启动全未测。
        LogBus.App($"多节点并发：正在补测 {pending.Count} 个节点的延迟（用于加权份额计算）…", "Debug");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            var map = await RealDelayTester.RealPingBatchAsync(pending, NeoPaths.RuntimeDir, ct: cts.Token);
            var got = 0;
            foreach (var kv in map)
            {
                // 只接受 >0 的有效值：-1 是「测到超时」，0 是「没测到」，
                // 两者都不该写成延迟结果（写了会让「未测速」变成假的负数/零）。
                if (kv.Value > 0)
                {
                    kv.Key.RealDelay = kv.Value;
                    DelayCache.Set(kv.Key, kv.Value);
                    got++;
                }
            }
            LogBus.App($"多节点并发：延迟补测完成 {got}/{pending.Count} 个成功"
                       + (got < pending.Count ? "（未成功的保持「未测速」，不影响连接）" : ""));
        }
        catch (OperationCanceledException)
        {
            LogBus.App($"多节点并发：延迟补测超时（{pending.Count} 个未完成）—— 本次按已有数据计算份额，不影响连接", "Warning");
        }
        catch (Exception ex)
        {
            LogBus.App($"多节点并发：延迟补测异常，不影响连接（[{ex.GetType().Name}] {ex.Message}）", "Debug");
        }
    }

    /// 节点吞吐缓存（Kbps）。键 = AppState.NodeKey（稳定标识）。
    ///   连接流程会**同步等待**吞吐探测（8 节点 × 256KB × 2 次，最坏十几秒），
    ///   而进程内缓存一重启就没了 —— 每次重启后的第一次连接都要重付一次，
    ///   用户体感就是「连接要等 15~25 秒」。所以这里把结果按 NodeKey 落盘（TTL 6 小时），
    ///   重启后直接复用；启动自动测速的末尾会预热「上次节点所在组」，把首次连接也变快。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Kbps, DateTime At)> _speedCache = new();
    private static readonly TimeSpan SpeedCacheTtl = TimeSpan.FromHours(6);
    private static readonly object _speedCacheDiskLock = new();
    private static bool _speedCacheLoaded;

    private sealed class SpeedEntry
    {
        public int Kbps { get; set; }
        public long AtUnix { get; set; }
    }

    private static string SpeedCachePath => Path.Combine(NeoPaths.DataDir, "speed-cache.json");

    private static void EnsureSpeedCacheLoaded()
    {
        if (_speedCacheLoaded) return;
        lock (_speedCacheDiskLock)
        {
            if (_speedCacheLoaded) return;
            _speedCacheLoaded = true;
            try
            {
                if (!File.Exists(SpeedCachePath)) return;
                var entries = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, SpeedEntry>>(
                    File.ReadAllText(SpeedCachePath));
                if (entries == null) return;
                var now = DateTime.UtcNow;
                foreach (var kv in entries)
                {
                    if (kv.Value == null || kv.Value.Kbps <= 0) continue;
                    var at = DateTimeOffset.FromUnixTimeSeconds(kv.Value.AtUnix).UtcDateTime;
                    if (now - at < SpeedCacheTtl) _speedCache[kv.Key] = (kv.Value.Kbps, at);
                }
            }
            catch { }
        }
    }

    private static void SaveSpeedCache()
    {
        try
        {
            lock (_speedCacheDiskLock)
            {
                var now = DateTime.UtcNow;
                var entries = new Dictionary<string, SpeedEntry>();
                foreach (var kv in _speedCache)
                {
                    if (now - kv.Value.At >= SpeedCacheTtl) continue;
                    entries[kv.Key] = new SpeedEntry
                    {
                        Kbps = kv.Value.Kbps,
                        AtUnix = new DateTimeOffset(kv.Value.At).ToUnixTimeSeconds(),
                    };
                }
                var tmp = SpeedCachePath + ".tmp";
                File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(entries));
                File.Move(tmp, SpeedCachePath, overwrite: true);
            }
        }
        catch { }
    }

    /// 缓存命中判定：内存/落盘共用同一个 6 小时 TTL（落盘加载时已按 TTL 过滤）。
    private static bool TryGetCachedSpeed(NodeItem node, out int kbps)
    {
        kbps = 0;
        EnsureSpeedCacheLoaded();
        if (!_speedCache.TryGetValue(AppState.NodeKey(node), out var cached)) return false;
        if (DateTime.UtcNow - cached.At >= SpeedCacheTtl) return false;
        kbps = cached.Kbps;
        return true;
    }

    /// 会拿到大量份额，浏览时每条连接抽签，抽到它就卡（Steam/推特慢的直接原因，
    /// 做法：RealDelayTester.MeasureSpeedBatchAsync（临时内核 + 每节点独立 socks 端口，
    /// 每节点下 256KB 折算 Kbps）。返回顺序与 group 一致；测不出 = 0（ComputeWeights 只给最低 1 份）。
    private static async Task<List<int>> GetGroupSpeedsAsync(IReadOnlyList<NodeItem> group)
    {
        EnsureSpeedCacheLoaded();
        var speeds = new int[group.Count];
        var missing = new List<int>();
        for (var i = 0; i < group.Count; i++)
        {
            if (TryGetCachedSpeed(group[i], out var cached))
            {
                speeds[i] = cached;
            }
            else
            {
                missing.Add(i);
            }
        }

        if (missing.Count == 0) return speeds.ToList();

        LogBus.App($"多节点并发：正在探测 {missing.Count} 个节点的真实吞吐（权重依据）…", "Debug");
        // 预算随节点数增长（10s + 1.2s/节点，上限 30s）：8 节点约 20s、19 节点约 30s。
        // 之前写死 15s，19 节点首次连接时探测会被取消、权重退化成「每节点 1 份」。
        // 超预算的部分本次按延迟加权，下次启动预热会补上。
        var probeBudget = TimeSpan.FromSeconds(Math.Min(30, 10 + missing.Count * 1.2));
        using var cts = new CancellationTokenSource(probeBudget);
        var map = await RealDelayTester.MeasureSpeedBatchAsync(
            missing.Select(i => group[i]).ToList(), NeoPaths.RuntimeDir, ct: cts.Token);

        var ok = 0;
        foreach (var kv in map)
        {
            var idx = missing.FirstOrDefault(i => ReferenceEquals(group[i], kv.Key), -1);
            if (idx < 0) continue;
            speeds[idx] = kv.Value;
            if (kv.Value > 0)
            {
                _speedCache[AppState.NodeKey(kv.Key)] = (kv.Value, DateTime.UtcNow);
                ok++;
            }
        }
        if (ok > 0) SaveSpeedCache();
        LogBus.App($"多节点并发：吞吐探测完成 {ok}/{missing.Count} 个成功"
                   + (ok < missing.Count ? "（测不出速度的节点只拿最低份额）" : ""));
        return speeds.ToList();
    }

    /// 启动预热：为「上次使用的节点」所在的多节点组提前测吞吐并落盘，
    /// 用户点连接时直接命中缓存，不必在连接流程里同步等探测。
    /// 由 StartupSpeedTest 在「启动自动测速」完成后调用（开关关闭时不预热）。
    public static async Task WarmSpeedCacheAsync()
    {
        try
        {
            if (!AppState.State.MultiNodeEnabled) return;
            if (string.Equals(AppState.State.MultiNodeMode, "balanced", StringComparison.OrdinalIgnoreCase)) return;
            if (IsConnected || IsConnecting) return;
            var node = AppState.CurrentNode ?? AppState.Nodes.FirstOrDefault();
            if (node == null) return;

            List<NodeItem> group;
            try
            {
                group = NodeGrouper.PickGroup(AppState.Nodes, node, Math.Clamp(AppState.State.MultiNodeMax, 2, 64));
            }
            catch { return; }
            if (group.Count < 2) return;

            EnsureSpeedCacheLoaded();
            var missing = group.Count(n => !TryGetCachedSpeed(n, out _));
            if (missing == 0) return;

            LogBus.App($"启动预热：正在测 {missing} 个节点的吞吐（让首次连接不必等待）…", "Debug");
            var sw = Stopwatch.StartNew();
            await GetGroupSpeedsAsync(group);
            LogBus.App($"启动预热完成：吞吐缓存已更新 · 耗时 {sw.Elapsed.TotalSeconds:F1}s", "Debug");
        }
        catch (Exception ex)
        {
            LogBus.App($"启动预热失败（不影响使用）：[{ex.GetType().Name}] {ex.Message}", "Debug");
        }
    }

    private static string ModeName(string mode) => mode switch
    {
        "global" => "全局",
        "direct" => "直连",
        _ => "规则",
    };

    // ---- 连接后自动补测延迟 ----
    // 用户报告的「显示未测速，但能正常使用」：延迟只在节点页手动点「真连测速」时才写入，
    // 直接连接一个从未测过的节点，Hero 区就一直显示「未测速」。
    // AutoSpeedTest 这个开关此前只定义、从未被读取（死配置），这里把它真正接上。
    // 注意：测延迟要临时另起内核进程打真实链路，不能在已连接时抢 10085 统计端口，
    // 因此 RealDelayTester 用的是独立随机端口，与当前会话不冲突。
    private static int _autoMeasureInFlight;

    /// 自动补测的重试次数。
    /// 保留 2 轮最小值取优，兼顾抖动与耗时。
    private const int AutoMeasureAttempts = 2;

    private static async Task TryAutoMeasureDelayAsync(NodeItem node)
    {
        try
        {
            if (!AppState.State.AutoSpeedTest) return;
            if (node.RealDelay != 0) return;                       // 已有结果就不重复测
            if (Interlocked.CompareExchange(ref _autoMeasureInFlight, 1, 0) != 0) return;

            try
            {
                await Task.Delay(1200);   // 让内核先站稳，避免与连接流程抢资源
                if (!IsConnected) return;
                if (node.RealDelay != 0) return;

                // 走「已连接的代理」测，而不是另起临时内核。
                // 代理方案 6/6 成功（3.81/0.67/0.54/3.61/3.58/3.74s）。
                // 另起内核那套要新建进程、等它监听随机端口、再打链路，任一步慢就误判 -1，
                // 会把「这次没测到」变成界面上的假「超时」。
                var best = await RealDelayTester.PingViaSocksAsync(
                    XrayConfigBuilder.DefaultSocksPort,
                    rounds: AutoMeasureAttempts,
                    ct: CancellationToken.None);

                if (!IsConnected) return;

                if (best > 0)
                {
                    node.RealDelay = best;
                    DelayCache.Set(node, best);
                    LogBus.App($"已自动补测节点延迟：{node.Name} → {best} ms", "Debug");
                }
                else
                {
                    // 全部尝试都没测到：**不写 -1**。
                    // -1 的语义是「确定超时」，而这里只代表「这次没测到」——
                    // 写进去会被「已有结果就不再测」的守卫永久锁死，界面显示假超时
                    // 保持 0（未测速）才是诚实的，下次连接会重试。
                    LogBus.App($"自动补测延迟未成功（{AutoMeasureAttempts} 次尝试均无响应，节点可能仍可用）：{node.Name}", "Warning");
                }

                // 通知 UI 刷新（Hero 区的「未测速」/ 节点卡片需要重绘）。
                // NodesChanged 的订阅者里有直接改 XAML 的（NodesPage.RebuildList /
                // MainWindow.UpdateSidebar），而本方法跑在后台线程，必须 marshal 回 UI 线程，
                // 否则跨线程访问 UI 抛异常（同 NotifyStateChanged 踩过的坑）。
                NotifyStateChanged();
                NotifyNodesChangedSafe();
            }
            finally
            {
                Interlocked.Exchange(ref _autoMeasureInFlight, 0);
            }
        }
        catch (Exception ex)
        {
            LogBus.App($"自动补测延迟失败（不影响连接）：[{ex.GetType().Name}] {ex.Message}", "Debug");
        }
    }

    public static int? CoreMemoryMb
    {
        get
        {
            try
            {
                if (_xray == null || !_xray.IsRunning) return null;
                using var p = Process.GetProcessById(_xray.Pid);
                return (int)(p.WorkingSet64 / 1024 / 1024);
            }
            catch { return null; }
        }
    }

    private static bool SameNode(NodeItem a, NodeItem b) =>
        a.EndPoint == b.EndPoint && a.Name == b.Name && a.Protocol == b.Protocol;

    // ---- sing-box 是否支持 balancer 出站（TUN 并发的前提）----
    private static bool? _singBoxBalancerSupport;

    /// 探测当前 sing-box 内核是否带 balancer 出站（即 neo 自编译版）。
    /// 为什么必须探测而不是「假定支持」：官方版 sing-box 没有该出站类型，
    /// 若 TUN 配置里写了 balancer，内核会以 `unknown outbound type` 直接启动失败，
    /// 结果是**整个 TUN 连不上**，比「退回单节点」严重得多。
    /// 因此这里用 `sing-box check` 跑一份最小 balancer 配置来判定能力，
    /// 不支持就退回单节点，保证 TUN 始终能用。
    /// 探测结果缓存到进程生命周期：内核 exe 不会在运行中变化（换内核会重启 neo）。
    /// 失败（超时/异常）一律按「不支持」处理 —— 保守退回单节点是安全的一侧。
    private static bool SingBoxSupportsBalancerInternal()
    {
        if (_singBoxBalancerSupport.HasValue) return _singBoxBalancerSupport.Value;
        _singBoxBalancerSupport = false;
        try
        {
            var exe = Path.Combine(NeoPaths.RuntimeDir, "sing-box.exe");
            if (!File.Exists(exe)) return false;

            var probe = Path.Combine(NeoPaths.DataDir, "singbox-balancer-probe.json");
            // 最小可校验配置：两个 socks 出站 + 一个 balancer。不需要真实可达，
            // `check` 只做配置结构与类型注册校验，不发起连接。
            const string probeJson = """
            {
              "log": {"level": "error"},
              "inbounds": [{"type":"socks","tag":"probe-in","listen":"127.0.0.1","listen_port":19999}],
              "outbounds": [
                {"type":"socks","tag":"pa","server":"127.0.0.1","server_port":1080},
                {"type":"socks","tag":"pb","server":"127.0.0.1","server_port":1081},
                {"type":"balancer","tag":"bal","outbounds":["pa","pb"],"strategy":"roundRobin","interval":"45s","idle_timeout":"30m"}
              ]
            }
            """;
            File.WriteAllText(probe, probeJson);

            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                ArgumentList = { "check", "-c", probe },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p == null) return false;

            // 两个流都要读：只读一个而另一个写满 4KB 管道缓冲会死锁（同类坑本项目已踩过）。
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!Task.WaitAll(new[] { stdout, stderr }, TimeSpan.FromSeconds(8)))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            try { p.WaitForExit(2000); } catch { }

            var supported = p.ExitCode == 0;
            _singBoxBalancerSupport = supported;
            LogBus.App(supported
                ? "sing-box 内核支持 balancer 出站（TUN 多节点并发可用）"
                : "sing-box 内核不支持 balancer 出站（TUN 将按单节点运行；如需并发请换自编译内核）",
                supported ? "Debug" : "Warning");
            return supported;
        }
        catch (Exception ex)
        {
            LogBus.App($"sing-box balancer 能力探测失败（按不支持处理）：{ex.Message}", "Debug");
            return false;
        }
    }

    /// <summary>换过内核 exe 后调用，让下次连接重新探测 balancer 能力。</summary>
    public static void ResetSingBoxCapability()
    {
        _singBoxBalancerSupport = null;
    }

    /// 对外暴露的 balancer 能力查询（设置页用来提示「是否支持 TUN 并发」）。
    /// 走同一个缓存探测，不自建一套判断，避免两处结论不一致。
    public static bool SingBoxSupportsBalancer
    {
        get
        {
            try { return SingBoxSupportsBalancerInternal(); }
            catch { return false; }
        }
    }


    private static string? _xrayVersion;

    /// 是否已经探测过（无论成功与否）。用于避免「探测失败 → 每次读取都重试」：
    /// 本属性的读取方是 MainWindow 每 2 秒一次的 UI 线程定时器，若不记忆失败，
    /// 一旦正则匹配不上（内核换了输出格式 / exe 半损坏 / 被杀软拦截），
    /// 就会**每 2 秒在 UI 线程重新起一次 xray 子进程**，界面周期性卡顿且永不停止。
    private static bool _xrayVersionProbed;

    /// 内核 exe 被替换后调用，让下一次读取重新探测版本。
    public static void ResetXrayVersion()
    {
        _xrayVersion = null;
        _xrayVersionProbed = false;
    }

    public static string? XrayVersion
    {
        get
        {
            if (_xrayVersionProbed) return _xrayVersion;
            _xrayVersionProbed = true;   // 先置位：即使下面抛异常也不反复重试
            try
            {
                var exe = Path.Combine(NeoPaths.RuntimeDir, "xray.exe");
                if (!File.Exists(exe)) return null;
                var txt = RunVersionProbe(exe, @"Xray\s+([\d.]+)");
                if (txt != null)
                {
                    _xrayVersion = txt;
                    LogBus.App($"检测到 xray {_xrayVersion}", "Debug");
                }
            }
            catch { }
            return _xrayVersion;
        }
    }

    private static string? _singBoxVersion;
    private static bool _singBoxVersionProbed;

    /// <summary>sing-box 内核 exe 被替换后调用，让下一次读取重新探测版本与 balancer 能力。
    /// 不重置的话，更新完内核界面会一直显示旧版本号，且能力判定仍停留在旧内核的结论上。</summary>
    public static void ResetSingBoxVersion()
    {
        _singBoxVersion = null;
        _singBoxVersionProbed = false;
        ResetSingBoxCapability();
    }

    public static string? SingBoxVersion
    {
        get
        {
            if (_singBoxVersionProbed) return _singBoxVersion;
            _singBoxVersionProbed = true;
            try
            {
                var exe = Path.Combine(NeoPaths.RuntimeDir, "sing-box.exe");
                if (!File.Exists(exe)) return null;
                var txt = RunVersionProbe(exe, @"version\s+(\S+)");
                if (txt != null)
                {
                    _singBoxVersion = txt;
                    LogBus.App($"检测到 sing-box {_singBoxVersion}", "Debug");
                }
            }
            catch { }
            return _singBoxVersion;
        }
    }

    /// 起一次 `&lt;exe&gt; version` 并抽出匹配组 1 的版本号；失败返回 null。
    /// 三个容易踩的坑，这里都避开：
    /// ① **不能 `RedirectStandardError = true` 却不读它** —— 子进程往 stderr 写满
    ///    管道缓冲区（Windows 约 4 KB）后会阻塞，而父进程正在 `ReadToEnd()` 等 stdout，
    ///    双方互等即死锁。这些探测只需要 stdout，因此直接不重定向 stderr。
    /// ② **`WaitForExit` 必须在 `ReadToEnd` 之后**（顺序原实现是对的，这里保持）：
    ///    否则输出还没读完就判定已退出，会读到截断内容。
    /// ③ 给整段加一个总时限，避免子进程挂在半途把调用线程（可能是 UI 线程）拖住。
    private static string? RunVersionProbe(string exe, string pattern)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            ArgumentList = { "version" },
            RedirectStandardOutput = true,
            RedirectStandardError = false,   // 见上：重定向却不读 = 死锁风险
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (p == null) return null;

        var read = p.StandardOutput.ReadToEndAsync();
        if (!read.Wait(TimeSpan.FromSeconds(5)))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return null;
        }
        try { p.WaitForExit(3000); } catch { }

        var m = System.Text.RegularExpressions.Regex.Match(read.Result, pattern);
        return m.Success ? m.Groups[1].Value : null;
    }
}
