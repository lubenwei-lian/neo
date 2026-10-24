using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Neo.Core;
using Neo.Core.Models;
using Neo.Core.Parsing;

namespace Neo.App.Services;

public static class AppState
{
    private static string StatePath => Path.Combine(NeoPaths.DataDir, "state.json");
    private static string CachePath => Path.Combine(NeoPaths.DataDir, "subscription.cache.txt");
    private static string SubCacheJsonPath => Path.Combine(NeoPaths.DataDir, "subscription-cache.json");

    public sealed class SubItem
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public bool Enabled { get; set; } = true;

        public int NodeCount { get; set; }
        public string? TrafficInfo { get; set; }
        public long SubUpload { get; set; }
        public long SubDownload { get; set; }
        public long SubTotal { get; set; }
        public long SubExpireUnix { get; set; }
        public long LastFetchUnix { get; set; }
    }

    public sealed class PersistedState
    {
        public string? SubscriptionUrl { get; set; }
        public string? SubscriptionName { get; set; }
        public string? TrafficInfo { get; set; }
        public string? ThemeMode { get; set; }

        public long SubUpload { get; set; }
        public long SubDownload { get; set; }
        public long SubTotal { get; set; }
        public long SubExpireUnix { get; set; }
        public string? LastNodeName { get; set; }

        /// 上次选中节点的稳定身份键（见 <see cref="NodeKey"/>）。
        public string? LastNodeKey { get; set; }
        public bool AutoConnect { get; set; }

        public string ProxyMode { get; set; } = "rule";
        public bool BypassPrivate { get; set; } = true;
        public bool BypassCn { get; set; } = true;
        public bool BlockAds { get; set; } = true;
        public bool AllowLan { get; set; } = false;
    // Info 级别的连接/切换/测速/更新日志都会落盘；Debug 仍只在排查时手动开启。
    public string LogLevel { get; set; } = "info";

    /// <summary>
    /// 传给内核（xray/sing-box/mihomo）的日志级别。
    /// 和界面日志分开：界面日志要「全部记录」，但内核 Info 是逐连接打印的，
    /// 若跟着开 info 会一天写几十万行、日志文件暴涨（实测一次连接就十几条）。
    /// 只有用户显式选 debug 时才让内核也开 debug。
    /// </summary>
    public string KernelLogLevel => LogLevel is "debug" ? "debug" : "warning";

        /// 配置迁移版本号：用于「只对老配置执行一次」的默认值修正。
        /// 0 = 迁移前的旧配置。新增迁移时递增此值并在 Load 里补对应分支。
        public int ConfigVersion { get; set; }
        public bool MinimizeToTray { get; set; } = true;

        /// <summary>启动后直接最小化到托盘（开机自启常配此项；默认关）。</summary>
        public bool StartMinimized { get; set; } = false;
        public bool CompactLayout { get; set; } = false;

        public string DnsServer { get; set; } = "223.5.5.5";

        public string Language { get; set; } = "zh-CN";

        public bool UseTun { get; set; } = false;

        /// <summary>
        /// 一律自动回退 C# 构建，不影响连接。默认关。
        /// </summary>
        public bool UseRustConfig { get; set; } = false;

        public int WinX { get; set; } = int.MinValue;
        public int WinY { get; set; } = int.MinValue;
        public int WinW { get; set; } = 1280;
        public int WinH { get; set; } = 800;

        /// <summary>
        /// 最大化退出 → 下次仍最大化；手动还原并调整过窗口 → 记住还原后的位置/尺寸。
        /// 注意：最大化时会话里 WinX/WinY/WinW/WinH 保存的是**还原态**边界，
        /// 这样用户点「还原」不会得到一个铺满屏幕的窗口。
        /// </summary>
        public bool StartMaximized { get; set; } = true;
        public bool AutoUpdate { get; set; } = true;

        public bool FailoverEnabled { get; set; } = true;

        /// 固定出口（多入口加速）：出口固定为一个节点，其他节点作为入口链路给它叠加带宽。
        /// 仅在非 TUN（xray）下生效。
        public bool FixedExitEnabled { get; set; } = false;

        /// 固定出口的出口节点稳定键（见 <see cref="NodeKey"/>）；空 = 自动挑延迟/吞吐最好的非 REALITY 节点。
        public string FixedExitExitKey { get; set; } = "";

        /// 入口链路数量：0 = 全部可用入口。
        public int FixedExitEntryCount { get; set; } = 6;

        /// 自动修复系统代理：已连接时系统代理被关掉（且原设置指向本机）则自动恢复。
        /// 默认开；如果你会同时用别的代理软件，可以关掉它，避免互相覆盖。
        public bool AutoRepairSystemProxy { get; set; } = true;

        /// 内核异常退出后是否自动重启。默认开；关掉后内核崩溃只断开、等你手动重连。
        public bool AutoRestartCore { get; set; } = true;

        /// 安全模式记录的「进入前」两项设置，配合 SafeModeAt 让用户一键恢复。
        public bool SafeModePrevTun { get; set; }
        public bool SafeModePrevFixedExit { get; set; }
        /// 上次进入安全模式的时间；空 = 没有待恢复的安全模式记录。
        public string SafeModeAt { get; set; } = "";

        public bool AutoSpeedTest { get; set; } = true;

        /// 启动后自动测一次全部节点的真连延迟（与节点页「真连测速」同一套实现），
        /// 由 设置 → 通用 →「启动自动测速」控制。
        /// 默认关：68 个节点全量真连在热点/蜂窝下要花 ~19–70 秒，期间占满带宽、
        ///   还会把「大部分节点超时」的中间态显示给你（实际只是慢），
        ///   对「稳定优先」是净负担。需要时在设置里随时打开。
        public bool StartupSpeedTest { get; set; } = false;

        /// 节点卡片显示实测出口信息（真实国家 + IP）。需要「位置检测」的结果，
        /// 打开后节点页会自动在后台检测一次。
        public bool ShowRealIp { get; set; } = false;

        /// 节点页「真连测速」开关：false（默认）= 用 TCP 握手测延迟（TCP 握手风格，快）；
        /// true = 走真实连接（慢但能判断是否真的能访问网站）。
        /// 关闭时节点页隐藏「真连测速」按钮、把「快速测延迟」升为主按钮；卡片主徽标显示 TCP 延迟。
    //   实测美国节点 TCP 68～112ms、真实代理 280～1200ms，默认按 TCP 排序会把用户
    //   引到「数字最好看但实际最慢」的节点上。默认用真连，快速测延迟仅作连通性参考。
    public bool RealDelayTestEnabled { get; set; } = true;

        /// <summary>
        /// 「检查预发布内核」：true 时 xray 更新会把 GitHub 上标记为 prerelease 的版本也算进候选。
        /// 默认 false（稳定优先）。sing-box / mihomo 的上游把 alpha 单列一条发布线，
        /// 且本项目只用它们的稳定发行版，因此该开关对这两个内核不生效。
        /// </summary>
        public bool CheckKernelPreRelease { get; set; } = false;

        /// <summary>本地 SOCKS 入站端口（默认 21808，可在 设置→内核 修改；改后下次连接生效）。</summary>
        public int SocksPort { get; set; } = Neo.Core.LocalPorts.DefaultSocks;

        /// <summary>本地 HTTP 入站端口（默认 21809）。</summary>
        public int HttpPort { get; set; } = Neo.Core.LocalPorts.DefaultHttp;

        /// <summary>
        /// 「协议 → 内核」手动映射（对标成熟客户端的 CoreTypeItem）：
        /// 键 = NodeProtocol 名称（VLESS/VMess/...），值 = "xray" / "sing-box"；缺省 = 自动按能力选。
        /// 只在能力允许时生效（把 hy2 指到 xray 会被静默回退 auto，并留日志）。
        /// </summary>
        public Dictionary<string, string> CoreMap { get; set; } = new();

        /// <summary>上次更新分流数据（geoip.dat / geosite.dat / Country.mmdb / rule-set）的时间
        /// （本地时间字符串，空 = 从未通过本程序更新过）。仅用于设置页展示。</summary>
        public string GeoUpdatedAt { get; set; } = "";

        public int AutoUpdateHours { get; set; } = 12;

        public bool AutoCheckUpdate { get; set; } = true;

        public long LastUpdateCheckUnix { get; set; }
        public List<RouteRule> CustomRules { get; set; } = new();

        public List<string> ManualLinks { get; set; } = new();

        public List<SubItem> Subscriptions { get; set; } = new();

        // 游戏直连保护：按进程名直连，游戏流量不经节点（防节点拒绝游戏 UDP）
        public bool GameDirectEnabled { get; set; } = true;
        public List<string> GameDirectProcesses { get; set; } = new();
    }
    public static PersistedState State { get; private set; } = new();

    /// 本次启动是否走了安全模式（主窗口会据此提示一次）。不落盘。
    public static bool SafeModeActive { get; set; }
    /// <summary>游戏直连进程名单（内置 + 用户自定义合并）</summary>
    public static System.Collections.Generic.List<string> GameDirectProcessList()
    {
        var list = new System.Collections.Generic.List<string>
        {
            "TslGame.exe",      // PUBG
            "cs2.exe",          // CS2
            "left4dead2.exe",   // L4D2
            "HTGame.exe",       // NTE
            "Client-Win64-Shipping.exe",
            "Sugar.exe",        // DeltaForce
        };
        foreach (var p in State.GameDirectProcesses)
        {
            var v = (p ?? "").Trim();
            if (v.Length > 0 && !list.Contains(v, StringComparer.OrdinalIgnoreCase)) list.Add(v);
        }
        return list;
    }
    public static List<NodeItem> Nodes { get; private set; } = new();
    private static List<NodeItem> _subNodes = new();
    public static NodeItem? CurrentNode { get; set; }
    public static DateTime? LastFetchLocal { get; private set; }

    public static event Action? NodesChanged;

    public static event Action? UiSettingsChanged;

    public static void NotifyUiSettingsChanged() => UiSettingsChanged?.Invoke();

    /// 供外部（如连接后自动补测延迟）请求刷新节点相关界面。
    /// C# 事件只能在声明类内部触发，因此必须留这个公开入口。
    public static void NotifyNodesChanged() => NodesChanged?.Invoke();

    /// 为什么必须这样：RefreshSubscriptionAsync 是由 MainWindow 的 Task.Run 在**后台线程**上跑的
    /// （那里刻意不 await，避免启动被网络阻塞）。它末尾会走 RebuildNodes() -> NodesChanged，
    /// 而订阅者里 MainWindow.UpdateSidebar / NodesPage.RebuildList / SubsPage.UpdateDisplay
    /// 全都是直接写 XAML 的 —— 后台线程碰 XAML 会抛 COMException。
    ///   「订阅 [...] 更新成功 · 68 个节点」（AppState.cs 内）
    ///  又打印「启动时订阅刷新失败：COMException」（MainWindow 的 catch），
    /// 造成「明明成功却报失败」，且界面节点数/订阅数不刷新。
    /// 做法与 ConnectionService.NotifyNodesChangedSafe 一致：已在 UI 线程则直接调，
    /// 否则 TryEnqueue 回去；退出中（DispatcherQueue 正在销毁）直接放弃，避免 fail-fast。
    /// 用「按需投递」而不是把所有订阅者都改掉 —— 本来就在 UI 线程的订阅者不必多绕一圈。
    private static void InvokeOnUiThread(Action? a)
    {
        if (a == null) return;
        if (App.IsShuttingDown) return;

        var dq = ConnectionService.UiDispatcher;
        if (dq == null || dq.HasThreadAccess) { a(); return; }
        try { dq.TryEnqueue(() => { if (!App.IsShuttingDown) a(); }); }
        catch { /* DispatcherQueue 正在销毁：放弃投递，不再直接调（避免跨线程写 XAML） */ }
    }

    /// <summary>RebuildNodes 用的安全通知（见 InvokeOnUiThread 注释）。</summary>
    private static void NotifyNodesChangedSafe() => InvokeOnUiThread(() => NodesChanged?.Invoke());

    /// <summary>订阅刷新完成后的安全通知。</summary>
    private static void NotifySubscriptionUpdatedSafe() => InvokeOnUiThread(() => SubscriptionUpdated?.Invoke());

    public static event Action? SubscriptionUpdated;

    /// 加载配置。一次进程只真正执行一次（<see cref="_loadedOnce"/> 幂等）。
    ///   出厂默认对象 → 43 个字段全丢」。我在隔离目录用带 MARKER 指纹的 v0 配置实跑 AppState.Load()，
    ///   结果**全部用户数据都活着**（SubscriptionUrl / LastNodeName / DnsServer / WinW / WinH /
    ///   ProxyMode / Subscriptions / CustomRules / ManualLinks 逐项保留），
    ///   两个迁移也都生效了 → 落盘序列化的是「刚读出来的那份对象」，不是静态初始化器。**该机制不成立。**
    ///   原因：`State = LoadStateWithRecovery();` 本来就排在 `MigrateConfig()` **之前**
    ///   `state.json` 合法但**内容残缺**时（例：`{ "ConfigVersion": 0, "LogLevel": "info" }`），
    ///   TryReadState 视为「读成功」→ 紧接 `TryBackupLastGood()` 把这份残缺内容**复制成 .lastgood**
    /// 本次三处加固（均在下面代码里可核对）：
    /// ① 结构上锁死「赋值 → 迁移 → 落盘」的次序：迁移块不再自己调 Save()（改为返回是否发生迁移），
    ///    落盘统一挪到本方法末尾、State 赋值之后。哪怕将来有人调整语句顺序，
    ///    也不可能再出现「Save() 序列化到非目标对象」的形态。
    /// ② 落盘只在配置完整时发生（<see cref="LoadOutcome.Complete"/>）：残缺文件绝不写盘、
    ///    更绝不拿去刷新 .lastgood，避免把「恢复失败」升级成「永久数据丢失」。
    /// ③ 并发：Load() 与 Save() 共用同一把 <see cref="SaveGate"/>。
    ///    之前只护写、不护「读 + 整体替换 State」：故障转移后台循环每 30 秒经 SetCurrentNode → Save()
    ///    落盘一次，若它恰落在 Load() 替换 State 的窗口内，就会把**替换前的旧对象**写盘
    ///    —— 内存是新值、磁盘是旧值，用户的设置改动在下次启动时静默回滚。
    ///    共用锁之后，任何 Save() 要么整体发生在替换之前、要么整体发生在之后，该交错不再可能。
    /// <summary>把持久化的本地端口写入运行期真源（无效/旧配置回退默认值）。</summary>
    public static void ApplyLocalPorts()
    {
        try
        {
            var err = Neo.Core.LocalPorts.TryApply(State.SocksPort, State.HttpPort);
            if (err == null) return;
            LogBus.App($"本地端口配置无效（{State.SocksPort}/{State.HttpPort}）：{err} —— 已回退默认 {Neo.Core.LocalPorts.DefaultSocks}/{Neo.Core.LocalPorts.DefaultHttp}", "Warning");
            Neo.Core.LocalPorts.Reset();
            State.SocksPort = Neo.Core.LocalPorts.DefaultSocks;
            State.HttpPort = Neo.Core.LocalPorts.DefaultHttp;
        }
        catch { Neo.Core.LocalPorts.Reset(); }
    }

    /// <summary>把持久化的协议→内核映射同步给 Core 层的路由表（连接/测速都用它）。</summary>
    public static void SyncCoreMap()
    {
        try
        {
            Neo.Core.Core.KernelRouter.Clear();
            foreach (var kv in State.CoreMap)
            {
                // 只接受已知协议名，避免脏数据让路由行为不可预期。
                if (!Enum.TryParse<NodeProtocol>(kv.Key, ignoreCase: true, out var proto)) continue;
                Neo.Core.Core.KernelRouter.Set(proto.ToString(), kv.Value);
            }
        }
        catch { }
    }

    public static void Load()
    {
        lock (SaveGate)
        {
            //   标志在**进入时**就置位，语义是「至多一次」而非「成功才计一次」。这样取舍是为了
            //   保证：MainWindow.OnRootLoaded 的兜底 `AppState.Load()`（MainWindow.xaml.cs:884）
            //   绝不会把已经加载好、且运行期已被改动的 State 再整体替换回磁盘旧值。
            //   代价：若首次加载就抛异常，后续不再重试（进程仍会以已读到的配置继续跑，
            //   且失败必有 Error 留痕，不会静默）。此处选择「宁可只读一次」是因为
            //   「重新读一次把运行期改动冲掉」正是本缺陷要消除的形态。
            if (_loadedOnce) return;
            _loadedOnce = true;

            var outcome = LoadStateWithRecovery();

            //   MigrateConfig() 只去改 State，所以 State 必须已经指向「刚读出来的那份」。
            State = outcome.State;

            bool migrated = false;
            try
            {
                migrated = MigrateConfig();

                MigrateLegacySubscription();
                RebuildSubNodesFromCaches();

                var lastUnix = State.Subscriptions.Where(s => s.Enabled).Select(s => s.LastFetchUnix).DefaultIfEmpty(0).Max();
                if (lastUnix > 0)
                    LastFetchLocal = DateTimeOffset.FromUnixTimeSeconds(lastUnix).LocalDateTime;

                MirrorLegacyFromFirst();
            }
            catch (Exception ex)
            {
                //   「设置莫名其妙变回默认」这类现象因此完全无法追查（有失败、无日志 = 看起来没发生过）。
                //   仍然不向调用者抛：Load() 在 App.OnLaunched 里、建窗口之前调用，抛出去会直接崩启动。
                LogBus.App($"配置迁移/重建失败（{ex.GetType().Name} {ex.Message}）——已保留读到的配置继续启动", "Error");
            }

            //   - Complete=false：读到的是**残缺配置**，此时绝不落盘 ——
            //     否则会把残缺内容补成一份「看着完整、实则全是默认值」的 state.json，
            //     并由 Save() 顺带把 .lastgood 也刷成这份壳，唯一备份就此销毁且不可恢复。
            //   - 正常读出且无需迁移时也不落盘：避免每次启动都无谓改写 state.json / .lastgood。
            try
            {
                if (outcome.Complete && (migrated || outcome.Recovered))
                    Save();

                //   直接穿出 Load()（调用方只有 `try { Load(); } catch { }`，等于静默炸掉一半初始化）。
                //   这里并入 try：失败留 Error 痕，且不会让启动流程崩掉。
                RebuildNodes();

                //   旧 state.json 没有 LastNodeKey → 自然走名字回退，升级后行为不变。
                if (!string.IsNullOrWhiteSpace(State.LastNodeKey))
                    CurrentNode = Nodes.FirstOrDefault(n => NodeKey(n) == State.LastNodeKey);
                if (CurrentNode == null && !string.IsNullOrWhiteSpace(State.LastNodeName))
                    CurrentNode = Nodes.FirstOrDefault(n => n.Name == State.LastNodeName);
                if (CurrentNode == null && Nodes.Count > 0)
                    CurrentNode = Nodes[0];

                SyncCoreMap();
                ApplyLocalPorts();
            }
            catch (Exception ex)
            {
                LogBus.App($"节点列表重建失败（{ex.GetType().Name} {ex.Message}）——配置本身已加载，节点列表可能为空", "Error");
            }
        }
    }

    /// 只对老配置执行一次的默认值修正。**只改内存，不落盘**（落盘由 <see cref="Load"/> 末尾统一负责）。
    /// 必须显式迁移：state.json 里已持久化的值会覆盖新默认值，光改默认值对老用户无效。
    /// 绑成了一个不受调用方控制的副作用 —— 只要 State 当时还没指向读出来的那份对象
    ///（见 Load 的时序说明），Save() 就会把**另一份出厂默认对象**写盘。
    /// 现在改为返回「本次是否发生了迁移」，由 Load() 在 State 赋值完成、且读到的是完整配置时统一落盘。
    /// <returns>本次是否真的改动了配置（决定 Load 是否需要落盘）。</returns>
    private static bool MigrateConfig()
    {
        bool changed = false;

        if (State.ConfigVersion < 1)
        {
            // v0 → v1：LogLevel 默认 info → warning。
            // 而用户日常并不需要逐连接日志。仅在用户从未改过（仍是老默认 info）时才动。
            if (string.Equals(State.LogLevel, "info", StringComparison.OrdinalIgnoreCase))
            {
                State.LogLevel = "warning";
                LogBus.App("日志级别已从「信息」调整为「警告」以减少日志体积（可在设置页改回）");
            }

            State.ConfigVersion = 1;
            changed = true;
        }

        if (State.ConfigVersion < 3)
        {
            // 背景：DnsServer 写在 state.json 里，可被手工编辑/被旧版本写坏成空串。
            // 这里在加载时统一归一，避免任何一处漏判就把软件变成「垃圾」。
            if (string.IsNullOrWhiteSpace(State.DnsServer)) { State.DnsServer = "223.5.5.5"; changed = true; }

            State.ConfigVersion = 3;
            changed = true;
        }

        return changed;
    }

    /// <summary>从磁盘加载的结果。Complete 表示「这份配置是完整读出来的」，而非容错回退产物。</summary>
    private readonly struct LoadOutcome
    {
        public LoadOutcome(PersistedState state, bool complete, bool recovered)
        {
            State = state;
            Complete = complete;
            Recovered = recovered;
        }

        public PersistedState State { get; }

        /// <summary>是否为「完整可用的配置」。false = 残缺/默认壳，**不得据此落盘、也不得拿去刷新 .lastgood**。</summary>
        public bool Complete { get; }

        /// <summary>是否为「从 .lastgood 回退恢复」。true 时需要把它固化成新的 state.json。</summary>
        public bool Recovered { get; }
    }

    /// 读取配置并做容错恢复。
    /// 原实现只要「JSON 能反序列化」就算读成功（<see cref="TryReadState"/> 对
    /// 合法但残缺的文件同样返回非 null），于是紧接着就 `TryBackupLastGood()`
    /// 把这份**残缺内容直接复制成 .lastgood** —— 唯一那份完好备份当场被销毁。
    /// .lastgood = 完整 43 字段配置）：
    ///   修复前 —— state.json 42 B → 1098 B 全默认；.lastgood 1880 B → 1098 B 全默认（备份被销毁）
    ///   修复后 —— state.json 42 B → 不变；.lastgood 1880 B → 不变（备份保住，走备份恢复）
    /// 因此判「读成功」必须同时满足「JSON 可解析」**和**「内容是一份真实完整配置」
    ///（见 <see cref="LooksLikeCompleteConfig"/>），否则一律按损坏进入备份回退。
    private static LoadOutcome LoadStateWithRecovery()
    {
        var (state, error, rawProps) = TryReadState(StatePath);
        if (state != null && LooksLikeCompleteConfig(state, rawProps))
        {
            // 只有「刚读出来的这份确实完整」才刷新备份 —— 顺序上它在读成功之后，语义正确。
            TryBackupLastGood(stateIsComplete: true);
            return new LoadOutcome(state, complete: true, recovered: false);
        }

        // 走到这里有两种情形：文件不存在（state==null 且 error==null，首次启动）
        // 或 文件存在但损坏/残缺（state==null 带 error，或 state!=null 但不完整）。
        if (state != null)
            error ??= $"内容不完整（顶层只有 {rawProps} 个字段，不像一份完整配置），按损坏处理";

        var (recovered, recoveredError, recoveredProps) = TryReadState(StatePath + ".lastgood");
        if (recovered != null && LooksLikeCompleteConfig(recovered, recoveredProps))
        {
            ArchiveCorruptState();
            LogBus.App($"state.json 加载失败（{error}）——已从上次完好备份自动恢复", "Error");
            return new LoadOutcome(recovered, complete: true, recovered: true);
        }

        // 没有任何可用备份：退默认配置。
        //   否则就是把「恢复失败」变成「永久数据丢失」。
        if (error != null)
        {
            ArchiveCorruptState();
            LogBus.App($"state.json 加载失败（{error}）——没有可用备份，本次以默认配置启动", "Error");
        }
        else if (recoveredError != null)
        {
            LogBus.App($"state.json 不存在；备用文件 .lastgood 也读取失败（{recoveredError}）——本次以默认配置启动", "Error");
        }

        return new LoadOutcome(new PersistedState(), complete: false, recovered: false);
    }

    /// 判定一份反序列化结果是否「像一份真实的完整配置」。
    /// 判据：**存在用户自建数据** —— 订阅 / 手动节点链接 / 自定义规则至少有一类非空。
    ///   但它是**内容残缺**的配置。旧代码把它当读成功、再拿它刷 .lastgood，备份就被销毁了。
    ///   没有判别力 —— 只有「用户数据是否存在」才具备判别力。
    ///   一份合法的 v0 老配置（带完整订阅）会因版本号为 0 被误判成残缺而被丢弃，
    ///   等于把「老用户升级」变成「老用户数据丢失」，比原缺陷更糟。版本号反映的是
    ///   「迁移跑没跑过」，与「内容完整不完整」无关，不能拿来当完整性判据。
    ///   会被判为「不完整」而走备份回退。此时若 .lastgood 不存在，结果是退默认配置 ——
    ///   与直接采用那份空白配置完全等价，无副作用；若 .lastgood 恰有旧的完整配置，则用户得到恢复。
    ///   两种情况都不丢数据，因此这里宁可保守。
    ///  （见 <see cref="NormalizeCollections"/>）。这里读得比那里早，必须自己也防空，
    ///   否则一个手改过的 state.json 会让启动路径抛 NRE。
    ///   原判据只看三个**用户数据集合**是否非空。后果：用户主动清空订阅/手动节点/自定义规则后，
    ///   本判据恒为 false → Save() 中 TryBackupLastGood(stateIsComplete: false) 永不刷新 .lastgood，
    ///   且反复打 Warning；此时若 state.json 损坏，会恢复出**用户已删除的旧订阅**（回魂）。
    ///   改为：只看**结构性配置字段**是否齐全（与用户数据是否为空无关）。
    ///   空集合是合法的用户状态，不该因此判定「配置不完整」。
    private static bool LooksLikeCompleteConfig(PersistedState s)
        => s != null
           //   版本号只反映「迁移跑没跑过」，与「内容完整不完整」无关；继续拿它判完整性，
           && !string.IsNullOrEmpty(s.ProxyMode)     // 代理模式
           && !string.IsNullOrEmpty(s.DnsServer)     // DNS 服务器
           && !string.IsNullOrEmpty(s.LogLevel);     // 日志级别

    /// 读取磁盘文件时使用的完整性判据：结构字段齐全 **且** 顶层 JSON 字段数够多。
    ///   `{"ConfigVersion":0,"LogLevel":"info"}` 这种空壳反序列化后，ProxyMode/DnsServer/LogLevel
    ///   三个结构字段会被类型默认值填满，于是被误判成「完整」，再把 .lastgood 刷成空壳
    ///   neo 自己序列化的 state.json 永远是全字段（40+ 个）；任何只手写/截断了几个键的文件
    ///   都会远低于阈值。这条判据与「用户数据是否为空」无关，也与 ConfigVersion 无关，
    ///   同时满足：合法 v0 老配置（全字段）→ 完整；空壳（2 个键）→ 不完整。
    /// <param name="rawProps">原始 JSON 顶层字段数（由 TryReadState 统计）。</param>
    private static bool LooksLikeCompleteConfig(PersistedState s, int rawProps)
        => LooksLikeCompleteConfig(s) && rawProps >= MinCompleteRawProps;

    /// <summary>完整配置的最小顶层字段数。neo 全量序列化约 40+ 项；低于 10 只可能是残壳/手写片段。</summary>
    private const int MinCompleteRawProps = 10;

    private static (PersistedState?, string?, int) TryReadState(string path)
    {
        try
        {
            if (!File.Exists(path)) return (null, null, 0);
            var text = File.ReadAllText(path);

            // 先统计原始 JSON 顶层字段数（给完整性判据用），再反序列化。
            // 解析失败视为损坏，交给调用方走备份回退。
            int rawProps;
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return (null, "根节点不是 JSON 对象", 0);
                rawProps = doc.RootElement.EnumerateObject().Count();
            }
            catch (JsonException jex)
            {
                return (null, "JSON 解析失败：" + jex.Message, 0);
            }

            var s = JsonSerializer.Deserialize<PersistedState>(text);
            if (s == null) return (new PersistedState(), null, rawProps);
            NormalizeCollections(s);
            return (s, null, rawProps);
        }
        catch (Exception ex) { return (null, ex.Message, 0); }
    }

    /// 反序列化后把被显式写成 `null` 的集合字段补成空集合。
    ///   **只在 JSON 里没有该字段时**才保持非 null；一旦文件里出现 `"Subscriptions": null`
    ///   （手改过的配置、第三方工具写出的配置都可能这样），反序列化结果就是 null，
    ///   随后任何 `.Count` / 遍历都抛 NullReferenceException。
    ///     · 修复前：`Load()` 在 RebuildNodes() 里 NRE（AppState.cs:605，读 State.ManualLinks）→ 进程异常退出
    ///     · 补本方法后：正常加载、不崩
    ///   读这几个集合，若不补 null 就会把该崩溃从 Load 末尾提前到 LoadStateWithRecovery，
    ///   让既有问题变得更早暴露却没有解决 —— 所以必须在这里根治。
    private static void NormalizeCollections(PersistedState s)
    {
        s.Subscriptions ??= new();
        s.ManualLinks ??= new();
        s.CustomRules ??= new();
        s.GameDirectProcesses ??= new();
    }

    /// 把当前 state.json 复制成 .lastgood（损坏时的自动恢复源）。
    /// 旧实现在「state.json 损坏 → 回退默认壳」这条路径上同样会走到备份，把那份**空壳复制成 .lastgood**，
    /// 唯一那份完好备份当场被销毁 —— 之后连自动恢复都没得救，属不可恢复的数据丢失。
    /// 是否允许刷新由调用方**显式传参**决定（而不是读一个共享标志）：
    /// 备份的合法性只取决于「此刻磁盘上那份 state.json 是否完整」，
    /// 与 State 字段赋值到哪一步无关，显式传参才不会因调用顺序变化而失效。
    /// 于是「state.json 写不进 / 备份写不进」两种失败在日志里长得一模一样（都等于没有日志）。
    /// 现在失败留痕；依旧不抛，备份不是主流程，不能因此阻断启动或保存。
    /// <param name="stateIsComplete">磁盘上这份 state.json 是否为完整配置。false = 残缺/默认壳，必须跳过。</param>
    private static void TryBackupLastGood(bool stateIsComplete)
    {
        // 残缺/默认壳一律不许覆盖备份：备份空壳 = 销毁唯一恢复源。
        if (!stateIsComplete)
        {
            LogBus.App("state.json 当前不是完整配置（可能是容错回退的默认值）——已跳过刷新 .lastgood，避免销毁唯一备份", "Warning");
            return;
        }

        try { File.Copy(StatePath, StatePath + ".lastgood", true); }
        catch (Exception ex)
        {
            LogBus.App($"state.json 备份到 .lastgood 失败（{StatePath}）：{ex.GetType().Name} {ex.Message}", "Error");
        }
    }

    private static void ArchiveCorruptState()
    {
        try
        {
            if (!File.Exists(StatePath)) return;
            File.Copy(StatePath, StatePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true);
        }
        catch { }
    }

    /// 串行化 state.json 的读写。必须加锁：调用方横跨多个线程 ——
    /// 故障转移后台循环会经 SetCurrentNode 走到这里，而 UI 线程有 20+ 处直接调 Save。
    /// 两者若同时进入，会撞同一个 state.json.tmp，一方 File.Move 抛 IOException，
    /// 又被下面的 catch 静默吞掉 → **那次设置永久丢失**（用户以为保存了，其实没有）。
    /// 原先只护「写」、不护「读 + 整体替换 State」：Load() 执行到一半时若被后台 Save() 抢先落盘，
    /// 写出的就是**替换之前的旧对象**，造成内存新值 / 磁盘旧值，用户设置下次启动静默回滚。
    /// 读写共用一把锁后，任何 Save() 要么整体发生在 State 替换之前、要么整体在其之后。
    private static readonly object SaveGate = new();
    private static readonly object SubCacheGate = new();

    /// 重复调用会再次整体替换 State，把运行期尚未落盘的改动直接丢弃，因此必须挡住。
    private static bool _loadedOnce;

    public static void Save()
    {
        lock (SaveGate)
        {
            try
            {
                SyncCoreMap();   // 保存即生效，不用重启
                ApplyLocalPorts();
                var tmp = StatePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(State, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, StatePath, true);

                //   判据取自刚写出的 State 实际形态，而不是某个缓存标志 ——
                //   保证 .lastgood 里**永远**是一份完整配置，残缺/默认壳绝不会覆盖它。
                TryBackupLastGood(stateIsComplete: LooksLikeCompleteConfig(State));
            }
            catch (Exception ex)
            {
                // 后果是真实的「关不掉 TUN」根因（{app}\data 对普通用户只读 → 所有设置都写不进磁盘）
                // 在日志里零痕迹，只剩「关掉后重启又变回开」这种无法追查的幽灵现象。
                // 现在一律留痕：Error 级别在 LogBus.Enabled 里绕过 LogLevel 门控，任何日志级别下都看得见。
                // 但**仍然不向调用者抛**：Save() 被 UI 线程 20+ 处直接调用，抛出去会直接崩界面。
                LogBus.App($"设置保存失败（{StatePath}）：{ex.GetType().Name} {ex.Message}", "Error");
            }
        }
    }

    public static string? PeekThemeMode()
    {
        try
        {
            if (!File.Exists(StatePath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(StatePath));
            return doc.RootElement.TryGetProperty("ThemeMode", out var v) ? v.GetString() : null;
        }
        catch { return null; }
    }

    private static readonly SemaphoreSlim SubRefreshGate = new(1, 1);

    /// 商家在「设备超限 / 链接被重置」时会返回占位节点：
    ///   server=127.0.0.1:6666，名字里写「使用的设备过多 / 请去官网获取新订阅链接」。
    /// 如果照常写进缓存，就会把用户原来能用的 68 个节点覆盖成 3 个本机假节点 —— 直接连不上。
    /// 因此这里必须识别并拒绝覆盖，提示用户去商家处理。
    private static bool IsProviderPlaceholder(IReadOnlyList<NodeItem> nodes, string? rawText, out string? detail)
    {
        detail = "否";
        try
        {
            var rust = Neo.Core.Bridge.RustCoreBridge.TryDetectPlaceholder(rawText ?? "", nodes);
            if (rust != null)
            {
                if (rust.Placeholder)
                {
                    detail = "是（" + (string.IsNullOrWhiteSpace(rust.Reason) ? "Rust 判定" : rust.Reason) + "）";
                    LogBus.App($"订阅占位判定（Rust）：{rust.Reason}", "Debug");
                    return true;
                }
                if (rust.Parsed) return false;
            }
        }
        catch { }

        if (nodes.Count == 0) return false;
        // 不能被当成「商家占位节点」；判断只看真实节点。
        var physical = nodes.Where(n => !n.IsClashPassthrough && !n.IsCustomConfig).ToList();
        if (physical.Count == 0) return false;

        var hints = new[] { "设备过多", "请去官网", "获取新订阅", "使用说明" };
        var local = 0;
        foreach (var n in physical)
        {
            if (n.Server is "127.0.0.1" or "::1" or "localhost") local++;
            foreach (var h in hints)
                if ((n.Name ?? "").Contains(h, StringComparison.Ordinal))
                {
                    detail = "是（节点名包含商家提示）";
                    return true;
                }
        }
        if (local == physical.Count) detail = "是（节点全部指向本机）";
        return local == physical.Count;
    }

    /// <summary>从拉取失败原因里区分 HTTP 状态与「解析失败」两类，供订阅日志展示。</summary>
    private static (string Http, string? ParseError) ClassifyFetchFailure(string? error)
    {
        var text = (error ?? "").Trim();
        var m = System.Text.RegularExpressions.Regex.Match(text, @"\bHTTP\s+(\d{3})\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success) return (m.Groups[1].Value, null);
        if (LooksLikeParseFailure(text)) return ("2xx", text);
        return ("—", null);
    }

    private static bool LooksLikeParseFailure(string text)
        => text.Contains("未解析", StringComparison.Ordinal)
        || text.Contains("解析", StringComparison.Ordinal)
        || text.Contains("格式", StringComparison.Ordinal)
        || text.Contains("网页", StringComparison.Ordinal)
        || text.Contains("SIP008", StringComparison.OrdinalIgnoreCase)
        || text.Contains("Clash", StringComparison.OrdinalIgnoreCase)
        || text.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)
        || text.Contains("订阅内容为空", StringComparison.Ordinal);

    private const string ProviderPlaceholderMessage =
        "订阅返回的是商家占位节点（常见原因：设备数量超限 / 订阅链接被重置）。已保留原有节点；请到商家官网获取新订阅链接，或释放其他设备的占用后重试。";

    public static async Task<(bool Ok, string? Error)> RefreshSubscriptionAsync()
    {
        //   本方法有**三个触发点**且都无互斥：
        //     MainWindow.xaml.cs:1174  启动路径（刻意 `_ = Task.Run` 不 await）
        //     MainWindow.xaml.cs:1214  30 分钟定时
        //     SubsPage.xaml.cs          用户点「更新」
        //   并发跑会各自 LoadSubCache → 各自 save → **后写覆盖先写**（缓存字典互相踩）。
        //   现加并发闸：拿不到就直接返回失败，不排队（避免用户点击被定时任务长时间阻塞）。
        if (!await SubRefreshGate.WaitAsync(0))
        {
            LogBus.App("订阅刷新已在运行中，本次请求跳过", "Debug");
            return (false, "订阅刷新正在进行中");
        }
        try
        {
        var enabled = State.Subscriptions.Where(s => s.Enabled).ToList();
        if (enabled.Count == 0)
            return (false, "尚未配置订阅链接");

        var cache = LoadSubCache();
        var okCount = 0;
        var errors = new List<string>();

        foreach (var sub in enabled)
        {
            var logId = SubscriptionLogService.Begin(sub.Name, SubscriptionLogService.HostOf(sub.Url), "全部刷新");
            var sw = Stopwatch.StartNew();
            var r = await SubscriptionFetcher.FetchAsync(sub.Url);
            sw.Stop();

            if (r.Success)
            {
                bool placeholder = IsProviderPlaceholder(r.Nodes, r.RawText, out var placeholderDetail);
                if (placeholder)
                {
                    LogBus.App($"订阅 [{sub.Name}]：{ProviderPlaceholderMessage}", "Error");
                    IncidentLog.Record("subscription", "订阅被商家限制，已保留原节点", ProviderPlaceholderMessage);
                    errors.Add($"{sub.Name}：{ProviderPlaceholderMessage}");
                    SubscriptionLogService.Finish(logId, "占位", "2xx", r.Nodes.Count, null, null,
                        placeholderDetail ?? "是", sw.Elapsed.TotalMilliseconds);
                    continue;
                }
                cache[sub.Id] = r.RawText ?? "";
                sub.NodeCount = r.Nodes.Count;
                sub.TrafficInfo = r.TrafficInfo;
                sub.SubUpload = r.SubUpload;
                sub.SubDownload = r.SubDownload;
                sub.SubTotal = r.SubTotal;
                sub.SubExpireUnix = r.SubExpireUnix;
                sub.LastFetchUnix = DateTimeOffset.Now.ToUnixTimeSeconds();
                okCount++;
                LogBus.App($"订阅 [{sub.Name}] 更新成功 · {r.Nodes.Count} 个节点 · 流量 {sub.TrafficInfo}");
                SubscriptionLogService.Finish(logId, "成功", "2xx", r.Nodes.Count, null, null,
                    placeholderDetail ?? "否", sw.Elapsed.TotalMilliseconds);
            }
            else
            {
                var (http, parseError) = ClassifyFetchFailure(r.Error);
                errors.Add($"{sub.Name}：{r.Error}");
                LogBus.App($"订阅 [{sub.Name}] 更新失败：{r.Error}", "Warning");
                SubscriptionLogService.Finish(logId, "失败", http, 0, r.Error, parseError,
                    "未判定", sw.Elapsed.TotalMilliseconds);
            }
        }

        SaveSubCache(cache);
        if (okCount == 0)
            return (false, string.Join("；", errors));

        RebuildSubNodesFromCaches();
        MirrorLegacyFromFirst();
        Save();
        LastFetchLocal = DateTime.Now;
        RebuildNodes();
        NotifySubscriptionUpdatedSafe();
        return (true, null);
        }
        finally { try { SubRefreshGate.Release(); } catch { } }   
    }

    public static async Task<(bool Ok, string? Error)> RefreshOneAsync(string id)
    {
        var sub = State.Subscriptions.FirstOrDefault(s => s.Id == id);
        if (sub == null) return (false, "订阅不存在");

        var logId = SubscriptionLogService.Begin(sub.Name, SubscriptionLogService.HostOf(sub.Url), "单条刷新");
        var sw = Stopwatch.StartNew();
        var r = await SubscriptionFetcher.FetchAsync(sub.Url);
        sw.Stop();

        if (!r.Success)
        {
            var (http, parseError) = ClassifyFetchFailure(r.Error);
            SubscriptionLogService.Finish(logId, "失败", http, 0, r.Error, parseError,
                "未判定", sw.Elapsed.TotalMilliseconds);
            return (false, r.Error);
        }
        if (IsProviderPlaceholder(r.Nodes, r.RawText, out var placeholderDetail))
        {
            LogBus.App($"订阅 [{sub.Name}]：{ProviderPlaceholderMessage}", "Error");
            IncidentLog.Record("subscription", "订阅被商家限制，已保留原节点", ProviderPlaceholderMessage);
            SubscriptionLogService.Finish(logId, "占位", "2xx", r.Nodes.Count, null, null,
                placeholderDetail ?? "是", sw.Elapsed.TotalMilliseconds);
            return (false, ProviderPlaceholderMessage);
        }

        var cache = LoadSubCache();
        cache[sub.Id] = r.RawText ?? "";
        SaveSubCache(cache);

        sub.NodeCount = r.Nodes.Count;
        sub.TrafficInfo = r.TrafficInfo;
        sub.SubUpload = r.SubUpload;
        sub.SubDownload = r.SubDownload;
        sub.SubTotal = r.SubTotal;
        sub.SubExpireUnix = r.SubExpireUnix;
        sub.LastFetchUnix = DateTimeOffset.Now.ToUnixTimeSeconds();

        RebuildSubNodesFromCaches();
        MirrorLegacyFromFirst();
        Save();
        LastFetchLocal = DateTime.Now;
        RebuildNodes();
        NotifySubscriptionUpdatedSafe();
        SubscriptionLogService.Finish(logId, "成功", "2xx", r.Nodes.Count, null, null,
            placeholderDetail ?? "否", sw.Elapsed.TotalMilliseconds);
        return (true, null);
    }

    public static IReadOnlyList<SubItem> Subscriptions => State.Subscriptions;

    public static string AddSubscription(string url, string? name = null)
    {
        var u = (url ?? "").Trim();
        var existing = State.Subscriptions.FirstOrDefault(s => string.Equals(s.Url, u, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.Enabled = true;
            Save();
            return existing.Id;
        }

        var sub = new SubItem
        {
            Id = "s" + DateTime.Now.ToString("MMddHHmmssfff"),
            Url = u,
            Name = string.IsNullOrWhiteSpace(name) ? TryGuessName(u) : name!.Trim(),
            Enabled = true,
        };
        State.Subscriptions.Add(sub);
        Save();
        return sub.Id;
    }

    public static void RemoveSubscriptionById(string id)
    {
        var sub = State.Subscriptions.FirstOrDefault(s => s.Id == id);
        if (sub == null) return;

        State.Subscriptions.Remove(sub);
        var cache = LoadSubCache();
        cache.Remove(id);
        SaveSubCache(cache);

        RebuildSubNodesFromCaches();
        MirrorLegacyFromFirst();
        Save();
        RebuildNodes();
    }

    public static void SetSubscriptionEnabled(string id, bool on)
    {
        var sub = State.Subscriptions.FirstOrDefault(s => s.Id == id);
        if (sub == null || sub.Enabled == on) return;

        sub.Enabled = on;
        RebuildSubNodesFromCaches();
        MirrorLegacyFromFirst();
        Save();
        RebuildNodes();
    }

    private static Dictionary<string, string> LoadSubCache()
    {
        //   原先是 `catch { }` 后**静默返回空字典**。它被 RebuildSubNodesFromCaches 使用，
        //   一旦缓存文件读失败或 JSON 损坏，cache 为空 → 所有订阅被 skip → _subNodes 变空
        //   → **节点列表整体消失**，而日志里零痕迹，用户完全无法归因。
        //   现在：区分「文件不存在」（正常，首次运行）与「读/解析失败」（异常，必须留痕）。
        if (!File.Exists(SubCacheJsonPath)) return new Dictionary<string, string>();   // 首次运行属正常
        try
        {
            var text = File.ReadAllText(SubCacheJsonPath);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(text)
                   ?? new Dictionary<string, string>();
        }
        catch (Exception ex)
        {
            LogBus.App($"订阅缓存读取失败（{SubCacheJsonPath}）：{ex.GetType().Name} {ex.Message} —— "
                     + "节点列表可能因此为空，将从订阅重新拉取", "Error");
            return new Dictionary<string, string>();
        }
    }

    private static void SaveSubCache(Dictionary<string, string> cache)
    {
        //   原先是 File.WriteAllText 整文件覆盖：该文件被 UI 线程（订阅页）与后台线程
        //   （RefreshSubscriptionAsync / RefreshOneAsync）并发写入，
        //   覆盖途中若被另一线程读取（或进程在写一半退出），会读到**半截 JSON** →
        //   解析失败 → LoadSubCache 返回空字典 → 节点列表整体消失。
        //   tmp+rename 保证读到的永远是「旧完整版」或「新完整版」，不会是半截。
        //   并发写同一 tmp 的碰撞由进程内的 lock 避免（与 SaveGate 同理，但独立门）。
        lock (SubCacheGate)
        {
            try
            {
                var tmp = SubCacheJsonPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(cache));
                File.Move(tmp, SubCacheJsonPath, true);
            }
            catch (Exception ex)
            {
                LogBus.App($"订阅缓存保存失败（{SubCacheJsonPath}）：{ex.GetType().Name} {ex.Message}", "Error");
            }
        }
    }

    private static void MigrateLegacySubscription()
    {
        if (State.Subscriptions.Count > 0) return;
        if (string.IsNullOrWhiteSpace(State.SubscriptionUrl)) return;

        var id = "main";
        State.Subscriptions.Add(new SubItem
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(State.SubscriptionName) ? TryGuessName(State.SubscriptionUrl) : State.SubscriptionName!,
            Url = State.SubscriptionUrl!,
            Enabled = true,
            TrafficInfo = State.TrafficInfo,
            SubUpload = State.SubUpload,
            SubDownload = State.SubDownload,
            SubTotal = State.SubTotal,
            SubExpireUnix = State.SubExpireUnix,
        });

        try
        {
            if (File.Exists(CachePath))
            {
                var cache = LoadSubCache();
                if (!cache.ContainsKey(id))
                    cache[id] = File.ReadAllText(CachePath);
                SaveSubCache(cache);
            }
        }
        catch { }
    }

    private static void RebuildSubNodesFromCaches()
    {
        var cache = LoadSubCache();
        var merged = new List<NodeItem>();
        foreach (var sub in State.Subscriptions.Where(s => s.Enabled))
        {
            if (!cache.TryGetValue(sub.Id, out var raw) || string.IsNullOrWhiteSpace(raw)) continue;
            var r = SubscriptionFetcher.ParseContent(raw);
            if (!r.Success) continue;
            foreach (var n in r.Nodes)
            {
                n.SourceSubscription = sub.Name;
                merged.Add(n);
            }
        }
        _subNodes = Dedupe(merged);
    }

    private static List<NodeItem> Dedupe(List<NodeItem> nodes)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<NodeItem>();
        foreach (var n in nodes)
        {
            if (seen.Add($"{n.Server}:{n.Port}:{n.Protocol}:{n.Name}"))
                result.Add(n);
        }
        return result;
    }

    private static void MirrorLegacyFromFirst()
    {
        var first = State.Subscriptions.FirstOrDefault(s => s.Enabled) ?? State.Subscriptions.FirstOrDefault();
        if (first == null) return;
        State.SubscriptionUrl = first.Url;
        State.SubscriptionName = first.Name;
        State.TrafficInfo = first.TrafficInfo;
        State.SubUpload = first.SubUpload;
        State.SubDownload = first.SubDownload;
        State.SubTotal = first.SubTotal;
        State.SubExpireUnix = first.SubExpireUnix;
    }

    /// 节点的稳定身份键：地址 + 端口 + 协议 + 名称。
    ///   重启后按名字匹配会认错节点。地址+端口+协议能唯一确定「服务端」，
    ///   名称用来区分同一服务器上的多个不同配置（不同 uuid / 传输参数）。
    /// 旧 state.json 没有 LastNodeKey，调用方都保留了按名字回退的分支。
    public static string NodeKey(NodeItem n) => $"{n.Server}|{n.Port}|{(int)n.Protocol}|{n.Name}";

    public static void SetCurrentNode(NodeItem node)
    {
        CurrentNode = node;
        State.LastNodeName = node.Name;
        State.LastNodeKey = NodeKey(node);
        Save();
        LogBus.App($"切换节点 → {node.Name}（{node.EndPoint}）", "Debug");
    }

    public static IReadOnlyList<string> ManualLinks => State.ManualLinks;

    public static (int Added, int Failed) AddManualLinks(IEnumerable<string> links)
    {
        int added = 0, failed = 0;
        foreach (var raw in links)
        {
            foreach (var part in (raw ?? "").Split(new[] { (char)13, (char)10 }, StringSplitOptions.RemoveEmptyEntries))
            {
                var link = part.Trim();
                if (link.Length == 0) continue;
                if (State.ManualLinks.Any(l => string.Equals(l, link, StringComparison.OrdinalIgnoreCase))) continue;
                if (ShareLinkParser.Parse(link) == null) { failed++; continue; }
                State.ManualLinks.Add(link);
                added++;
            }
        }
        if (added > 0) { Save(); RebuildNodes(); }
        return (added, failed);
    }

    public static void RemoveManualLink(string link)
    {
        if (State.ManualLinks.RemoveAll(l => string.Equals(l, link, StringComparison.OrdinalIgnoreCase)) == 0)
            return;
        Save();
        RebuildNodes();
        if (CurrentNode != null && !Nodes.Contains(CurrentNode))
            CurrentNode = Nodes.FirstOrDefault();
    }

    private static void RebuildNodes()
    {
        //   重新映射到新对象上；优先用稳定身份键，旧 state.json 没有 key 时回退名字。
        var cur = CurrentNode;
        var curKey = cur != null ? NodeKey(cur) : null;
        var curName = cur?.Name;
        var merged = new List<NodeItem>();
        foreach (var link in State.ManualLinks)
        {
            var n = ShareLinkParser.Parse(link);
            if (n == null) continue;
            n.SourceSubscription = "手动";
            merged.Add(n);
        }
        merged.AddRange(_subNodes);
        foreach (var n in merged) { DelayCache.Apply(n); LocationCache.Apply(n); }
        Nodes = merged;
        if (CurrentNode != null && !Nodes.Contains(CurrentNode) && (curKey != null || curName != null))
        {
            CurrentNode =
                (curKey != null ? Nodes.FirstOrDefault(n => NodeKey(n) == curKey) : null)
                ?? (curName != null ? Nodes.FirstOrDefault(n => n.Name == curName) : null)
                ?? CurrentNode;
        }
        NotifyNodesChangedSafe();
    }

    public static void SetSubscription(string url, string? name = null)
    {
        State.SubscriptionUrl = url.Trim();
        if (!string.IsNullOrWhiteSpace(name))
            State.SubscriptionName = name.Trim();
        Save();
    }

    private static string TryGuessName(string url)
    {
        try { return new Uri(url).Host.Replace("www.", ""); }
        catch { return "我的订阅"; }
    }

    public static string ProtocolName(NodeProtocol p) => p switch
    {
        NodeProtocol.VMess => "VMess",
        NodeProtocol.VLESS => "VLESS",
        NodeProtocol.Trojan => "Trojan",
        NodeProtocol.Shadowsocks => "Shadowsocks",
        NodeProtocol.Hysteria2 => "Hysteria2",
        NodeProtocol.TUIC => "TUIC",
        NodeProtocol.AnyTls => "AnyTLS",
        NodeProtocol.Socks => "Socks5",
        NodeProtocol.MihomoConfig => "Clash 直通",
        NodeProtocol.WireGuard => "WireGuard",
        NodeProtocol.CustomConfig => "自定义配置",
        NodeProtocol.Naive => "naive",
        _ => "Unknown",
    };

    public static string TransportName(NodeItem n)
    {
        if (!string.IsNullOrWhiteSpace(n.Security) && n.Security != "none")
            return n.Security == "reality" ? "Reality" : n.Security.ToUpperInvariant();
        return n.Network switch
        {
            "ws" => "WebSocket",
            "grpc" => "gRPC",
            "h2" => "HTTP/2",
            "quic" => "QUIC",
            "http" => "HTTP",
            _ => "TCP",
        };
    }

    public static (string Flow, string Total, string Expire) TrafficParts() => TrafficParts(State.TrafficInfo);

    public static (string Flow, string Total, string Expire) TrafficParts(string? meta)
    {
        var t = meta ?? "";
        string flow = "—", total = "—", expire = "—";
        if (!string.IsNullOrWhiteSpace(t))
        {
            var m = System.Text.RegularExpressions.Regex.Match(t,
                @"(?:剩余流量|已用流量|已用|剩余|可用)[：:]\s*([\d.]+\s*[KMGT]?B)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) flow = m.Groups[1].Value.Trim();

            var tm = System.Text.RegularExpressions.Regex.Match(t,
                @"(?:总量|总共|套餐流量)[：:]\s*([\d.]+\s*[KMGT]?B)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (tm.Success) total = tm.Groups[1].Value.Trim();

            var em = System.Text.RegularExpressions.Regex.Match(t,
                @"(?:到期时间|到期|过期时间|过期)[：:]\s*([^|]+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (em.Success) expire = em.Groups[1].Value.Trim();
        }
        return (flow, total, expire);
    }

    public static (long Used, long Total, long Remaining) SubUsage() =>
        SubUsage(State.SubUpload, State.SubDownload, State.SubTotal, State.TrafficInfo);

    public static (long Used, long Total, long Remaining) SubUsage(long subUpload, long subDownload, long subTotal, string? meta)
    {
        long used = 0, total = 0;
        if (subTotal > 0)
        {
            total = subTotal;
            used = Math.Max(0, subUpload + subDownload);
        }

        if (total <= 0 || used <= 0)
        {
            var t = meta ?? "";
            long usedMeta = 0, remainingMeta = 0, totalMeta = 0;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(t,
                @"(剩余流量|已用流量|已用|剩余|可用|总量|总共|套餐流量)[：:]\s*([\d.]+)\s*([KMGT]?B)"))
            {
                if (!TryParseSizeBytes(m.Groups[2].Value + " " + m.Groups[3].Value, out var v)) continue;
                switch (m.Groups[1].Value)
                {
                    case "总量":
                    case "总共":
                    case "套餐流量":
                        totalMeta = v;
                        break;
                    case "剩余流量":
                    case "剩余":
                    case "可用":
                        remainingMeta = v;
                        break;
                    default:
                        usedMeta = v;
                        break;
                }
            }
            if (total <= 0) total = totalMeta;
            if (used <= 0)
                used = usedMeta > 0 ? usedMeta
                    : (total > 0 && remainingMeta > 0 ? Math.Max(0, total - remainingMeta) : 0);
        }

        var remaining = total > 0 ? Math.Max(0, total - used) : 0;
        return (used, total, remaining);
    }

    public static double? TrafficUsedRatio()
    {
        var (used, total, _) = SubUsage();
        if (total <= 0) return null;
        return Math.Clamp((double)used / total, 0, 1);
    }

    private static bool TryParseSizeBytes(string s, out long bytes)
    {
        bytes = 0;
        var m = System.Text.RegularExpressions.Regex.Match(s, @"([\d.]+)\s*([KMGT]?B)");
        if (!m.Success || !double.TryParse(m.Groups[1].Value, out var v)) return false;
        double mult = m.Groups[2].Value switch
        {
            "TB" => 1024d * 1024 * 1024 * 1024,
            "GB" => 1024d * 1024 * 1024,
            "MB" => 1024d * 1024,
            "KB" => 1024d,
            _ => 1d,
        };
        bytes = (long)(v * mult);
        return true;
    }

    private static bool TryToGb(string num, string unit, out double gb)
    {
        gb = 0;
        if (!double.TryParse(num, out var v)) return false;
        gb = unit switch
        {
            "TB" => v * 1024,
            "GB" => v,
            "MB" => v / 1024,
            "KB" => v / 1024 / 1024,
            _ => v,
        };
        return true;
    }

    public static string GuessFlag(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("香港") || n.Contains("hk") || n.Contains("hongkong")) return "hk";
        if (n.Contains("日本") || n.Contains("jp") || n.Contains("japan") || n.Contains("东京") || n.Contains("toyko") || n.Contains("tokyo")) return "jp";
        if (n.Contains("新加坡") || n.Contains("sg") || n.Contains("singapore")) return "sg";
        if (n.Contains("美国") || n.Contains("us") || n.Contains("united") || n.Contains("圣何塞") || n.Contains("洛杉矶") || n.Contains("西雅图")) return "us";
        if (n.Contains("韩国") || n.Contains("kr") || n.Contains("korea") || n.Contains("首尔")) return "kr";
        if (n.Contains("英国") || n.Contains("uk") || n.Contains("britain") || n.Contains("伦敦") || n.Contains("london")) return "gb";
        if (n.Contains("德国") || n.Contains("de") || n.Contains("germany") || n.Contains("法兰克福")) return "de";
        if (n.Contains("荷兰") || n.Contains("nl") || n.Contains("netherlands")) return "nl";
        if (n.Contains("澳大利亚") || n.Contains("au") || n.Contains("australia") || n.Contains("悉尼")) return "au";
        if (n.Contains("台湾") || n.Contains("台北") || n.Contains("taiwan")) return "tw";
        return "other";
    }
}
