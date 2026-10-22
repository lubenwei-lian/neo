using System;
using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using Neo.App.Pages;
using Neo.App.Services;
using Neo.Core;
using Neo.Core.Core;

namespace Neo.App;

public sealed partial class MainWindow : Window
{
    private readonly (Button button, string key)[] _navItems;
    private string _currentPage = "conn";

    /// 导航方向：+1 = 向右（目标页在右侧），-1 = 向左。
    /// 供 AnimatePageIn 决定新页从哪一侧滑入；空间方向与导航条顺序一致，
    /// 用户才能「跟着元素走」（Fluent motion 的 Connected 原则）。
    private int _navDir = 1;

    private static readonly string[] _navOrder = { "conn", "nodes", "subs", "settings", "rule", "log" };

    /// 上一次做「切入动效」的那个页面元素。
    ///   否则它停在半路（偏移 20px、透明度 0.4）——用户再切回来看到的就是一页残缺界面。
    private FrameworkElement? _pageInTarget;
    private bool _dark;
    private bool _exiting;
    private bool _rootLoaded;
    private bool _theming;
    private int _traySpinIdx;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _traySpinTimer;
    private bool _traySpinActive;

    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private MenuFlyoutItem? _trayConnectItem;
    private MenuFlyoutItem? _trayNodeItem;
    private MenuFlyoutItem? _trayStatusItem;
    private MenuFlyout? _trayFlyout;
    private readonly DispatcherTimer _coreTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _autoTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    /// 退出流程只跑一次：Exit 会触发若干关闭回调再次进来。
    private int _exitingOnce;

    // 导航项选中态的配色。**必须与 Themes/NeoTheme.xaml 的令牌保持一致** ——
    // 这两处是同一套色板的两份拷贝（XAML 走 ThemeResource，这里走代码赋值），
    // 改一处必须同步改另一处，否则导航条会与卡片「不是同一种蓝」。
    private static readonly Color C_LightAccent = Color.FromArgb(255, 0x25, 0x63, 0xEB);
    private static readonly Color C_LightText1 = Color.FromArgb(255, 0x12, 0x12, 0x16);
    private static readonly Color C_LightText2 = Color.FromArgb(255, 0x5E, 0x5E, 0x66);
    private static readonly Color C_LightAccentSoft = Color.FromArgb(0x1A, 0x25, 0x63, 0xEB);
    private static readonly Color C_LightGlassStrong = Color.FromArgb(0xDB, 0xFF, 0xFF, 0xFF);

    private static readonly Color C_DarkAccent = Color.FromArgb(255, 0x5A, 0xA9, 0xFF);
    private static readonly Color C_DarkText1 = Color.FromArgb(255, 0xEC, 0xEC, 0xEC);
    private static readonly Color C_DarkText2 = Color.FromArgb(255, 0x9A, 0x9A, 0xA0);
    private static readonly Color C_DarkAccentSoft = Color.FromArgb(0x24, 0x5A, 0xA9, 0xFF);
    private static readonly Color C_DarkGlassStrong = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);

    public MainWindow()
    {
        InitializeComponent();

        // 先把原生窗口藏起来，等界面渲染好再显示 —— 消除启动时的黑壳闪现。
        // 但内容要到 OnRootLoaded（968 ms）才开始构建、渐显动画 985 ms 才启动、
        // 首帧内容约 1.9 s 才画出来。这 1.2 秒里窗口是个**只有标题栏按钮的黑壳**，
        // 用户看到的就是「每次打开都会闪一下」。
        // 关键：窗口可见性必须在内容就绪**之前**就收起来，所以放在构造函数最前面
        //（此时窗口尚未绘制过，Hide 不会产生可见的闪烁）。
        try { AppWindow.Hide(); } catch { }

        App.StartupMark("MainWindow 构造函数：已隐藏窗口");
        _navItems = new[]
        {
            (NavConn, "conn"),
            (NavNodes, "nodes"),
            (NavSubs, "subs"),
            (NavSettings, "settings"),
            (NavRule, "rule"),
            (NavLog, "log"),
        };

        // `.nav:hover{background:var(--track);color:var(--text1)}`。
        // 为什么用 Storyboard 而不是 XAML 的 PointerOver 视觉态：
        // 导航项的选中态背景是由 UpdateNavVisuals() 在代码里直接赋值的
        //（btn.Background = on ? selBg : clear），会覆盖视觉态设置。
        // 冲突之下，用代码统管 hover 才能保证「选中高亮」与「hover 底色」不互相打架。
        foreach (var (btn, _) in _navItems)
        {
            btn.PointerEntered += (s, _) =>
            {
                if (s is not Button b || b.Tag as string == _currentPage) return;
                AnimateBrush(b, "Background", C_Hover(dark: _dark), 160);
            };
            btn.PointerExited += (s, _) =>
            {
                if (s is not Button b) return;
                bool on = b.Tag as string == _currentPage;
                AnimateBrush(b, "Background",
                    on ? (_dark ? C_DarkAccentSoft : C_LightAccentSoft) : Colors.Transparent, 160);
            };
        }

        SetupWindow();
        SetupTray();
        // 亮色主题已移除：启动即固定暗色。
        // 原先这里读 AppState.PeekThemeMode() 决定亮/暗/跟随系统，
        // 但旧的 state.json 里可能存着 "light" 或 "system"，会把界面切到亮色 —— 现在不再允许。
        ApplyTheme(ElementTheme.Dark);

        // 系统主题跟随已无意义（只留暗色），故不再订阅系统主题变化。
        // ★ 页面惰性创建：启动只建首屏（连接页）；设置页的 OpenRulesRequested 订阅
        //   已移入 EnsureSettingsPage()，其余页在首次导航时才创建。
        EnsureConnPage();
        Root.Loaded += OnRootLoaded;
        App.StartupMark("Root.Loaded 已订阅");
        AppState.NodesChanged += UpdateSidebar;
        ConnectionService.StateChanged += UpdateConnUi;
        // 让 ConnectionService 能把状态通知 marshal 回 UI 线程：
        // 内核崩溃等事件发生在后台线程，订阅者里有直接改 XAML 的，跨线程会抛异常。
        ConnectionService.UiDispatcher = DispatcherQueue;
        //   原界面对此完全无提示（用户以为设置已生效，实际可能已断开/没生效），这里转成状态提示。
        ConnectionService.SettingsReloadFailed += msg => DispatcherQueue.TryEnqueue(() =>
        {
            if (App.IsShuttingDown) return;
            ShowStatusText(msg);
        });
        // ★ 2026-10-23 第二批（F9）：内核看门狗进安全模式时必须让用户看见。
        //   转成「状态条 + 托盘气泡」双通道：窗口最小化到托盘时只有气泡可见，
        //   而 常见客户端的通病则是「内核死了/退化了却毫无提示」。
        ConnectionService.UserNotice += msg => DispatcherQueue.TryEnqueue(() =>
        {
            if (App.IsShuttingDown) return;
            ShowStatusText(msg);
            try { TrayIcon.ShowNotification("neo 内核兜底", msg); } catch { }
        });
        // 让「不在 MainWindow 里的退出路径」（提权重启）也能走有序收尾，
        App.ExitRequested = ExitForRelaunch;
        // 交给后台线程按 1 秒节奏采样，UI 线程只读缓存，界面不再被同步子进程调用冻结。
        StatsSampler.Start();
        FailoverService.Init(a => DispatcherQueue.TryEnqueue(() =>
        {
            // 退出中：不要再执行投递过来的 UI 动作（队列/控件正在销毁）。
            if (App.IsShuttingDown) return;
            a();
        }));
        FailoverService.Switched += msg => DispatcherQueue.TryEnqueue(() =>
        {
            if (App.IsShuttingDown) return;
            ShowStatusText(msg);
            // 最小化到托盘时状态条看不见，必须另发系统通知，否则用户仍然不知道节点已切换
            try { TrayIcon.ShowNotification("neo 自动故障转移", msg); } catch { }
        });
        _coreTimer.Tick += (_, _) => { if (App.IsShuttingDown) return; UpdateCoreCard(); UpdateUsageCard(); };
        _coreTimer.Start();
        _autoTimer.Tick += async (_, _) =>
        {
            if (App.IsShuttingDown) return;
            await MaybeAutoUpdateAsync();
            if (App.IsShuttingDown) return;
            await MaybeAutoCheckAppUpdateAsync();
        };
        _autoTimer.Start();
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); StatusToast.Visibility = Visibility.Collapsed; };

        _pageIdleTimer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        _pageIdleTimer.Tick += (_, _) => ReleaseIdlePages();
        _pageIdleTimer.Start();

        NavigateTo("conn");
    }

    /// 目标几何（启动时应停放的位置/尺寸）。构造时记录，等窗口在屏幕外渲染完成后再搬过去。
    /// int.MinValue 表示「无保存值」，此时用默认尺寸并让系统决定位置。
    private int _targetX = int.MinValue, _targetY = int.MinValue;
    private int _targetW = 1280, _targetH = 800;

    /// 启动时是否最大化（来自 state.StartMaximized；默认 true）。
    private bool _startMaximized;

    // ★ 2026-10-23 内存优化：页面空闲释放。
    //   现状是「惰性创建 + 永不销毁」：把所有页都点一遍后，节点页（68 张卡片）等常驻 ~35MB+。
    //   这里在切走 45 秒后把非当前页从可视树移除并置空（页面自己退订静态事件），
    //   再点回去时重新创建（惰性创建机制本来就在，代价 <100ms）。
    private Microsoft.UI.Xaml.DispatcherTimer? _pageIdleTimer;

    private void SetupWindow()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarRoot);

        try
        {
            // 只**记录**目标几何，不在这里搬到目标位置 —— 见 OnRootLoaded 的离屏预热策略。
            //   这里才是**真正**决定启动尺寸的地方（RestoreWindowBounds 只是兜底，
            //   旧代码只有下限 `Math.Max(960/640)`、**没有上限** ⇒ state.json 里存着
            //   1936x1048 而屏幕只有 1920x1080 时，窗口横向超出 16px、位置被顶到 x=-8，
            //   右侧内容被裁；换更小的屏幕会更糟。
            //   现在按**当前显示器工作区**（排除任务栏）夹上限与位置，坐标失效则居中。
            var st = AppState.State;
            _startMaximized = st.StartMaximized;
            var wa = GetWorkAreaFor(st.WinX, st.WinY, st.WinW, st.WinH);
            int maxW = wa.Width > 0 ? wa.Width : 1920;
            int maxH = wa.Height > 0 ? wa.Height : 1080;
            const int wantMinW = 900, wantMinH = 600;

            _targetW = Math.Clamp(st.WinW <= 0 ? maxW : st.WinW, Math.Min(wantMinW, maxW), maxW);
            _targetH = Math.Clamp(st.WinH <= 0 ? maxH : st.WinH, Math.Min(wantMinH, maxH), maxH);

            if (st.WinX == int.MinValue || st.WinY == int.MinValue
                || st.WinX + _targetW <= wa.X || st.WinY + _targetH <= wa.Y
                || st.WinX >= wa.X + wa.Width || st.WinY >= wa.Y + wa.Height)
            {
                // 无记录或完全在屏幕外 → 居中
                _targetX = wa.X + (wa.Width - _targetW) / 2;
                _targetY = wa.Y + (wa.Height - _targetH) / 2;
            }
            else
            {
                _targetX = Math.Clamp(st.WinX, wa.X, wa.X + Math.Max(0, wa.Width - _targetW));
                _targetY = Math.Clamp(st.WinY, wa.Y, wa.Y + Math.Max(0, wa.Height - _targetH));
            }

            AppWindow.Resize(new SizeInt32(_targetW, _targetH));

            AppWindow.Closing += OnAppWindowClosing;
            AppWindow.Changed += OnAppWindowChanged;

            var tb = AppWindow.TitleBar;
            tb.ButtonBackgroundColor = Colors.Transparent;
            tb.ButtonInactiveBackgroundColor = Colors.Transparent;
            tb.ButtonHoverBackgroundColor = Color.FromArgb(30, 128, 128, 128);

            // **固定色背板**（暗色 #08080A / 亮色 #F2F2F4）。
            // 保留 Win11Flair.ApplyDwmChrome（圆角 / 边框 / 深色标题栏）——
            App.StartupMark("背板：固定色（未套用 Mica）");

            // 标题栏右上角安全区按系统实际按钮宽度动态计算，
            // 取代 XAML 里硬编码的 Margin="0,0,140,0"（高缩放下会压住内容）。
            Ui.Win11Flair.ApplyTitleBarInset(this, TopRightTools);
        }
        catch
        {
        }
    }

    /// 窗口几何是否已在启动预热阶段套用过。套用过就不要再让 OnRootLoaded 重复套用，
    /// 否则同一次启动会出现二次位移。
    private bool _boundsAppliedAtStartup;

    private void UpdateUsageCard()
    {
        var (val, unit) = UsageTracker.Split(UsageTracker.TodayBytes);
        UsageTodayText.Text = val;
        UsageTodayUnit.Text = unit;
        UsageTotalText.Text = UsageTracker.Format(UsageTracker.TotalBytes);
    }

    private const int MinWindowWidthDip = 900;
    private const int MinWindowHeightDip = 600;

    /// <summary>防重入：自己 Resize 触发的 Changed 不再二次夹紧。</summary>
    private bool _clampingSize;

    /// <summary>连接页当前是否认为窗口可见（避免重复推送）。</summary>
    private bool _connPageVisible = true;

    /// 为什么最小尺寸要在这里夹、而不是只在 XAML 里设 MinWidth/MinHeight：
    /// 窗口的最小尺寸由 AppWindow 决定，XAML 的 MinWidth 只影响内容布局，
    /// 拖到 900 以下时圆环（236×scale，scale 下限 1.0）会溢出卡片。
    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (App.IsShuttingDown) return;

        if (args.DidSizeChange && !_clampingSize)
        {
            try
            {
                var s = sender.Size;
                // 最小尺寸按 DIP 定义，换算成物理像素再夹（高缩放屏 900px ≠ 900DIP）。
                double scale = 1.0;
                try { scale = Content?.XamlRoot?.RasterizationScale ?? 1.0; } catch { }
                if (scale <= 0.1) scale = 1.0;
                int minW = (int)Math.Ceiling(MinWindowWidthDip * scale);
                int minH = (int)Math.Ceiling(MinWindowHeightDip * scale);
                int w = Math.Max(s.Width, minW);
                int h = Math.Max(s.Height, minH);
                if (w != s.Width || h != s.Height)
                {
                    _clampingSize = true;
                    try { sender.Resize(new SizeInt32(w, h)); }
                    finally { _clampingSize = false; }
                }
            }
            catch { }
        }

        if (args.DidPresenterChange)
        {
            // 记住「这次是不是最大化退出」：下次启动按同样状态出现（用户要求默认最大化）。
            try
            {
                AppState.State.StartMaximized = AppWindow.Presenter is OverlappedPresenter op
                                                && op.State == OverlappedPresenterState.Maximized;
            }
            catch { }
        }

        if (args.DidVisibilityChange || args.DidPresenterChange)
            SyncConnPageWindowVisibility();
    }

    /// 把「窗口确实可见（非最小化）」同步给连接页。
    /// 托盘隐藏/恢复走 OnAppWindowClosing / ShowMainWindow 的显式调用；
    /// 这里补齐标题栏最小化/还原这条路径（AppWindow.IsVisible 仍为 true，但 Presenter 进入 Minimized）。
    private void SyncConnPageWindowVisibility()
    {
        bool visible;
        try
        {
            visible = AppWindow.IsVisible
                && (AppWindow.Presenter is not OverlappedPresenter op
                    || op.State != OverlappedPresenterState.Minimized);
        }
        catch { visible = true; }

        if (visible == _connPageVisible) return;
        _connPageVisible = visible;
        try { _pageConn?.OnWindowVisibilityChanged(visible); } catch { }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        UsageTracker.Flush();
        SaveWindowBounds();
        if (_exiting || !AppState.State.MinimizeToTray)
        {
            // 真正退出：同样先立标志再停后台活动。此处是同步回调，不能 await，
            // 用不阻塞的 Stop()（门控已保证迟到的投递会放弃）。
            App.IsShuttingDown = true;
            try { StatsSampler.Stop(); } catch { }
            try { FailoverService.Stop(); } catch { }
            try { StopOwnTimersAndDisconnect(); } catch { }
            try { DisposeTrayIcon(); } catch { }

            // 进程却仍然存活（MainWindowHandle=0、无托盘图标、217 MB 常驻），
            // 因为托盘库以 ContextMenuMode.SecondWindow 模式额外创建了一个 WinUI 宿主窗口
            //（hWnd 类名 Microsoft.UI.Content.PopupWindowSiteBridge，"主机弹出窗口"），
            // 它会让「最后一个窗口关闭 → 退出消息循环」的默认行为不成立，从而留下僵尸进程。
            // 这里显式要求退出，语义也更明确：这个分支的意图就是「关掉窗口 = 结束程序」。
            try { Application.Current.Exit(); } catch { }
            return;
        }
        args.Cancel = true;
        try { sender.Hide(); } catch { }

        // 隐藏到托盘后用户看不到任何页面：立刻释放全部页面（含当前页），
        // 把节点页那 30MB+ 的卡片树还给系统；再点托盘图标会重新惰性创建。
        try { ReleaseIdlePages(force: true); } catch { }

        _connPageVisible = false;
        try { _pageConn?.OnWindowVisibilityChanged(false); } catch { }

        // 窗口隐藏后这些 UI 谁看不见，但 _coreTimer 仍每 2 秒跑：
        // UpdateCoreCard()（内含 Process.GetProcessById + 读 WorkingSet64）、
        // UpdateUsageCard()、UpdateTrayMenu()（重写托盘菜单文本 + ApplyTrayIcon）。
        // 属纯无用开销 —— 真实需要的是「托盘图标继续在」，而不是「每 2 秒重画一遍」。
        // 只停这一个取样表：托盘图标 / 旋转动画（_traySpinTimer）/ Dock 状态**都保留**，
        // 否则用户最小化后图标会消失，那就是把好事做成了事故。
        // 重启在 ShowMainWindow()，与这里一一对应。
        try { _coreTimer.Stop(); } catch { }
    }

    private void SaveWindowBounds()
    {
        try
        {
            // 最大化时不要把「铺满屏幕的边界」写成还原态，否则用户点还原会得到一个和屏幕
            // 一样大的窗口（等于还原无效）。只记状态，边界沿用上次还原态。
            var maximized = false;
            try
            {
                maximized = AppWindow.Presenter is OverlappedPresenter op
                            && op.State == OverlappedPresenterState.Maximized;
            }
            catch { }
            AppState.State.StartMaximized = maximized;

            if (!maximized)
            {
                var p = AppWindow.Position;
                var s = AppWindow.Size;
                AppState.State.WinX = p.X;
                AppState.State.WinY = p.Y;
                AppState.State.WinW = s.Width;
                AppState.State.WinH = s.Height;
            }
            AppState.Save();
        }
        catch { }
    }

    /// 恢复窗口位置与尺寸。
    ///   我希望完美自适应各个电脑屏幕尺寸」）：
    ///   旧实现有两个真实缺陷 ——
    ///     · 只有下限 `Math.Max(960/640)`，**没有上限**：state.json 里存的是
    ///       于是窗口横向**超出屏幕 16px**（位置被顶到 x=-8），右侧被裁掉；
    ///       换到更小的屏幕（如 1366x768）时会得到比整个屏幕还大的窗口。
    ///     · 位置同样不校验：显示器拔掉/换分辨率/换机器后，旧坐标可能落在
    ///       已不存在的屏幕上，窗口就跑到了看不见的地方。
    ///   现在把「尺寸 + 位置」都夹进当前显示器的**工作区**（排除任务栏），
    ///   并保证最小可用尺寸不超过工作区，从而任何屏幕都能完整显示、不需要滚动。
    private void RestoreWindowBounds()
    {
        try
        {
            var st = AppState.State;

            // 目标显示器的工作区（含任务栏尺寸扣除）。用窗口中心点所在屏幕，
            // 旧坐标失效时退回主屏。
            var wa = GetWorkAreaFor(st.WinX, st.WinY, st.WinW, st.WinH);

            const int MinW = 900, MinH = 600;
            // 先按工作区夹上限，再夹下限：下限也不能超过工作区，否则小屏必然溢出。
            int maxW = Math.Max(320, wa.Width);
            int maxH = Math.Max(240, wa.Height);
            int w = Math.Clamp(st.WinW <= 0 ? maxW : st.WinW, Math.Min(MinW, maxW), maxW);
            int h = Math.Clamp(st.WinH <= 0 ? maxH : st.WinH, Math.Min(MinH, maxH), maxH);

            // 位置：无记录（int.MinValue）或落在工作区外 → 居中。
            int x, y;
            if (st.WinX == int.MinValue || st.WinY == int.MinValue
                || st.WinX + w <= wa.X || st.WinY + h <= wa.Y
                || st.WinX >= wa.X + wa.Width || st.WinY >= wa.Y + wa.Height)
            {
                x = wa.X + (wa.Width - w) / 2;
                y = wa.Y + (wa.Height - h) / 2;
            }
            else
            {
                // 部分可见 → 推回工作区内，保证标题栏可抓取
                x = Math.Clamp(st.WinX, wa.X, wa.X + wa.Width - w);
                y = Math.Clamp(st.WinY, wa.Y, wa.Y + wa.Height - h);
            }

            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, w, h));
        }
        catch { }
    }

    private struct WorkArea { public int X, Y, Width, Height; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;      // 工作区（已排除任务栏）
        public uint dwFlags;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    /// 取「窗口应落在的显示器」工作区。
    /// 为什么用 Win32 而不是 WinAppSDK 的 DisplayArea：本 SDK 版本（1.8.260803003）
    /// 的 `Microsoft.UI.Windowing.DisplayArea` **只有 `Primary`，没有 `GetAll()`**
    /// MonitorFromPoint + GetMonitorInfo 全版本可用，且能正确返回**多显示器**下
    /// 命中的那一块，包括已排除任务栏的 rcWork。
    /// 坐标无效（int.MinValue / 落在所有显示器之外）时用 MONITOR_DEFAULTTONEAREST 取最近一块。
    private static WorkArea GetWorkAreaFor(int winX, int winY, int winW, int winH)
    {
        try
        {
            int cx, cy;
            if (winX == int.MinValue || winY == int.MinValue || winW <= 0 || winH <= 0)
            {
                // 无记录：直接问主显示器工作区
                var pt0 = new POINT { X = 0, Y = 0 };
                var h0 = MonitorFromPoint(pt0, MONITOR_DEFAULTTONEAREST);
                var mi0 = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
                if (h0 != IntPtr.Zero && GetMonitorInfo(h0, ref mi0))
                    return new WorkArea
                    {
                        X = mi0.rcWork.Left,
                        Y = mi0.rcWork.Top,
                        Width = mi0.rcWork.Right - mi0.rcWork.Left,
                        Height = mi0.rcWork.Bottom - mi0.rcWork.Top,
                    };
                return new WorkArea();
            }

            cx = winX + winW / 2;
            cy = winY + winH / 2;
            var pt = new POINT { X = cx, Y = cy };
            var hMon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
            if (hMon == IntPtr.Zero) return new WorkArea();

            var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(hMon, ref mi)) return new WorkArea();

            return new WorkArea
            {
                X = mi.rcWork.Left,
                Y = mi.rcWork.Top,
                Width = mi.rcWork.Right - mi.rcWork.Left,
                Height = mi.rcWork.Bottom - mi.rcWork.Top,
            };
        }
        catch { return new WorkArea(); }
    }

    private void ShowMainWindow()
    {
        try { AppWindow.Show(); } catch { }
        Activate();

        _connPageVisible = true;
        try { _pageConn?.OnWindowVisibilityChanged(true); } catch { }

        //   隐藏分支里的 Stop 一一对应）。不放在这里的话，最小化到托盘再唤回后
        //   内核卡片内存与用量卡片就再也不会刷新了。
        // 退出中不启动：此时计时器已由 StopOwnTimersAndDisconnect 停掉，且控件正在销毁。
        if (!App.IsShuttingDown)
        {
            try { _coreTimer.Start(); } catch { }
            // 立刻补一次取值，避免用户看到「停了几小时前」的陈旧内存数字。
            try { UpdateCoreCard(); UpdateUsageCard(); } catch { }
        }
    }

    private void SetupTray()
    {
        var flyout = new MenuFlyout();
        _trayFlyout = flyout;

        _trayStatusItem = new MenuFlyoutItem { Text = "● 未连接", IsEnabled = false };
        flyout.Items.Add(_trayStatusItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var showItem = new MenuFlyoutItem { Text = "显示主窗口" };
        showItem.Click += delegate { ShowMainWindow(); };
        flyout.Items.Add(showItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        _trayConnectItem = new MenuFlyoutItem { Text = "连接" };
        _trayConnectItem.Click += async delegate { await ToggleConnectionFromTrayAsync(); };
        flyout.Items.Add(_trayConnectItem);

        _trayNodeItem = new MenuFlyoutItem { Text = "当前节点：—", IsEnabled = false };
        flyout.Items.Add(_trayNodeItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var exitItem = new MenuFlyoutItem { Text = "退出" };
        exitItem.Click += async delegate
        {
            LogBus.App("neo exit（用户退出）");
            await ExitApplicationAsync();
        };
        flyout.Items.Add(exitItem);

        flyout.Opening += (_, _) => UpdateTrayMenu();

        try { TrayIcon.ContextMenuMode = H.NotifyIcon.ContextMenuMode.SecondWindow; } catch { }
        TrayIcon.ContextFlyout = flyout;
        TrayIcon.DoubleClickCommand = new RelayCommand(ShowMainWindow);

        ApplyFlyoutTheme();
        UpdateTrayMenu();
    }

    private void ApplyFlyoutTheme()
    {
        if (_trayFlyout == null) return;
        try
        {
            var style = new Style(typeof(MenuFlyoutPresenter));
            style.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty,
                _dark ? ElementTheme.Dark : ElementTheme.Light));
            _trayFlyout.MenuFlyoutPresenterStyle = style;
        }
        catch { }
    }

    private async Task ToggleConnectionFromTrayAsync()
    {
        if (ConnectionService.IsConnected)
        {
            ConnectionService.Disconnect("托盘菜单断开");
            try { TrayIcon.ShowNotification("neo", "已断开连接"); } catch { }
            return;
        }

        var node = AppState.CurrentNode ?? AppState.Nodes.FirstOrDefault();
        if (node == null)
        {
            ShowStatusText("未选择节点 — 去「节点」页点选一个");
            return;
        }

        ShowStatusText("正在连接…");
        var (ok, err) = await ConnectionService.ConnectAsync(node);
        if (!ok) ShowStatusText(err ?? "连接失败");
        try { TrayIcon.ShowNotification("neo", ok ? $"已连接 · {node.Name}" : $"连接失败：{err ?? "未知错误"}"); } catch { }
    }

    private void UpdateTrayMenu()
    {
        if (_trayConnectItem == null || _trayNodeItem == null) return;
        bool on = ConnectionService.IsConnected;
        _trayConnectItem.Text = on ? "断开" : "连接";
        var node = ConnectionService.ConnectedNode ?? AppState.CurrentNode;
        _trayNodeItem.Text = $"当前节点：{node?.Name ?? "未选择"}";
        TrayIcon.ToolTipText = on ? $"neo · 已连接 · {node?.Name}" : "neo · 未连接";

        if (_trayStatusItem != null)
        {
            if (on)
            {
                var dur = ConnectionService.ConnectedAt is { } t ? DateTime.Now - t : TimeSpan.Zero;
                var durText = dur.TotalHours >= 1
                    ? $"{(int)dur.TotalHours} 时 {dur.Minutes} 分"
                    : dur.TotalMinutes >= 1 ? $"{(int)dur.TotalMinutes} 分" : "刚刚";
                _trayStatusItem.Text = $"● 已连接 · 已运行 {durText}";
                _trayStatusItem.Foreground = ThemeUtil.Get("NeoGreen", Color.FromArgb(255, 0x16, 0xA3, 0x4A));
            }
            else
            {
                _trayStatusItem.Text = "● 未连接";
                _trayStatusItem.Foreground = ThemeUtil.Get("NeoText2", Color.FromArgb(255, 0x6B, 0x72, 0x80));
            }
        }

        ApplyTrayIcon();
    }

    private System.Drawing.Icon? _trayIconOff;
    private System.Drawing.Icon? _trayIconOn;
    private System.Drawing.Icon[]? _traySpinIcons;

    private void ApplyTrayIcon()
    {
        try
        {
            if (ConnectionService.IsConnecting)
            {
                _traySpinIcons ??= LoadSpinnerIcons();
                if (!_traySpinActive)
                {
                    _traySpinActive = true;
                    _traySpinIdx = 0;
                    if (_traySpinIcons.Length > 0) TrayIcon.UpdateIcon(_traySpinIcons[0]);
                    _traySpinTimer ??= CreateSpinTimer();
                    _traySpinTimer.Start();
                }
                return;
            }

            _traySpinTimer?.Stop();
            _traySpinActive = false;

            if (ConnectionService.IsConnected)
            {
                _trayIconOn ??= LoadTrayIcon("neo-active.ico");
                if (_trayIconOn != null) TrayIcon.UpdateIcon(_trayIconOn);
            }
            else
            {
                _trayIconOff ??= LoadTrayIcon("neo.ico");
                if (_trayIconOff != null) TrayIcon.UpdateIcon(_trayIconOff);
            }
        }
        catch { }
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateSpinTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(110);
        timer.IsRepeating = true;
        timer.Tick += (_, _) =>
        {
            if (_traySpinIcons == null || _traySpinIcons.Length == 0) return;
            _traySpinIdx = (_traySpinIdx + 1) % _traySpinIcons.Length;
            try { TrayIcon.UpdateIcon(_traySpinIcons[_traySpinIdx]); } catch { }
        };
        return timer;
    }

    // 用 System.Drawing.Icon 从真正的 .ico 直接构造，绕开 IconSource(ImageSource) 的位图转换
    private static System.Drawing.Icon? LoadTrayIcon(string file)
    {
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", file);
            if (!System.IO.File.Exists(path)) return null;
            return new System.Drawing.Icon(path, 32, 32);
        }
        catch { return null; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    // ---- 启动预热用的分层窗口 API ----
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int LWA_ALPHA = 0x00000002;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_FRAMECHANGED = 0x0020;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    /// 把窗口设为分层窗口并「几乎全透明」（alpha=1，肉眼不可见）用于预热首帧渲染。
    /// 为什么用 alpha=1 而不是 0：alpha=0 时窗口对 DWM 而言是完全不可见的，
    /// DWM 会跳过它的合成，XAML 也就不会真正把首帧交给合成器 —— 结果是我们「等两帧」
    /// alpha=1 在视觉上同样不可见，但让窗口保持「可合成」状态，XAML 会正常渲染首帧。
    private static bool SetFullyTransparent(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return false;
            var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_LAYERED);
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
            return SetLayeredWindowAttributes(hwnd, 0, 1, LWA_ALPHA);
        }
        catch { return false; }
    }

    /// 预热结束：把窗口恢复为完全不透明。
    /// 注意**不要在这里清除 WS_EX_LAYERED**（也不要 SWP_FRAMECHANGED）。
    /// 而非应用自身的暗色背景 (30,33,38)）。
    /// 只把 alpha 从 0 调到 255 是纯合成操作，不触发重绘，因此没有黑帧。
    /// 保留 WS_EX_LAYERED 的代价：DWM 会对分层窗口走一次合成拷贝。alpha=255 时观感不变，
    /// 但为避免长期影响动画性能，这里在**首帧稳定之后**再异步去掉该样式
    ///（见 ScheduleUnlayerAfterFirstPaint），让去样式引发的重绘发生在窗口已有内容时 —— 那时
    /// 重绘的是真实界面，不会出现黑屏。
    private static void MakeOpaque(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return;
            SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
        }
        catch { }
    }

    /// 首帧已经稳定显示之后，再去掉 WS_EX_LAYERED（避免长期以分层窗口运行影响动画性能）。
    /// 必须**在 UI 线程上**执行：SetWindowLong/SetWindowPos 操作的是本窗口，
    /// 从后台线程去改会造成跨线程窗口访问（WinUI 下可能 fail-fast，和本项目其它
    /// 跨线程问题同源）。因此这里用 DispatcherQueue 计时器，而不是 Threading.Timer。
    private void ScheduleUnlayerAfterFirstPaint(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            var t = DispatcherQueue.CreateTimer();
            t.Interval = TimeSpan.FromMilliseconds(1200);
            t.IsRepeating = false;
            t.Tick += (_, _) =>
            {
                t.Stop();
                try
                {
                    if (App.IsShuttingDown) return;
                    var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                    if ((ex & WS_EX_LAYERED) == 0) return;
                    SetWindowLong(hwnd, GWL_EXSTYLE, ex & ~WS_EX_LAYERED);
                    SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
                    App.StartupMark("已移除分层窗口样式（恢复正常合成路径）");
                }
                catch { }
            };
            t.Start();
        }
        catch { }
    }

    // spinner 帧是 PNG，System.Drawing.Icon 不能直读 PNG，用位图句柄转 Icon。
    // 注意 HICON 的所有权：`Bitmap.GetHicon()` 返回的句柄**必须**由调用方 DestroyIcon，
    // 否则每次运行泄漏 8 个 GDI 句柄（`Icon.Dispose()` 不会销毁 FromHandle 传入的外部句柄）。
    // 因此这里先把句柄包成临时 Icon、Clone 出一份自有句柄的副本（Clone 会新建 HICON，
    // 与原句柄脱离），再立刻销毁原句柄 —— 返回的每个 Icon 都拥有自己的句柄，可安全 Dispose。
    private static System.Drawing.Icon[] LoadSpinnerIcons()
    {
        var list = new System.Collections.Generic.List<System.Drawing.Icon>();
        for (var i = 0; i < 8; i++)
        {
            try
            {
                var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "spinner", "frame" + i + ".png");
                if (!System.IO.File.Exists(path)) continue;
                using var bmp = new System.Drawing.Bitmap(path);
                var h = bmp.GetHicon();
                try
                {
                    using var tmp = System.Drawing.Icon.FromHandle(h);
                    list.Add((System.Drawing.Icon)tmp.Clone());
                }
                finally
                {
                    try { DestroyIcon(h); } catch { }
                }
            }
            catch { }
        }
        return list.ToArray();
    }

    // 故 OnThemeLightClick / OnThemeDarkClick 随之删除。
    // SetThemeMode 保留：它仍被设置页的 ThemeRequested 与「跟随系统」回调调用，
    // 但现在只接受 dark —— 传入 light/system 会被强制归一到 dark。

    public void SetThemeMode(string mode)
    {
        // 强制暗色：亮色主题已整体移除，任何入口（含旧的 state.json 里存着 "light"）
        // 都不应把界面切到亮色。这是唯一的归一化点，改一处即可覆盖全部入口。
        _ = mode;
        const string forced = "dark";
        ApplyTheme(ElementTheme.Dark);
        if (AppState.State.ThemeMode != forced)
        {
            AppState.State.ThemeMode = forced;
            AppState.Save();
        }
    }

    private void ApplyTheme(ElementTheme theme)
    {
        if (!_rootLoaded || _theming)
        {
            ApplyThemeCore(theme);
            return;
        }

        _theming = true;
        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        var dip = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = 1,
            To = 0.55,
            Duration = TimeSpan.FromMilliseconds(130),
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
            {
                EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseIn,
            },
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(dip, Root);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(dip, "Opacity");
        sb.Children.Add(dip);
        sb.Completed += (_, _) =>
        {
            ApplyThemeCore(theme);
            var sb2 = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            var back = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = 0.55,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(280),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
                {
                    EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut,
                },
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(back, Root);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(back, "Opacity");
            sb2.Children.Add(back);
            sb2.Completed += (_, _) => { _theming = false; };
            sb2.Begin();
        };
        sb.Begin();
    }

    public Microsoft.UI.Xaml.Controls.Canvas RippleLayerHost => RippleLayer;

    private void ApplyThemeCore(ElementTheme theme)
    {
        Root.RequestedTheme = theme;
        bool dark = theme == ElementTheme.Dark ||
                    (theme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark);
        _dark = dark;
        ApplyFlyoutTheme();

        var text1 = new SolidColorBrush(dark ? C_DarkText1 : C_LightText1);
        var text2 = new SolidColorBrush(dark ? C_DarkText2 : C_LightText2);

        // 亮/暗切换按钮已移除（用户要求只留暗色），这段原先给 BtnLight / BtnDark
        // 上色与设前景的代码随之删除。text1 / text2 仍被下方逻辑使用，保留。

        try
        {
            var fg = dark ? Colors.White : C_LightText1;
            var tb = AppWindow.TitleBar;
            tb.ButtonForegroundColor = fg;
            tb.ButtonHoverForegroundColor = fg;
        }
        catch { }

        // DWM 层面（系统绘制的窗口边框与标题栏非客户区）随主题同步。
        // 只做 WinUI 侧的 ButtonForegroundColor 是不够的：暗色界面配亮色 DWM 边框会很割裂。
        Ui.Win11Flair.ApplyDwmChrome(this, dark);

        UpdateNavVisuals();
    }

    // ================= 页面惰性创建（省内存） =================
    // 启动时只构建首屏（连接页）；其余页在首次导航到时才 InitializeComponent 并加入
    // PageHost，之后缓存复用、不销毁不重建（来回切页不会闪）。
    // 注意：任何「退出/隐藏/销毁」路径都不能通过这些页面属性访问页面（会触发创建）；
    //       这类路径统一走 _pageXxx 字段判空。
    private ConnectionPage? _pageConn;
    private NodesPage? _pageNodes;
    private SubsPage? _pageSubs;
    private SettingsPage? _pageSettings;
    private RulePage? _pageRule;
    private LogPage? _pageLog;

    private FrameworkElement EnsurePage(string key) => key switch
    {
        "nodes" => EnsureNodesPage(),
        "subs" => EnsureSubsPage(),
        "settings" => EnsureSettingsPage(),
        "rule" => EnsureRulePage(),
        "log" => EnsureLogPage(),
        _ => EnsureConnPage(),
    };

    private FrameworkElement? CreatedPage(string key) => key switch
    {
        "nodes" => _pageNodes,
        "subs" => _pageSubs,
        "settings" => _pageSettings,
        "rule" => _pageRule,
        "log" => _pageLog,
        _ => _pageConn,
    };

    private ConnectionPage EnsureConnPage()
    {
        if (_pageConn != null) return _pageConn;
        var p = new ConnectionPage { Visibility = Visibility.Collapsed };
        p.OpenNodesRequested += (_, _) => NavigateTo("nodes");
        p.ConnectionFailed += (_, msg) => ShowStatusText(msg);
        PageHost.Children.Add(p);
        _pageConn = p;
        // 首屏页在窗口可见性变化之前创建，建完同步一次（默认可见 = true）。
        try { p.OnWindowVisibilityChanged(_connPageVisible); } catch { }
        return p;
    }

    private NodesPage EnsureNodesPage()
    {
        if (_pageNodes != null) return _pageNodes;
        var p = new NodesPage { Visibility = Visibility.Collapsed };
        PageHost.Children.Add(p);
        _pageNodes = p;
        return p;
    }

    private SubsPage EnsureSubsPage()
    {
        if (_pageSubs != null) return _pageSubs;
        var p = new SubsPage { Visibility = Visibility.Collapsed };
        PageHost.Children.Add(p);
        _pageSubs = p;
        return p;
    }

    private SettingsPage EnsureSettingsPage()
    {
        if (_pageSettings != null) return _pageSettings;
        var p = new SettingsPage { Visibility = Visibility.Collapsed };
        // 订阅点从 MainWindow 构造里挪到创建点（页面不再于启动时创建）。
        p.OpenRulesRequested += (_, _) => NavigateTo("rule");
        PageHost.Children.Add(p);
        _pageSettings = p;
        // Loaded 里还会再刷一次（幂等）；先刷一次，保证首帧状态正确。
        try { p.RefreshFromState(); } catch { }
        return p;
    }

    private RulePage EnsureRulePage()
    {
        if (_pageRule != null) return _pageRule;
        var p = new RulePage { Visibility = Visibility.Collapsed };
        PageHost.Children.Add(p);
        _pageRule = p;
        try { p.RefreshFromState(); } catch { }
        return p;
    }

    private LogPage EnsureLogPage()
    {
        if (_pageLog != null) return _pageLog;
        var p = new LogPage { Visibility = Visibility.Collapsed };
        PageHost.Children.Add(p);
        _pageLog = p;
        return p;
    }

    /// <summary>
    /// 空闲释放：把「已创建、但当前不在看」的页从可视树移除并置空（连接页永不释放）。
    /// 触发点：① 切页 45 秒后；② 窗口隐藏到托盘时（force）。释放后再次导航会重新创建。
    /// </summary>
    private void ReleaseIdlePages(bool force = false)
    {
        try { _pageIdleTimer?.Stop(); } catch { }
        try { LogBus.App($"内存优化：开始释放空闲页面（force={force}，当前页={_currentPage}）", "Debug"); } catch { }
        foreach (var key in new[] { "nodes", "subs", "settings", "rule", "log" })
        {
            if (key == _currentPage)
            {
                // force（隐藏到托盘）时当前页也可以释放——用户看不到它；回来导航会重建。
                if (!force || key == "conn") continue;
            }
            // 节点页：先只丢卡片树（实测整页移除对工作集无效，卡片才是大头），
            // 只有隐藏到托盘（force）时才整页释放。
            if (key == "nodes" && !force)
            {
                try { _pageNodes?.ReleaseTiles(); } catch { }
                continue;
            }
            ReleasePage(key);
        }

    }

    private void ReleasePage(string key)
    {
        var page = CreatedPage(key);
        if (page == null) return;
        try
        {
            switch (key)
            {
                case "nodes": _pageNodes?.Release(); break;
                case "subs": _pageSubs?.Release(); break;
                case "settings": _pageSettings?.Release(); break;
                case "rule": _pageRule?.Release(); break;
                case "log": _pageLog?.Release(); break;
            }
        }
        catch { }
        // 切页动画的目标引用也会拖住页面（_pageInTarget 是强引用），释放前一并清掉。
        try { StopPageAnim(page); } catch { }
        if (ReferenceEquals(_pageInTarget, page)) _pageInTarget = null;
        try { PageHost.Children.Remove(page); } catch { }
        // 摘掉页面的内容树：PageHost 只是移除引用，页面内部仍持有整棵控件树；
        // 清空 Content 让 XAML 树立即断开（重新导航时会重新构造）。
        try { if (page is Microsoft.UI.Xaml.Controls.ContentControl cc) cc.Content = null; } catch { }
        try { LogBus.App($"内存优化：已释放页面 {key}（PageHost 剩余 {PageHost.Children.Count} 个）", "Debug"); } catch { }
        try { LogBus.App($"内存优化：已释放页面 {key}（PageHost 剩余 {PageHost.Children.Count} 个）", "Info"); } catch { }
        switch (key)
        {
            case "nodes": _pageNodes = null; break;
            case "subs": _pageSubs = null; break;
            case "settings": _pageSettings = null; break;
            case "rule": _pageRule = null; break;
            case "log": _pageLog = null; break;
        }
    }

    private void OnNavClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string key &&
            key is "conn" or "nodes" or "subs" or "settings" or "rule" or "log")
        {
            NavigateTo(key);
        }
    }

    private void NavigateTo(string page)
    {
        //   用户报「点首页→节点直接一帧出现」。除了时长/曲线不到位，
        //   「方向感」缺失也是主因：旧实现只做 translateY(10px)、X 不动，
        //   换页语意本就是左右换，不体现方向就只剩「淡一下」的观感。
        var from = Array.IndexOf(_navOrder, _currentPage);
        var to = Array.IndexOf(_navOrder, page);
        if (from >= 0 && to >= 0 && to != from) _navDir = to > from ? 1 : -1;

        _currentPage = page;

        // 切页就把「空闲释放」倒计时重置（回访的页面立即重新计时）。
        try { _pageIdleTimer?.Stop(); _pageIdleTimer?.Start(); } catch { }

        // 惰性创建：首次导航到该页时才建页；已建页只切 Visibility，不销毁不重建。
        var target = EnsurePage(page);
        foreach (var key in _navOrder)
        {
            if (CreatedPage(key) is { } created)
                created.Visibility = key == page ? Visibility.Visible : Visibility.Collapsed;
        }

        PageTitleText.Text = page switch
        {
            "conn" => "连接",
            "nodes" => "节点",
            "subs" => "订阅",
            "settings" => "设置",
            "rule" => "规则",
            "log" => "日志",
            _ => "连接",
        };

        UpdateNavVisuals();

        //   两页都不卸载、只在 Loaded 时读一次 state —— 切页时不重刷就会显示旧值，
        //   此时拨任一开关会把过期值写回，静默回滚另一页刚改的设置。每次切页都重刷。
        try
        {
            switch (page)
            {
                case "rule": EnsureRulePage().RefreshFromState(); break;
                case "settings": EnsureSettingsPage().RefreshFromState(); break;
                case "nodes": EnsureNodesPage().EnsureTiles(); break;
            }
        }
        catch { }

        AnimatePageIn(target, _navDir);
    }

    private static Color C_Hover(bool dark)
        => dark ? Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0C, 0x12, 0x16, 0x16);

    /// 让某个元素上的画刷颜色做时长 ms 的过渡，复刻 CSS `transition:background .16s ease`。
    /// 做法：取出该属性的当前 SolidColorBrush（没有就新建），
    /// 用 ColorAnimation 改它的 Color。**不能**每帧新建画刷 —— 那样得不到过渡，
    /// 而是瞬间跳变，且会不断分配对象。
    /// 每个画刷身上「正在跑」的那条 Storyboard。用于在开新动画前先把旧的停掉。
    /// 为什么必须停：`AnimateBrush` 每次 hover 都 `new Storyboard` 并 `Begin()`，
    /// 却从不 `Stop()`。用户在导航栏来回移动鼠标时，PointerEntered 会高频触发，
    /// 于是同一个画刷上会叠起成百上千条 ColorAnimation，且**永不回收**；
    /// 且停下鼠标后仍维持高占用一段时间）。
    /// 用 ConditionalWeakTable 记「画刷 → 它的动画」，开新的之前先停旧的，即可根治。
    /// 用弱表是为了不拖住画刷（画刷随控件回收）。
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SolidColorBrush,
        Microsoft.UI.Xaml.Media.Animation.Storyboard> _brushAnims = new();

    private static void AnimateBrush(DependencyObject target, string prop, Color to, int ms)
    {
        try
        {
            var brush = target.GetType().GetProperty(prop)?.GetValue(target) as SolidColorBrush;
            if (brush == null)
            {
                brush = new SolidColorBrush(to);
                target.GetType().GetProperty(prop)?.SetValue(target, brush);
                return;
            }

            // 同一个画刷上若已有动画在跑，先停掉再起新的 —— 否则动画会层层累积。
            if (_brushAnims.TryGetValue(brush, out var prev))
            {
                try { prev.Stop(); } catch { }
                _brushAnims.Remove(brush);
            }

            var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            var anim = new Microsoft.UI.Xaml.Media.Animation.ColorAnimation
            {
                To = to,
                Duration = TimeSpan.FromMilliseconds(ms),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.QuadraticEase
                {
                    EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut,
                },
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, brush);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, "Color");
            sb.Children.Add(anim);
            sb.Begin();
            _brushAnims.AddOrUpdate(brush, sb);
        }
        catch { }
    }

    /// 起因：用户「软件动画效果不是很好，点击首页--节点，没有弹出动画，直接一帧出现」。
    /// 旧实现为什么看起来"根本没有动画"——三条叠在一起：
    ///   ① 只做**垂直**位移 10px、X 方向不动。但本应用 6 个页面在内容区是**横向**排列的
    ///      纵向 10px 又正好和"内容比视口高、本来就要滚"混在一起，观感上等于没动。
    ///   ② 曲线用 ExponentialEase(Exponent=3, EaseOut) 去近似 cubic-bezier(.2,.8,.2,1)。
    ///      该指数曲线在头 30% 的时间里已经走完约 70% 路程，剩余位移几乎全挤进头两帧 ——
    ///      人眼只捕捉到"已经到位"，中间过程被压缩没了。
    ///      首帧与后续帧之间容易出现可感知的断裂。
    /// 现在改为：**方向由导航次序决定**（向右切页从右侧滑入，向左则从左侧）+
    /// 更贴合换页语意的时长 + 官方曲线 + Composition。
    ///   · 时长 260 ms：WinUI 标准档为 ControlFastAnimationDuration 167 ms、
    ///     整页切换取 250 ms 档稍加一点，既不拖沓也不抢。
    ///   · 曲线 cubic-bezier(0, 0, 0, 1) = Fluent「Fast Out, Slow In」，同一页面明确写给
    ///     "objects or UI entering the scene, either navigating or spawning"。
    ///     WinUI 的 Storyboard 缓动类都只有单一控制点，**表达不了这条曲线**；
    ///     改用 Composition 的 CreateCubicBezierEasingFunction —— 参数与 CSS 的
    ///     cubic-bezier(p1x,p1y,p2x,p2y) 完全同构，两个控制点都能给。
    ///   · 位移 22 px：Fluent 的 drill-in 用 22% 视口位移（1920 屏 ≈ 422 px，对本应用过多），
    ///     Material 3 的共享轴最大 30%；本应用是"同一窗口换标签页"而非钻入新层级，
    ///     故取其下限量级的 22 px（260 ms 内可辨认，且不会把内容甩出可视区）。
    /// 为什么用 Composition 而不是 Storyboard：
    ///   合成器线程驱动，不占 UI 线程；且同一个视觉对象上 StartAnimation 同名属性会
    ///   **覆盖**上一条动画，不存在 Storyboard 越叠越多、永不回收的问题。
    /// 为什么用 "Translation" 而不是 visual.Offset：
    ///   XAML 元素的 Offset 由布局系统在每次布局时回写，动画期间父容器一旦重新布局
    ///   （切页时 Visibility 变化必然触发），动画会被布局结果顶掉。
    ///   ElementCompositionPreview.SetIsTranslationEnabled + Properties["Translation"] 是
    private void AnimatePageIn(FrameworkElement? el, int dir)
    {
        if (el == null || !_rootLoaded) return;
        try
        {
            // 上一页若还有动画在跑，先停掉并复位。
            // 不这么做：连点导航时上一页会停在半路（位移未归零、透明度 0.4），
            // 再切回来就是一页残缺界面 —— 改造前因每次都新建 Storyboard 反而不存在此问题，
            // 属于"改成 Composition 后必须一并补上"的配套项。
            StopPageAnim(_pageInTarget);
            _pageInTarget = el;

            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(el, true);
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(el);
            var compositor = visual.Compositor;

            // Fluent「Fast Out, Slow In」cubic-bezier(0,0,0,1)
            var ease = compositor.CreateCubicBezierEasingFunction(
                new System.Numerics.Vector2(0f, 0f), new System.Numerics.Vector2(0f, 1f));

            var dur = TimeSpan.FromMilliseconds(260);
            var dx = 22f * (dir >= 0 ? 1f : -1f);

            visual.Properties.InsertVector3("Translation", new System.Numerics.Vector3(dx, 0f, 0f));
            visual.Opacity = 0f;

            var slide = compositor.CreateVector3KeyFrameAnimation();
            slide.Duration = dur;
            slide.InsertKeyFrame(1f, System.Numerics.Vector3.Zero, ease);
            visual.StartAnimation("Translation", slide);

            // 移位与淡入用**同一条**曲线、同一时长：起步处位移最快、透明度也涨得最快，
            // 二者速度同步，观感是"滑进来"而不是"先淡后滑"（那会显得分两步）。
            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.Duration = dur;
            fade.InsertKeyFrame(1f, 1f, ease);
            visual.StartAnimation("Opacity", fade);
        }
        catch { }
    }

    /// <summary>停掉某页残留的切入动画并把位移/透明度复位（配合 AnimatePageIn 使用）。</summary>
    private static void StopPageAnim(FrameworkElement? el)
    {
        if (el == null) return;
        try
        {
            var v = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(el);
            v.StopAnimation("Translation");
            v.StopAnimation("Opacity");
            v.Properties.InsertVector3("Translation", System.Numerics.Vector3.Zero);
            v.Opacity = 1f;
        }
        catch { }
    }

    private void UpdateNavVisuals()
    {
        var accent = new SolidColorBrush(_dark ? C_DarkAccent : C_LightAccent);
        var text2 = new SolidColorBrush(_dark ? C_DarkText2 : C_LightText2);

        foreach (var (btn, key) in _navItems)
        {
            bool on = key == _currentPage;

            // 原先这里是 `var clear = new SolidColorBrush(...)` 建一次，
            // 然后 `btn.Background = on ? selBg : clear` 把它赋给所有未选中项 ——
            // WinUI 赋 Brush 不会克隆对象，5 个按钮的 Background 指向**同一个**
            // SolidColorBrush。而 hover 动效（AnimateBrush）正是对该画刷实例做
            // ColorAnimation，于是「悬停一项」会把那个共享画刷一起染色，
            // 每个按钮各建一个画刷即可根治，且不改动其它逻辑。
            btn.Background = new SolidColorBrush(
                on ? (_dark ? C_DarkAccentSoft : C_LightAccentSoft) : Colors.Transparent);
            btn.Foreground = on ? accent : text2;
            btn.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private async void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        Root.Loaded -= OnRootLoaded;
        App.StartupMark("OnRootLoaded 进入");

        // AppState.Load() / UsageTracker.Load() 已在 App.OnLaunched 里、建窗口之前完成
        //（为了让窗口能直接用保存的几何创建，避免启动时的位置跳变）。
        // 这里只在「确实还没加载过」时兜底 —— 例如将来有人改了启动顺序。
        if (AppState.Nodes.Count == 0 && AppState.State.Subscriptions.Count == 0)
        {
            try { AppState.Load(); } catch { }
        }
        // 已加载过就不重复套用窗口位置；没加载过（上面的兜底分支）才需要补一次。
        if (!_boundsAppliedAtStartup) RestoreWindowBounds();

        ConnectionService.CleanupOrphanCores();
        ConnectionService.CleanupOrphanedSystemProxy();
        try
        {
            BrandMarkBox.Background = new Microsoft.UI.Xaml.Media.ImageBrush
            {
                ImageSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/neo-mark.png")),
                Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            };
        }
        catch { }
        UpdateSidebar();
        UpdateUsageCard();
        GlassTuner.ApplyFixed();
        UpdateConnUi();
        // 设置页 / 规则页改为首次导航时创建，并在 EnsurePage 内刷新；这里不再预建。
        try
        {
            if (_pageSettings != null) _pageSettings.RefreshFromState();
            if (_pageRule != null) _pageRule.RefreshFromState();
        }
        catch { }

        try
        {
            foreach (var (btn, _) in _navItems)
                Ui.Ripple.Attach(btn, RippleLayer);
        }
        catch { }
        App.StartupMark("同步初始化全部完成");

        // 消除「启动黑壳」的最终方案：窗口一步到位放在目标位置，先用**分层窗口全透明**
        // 把首帧渲染跑起来（用户看不见），渲染好再把不透明度放开。
        // 窗口一旦可见，WinUI 才建立合成器并绘制首帧，这需要数百毫秒，期间屏幕上是纯黑窗口。
        //   · 原实现（构造里 Activate，内容等 Loaded 后才建）：黑壳约 1200 ms
        //   · 只在隐藏态 UpdateLayout()：约 560 ms（UpdateLayout 不产生渲染帧，无用）
        //   · 隐藏态等 CompositionTarget.Rendering：约 560 ms（隐藏时该事件不触发）
        //     （移动 1310 ms、黑壳 1594–1814 ms），说明「移动窗口」本身会触发重新合成，
        //      也就是说位移方案治标不治本
        //   · 本方案：不移动窗口，改用 WS_EX_LAYERED 全透明（alpha=0）预热，
        //     预热完直接放开 alpha —— 用户从始至终看不到黑壳，因为黑壳期间窗口是全透明的
        try { Root.UpdateLayout(); Root.Opacity = 1; } catch { }
        _rootLoaded = true;

        try
        {
            // 先摆到最终位置（不产生后续位移）
            if (_targetX != int.MinValue && _targetY != int.MinValue)
            {
                AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(_targetX, _targetY, _targetW, _targetH));
                _boundsAppliedAtStartup = true;
            }

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            bool layered = SetFullyTransparent(hwnd);
            App.StartupMark($"窗口已显示（分层透明={layered}），开始预热渲染");

            AppWindow.Show();

            // ★ 2026-10-23：默认最大化。实测「Show() 之前 Maximize()」会被 Show 复位成
            //   「工作区大小的还原窗口」（IsZoomed=False，标题栏按钮也不显示还原态）；
            //   放在 Show() 之后才真正进入最大化状态。此时窗口还是全透明（下面才 Reveal），
            //   所以用户看不到尺寸跳变。
            if (_startMaximized)
            {
                try
                {
                    if (AppWindow.Presenter is OverlappedPresenter opMax
                        && opMax.State != OverlappedPresenterState.Maximized)
                        opMax.Maximize();
                }
                catch { }
            }

            var revealed = false;
            void Reveal()
            {
                if (revealed) return;
                revealed = true;
                try
                {
                    if (layered)
                    {
                        // 仅调 alpha（合成操作，不触发重绘 → 无黑帧）
                        MakeOpaque(hwnd);
                        // 界面稳定后再去掉分层样式（避免长期以分层窗口运行影响动画性能）
                        ScheduleUnlayerAfterFirstPaint(hwnd);
                    }
                    Activate();
                }
                catch { }
                App.StartupMark("窗口已恢复不透明（内容已渲染完成）");

        // 启动走到「窗口已显示且内容已渲染」才算成功：清掉看门狗 marker 与失败计数。
        try { Services.StartupGuard.Clear(); } catch { }
        if (Services.AppState.SafeModeActive)
        {
            Services.AppState.SafeModeActive = false;
            try { ShowStatusText("已用安全模式启动：TUN / 固定出口已关闭，可在设置里重新开启"); } catch { }
        }
            }

            // 两帧：第一帧建合成器，第二帧确保画面真的画出来了
            int frames = 0;
            void OnFrame(object? s, object e)
            {
                if (++frames < 2) return;
                try { Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnFrame; } catch { }
                Reveal();
            }
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnFrame;

            // 兜底 400 ms：无论如何都要把窗口显示出来，绝不能让它一直透明。
            var t = DispatcherQueue.CreateTimer();
            t.Interval = TimeSpan.FromMilliseconds(400);
            t.IsRepeating = false;
            t.Tick += (_, _) =>
            {
                t.Stop();
                try { Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnFrame; } catch { }
                Reveal();
            };
            t.Start();
        }
        catch (Exception ex)
        {
            // 任一环节失败：退回最朴素的做法，保证窗口一定以不透明状态出现在正确位置。
            LogBus.App($"启动预热失败（不影响使用）：{ex.GetType().Name}", "Warning");
            try { ShowMainWindow(); } catch { }
        }

        // 网络请求放到界面可见之后：它慢不该让用户对着黑屏等。
        //   「订阅刷新完成 4547/5927 ms」）。原实现是在 **UI 线程**上
        //   `await AppState.RefreshSubscriptionAsync()` —— await 虽不阻塞线程，
        //   但它后面的续体仍排在 UI 队列上，且这 5 秒里窗口刚显示、用户正要点击，
        //   体感就是「打开后卡住不动」。
        //   现改为：**后台线程**跑订阅刷新（不占 UI 线程），完成后再回 UI 线程
        //   刷新界面（RebuildNodes/NodesChanged 会自己走 UI 线程）。
        //   注意：这里刻意**不 await** 它 —— 启动流程不该等网络。
        if (!string.IsNullOrWhiteSpace(AppState.State.SubscriptionUrl))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await AppState.RefreshSubscriptionAsync();
                    App.StartupMark("订阅刷新完成");
                }
                catch (Exception ex)
                {
                    LogBus.App($"启动时订阅刷新失败：{ex.GetType().Name}", "Warning");
                }
            });
        }

        if (AppState.State.AutoConnect)
        {
            var node = AppState.CurrentNode ?? AppState.Nodes.FirstOrDefault();
            if (node != null)
            {
                try
                {
                    var (ok, err) = await ConnectionService.ConnectAsync(node);
                    if (!ok) ShowStatusText(err ?? "自动连接失败");
                }
                catch { }
            }
        }

        // 启动自动测速（设置 → 通用 →「启动自动测速」）：等节点就绪后在后台跑一次，
        // 让节点列表/延迟在用户点开时就是实测值。开关关闭时 Schedule 内部直接返回。
        StartupSpeedTest.Schedule();
    }

    private void UpdateSidebar()
    {
        NavNodesCount.Text = AppState.Nodes.Count.ToString();
        NavSubsCount.Text = AppState.Subscriptions.Count.ToString();
        UpdateConnUi();
    }

    private async Task MaybeAutoUpdateAsync()
    {
        try
        {
            if (!AppState.State.AutoUpdate) return;
            if (string.IsNullOrWhiteSpace(AppState.State.SubscriptionUrl)) return;
            var hours = Math.Clamp(AppState.State.AutoUpdateHours, 1, 24);
            if (AppState.LastFetchLocal is { } t && DateTime.Now - t < TimeSpan.FromHours(hours)) return;
            await AppState.RefreshSubscriptionAsync();
            LogBus.App($"订阅已自动更新（{hours} 小时计划）");
        }
        catch { }
    }

    private async Task MaybeAutoCheckAppUpdateAsync()
    {
        try
        {
            if (!AppState.State.AutoCheckUpdate) return;

            // 节流：距上次检查 ≥6 小时才查。旧实现按「每天某个时间点后一次」判断，
            // 配合 30 分钟定时器行为不稳定；该「检查时间」设置已随本次改动移除。
            var last = AppState.State.LastUpdateCheckUnix;
            if (last > 0 && DateTimeOffset.Now.ToUnixTimeSeconds() - last < 6 * 3600) return;

            var r = await AppUpdateService.CheckAsync();
            AppState.State.LastUpdateCheckUnix = DateTimeOffset.Now.ToUnixTimeSeconds();
            AppState.Save();

            if (!r.Ok)
            {
                LogBus.App($"自动检查更新失败（静默，不打扰用户）：{r.Error}", "Warning");
                return;
            }
            if (!r.HasNew) return;

            // 不弹窗、不强制跳转：设置页显示「发现新版本」与下载按钮，托盘气泡提示一次。
            // （用户在玩游戏 / 全屏 / TUN 下也不会被打断；详情等他主动点设置页。）
            try { EnsureSettingsPage().NotifyUpdateAvailable(r); }
            catch (Exception ex) { LogBus.App($"更新提示写入设置页失败：{ex.GetType().Name}", "Warning"); }
            ShowStatusText($"发现新版本 {r.LatestTag}（设置 → 通用 可更新）");
            try { TrayIcon.ShowNotification("neo 发现新版本", $"neo {r.LatestTag} 已发布，可在「设置 → 通用」更新"); } catch { }
            LogBus.App($"自动检查更新：发现新版本 {r.LatestTag}（已提示，未弹出对话框）");
        }
        catch (Exception ex)
        {
            LogBus.App($"自动检查更新异常（不影响使用）：{ex.GetType().Name} {ex.Message}", "Warning");
        }
    }

    private void UpdateConnUi()
    {
        bool on = ConnectionService.IsConnected;
        bool connecting = ConnectionService.IsConnecting;

        NavConnStatus.Text = connecting && !on ? "连接中" : on ? "已连接" : "未连接";
        var dotColor = connecting && !on
            ? (_dark ? Color.FromArgb(255, 0x4C, 0xC2, 0xFF) : Color.FromArgb(255, 0x25, 0x63, 0xEB))
            : on
                ? Color.FromArgb(255, 0x16, 0xA3, 0x4A)
                : (_dark ? Color.FromArgb(255, 0x6B, 0x72, 0x80) : Color.FromArgb(255, 0x98, 0xA0, 0xAA));
        NavConnDot.Fill = new SolidColorBrush(dotColor);
        NavConnStatus.Foreground = new SolidColorBrush(dotColor);

        UpdateCoreCard();
        UpdateTrayMenu();
    }

    private void UpdateCoreCard()
    {
        bool running = ConnectionService.IsConnected;
        CoreStatusText.Text = running ? "运行中" : "未运行";
        CoreStatusText.Foreground = running
            ? ThemeUtil.Get("NeoGreen", Color.FromArgb(255, 0x16, 0xA3, 0x4A))
            : ThemeUtil.Get("NeoText2", Color.FromArgb(255, 0x6B, 0x72, 0x80));
        CoreStatusBadge.Background = running
            ? ThemeUtil.Get("NeoGreenSoft", Color.FromArgb(0x1A, 0x16, 0xA3, 0x4A))
            : ThemeUtil.Get("NeoChipBg", Color.FromArgb(0x0C, 0x1A, 0x1D, 0x21));
        int? mem = ConnectionService.CoreMemoryMb;
        CoreMemText.Text = mem is { } m ? $"{m} MB" : "—";

        if (ConnectionService.CoreKind == "sing-box")
        {
            SideCoreVersion.Text = ConnectionService.SingBoxVersion is { } sv ? "sing-box " + sv : "sing-box —";
        }
        else
        {
            // 内核被一键更新替换后（ResetXrayVersion）下一次探测会拿到新版本，这里必须随之刷新；
            // 旧守卫只在占位符状态写入一次，导致更新后侧栏永远显示旧版本。
            var ver = ConnectionService.XrayVersion;
            if (ver != null && SideCoreVersion.Text != "xray " + ver)
                SideCoreVersion.Text = "xray " + ver;
        }
    }

    public void ExitForUpdate()
    {
        LogBus.App("neo exit（更新安装）");
        // 更新路径无法 await（调用方是同步方法），用同步版收尾：
        // Stop() 已受 IsShuttingDown 门控保护，不会因未等循环结束而出问题。
        ExitApplication();
    }

    /// 提权重启（TUN 模式需要管理员权限）的退出收尾。与更新安装同理：只做有序收尾、
    /// 由调用方负责启动新实例，因此这里不能走 ExitApplication() 里那句 Exit 之外的差异 ——
    /// 实际上就是用同一套收尾，只是语义上单独命名便于将来分化。
    private void ExitForRelaunch()
    {
        LogBus.App("neo exit（提权重启）");
        ExitApplication();
    }

    /// 统一退出收尾（同步版）。用于无法 await 的调用点（窗口关闭回调 / 更新安装）。
    /// 顺序仍是「先立退出标志 → 停后台活动 → 断连 → Exit」，但不等待探活循环结束。
    private void ExitApplication()
    {
        if (Interlocked.Exchange(ref _exitingOnce, 1) != 0) return;
        _exiting = true;

        // 最先立标志：此后所有迟到的「投递回 UI 线程」都会主动放弃，
        // 而不是去碰正在销毁的 DispatcherQueue（那会以不可捕获的 fail-fast 终止进程）。
        App.IsShuttingDown = true;

        try { StatsSampler.Stop(); } catch { }
        try { FailoverService.Stop(); } catch { }
        try { StopOwnTimersAndDisconnect(); } catch { }
        try { DisposeTrayIcon(); } catch { }

        try { Application.Current.Exit(); } catch { }
    }

    /// 统一退出收尾（异步版）。托盘「退出」走这条：它会等自动故障转移的探活循环
    /// 真正结束，然后才逐项停表、断连、Exit —— 确保 Exit 之后不再有自己启动的
    private async Task ExitApplicationAsync()
    {
        if (Interlocked.Exchange(ref _exitingOnce, 1) != 0) return;
        _exiting = true;
        App.IsShuttingDown = true;

        try { StatsSampler.Stop(); } catch { }
        try { await FailoverService.StopAsync(); } catch { }
        try { StopOwnTimersAndDisconnect(); } catch { }
        try { DisposeTrayIcon(); } catch { }

        try { Application.Current.Exit(); } catch { }
    }

    /// <summary>停掉本窗口自己的计时器、断开连接、落盘用量。</summary>
    private void StopOwnTimersAndDisconnect()
    {
        try { _traySpinTimer?.Stop(); } catch { }
        try { _coreTimer.Stop(); } catch { }
        try { _autoTimer.Stop(); } catch { }
        try { _toastTimer.Stop(); } catch { }

        // 每一页都有自己的计时器，且它们的 Tick 都会写 XAML。
        // 之前只停了 ConnectionPage，其余（订阅页 3 秒、日志页 150ms、节点页防抖、
        // 规则页错误提示、设置页玻璃参数落盘）在 Exit 之后仍会触发 → 同类 fail-fast 风险。
        // 惰性创建后只对「已创建过」的页面停表；不能走页面属性（那会在退出时把页建出来）。
        try { _pageConn?.StopTimers(); } catch { }
        try { _pageSubs?.StopTimers(); } catch { }
        try { _pageLog?.StopTimers(); } catch { }
        try { _pageNodes?.StopTimers(); } catch { }
        try { _pageRule?.StopTimers(); } catch { }
        try { _pageSettings?.StopTimers(); } catch { }

        try { ConnectionService.Disconnect("程序退出"); } catch { }
        try { UsageTracker.Flush(); } catch { }
    }

    /// 释放托盘图标。必须先撤下图标再释放资源，否则图标会残留在通知区域直到鼠标划过。
    /// 同时释放图标句柄（System.Drawing.Icon 持有 HICON，不 Dispose 会泄漏 GDI 句柄）。
    /// 调用时机很关键：本方法只在**真正退出**时调用。若误在「最小化到托盘」路径调用，
    /// 会把用户正要使用的托盘图标弄没。ContextMenuMode 为 SecondWindow 时，
    /// 该模式会额外持有一个隐藏的 WinUI 宿主窗口（见 OnAppWindowClosing 的注释），
    /// 因此这里只撤图标、不依赖它的生命周期来结束进程。
    private void DisposeTrayIcon()
    {
        try { TrayIcon.Dispose(); } catch { }
        try { _trayIconOff?.Dispose(); } catch { }
        try { _trayIconOn?.Dispose(); } catch { }
        if (_traySpinIcons != null)
        {
            foreach (var ic in _traySpinIcons) { try { ic.Dispose(); } catch { } }
        }
    }

    ///         时长 .3s，曲线 cubic-bezier(.2,.8,.2,1)
    ///   这里：位移 **20px** 从下往上滑入 + 淡入，时长 **300ms**，ExponentialEase 近似原曲线
    ///         （起步快、尾部缓 —— WinUI 的 CubicEase 只有一个控制点，无法完整复刻）。
    private void ShowStatusText(string s)
    {
        StatusMain.Text = s;
        StatusDot.Fill = new SolidColorBrush(Color.FromArgb(255, 0x98, 0xA0, 0xAA));
        StatusToast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
        AnimateToastIn();
    }

    private void AnimateToastIn()
    {
        try
        {
            var tt = StatusToast.RenderTransform as TranslateTransform ?? new TranslateTransform();
            StatusToast.RenderTransform = tt;
            StatusToast.Opacity = 0;

            var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            var fade = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = 0, To = 1,
                Duration = TimeSpan.FromMilliseconds(300),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.ExponentialEase
                { Exponent = 3, EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut },
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fade, StatusToast);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fade, "Opacity");

            var slide = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = 20, To = 0,
                Duration = TimeSpan.FromMilliseconds(300),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.ExponentialEase
                { Exponent = 3, EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut },
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(slide, tt);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(slide, "Y");

            sb.Children.Add(fade);
            sb.Children.Add(slide);
            sb.Begin();
        }
        catch { }
    }
}

internal sealed class RelayCommand : System.Windows.Input.ICommand
{
    private readonly Action _action;

    public RelayCommand(Action action) => _action = action;

    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _action();
}
