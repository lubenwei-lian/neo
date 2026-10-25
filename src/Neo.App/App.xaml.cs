using Neo.Core;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Neo.App;

public partial class App : Application
{
    private Window? _window;
    private static Mutex? _single;

    public static MainWindow? MainWindowInstance { get; private set; }

    /// 进程正在退出。所有「把工作 marshal 回 UI 线程」的入口都要先看这个标志。
    /// 为什么需要它：Application.Current.Exit() 之后，XAML 与 DispatcherQueue 会进入销毁流程，
    /// 此时若还有后台线程去投递 UI 工作 / 取当前线程的 DispatcherQueue，CoreMessaging 会以
    /// HRESULT 0x8001010E（RPC_E_WRONG_THREAD）直接 fail-fast（0xc000027b），
    /// 这种失败**不是可捕获的 .NET 异常**，try/catch 拦不住，进程会立刻消失。
    /// 转储里 stowed exception 出自一个已结束的后台线程，栈顶 WinUIGetDispatcherQueueForCurrentThread。
    /// 因此退出时先立此标志，让所有迟到的回调直接放弃，而不是去碰已销毁的队列。
    public static volatile bool IsShuttingDown;

    /// <summary>释放单实例锁：重启应用前必须调，否则新进程会被旧进程的锁挡住。</summary>
    public static void ReleaseSingleInstance()
    {
        try { _single?.ReleaseMutex(); } catch { }
        try { _single?.Dispose(); } catch { }
        _single = null;
    }

    /// <summary>
    /// 重启应用（导入配置后生效）：断开代理 → 保存状态 → 释放单实例锁 → 延迟拉起新进程 → 退出当前进程。
    /// 延迟是必要的：新进程启动时旧进程的 Mutex 可能还没真正释放。
    /// </summary>
    /// <param name="saveState">
    /// 是否在退出前把内存态写回 state.json。导入配置后重启必须传 false —— 否则会把
    /// 「导入前」的内存配置写回磁盘，覆盖刚导入的文件（实测踩到：导入 rule 备份后重启变回 global）。
    /// </param>
    public static void Restart(bool saveState = true)
    {
        IsShuttingDown = true;
        try { Services.LogBus.App("应用重启：断开代理", "Info"); Services.ConnectionService.Disconnect("重启应用"); } catch { }
        if (saveState)
        {
            try { Services.AppState.Save(); } catch { }
        }
        var exe = Environment.ProcessPath;
        try { Services.LogBus.App($"应用重启：exe={exe}，释放单实例锁", "Info"); } catch { }
        ReleaseSingleInstance();
        try { Services.RunStateMarker.Clear(); } catch { }
        if (!string.IsNullOrEmpty(exe))
        {
            // 必须**同步**拉起新进程再退出：之前用 Task.Run + 900ms 延迟，
            // 结果 Current.Exit() 直接把进程结束掉，延迟任务还没跑就被杀了 →
            // 用户点「立即重启」后应用直接消失（实测踩到）。单实例锁已在上一步释放，同步启动安全。
            try
            {
                Process.Start(new ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                    WorkingDirectory = AppContext.BaseDirectory,
                });
                Services.LogBus.App("应用重启：新进程已拉起", "Info");
            }
            catch (Exception ex)
            {
                try { Services.LogBus.App("应用重启：拉起新进程失败 " + ex.Message, "Error"); } catch { }
            }
        }
        try { Current.Exit(); } catch (Exception ex) { try { Services.LogBus.App("应用重启：退出失败 " + ex.Message, "Error"); } catch { } }
    }

    /// 由 MainWindow 注册的「有序退出」入口。任何不在 MainWindow 里的退出路径
    /// （目前是 ConnectionService.RelaunchElevated 的提权重启）都必须走它，
    /// 而不是自己直接调 Application.Current.Exit()。
    /// 为什么要有这个钩子：直接 Exit() 会跳过全部收尾 —— IsShuttingDown 没立、
    /// 统计采样/故障转移探活/各页计时器都还在跑、连接没断（xray 变孤儿、10808 被占）、
    /// 留 null 时退回直接 Exit，保证即使钩子没接上也不会卡住用户。
    public static Action? ExitRequested;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            try { Services.LogBus.Exception("未处理异常", e.Exception); } catch { }

            // 只对「一个已知可安全恢复」的异常做抑制：ContentDialog 多开冲突。
            // WinUI 源码里该冲突抛 E_ASYNC_OPERATION_NOT_STARTED (0x80000019)，
            // 语义是「这个对话框没能打开」——纯粹是 UI 交互层失败，进程状态没有损坏，
            // 忽略它继续运行是安全的（用户重试一次即可）。
            // 绝不做「一律 e.Handled = true」：官方文档明确警告不可例行如此
            //（"Do not routinely set this value to true… may not guarantee that the app
            // wouldn't terminate anyways"），且 WinRT 判定为不可恢复的异常即使设了
            // Handled 也会照样终止进程；盲目吞掉只会掩盖真正的 bug 并留下脏状态。
            // 真正的修复在 Neo.App.Ui.DialogGuard（已把全部对话框调用串行化），
            // 这里只是最后一道窄兜底：万一将来新增调用点漏走了 DialogGuard，
            // 也不至于让「弹个窗失败」这种小事杀掉整个 VPN 客户端、连带断网。
            if (e.Exception is System.Runtime.InteropServices.COMException com &&
                com.HResult == unchecked((int)0x80000019))
            {
                e.Handled = true;
                try { Services.LogBus.App("已抑制 ContentDialog 多开冲突（0x80000019），程序继续运行", "Warning"); } catch { }
            }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                try { Services.LogBus.Exception("域级未处理异常", ex); } catch { }
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try { Services.LogBus.Exception("后台任务未观察异常", e.Exception); } catch { }
            e.SetObserved();
        };
    }

    /// 从进程启动开始计时的秒表，用于定位启动各阶段耗时。
    /// 启动时窗口会先以「空壳」出现、内容随后才渲染，这段时间就是用户看到的「闪一下」，
    /// 因此必须能测出时间去哪了，而不是靠猜。
    internal static readonly System.Diagnostics.Stopwatch StartupSw = System.Diagnostics.Stopwatch.StartNew();

    internal static void StartupMark(string what)
    {
        try { Services.LogBus.App($"[启动计时] {StartupSw.ElapsedMilliseconds,5} ms  {what}", "Debug"); } catch { }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        bool takeover = Environment.GetCommandLineArgs().Contains("--takeover");
        _single = new Mutex(true, "neo-single-instance-4f8a2c", out bool createdNew);
        if (!createdNew && !takeover)
        {
            //   还没建好（或正隐藏到托盘），ActivateExistingInstance() 首次会失败。
            //   旧逻辑失败后直接继续启动 → 起第二个实例（双托盘图标、抢端口、同时写 state.json）。
            //   现在：首次失败后最多重试 5 秒（每 300ms 一次）；成功则唤醒旧实例后退出，
            //   仍失败也**直接退出**（绝不启动第二个实例），并留 Warning 日志。
            bool activated = ActivateExistingInstance();
            if (!activated)
            {
                try
                {
                    Services.LogBus.App(
                        "检测到已有 neo 实例，但首次唤醒主窗口失败 —— 最多重试 5 秒（每 300ms 一次）",
                        "Warning");
                }
                catch { }

                var deadline = DateTime.UtcNow.AddMilliseconds(5000);
                while (!activated && DateTime.UtcNow < deadline)
                {
                    try { System.Threading.Thread.Sleep(300); } catch { break; }
                    activated = ActivateExistingInstance();
                }
            }

            if (!activated)
            {
                try
                {
                    Services.LogBus.App(
                        "已有 neo 实例正在运行，唤醒其主窗口重试 5 秒仍失败 —— 本次启动直接退出（不启动第二个实例）",
                        "Warning");
                }
                catch { }
            }

            Environment.Exit(0);
            return;
        }

        // 必须在建窗口之前加载配置。
        // 为什么：窗口的「上次位置/尺寸」存在 state.json 里，而它原先是在
        // MainWindow.OnRootLoaded（Root.Loaded 事件，约启动后 1.5 秒）才读的 ——
        // 于是窗口先以构造函数里的默认几何（1280x800）显示出来，几百毫秒后才跳到保存的位置。
        //   268 ms  窗口已创建 1280x800 @ (104,104)，不可见
        //  1056 ms  变为可见    1280x800 @ (104,104)     ← 用户先看到这个
        //  1584 ms  跳到       1936x1048 @ (-8,-8)      ← 0.5 秒后才跳过去
        // 用户描述的「每次打开都会闪一下」就是这次可见的位置跳变。
        // 另外，CompactLayout 等配置也被 NodeTile 构造函数在首次构建列表时读取，
        // 而首次构建同样发生在 Loaded 之后 —— 提前加载顺带修掉「首帧用默认配置」的问题。
        try { Services.AppState.Load(); } catch { }
        try { Services.UsageTracker.Load(); } catch { }

        //  运行标记：正常退出会删除；下次启动若标记还在 → 上次没走有序收尾（强杀/崩溃）。
        //  必须先读旧标记再写新标记，否则会把自己刚写的当成「上次残留」。
        bool previousRunUnclean = Services.RunStateMarker.Exists;
        Services.RunStateMarker.MarkRunning();
        if (previousRunUnclean)
        {
            try
            {
                Services.LogBus.App("检测到上次未正常退出（运行标记未被清理）——窗口就绪后将执行残留清理（系统代理快照/内核/测速临时文件）", "Warning");
            }
            catch { }
        }

        // 启动看门狗：上次没走到窗口显示 → 计数；连续两次失败 → 安全模式（先保证能起来）。
        // 只关「TUN / 固定出口」这两项最容易把启动拖死的设置，其他设置原样保留。
        try
        {
            var guard = Services.StartupGuard.Begin();
            if (guard.PreviousLaunchFailed)
            {
                Services.LogBus.App($"检测到上次启动未完成（{guard.MarkerDetail}），连续失败 {guard.ConsecutiveFailures} 次", "Warning");
                Services.IncidentLog.Record("startup", "上次启动未完成（连续失败 " + guard.ConsecutiveFailures + " 次）", guard.MarkerDetail);
            }
            if (guard.SafeMode)
            {
                // 记住进入安全模式前的两项，供用户一键恢复（否则等于程序偷偷改了设置）。
                Services.AppState.State.SafeModePrevTun = Services.AppState.State.UseTun;
                Services.AppState.State.SafeModePrevFixedExit = Services.AppState.State.FixedExitEnabled;
                Services.AppState.State.SafeModeAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                Services.AppState.State.UseTun = false;
                Services.AppState.State.FixedExitEnabled = false;
                Services.AppState.Save();
                Services.AppState.SafeModeActive = true;
                Services.LogBus.App("已进入安全模式：TUN / 固定出口 已临时关闭（其他设置保留）。请确认启动正常后，再按需在设置里重新开启", "Error");
                Services.IncidentLog.Record("safe-mode", "连续启动失败，已进入安全模式（关闭 TUN/固定出口）");
            }
        }
        catch { }

        //   清掉上次安装中断留下的 xray.exe.new / sing-box.exe.new；
        //   若内核文件缺失但 .bak-prev 还在（替换恰好死在半路），用备份恢复，避免「内核没了」。
        try { Services.KernelInstall.RecoverIfNeeded(); } catch { }
        StartupMark("配置加载完成（建窗口前）");

        // 给 Core 层的订阅拉取注入两样东西（Core 不能反向引用 App，所以用回调）：
        //   ① 代理兜底：直连失败时改走 neo 自己的本地 socks 端口重试；
        //   ② 日志：Core 没有 LogBus，转接到 Neo.App 的 LogBus。
        try
        {
            Neo.Core.Parsing.SubscriptionFetcher.ProxyFallbackFactory = () =>
            {
                // 连着自己内核时才有意义；未连接则返回 null（只剩直连一条路）。
                if (!Services.ConnectionService.IsConnected) return null;
                return new System.Net.Http.HttpClientHandler
                {
                    Proxy = new System.Net.WebProxy(
                        $"socks5://127.0.0.1:{Neo.Core.LocalPorts.Socks}"),
                    UseProxy = true,
                };
            };
            Neo.Core.Parsing.SubscriptionFetcher.Logger = m => Services.LogBus.App(m, "Debug");
            //   本机原因（内核起不来/端口被占）造成的整批失败在日志里零痕迹，
            //   界面却把全部节点显示成「超时」，用户会误判成节点的问题。
            //   非错误的测速/可用性复核/预检记录改落 Info：原来一律降级成 Debug，
            //   日志级别一高就全看不见（本次卡顿排查就吃过这个亏）。
            Neo.Core.Core.RealDelayTester.Logger = (m, isError) =>
                Services.LogBus.App(m, isError ? "Error" : "Info");
        }
        catch { }

        _window = new MainWindow();
        MainWindowInstance = _window as MainWindow;
        StartupMark("MainWindow 构造完成（窗口仍处于隐藏态）");
        // 刻意不在这里 Activate()：MainWindow 构造函数已把窗口隐藏，
        // 目的是避免「窗口先以黑壳出现、内容随后才渲染」的启动闪现。
        // 窗口由 MainWindow.OnRootLoaded 在内容就绪后自行 ShowMainWindow() 显示。
        StartupMark("等待 OnRootLoaded 显示窗口");

        try
        {
            var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
            Services.LogBus.App(
                $"neo start up | v{ver} | {Environment.ProcessPath} | {Environment.OSVersion} | 数据目录 {Neo.Core.NeoPaths.DataDir}");
        }
        catch { }
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);

    private const string MainWindowClass = "WinUIDesktopWin32WindowClass";

    /// 把已存在实例的主窗口唤到前台；成功返回 true（此时本进程应直接退出）。
    /// 托盘库 H.NotifyIcon 用 ContextMenuMode.SecondWindow 模式会额外创建一个隐藏的
    /// WinUI 宿主窗口（类名 Microsoft.UI.Content.PopupWindowSiteBridge，标题「主机弹出窗口」），
    /// 而它在 EnumWindows 的枚举顺序里**排在真正主窗口之前**。
    /// 旧实现命中第一个窗口就 break，于是每次「隐藏到托盘后重新双击 neo」都在对那个
    /// 隐藏宿主窗口 ShowWindow/SetForegroundWindow —— 主窗口永远不回来，用户只能去点托盘图标。
    /// 主窗口的类名是 WinUIDesktopWin32WindowClass（托盘宿主、GDI+、IME 等都不是）。
    private static bool ActivateExistingInstance()
    {
        var found = false;
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("neo"))
            {
                if (p.Id == Environment.ProcessId) continue;
                uint target = (uint)p.Id;
                var hit = IntPtr.Zero;

                EnumWindows((h, _) =>
                {
                    GetWindowThreadProcessId(h, out var pid);
                    if (pid != target) return true;

                    var cls = new System.Text.StringBuilder(256);
                    GetClassName(h, cls, 256);
                    if (!string.Equals(cls.ToString(), MainWindowClass, StringComparison.Ordinal))
                        return true;   // 不是主窗口（托盘宿主 / GDI+ / IME 等），继续找

                    hit = h;
                    return false;      // 找到了
                }, IntPtr.Zero);

                if (hit == IntPtr.Zero)
                {
                    // 该类名没命中（未来 WinUI 改了类名）：退回旧行为，但至少跳过隐藏窗口，
                    // 免得再去激活托盘宿主窗口而让用户以为「点了没反应」。
                    EnumWindows((h, _) =>
                    {
                        GetWindowThreadProcessId(h, out var pid);
                        if (pid == target && IsWindowVisible(h))
                        {
                            hit = h;
                            return false;
                        }
                        return true;
                    }, IntPtr.Zero);
                }

                if (hit != IntPtr.Zero)
                {
                    ShowWindow(hit, 9);   // SW_RESTORE：从最小化/隐藏恢复
                    ShowWindow(hit, 5);   // SW_SHOW
                    SetForegroundWindow(hit);
                    found = true;
                    break;
                }
            }
        }
        catch { }
        return found;
    }
}
