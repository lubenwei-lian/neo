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

    // 用户端口的本地中继（无感切换的核心）：内核监听 21908/21909，用户仍用 21808/21809。
    private static PortRelay? _relaySocks;
    private static PortRelay? _relayHttp;
    /// <summary>正在退役的旧内核（等它连接归零后 Stop）。下一次切换若目标槽位被它占着，会强停它。</summary>
    private static CoreProcess? _retiringCore;

    /// <summary>无感切换的互斥门（0 = 空闲，1 = 切换中），防连点并发。</summary>
    private static int _switchGate;

    /// <summary>
    /// 无感切换进度文本（"正在切换节点…" / ""）。ConnectionPage 订阅后显示在圆环右侧留白处。
    /// 刻意不走 UserNotice：那条会顶到状态条、字多，用户明确要求「只显示正在切换节点」且不挡文字。
    /// </summary>
    public static event Action<string>? SwitchProgress;

    /// <summary>确保用户端口的中继在跑，并把后端指向当前活跃内核。</summary>
    private static void EnsureRelays()
    {
        if (_relaySocks == null || _relaySocks.ListenPort != LocalPorts.Socks)
        {
            try { _relaySocks?.Dispose(); } catch { }
            _relaySocks = new PortRelay(LocalPorts.Socks, LocalPorts.CoreSocks, OnRelayBackendFail);
        }
        if (_relayHttp == null || _relayHttp.ListenPort != LocalPorts.Http)
        {
            try { _relayHttp?.Dispose(); } catch { }
            _relayHttp = new PortRelay(LocalPorts.Http, LocalPorts.CoreHttp, OnRelayBackendFail);
        }
        if (!_relaySocks.IsRunning) _relaySocks.Start();
        if (!_relayHttp.IsRunning) _relayHttp.Start();
        _relaySocks.SwitchBackend(LocalPorts.CoreSocks);
        _relayHttp.SwitchBackend(LocalPorts.CoreHttp);
    }

    private static DateTime _relayFailWarnAt;

    /// <summary>
    /// P2-2（审计）：中继后端连不上 = 当前内核已经不可用。原实现 only 注释说「交给上层处理」，
    /// 但回调从未接线，连接被静默关闭、日志全绿，用户只觉得「端口能连但请求全失败」。
    /// 这里限流记 Warning，给排障留痕（真正的恢复仍由内核重启/故障转移负责）。
    /// </summary>
    private static void OnRelayBackendFail(int port)
    {
        try
        {
            var now = DateTime.Now;
            if ((now - _relayFailWarnAt).TotalSeconds < 10) return;
            _relayFailWarnAt = now;
            LogBus.App($"端口中继：后端 {port} 连不上（内核可能已退出），请求已直接失败；等待自动恢复或手动重连", "Warning");
        }
        catch { }
    }

    /// <summary>停掉用户端口中继（断开连接时调用；下次连接会重建以适配端口变更）。</summary>
    private static void StopRelays()
    {
        try { _relaySocks?.Dispose(); } catch { }
        try { _relayHttp?.Dispose(); } catch { }
        _relaySocks = null;
        _relayHttp = null;
    }

    /// naive 节点的辅助进程（naive.exe 只提供 socks 入口；前置 sing-box 负责 http/TUN）。
    private static CoreProcess? _naive;

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

    /// 连接后自检的取消源：断开 / 切换会话 / 内核退出时取消。
    private static CancellationTokenSource? _selfCheckCts;

    /// 本轮会话已自动切换过几次（最多 2 次：一个坏节点可能是偶然，两次都坏就停手）。
    private static int _selfCheckSwitchCount;

    /// 本轮会话已经试过的节点键（避免自检在两个坏节点之间来回切）。
    // P2-7（审计）：这个集合被 UI 线程（ConnectAsync 里 Clear）和自检后台线程（Add）同时读写，
    // HashSet 无锁并发可能抛 InvalidOperationException 或破坏内部结构。换 ConcurrentDictionary。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _selfCheckTried = new(StringComparer.Ordinal);

    /// 当前 ConnectAsync 是否由「自检自动切换」发起（该新会话不再自检，保证一轮最多切 1 次）。
    private static bool _selfCheckSwitching;

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

    /// 固定出口是否生效（本次会话）。生效时出口固定为单一节点，其它节点只作入口。
    public static bool FixedExitActive { get; private set; }

    /// 当前会话是否由 TUN 接管（系统代理不生效，故障转移自愈逻辑据此跳过）。
    public static bool IsTunSession => _tunSessionActive;

    /// 固定出口模式下参与分摊的入口链路数。
    public static int FixedExitEntries { get; private set; }

    /// 固定出口模式下出口节点的名称。
    public static string? FixedExitName { get; private set; }

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

    /// <summary>
    /// 需要直接告诉用户的兜底事件（内核看门狗进安全模式等）。
    /// 与 LogLine 的区别：LogLine 是日志流（用户不看日志就永远不知道），本事件由 MainWindow
    /// 转成状态条 + 托盘气泡 —— 因为内核异常时窗口常常在托盘里，只有气泡才看得见。
    /// 2026-10-23 第二批新增（F9）。
    /// </summary>
    public static event Action<string>? UserNotice;

    public static string ConfigPath => Path.Combine(NeoPaths.DataDir, "config.json");

    public static async Task<(bool Ok, string? Error)> ConnectAsync(NodeItem node, bool force = false)
    {
        if (!force && IsConnected && ConnectedNode != null && SameNode(ConnectedNode, node))
            return (true, null);

        // 用户/故障转移发起的连接：重置「自检自动切换」的预算。
        // （自检自己发起的连接不清零，否则会无限切下去。）
        if (!_selfCheckSwitching)
        {
            _selfCheckSwitchCount = 0;
            _selfCheckTried.Clear();
        }

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

        // 上次分流数据更新时若有文件被内核占用，已暂存为 *.pending；这里在启动内核前应用，
        // 保证「更新后连接」用到的就是新数据（否则要等下一次重启才生效）。
        try { GeoDataUpdater.ApplyPending(); } catch { }

        FixedExitActive = false;
        FixedExitEntries = 0;
        FixedExitName = null;

        bool useTun = AppState.State.UseTun;

        // ★ 2026-10-23：Clash 直通节点（整份 YAML → mihomo）。
        //   限制：暂不支持 TUN（mihomo 的 TUN 需要额外适配本项目的网卡/路由回收逻辑）；
        //   未安装 mihomo 时明确报错并指路，而不是抛一个「内核不存在」。
        bool useMihomo = node.Protocol == NodeProtocol.MihomoConfig;
        string? mihomoConfigPath = null;
        string? mihomoDataDir = null;
        if (useMihomo)
        {
            if (useTun)
            {
                return (false, "Clash 直通节点暂不支持 TUN 模式 —— 请关闭 TUN，或改用拆分出来的普通节点");
            }
            if (!KernelCatalog.IsInstalled(CoreEngine.Mihomo))
            {
                return (false, "Clash 直通需要 mihomo 内核：请到 设置 → 内核 → mihomo，点「检查更新」按需安装后再连接");
            }
        }

        // ★ 2026-10-23：naiveproxy（两段式：naive.exe 辅助 + sing-box 前置）。
        bool useNaive = node.IsNaive;
        string? naiveConfigPath = null;
        if (useNaive)
        {
            if (!KernelCatalog.IsInstalled(CoreEngine.Naive))
                return (false, "naive 节点需要 naive 内核：请到 设置 → 内核 → naive，点「检查更新」按需安装后再连接");
        }

        // ★ 2026-10-23：自定义配置直通（订阅给的是整份 xray/sing-box JSON）。
        //   限制：暂不支持 TUN（不做 tun inbound 注入），固定出口也跳过（会破坏用户自己的路由）。
        bool useCustom = node.IsCustomConfig;
        bool customSingBox = useCustom && string.Equals(node.CustomEngine, "sing-box", StringComparison.OrdinalIgnoreCase);
        string? customConfigPath = null;
        if (useCustom)
        {
            //   2026-10-23：sing-box 自定义配置现在支持 TUN（注入 tun 入口 + sniff/hijack-dns + 最小 DNS）；
            //   xray 本身没有 tun 入站，仍然明确拒绝。
            if (useTun && !customSingBox)
                return (false, "自定义 xray 配置不支持 TUN 模式（xray 没有 tun 入站）—— 请关闭 TUN；"
                             + "需要 TUN 的话请改用 sing-box 格式的自定义配置");
            if (!node.Extras.TryGetValue("config_path", out var customSrc) || !File.Exists(customSrc))
                return (false, "自定义配置文件不存在（可能已被清理），请重新拉取订阅");
        }

        // ★ 2026-10-23 第二批：内核能力判定（NodeItem.RequiresXray / RequiresSingBox，依据见台账 F1 实测）。
        //   TUN 模式固定跑 sing-box，而 mKCP / xhttp / VLESS 后量子加密 / REALITY pqv / vcn 只有 xray 支持。
        //   这类节点在 TUN 下**必须明确拒绝**：静默改走 sing-box 会变成「连上了但所有站点超时」，
        //   比直接提示难排查得多；静默改走系统代理又会让用户以为 TUN 生效（UDP 泄漏）。
        if (useTun && node.RequiresXray)
        {
            var why = XrayOnlyReason(node);
            LogBus.App($"连接中止：节点「{node.Name}」需要 xray 专有能力（{why}），TUN 模式固定跑 sing-box，无法承载", "Error");
            IncidentLog.Record("connect", "TUN 与节点能力冲突，已拒绝连接", $"{node.Name}：{why}");
            return (false, $"节点「{node.Name}」用到 xray 专有能力（{why}），TUN 模式不支持；请关闭 TUN 或换节点");
        }

        //   优先级：xray 专有能力（xhttp/kcp/VLESS 加密/pqv/vcn）优先于「明文 vless 需 sing-box」的判定，
        //   否则「明文 vless + xhttp」这类节点会被推给 sing-box，然后因为 xhttp 不支持而直接失败。
        // ★ 2026-10-23：叠加「协议 → 内核」手动映射（设置→内核→协议指定内核）。
        //   能力不允许时 KernelRouter 返回 Auto（保持自动判定）；TUN 恒 sing-box，映射与 TUN 冲突时忽略并留痕。
        if (useTun && Neo.Core.Core.KernelRouter.ResolveFor(node) == Neo.Core.Core.KernelRouter.Choice.Xray)
            LogBus.App("协议→内核映射要求 xray，但 TUN 模式固定 sing-box，本次忽略该映射", "Warning");

        bool useSingBox = useNaive
            ? true   // naive 的前置固定用 sing-box（socks 出站 + 可选 tun 入站）
            : useCustom
            ? customSingBox
            : useTun
            || Neo.Core.Core.KernelRouter.Apply(node,
                   node.Protocol is NodeProtocol.Hysteria2 or NodeProtocol.TUIC
                   || (node.RequiresSingBox && !node.RequiresXray));

        // 固定出口（多入口加速）：出口固定一个节点，其它节点作为入口给它叠加带宽。
        // TUN 走 sing-box，暂不支持链式出站，按普通模式连接。
        NodeItem? fixedExit = null;
        // 2026-10-23 审计 P1-2：完整连接也会生成 sing-box 配置，clash_api 端口必须跟随当前槽位，
        // 否则 ActiveSlot 变成 1 之后，统计按 21821 取数而内核监听 21811 → 流量统计永久静默失效。
        SingBoxConfigBuilder.ClashApiPortOverride = LocalPorts.SlotClashApi(LocalPorts.ActiveSlot);

        var fixedEntries = new List<NodeItem>();
        if (useCustom && AppState.State.FixedExitEnabled)
        {
            LogBus.App("自定义配置直通：固定出口与本节点自带的 outbounds/routing 冲突，本次不启用固定出口", "Warning");
        }
        else if (AppState.State.FixedExitEnabled)
        {
            if (useTun)
            {
                LogBus.App("固定出口：TUN 模式暂不支持（需要 xray 链式出站），本次按普通模式连接", "Warning");
            }
            else
            {
                try
                {
                    fixedExit = ResolveFixedExitNode();
                    if (fixedExit == null)
                    {
                        LogBus.App("固定出口：没有可用出口节点（出口需为非 REALITY 的 ws/tls 节点），本次按普通模式连接", "Warning");
                    }
                    else
                    {
                        fixedEntries = PickFixedExitEntries(fixedExit, AppState.State.FixedExitEntryCount);
                        if (fixedEntries.Count == 0)
                        {
                            LogBus.App("固定出口：除出口「" + fixedExit.Name + "」外没有可用入口节点，本次按单节点连接", "Warning");
                            fixedExit = null;
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogBus.App("固定出口：准备失败，退回普通模式（" + ex.GetType().Name + ": " + ex.Message + "）", "Warning");
                    fixedExit = null;
                }
            }
        }

        string json;
        try
        {
            if (useNaive)
            {
                // naive 配置 + 前置 sing-box 配置（socks 出站指向 127.0.0.1:NaiveListen）。
                var naiveDir = Path.Combine(NeoPaths.DataDir, "naive");
                Directory.CreateDirectory(naiveDir);
                naiveConfigPath = Path.Combine(naiveDir, "config.json");
                var naiveJson = NaiveConfigBuilder.Build(node, LocalPorts.NaiveListen);
                var ntmp = naiveConfigPath + ".tmp";
                File.WriteAllText(ntmp, naiveJson);
                File.Move(ntmp, naiveConfigPath, true);

                var frontNode = new NodeItem
                {
                    Protocol = NodeProtocol.Socks,
                    Server = "127.0.0.1",
                    Port = LocalPorts.NaiveListen,
                    Name = node.Name,
                    Security = "none",
                };
                json = useTun
                    ? SingBoxConfigBuilder.BuildTun(frontNode, LocalPorts.CoreSocks, LocalPorts.CoreHttp,
                        AppState.State.ProxyMode, AppState.State.BypassPrivate, AppState.State.BypassCn,
                        AppState.State.BlockAds, AppState.State.AllowLan, AppState.State.CustomRules,
                        AppState.State.KernelLogLevel, AppState.State.DnsServer,
                        AppState.State.GameDirectEnabled, AppState.GameDirectProcessList())
                    : SingBoxConfigBuilder.Build(frontNode, LocalPorts.CoreSocks, LocalPorts.CoreHttp,
                        AppState.State.ProxyMode, AppState.State.BypassPrivate, AppState.State.BypassCn,
                        AppState.State.BlockAds, AppState.State.AllowLan, AppState.State.CustomRules,
                        AppState.State.KernelLogLevel, AppState.State.DnsServer,
                        AppState.State.GameDirectEnabled, AppState.GameDirectProcessList());
                CoreKind = "naive";
            }
            else if (useCustom)
            {
                // 自定义整份配置：原样保留 outbounds/routing，只改本地接入层与统计接口。
                var srcJson = File.ReadAllText(node.Extras["config_path"]);
                var customDir = Path.Combine(NeoPaths.DataDir, "custom");
                Directory.CreateDirectory(customDir);
                customConfigPath = Path.Combine(customDir, "runtime-config.json");
                json = customSingBox
                    ? (useTun
                        ? CustomConfigBuilder.BuildSingBoxTun(srcJson,
                            LocalPorts.CoreSocks, LocalPorts.CoreHttp,
                            LocalPorts.CoreClashApi, SingBoxConfigBuilder.ClashApiSecret,
                            AppState.State.AllowLan, AppState.State.DnsServer)
                        : CustomConfigBuilder.BuildSingBox(srcJson,
                            LocalPorts.CoreSocks, LocalPorts.CoreHttp,
                            LocalPorts.CoreClashApi, SingBoxConfigBuilder.ClashApiSecret,
                            AppState.State.AllowLan))
                    : CustomConfigBuilder.BuildXray(srcJson,
                        LocalPorts.CoreSocks, LocalPorts.CoreHttp,
                        LocalPorts.CoreStatsApi, AppState.State.AllowLan, AppState.State.KernelLogLevel);
                var ctmp = customConfigPath + ".tmp";
                File.WriteAllText(ctmp, json);
                File.Move(ctmp, customConfigPath, true);
                CoreKind = customSingBox ? "sing-box" : "xray";
            }
            else if (useMihomo)
            {
                // 整份 Clash YAML 交给 mihomo：只覆盖本地接入端口/控制器。
                var apiPort = LocalPorts.CoreClashApi;
                // 2026-10-23 无感切换：mihomo 也必须监听内核内部端口。
                //   之前这里传的是用户端口（21808/21809），与 PortRelay 抢同一个监听端口，
                //   表现为 mihomo 会话端口被占、中继也没起到转发作用。
                var yaml = MihomoConfigBuilder.Build(node,
                    LocalPorts.CoreHttp, LocalPorts.CoreSocks,
                    apiPort, AppState.State.KernelLogLevel);
                mihomoDataDir = Path.Combine(NeoPaths.DataDir, "mihomo");
                Directory.CreateDirectory(mihomoDataDir);
                mihomoConfigPath = Path.Combine(mihomoDataDir, "config.yaml");
                var ytmp = mihomoConfigPath + ".tmp";
                File.WriteAllText(ytmp, yaml);
                File.Move(ytmp, mihomoConfigPath, true);

                // GEOIP / GEOSITE 规则需要数据文件：runtime 里有就复制到 mihomo 工作目录，
                // 避免它启动时联网下载（本项目里 mihomo 走系统代理前还没有网络，下载必失败）。
                try
                {
                    foreach (var name in new[] { "Country.mmdb", "geoip.dat", "geosite.dat" })
                    {
                        var src = Path.Combine(NeoPaths.RuntimeDir, name);
                        if (File.Exists(src)) File.Copy(src, Path.Combine(mihomoDataDir, name), true);
                    }
                }
                catch { }

                json = "";
                CoreKind = "mihomo";
            }
            else if (fixedExit != null && !useSingBox)
            {
                json = XrayConfigBuilder.BuildFixedExit(fixedExit, fixedEntries,
                    LocalPorts.CoreSocks, LocalPorts.CoreHttp,
                    AppState.State.ProxyMode, AppState.State.BypassPrivate, AppState.State.BypassCn,
                    AppState.State.BlockAds, AppState.State.AllowLan, AppState.State.CustomRules,
                    AppState.State.KernelLogLevel, AppState.State.DnsServer);
                FixedExitActive = true;
                FixedExitEntries = fixedEntries.Count;
                FixedExitName = fixedExit.Name;
                LogBus.App("固定出口已启用：出口「" + fixedExit.Name + "」· 入口 " + fixedEntries.Count + " 条链路（"
                    + string.Join("、", fixedEntries.Take(6).Select(n => n.Name))
                    + (fixedEntries.Count > 6 ? "…" : "") + "）");
            }
            else
            {
                json = useMihomo ? "" : useSingBox
                    ? (useTun
                        ? SingBoxConfigBuilder.BuildTun(node, LocalPorts.CoreSocks, LocalPorts.CoreHttp,
                            AppState.State.ProxyMode, AppState.State.BypassPrivate, AppState.State.BypassCn,
                            AppState.State.BlockAds, AppState.State.AllowLan, AppState.State.CustomRules,
                            AppState.State.KernelLogLevel, AppState.State.DnsServer,
                            AppState.State.GameDirectEnabled, AppState.GameDirectProcessList())
                        : SingBoxConfigBuilder.Build(node, LocalPorts.CoreSocks, LocalPorts.CoreHttp,
                            AppState.State.ProxyMode, AppState.State.BypassPrivate, AppState.State.BypassCn,
                            AppState.State.BlockAds, AppState.State.AllowLan, AppState.State.CustomRules,
                            AppState.State.KernelLogLevel, AppState.State.DnsServer,
                            AppState.State.GameDirectEnabled, AppState.GameDirectProcessList()))
                    : XrayConfigBuilder.Build(node,
                        LocalPorts.CoreSocks, LocalPorts.CoreHttp,
                        AppState.State.ProxyMode, AppState.State.BypassPrivate, AppState.State.BypassCn,
                        AppState.State.BlockAds, AppState.State.AllowLan, AppState.State.CustomRules,
                        AppState.State.KernelLogLevel, AppState.State.DnsServer);
            }
            if (!useMihomo && !useCustom && !useNaive) CoreKind = useSingBox ? "sing-box" : "xray";
        }
        catch (Exception ex)
        {
            // 兜底：固定出口配置生成失败（例如出口节点类型不支持串联）时，
            // 自动退回单节点再试一次，避免用户直接连不上（稳中取快）。
            try
            {
                LogBus.App("配置生成失败，正在改用单节点配置重试（兜底）：" + ex.Message, "Warning");
                IncidentLog.Record("fallback", "配置生成失败，已退回单节点重试", ex.Message);
                FixedExitActive = false;
                FixedExitEntries = 0;
                FixedExitName = null;
                json = useMihomo ? "" : useSingBox
                    ? (useTun
                        ? SingBoxConfigBuilder.BuildTun(node, LocalPorts.CoreSocks, LocalPorts.CoreHttp,
                            AppState.State.ProxyMode, AppState.State.BypassPrivate, AppState.State.BypassCn,
                            AppState.State.BlockAds, AppState.State.AllowLan, AppState.State.CustomRules,
                            AppState.State.KernelLogLevel, AppState.State.DnsServer,
                            AppState.State.GameDirectEnabled, AppState.GameDirectProcessList())
                        : SingBoxConfigBuilder.Build(node, LocalPorts.CoreSocks, LocalPorts.CoreHttp,
                            AppState.State.ProxyMode, AppState.State.BypassPrivate, AppState.State.BypassCn,
                            AppState.State.BlockAds, AppState.State.AllowLan, AppState.State.CustomRules,
                            AppState.State.KernelLogLevel, AppState.State.DnsServer,
                            AppState.State.GameDirectEnabled, AppState.GameDirectProcessList()))
                    : XrayConfigBuilder.Build(node,
                        LocalPorts.CoreSocks, LocalPorts.CoreHttp,
                        AppState.State.ProxyMode, AppState.State.BypassPrivate, AppState.State.BypassCn,
                        AppState.State.BlockAds, AppState.State.AllowLan, AppState.State.CustomRules,
                        AppState.State.KernelLogLevel, AppState.State.DnsServer);
            }
            catch (Exception ex2)
            {
                return (false, "配置生成失败：" + ex2.Message);
            }
        }

        // ---- 启动前配置预检 + 自适应 ----
        // 写盘/启动前先用内核自己的 -test/check 验一遍：版本变化导致字段/传输被弃用时，
        // 按「版本规则 → 去性能字段 → 最简单节点配置」逐级降级，绝不把内核拒绝的配置直接交给它。
        if (useMihomo)
        {
            if (string.IsNullOrEmpty(mihomoConfigPath) || !File.Exists(mihomoConfigPath))
                return (false, "Clash 直通配置生成失败（YAML 未写出），请检查订阅缓存是否被清理后重试");
            var chk = await Task.Run(() => RunMihomoCheck(mihomoConfigPath!));
            if (!chk.Ok) return (false, "Clash 配置未通过 mihomo 校验：" + chk.Message);
        }
        else if (useCustom)
        {
            // 自定义配置不用自适应改写（会把用户自己的字段改掉），只做一次原样校验。
            var customExe = Path.Combine(NeoPaths.RuntimeDir, useSingBox ? "sing-box.exe" : "xray.exe");
            var customKind = useSingBox ? KernelKind.SingBox : KernelKind.Xray;
            var chk = await Task.Run(() => KernelCompat.RunCheck(customExe, customKind, customConfigPath!, 15000));
            if (!chk.Ok)
                return (false, $"自定义配置未通过 {(useSingBox ? "sing-box" : "xray")} 校验：{chk.Output}");
        }
        else
        {
            json = await EnsureCompatibleConfigAsync(json, node, useSingBox);
        }

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
        var cfgPath = ConfigPath;
        if (useMihomo)
        {
            cfgPath = mihomoConfigPath!;   // YAML 已在上面原子写出，不再写 config.json
        }
        else if (useCustom && customConfigPath != null)
        {
            cfgPath = customConfigPath;    // 自定义配置已原子写出（runtime-config.json）
        }
        else
        {
            try
            {
                var cfgTmp = ConfigPath + ".tmp";
                File.WriteAllText(cfgTmp, json);
                File.Move(cfgTmp, ConfigPath, true);
            }
            catch (Exception ex) { return (false, "配置写入失败：" + ex.Message); }
        }

        var exe = Path.Combine(NeoPaths.RuntimeDir,
            useMihomo ? "mihomo.exe" : (useSingBox ? "sing-box.exe" : "xray.exe"));

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

            // naive：先起辅助进程并等 socks 端口就绪，再起前置 sing-box。
            if (useNaive && naiveConfigPath != null)
            {
                var naiveExe = KernelCatalog.ExePath(CoreEngine.Naive);
                _naive = new CoreProcess(naiveExe, Path.GetDirectoryName(naiveConfigPath)!);
                _naive.LogLine += line => LogBus.Core("[naive] " + line);
                _naive.Exited += code => LogBus.App($"naive 辅助进程退出（exit code {code}）", "Warning");
                _naive.Start(naiveConfigPath, new[] { naiveConfigPath });
                var ready = false;
                for (var i = 0; i < 25 && !ready; i++)
                {
                    await Task.Delay(100);
                    try
                    {
                        using var c = new System.Net.Sockets.TcpClient();
                        await c.ConnectAsync(System.Net.IPAddress.Loopback, LocalPorts.NaiveListen);
                        ready = true;
                    }
                    catch { }
                }
                if (!ready)
                {
                    try { _naive.StopAndVerify(); } catch { }
                    _naive = null;
                    return (false, "naive 辅助进程启动失败（socks 入口未就绪），请检查 naive 内核是否完整");
                }
                LogBus.App($"naive 辅助进程已就绪（PID {_naive.Pid} · 本地 socks {LocalPorts.NaiveListen}）");
            }

            _xray = new CoreProcess(exe, NeoPaths.RuntimeDir);
            _xray.LogLine += line =>
            {
                LogBus.Core(line);
                _kernelTail.Enqueue(line);
                while (_kernelTail.Count > 60) _kernelTail.TryDequeue(out _);
                LogLine?.Invoke(line);
            };
            var coreSelf = _xray;
            coreSelf.Exited += code => OnCoreExited(coreSelf, code);
            if (useMihomo)
                _xray.Start(cfgPath, new[] { "-f", cfgPath, "-d", mihomoDataDir! });
            else
                _xray.Start(cfgPath);
        }
        catch (Exception ex)
        {
            _xray = null;
            LogBus.Exception($"内核启动失败（{CoreKind}）", ex);
            return (false, "内核启动失败：" + ex.Message);
        }

        await Task.Delay(useMihomo ? 1400 : 800);
        if (_xray == null || !_xray.IsRunning)
        {
            Disconnect("内核启动失败");
            //   原先只查 10808/10085 —— 而这两个正是刚被门禁放行的，于是 10809 冲突
            var busyPort = FirstBusyPort();
            bool busy = busyPort != 0;
            var bootTail = KernelTailText();
            // 已知特征映射成中文可读原因（预检通过但内核仍退出时，多半是运行期/传输/字段问题）。
            var bootHint = KernelCompat.DescribeStartupFailure(bootTail);
            LogBus.App(busy
                ? $"内核启动失败：本地端口 {busyPort} 被占用（残留内核已尝试清理，请重试）"
                : "内核启动后立即退出（配置或参数错误）"
                  + (bootHint.Length > 0 ? "；已知特征：" + bootHint : "")
                  + (bootTail.Length > 0 ? $"{Environment.NewLine}内核最后输出：{bootTail}" : ""), "Error");
            var failMsg = busy
                ? "内核启动失败：本地端口 " + busyPort + " 被占用（残留内核已尝试清理，请重试）"
                : "内核启动后立即退出（配置或参数错误，详见内核日志）"
                  + (bootHint.Length > 0 ? "（" + bootHint + "）" : "");
            IncidentLog.Record("error", failMsg, bootTail);
            return (false, failMsg);
        }

        // ★ 2026-10-23 无感切换：内核只监听内部端口，用户端口交给 PortRelay 转发。
        //   这样切换节点时只需改 Relay 的后端指针，用户端口不变、系统代理不用改、老连接不断。
        EnsureRelays();
        LogBus.App($"{CoreKind} 内核已启动（PID {_xray.Pid} · 内核端口 {LocalPorts.CoreSocks}/{LocalPorts.CoreHttp} · 用户端口 {LocalPorts.Socks}/{LocalPorts.Http} 由中继转发）");

        // 系统代理：非 TUN 时连上就自动写入 Windows。
        //   Enable()，开关的两种状态落到同一结果，纯属误导）。因此这里不再有任何开关判断，
        //   行为与旧版一致：连上即写、断开即清。
        //   TUN 模式走虚拟网卡接管全部流量，不需要也不应该写系统代理。
        // ★ 2026-10-22 关键修复：系统代理必须指向「HTTP 入站」端口（10809），不是 SOCKS 端口（10808）。
        //   Windows 的 ProxyServer 里裸写 "127.0.0.1:10808" 会被当作 **HTTP 代理**，
        //   于是 Edge / Steam / 其它走 WinINet 的程序把 HTTP CONNECT 发到 socks 监听端口，
        //   直接被拒（实测 curl -x http://127.0.0.1:10808 → 1ms 失败；-x http://127.0.0.1:10809 → 200）。
        //   这也是「所有网页转圈、Steam 加载半天」的直接原因。
        //   同时把 socks 显式写进分号串，支持 socks 的程序仍可直连 10808。
        if (!useTun)
            SystemProxy.Enable(
                $"http=127.0.0.1:{LocalPorts.Http};" +
                $"https=127.0.0.1:{LocalPorts.Http};" +
                $"socks=127.0.0.1:{LocalPorts.Socks}");

        // 记录本次会话是否用了 TUN：断开时据此决定是否清理 neo-tun 网卡与路由
        // （TUN 打开就"关闭不掉"的修复，见 TeardownTunNetwork 注释）
        _tunSessionActive = useTun;
        //   neo-tun 建立/销毁会触发 NetworkAddressChanged，FailoverService 据此
        //   过滤掉「我们自己造成的网络变化」，避免每次连 TUN 都误报「立即复查」。
        if (useTun) FailoverService.MarkTunNetworkEvent();

        IsConnected = true;
        ConnectedNode = node;
        IncidentLog.Record("connect", "已连接 " + node.Name
            + (FixedExitActive ? "（固定出口 " + FixedExitName + "）" : ""));
        ConnectedAt = DateTime.Now;
        _lastCoreStartAt = DateTime.Now;   // 供「内核是否稳定运行过」判断使用
        _ = TryAutoMeasureDelayAsync(node);   // 后台补测本节点延迟，让「未测速」自愈
        _ = WarmupDnsAsync();                 // 连接成功后预热 DNS（x.com / cloudflare），减轻首访冷启动
        StartWatchdog(node);                  // 内核兜底：每 20s 检查进程/端口/TUN/stats API
        var gdCount = (useSingBox && AppState.State.GameDirectEnabled) ? AppState.GameDirectProcessList().Count : 0;
        LogBus.App($"=== 会话开始 · 节点：{node.Name} · 模式：{ModeName(AppState.State.ProxyMode)} · TUN={(useTun ? "开" : "关")} · 游戏直连={(gdCount > 0 ? $"开（{gdCount} 个进程）" : "关")}" +
                   (FixedExitActive ? $" · 固定出口={FixedExitName}（入口 {FixedExitEntries}）" : "") + " ===");
        NotifyStateChanged();
        // 连接后自检（fire-and-forget）：链路站稳后确认「目标站」真的可访问；
        // 不可用且条件允许时自动切同地区下一个节点（最多 1 次）。不阻塞本连接流程。
        StartPostConnectSelfCheck(node);
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

    // ---- 启动前配置预检 + 自适应 ----
    // 背景：内核版本变化会移除旧字段/传输；此前没有预检，被拒只能表现为「内核启动后立即退出」。
    // 流程：预检 → 版本规则适配 → 去性能字段 → 最简单节点配置，每级都留痕（LogBus + IncidentLog）。
    // 预检本身跑不起来（缺 exe / 超时）不阻断连接。

    private static async Task<string> EnsureCompatibleConfigAsync(string json, NodeItem node, bool useSingBox)
    {
        var kind = useSingBox ? KernelKind.SingBox : KernelKind.Xray;
        var coreName = useSingBox ? "sing-box" : "xray";
        var exe = Path.Combine(NeoPaths.RuntimeDir, useSingBox ? "sing-box.exe" : "xray.exe");
        var tmp = Path.Combine(Path.GetTempPath(), $"neo-preflight-{Environment.ProcessId}-{DateTime.Now:HHmmssfff}.json");
        var current = json;
        var lastStderr = "";
        try
        {
            // 版本取自既有缓存探测；失败则只跳过「版本规则」这一级，预检照跑。
            Version? ver = null;
            try
            {
                var txt = await Task.Run(() => useSingBox ? SingBoxVersion : XrayVersion);
                if (!string.IsNullOrWhiteSpace(txt)) ver = KernelCompat.ParseVersion(txt, kind);
            }
            catch { }

            File.WriteAllText(tmp, current);
            var r0 = await Task.Run(() => KernelCompat.RunCheck(exe, kind, tmp, 8000));
            if (r0.Ok)
            {
                if (ver != null)
                {
                    // 通过时只记录版本注记（不改配置），例如 xray 26 仍支持但弃用 ws 的提示。
                    KernelCompat.Adapt(current, kind, ver, out var okNotes);
                    foreach (var n in okNotes)
                        if (n.StartsWith("版本提示：", StringComparison.Ordinal)) LogBus.App(n, "Debug");
                }
                LogBus.App($"配置预检通过（{coreName}）", "Debug");
                return current;
            }
            if (r0.InfraFailed)
            {
                LogBus.App($"配置预检无法执行（不阻断连接）：{r0.Output}", "Warning");
                return current;
            }
            lastStderr = r0.Output;
            RecordPreflightFailure("配置预检失败", r0.Output);

            // 第 1 级：按内核版本规则适配（sing-box 1.16 DNS 字段 / xray 传输注记）
            if (ver != null)
            {
                var adapted = KernelCompat.Adapt(current, kind, ver, out var notes);
                LogAdaptNotes(notes);
                if (!string.Equals(adapted, current, StringComparison.Ordinal))
                {
                    File.WriteAllText(tmp, adapted);
                    var r1 = await Task.Run(() => KernelCompat.RunCheck(exe, kind, tmp, 8000));
                    if (r1.Ok)
                    {
                        RecordAdaptation($"按内核版本 {ver} 适配后预检通过（{coreName}）");
                        return adapted;
                    }
                    if (r1.InfraFailed)
                    {
                        LogBus.App($"配置预检无法执行（不阻断连接）：{r1.Output}", "Warning");
                        return adapted;
                    }
                    lastStderr = r1.Output;
                    RecordPreflightFailure("版本规则适配后预检仍失败", r1.Output);
                    current = adapted;
                }
            }

            // 第 2 级：去掉性能向可选字段（sockopt / observatory / mux 等）
            var stripped = KernelCompat.StripOptionalFields(current, kind, out var stripNotes);
            if (!string.Equals(stripped, current, StringComparison.Ordinal))
            {
                LogAdaptNotes(stripNotes);
                File.WriteAllText(tmp, stripped);
                var r2 = await Task.Run(() => KernelCompat.RunCheck(exe, kind, tmp, 8000));
                if (r2.Ok)
                {
                    RecordAdaptation($"去掉性能向可选字段后预检通过（{coreName}）");
                    return stripped;
                }
                if (r2.InfraFailed)
                {
                    LogBus.App($"配置预检无法执行（不阻断连接）：{r2.Output}", "Warning");
                    return stripped;
                }
                lastStderr = r2.Output;
                RecordPreflightFailure("去掉性能向可选字段后预检仍失败", r2.Output);
                current = stripped;
            }

            // 第 3 级：退回最简单节点配置（不链式、不分流、无自定义规则）
            var minimal = BuildMinimalConfig(node, useSingBox);
            if (minimal != null)
            {
                File.WriteAllText(tmp, minimal);
                var r3 = await Task.Run(() => KernelCompat.RunCheck(exe, kind, tmp, 8000));
                if (r3.Ok)
                {
                    RecordAdaptation($"已退回最简单节点配置并通过预检（{coreName}）");
                    return minimal;
                }
                if (r3.InfraFailed)
                {
                    LogBus.App($"配置预检无法执行（不阻断连接）：{r3.Output}", "Warning");
                    return minimal;
                }
                lastStderr = r3.Output;
                RecordPreflightFailure("最简单节点配置预检仍失败", r3.Output);
                current = minimal;
            }

            var lines = KernelCompat.FirstLines(lastStderr, 3);
            LogBus.App($"配置预检在逐级降级后仍失败（{coreName}），将按最后一份配置尝试启动：{lines}", "Error");
            IncidentLog.Record("compat", "配置预检逐级降级后仍失败，仍尝试启动", lines);
            return current;
        }
        catch (Exception ex)
        {
            LogBus.App($"配置预检异常（不阻断连接）：[{ex.GetType().Name}] {ex.Message}", "Warning");
            return json;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    /// <summary>预检失败留痕：LogBus + IncidentLog("compat", 原因, 原始 stderr 前 3 行)。</summary>
    private static void RecordPreflightFailure(string summary, string output)
    {
        var lines = KernelCompat.FirstLines(output, 3);
        LogBus.App($"{summary}：{lines}", "Warning");
        IncidentLog.Record("compat", summary, lines);
    }

    /// <summary>KernelCompat 注记写日志：改动类写「已自适应：…」+ IncidentLog；版本提示只记 Debug。</summary>
    private static void LogAdaptNotes(List<string> notes)
    {
        foreach (var n in notes)
        {
            if (string.IsNullOrWhiteSpace(n)) continue;
            if (n.StartsWith("版本提示：", StringComparison.Ordinal))
                LogBus.App(n, "Debug");
            else
                RecordAdaptation(n);
        }
    }

    private static void RecordAdaptation(string detail)
    {
        LogBus.App($"已自适应：{detail}", "Warning");
        IncidentLog.Record("compat", "已自适应：" + detail);
    }

    /// <summary>最后一级降级：构造最简单的单节点配置（global 分流、无自定义规则、错误级日志）。</summary>
    private static string? BuildMinimalConfig(NodeItem node, bool useSingBox)
    {
        try
        {
            // 最简单配置不含固定出口链路；若之前已按固定出口生成过配置，这里要把状态复位，避免日志/界面虚报。
            FixedExitActive = false;
            FixedExitEntries = 0;
            FixedExitName = null;

            const string dns = "223.5.5.5";
            if (useSingBox)
            {
                return AppState.State.UseTun
                    ? SingBoxConfigBuilder.BuildTun(node, LocalPorts.CoreSocks, LocalPorts.CoreHttp,
                        "global", false, false, false, false, null, "error", dns)
                    : SingBoxConfigBuilder.Build(node, LocalPorts.CoreSocks, LocalPorts.CoreHttp,
                        "global", false, false, false, false, null, "error", dns);
            }
            return XrayConfigBuilder.Build(node, LocalPorts.CoreSocks, LocalPorts.CoreHttp,
                "global", false, false, false, false, null, "error", dns);
        }
        catch (Exception ex)
        {
            LogBus.App($"构造最简单节点配置失败：{ex.GetType().Name} {ex.Message}", "Warning");
            return null;
        }
    }

    /// <summary>
    /// 无感切换节点（2026-10-23）：先在另一个槽位启动新内核并等它就绪，再把用户端口的中继
    /// 后端切过去，最后延迟停掉旧内核。
    ///   · 期间系统代理不变、用户不用断开、已建立的连接继续走旧内核（下载/视频不中断）；
    ///   · 只对「xray 承载的普通节点 + 非 TUN + 非固定出口」生效；
    ///     其余（sing-box 节点 / TUN / naive / Clash 直通 / 自定义配置）回退普通连接
    ///     —— 那些场景会有 3～4 秒断网，但功能正确。
    /// </summary>
    public static async Task<(bool Ok, string? Err)> SwitchNodeFastAsync(NodeItem node)
    {
        // 2026-10-23 审计 P1-4：连点两次/两个入口同时触发会并发跑两次切换，在同一槽位端口上打架。
        if (Interlocked.Exchange(ref _switchGate, 1) == 1)
            return (false, "正在切换节点中，请稍候");
        // 2026-10-23 审计 P1-3：切换期间也置 IsConnecting，让界面/托盘/故障转移的并发入口全部让路
        //（否则切到一半点断开会让会话「复活」：系统代理已关、看门狗已取消）。
        IsConnecting = true;
        try { SwitchProgress?.Invoke("正在切换节点…"); } catch { }
        try
        {
        if (!IsConnected || _xray is not { IsRunning: true })
        {
            return await ConnectAsync(node, force: true);
        }
        if (AppState.State.UseTun || node.IsNaive || node.IsClashPassthrough || node.IsCustomConfig
            || AppState.State.FixedExitEnabled)
        {
            LogBus.App("节点切换：当前会话不支持无感切换（TUN/特殊节点/固定出口），走普通重连", "Debug");
            return await ConnectAsync(node, force: true);
        }

        // 2026-10-23：sing-box 节点同样支持无感切换（clash_api 已按槽位分开：21811/21821）。
        var useSingBox = KernelRouter.Apply(node, node.Protocol is NodeProtocol.Hysteria2 or NodeProtocol.TUIC
            || (node.RequiresSingBox && !node.RequiresXray));
        var exeName = useSingBox ? "sing-box.exe" : "xray.exe";
        var exePath = Path.Combine(NeoPaths.RuntimeDir, exeName);
        if (!File.Exists(exePath))
        {
            LogBus.App("节点切换：缺少内核 " + exeName + "，保持当前节点", "Warning");
            return (false, "缺少内核 " + exeName);
        }

        var oldSlot = LocalPorts.ActiveSlot;
        var newSlot = 1 - oldSlot;

        // 2026-10-23 审计 P0-2：上一轮切换的旧内核可能还在「等连接归零」的退役期，
        // 仍然占着目标槽位端口。不做预检就直接起新内核 → 绑不上端口 → 8 秒后切换失败
        //（表现为「连续切第二次必挂」）。这里先把它强停，等端口释放。
        if (!PortFree(LocalPorts.SlotSocks(newSlot)) || !PortFree(LocalPorts.SlotHttp(newSlot)))
        {
            LogBus.App("无感切换：目标槽位仍被上一轮内核占用（退役中），先强制退役再切", "Warning");
            try { _retiringCore?.Stop(); } catch { }
            for (var i = 0; i < 20; i++)
            {
                if (PortFree(LocalPorts.SlotSocks(newSlot)) && PortFree(LocalPorts.SlotHttp(newSlot))) break;
                await Task.Delay(150);
            }
            if (!PortFree(LocalPorts.SlotSocks(newSlot)) || !PortFree(LocalPorts.SlotHttp(newSlot)))
            {
                LogBus.App("无感切换：目标槽位端口仍被占用，回退普通重连", "Warning");
                return await ConnectAsync(node, force: true);
            }
        }

        var newSocks = LocalPorts.SlotSocks(newSlot);
        var newHttp = LocalPorts.SlotHttp(newSlot);
        var newStats = LocalPorts.SlotStats(newSlot);

        // 2026-10-23 审计 P1-6：配置放数据目录而不是 %TEMP% —— IsConfirmedOwnCore 靠
        // 命令行里是否含数据目录来认「这是我们的内核」，放 TEMP 会导致崩溃后孤儿内核清不掉、
        // 下次启动一直报「端口被占用」。
        var cfgPath = Path.Combine(NeoPaths.DataDir, $"neo-switch-{newSlot}-{Guid.NewGuid():N}.json");
        CoreProcess? newCore = null;
        try
        {
            var prevClash = SingBoxConfigBuilder.ClashApiPortOverride;
            string json;
            try
            {
                if (useSingBox)
                {
                    SingBoxConfigBuilder.ClashApiPortOverride = LocalPorts.SlotClashApi(newSlot);
                    json = SingBoxConfigBuilder.Build(node, newSocks, newHttp,
                        AppState.State.ProxyMode, AppState.State.BypassPrivate, AppState.State.BypassCn,
                        AppState.State.BlockAds, AppState.State.AllowLan, AppState.State.CustomRules,
                        AppState.State.KernelLogLevel, AppState.State.DnsServer,
                        AppState.State.GameDirectEnabled, AppState.GameDirectProcessList());
                }
                else
                {
                    json = XrayConfigBuilder.Build(node, newSocks, newHttp,
                        AppState.State.ProxyMode, AppState.State.BypassPrivate, AppState.State.BypassCn,
                        AppState.State.BlockAds, AppState.State.AllowLan, AppState.State.CustomRules,
                        AppState.State.KernelLogLevel, AppState.State.DnsServer, newStats);
                }
            }
            finally
            {
                SingBoxConfigBuilder.ClashApiPortOverride = prevClash;
            }
            await File.WriteAllTextAsync(cfgPath, json);

            newCore = new CoreProcess(exePath, NeoPaths.RuntimeDir);
            newCore.LogLine += line =>
            {
                LogBus.Core(line);
                _kernelTail.Enqueue(line);
                while (_kernelTail.Count > 60) _kernelTail.TryDequeue(out _);
            };
            newCore.Start(cfgPath);

            // 等新内核就绪：端口可连 + 进程仍存活（最长 8 秒）
            var ready = false;
            for (var i = 0; i < 40 && !ready; i++)
            {
                await Task.Delay(200);
                if (!newCore.IsRunning) break;
                try
                {
                    using var probe = new System.Net.Sockets.TcpClient();
                    using var cts = new CancellationTokenSource(300);
                    await probe.ConnectAsync(System.Net.IPAddress.Loopback, newSocks, cts.Token);
                    ready = true;
                }
                catch { }
            }
            if (!ready || !newCore.IsRunning)
            {
                try { newCore.Stop(); } catch { }
                LogBus.App("无感切换失败：新内核未能就绪，保持当前节点不变", "Warning");
                IncidentLog.Record("failover", "无感切换失败（新内核未就绪），保持原节点");
                return (false, "新内核未能就绪，已保持当前节点");
            }

            // 切换：旧内核继续服务已有连接，新连接立刻走新内核
            var prev = _xray;
            _xray = newCore;
            var coreSelf = _xray;
            coreSelf.Exited += code => OnCoreExited(coreSelf, code);
            LocalPorts.ActiveSlot = newSlot;
            CoreKind = useSingBox ? "sing-box" : "xray";
            EnsureRelays();
            ConnectedNode = node;
            AppState.SetCurrentNode(node);
            ConnectedAt = DateTime.Now;
            IsConnected = true;

            LogBus.App($"无感切换完成：{node.Name}（新内核 PID {newCore.Pid} · 槽位 {newSlot}），旧内核 20 秒后停止");
            // 用户要求：不要长文案。切换结果只写日志，界面只显示圆环旁的「正在切换节点…」。

            // 旧内核退役（2026-10-23 下载无感）：不再固定 20 秒 —— 正在下载/看视频的连接全在旧内核上，
            // 到点硬停会把长连接截断。改成「等旧槽位连接数归零」再停，最长 5 分钟兜底。
            var oldSocks = LocalPorts.SlotSocks(oldSlot);
            var oldHttp = LocalPorts.SlotHttp(oldSlot);
            _retiringCore = prev;
            _ = Task.Run(async () =>
            {
                // P2-6（审计）：切换瞬间可能有一条连接已经 accept、但还没记进旧后端的计数，
                // 退役循环第一轮就会看到 0 并立刻停旧内核，那条请求随之失败。
                // 先等 2 秒让在途连接落账，再开始判断。
                try { await Task.Delay(2000); } catch { }
                var deadline = DateTime.Now.AddMinutes(5);
                while (DateTime.Now < deadline)
                {
                    var n = (_relaySocks?.ActiveFor(oldSocks) ?? 0) + (_relayHttp?.ActiveFor(oldHttp) ?? 0);
                    if (n == 0) break;
                    try { await Task.Delay(2000); } catch { break; }
                }
                var left = (_relaySocks?.ActiveFor(oldSocks) ?? 0) + (_relayHttp?.ActiveFor(oldHttp) ?? 0);
                try { prev?.Stop(); } catch { }
                try { if (ReferenceEquals(_retiringCore, prev)) _retiringCore = null; } catch { }
                try { File.Delete(cfgPath); } catch { }
                try
                {
                    LogBus.App(left > 0
                        ? $"旧内核已退役（原槽位 {oldSlot}，残留 {left} 条连接被强制关闭）"
                        : $"旧内核已退役（原槽位 {oldSlot}，连接已全部结束）", "Debug");
                }
                catch { }
            });
            return (true, null);
        }
        catch (Exception ex)
        {
            try { newCore?.Stop(); } catch { }
            try { File.Delete(cfgPath); } catch { }
            LogBus.Exception("无感切换失败", ex);
            return (false, "无感切换失败：" + ex.Message);
        }
        }
        finally
        {
            try { SwitchProgress?.Invoke(""); } catch { }
            Interlocked.Exchange(ref _switchGate, 0);
            IsConnecting = false;
        }
    }

    public static void Disconnect(string? reason = null)
    {
        // 断开 / 切换会话：取消尚未完成的连接后自检。
        CancelPostConnectSelfCheck();
        CancelWatchdog();
        // 会话真正结束（非看门狗发起的重启）时，把「看门狗连续触发」计数归零。
        if (!_watchdogRestarting) _watchdogTriggerCount = 0;

        // 用户主动断开 / 退出程序：清掉「故障转移放弃后重连」的待办，
        // 避免用户刚点断开，5 分钟后又被自动连回去。
        if (reason is "用户点击圆环断开" or "托盘菜单断开" or "程序退出")
            FailoverService.CancelPendingReconnect();

        bool was = IsConnected;
        var startedAt = ConnectedAt;
        IsConnected = false;
        ConnectedNode = null;
        ConnectedAt = null;
        FixedExitActive = false;
        FixedExitEntries = 0;
        FixedExitName = null;

        // 用户主动断开时，取消尚未执行的内核自动重启，否则会「断开后又自己连回来」
        try { _coreRestartCts?.Cancel(); } catch { }

        try { SystemProxy.Disable(); } catch { }
        // 用户端口先摘掉中继，再停内核 —— 否则会出现「端口还在监听但后端已死」的窗口。
        StopRelays();

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

        // naive 辅助进程：必须一起停，否则它会一直占着 socks 入口。
        try
        {
            if (_naive != null && !_naive.StopAndVerify())
                LogBus.App("naive 辅助进程未能在超时内退出", "Warning");
        }
        catch (Exception ex) { LogBus.Exception("停止 naive 辅助进程失败", ex); }
        _naive = null;

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
        foreach (var name in new[] { "xray", "sing-box", "naive" })
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
                    //   2026-10-23：TUN 网卡「存在」不等于「在劫持流量」。
                    //   残留网卡可能是上次以管理员身份运行 TUN 留下的；普通权限的 neo 根本删不掉它
                    //   （Remove-NetAdapter 需要管理员），旧逻辑于是每次连接都打一条吓人的 WARN +
                    //   白跑一次拆除（实测 12 次重连 = 12 条 WARN）。现在先判是否真的启用：
                    //   网卡 Up 或带网关 → 才当残留处理；否则只提示一次（Info），不影响上网。
                    if (!IsTunAdapterActive())
                    {
                        if (!_tunResidualHinted)
                        {
                            _tunResidualHinted = true;
                            LogBus.App("发现未启用的 neo-tun 残留网卡（无路由，不影响上网）。若需彻底移除，请以管理员身份运行一次 neo 或重启系统。", "Info");
                        }
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

    /// 只提示一次「未启用的 TUN 残留网卡」，避免每次重连都刷日志。
    private static bool _tunResidualHinted;

    /// <summary>neo-tun 网卡是否真的在用（Up 或带网关）；仅"存在"不算（可能是无权删除的残留）。</summary>
    private static bool IsTunAdapterActive()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!string.Equals(ni.Name, "neo-tun", StringComparison.OrdinalIgnoreCase)
                    && !(ni.Description ?? "").Contains("neo-tun", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up) return true;
                try
                {
                    if (ni.GetIPProperties().GatewayAddresses.Count > 0) return true;
                }
                catch { }
            }
        }
        catch { }
        return false;
    }

    /// <summary>系统里是否还有名为 neo-tun 的虚拟网卡（与 FailoverService 的判定口径保持一致）。</summary>
    private static bool HasTunAdapter()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (string.Equals(ni.Name, "neo-tun", StringComparison.OrdinalIgnoreCase))
                {
                    LogBus.App($"TUN 残留判定：网卡 [{ni.Name}] / [{ni.Description}]", "Debug");
                    return true;
                }
                // 网卡描述名也认：部分系统上 Name 是「本地连接* 12」，Description 才带 neo-tun。
                if ((ni.Description ?? "").Contains("neo-tun", StringComparison.OrdinalIgnoreCase))
                {
                    LogBus.App($"TUN 残留判定：描述 [{ni.Name}] / [{ni.Description}]", "Debug");
                    return true;
                }
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

                // 自定义配置直通（data\custom\runtime-config.json）与 mihomo（data\mihomo\config.yaml）
                // 的配置也都在本安装的数据目录下。之前只认 ConfigPath，导致这两类会话崩溃后
                // 留下的孤儿内核「认不出来、不敢杀」，TUN 网卡也会因为「仍有 sing-box 在跑」被跳过拆除
                //（实测：强杀 neo 后遗留 sing-box + neo-tun Up，一直劫持流量）。
                var dataDir = NeoPaths.DataDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (dataDir.Length > 3 && cmd.Contains(dataDir, StringComparison.OrdinalIgnoreCase)) return true;

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
    /// 只在「确实是 neo 端口」且「端口无人监听」时才清，避免误伤第三方代理（如其它客户端占用同端口）。
    public static void CleanupOrphanedSystemProxy()
    {
        try
        {
            //   2026-10-23 端口迁移：旧版本默认 10808/10809 写进过用户注册表，
            //   换端口后这些残留不再被认作「neo 端口」→ 必须一并纳入清理名单，
            //   否则升级后用户会遇到「旧代理还开着、但 neo 已不监听」的全网断流。
            //   判据仍是「该端口无人监听」，所以别的客户端正占用 10808 时不会被误关（实测 v2rayN 场景）。
            // 2026-10-23 审计 P0-1 修复：注册表里写的是**用户端口**（21808/21809，由 PortRelay 监听），
            // 内核端口是 21908/21909。原先这里传内核端口 → PointsToNeoPort 精确比较永不命中 →
            // 清不掉残代理、启动自愈也静默失效（neo 被强杀后 Edge/Steam 全断网）。
            if (SystemProxy.CleanupOrphaned(
                    LocalPorts.Socks, LocalPorts.Http,
                    LocalPorts.CoreSocks, LocalPorts.CoreHttp,
                    10808, 10809))
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
        (LocalPorts.Socks, !PortFree(LocalPorts.Socks)),
        (LocalPorts.Http, !PortFree(LocalPorts.Http)),
        // 2026-10-23 无感切换：内核现在监听 CoreSocks/CoreHttp，这两个也必须空闲。
        (LocalPorts.CoreSocks, !PortFree(LocalPorts.CoreSocks)),
        (LocalPorts.CoreHttp, !PortFree(LocalPorts.CoreHttp)),
        (LocalPorts.CoreStatsApi, !PortFree(LocalPorts.CoreStatsApi)),
        (LocalPorts.CoreClashApi, !PortFree(LocalPorts.CoreClashApi)),
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

    /// <summary>
    /// 内核连续崩溃、自动重启被放弃时，必须让用户「看见」——否则表现为
    /// 「刚刚还在用，突然所有网页打不开」，用户不知道是内核退出了保护机制。
    /// 走 UserNotice（状态条 + 托盘气泡双通道；托盘模式下只有气泡可见）。
    /// </summary>
    private static void NotifyKernelGaveUp(string detail)
    {
        try { UserNotice?.Invoke($"{detail}，已停止自动重启。请手动重连；若反复出现，请导出诊断（日志里带内核最后输出）。"); } catch { }
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

    // ---- 连接后自检（目标站真可达性）----
    // 背景：故障转移探活只打 google/cloudflare/telegram，测不出「google 通但 x.com 死」的形态
    //（AWS 日本 02 实测：google 204 正常、x.com 12s 超时）。因此每个会话连上后单独自检一次目标站，
    // 不可用且条件允许时自动切同地区下一个节点（最多 1 次）。全程后台执行，不阻塞连接流程。

    /// <summary>启动本会话的连接后自检（fire-and-forget）。</summary>
    private static void StartPostConnectSelfCheck(NodeItem node)
    {
        // 自检自动切换出来的新会话**也要自检**：否则「切到的备用节点同样不可用」就再也发现不了。
        // 防连环切靠 _selfCheckSwitchCount（每轮最多 2 次）与 _selfCheckTried（不重复试同一节点），
        // 只有用户/故障转移发起的新连接才重置这两个预算。
        if (!_selfCheckSwitching)
        {
            _selfCheckSwitchCount = 0;
            _selfCheckTried.Clear();
        }

        CancelPostConnectSelfCheck();

        var cts = new CancellationTokenSource();
        _selfCheckCts = cts;
        var expectKey = AppState.NodeKey(node);
        _ = Task.Run(async () =>
        {
            try { await RunPostConnectSelfCheckAsync(expectKey, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogBus.App($"连接后自检异常（已忽略，不影响连接）：[{ex.GetType().Name}] {ex.Message}", "Warning");
            }
            finally
            {
                if (ReferenceEquals(_selfCheckCts, cts)) _selfCheckCts = null;
                try { cts.Dispose(); } catch { }
            }
        });
    }

    /// <summary>取消连接后自检（断开 / 切换会话 / 内核退出时调用）。</summary>
    private static void CancelPostConnectSelfCheck()
    {
        var cts = _selfCheckCts;
        _selfCheckCts = null;
        // 只 Cancel 不 Dispose：任务自己的 finally 负责释放，避免任务还在用 token 时被释放。
        try { cts?.Cancel(); } catch { }
    }

    /// <summary>
    /// 等 5 秒让链路站稳，再经本地 socks 并发探测目标站；不可用且允许时自动切同地区下一个节点。
    /// 「可用」= x.com 或 cloudflare 任一可达（并发发起、任一成功即取消另一个）；
    /// 只有 Google 通不算可用（那正是本自检要抓的「google 通但目标站死」形态）。
    /// </summary>
    private static async Task RunPostConnectSelfCheckAsync(string sessionKey, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested || !IsConnected || App.IsShuttingDown) return;

        // 会话已不是「这次连接」→ 本次自检作废，避免旧任务误判新会话。
        var cur = ConnectedNode;
        if (cur == null || !string.Equals(AppState.NodeKey(cur), sessionKey, StringComparison.Ordinal)) return;

        // 固定出口 / TUN 模式不做：TUN 下系统代理不生效，socks 探测口径也不同。
        if (FixedExitActive)
        {
            LogBus.App("连接后自检：固定出口模式，跳过目标站自检", "Debug");
            return;
        }
        if (IsTunSession)
        {
            LogBus.App("连接后自检：TUN 模式，跳过目标站自检", "Debug");
            return;
        }

        // 并发探测：x.com（8s）与 cloudflare（5s）同时发起，任一可达立刻判可用并取消另一个；
        // 两个都失败后再打一次 google（5s），仅用于日志区分「链路不通」还是「链路通但目标站不可达」。
        var (usable, xOk, cfOk, googleOk) = await RunSelfCheckProbesAsync(ct).ConfigureAwait(false);

        if (ct.IsCancellationRequested || !IsConnected) return;
        cur = ConnectedNode;
        if (cur == null || !string.Equals(AppState.NodeKey(cur), sessionKey, StringComparison.Ordinal)) return;

        if (usable)
        {
            LogBus.App($"连接后自检通过：{(xOk ? "x.com 通" : "cloudflare 通")}（并发探测，另一项已取消等待）", "Debug");
            return;
        }

        var detail = $"x.com=不通 · cloudflare=不通 · google={(googleOk ? "通（仅链路可用）" : "不通")}";

        // 不可用：先记录（Warning + IncidentLog），再按条件决定是否自动切换。
        LogBus.App($"连接后自检：目标站不可用（{detail}）", "Warning");
        IncidentLog.Record("selfcheck", "连接后自检目标站不可用", $"{cur.Name} · {detail}");

        if (_selfCheckSwitchCount >= 2)
        {
            LogBus.App($"连接后自检：本会话已自动切换 {_selfCheckSwitchCount} 次仍不可用，停止自动切换（避免连环切）", "Warning");
            return;
        }
        if (IsConnecting || IsSessionBusy)
        {
            LogBus.App("连接后自检：用户正在操作连接，本次不自动切换", "Warning");
            return;
        }

        // 候选：同地区 + 协议可走 xray（Hysteria2/TUIC 只能走 sing-box）+ 已测速（RealDelay > 0），
        // 按 RealDelay 升序取前 3 个，再排除当前节点后的第一个。
        var region = NodeGrouper.RegionOf(cur.Name);
        var top3 = AppState.Nodes
            .Where(n => n.IsValid && n.RealDelay > 0)
            .Where(n => n.Protocol is not (NodeProtocol.Hysteria2 or NodeProtocol.TUIC))
            // ★ 2026-10-23 第二批：TUN 下跳过 xray 专有能力节点（它们会被 ConnectAsync 直接拒绝）
            .Where(n => !(AppState.State.UseTun && n.RequiresXray))
            .Where(n => string.Equals(NodeGrouper.RegionOf(n.Name), region, StringComparison.Ordinal))
            .OrderBy(n => n.RealDelay)
            .Take(3)
            .ToList();
        var curKey = AppState.NodeKey(cur);
        // 排除当前节点与本次会话已经试过的节点（否则会在两个坏节点间来回切）。
        var cand = top3.FirstOrDefault(n =>
        {
            var key = AppState.NodeKey(n);
            return !string.Equals(key, curKey, StringComparison.Ordinal)
                   && !_selfCheckTried.ContainsKey(key);
        });
        if (cand == null)
        {
            // 未测速 / 无同地区候选：只记录，不切换。
            LogBus.App($"连接后自检：同地区（{NodeGrouper.RegionDisplay(region)}）没有可切换的已测速候选节点，保持当前节点", "Warning");
            IncidentLog.Record("selfcheck", "自检不可用但无同地区候选，保持当前节点", $"{cur.Name} · 地区 {region}");
            return;
        }

        // 切换前再确认一次「还是本次会话」：探测耗时期间可能已被用户/故障转移切走。
        cur = ConnectedNode;
        if (cur == null || !string.Equals(AppState.NodeKey(cur), sessionKey, StringComparison.Ordinal)) return;

        // 记下当前节点已试过，并计入本轮切换次数（最多 2 次，见方法开头）。
        _selfCheckTried.TryAdd(AppState.NodeKey(cur), 0);
        _selfCheckSwitchCount++;
        LogBus.App($"连接后自检：正在自动切换到同地区节点「{cand.Name}」（{cand.RealDelay} ms）", "Warning");
        _selfCheckSwitching = true;
        bool ok;
        string? err;
        try
        {
            // 走 ConnectAsync 内部的连接流程：SessionGate 会拒绝与用户操作并发的情况。
            (ok, err) = await ConnectAsync(cand).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ok = false;
            err = $"[{ex.GetType().Name}] {ex.Message}";
        }
        finally
        {
            _selfCheckSwitching = false;
        }

        if (ok)
        {
            AppState.SetCurrentNode(cand);
            LogBus.App($"自检不可用，已自动切换到 {cand.Name}（第 {_selfCheckSwitchCount}/2 次）", "Warning");
            IncidentLog.Record("selfcheck", "自检不可用，已自动切换节点", $"{cur.Name} → {cand.Name}（{cand.RealDelay} ms）");
        }
        else
        {
            LogBus.App($"连接后自检：自动切换失败（{err ?? "未知原因"}）", "Error");
            IncidentLog.Record("selfcheck", "自检不可用，自动切换失败", $"{cur.Name} → {cand.Name}：{err}");
        }
    }

    /// <summary>
    /// 并发探测 x.com 与 cloudflare：任一成功立刻判可用并取消另一个（最坏等 max(8s,5s)）；
    /// 两个都失败后才打一次 google，仅用于日志区分「链路不通」还是「链路通但目标站不可达」。
    /// </summary>
    private static async Task<(bool Usable, bool XOk, bool CfOk, bool GoogleOk)> RunSelfCheckProbesAsync(CancellationToken ct)
    {
        bool xOk = false, cfOk = false;
        using (var raceCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var xTask = ProbeSelfCheckTargetAsync("https://x.com/", 8, raceCts.Token);
            var cfTask = ProbeSelfCheckTargetAsync("http://cp.cloudflare.com/generate_204", 5, raceCts.Token);
            var pending = new List<Task<bool>> { xTask, cfTask };
            try
            {
                while (pending.Count > 0)
                {
                    var done = await Task.WhenAny(pending).ConfigureAwait(false);
                    pending.Remove(done);
                    if (ReferenceEquals(done, xTask)) xOk = await done.ConfigureAwait(false);
                    else cfOk = await done.ConfigureAwait(false);
                    if (xOk || cfOk)
                    {
                        // 任一成功 → 立刻判可用，取消另一个探测，不再等它超时。
                        try { raceCts.Cancel(); } catch { }
                        break;
                    }
                }
            }
            finally
            {
                // 取消后另一个任务会以 OperationCanceledException 结束；这里统一观察掉，避免未观察异常。
                try { await Task.WhenAll(xTask, cfTask).ConfigureAwait(false); } catch { }
            }
        }

        if (xOk || cfOk) return (true, xOk, cfOk, false);

        // 两个都失败后才打 google —— 只用于日志区分，不改变「可用」判定。
        var googleOk = await ProbeSelfCheckTargetAsync("https://www.google.com/generate_204", 5, ct).ConfigureAwait(false);
        return (false, false, false, googleOk);
    }

    /// <summary>通过本地 socks 探测一个目标；超时/拒绝/异常都算「不通」。</summary>
    private static async Task<bool> ProbeSelfCheckTargetAsync(string url, int timeoutSeconds, CancellationToken ct)
    {
        try
        {
            using var handler = new SocketsHttpHandler
            {
                Proxy = new System.Net.WebProxy($"socks5://127.0.0.1:{LocalPorts.Socks}"),
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromSeconds(timeoutSeconds),
                // 不跟随重定向：拿到有效 HTTP 响应就证明链路可用，跟随只会多一次请求。
                AllowAutoRedirect = false,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
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

    private static void OnCoreExited(CoreProcess? core, int code)
    {
        // 2026-10-23 审计 P1-1：只处理「当前活跃内核」的退出。
        // 无感切换后旧内核会被延迟退役，它的退出事件不能把新会话拆掉
        //（否则表现为「正用着突然断网」，而且新内核变成没人管的孤儿）。
        if (core != null && !ReferenceEquals(core, _xray))
        {
            try { LogBus.App("旧内核已退出（已被新内核替换，忽略）", "Debug"); } catch { }
            return;
        }
        if (!IsConnected) return;
        var node = ConnectedNode;
        IsConnected = false;
        ConnectedNode = null;
        // 内核退出 = 本会话结束：取消连接后自检。
        CancelPostConnectSelfCheck();
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

        CancelWatchdog();   // 内核已退出：交给现有「内核意外退出自动重启」路径，避免看门狗重复重启
        _xray = null;
        var crashTail = KernelTailText();
        LogBus.App($"内核意外退出（exit code {code}）— 已自动断开并清理系统代理"
                   + (crashTail.Length > 0 ? $"{Environment.NewLine}内核最后输出：{crashTail}" : ""), "Error");
        IncidentLog.Record("kernel-exit", "内核意外退出（exit code " + code + "）", crashTail);

        //   所以「=== 会话结束」那行日志根本不会打印 —— 内核自己退出时，
        //   那次 18.6 秒瞬断「查不出是谁断的」的原因之一。这里补上同格式的收尾记录
        //   （含时长与原因），使三条断开路径（用户/故障转移/内核退出）在日志里可区分。
        var durCore = endedAt == null ? "" : $"（时长 {(DateTime.Now - endedAt.Value).TotalMinutes:F0} 分钟）";
        LogBus.App($"=== 会话结束{durCore}（原因：内核意外退出） ===");

        // 必须先把自动重启安排出去：本方法运行在 Process.Exited 的线程池线程上，
        // 而 StateChanged 的订阅者里有直接操作 XAML 的（MainWindow.UpdateConnUi），
        // 跨线程访问 UI 会抛异常，且该异常会被 CoreProcess 的 try/catch 静默吞掉——
        if (node != null)
        {
            if (AppState.State.AutoRestartCore)
            {
                _ = TryAutoRestartCoreAsync(node);
            }
            else
            {
                LogBus.App("内核异常退出：设置里关闭了「内核异常退出后自动重启」，保持断开（可手动重连）", "Warning");
                IncidentLog.Record("kernel-exit", "内核异常退出，自动重启已关闭（设置）");
            }
        }

        NotifyStateChanged();
    }

    /// 状态变更通知。订阅者包含直接操作 XAML 的 UI 回调，而本类多处从后台线程
    /// （如 Process.Exited）调用它 —— 跨线程碰 UI 会抛异常，且该异常会被
    /// 因此这里：① 必要时 marshal 到 UI 线程；② 每个订阅者单独 try/catch，互不牵连；③ 失败留痕不再静默。
    public static Microsoft.UI.Dispatching.DispatcherQueue? UiDispatcher;

    /// <summary>
    /// 启动前用 mihomo 自己的 `-t -f &lt;yaml&gt;` 做配置校验（等价 xray -test / sing-box check）。
    /// 失败时会带出内核输出的前几行，用户能直接看出是订阅 YAML 的哪一段不合法。
    /// 校验器本身跑不起来（缺 exe / 超时）不阻断启动——与其它内核预检一致的 fail-open 取向。
    /// </summary>
    private static (bool Ok, string Message) RunMihomoCheck(string yamlPath)
    {
        try
        {
            var exe = Path.Combine(NeoPaths.RuntimeDir, "mihomo.exe");
            if (!File.Exists(exe)) return (true, "mihomo 不存在，跳过校验");
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(yamlPath) ?? NeoPaths.RuntimeDir,
            };
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(yamlPath);
            using var proc = Process.Start(psi);
            if (proc == null) return (true, "mihomo 校验启动失败，跳过校验");
            var so = proc.StandardOutput.ReadToEnd();
            var se = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(15000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return (true, "mihomo 校验超时，跳过校验");
            }
            if (proc.ExitCode == 0) return (true, "");
            var tail = string.Join(" | ", (se + "\n" + so).Split('\n')
                .Select(x => x.Trim()).Where(x => x.Length > 0).TakeLast(3));
            return (false, tail.Length > 0 ? tail : $"mihomo 校验失败（退出码 {proc.ExitCode}）");
        }
        catch (Exception ex)
        {
            return (true, "mihomo 校验无法执行：" + ex.Message);
        }
    }

    /// <summary>列出该节点用到的 xray 专有能力（用于 TUN 冲突提示与日志，口径与 NodeItem.RequiresXray 一致）。</summary>
    private static string XrayOnlyReason(NodeItem n)
    {
        var list = new List<string>();
        var net = (n.Network ?? "").ToLowerInvariant();
        if (net is "kcp" or "mkcp") list.Add("mKCP 传输");
        if (net is "xhttp") list.Add("xhttp 传输");
        if (!string.IsNullOrEmpty(n.VlessEncryption) && !n.VlessEncryption.Equals("none", StringComparison.OrdinalIgnoreCase))
            list.Add("VLESS 后量子加密");
        if (!string.IsNullOrEmpty(n.Mldsa65Verify)) list.Add("REALITY pqv 校验");
        if (!string.IsNullOrEmpty(n.VerifyPeerCertByName)) list.Add("证书名校验 vcn");
        return list.Count == 0 ? "xray 专有能力" : string.Join(" / ", list);
    }

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

    // ---- 内核看门狗（兜底）----
    // 针对「内核死掉不重启」的痛点：连接成功后每 20s 主动检查
    //   ① 10808/10809 是否仍监听、② 内核进程 IsRunning、③ TUN 网卡是否还在、
    //   ④ xray stats API（10085）是否连续 60s 无响应（卡死）。
    // 连续失败 2 次 → 复用 TryAutoRestartCoreAsync；重启失败或连续触发 3 次 → 进入安全模式。
    private static CancellationTokenSource? _watchdogCts;
    private const int WatchdogIntervalSeconds = 20;
    private const int WatchdogMaxTriggers = 3;
    private static int _watchdogTriggerCount;   // 跨自动重启保留；会话真正结束/进入安全模式时清零
    private static bool _watchdogRestarting;    // 看门狗发起的重启中（Disconnect 不清零触发计数）

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
                    NotifyKernelGaveUp($"内核连续 {_earlyCrashCount} 次启动即崩溃");
                    return;
                }
                _coreRestartAttempts++;
                if (_coreRestartAttempts > MaxCoreRestartAttempts)
                {
                    LogBus.App($"内核已连续异常退出 {_coreRestartAttempts} 次：已停止自动重启，请手动重连或检查内核日志", "Error");
                    NotifyKernelGaveUp($"内核已连续异常退出 {_coreRestartAttempts} 次");
                    return;
                }
                var retryDelay = TimeSpan.FromSeconds(5 * _earlyCrashCount);
                LogBus.App($"内核启动后立即崩溃，{retryDelay.TotalSeconds:F0} 秒后重试（第 {_earlyCrashCount}/{MaxEarlyCrashAttempts} 次）", "Warning");

                await Task.Delay(retryDelay, cts.Token);
                if (cts.IsCancellationRequested || IsConnected) return;
                if (ReferenceEquals(_coreRestartCts, cts)) { try { cts.Dispose(); } catch { } _coreRestartCts = null; }

                var (rok, rerr) = await ConnectAsync(node);
                if (rok) { _earlyCrashCount = 0; LogBus.App($"内核已自动重启并恢复连接（{node.Name}）", "Warning"); IncidentLog.Record("kernel-restart", "内核已自动重启并恢复连接", node.Name); }
                else { LogBus.App($"内核自动重启失败：{rerr}", "Error"); IncidentLog.Record("kernel-restart", "内核自动重启失败", rerr); }
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
                NotifyKernelGaveUp($"内核已连续异常退出 {_coreRestartAttempts} 次");
                return;
            }

            _coreRestartAttempts++;
            var delay = TimeSpan.FromSeconds(_coreRestartAttempts switch { 1 => 3, 2 => 10, _ => 30 });
            LogBus.App($"内核异常退出，{delay.TotalSeconds:F0} 秒后自动重启（第 {_coreRestartAttempts}/{MaxCoreRestartAttempts} 次）", "Warning");

            await Task.Delay(delay, cts.Token);
            if (cts.IsCancellationRequested || IsConnected) return;   // 用户已手动连上或已断开

            // 交接给 ConnectAsync：它内部会调 Disconnect()，若 _coreRestartCts 仍指向本 cts 会被误取消
            if (ReferenceEquals(_coreRestartCts, cts)) { try { cts.Dispose(); } catch { } _coreRestartCts = null; }

            var (ok, err) = await ConnectAsync(node);
            if (ok) { LogBus.App($"内核已自动重启并恢复连接（{node.Name}）", "Warning"); IncidentLog.Record("kernel-restart", "内核已自动重启并恢复连接", node.Name); }
            else { LogBus.App($"内核自动重启失败：{err}", "Error"); IncidentLog.Record("kernel-restart", "内核自动重启失败", err); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogBus.App($"内核自动重启异常：[{ex.GetType().Name}] {ex.Message}", "Error");
        }
        finally
        {
            if (ReferenceEquals(_coreRestartCts, cts)) { try { cts.Dispose(); } catch { } _coreRestartCts = null; }
            _coreRestartInProgress = false;
        }
    }

    // ---- 内核看门狗实现 ----
    private static void StartWatchdog(NodeItem node)
    {
        CancelWatchdog();
        var cts = new CancellationTokenSource();
        _watchdogCts = cts;
        _ = Task.Run(async () =>
        {
            try { await WatchdogLoopAsync(node, cts.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { LogBus.App($"内核兜底：看门狗异常退出：[{ex.GetType().Name}] {ex.Message}", "Error"); }
            finally
            {
                if (ReferenceEquals(_watchdogCts, cts)) _watchdogCts = null;
                try { cts.Dispose(); } catch { }
            }
        });
    }

    private static void CancelWatchdog()
    {
        var cts = _watchdogCts;
        _watchdogCts = null;
        try { cts?.Cancel(); } catch { }
    }

    private static async Task WatchdogLoopAsync(NodeItem node, CancellationToken ct)
    {
        var fails = 0;
        var statsFails = 0;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(WatchdogIntervalSeconds), ct);
            if (ct.IsCancellationRequested || !IsConnected || App.IsShuttingDown) return;
            // 2026-10-23 审计 P1-5：无感切换/换节点后，旧看门狗必须退出 ——
            // 否则它仍拿旧 node 触发自动重启，会把用户切回已经换掉的节点。
            if (!ReferenceEquals(ConnectedNode, node)) return;
            if (_watchdogRestarting) return;   // 已在本看门狗发起的重启流程里

            var why = "";
            var processOk = _xray != null && _xray.IsRunning;
            if (!processOk) why = "内核进程未运行";
            else if (PortFree(LocalPorts.Socks) || PortFree(LocalPorts.Http))
                why = $"本地 {LocalPorts.Socks}/{LocalPorts.Http} 端口未监听";
            else if (_tunSessionActive && !HasTunAdapter())
                why = "TUN 网卡 neo-tun 不存在";
            else if (_naive != null && !_naive.IsRunning)
                why = "naive 辅助进程未运行";

            // stats API：仅 xray 有 10085（sing-box 用 clash_api，不按这个口径判卡死）。
            // 用现有 XrayStats.Query（xray api statsquery）探活，3s 超时；连续 3 次（约 60s）无响应 = 卡死。
            var statsKill = false;
            if (why.Length == 0 && CoreKind == "xray")
            {
                if (XrayStats.Query() != null) statsFails = 0;
                else
                {
                    statsFails++;
                    LogBus.App($"内核兜底：stats API 无响应（连续 {statsFails}/{WatchdogMaxTriggers} 次，约 {statsFails * WatchdogIntervalSeconds}s）", "Warning");
                    if (statsFails >= WatchdogMaxTriggers)
                    {
                        statsKill = true;
                        why = "stats API 连续 60 秒无响应（内核卡死）";
                    }
                }
            }
            else if (why.Length > 0 || CoreKind != "xray")
            {
                statsFails = 0;
            }

            if (why.Length == 0)
            {
                fails = 0;
                continue;
            }

            fails++;
            LogBus.App($"内核兜底：检测到异常（{why}），连续 {fails}/2 次", "Warning");
            if (!statsKill && fails < 2) continue;

            _watchdogTriggerCount++;
            IncidentLog.Record("watchdog", statsKill ? "内核无响应" : "内核看门狗触发",
                $"{why}（第 {_watchdogTriggerCount}/{WatchdogMaxTriggers} 次）");
            LogBus.App($"内核兜底：{why}，开始自动重启内核（第 {_watchdogTriggerCount}/{WatchdogMaxTriggers} 次）", "Warning");

            if (_watchdogTriggerCount >= WatchdogMaxTriggers)
            {
                EnterSafeMode($"看门狗连续触发 {_watchdogTriggerCount} 次（最后原因：{why}）");
                return;
            }

            if (_coreRestartInProgress)
            {
                LogBus.App("内核兜底：已有内核自动重启在进行，看门狗让行", "Warning");
                return;
            }

            _watchdogRestarting = true;
            try { await TryAutoRestartCoreAsync(node); }
            catch (Exception ex) { LogBus.App($"内核兜底：看门狗发起自动重启异常：[{ex.GetType().Name}] {ex.Message}", "Error"); }
            finally { _watchdogRestarting = false; }

            if (ct.IsCancellationRequested || App.IsShuttingDown) return;   // 重启期间用户已断开/退出：不判失败
            if (_coreRestartInProgress)
            {
                LogBus.App("内核兜底：看门狗已让行给现有自动重启", "Warning");
                return;
            }
            if (!IsConnected) EnterSafeMode($"看门狗自动重启后仍未连接（最后原因：{why}）");
            return;   // 成功重启后 ConnectAsync 已启动新看门狗，本循环结束
        }
    }

    private static void EnterSafeMode(string reason)
    {
        _watchdogTriggerCount = 0;
        try
        {
            var st = AppState.State;
            st.SafeModePrevTun = st.UseTun;
            st.SafeModePrevFixedExit = st.FixedExitEnabled;
            st.SafeModeAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            st.UseTun = false;
            st.FixedExitEnabled = false;
            AppState.Save();
            AppState.SafeModeActive = true;
        }
        catch (Exception ex)
        {
            LogBus.App($"内核兜底：写入安全模式设置失败：[{ex.GetType().Name}] {ex.Message}", "Error");
        }

        try { Disconnect("内核兜底：进入安全模式"); } catch { }
        LogBus.App($"内核兜底：已进入安全模式（原因：{reason}）—— TUN 与固定出口已关闭，请检查内核日志/配置后在设置页按需重新开启", "Error");
        IncidentLog.Record("watchdog", "看门狗进入安全模式", reason);

        // ★ 2026-10-23 第二批（F9）：内核兜底必须让用户看见。
        //   旧实现只写日志 + 状态字段：窗口在托盘里时用户完全无感（常见客户端的死穴正是「内核死了没人知道」）。
        try { NotifyStateChanged(); } catch { }
        try { UserNotice?.Invoke($"内核连续异常，已进入安全模式（TUN/固定出口已关闭）：{reason}"); } catch { }
    }
    /// 解析固定出口的出口节点：优先用户手动指定的键，否则自动选延迟最好的非 REALITY 节点。
    private static NodeItem? ResolveFixedExitNode()
    {
        var cands = AppState.Nodes.Where(XrayConfigBuilder.CanBeFixedExit).ToList();
        if (cands.Count == 0) return null;
        var key = AppState.State.FixedExitExitKey ?? "";
        if (key.Length > 0)
        {
            var hit = cands.FirstOrDefault(n => string.Equals(AppState.NodeKey(n), key, StringComparison.Ordinal));
            if (hit != null) return hit;
        }
        return cands
            .OrderBy(n => n.RealDelay > 0 ? n.RealDelay : int.MaxValue)
            .First();
    }

    /// 选入口节点：排除出口本身，按「已测速优先 → 延迟低」排序，取前 count 个（0 = 全部）。
    private static List<NodeItem> PickFixedExitEntries(NodeItem exit, int count)
    {
        var exitKey = AppState.NodeKey(exit);
        var pool = AppState.Nodes
            .Where(n => n.IsValid && !string.Equals(AppState.NodeKey(n), exitKey, StringComparison.Ordinal))
            .Where(n => n.Network is not ("h2" or "quic"))
            .Where(n => n.Protocol is NodeProtocol.VMess or NodeProtocol.VLESS or NodeProtocol.Trojan or NodeProtocol.Shadowsocks)
            // ★ 2026-10-23 第二批：xray 链式出站要求每个 hop 都能被 xray 承载
            //   （ss-plugin / 明文 vless 只有 sing-box 支持，混进链里会让整份配置被拒）
            .Where(n => !n.RequiresSingBox)
            .OrderByDescending(n => n.RealDelay > 0)
            .ThenBy(n => n.RealDelay > 0 ? n.RealDelay : int.MaxValue)
            .ToList();
        if (count <= 0) count = pool.Count;
        // 上限 8：入口本质是并发连接数，开太多容易被商家按「设备/并发超限」限制。
        return pool.Take(Math.Clamp(count, 1, 8)).ToList();
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

    /// 自动补测的轮数（在冷启动热身之后执行）。
    /// 3 轮取最优：兼顾抖动；冷启动的 DNS/TCP/TLS 成本由前面 1 次丢弃探测吃掉，不计入显示值。
    private const int AutoMeasureAttempts = 3;

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

                // 冷启动虚高的根因：连上后的第一次探测包含 DNS（223.5.5.5 冷查询，实测 401ms）、
                // TCP、TLS 冷启动成本，一次往返被算进「节点延迟」。因此先做 1 次**丢弃的预热探测**，
                // 把这部分一次性成本吃掉；显示值只取热身之后 3 轮里的最优。
                var cold = await RealDelayTester.PingViaSocksAsync(
                    LocalPorts.Socks,
                    target: "https://x.com/",
                    timeoutMs: 12000,   // 冷启动放宽超时，避免把「第一次慢」误判成失败
                    rounds: 1,
                    ct: CancellationToken.None);
                if (!IsConnected) return;
                if (cold < 0)
                {
                    // 热身全超时：**不写 -1**（沿用「不写假超时」原则），保持 0 未测速，下次连接再试。
                    LogBus.App($"冷启动探测失败（全超时，保持未测速）：{node.Name}", "Warning");
                    return;
                }
                LogBus.App($"冷启动探测：{cold} ms（仅用于热身）");

                // 走「已连接的代理」测，而不是另起临时内核（另起进程任一步慢就误判 -1）。
                var best = await RealDelayTester.PingViaSocksAsync(
                    LocalPorts.Socks,
                    rounds: AutoMeasureAttempts,
                    ct: CancellationToken.None);

                if (!IsConnected) return;

                if (best > 0)
                {
                    node.RealDelay = best;
                    DelayCache.Set(node, best);
                    LogBus.App($"节点延迟（热）：{best} ms · {node.Name}");
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

    /// <summary>
    /// 连接成功后 fire-and-forget：经现有本地 socks 链路各请求一次 x.com 与 cp.cloudflare.com，
    /// 预热 DNS 缓存与 TCP/TLS 握手（不新开进程、不写任何测速结果，失败静默）。
    /// </summary>
    private static async Task WarmupDnsAsync()
    {
        try
        {
            foreach (var url in new[] { "https://x.com/", "http://cp.cloudflare.com/generate_204" })
            {
                if (!IsConnected) return;
                try
                {
                    await RealDelayTester.PingViaSocksAsync(
                        LocalPorts.Socks,
                        target: url,
                        timeoutMs: 12000,
                        rounds: 1,
                        ct: CancellationToken.None);
                }
                catch { }
            }
            LogBus.App("DNS/TLS 预热完成（x.com / cp.cloudflare.com）", "Debug");
        }
        catch (Exception ex)
        {
            LogBus.App($"DNS/TLS 预热失败（不影响连接）：[{ex.GetType().Name}] {ex.Message}", "Debug");
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

    // 历史：多节点并发已移除，sing-box balancer 能力探测随之删除 ——
    // 内核固定为官方 1.14.3，不再需要自编译 balancer 补丁，也不再写 singbox-balancer-probe.json。

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

    /// <summary>sing-box 内核 exe 被替换后调用，让下一次读取重新探测版本。
    /// 不重置的话，更新完内核界面会一直显示旧版本号。</summary>
    public static void ResetSingBoxVersion()
    {
        _singBoxVersion = null;
        _singBoxVersionProbed = false;
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
