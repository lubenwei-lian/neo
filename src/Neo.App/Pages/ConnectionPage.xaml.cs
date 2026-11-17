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
using Neo.Core.Presentation;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using System.IO;
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
    //   清掉下一次刚显示的提示（连续两次切换时）。
    private int _switchHintGeneration;

    private async Task HideSwitchHintAsync(int delayMs, int generation)
    {
        try
        {
            if (delayMs > 0) await Task.Delay(delayMs);
            if (App.IsShuttingDown) return;
            if (generation != _switchHintGeneration) return;
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
    public event EventHandler? OpenLogRequested;
    public FrameworkElement DetailsAnchor => NetworkDetailsButton;
    public event Action<bool>? OpenNetworkDiagnosticsRequested;

    public event EventHandler<string>? ConnectionFailed;

    private Microsoft.UI.Xaml.Media.Animation.Storyboard? _ringPulseSb;
    private Microsoft.UI.Xaml.Media.Animation.Storyboard? _ringBreathSb;
    /// 「刚连上」只播一次生长/波纹/填充动画的闸门。
    /// 为什么需要：UpdateStatusRing 每次状态刷新都会跑，若每次都重播，
    /// 界面会反复出现波纹与生长，观感变成闪烁。断开时复位。

    /// 圆环当前已经呈现的视觉阶段：-1 未知 / 0 未连接 / 1 连接中 / 2 已连接。
    /// 为什么需要它：UpdateStatusRing 由 RefreshConnUi 驱动，连接中每秒会跑很多次；
    /// 若每次都 ApplyRingDash + Storyboard.Begin()，「充能」动画会被反复重置到 0
    /// 页面隐藏时 StopRingStoryboards 会把它复位成 -1，恢复可见时重新布景。

    public ConnectionPage()
    {
        InitializeComponent();
        SetMode("rule");
        ResetChart();
        ActualThemeChanged += (_, _) => SetMode(_mode);
        ConnectionService.StateChanged += RefreshConnUi;
        NetworkHealthService.Changed += OnNetworkHealthChanged;
        NetworkDiagnosticsService.Changed += OnNetworkHealthChanged;
        CurrentExitService.Changed += OnCurrentExitChanged;
        FloatingTrafficService.Changed += OnFloatBallSettingChanged;
        BuildHomeSiteRows();
        _lastSuccessEpoch = ConnectionService.SessionEpoch;
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

        // 且不能压住圆环内的状态文字/节点行）。
        ConnectionService.SwitchProgress += msg => DispatcherQueue.TryEnqueue(() =>
        {
            if (App.IsShuttingDown) return;
            try
            {
                if (!string.IsNullOrEmpty(msg))
                {
                    _switchHintGeneration++;
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
                    _ = HideSwitchHintAsync(delay, _switchHintGeneration);
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
        foreach (var fade in _modeFades) fade?.Stop();
        NetworkHealthService.Changed -= OnNetworkHealthChanged;
        NetworkDiagnosticsService.Changed -= OnNetworkHealthChanged;
        CurrentExitService.Changed -= OnCurrentExitChanged;
        FloatingTrafficService.Changed -= OnFloatBallSettingChanged;
        StopConnectionSplash();
        try { _statsTimer.Stop(); } catch { }
        try { _resizeDebounce?.Stop(); } catch { }
        StopRingStoryboards();
        StopConnectionSplash();
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
            PeakText.Text = "—"; PeakUnit.Text = "";
            RingTimeText.Text = "00:00:00";
            return;
        }

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

        if (!IsPageVisible()) return;

        StatsCoreMemText.Text = mem is { } m ? m.ToString() : "—";

        if (hasPrev)
        {
            var (uv, uu) = SplitBytes(upBps);
            UpSpeedText.Text = uv; UpSpeedUnit.Text = uu + "/s";
            var (dv, du) = SplitBytes(downBps);
            DownSpeedText.Text = dv; DownSpeedUnit.Text = du + "/s";

            var (pv, pu) = SplitBytes(_peakDownBps);
            PeakText.Text = pv; PeakUnit.Text = pu + "/s";
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

            CoreStateText.Text = "内核";
            CoreStateText.Foreground = GetThemeBrush("NeoText1", Colors.White);
            CoreDot.Fill = running ? green : text2;

            //   统计与版本要走 sing-box，标签单独显示 naive。
            bool naiveKind = ConnectionService.CoreKind == "naive";
            bool sing = ConnectionService.CoreKind == "sing-box" || naiveKind;
            bool mihomo = ConnectionService.CoreKind == "mihomo";
            CoreKindText.Text = mihomo ? "mihomo" : (naiveKind ? "naive + sing-box" : (sing ? "sing-box" : "xray"));
            CoreVersionText.Text = mihomo
                ? (KernelCatalog.CachedVersion(CoreEngine.Mihomo) ?? "—")
                : (sing ? (ConnectionService.CachedSingBoxVersion ?? "—") : (ConnectionService.CachedXrayVersion ?? "—"));

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
    private static uint Packed(Color color) => (uint)(color.A << 24 | color.R << 16 | color.G << 8 | color.B);
    private static readonly uint[] DnPixels = DnPalette.Select(Packed).ToArray();
    private static readonly uint[] UpPixels = UpPalette.Select(Packed).ToArray();
    private readonly TrafficChartRaster _chartRaster = new();
    private WriteableBitmap? _chartBitmap;
    private Stream? _chartStream;
    private DateTime? _sessionStart;
    // 渲染降本：方块尺寸/位置只在布局失效时设置一次，Fill 相同则跳过 ——
    // 每秒常态重绘从上万次依赖属性写入降到几百次。
    private bool _layoutDirty = true;
    private int _layoutCols = -1;
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

    /// Single DPI-aware image replaces thousands of XAML cells; resize allocates only when geometry changes.
    private void EnsureCells(int cols, double plotH)
    {
        var scale = Math.Clamp(XamlRoot?.RasterizationScale ?? 1, 0.5, 4);
        if (_chartRaster.Configure(cols, plotH, scale))
        {
            _chartStream?.Dispose();
            _chartBitmap = new WriteableBitmap(_chartRaster.Width, _chartRaster.Height);
            _chartStream = _chartBitmap.PixelBuffer.AsStream();
            TrafficImage.Source = _chartBitmap;
            TrafficImage.Width = _chartRaster.Width / scale;
            TrafficImage.Height = _chartRaster.Height / scale;
            _layoutDirty = true;
        }
        if (_layoutDirty || _layoutCols != cols)
        {
            _layoutCols = cols; _layoutDirty = false;
        }
    }

    public void ReleaseChartSurface()
    {
        _chartStream?.Dispose(); _chartStream = null;
        TrafficImage.Source = null; _chartBitmap = null; _chartRaster.Release();
        _layoutDirty = true;
    }

    private const double RingBaseSize = 236;

    // Typography uses effective window pixels; card rows reserve room for its
    // native reflow without scaling the full page or rasterizing the interface.
    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    { StopConnectionSplash(); UpdateHomeViewport(e.NewSize.Width, e.NewSize.Height); }

    private void OnChartFitSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 1 || e.NewSize.Height <= 1) return;
        ChartCanvas.Width = e.NewSize.Width; ChartCanvas.Height = e.NewSize.Height;
        RedrawChart();
    }

    private void UpdateHomeViewport(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 1 || height <= 1) return;
        var compact = height < 680;
        var scale = Ui.AdaptiveText.Scale;
        ConfigureCoreRows(true);
        ConfigureHomeSiteRows(compact || width < 1200 ? 3 : 2);
        HomeSiteGrid.RowSpacing = compact || width < 1200 ? 2 : 3;
        HomeGrid.RowSpacing = HomeGrid.ColumnSpacing = compact ? 10 : 12;
        RightRailCol.Width = new GridLength(1, GridUnitType.Star);
        // Match the prototype's rows; scaling their whitespace with the font
        // made the node surface oversized and squeezed the traffic chart.
        HomeGrid.RowDefinitions[0].Height = new GridLength(compact ? 146 : Math.Max(264, Math.Ceiling(147 * scale + 72)));
        HomeGrid.RowDefinitions[2].Height = new GridLength(compact ? 72 : 88);
        HomeGrid.RowDefinitions[3].Height = new GridLength(compact ? 130 : height >= 840 ? 224 : 168);
        HeroCard.Padding = new Thickness(compact ? 12 : scale > 1.1 ? 24 : 28);
        ConnectionHeroLayout.RowSpacing = 0;
        ConnectionTimeHost.Margin = new Thickness(0);
        if (HomeSiteViewport.Parent is Grid healthLayout && healthLayout.Parent is Border healthCard)
        {
            healthCard.Padding = new Thickness(compact ? 14 : 18, compact ? 8 : 14, compact ? 14 : 18, compact ? 8 : 14);
            healthLayout.RowSpacing = compact ? 2 : 8;
        }
        foreach (var child in HomeSiteGrid.Children.OfType<Grid>())
        {
            child.MinHeight = compact ? 18 : 20;
            foreach (var text in child.Children.OfType<TextBlock>()) Ui.AdaptiveText.SetBaseSize(text, compact ? 10.5 : 12);
        }
        Ui.AdaptiveText.SetBaseSize(RingTimeText, compact ? 30 : 44);
        Ui.AdaptiveText.SetBaseSize(RingStateText, compact ? 15 : 18);
        Ui.AdaptiveText.SetBaseHeight(RingHit, compact ? 30 : 48);
        ConnectionHeroLayout.RowDefinitions[6].Height = new GridLength(Math.Ceiling(Ui.ResponsiveViewport.FontSize(compact ? 30 : 48, scale)));
        CurrentNodeCard.Padding = new Thickness(compact ? 14 : 18, compact ? 10 : 18, compact ? 14 : 18, HeroCard.Padding.Bottom);
        FloatBallRow.Height = ConnectionHeroLayout.RowDefinitions[6].Height.Value;
        SelNodeName.MaxLines = compact ? 1 : 2;
        CurrentNodeLayout.RowSpacing = 0;
        CurrentNodeHost.Margin = new Thickness(0, compact ? 5 : 8, 0, 0);
        NodeMetaHost.Margin = new Thickness(0, compact ? 2 : 8, 0, 0);
        Ui.AdaptiveText.SetBaseSize(SelNodeName, compact ? 13 : 14);
        Ui.AdaptiveText.SetBaseSize(SelHint, compact ? 10.5 : 11.5);
        Ui.AdaptiveText.SetBaseSize(KernelHint, compact ? 10 : 11);
        Ui.AdaptiveText.SetBaseSize(HeroLat, compact ? 10 : 11);
        Ui.AdaptiveText.SetBaseSize(HeroProto, compact ? 10 : 11);
        Ui.AdaptiveText.SetBaseSize(HeroTransport, compact ? 10 : 11);
        Ui.AdaptiveText.SetBaseSize(HeroEndpoint, compact ? 10 : 11);
        KernelHint.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ModeCard.Padding = NetworkCard.Padding = new Thickness(14, compact ? 6 : 10, 14, compact ? 6 : 10);
        TunModeDescription.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CoreCard.Padding = new Thickness(16, compact ? 8 : 12, 16, compact ? 8 : 12);
        CoreRows.RowSpacing = compact ? 4 : 10;
        if (CoreHeaderHost.Parent is Grid coreLayout) coreLayout.RowSpacing = compact ? 6 : 12;
        foreach (var item in CoreRows.Children)
        {
            if (item is TextBlock label) Ui.AdaptiveText.SetBaseSize(label, compact ? 11 : 13);
            else if (item is Panel panel) foreach (var child in panel.Children) if (child is TextBlock value) Ui.AdaptiveText.SetBaseSize(value, compact ? 11 : 12.5);
        }
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

    private void OnHomeGridSizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateHomeViewport(e.NewSize.Width, e.NewSize.Height);

    private void OnFloatBallSettingChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (!App.IsShuttingDown) FloatBallSwitch.IsOn = AppState.State.FloatingTrafficEnabled;
    });

    private void OnFloatBallToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing || FloatBallSwitch.IsOn == AppState.State.FloatingTrafficEnabled) return;
        FloatingTrafficService.SetEnabled(FloatBallSwitch.IsOn);
    }


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


    private void RedrawChart()
    {
        if (!IsPageVisible() || _resizeDebounce is { IsRunning: true }) return;
        var width = ChartViewport.ActualWidth; var height = ChartViewport.ActualHeight;
        if (width <= 1 || height <= 1) return;
        var start = Math.Max(0, _dnHist.Count - 24);
        double ceiling = 1024;
        for (var i = start; i < _dnHist.Count; i++) ceiling = Math.Max(ceiling, Math.Max(_dnHist[i], _upHist[i]));
        ceiling *= 1.12;
        DownloadCurve.Data = Ui.TrafficLineGeometry.Build(_dnHist, start, width, height, ceiling, false);
        DownloadArea.Data = Ui.TrafficLineGeometry.Build(_dnHist, start, width, height, ceiling, true);
        UploadCurve.Data = Ui.TrafficLineGeometry.Build(_upHist, start, width, height, ceiling, false);
        RefreshHomeLog();
    }

    private void OnChartPointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_times.Count == 0 || ChartViewport.ActualWidth <= 1) return;
        var p = e.GetCurrentPoint(ChartCanvas).Position;
        var start = Math.Max(0, _times.Count - 120);
        var index = Math.Clamp(_times.Count - 1 - (int)Math.Round((ChartViewport.ActualWidth - p.X) / (ChartViewport.ActualWidth / Math.Max(1, _times.Count - start - 1))), start, _times.Count - 1);
        TipTime.Text = _times[index].ToString("HH:mm:ss");
        TipDn.Text = "下载 " + UsageTracker.Format((long)_dnHist[index]) + "/s";
        TipUp.Text = "上传 " + UsageTracker.Format((long)_upHist[index]) + "/s";
        ChartTip.Visibility = Visibility.Visible;
        Canvas.SetLeft(ChartTip, Math.Max(0, Math.Min(p.X, ChartViewport.ActualWidth - 150)));
        Canvas.SetTop(ChartTip, Math.Max(0, p.Y - 55));
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
        StopConnectionSplash();
        }
    }

    private void StopRingStoryboards()
    {
        try { _ringPulseSb?.Stop(); } catch { }
        try { _ringBreathSb?.Stop(); } catch { }

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
    ///   已连接时只切换 TUN 接管层，保留节点内核与连接；未连接时保存下次连接的选择。
    private async void OnTunToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        try
        {
            bool want = TunSwitch.IsOn;
            if (want && !ConnectionService.IsElevated)
            {
                AppState.State.UseTun = want;
                AppState.Save();
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
                return;
            }
            var changed = await ConnectionService.SetTunEnabledAsync(want);
            if (!changed.Ok) ConnectionService.NotifyUser(changed.Error ?? "切换 TUN 失败");
            AppState.Save();
            RefreshConnUi();
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
        var on = ConnectionService.IsConnected; var connecting = ConnectionService.IsConnecting && !on;
        RingHit.Content = connecting ? "连接中…" : on ? "断开连接" : "连接";
        RingHit.Background = on ? GetThemeBrush("NeoAccentSoft", Colors.Transparent) : Transparent;
        RingHit.BorderBrush = GetThemeBrush(on ? "NeoAccentLine" : "NeoOutline", Colors.Gray);
        RingHit.Foreground = GetThemeBrush(on ? "NeoAccent" : "NeoText1", Colors.White);
        RingHit.FontWeight = FontWeights.SemiBold;
        RingHit.IsEnabled = !ConnectionService.IsConnecting;
        ConnectionDot.Fill = GetThemeBrush(on ? "NeoGreen" : connecting ? "RingBlue" : "NeoText3", Colors.Gray);
        if (on && _lastSuccessEpoch != ConnectionService.SessionEpoch)
        {
            _lastSuccessEpoch = ConnectionService.SessionEpoch;
            if (IsPageVisible()) PlayConnectionSplash();
        }
        if (!IsPageVisible()) StopConnectionSplash();
        RefreshHomeLog();
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

    private void OnNetworkHealthChanged()
    {
        if (App.IsShuttingDown) return;
        try
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!App.IsShuttingDown) RefreshNetworkHealthUi();
            });
        }
        catch { }
    }

    private void OnNetworkDetailsClicked(object sender, RoutedEventArgs e)
        => OpenNetworkDiagnosticsRequested?.Invoke(false);

    private void OnCurrentExitChanged()
    {
        if (App.IsShuttingDown) return;
        DispatcherQueue.TryEnqueue(() => { if (!App.IsShuttingDown) RefreshCurrentExitUi(); });
    }

    private void RefreshCurrentExitUi()
    {
        var exit = CurrentExitService.Current;
        var valid = ConnectionService.IsConnected && exit.Epoch == ConnectionService.SessionEpoch;
        ExitRegionText.Text = !ConnectionService.IsConnected ? "—" : !valid ? "待检测" : exit.IsRunning ? "检测中…"
            : exit.Result.Confirmed ? Ui.CurrentExitDisplay.Region(exit.Result.Country) + " · " + exit.Result.Ip : "未确认";
        ToolTipService.SetToolTip(ExitRegionText, valid
            ? exit.Result.Detail + (exit.Result.Confirmed ? "\n" + exit.Result.Source + "\n" + exit.Result.Route.Path : "") : null);
    }

    private async void OnCheckNetworkClicked(object sender, RoutedEventArgs e)
    {
        if (NetworkDiagnosticsService.Current.IsRunning) { NetworkDiagnosticsService.Cancel(); return; }
        try { await NetworkDiagnosticsService.RunAsync(); }
        catch (Exception ex) { ConnectionFailed?.Invoke(this, "连通性检测失败：" + ex.GetType().Name); }
    }

    private void RefreshNetworkHealthUi()
    {
        var snapshot = NetworkDiagnosticsService.Current;
        var valid = ConnectionService.IsConnected && snapshot.Epoch == ConnectionService.SessionEpoch;
        var summary = Ui.ConnectivityDisplay.Summarize(snapshot.Results.Select(r => r.State).ToArray(), valid, snapshot.IsRunning, snapshot.CompletedAt.HasValue);
        NetworkHealthText.Text = summary.Percentage;
        NetworkHealthText.Foreground = summary.Complete && summary.Passed == summary.Total ? GetThemeBrush("NeoGreen", Colors.Green) : GetThemeBrush("NeoText2", Colors.Gray);
        NetworkHealthDetailText.Text = !ConnectionService.IsConnected ? "连接后检测" : !valid ? "待检测" : snapshot.IsRunning ? $"检测中 {summary.Finished}/{summary.Total}" : summary.Complete ? $"通过 {summary.Passed}/{summary.Total}" : "检测未完成";
        NetworkCheckButton.IsEnabled = ConnectionService.IsConnected;
        NetworkCheckButton.Content = valid && snapshot.IsRunning ? "停止" : "检测网络";
        for (var i = 0; i < _homeSiteResults.Count; i++)
        {
            var result = valid ? snapshot.Results.FirstOrDefault(r => r.Target == NetworkDiagnosticTargets.All[i]) : null;
            var value = _homeSiteResults[i];
            value.Text = Ui.ConnectivityDisplay.Site(result?.State);
            value.Foreground = result?.State == "可达" ? GetThemeBrush("NeoGreen", Colors.Green) : result?.State == "失败" ? GetThemeBrush("NeoRed", Colors.Red) : GetThemeBrush("NeoText3", Colors.Gray);
        }
    }

    private void RefreshConnUi()
    {
        RefreshNetworkHealthUi();
        FloatBallSwitch.IsOn = AppState.State.FloatingTrafficEnabled;
        if (_mode != AppState.State.ProxyMode)
            SetMode(AppState.State.ProxyMode);

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

        RefreshCurrentExitUi();
        RingStateText.Text = ConnectionService.IsConnecting && !on ? "连接中…" : (on ? "已连接" : "未连接");
        RingStateText.Foreground = ConnectionService.IsConnecting && !on
            ? GetThemeBrush("RingBlue", Color.FromArgb(255, 0x7C, 0xC4, 0xFF))
            : on
                ? GetThemeBrush("NeoText1", Colors.White)
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

        KernelHint.Text = fixedExit != null ? "" : KernelSupport.Describe(node)
            + (string.IsNullOrWhiteSpace(node?.SourceSubscription) ? "" : " · " + node.SourceSubscription);
        SelNodeName.Text = fixedExit != null ? "出口：" + Ui.NodeDisplayName.WithoutFlag(fixedExit.Name) : Ui.NodeDisplayName.WithoutFlag(node?.Name ?? "未选择节点");
        SelHint.Text = fixedExit != null
            ? "固定出口 · 入口 " + ConnectionService.FixedExitEntries + " 条（切换入口不影响出口）"
            : on && selected != null && connected != null
                && !ReferenceEquals(selected, connected) && selected.Name != connected.Name
                ? "已选择「" + selected.Name + "」，点击连接按钮应用"
                : node == null
                    ? "去节点页选择一个"
                    : displayDelay > 0
                        ? $"{NodeGrouper.RegionDisplay(AppState.GuessFlag(node.Name))} · 延迟 {displayDelay} ms"
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

    // The source fades each selected segment's fill; it has no travelling or
    // overshooting slider. Retain the XAML glider name only for compatibility.
    private readonly Microsoft.UI.Xaml.Media.Animation.Storyboard?[] _modeFades = new Microsoft.UI.Xaml.Media.Animation.Storyboard?[3];

    private void OnModeSegRootSizeChanged(object sender, SizeChangedEventArgs e) { }

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
        var animate = _modeSegmentReady && _mode != mode && Ui.PrototypeMotion.Enabled;
        _mode = mode;
        var text2 = GetThemeBrush("NeoText2", ActualTheme == ElementTheme.Dark
            ? Color.FromArgb(255, 0x9A, 0xA1, 0xAC)
            : Color.FromArgb(255, 0x6B, 0x72, 0x80));
        var onText = GetThemeBrush("NeoText1", Colors.White);

        // 文字颜色/字重仍按选中态切换（背景统一交给 SegGlider 滑动块）
        ApplyText(SegRuleText, mode == "rule", text2, onText);
        ApplyText(SegGlobalText, mode == "global", text2, onText);
        ApplyText(SegDirectText, mode == "direct", text2, onText);

        int idx = mode switch { "global" => 1, "direct" => 2, _ => 0 };
        SegGlider.Visibility = Visibility.Collapsed;
        TextBlock[] labels = [SegRuleText, SegGlobalText, SegDirectText];
        for (var i = 0; i < labels.Length; i++)
        {
            // Parent is still null during InitializeComponent/constructor work.
            var segment = ModeSegRoot.Children.OfType<Grid>().First(child => Grid.GetColumn(child) == i);
            var brush = segment.Background as SolidColorBrush ?? new SolidColorBrush(Colors.Transparent);
            var current = brush.Color; _modeFades[i]?.Stop(); _modeFades[i] = null; brush.Color = current;
            segment.Background = brush; segment.CornerRadius = new CornerRadius(13);
            var target = i == idx ? (ActualTheme == ElementTheme.Dark ? Color.FromArgb(36,255,255,255) : Color.FromArgb(24,0,0,0)) : Colors.Transparent;
            if (!animate) { brush.Color = target; continue; }
            var board = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            var change = new Microsoft.UI.Xaml.Media.Animation.ColorAnimation { From = current, To = target, Duration = TimeSpan.FromMilliseconds(140) };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(change, brush);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(change, "Color");
            board.Children.Add(change); var slot = i; _modeFades[i] = board;
            board.Completed += (_, _) => { if (ReferenceEquals(_modeFades[slot], board)) { board.Stop(); brush.Color = target; _modeFades[slot] = null; } };
            board.Begin();
        }
        _modeSegmentReady = true;

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
        => ThemeUtil.Get(key, fallback);
}
