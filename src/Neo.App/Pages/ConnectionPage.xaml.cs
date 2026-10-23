using Neo.Core;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Neo.App.Services;
using Neo.Core.Core;
using Neo.Core.Models;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Text;

namespace Neo.App.Pages;

public sealed partial class ConnectionPage : UserControl
{
    private static readonly SolidColorBrush Transparent = new(Color.FromArgb(0, 0, 0, 0));
    private static readonly SolidColorBrush OnTextLight = new(Colors.White);
    private static readonly SolidColorBrush OnTextDark = new(Color.FromArgb(255, 0x0B, 0x10, 0x15));

    private string _mode = "rule";
    private bool _syncing;

    private readonly Microsoft.UI.Xaml.DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    /// <summary>上一次刷新时看到的「已连接节点」，用于发现后台无感切换。</summary>
    private Neo.Core.Models.NodeItem? _lastConnectedNodeSeen;
    /// <summary>切换提示开始显示的时间（用于保证最短可见时长）。</summary>
    private DateTime _switchHintShownAt;

    private async Task HideSwitchHintAsync(int delayMs)
    {
        try
        {
            if (delayMs > 0) await Task.Delay(delayMs);
            if (App.IsShuttingDown) return;
            RingSwitchHint.Text = "";
            RingSwitchHint.Opacity = 0;
        }
        catch { }
    }
    private CoreStats.Snapshot? _lastSnap;
    private DateTime _lastSnapAt;
    private long _peakDownBps;

    private const int MaxChartPoints = 900;
    private const double PlotLeft = 10;
    private const double CellSize = 9;
    private const double CellGap = 2;
    private const double BandGap = 20;
    /// <summary>窗口是否可见（最小化到托盘 / 最小化 / 恢复）。由 MainWindow 推送。</summary>
    private bool _windowVisible = true;
    /// <summary>Activated 是否已订阅（改到 Loaded 里订阅，见 HookWindowActivation）。</summary>
    private bool _activatedHooked;
    private readonly List<DateTime> _times = new();
    private readonly List<double> _dnHist = new();
    private readonly List<double> _upHist = new();

    public event EventHandler? OpenNodesRequested;

    public event EventHandler<string>? ConnectionFailed;

    private Microsoft.UI.Xaml.Media.Animation.Storyboard? _ringPulseSb;
    private Microsoft.UI.Xaml.Media.Animation.Storyboard? _ringBreathSb;
    /// 「刚连上」只播一次生长/波纹/填充动画的闸门。
    /// 为什么需要：UpdateStatusRing 每次状态刷新都会跑，若每次都重播，
    /// 界面会反复出现波纹与生长，观感变成闪烁。断开时复位。
    private bool _ringConnectedAnimDone;

    /// 圆环当前已经呈现的视觉阶段：-1 未知 / 0 未连接 / 1 连接中 / 2 已连接。
    /// 为什么需要它：UpdateStatusRing 由 RefreshConnUi 驱动，连接中每秒会跑很多次；
    /// 若每次都 ApplyRingDash + Storyboard.Begin()，「充能」动画会被反复重置到 0
    /// 页面隐藏时 StopRingStoryboards 会把它复位成 -1，恢复可见时重新布景。
    private int _ringVisualState = -1;

    public ConnectionPage()
    {
        InitializeComponent();
        SetMode("rule");
        ResetChart();
        ActualThemeChanged += (_, _) => SetMode(_mode);
        ConnectionService.StateChanged += RefreshConnUi;
        AppState.NodesChanged += RefreshConnUi;
        //   还是 null（MainWindow 尚在构造），旧代码从未订阅成功，失焦暂停从未生效。
        RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => ApplyPageVisibility());
        Loaded += (_, _) =>
        {
            HookWindowActivation();
            RefreshConnUi();
            RefreshStats();
            // Loaded 触发时画布可能尚未完成布局（ActualWidth=0），延迟一拍等真实尺寸再首绘
            DispatcherQueue.TryEnqueue(() =>
            {
                if (App.IsShuttingDown) return;
                try
                {
                    RedrawChart();
                }
                catch (Exception ex) { LogBus.Exception("图表首绘失败", ex); }
            });
        };
        _statsTimer.Tick += (_, _) => RefreshStats();
        _statsTimer.Start();

        // 2026-10-23：无感切换提示 —— 显示在圆环右侧留白处（用户要求：只显示「正在切换节点…」，
        // 且不能压住圆环内的状态文字/节点行）。
        ConnectionService.SwitchProgress += msg => DispatcherQueue.TryEnqueue(() =>
        {
            if (App.IsShuttingDown) return;
            try
            {
                if (!string.IsNullOrEmpty(msg))
                {
                    _switchHintShownAt = DateTime.Now;
                    RingSwitchHint.Text = msg;
                    RingSwitchHint.Opacity = 0.9;
                }
                else
                {
                    // 实测切换只要 0.6 秒左右：如果立刻清空，提示一闪而过根本看不见。
                    // 强制至少显示 1.2 秒，之后淡出。
                    var shown = (DateTime.Now - _switchHintShownAt).TotalMilliseconds;
                    var delay = (int)Math.Max(0, 1200 - shown);
                    _ = HideSwitchHintAsync(delay);
                }
            }
            catch { }
        });
    }

    /// 退出时停掉本页所有自己的计时器。
    /// Application.Current.Exit() 之后 XAML 与 DispatcherQueue 进入销毁流程，
    /// 仍在跑的 DispatcherTimer 回调会去碰已失效的 UI 对象 / 队列，
    /// 触发 CoreMessaging fail-fast（0xc000027b）——本机已有此崩溃实例。
    public void StopTimers()
    {
        try { _statsTimer.Stop(); } catch { }
        try { _resizeDebounce?.Stop(); } catch { }
        StopRingStoryboards();
    }

    private void RefreshStats()
    {
        if (!ConnectionService.IsConnected)
        {
            _lastSnap = null;
            _peakDownBps = 0;
            if (_dnHist.Count > 0 || _upHist.Count > 0)
                ResetChart();

            if (!IsPageVisible())
            {
                UpdateRingTime();
                return;
            }

            UpSpeedText.Text = "0"; UpSpeedUnit.Text = "B/s";
            DownSpeedText.Text = "0"; DownSpeedUnit.Text = "B/s";
            ConnCountText.Text = "—";
            TotalUpText.Text = "0"; TotalUpUnit.Text = "B";
            TotalDownText.Text = "0"; TotalDownUnit.Text = "B";
            StatsCoreMemText.Text = "—";
            PeakText.Text = "未连接";
            RingTimeText.Text = "00:00:00";
            return;
        }

        // 2026-10-23 无感切换：内核在后台换掉了（节点变了），但没有任何点击/导航事件，
        // 界面必须自己发现「已连接节点变了」并刷新 —— 否则圆环还显示旧节点名。
        var nowConnected = ConnectionService.ConnectedNode;
        if (!ReferenceEquals(_lastConnectedNodeSeen, nowConnected))
        {
            _lastConnectedNodeSeen = nowConnected;
            try { RefreshConnUi(); } catch { }
        }

        int? mem = ConnectionService.CoreMemoryMb;

        // 读后台采样器的缓存，不在 UI 线程上取数。
        var snap = StatsSampler.Latest;
        if (snap is not { } cur) return;

        bool hasPrev = _lastSnap is not null;
        long upBps = 0, downBps = 0;
        if (_lastSnap is { } prev)
        {
            double dt = Math.Max((DateTime.Now - _lastSnapAt).TotalSeconds, 0.001);
            // P2-4（审计）：原来除以 Math.Max(1, (long)dt) —— 采样抖动到 1.4 秒时会被当成 1 秒，
            // 速度高估约 40%。改为按真实浮点间隔算，并对极小间隔设下限避免除爆。
            upBps = (long)(Math.Max(0, cur.Uplink - prev.Uplink) / Math.Max(0.2, dt));
            downBps = (long)(Math.Max(0, cur.Downlink - prev.Downlink) / Math.Max(0.2, dt));
            if (downBps > _peakDownBps) _peakDownBps = downBps;

            // 数据入库与"看不看得见"解耦：窗口最小化到托盘 / 窗口在后台 / 切到别的页面时，
            // 依旧每秒把采样写进图表历史；否则用户点开时图表从"现在"重新开始，中间那段直接缺失。
            // 重绘由 PushChart 内部的 IsPageVisible 拦住，看不见时只记录不绘制。
            PushChart(upBps, downBps);
        }
        _lastSnap = cur;
        _lastSnapAt = DateTime.Now;

        if (!IsPageVisible())
        {
            UpdateRingTime();
            return;
        }

        StatsCoreMemText.Text = mem is { } m ? m.ToString() : "—";

        if (hasPrev)
        {
            var (uv, uu) = SplitBytes(upBps);
            UpSpeedText.Text = uv; UpSpeedUnit.Text = uu + "/s";
            var (dv, du) = SplitBytes(downBps);
            DownSpeedText.Text = dv; DownSpeedUnit.Text = du + "/s";

            var (pv, pu) = SplitBytes(_peakDownBps);
            PeakText.Text = $"峰值 ↓ {pv} {pu}/s";
        }

        var (tv, tu) = SplitBytes(cur.Uplink);
        TotalUpText.Text = tv; TotalUpUnit.Text = tu;
        var (dv2, du2) = SplitBytes(cur.Downlink);
        TotalDownText.Text = dv2; TotalDownUnit.Text = du2;

        ConnCountText.Text = StatsSampler.ActiveConnections.ToString();

        UpdateRingTime();
        UpdateCoreCardUi();
    }

    /// 内核种类/版本来自 ConnectionService，内存来自 CoreMemoryMb；固定出口生效时显示入口数。
    private void UpdateCoreCardUi()
    {
        try
        {
            bool running = ConnectionService.IsConnected;
            var green = GetThemeBrush("NeoGreen", Color.FromArgb(255, 0x16, 0xA3, 0x4A));
            var text2 = GetThemeBrush("NeoText2", Color.FromArgb(255, 0x9A, 0x9A, 0xA0));

            CoreStateText.Text = running ? "内核运行中" : "内核未运行";
            CoreStateText.Foreground = running ? green : text2;
            CoreDot.Fill = running ? green : text2;

            bool sing = ConnectionService.CoreKind == "sing-box";
            bool mihomo = ConnectionService.CoreKind == "mihomo";
            CoreKindText.Text = mihomo ? "mihomo" : (sing ? "sing-box" : "xray");
            CoreVersionText.Text = mihomo
                ? (KernelCatalog.ReadVersion(CoreEngine.Mihomo) ?? "—")
                : (sing ? (ConnectionService.SingBoxVersion ?? "—") : (ConnectionService.XrayVersion ?? "—"));

            var fixedExit = running && ConnectionService.FixedExitActive ? ConnectionService.FixedExitEntries + 1 : 0;
            CoreTagText.Text = fixedExit > 0 ? "固定出口" : (running ? ConnectionService.CoreKind : "—");

            CoreStrategyText.Text = fixedExit > 0
                ? $"出口固定 · {ConnectionService.FixedExitName}"
                : (running ? "单节点" : "—");

            CoreNodesText.Text = fixedExit > 0
                ? $"{fixedExit} / {AppState.Nodes.Count}"
                : (running ? $"1 / {AppState.Nodes.Count}" : "—");
        }
        catch { }
    }

    /// 这里改用「连接时刻 → 现在」的差值，避免计时器漂移与断线后继续累加。
    private void UpdateRingTime()
    {
        try
        {
            var t = ConnectionService.IsConnected && ConnectionService.ConnectedAt is { } at
                ? DateTime.Now - at
                : TimeSpan.Zero;
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            int totalSec = (int)t.TotalSeconds;
            var txt = $"{totalSec / 3600:00}:{totalSec % 3600 / 60:00}:{totalSec % 60:00}";
            if (RingTimeText.Text != txt) RingTimeText.Text = txt;
        }
        catch { }
    }

    private static (string Value, string Unit) SplitBytes(long bytes)
    {
        if (bytes < 1024) return (bytes.ToString(), "B");
        if (bytes < 1024 * 1024) return ((bytes / 1024.0).ToString("0.#"), "KB");
        if (bytes < 1024L * 1024 * 1024) return ((bytes / 1024.0 / 1024).ToString("0.##"), "MB");
        return ((bytes / 1024.0 / 1024 / 1024).ToString("0.##"), "GB");
    }

    // 曲线专用 EWMA：TCP 突发让秒级差分在 0 与峰值间来回跳，原图直接画原始值锯齿刺眼。
    // 数字区与峰值仍用原始瞬时值，只有进图数据走平滑（alpha=0.35，约 5 个采样点收敛 90%，平滑与响应兼顾）。
    private const double ChartSmoothAlpha = 0.35;
    // GitHub 风方块柱状图：每列=一个时间片（X 轴从左往右时间递增），列内方块自底部堆叠，
    // 高度=流量档位（固定 8 档 Y 轴，位置永不跳动），颜色随行升高变浓。上下两条带：蓝=下载 / 橙=上传。
    private const int ChartRows = 8;
    //   改成 1 秒/列后，图表按秒推进；可见时间窗从约 6 分钟缩到约 2 分钟（列数不变）。
    //   采样本来就是 1 秒一次（_statsTimer），所以这里只改聚合粒度，不增加采集开销。
    private const int SlotSeconds = 1;
    private static readonly Windows.UI.Color[] DnPalette =
    {
        Windows.UI.Color.FromArgb(255, 0x2F, 0x3B, 0x4A),   // 空（提亮到背景可辨，网格常驻可见）
        Windows.UI.Color.FromArgb(255, 0x2b, 0x4a, 0x70),   // 行1 淡
        Windows.UI.Color.FromArgb(255, 0x33, 0x61, 0x9e),
        Windows.UI.Color.FromArgb(255, 0x3b, 0x78, 0xc6),
        Windows.UI.Color.FromArgb(255, 0x44, 0x8e, 0xe6),
        Windows.UI.Color.FromArgb(255, 0x55, 0xa1, 0xf5),
        Windows.UI.Color.FromArgb(255, 0x6c, 0xb2, 0xff),
        Windows.UI.Color.FromArgb(255, 0x8a, 0xc2, 0xff),
        Windows.UI.Color.FromArgb(255, 0xaa, 0xd2, 0xff),   // 行8 最亮最浓
    };
    private static readonly Windows.UI.Color[] UpPalette =
    {
        Windows.UI.Color.FromArgb(255, 0x2F, 0x3B, 0x4A),
        Windows.UI.Color.FromArgb(255, 0x6b, 0x4a, 0x1a),
        Windows.UI.Color.FromArgb(255, 0x8a, 0x5f, 0x20),
        Windows.UI.Color.FromArgb(255, 0xa8, 0x74, 0x26),
        Windows.UI.Color.FromArgb(255, 0xc6, 0x89, 0x2c),
        Windows.UI.Color.FromArgb(255, 0xe0, 0x9b, 0x32),
        Windows.UI.Color.FromArgb(255, 0xf5, 0xad, 0x3d),
        Windows.UI.Color.FromArgb(255, 0xff, 0xbe, 0x4d),
        Windows.UI.Color.FromArgb(255, 0xff, 0xd1, 0x66),
    };
    private static readonly SolidColorBrush[] DnBrushes =
        DnPalette.Select(c => new SolidColorBrush(c)).ToArray();
    private static readonly SolidColorBrush[] UpBrushes =
        UpPalette.Select(c => new SolidColorBrush(c)).ToArray();
    private readonly List<Microsoft.UI.Xaml.Shapes.Rectangle> _dnCells = new();
    private readonly List<Microsoft.UI.Xaml.Shapes.Rectangle> _upCells = new();
    private int _cellsCols = -1;
    private DateTime? _sessionStart;
    // 渲染降本：方块尺寸/位置只在布局失效时设置一次，Fill 相同则跳过 ——
    // 每秒常态重绘从上万次依赖属性写入降到几百次。
    private bool _layoutDirty = true;
    private int[]? _dnLvCache;
    private int[]? _upLvCache;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _resizeDebounce;

    private void PushChart(long upBps, long downBps)
    {
        _sessionStart ??= DateTime.Now;
        _dnHist.Add(downBps);
        _upHist.Add(upBps);
        _times.Add(DateTime.Now);

        //   List.RemoveAt(0) 是整体搬移 O(n)。连接满 15 分钟（900 点）后就进入稳态：
        //   改成「超到 2 倍上限才一次性砍掉一半」：摊还后每秒几乎不搬移，
        if (_dnHist.Count > MaxChartPoints * 2)
        {
            int drop = _dnHist.Count - MaxChartPoints;
            _dnHist.RemoveRange(0, drop);
            _upHist.RemoveRange(0, drop);
            _times.RemoveRange(0, drop);
        }
        // 只要页面可见就重绘：取消失焦停画，窗口在后台（未聚焦）时图表也继续走。
        if (IsPageVisible()) RedrawChart();
    }

    /// 本页当前是否真的显示着。用于跳过「看不见的重绘」：
    /// 每秒一次的全量重排对本页是常态开销，隐藏时完全应当省掉。
    private bool IsPageVisible() => Visibility == Visibility.Visible && _windowVisible;

    private void ResetChart()
    {
        _dnHist.Clear();
        _upHist.Clear();
        _times.Clear();
        _sessionStart = null;
        RedrawChart();
    }

    /// 固定 8 档 Y 轴（阈值单位 MB/s）。下载顶格 = 20MB/s；上传整条刻度缩为 1/4，顶格 = 5MB/s。
    private static readonly double[] DnThresholds = { 1, 3, 7, 8.5, 10, 15, 20 };
    private static readonly double[] UpThresholds = DnThresholds.Select(t => t * 0.25).ToArray();

    private static int LevelOf(double bytesPerSec, double[] th)
    {
        if (bytesPerSec <= 0) return 0;
        double mb = bytesPerSec / (1024.0 * 1024);
        for (int i = 0; i < th.Length; i++)
            if (mb < th[i]) return i + 1;
        return th.Length + 1;
    }

    /// 方块池：按列数建一次（每条带 cols*rows 个），列数变化时重建。
    private void EnsureCells(int cols, double plotH)
    {
        // 只在「列数变多」时重建池子，变少时复用已有方块（多出来的隐藏起来）。
        // 原实现是 `if (_cellsCols != cols)` —— 只要列数不等就推倒重建，
        // 于是「隐藏 → 变窄 → 可见 → 变宽」这条路径会重建两次（第二次要新建近两千个
        // Rectangle），全部压在 UI 线程上。改为只在需要更多列时才扩容，
        // 切页回流就不会再触发整池重建；方块的几何仍按当前真实列数重排。
        if (_cellsCols < cols)
        {
            if (_cellsCols > 0)
            {
                foreach (var c in _dnCells) ChartCanvas.Children.Remove(c);
                foreach (var c in _upCells) ChartCanvas.Children.Remove(c);
                _dnCells.Clear();
                _upCells.Clear();
            }
            _cellsCols = cols;
            var total = cols * ChartRows;
            for (int i = 0; i < total; i++)
            {
                var dn = new Microsoft.UI.Xaml.Shapes.Rectangle { RadiusX = 2, RadiusY = 2 };
                var up = new Microsoft.UI.Xaml.Shapes.Rectangle { RadiusX = 2, RadiusY = 2 };
                Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(dn, 10);
                Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(up, 10);
                ChartCanvas.Children.Add(dn);
                ChartCanvas.Children.Add(up);
                _dnCells.Add(dn);
                _upCells.Add(up);
            }
            _dnLvCache = new int[total];
            _upLvCache = new int[total];
            // 缓存初值必须为 -1：0 会被填色循环当作"已是空槽色"而跳过赋值，
            // 而 Rectangle 的 Fill 默认为 null —— 空槽方块将永远隐形。
            for (int i = 0; i < total; i++) { _dnLvCache[i] = -1; _upLvCache[i] = -1; }
            _layoutDirty = true;
        }
        // 否则它们会停在原位继续显示（画面出现多余的方块）。
        if (_cellsCols > cols) _layoutDirty = true;

        if (_layoutDirty)
        {
            // 尺寸与位置只在布局失效时写一次（建池 / 窗口尺寸变化），常态重绘不碰几何
            double bandH = ChartRows * (CellGap + CellSize) - CellGap;
            double bandTopDn = Math.Max(4, (plotH - (bandH * 2 + BandGap)) / 2);
            double bandTopUp = bandTopDn + bandH + BandGap;

            // Canvas 不参与布局，位置只能手算 —— 放在下载带底部与上传带顶部之间的空隙，
            // 留 2px 上边距，避免与下载带最底一排方块贴在一起。
            try
            {
                Microsoft.UI.Xaml.Controls.Canvas.SetLeft(UpBandLabel, PlotLeft);
                Microsoft.UI.Xaml.Controls.Canvas.SetTop(UpBandLabel, bandTopDn + bandH + 2);
            }
            catch { }
            for (int c = 0; c < cols; c++)
            {
                for (int r = 0; r < ChartRows; r++)
                {
                    int cellIdx = c * ChartRows + r;
                    var dn = _dnCells[cellIdx];
                    var up = _upCells[cellIdx];
                    dn.Visibility = Visibility.Visible;
                    up.Visibility = Visibility.Visible;
                    dn.Width = CellSize;
                    dn.Height = CellSize;
                    up.Width = CellSize;
                    up.Height = CellSize;
                    double x = PlotLeft + c * (CellGap + CellSize);
                    Microsoft.UI.Xaml.Controls.Canvas.SetLeft(dn, x);
                    Microsoft.UI.Xaml.Controls.Canvas.SetTop(dn, bandTopDn + (ChartRows - 1 - r) * (CellGap + CellSize));
                    Microsoft.UI.Xaml.Controls.Canvas.SetLeft(up, x);
                    Microsoft.UI.Xaml.Controls.Canvas.SetTop(up, bandTopUp + (ChartRows - 1 - r) * (CellGap + CellSize));
                }
            }
            for (int i = cols * ChartRows; i < _dnCells.Count; i++)
            {
                _dnCells[i].Visibility = Visibility.Collapsed;
                _upCells[i].Visibility = Visibility.Collapsed;
            }
            _layoutDirty = false;
        }
    }

    private const double RingBaseSize = 236;

    /// 用来把圆环按同一比例随卡宽放大，避免宽窗口下环显得又小又空。
    private const double RingReferenceCardWidth = 758;

    /// 按 hero 卡实际宽度缩放圆环（等比、有上限）。
    /// neo 铺满 1936 后左栏约 1272，若环固定 236 就只占卡宽 18.6%，
    /// 环周围横向空一大片 —— 那也是「留白很多」的一种。
    ///   在 1936 窗口中，1.3 → 圆环 307px、hero 卡总高 **378px**，
    ///   上限 1.3 是按 1936x1048 的屏幕定的。矮屏（1366x768 工作区仅约 728px）
    ///   连 1.0 都放不下，故再加一道**高度约束**：可用高度越矮，环压得越小。
    ///   见 RingMaxScaleForHeight —— 它把「按宽度算出的比例」再按可用高夹一次。
    private const double RingMaxScale = 1.3;

    /// 环在「矮屏」下的最小比例。
    ///   这个常量仅为兼容保留，当前**不再参与计算**（见 UpdateRingScaleByWidthOnly）。
    private const double RingMinScale = 0.62;

    /// 本页尺寸变化 → 重算圆环比例。
    ///   关闭也抖、点不动」）：
    ///   我上一版让环比例同时受**可用高度**约束，于是形成**布局反馈环** ——
    ///     环变大 ⇒ 内容变高 ⇒ 超出视口 ⇒ 出现滚动条 ⇒ 可用宽变小 ⇒ 重算比例 ⇒ 环变小
    ///     ⇒ 内容变矮 ⇒ 滚动条消失 ⇒ 宽度恢复 ⇒ 环又变大 ⇒ 无限往复
    ///   现在：环比例**只由宽度决定**（宽度变化不影响内容高度，因此不构成环），
    ///   高度不足时由外层的 ScrollViewer 正常滚动兜底 —— 宁可滚动，也不要抖动。
    ///   这也修掉了「顺带点不动」：不再有反复重设尺寸的事件循环。
    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        try
        {
            // 只依赖宽度。页面宽 = 左列(hero 卡所在) + 间距 14 + 右栏 372，
            // 故 hero 卡宽 = 页面宽 - 372 - 14。
            if (e.NewSize.Width <= 1) return;
            double cardW = e.NewSize.Width - 372 - 14;
            if (cardW <= 1) return;
            UpdateRingScaleByWidthOnly(cardW);
        }
        catch { }
    }

    private void OnHeroCardSizeChanged(object sender, SizeChangedEventArgs e)
    {
        try
        {
            double cardW = e.NewSize.Width;
            if (cardW <= 1) return;
            UpdateRingScaleByWidthOnly(cardW);
        }
        catch { }
    }

    /// 按 hero 卡宽重算环比例。**只看宽度**，不读 ActualHeight ——
    /// 读高度会构成「改尺寸 → 改高度 → 再改尺寸」的反馈环（见 OnPageSizeChanged 的注释）。
    /// 注意宽度本身也受滚动条影响（出现滚动条会少约 12px），但那只会让环略微变小、
    /// 不会再反向放大，且每次变化都收敛（scale 变化小于 0.01 时直接返回），因此不会震荡。
    private void UpdateRingScaleByWidthOnly(double cardW)
    {
        if (cardW <= 1) return;
        double inner = Math.Max(1, cardW - 52);   // 减去卡片左右 padding 26+26
        double scale = Math.Clamp(inner / RingReferenceCardWidth, 1.0, RingMaxScale);

        if (Math.Abs(_ringScale - scale) < 0.01) return;
        _ringScale = scale;

        // Viewbox 目标尺寸（会把内部 236 的环内容等比放大并按该尺寸占位）
        RingBox.Width = RingBaseSize * scale;
        RingBox.Height = RingBaseSize * scale;
        // 命中层叠在 Viewbox 外层，必须跟着放大，否则环边缘点不动
        RingHit.Width = RingBaseSize * scale;
        RingHit.Height = RingBaseSize * scale;
    }

    /// 语义：
    ///   · 未连接 → 用当前节点连接；若无节点则提示去节点页选
    ///   · 已连接 → 断开（不做二次确认：圆环就是主开关，断开是可恢复动作）
    ///   · 连接中 → 忽略（避免重复发起；ConnectAsync 内部也有 IsConnecting 保护）
    /// 为什么不直接在 XAML 里绑 ConnectAsync：要复用既有的错误通道
    /// ConnectionFailed（MainWindow 用它弹提示），失败时用户才看得到原因。
    private async void OnRingTapped(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ConnectionService.IsConnecting) return;

            if (ConnectionService.IsConnected)
            {
                var sel = AppState.CurrentNode;
                var conn = ConnectionService.ConnectedNode;
                // 2026-10-23 无感切换：已连接、但选中的是另一个节点时，点圆环 = 切换到它，
                // 而不是「先断开再连」（那样会断网 3～4 秒）。选中的就是当前节点时才断开。
                if (sel != null && (conn == null
                    || !string.Equals(AppState.NodeKey(sel), AppState.NodeKey(conn), StringComparison.Ordinal)))
                {
                    var (swOk, swErr) = await ConnectionService.SwitchNodeFastAsync(sel);
                    if (!swOk) ConnectionFailed?.Invoke(this, swErr ?? "切换失败");
                    RefreshConnUi();
                    return;
                }
                ConnectionService.Disconnect("用户点击圆环断开");
                RefreshConnUi();
                return;
            }

            var node = AppState.CurrentNode ?? AppState.Nodes.FirstOrDefault();
            if (node == null)
            {
                ConnectionFailed?.Invoke(this, "未选择节点 — 先去「节点」页选一个");
                return;
            }

            var (ok, err) = await ConnectionService.ConnectAsync(node);
            if (!ok) ConnectionFailed?.Invoke(this, err ?? "连接失败");
        }
        catch (Exception ex)
        {
            LogBus.Exception("圆环点击连接失败", ex);
        }
    }

    /// 代价：窗口更宽时左栏会相应变宽 —— 这是「铺满宽窗口」的必然结果，
    private void OnHomeGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        try
        {
            if (Math.Abs(RightRailCol.Width.Value - 372) > 0.5)
                RightRailCol.Width = new GridLength(372);
        }
        catch { }
    }

    private double _ringScale = 1.0;

    private void OnChartSizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
    {
        // 手动拖拽缩放时本事件每帧触发；若每帧全量重排两千多个方块会把 UI 线程打爆（拖拽卡顿）。
        // 防抖：拖拽期间不重绘，停手 150ms 后重排一次。
        if (_resizeDebounce == null)
        {
            _resizeDebounce = DispatcherQueue.CreateTimer();
            _resizeDebounce.Interval = TimeSpan.FromMilliseconds(150);
            _resizeDebounce.IsRepeating = false;
            _resizeDebounce.Tick += (_, _) =>
            {
                _resizeDebounce.Stop();
                _layoutDirty = true;
                RedrawChart();
            };
        }
        _resizeDebounce.Start();
    }

    private (DateTime Time, double Dn, double Up, int LvDn, int LvUp)?[]? _colInfo;

    private void RedrawChart()
    {
        // 防抖窗口期内跳过（拖拽进行中），松手后的防抖 Tick 会补一次完整重排
        if (_resizeDebounce is { IsRunning: true }) return;

        // 此时若照常重绘，下面那句 Math.Max(200, ...) 会兜底成 200px 宽，
        // 算出 cols ≈ 17，于是 EnsureCells 把方块池**重建**为 17 列（删 2×2×8 个、建 17×8 个）；
        // 等页面重新可见、宽度回到 ~1340px，cols 变回 ~120，池子又被**整体重建一次**
        //（先 Remove 掉全部旧 Rectangle，再新建 2×120×8 ≈ 1900 个）。
        // 而这一大段工作全发生在 UI 线程上、正好落在用户看到切换的那一刻。
        // 修法：隐藏期间干脆不画 —— 看不见的重绘没有意义，且能避免池子被降级重建。
        // 保留 _layoutDirty=true，等宽度恢复后一次性按真实尺寸建池。
        if (ChartCanvas.ActualWidth <= 1) return;

        // 画布真实尺寸（不再经 Viewbox 拉伸，方块永远正方）
        double plotW = Math.Max(200, ChartCanvas.ActualWidth - PlotLeft - 8);
        double plotH = Math.Max(80, ChartCanvas.ActualHeight - 12);

        int cols = Math.Max(8, (int)((plotW - PlotLeft) / (CellGap + CellSize)));
        EnsureCells(cols, plotH);

        // 每列 = SlotSeconds 秒时间片（取片内最大速度）；散点标记：只在档位对应的高度亮一个方块，
        // 同流量永远同一行（Y 轴固定 8 档不跳动），旧列从左侧滑出。
        int n = _dnHist.Count;
        // 复用同一块列信息数组：本方法每秒跑一次，原实现每次 new 一个 108 项可空元组数组
        // （约 4.3KB/秒，约 15.5MB/小时的 gen0 垃圾）。只有列数变化（窗口缩放）时才重建。
        if (_colInfo == null || _colInfo.Length != cols)
            _colInfo = new (DateTime Time, double Dn, double Up, int LvDn, int LvUp)?[cols];
        else
            Array.Clear(_colInfo, 0, cols);
        var info = _colInfo;
        if (_sessionStart is { } ss && n > 0)
        {
            int latestSlotS = (int)((_times[^1] - ss).TotalSeconds / SlotSeconds);
            int firstCol = latestSlotS - cols + 1;
            for (int i = 0; i < n; i++)
            {
                int s = (int)((_times[i] - ss).TotalSeconds / SlotSeconds);
                int c = s - firstCol;
                if (c < 0 || c >= cols) continue;
                if (info[c] is { } cur)
                    info[c] = (cur.Time, Math.Max(cur.Dn, _dnHist[i]), Math.Max(cur.Up, _upHist[i]), 0, 0);
                else
                    info[c] = (_times[i], _dnHist[i], _upHist[i], 0, 0);
            }
            for (int c = 0; c < cols; c++)
                if (info[c] is { } v)
                    info[c] = (v.Time, v.Dn, v.Up, LevelOf(v.Dn, DnThresholds), LevelOf(v.Up, UpThresholds));
        }
        _colInfo = info;

        for (int c = 0; c < cols; c++)
        {
            int lvDn = info[c]?.LvDn ?? 0;
            int lvUp = info[c]?.LvUp ?? 0;
            for (int r = 0; r < ChartRows; r++)
            {
                int cellIdx = c * ChartRows + r;
                if (cellIdx >= _dnCells.Count) break;
                // 散点标记：只亮档位对应的那一格（第 lv 格），其余暗；
                // Fill 同值跳过，避免每秒对两千多个方块重复写依赖属性。
                int shownDn = (lvDn > 0 && r == lvDn - 1) ? lvDn : 0;
                int shownUp = (lvUp > 0 && r == lvUp - 1) ? lvUp : 0;
                if (_dnLvCache![cellIdx] != shownDn)
                {
                    _dnCells[cellIdx].Fill = DnBrushes[shownDn];
                    _dnLvCache[cellIdx] = shownDn;
                }
                if (_upLvCache![cellIdx] != shownUp)
                {
                    _upCells[cellIdx].Fill = UpBrushes[shownUp];
                    _upLvCache[cellIdx] = shownUp;
                }
            }
        }

    }

    private void OnChartPointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_colInfo == null) { ChartTip.Visibility = Visibility.Collapsed; return; }
        var pos = e.GetCurrentPoint(ChartCanvas).Position;
        int c = (int)((pos.X - PlotLeft) / (CellGap + CellSize));
        if (c < 0 || c >= _colInfo.Length || _colInfo[c] is not { } v)
        {
            ChartTip.Visibility = Visibility.Collapsed;
            return;
        }
        TipTime.Text = "时间 " + v.Time.ToString("HH:mm:ss");
        TipDn.Text = $"↓ 下载 {HumanRate(v.Dn)}（第 {v.LvDn} 档）";
        TipUp.Text = $"↑ 上传 {HumanRate(v.Up)}（第 {v.LvUp} 档）";

        ChartTip.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        double left = pos.X + 12;
        if (left + ChartTip.ActualWidth > ChartCanvas.ActualWidth) left = pos.X - ChartTip.ActualWidth - 12;
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(ChartTip, left);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(ChartTip, Math.Max(0, pos.Y - 40));
        ChartTip.Visibility = Visibility.Visible;
    }

    private void OnChartPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        ChartTip.Visibility = Visibility.Collapsed;
    }

    private void OnWinActivated(object sender, WindowActivatedEventArgs e)
    {
        // 取消"失焦就停画"：窗口在后台（未聚焦）时图表也继续推进。
        // 这里只在重新拿到焦点时补一次完整重绘，修正窗口被遮挡期间可能漏掉的帧。
        if (e.WindowActivationState != WindowActivationState.Deactivated)
        {
            try { RedrawChart(); } catch { }
        }
    }

    /// 窗口显示/隐藏（最小化到托盘、标题栏最小化、从托盘恢复）。由 MainWindow 推送。
    /// 隐藏时停掉 1 秒统计表与环/脉冲/彗尾的无限 Storyboard，恢复时按当前连接状态重放。
    public void OnWindowVisibilityChanged(bool visible)
    {
        if (_windowVisible == visible) return;
        _windowVisible = visible;
        ApplyPageVisibility();
    }

    /// <summary>第一次 Loaded 时订阅窗口激活事件（此时 MainWindowInstance 已就绪）。</summary>
    private void HookWindowActivation()
    {
        if (_activatedHooked) return;
        try
        {
            if (App.MainWindowInstance is { } mw)
            {
                mw.Activated += OnWinActivated;
                _activatedHooked = true;
            }
        }
        catch { }
    }

    /// 按「页面可见 + 窗口可见」统一停/启本页的后台活动：
    /// 1 秒统计表、图表重绘、环/脉冲/彗尾 Storyboard。
    private void ApplyPageVisibility()
    {
        if (App.IsShuttingDown) return;

        // 1 秒统计表始终运行：窗口隐藏（最小化到托盘）/切到别的页面时，
        // 仍然要每秒把流量写进图表历史 —— 后台也要一直记录。
        // 该计时器每秒只做几次差分与入列；重绘由 IsPageVisible 拦住，看不见时不画。
        try { _statsTimer.Start(); } catch { }

        if (IsPageVisible())
        {
            RefreshConnUi();
            RefreshStats();
            try { RedrawChart(); } catch { }
        }
        else
        {
            StopRingStoryboards();
        }
    }

    private void StopRingStoryboards()
    {
        try { _ringPulseSb?.Stop(); } catch { }
        try { _ringBreathSb?.Stop(); } catch { }
        _ringVisualState = -1;
    }

    private static string HumanRate(double bps)
    {
        if (bps < 1024) return $"{bps:0} B/s";
        if (bps < 1024 * 1024) return $"{bps / 1024:0.#} KB/s";
        return $"{bps / 1024 / 1024:0.#} MB/s";
    }

    /// 删除理由（当时的真实逻辑）：
    ///   · 连接成功后 ConnectAsync 里是**无条件** `SystemProxy.Enable()`（非 TUN 时）；
    ///   · 而开关本身是「拨开=连接」，两种状态最终都落到同一个 Enable()；
    ///   · 于是它既不改变行为、又误导用户（看到"关"，以为系统代理没开）。
    /// 现在：连接成功后总是自动写入系统代理，不再需要这个开关。
    /// TUN 开关保留（它真会改变网络接管方式，需要用户决策）。

    ///   此前拨开若未提权，会强行走 RelaunchElevated() 重启全程序 —— 用户以为
    ///   "只是打开一个开关"，实际整个程序被重启，观感就是「打开就关不掉了」。
    ///   现在改为纯设置项：拨动只记录意图（AppState.UseTun）并落盘，不再重启程序。
    ///   已连接时拨动必须重载内核才生效。这里保留 ReloadForSettingsAsync ——
    ///   否则用户拨到 off 之后 TUN 仍在接管路由，那才是真的「关不掉」。
    ///   重载失败/未连接时只保存设置，不报错（下次连接自然生效）。
    private async void OnTunToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        try
        {
            bool want = TunSwitch.IsOn;
            AppState.State.UseTun = want;
            AppState.Save();

            if (want && !ConnectionService.IsElevated)
            {
                var dlg = new ContentDialog
                {
                    Title = "TUN 模式需要管理员权限",
                    Content = "TUN 模式会创建全局虚拟网卡并接管路由，必须由管理员身份运行才生效。\n\n"
                            + "当前不是管理员：设置已保存，但连接时 TUN 不会生效。\n"
                            + "如需使用，请右键 neo 快捷方式 →「以管理员身份运行」，或在下面选择立即提权重启。",
                    PrimaryButtonText = "以管理员重启",
                    CloseButtonText = "稍后（仅保存设置）",
                    XamlRoot = XamlRoot,
                };
                if (await Neo.App.Ui.DialogGuard.ShowAsync(dlg) == ContentDialogResult.Primary)
                {
                    // 保留这条提权路径，但由用户显式选择，不再由拨开关隐式触发
                    ConnectionService.RelaunchElevated();
                    return;
                }
            }

            // 已连接时立即重载内核，让开关真正生效（关闭 TUN 会连带拆除网卡与路由，
            // 见 ConnectionService.TeardownTunNetwork）。未连接时无需动作。
            if (ConnectionService.IsConnected)
                _ = ConnectionService.ReloadForSettingsAsync();
        }
        catch (Exception ex)
        {
            LogBus.Exception("切换 TUN 设置失败", ex);
        }
    }

    /// 抓不到任何中间帧 —— 前一顿是原界面、下一帧就是完整菜单，它自带的出现过渡
    /// 短到不可感知，且应用侧除了覆写 RequestedTheme 没有别的干预点。
    /// 换成自绘 Popup（Composition 驱动）后，卡片有 220 ms 的位移+缩放+淡入，
    /// 列表项再按 18 ms/项错峰（封顶 7 项，见 PopupList.PlayStagger）。
    private void OnNodePickerClick(object sender, RoutedEventArgs e)
    {
        if (_nodePickerList == null)
        {
            _nodePickerList = new Neo.App.Ui.PopupList();
            // 关闭时释放在场节点行（下次点开会重建）：68 个节点约 270 个 XAML 元素，
            // 不释放会让复用的弹层在关闭后长期占着整棵列表子树。
            _nodePickerList.Dismissed += (_, _) => _nodePickerList?.ClearItems();
        }

        // 每次重新填充：节点列表/延迟随时在变（测速会改 RealDelay），
        // 复用一个已建好的 PopupList 只为省掉重复创建控件的开销。
        _nodePickerList.ClearItems();

        var current = AppState.CurrentNode;

        // 排序/显示与节点卡片同口径（NodeItem.DisplayDelay）：只用 RealDelay 会让「刚测完 TCP 延迟」
        // 的节点在这里仍显示空白（2026-10-23 用户反馈）。主口径无结果时回退另一口径。
        var preferReal = AppState.State.RealDelayTestEnabled;
        var ordered = AppState.Nodes
            .OrderBy(n => n.DisplayDelay(preferReal) > 0 ? 0 : 1)
            .ThenBy(n => { var d = n.DisplayDelay(preferReal); return d > 0 ? d : int.MaxValue; })
            .ToList();

        foreach (var n in ordered)
        {
            var latency = n.DisplayDelay(preferReal);
            var lat = latency > 0 ? $"{latency} ms"
                : latency == -1 ? "超时"
                : "";
            var text = lat.Length > 0 ? $"{n.Name} · {lat}" : n.Name;
            bool isCurrent = current != null
                && string.Equals(AppState.NodeKey(n), AppState.NodeKey(current), StringComparison.Ordinal);

            var node = n;
            _nodePickerList.AddItem(
                text,
                // 当前节点打勾（原 MenuFlyoutItem.Icon = ）
                isCurrent ? "\uE73E" : null,
                async () =>
                {
                    // 连点/双击保护：内核正在连接（含上次切换的无感重连）时忽略后续点击，
                    // 避免连点触发多次重连、互相踩内核进程。
                    if (ConnectionService.IsConnecting) return;

                    bool wasConnected = ConnectionService.IsConnected;
                    AppState.SetCurrentNode(node);
                    RefreshConnUi();

                    // 连接中切换 = 无感重连：不再弹确认框，直接换节点重连。
                    // 无感不等于无声：INFO 日志留一句可读记录，便于用户/排障时确认确实切换过。
                    if (wasConnected)
                    {
                        // 2026-10-23 无感切换：先在新槽位起好新内核，再把用户端口的中继指过去，
                        // 老连接继续走旧内核。用户不需要断开、系统代理不变。
                        LogBus.App($"切换节点：{node.Name}（无感切换）");
                        var (ok, err) = await ConnectionService.SwitchNodeFastAsync(node);
                        if (!ok)
                        {
                            LogBus.App($"切换节点失败：{node.Name} — {err ?? "未知原因"}", "Warning");
                            ConnectionFailed?.Invoke(this, err ?? "重连失败");
                        }
                        RefreshConnUi();
                    }
                },
                true);
        }

        if (ordered.Count == 0)
        {
            _nodePickerList.AddDisabled("暂无节点 — 先去「订阅」页添加订阅");
        }
        else
        {
            _nodePickerList.AddSeparator();
            _nodePickerList.AddItem("打开节点页挑选…", null,
                () => OpenNodesRequested?.Invoke(this, EventArgs.Empty), true);
        }

        // 宽度与节点选择行同宽（MenuFlyout 的观感即如此），过高时由 PopupList 内部滚动。
        Neo.App.Ui.PopupList.ShowBelow(_nodePickerList, NodePickerRow,
            NodePickerRow.ActualWidth > 0 ? NodePickerRow.ActualWidth : 420);
    }

    /// <summary>复用的节点下拉（每次点开重新填充，见 OnNodePickerClick）。</summary>
    private Neo.App.Ui.PopupList? _nodePickerList;

    private const double RingCircumference = 653.45;

    /// <summary>环笔画厚度。WinUI 的 StrokeDashArray 单位是「笔画厚度的倍数」，用它做归一化。</summary>
    private const double RingStrokeThickness = 9;

    /// 归一化后的整圆 dash 长度 = 周长 / 笔画厚度 ≈ 72.606。
    /// 连接完成后 dasharray 只放这一段、offset 归零 ⇒ 画满整圈（用户要求「补上缺口」）。
    private static readonly double RingDashFull = RingCircumference / RingStrokeThickness;

    /// 给环设置「虚线长度 = 整圆」并把偏移设为 offsetUnits，从而控制**画出多少弧**。
    /// offsetUnits = RingDashFull 时整圈被推走 ⇒ 什么都看不到；
    /// offsetUnits = 0 时整圈画出（闭环）。
    private void ApplyRingDash(double offsetUnits)
    {
        try
        {
            if (RingArc.StrokeDashArray.Count != 1 || Math.Abs(RingArc.StrokeDashArray[0] - RingDashFull) > 0.01)
            {
                RingArc.StrokeDashArray.Clear();
                RingArc.StrokeDashArray.Add(RingDashFull);
            }
            RingArc.StrokeDashOffset = offsetUnits;
        }
        catch { }
    }

    /// 连接完成时的「衍射波纹」：两圈细环从环带向外扩散并淡出，第二圈延后 0.18s 形成波纹感。
    /// 用 ScaleTransform + Opacity 两条动画组合，避免依赖 Shape 的尺寸动画（布局代价高）。
    private void PlayRingWaves()
    {
        try
        {
            PlayOneWave(RingWave1, RingWave1Scale, TimeSpan.Zero);
            PlayOneWave(RingWave2, RingWave2Scale, TimeSpan.FromMilliseconds(180));
        }
        catch { }
    }

    private static void PlayOneWave(Microsoft.UI.Xaml.Shapes.Ellipse wave,
                                     Microsoft.UI.Xaml.Media.ScaleTransform scale,
                                     TimeSpan delay)
    {
        if (wave == null || scale == null) return;
        wave.Opacity = 0;
        scale.ScaleX = scale.ScaleY = 1.0;
        wave.Visibility = Visibility.Visible;

        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();

        var op = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(900),
            BeginTime = delay,
        };
        op.KeyFrames.Add(new Microsoft.UI.Xaml.Media.Animation.DiscreteDoubleKeyFrame
        { KeyTime = TimeSpan.Zero, Value = 0.55 });
        op.KeyFrames.Add(new Microsoft.UI.Xaml.Media.Animation.LinearDoubleKeyFrame
        { KeyTime = TimeSpan.FromMilliseconds(900), Value = 0.0 });
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(op, wave);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(op, "Opacity");
        sb.Children.Add(op);

        var sc = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = 1.0,
            To = 1.5,
            Duration = TimeSpan.FromMilliseconds(900),
            BeginTime = delay,
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
            { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut },
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(sc, scale);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(sc, "ScaleX");
        sb.Children.Add(sc);

        var sc2 = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = 1.0,
            To = 1.5,
            Duration = TimeSpan.FromMilliseconds(900),
            BeginTime = delay,
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
            { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut },
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(sc2, scale);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(sc2, "ScaleY");
        sb.Children.Add(sc2);

        sb.Begin();
    }

    /// 环内逐渐被浅绿填充：0.7s 内 opacity 0 → 目标值，同时轻微放大（1.0 → 1.06）产生「涨起来」感。
    private void PlayRingFill(bool show)
    {
        try
        {
            if (RingFill == null) return;
            var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();

            var op = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = RingFill.Opacity,
                To = show ? 0.85 : 0.0,
                Duration = TimeSpan.FromMilliseconds(show ? 700 : 260),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
                { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut },
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(op, RingFill);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(op, "Opacity");
            sb.Children.Add(op);

            if (show)
            {
                RingFillScale.ScaleX = RingFillScale.ScaleY = 1.0;
                var sc = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                {
                    From = 1.0, To = 1.06,
                    Duration = TimeSpan.FromMilliseconds(700),
                    EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
                    { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut },
                };
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(sc, RingFillScale);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(sc, "ScaleX");
                sb.Children.Add(sc);

                var sc2 = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                {
                    From = 1.0, To = 1.06,
                    Duration = TimeSpan.FromMilliseconds(700),
                    EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
                    { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut },
                };
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(sc2, RingFillScale);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(sc2, "ScaleY");
                sb.Children.Add(sc2);
            }

            sb.Begin();
        }
        catch { }
    }

    /// 「充能收口」动画：连接中那条正在生长的弧**补满整圈**，耗时 0.6s。
    /// 直接推到整圈（From 不显式指定 = 取当前值，保证不跳帧），不再需要旋转回正。
    /// 补满后让外发光从 0.85 落回 0.35（0.5s），就是用户要的「整圈闪一下再落成常亮」。
    private void PlayRingGrow()
    {
        try
        {
            RingArc.Visibility = Visibility.Visible;
            RingArc.Opacity = 1;

            var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();

            var an = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                // From 不显式指定 ⇒ 从充能到此刻的当前长度起步，保证连续
                To = 0.0,
                Duration = TimeSpan.FromMilliseconds(600),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
                { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut },
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(an, RingArc);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(an, "StrokeDashOffset");
            sb.Children.Add(an);

            sb.Completed += (_, _) =>
            {
                try
                {
                    if (!ConnectionService.IsConnected) return;

                    // 「闪一下」：外发光先冲亮，再落回已连接的常态柔光
                    RingGlow.Visibility = Visibility.Visible;
                    RingGlow.Opacity = 0.85;

                    var flash = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
                    var g = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                    {
                        From = 0.85,
                        To = 0.35,
                        Duration = TimeSpan.FromMilliseconds(520),
                        EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
                        { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut },
                    };
                    Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(g, RingGlow);
                    Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(g, "Opacity");
                    flash.Children.Add(g);
                    flash.Begin();
                }
                catch { }
            };

            sb.Begin();
        }
        catch { }
    }

    /// 把环收回（offset 回到整圈 = 看不见），260ms。
    ///   环已经画满整圈、但环心文字是「未连接」—— 视觉与实际状态相反。
    ///   原因是收回动画依赖 `RingArc.StrokeDashOffset` 的**当前值**作起始，
    ///   而生长动画播完后该属性停在 0；若此时状态直接跳到「未连接」，
    ///   收回动画的起点与终点都是 0（`From` 取到的正是 0），于是什么都不动、环留在满圈。
    ///   修法：断开时**不依赖当前值**，硬编码从 0 收到整圈；并保证最终把可见性也关掉，
    ///   让「看不见」由 Visibility 兜底，而不是只靠动画值。
    private void PlayRingRetract()
    {
        try
        {
            RingArc.Visibility = Visibility.Visible;
            var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            var an = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = 0.0,                 // 明确从「满圈」起，不取当前值
                To = RingDashFull,          // 收到「整圈被推走」= 看不见
                Duration = TimeSpan.FromMilliseconds(260),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
                { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseIn },
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(an, RingArc);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(an, "StrokeDashOffset");
            sb.Children.Add(an);
            sb.Completed += (_, _) =>
            {
                // 动画结束再隐藏：双保险，确保「未连接」时环一定不可见
                try
                {
                    if (!ConnectionService.IsConnected && !ConnectionService.IsConnecting)
                    {
                        RingArc.Visibility = Visibility.Collapsed;
                        ApplyRingDash(RingDashFull);
                    }
                }
                catch { }
            };
            sb.Begin();
        }
        catch { }
    }

    private void UpdateStatusRing()
    {
        //   可见时由 ApplyPageVisibility 重新进入本方法，按当前连接状态恢复。
        if (!IsPageVisible()) { StopRingStoryboards(); return; }

        try
        {
            bool connecting = ConnectionService.IsConnecting && !ConnectionService.IsConnected;
            bool on = ConnectionService.IsConnected;

            // 阶段没变就不重启动画（否则连接中每秒多次刷新会把「充能」反复归零）
            int desired = connecting ? 1 : (on ? 2 : 0);
            if (desired == _ringVisualState) return;
            _ringVisualState = desired;

            EnsureRingStoryboards();

            if (connecting)
            {
                //   「连接只做呼吸灯效果，其他的不需要」「连接中有一个点绕着转，不是很好」
                //   → 去掉充能弧与绕行亮点，只让外发光 + 环心柔光一起缓慢呼吸；
                //     环本身不画弧、不旋转，状态由中心文字「连接中…」表达。
                _ringPulseSb?.Stop();
                RingPulse.Visibility = Visibility.Visible;
                RingPulse.Opacity = 0;

                RingArc.Visibility = Visibility.Collapsed;
                RingGlow.Visibility = Visibility.Visible;
                RingGlow.Opacity = 0;

                _ringBreathSb?.Begin();
                PlayRingFill(false);
            }
            else if (on)
            {
                _ringBreathSb?.Stop();

                RingArcRotate.Angle = 0;
                RingPulse.Visibility = Visibility.Visible;
                _ringPulseSb?.Begin();

                // 已连接保留外发光（呼吸的柔光），但比连接中更淡
                RingGlow.Visibility = Visibility.Visible;
                RingGlow.Opacity = 0.35;

                // 只在「刚连上」时播放生长 + 波纹 + 填充，避免每次刷新都重播
                if (!_ringConnectedAnimDone)
                {
                    _ringConnectedAnimDone = true;
                    PlayRingGrow();
                    PlayRingWaves();
                }
                else
                {
                    RingArc.Visibility = Visibility.Visible;
                    RingArc.Opacity = 1;
                    ApplyRingDash(0);   // 已在连接态：保持闭环
                }
                PlayRingFill(true);
            }
            else
            {
                _ringBreathSb?.Stop();
                _ringPulseSb?.Stop();
                _ringConnectedAnimDone = false;

                RingGlow.Visibility = Visibility.Collapsed;
                RingGlow.Opacity = 0;
                RingPulse.Visibility = Visibility.Visible;
                RingPulse.Opacity = 0;
                PlayRingFill(false);
                if (RingArc.Visibility == Visibility.Visible) PlayRingRetract();
                else RingArc.Visibility = Visibility.Collapsed;
            }
        }
        catch { }
    }

    /// 建立两套无限 Storyboard（只建一次）：
    ///     外发光 0.10↔0.55、环心柔光 0.04↔0.20，1.6s 一个来回；不画弧、不旋转、无亮点。
    /// 两者都只动 Opacity：不触碰布局，也不产生新的绘制层。
    private void EnsureRingStoryboards()
    {
        if (_ringBreathSb != null) return;

        // 已连接：中心柔光呼吸
        if (_ringPulseSb == null)
        {
            _ringPulseSb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            var pulse = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = 0.35,
                To = 0.95,
                Duration = TimeSpan.FromMilliseconds(900),
                AutoReverse = true,
                RepeatBehavior = Microsoft.UI.Xaml.Media.Animation.RepeatBehavior.Forever,
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(pulse, RingPulse);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(pulse, "Opacity");
            _ringPulseSb.Children.Add(pulse);
        }

        // 连接中：呼吸灯（外发光 + 环心柔光，同一节奏）
        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();

        var glow = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = 0.10,
            To = 0.55,
            Duration = TimeSpan.FromMilliseconds(1600),
            AutoReverse = true,
            RepeatBehavior = Microsoft.UI.Xaml.Media.Animation.RepeatBehavior.Forever,
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.SineEase
            { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseInOut },
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(glow, RingGlow);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(glow, "Opacity");
        sb.Children.Add(glow);

        var core = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = 0.04,
            To = 0.20,
            Duration = TimeSpan.FromMilliseconds(1600),
            AutoReverse = true,
            RepeatBehavior = Microsoft.UI.Xaml.Media.Animation.RepeatBehavior.Forever,
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.SineEase
            { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseInOut },
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(core, RingPulse);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(core, "Opacity");
        sb.Children.Add(core);

        _ringBreathSb = sb;
    }

    private void RefreshConnUi()
    {
        if (_mode != AppState.State.ProxyMode)
            SetMode(AppState.State.ProxyMode);

        // 本地代理地址跟随后端常量（2026-10-23 端口迁移到 21xxx，避免与其它客户端撞端口）。
        try
        {
            LocalProxyAddrText.Text = $"127.0.0.1:{Neo.Core.LocalPorts.Socks}";
        }
        catch { }

        bool on = ConnectionService.IsConnected;
        var selected = AppState.CurrentNode;
        var connected = ConnectionService.ConnectedNode;
        // 固定出口生效时，实际出口是「出口节点」，用户选中的节点只是入口之一 ——
        // 用户反馈「切到 0.1 倍节点但圆环还显示旧节点」，根因就在这里：显示的是 ConnectedNode。
        var fixedExit = on && ConnectionService.FixedExitActive
            ? AppState.Nodes.FirstOrDefault(n => n.Name == ConnectionService.FixedExitName)
            : null;
        var node = fixedExit ?? connected ?? selected ?? AppState.Nodes.FirstOrDefault();
        // 统一「显示口径」：与节点卡片一致（真连开关决定主口径，另一口径有值时回退）。
        var displayDelay = node?.DisplayDelay(AppState.State.RealDelayTestEnabled) ?? 0;

        bool hasSub = !string.IsNullOrWhiteSpace(AppState.State.SubscriptionUrl);
        SubBadge.Text = hasSub ? $"{AppState.Nodes.Count} 节点" : "未配置订阅";
        SubNameText.Text = hasSub
            ? (AppState.State.SubscriptionName ?? "我的订阅")
            : "未配置订阅";
        SubUrlText.Text = hasSub
            ? TrimUrl(AppState.State.SubscriptionUrl)
            : "—";
        var (flow, total, expire) = AppState.TrafficParts();
        var (subUsedB, subTotalB, subRemainingB) = AppState.SubUsage();
        if (subTotalB > 0)
        {
            SubFlow.Text = UsageTracker.Format(subUsedB);
            SubTotal.Text = $"/ {UsageTracker.Format(subTotalB)} · 剩 {UsageTracker.Format(subRemainingB)}";
        }
        else
        {
            SubFlow.Text = flow;
            SubTotal.Text = total == "—" ? "" : $"/ {total}";
        }
        SubExpire.Text = expire;

        double subBarPct = hasSub ? (AppState.TrafficUsedRatio() ?? 0) * 100 : 0;
        ConnSubBarCols.ColumnDefinitions[0].Width = new GridLength(subBarPct, GridUnitType.Star);
        ConnSubBarCols.ColumnDefinitions[1].Width = new GridLength(100 - subBarPct, GridUnitType.Star);

        _syncing = true;
        // 「系统代理」开关已删除（见上方说明）；只同步 TUN 开关的显示状态。
        TunSwitch.IsOn = AppState.State.UseTun;
        _syncing = false;

        RingStateText.Text = ConnectionService.IsConnecting && !on ? "连接中…" : (on ? "已连接" : "未连接");
        RingStateText.Foreground = ConnectionService.IsConnecting && !on
            ? GetThemeBrush("RingBlue", Color.FromArgb(255, 0x7C, 0xC4, 0xFF))
            : on
                ? GetThemeBrush("NeoGreen", Color.FromArgb(255, 0x16, 0xA3, 0x4A))
                : GetThemeBrush("NeoText3", Color.FromArgb(255, 0x8E, 0x8E, 0x96));

        if (node != null)
        {
            //   节点名仍由下方节点行 SelNodeName 显示（见本方法后段），信息不丢。
            HeroLat.Text = displayDelay > 0 ? $"{displayDelay} ms"
                : displayDelay == -1 ? "超时"
                : "未测速";
            HeroProto.Text = AppState.ProtocolName(node.Protocol);
            HeroTransport.Text = AppState.TransportName(node);
            HeroEndpoint.Text = node.EndPoint;
        }
        else
        {
            HeroLat.Text = "—";
            HeroProto.Text = "—";
            HeroTransport.Text = "—";
            HeroEndpoint.Text = "—";
        }

        // 固定出口标记：仅在真正生效时显示入口数。
        if (fixedExit != null)
        {
            HeroMultiText.Text = $"固定出口 · 入口 {ConnectionService.FixedExitEntries} 条";
            HeroMultiBadge.Visibility = Visibility.Visible;
        }
        else
        {
            HeroMultiBadge.Visibility = Visibility.Collapsed;
        }

        // 因此原先画在 HeroFlag 上的那次绘制随 HeroFlag 一并移除。
        var flagKey = node == null ? "other" : AppState.GuessFlag(node.Name);
        Ui.FlagPainter.Draw(SelFlag, flagKey);

        SelNodeName.Text = fixedExit != null ? "出口：" + fixedExit.Name : (node?.Name ?? "未选择节点");
        SelHint.Text = fixedExit != null
            ? "固定出口 · 入口 " + ConnectionService.FixedExitEntries + " 条（切换入口不影响出口）"
            : on && selected != null && connected != null
                && !ReferenceEquals(selected, connected) && selected.Name != connected.Name
                ? "已选择「" + selected.Name + "」，点击圆环重连后生效"
                : node == null
                    ? "去节点页选择一个"
                    : displayDelay > 0
                        ? $"延迟 {(displayDelay <= 800 ? "低" : displayDelay <= 2000 ? "中" : "高")}"
                        : displayDelay == -1
                            ? "延迟超时"
                            : "";
            UpdateStatusRing();
        UpdateCoreCardUi();
    }

    private static string TrimUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "—";
        try
        {
            var u = new Uri(url.Trim());
            return u.Host;
        }
        catch { return url.Trim(); }
    }

    /// uiverse 参考 `glass-radio-group` 的动效语言）。
    /// 与旧实现的区别：旧的是三个 Border 各自硬切背景色（无中间态、无滑动）；
    /// 现在背景只有 SegGlider 一块，靠 TranslateTransform 在 0 / 1/3 / 2/3 位置间滑动。
    /// glider 宽度 = (根宽 - 2×间距) / 3，与三段的实际宽度一致（ColumnSpacing=3，两处间隙共 6）。
    /// 缓动：参考的 cubic-bezier(0.37, 1.95, 0.66, 0.56) 有「过冲后回弹」特征，
    ///       WinUI 里对应的最接近项是 BackEase（EaseOut），Amplitude 控制过冲幅度。
    ///       x1=0.37/y1=1.95 的过冲较强，取 Amplitude≈0.45 逼近（0.5 会晃得过头）。
    private void SlideGlider(int index, bool animate)
    {
        try
        {
            if (ModeSegRoot == null || SegGlider == null) return;

            double total = ModeSegRoot.ActualWidth;
            if (total <= 1)
            {
                //   原写法是「宽度还没测出来就 DispatcherQueue.TryEnqueue(() => SlideGlider(...))」，
                //   但控件未完成布局时 ActualWidth 会**一直是 0**，于是每投递一次又投递一次 ——
                //   （日志最后一行是「等待 OnRootLoaded 显示窗口」，进程不响应抓图）。
                //   修法：不做自投递重试，改为**只登记一次待办**，由 SizeChanged 在真正拿到宽度时补做。
                _gliderPendingIndex = index;
                return;
            }

            const double gapTotal = 6.0;              // ColumnSpacing=3，两处
            double w = Math.Max(1, (total - gapTotal) / 3.0);
            const double gap = 3.0;
            double targetX = index * (w + gap);

            SegGlider.Width = w;
            _gliderPendingIndex = -1;

            if (!animate)
            {
                SegGliderShift.X = targetX;
                return;
            }

            var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            var an = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = SegGliderShift.X,
                To = targetX,
                Duration = TimeSpan.FromMilliseconds(420),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.BackEase
                {
                    EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut,
                    Amplitude = 0.45,
                },
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(an, SegGliderShift);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(an, "X");
            sb.Children.Add(an);
            sb.Begin();
        }
        catch { }
    }

    /// 宽度未就绪时的待办索引（-1 = 无待办）。
    /// 由 SizeChanged 在真正拿到宽度后补做一次定位，替代原来的自投递重试（那会死循环）。
    private int _gliderPendingIndex = -1;

    private void OnModeSegRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        try
        {
            // 尺寸变化时：若有待办就补做；否则重算宽度（窗口缩放后 glider 要跟着变宽/移位）
            int idx = _gliderPendingIndex >= 0
                ? _gliderPendingIndex
                : (_mode switch { "global" => 1, "direct" => 2, _ => 0 });
            if (e.NewSize.Width > 1) SlideGlider(idx, false);
        }
        catch { }
    }

    private void OnModeTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not Grid { Tag: string mode }) return;
        SetMode(mode);
        if (AppState.State.ProxyMode != mode)
        {
            AppState.State.ProxyMode = mode;
            AppState.Save();
            _ = ConnectionService.ReloadForSettingsAsync();
        }
    }

    private void SetMode(string mode)
    {
        _mode = mode;
        var text2 = GetThemeBrush("NeoText2", ActualTheme == ElementTheme.Dark
            ? Color.FromArgb(255, 0x9A, 0xA1, 0xAC)
            : Color.FromArgb(255, 0x6B, 0x72, 0x80));
        var onText = ActualTheme == ElementTheme.Dark ? OnTextDark : OnTextLight;

        // 文字颜色/字重仍按选中态切换（背景统一交给 SegGlider 滑动块）
        ApplyText(SegRuleText, mode == "rule", text2, onText);
        ApplyText(SegGlobalText, mode == "global", text2, onText);
        ApplyText(SegDirectText, mode == "direct", text2, onText);

        int idx = mode switch { "global" => 1, "direct" => 2, _ => 0 };
        // 首次（布局未就绪）不带动画直接定位，之后滑动带过冲回弹
        bool animate = _modeSegmentReady;
        _modeSegmentReady = true;
        SlideGlider(idx, animate);

        UpdateModeNote(mode);
    }

    /// <summary>首帧定位不做动画的闸门（避免开页时滑块从左侧滑一下）。</summary>
    private bool _modeSegmentReady;

    private void UpdateModeNote(string mode)
    {
        ModeNote.Text = mode switch
        {
            "global" => "全局模式：所有流量都经节点转发。",
            "direct" => "直连模式：不使用节点，全部直连。",
            _ => "规则模式：国内域名与 IP 直连，其余流量走节点；命中「广告拦截」规则时直接丢弃。",
        };
    }

    private static void ApplyText(TextBlock text, bool on, Brush text2, Brush onText)
    {
        text.Foreground = on ? onText : text2;
        text.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private static Brush GetThemeBrush(string key, Color fallback)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush)
            return brush;
        return new SolidColorBrush(fallback);
    }
}
