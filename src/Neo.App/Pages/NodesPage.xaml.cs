using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Neo.App.Services;
using Neo.App.Ui;
using Neo.Core;
using Neo.Core.Core;
using Neo.Core.Models;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace Neo.App.Pages;

public sealed partial class NodesPage : UserControl
{
    private List<NodeTile> _tiles = new();
    private NodeTile? _selected;
    private bool _realTesting;
    /// TCP 握手延迟测试进行中（与真连测速互不干扰：两边各自判断/落值）。
    private bool _tcpTesting;
    private bool userPickedDuringTest;
    private CancellationTokenSource? _realCts;
    private int _lastCols;
    private DateTime _lastLayoutWidthApply = DateTime.MinValue;

    /// 显示的是「当前时间」，并非测速时间 —— 界面上的「上次真连测速 HH:mm:ss」因此永远等于现在。
    /// null = 本进程内还没跑过真连测速（节点上的延迟可能来自 DelayCache 缓存）。
    private DateTime? _lastRealTestAt;
    private DateTime? _lastTcpTestAt;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _resizeTrailTimer;
    // 节点列表筛选：只看可用 / 只看未测 / 限定地区（空 = 全部）
    private bool _onlyAvailable;
    private bool _onlyUntested;
    private string _regionFilter = "";
    private Neo.App.Ui.PopupList? _filterList;

    public NodesPage()
    {
        InitializeComponent();
        AppState.NodesChanged += RebuildList;
        AppState.UiSettingsChanged += OnUiSettingsChanged;
        Loaded += (_, _) =>
        {
            UpdateTestButtonMode();
            RebuildList();
            try { Ui.Ripple.Attach(RealTestBtn, App.MainWindowInstance?.RippleLayerHost); } catch { }
            // 自动检测只在「页面真正可见」时触发（见 Visibility 回调），
            // 不在启动阶段就把 60+ 个节点全查一遍。
        };
        // 页面切到可见时才做「真实 IP」自动检测：避免启动时后台就把 60+ 节点全查一遍。
        try
        {
            RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
            {
                if (Visibility == Visibility.Visible) TryAutoDetectLocations();
            });
        }
        catch { }
        _resizeTrailTimer = DispatcherQueue.CreateTimer();
        _resizeTrailTimer.Interval = TimeSpan.FromMilliseconds(160);
        _resizeTrailTimer.IsRepeating = false;
        _resizeTrailTimer.Tick += (_, _) => { _lastLayoutWidthApply = DateTime.Now; UpdateLayoutWidth(); };
    }

    /// <summary>退出时停掉本页计时器（Exit 后它仍会触发并写 XAML）。</summary>
    public void StopTimers()
    {
        try { _resizeTrailTimer?.Stop(); } catch { }
    }

    /// <summary>
    /// 仅释放卡片树（保留页面本身）：节点页的内存大头是 68 张卡片（控件+画刷+模板），
    /// 离开页面一段时间后把 ItemsSource 置空，让卡片可被回收；回来时 EnsureTiles 重建。
    /// 实测比「整页移除」更有效——WinUI3 的页面对象/原生资源被框架保留，整页移除工作集不回落。
    /// </summary>
    public void ReleaseTiles()
    {
        // 只清 ItemsSource 没用：本页 _tiles 还持有全部 NodeTile，且订阅刷新会立刻重建列表。
        // 必须同时清 _tiles 并在释放期间忽略 NodesChanged，才能真正把卡片树交出去。
        _tilesReleased = true;
        try { NodeList.ItemsSource = null; } catch { }
        try { _tiles = new List<NodeTile>(); } catch { }
    }

    /// 释放期间忽略「节点列表变化」导致的自动重建（否则后台刷新一跑就白释放了）。
    private bool _tilesReleased;

    /// <summary>回到节点页时若卡片树已被释放则重建（幂等）。</summary>
    public void EnsureTiles()
    {
        try
        {
            _tilesReleased = false;
            if (NodeList.ItemsSource == null || _tiles.Count == 0) RebuildList();
        }
        catch { }
    }

    /// <summary>
    /// 可视树移除并置空。**必须先退订静态事件** —— AppState.NodesChanged 是静态事件，
    /// 不退订的话本页一直被事件源引用，释放等于没释放。
    /// </summary>
    public void Release()
    {
        StopTimers();
        try { AppState.NodesChanged -= RebuildList; } catch { }
        try { AppState.UiSettingsChanged -= OnUiSettingsChanged; } catch { }
        try { NodeList.ItemsSource = null; } catch { }
    }

    /// 「真实 IP 显示」等界面设置变化时：重建卡片，并补一次出口检测（若还没测过）。
    private void OnUiSettingsChanged()
    {
        UpdateTestButtonMode();
        RebuildList();
        TryAutoDetectLocations();
    }

    /// <summary>
    /// 按「设置 → 通用 → 真连测速」切换工具栏主按钮：
    /// 关闭（默认）= 隐藏「真连测速」，「快速测延迟」(TCP) 变主按钮；
    /// 开启 = 「真连测速」恢复主按钮，「快速测延迟」降为次要按钮。
    /// </summary>
    private void UpdateTestButtonMode()
    {
        try
        {
            var real = AppState.State.RealDelayTestEnabled;
            RealTestBtn.Visibility = real ? Visibility.Visible : Visibility.Collapsed;
            TcpTestBtn.Style = (Style)Resources[real ? "ToolBtn" : "ToolBtnPrimary"];
        }
        catch (Exception ex)
        {
            LogBus.App($"切换测速按钮失败（不影响测速）：[{ex.GetType().Name}] {ex.Message}", "Warning");
        }
    }

    private void RebuildList()
    {
        if (_realTesting) return;
        // 空闲释放期间不重建（后台订阅刷新会频繁触发 NodesChanged），
        // 等用户回到节点页时 EnsureTiles 再统一重建。
        if (_tilesReleased && Visibility != Visibility.Visible) return;

        // 排序口径必须与卡片显示一致（NodeItem.DisplayDelay）：关闭真连测速时界面显示的是 TCP，
        var preferReal = AppState.State.RealDelayTestEnabled;
        var nodes = AppState.Nodes
            .OrderBy(n => { var d = n.DisplayDelay(preferReal); return d <= 0 ? int.MaxValue : d; })
            .ToList();

        _tiles = nodes.Select(n => new NodeTile(n)).ToList();
        _selected = null;
        ApplyFilter();
        UpdateFooter();
    }

    private void ApplyFilter()
    {
        var q = SearchBox.Text.Trim().ToLowerInvariant();
        IEnumerable<NodeTile> qry = _tiles;
        if (q.Length > 0) qry = qry.Where(t => t.SearchText.Contains(q));
        if (_onlyAvailable) qry = qry.Where(t => t.RealValue > 0);
        if (_onlyUntested) qry = qry.Where(t => t.RealValue <= 0);
        if (_regionFilter.Length > 0) qry = qry.Where(t => NodeGrouper.RegionOf(t.Node.Name) == _regionFilter);
        var list = qry.ToList();

        var cur = AppState.CurrentNode;
        foreach (var t in list)
            t.SetSelected(cur != null && ReferenceEquals(t.Node, cur));

        NodeList.ItemsSource = list;
        EmptyState.Visibility = _tiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateLayoutWidth();
    }

    private int ComputeCols(double avail)
    {
        var compact = AppState.State.CompactLayout;
        var minW = compact ? 150.0 : 176.0;
        var gap = 8.0;
        var maxCols = compact ? 6 : 5;

        var cols = (int)Math.Floor((avail - 4 + gap) / (minW + gap));
        if (cols < 2) cols = 2;
        if (cols > maxCols) cols = maxCols;
        return cols;
    }

    private void UpdateLayoutWidth()
    {
        var avail = NodeList.ActualWidth;
        if (avail <= 0) return;

        var compact = AppState.State.CompactLayout;
        var cols = ComputeCols(avail);
        _lastCols = cols;

        if (NodeList.ItemsPanelRoot is not ItemsWrapGrid wg) return;

        var cellW = avail / cols - 2.0;
        var cellH = (compact ? 58.0 : 66.0) + 10;

        if (double.IsNaN(wg.ItemWidth) || Math.Abs(wg.ItemWidth - cellW) >= 1.5) wg.ItemWidth = cellW;
        if (double.IsNaN(wg.ItemHeight) || Math.Abs(wg.ItemHeight - cellH) >= 0.5) wg.ItemHeight = cellH;
    }

    private void OnScrollSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 0.5) return;
        var avail = NodeList.ActualWidth;
        if (avail <= 0) return;
        var cols = ComputeCols(avail);
        if (cols == _lastCols && cols < 5) return;

        if ((DateTime.Now - _lastLayoutWidthApply).TotalMilliseconds >= 150)
        {
            _lastLayoutWidthApply = DateTime.Now;
            UpdateLayoutWidth();
        }
        else
        {
            _resizeTrailTimer?.Stop();
            _resizeTrailTimer?.Start();
        }
    }

    private void OnTileTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not NodeTile tile) return;
        userPickedDuringTest = true;
        if (ReferenceEquals(tile, _selected)) return;
        // 全表互斥：不仅灭上次点击的，还要灭"当前连接节点"的常驻高亮，否则双亮
        if (_tiles is not null)
            foreach (var t in _tiles)
                t.SetSelected(ReferenceEquals(t, tile));
        _selected = tile;
        AppState.SetCurrentNode(tile.Node);
        PopCard(fe);
    }

    private void OnTileTestTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not NodeTile tile) return;
        e.Handled = true;
        _ = RunSingleTestAsync(tile);
    }

    private async Task RunSingleTestAsync(NodeTile tile)
    {
        await tile.TestAsync();
        _lastRealTestAt = DateTime.Now;
        UpdateFooter();
    }

    private void OnTileEnter(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b && b.DataContext is NodeTile t && !t.IsSelected)
        {
            b.Background = ThemeUtil.Get("NeoGlass", Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF));
            AnimateCard(b, "TranslateY", b, -2.0, 110);
        }
    }

    private void OnTileLeave(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b && b.DataContext is NodeTile t && !t.IsSelected)
        {
            b.Background = t.CardBg;
            AnimateCard(b, "TranslateY", b, 0.0, 150);
        }
    }

    private static Microsoft.UI.Xaml.Media.CompositeTransform CardTransform(FrameworkElement el)
    {
        if (el.RenderTransform is Microsoft.UI.Xaml.Media.CompositeTransform c)
            return c;
        c = new Microsoft.UI.Xaml.Media.CompositeTransform();
        el.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        el.RenderTransform = c;
        return c;
    }

    private static void AnimateCard(FrameworkElement el, string prop, object target, double to, int ms)
    {
        try
        {
            var ct = CardTransform(el);
            var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            var da = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                To = to,
                Duration = TimeSpan.FromMilliseconds(ms),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
                {
                    EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut,
                },
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(da, ct);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(da, prop);
            sb.Children.Add(da);
            sb.Begin();
        }
        catch { }
    }

    private static void PopCard(FrameworkElement el)
    {
        try
        {
            var ct = CardTransform(el);
            var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            foreach (var prop in new[] { "ScaleX", "ScaleY" })
            {
                var da = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                {
                    To = 1.04,
                    Duration = TimeSpan.FromMilliseconds(110),
                    EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
                    {
                        EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut,
                    },
                };
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(da, ct);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(da, prop);
                sb.Children.Add(da);
            }
            sb.Completed += (_, _) =>
            {
                try
                {
                    var sb2 = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
                    foreach (var prop in new[] { "ScaleX", "ScaleY" })
                    {
                        var da = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                        {
                            To = 1.0,
                            Duration = TimeSpan.FromMilliseconds(150),
                            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
                            {
                                EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseIn,
                            },
                        };
                        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(da, ct);
                        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(da, prop);
                        sb2.Children.Add(da);
                    }
                    sb2.Begin();
                }
                catch { }
            };
            sb.Begin();
        }
        catch { }
    }

    private void OnFlagDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is Border b && b.Child is Grid g && args.NewValue is NodeTile t)
            FlagPainter.Draw(g, t.FlagKey);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApplyFilter();
    }

    private void OnSortClick(object sender, RoutedEventArgs e)
    {
        _tiles = _tiles.OrderBy(t => t.RealValue <= 0 ? int.MaxValue : t.RealValue).ToList();
        ApplyFilter();
    }

    /// 只补测「未测/超时/失败」的节点（日常维护用，几秒就能跑完）。
    private async void OnRetestFailedClick(object sender, RoutedEventArgs e)
    {
        if (_realTesting) { _realCts?.Cancel(); return; }
        var list = (NodeList.ItemsSource as IEnumerable<NodeTile>)?.ToList() ?? _tiles.ToList();
        var failed = list.Where(t => t.RealValue <= 0).ToList();
        if (failed.Count == 0)
        {
            LogBus.App("补测：当前列表没有未测/失败的节点", "Debug");
            return;
        }
        await RunBatchAsync(failed, autoSelect: false);
    }

    /// 筛选菜单：全部 / 只看可用 / 只看未测 / 按地区。
    private void OnFilterTapped(object sender, RoutedEventArgs e)
    {
        _filterList ??= new Neo.App.Ui.PopupList();
        _filterList.ClearItems();
        _filterList.AddItem("全部节点", null, () =>
        {
            _onlyAvailable = false; _onlyUntested = false; _regionFilter = "";
            FilterBtnText.Text = "筛选"; ApplyFilter();
        });
        _filterList.AddItem("只看可用（延迟 > 0）", null, () =>
        {
            _onlyAvailable = true; _onlyUntested = false;
            FilterBtnText.Text = "只看可用"; ApplyFilter();
        });
        _filterList.AddItem("只看未测/失败", null, () =>
        {
            _onlyUntested = true; _onlyAvailable = false;
            FilterBtnText.Text = "只看未测"; ApplyFilter();
        });
        _filterList.AddSeparator();
        var regions = AppState.Nodes.Select(n => NodeGrouper.RegionOf(n.Name)).Distinct().OrderBy(r => r).ToList();
        foreach (var r in regions)
        {
            var region = r;
            _filterList.AddItem("地区：" + NodeGrouper.RegionDisplay(region), null, () =>
            {
                _regionFilter = region; _onlyAvailable = false; _onlyUntested = false;
                FilterBtnText.Text = NodeGrouper.RegionDisplay(region); ApplyFilter();
            });
        }
        _filterList.AddDisabled("筛选只影响显示，不影响连接");
        Neo.App.Ui.PopupList.ShowBelow(_filterList, FilterBtn, 220);
    }

    /// 卡片右键：复制常用信息（名称 / 服务器 / 实测出口 IP）。
    private void OnTileRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not NodeTile tile) return;
        var flyout = new MenuFlyout();
        void AddItem(string text, string value)
        {
            var mi = new MenuFlyoutItem { Text = text };
            mi.Click += (_, _) =>
            {
                try
                {
                    var dp = new DataPackage();
                    dp.SetText(value);
                    Clipboard.SetContent(dp);
                    Clipboard.Flush();
                    LogBus.App($"已复制：{text}", "Debug");
                }
                catch { }
            };
            flyout.Items.Add(mi);
        }
        AddItem("复制节点名称", tile.Node.Name);
        AddItem("复制服务器地址", $"{tile.Node.Server}:{tile.Node.Port}");
        if (!string.IsNullOrEmpty(tile.Node.ExitIp)) AddItem("复制实测出口 IP", tile.Node.ExitIp!);
        flyout.ShowAt(fe, new FlyoutShowOptions { Position = e.GetPosition(fe) });
        e.Handled = true;
    }

    private async void OnRealTestClick(object sender, RoutedEventArgs e)
    {
        if (_realTesting)
        {
            _realCts?.Cancel();
            return;
        }
        // 优化：只测当前列表里实际显示的节点（搜索/筛选后测得更快），
        // 没筛选时等于测全部。
        var list = (NodeList.ItemsSource as IEnumerable<NodeTile>)?.ToList() ?? _tiles.ToList();
        await RunBatchAsync(list, autoSelect: true);
    }

    /// <summary>
    /// 快速测延迟（TCP 握手，对应常见客户端的「测试延迟」）：只测到节点服务器的 TCP 握手，
    /// 不走代理、不写 RealDelay —— 与「真连测速」的结果各存各的、互不覆盖。
    /// </summary>
    private async void OnTcpTestClick(object sender, RoutedEventArgs e)
    {
        if (_realTesting || _tcpTesting) return;   // 真连测速进行中则忽略，避免互相拖慢/干扰
        _tcpTesting = true;
        try { TcpTestBtn.IsEnabled = false; } catch { }
        var list = (NodeList.ItemsSource as IEnumerable<NodeTile>)?.ToList() ?? _tiles.ToList();
        if (list.Count == 0)
        {
            _tcpTesting = false;
            try { TcpTestBtn.IsEnabled = true; } catch { }
            return;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tileByNode = list.GroupBy(t => t.Node).ToDictionary(g => g.Key, g => g.First());
        var done = 0;
        try
        {
            FootTime.Text = $"TCP 延迟测试中 0/{list.Count}…";
            FootAvg.Text = "";
            var result = await TcpDelayTester.TestBatchAsync(
                list.Select(t => t.Node).ToList(),
                concurrency: 32,
                timeoutMs: 3000,
                onResult: (node, ms) => _ = DispatcherQueue.TryEnqueue(() =>
                {
                    if (App.IsShuttingDown) return;
                    if (tileByNode.TryGetValue(node, out var tile)) tile.SetTcpResult(ms);
                    done++;
                    FootTime.Text = $"TCP 延迟测试中 {done}/{list.Count}…";
                }));

            // onResult 的 UI 投递可能还没落地：用返回字典兜底（相同值不重复写）。
            foreach (var kv in result)
            {
                if (tileByNode.TryGetValue(kv.Key, out var tile) && tile.Node.TcpDelay != kv.Value)
                    tile.SetTcpResult(kv.Value);
            }

            //   原实现于是「快速测延迟」永远测不到这些节点（v2rayN 能测是因为它一律走真实代理请求），
            //   而且界面上没有任何解释。这里对这批节点自动补跑一次真连测速（sing-box 临时实例，
            //   内部并发上限 4），让一次点击覆盖全部节点；结果写 RealDelay，
            //   卡片与连接页下拉通过 NodeItem.DisplayDelay 回退显示。
            var udpNodes = list.Select(t => t.Node)
                .Where(n => n.IsValid
                    && !n.IsClashPassthrough && !n.IsCustomConfig && !n.IsNaive
                    && n.TcpDelay == 0
                    && DelayTestPlanner.RequiresRealConnection(n))
                .Distinct()
                .ToList();
            var udpDone = 0;
            if (udpNodes.Count > 0)
            {
                FootTime.Text = $"UDP 协议补测真连 0/{udpNodes.Count}…";
                try
                {
                    await RealDelayTester.RealPingBatchAsync(
                        udpNodes, NeoPaths.RuntimeDir, budgetSeconds: 90,
                        onResult: (node, ms) => _ = DispatcherQueue.TryEnqueue(() =>
                        {
                            if (App.IsShuttingDown) return;
                            if (tileByNode.TryGetValue(node, out var tile)) tile.SetResult(ms);
                            udpDone++;
                            FootTime.Text = $"UDP 协议补测真连 {udpDone}/{udpNodes.Count}…";
                        }));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    LogBus.App($"UDP 协议补测真连失败（不影响其它节点）：[{ex.GetType().Name}] {ex.Message}", "Warning");
                }
                LogBus.App($"UDP 协议节点真连补测完成：{udpDone}/{udpNodes.Count}");
            }

            // 按「当前显示口径」重排卡片：>0 升序，0/-1 统一放最后（999999）。
            _tiles = _tiles.OrderBy(t => t.RealValue > 0 ? t.RealValue : 999999).ToList();
            ApplyFilter();

            var tested = list.Count(t => t.Node.TcpDelay != 0 || t.Node.RealDelay != 0);
            var okCount = list.Count(t => t.RealValue > 0);
            var tcpOk = list.Count(t => t.Node.TcpDelay > 0);
            _lastTcpTestAt = DateTime.Now;
            FootTime.Text = $"已测 {tested}/{list.Count}（耗时 {sw.Elapsed.TotalSeconds:F1} s）";
            FootAvg.Text = okCount > 0
                ? (udpNodes.Count > 0
                    ? $"· TCP 可连 {tcpOk} · 平均 {list.Where(t => t.RealValue > 0).Average(t => t.RealValue):F0} ms"
                    : $"· 平均 TCP {list.Where(t => t.Node.TcpDelay > 0).Average(t => t.Node.TcpDelay):F0} ms")
                : "";
            LogBus.App($"延迟测试完成：{tested}/{list.Count} 已测 · {okCount} 可用"
                + (udpNodes.Count > 0 ? $"（含 UDP 真连补测 {udpDone} 个）" : "")
                + $" · 耗时 {sw.Elapsed.TotalSeconds:F1}s");
        }
        catch (Exception ex)
        {
            LogBus.Exception("TCP 延迟测试失败", ex);
        }
        finally
        {
            _tcpTesting = false;
            try { TcpTestBtn.IsEnabled = true; } catch { }
        }
    }

    /// 把「节点对象 → (国家, IP)」的检测结果按稳定 NodeKey 回填到当前节点列表，
    /// 避免订阅刷新重建对象后结果丢失。返回是否有任何变更。
    private static void ApplyLocations(IReadOnlyDictionary<NodeItem, (string Country, string Ip)> map)
    {
        if (map.Count == 0) return;
        var byKey = new Dictionary<string, (string Country, string Ip)>();
        foreach (var kv in map) byKey[AppState.NodeKey(kv.Key)] = kv.Value;
        foreach (var n in AppState.Nodes)
            if (byKey.TryGetValue(AppState.NodeKey(n), out var loc))
            {
                n.ExitCountry = loc.Country;
                n.ExitIp = loc.Ip;
                if (!string.IsNullOrEmpty(loc.Country)) LocationCache.Set(n, loc.Country, loc.Ip);
            }
    }

    // 「真实 IP 显示」打开后自动跑一次检测（静默，不弹结果框），让卡片直接显示真实出口。
    private bool _geoAutoTried;
    private async void TryAutoDetectLocations()
    {
        if (_geoAutoTried || _geoTesting) return;
        // 开关没开时**不能**把已尝试标记置上，否则用户之后打开开关就再也不会自动检测。
        if (!AppState.State.ShowRealIp) return;
        _geoAutoTried = true;
        try
        {
            var all = AppState.Nodes.ToList();
            if (all.Count == 0) return;
            // 12 小时内的检测结果直接复用；只补测「没有新鲜缓存、也没在本次进程测过」的节点。
            var nodes = all.Where(n =>
                string.IsNullOrEmpty(n.ExitCountry) && !LocationCache.TryGet(n, out _, out _)).ToList();
            if (nodes.Count == 0) return;

            LogBus.App($"真实 IP 显示：后台检测 {nodes.Count} 个节点的出口位置（缓存 12 小时内跳过）…");
            var map = await RealDelayTester.DetectExitLocationsAsync(
                nodes, NeoPaths.RuntimeDir,
                onResult: (node, country, ip) => _ = DispatcherQueue.TryEnqueue(() =>
                {
                    if (App.IsShuttingDown) return;
                    node.ExitCountry = country;
                    node.ExitIp = ip;
                    _tiles.FirstOrDefault(t => ReferenceEquals(t.Node, node))?.RefreshLocation();
                }));

            // 检测期间订阅刷新可能重建了节点对象：按稳定 NodeKey 回填到**当前**列表，
            // 否则结果全落在旧对象上、界面永远不显示（实测踩到过）。
            ApplyLocations(map);
            var detected = map.Values.Count(v => !string.IsNullOrEmpty(v.Country));
            var known = AppState.Nodes.Count(n => !string.IsNullOrEmpty(n.ExitCountry));
            RebuildList();
            LogBus.App($"真实 IP 显示：本次检测 {nodes.Count} 个（成功 {detected}）；当前已显示 {known}/{AppState.Nodes.Count}");
        }
        catch (Exception ex)
        {
            LogBus.App($"真实 IP 显示：出口检测失败（不影响使用）：[{ex.GetType().Name}] {ex.Message}", "Warning");
        }
    }

    // ---- 位置检测（真实出口国家）----
    //   名称里的国旗/地区是订阅方写的，可能与实际出口不符（例如标日本、实际在美国）。
    //   这里经每个节点自己的 socks 入口访问 Cloudflare trace，读出真实 loc，
    //   与名称标记不一致的节点在卡片上标红「实际 XX」，并弹窗列出清单。
    private bool _geoTesting;
    private CancellationTokenSource? _geoCts;

    private async void OnGeoTestClick(object sender, RoutedEventArgs e)
    {
        if (_geoTesting) { _geoCts?.Cancel(); return; }

        var nodes = AppState.Nodes.ToList();
        if (nodes.Count == 0) return;

        _geoTesting = true;
        GeoTestBtnText.Text = "停止";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        LogBus.App($"位置检测开始（{nodes.Count} 个节点 · 经各节点访问 Cloudflare trace）");
        var mismatches = new List<string>();
        var ok = 0;
        try
        {
            _geoCts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var map = await RealDelayTester.DetectExitLocationsAsync(
                nodes, NeoPaths.RuntimeDir,
                onResult: (node, country, ip) => _ = DispatcherQueue.TryEnqueue(() =>
                {
                    if (App.IsShuttingDown) return;
                    node.ExitCountry = country;
                    node.ExitIp = ip;
                    _tiles.FirstOrDefault(t => ReferenceEquals(t.Node, node))?.RefreshLocation();
                }),
                ct: _geoCts.Token);

            ApplyLocations(map);
            ok = AppState.Nodes.Count(n => !string.IsNullOrEmpty(n.ExitCountry));
            foreach (var n in AppState.Nodes)
            {
                var actual = n.ExitCountry ?? "";
                if (string.IsNullOrEmpty(actual)) continue;
                var claimed = AppState.GuessFlag(n.Name);
                if (claimed != "other" && !string.Equals(claimed, actual, StringComparison.OrdinalIgnoreCase))
                    mismatches.Add($"• {n.Name}：标记 {NodeGrouper.RegionDisplay(claimed)} → 实际 {NodeGrouper.RegionDisplay(actual)}");
            }
            foreach (var t in _tiles) t.RefreshLocation();
            RebuildList();

            LogBus.App($"位置检测完成：{ok}/{nodes.Count} 个节点有结果 · 标记与实际不符 {mismatches.Count} 个 · 耗时 {sw.Elapsed.TotalSeconds:F1}s");
            FootTime.Text = $"位置检测：{mismatches.Count} 个标记不符（{DateTime.Now:HH:mm:ss}）";
            FootAvg.Text = "";

            var unresolved = nodes.Count - ok;
            var head = $"已检测 {ok}/{nodes.Count} 个节点"
                + (unresolved > 0 ? $"（{unresolved} 个无响应/超时，未能判定）" : "") + "。";
            var body = mismatches.Count == 0
                ? head + "\n\n出口国家与名称标记全部一致。"
                : head + $"\n\n{mismatches.Count} 个标记与实际出口不符：\n\n" + string.Join("\n", mismatches.Take(20))
                  + (mismatches.Count > 20 ? $"\n… 其余 {mismatches.Count - 20} 个见日志" : "");
            var dlg = new ContentDialog
            {
                Title = "位置检测结果",
                Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, MaxWidth = 560 },
                CloseButtonText = "知道了",
                XamlRoot = XamlRoot,
            };
            await Neo.App.Ui.DialogGuard.ShowAsync(dlg);
        }
        catch (OperationCanceledException)
        {
            LogBus.App("位置检测已取消", "Warning");
        }
        catch (Exception ex)
        {
            LogBus.Exception("位置检测", ex);
        }
        finally
        {
            _geoTesting = false;
            GeoTestBtnText.Text = "位置检测";
            _geoCts?.Dispose();
            _geoCts = null;
        }
    }

    private async Task RunBatchAsync(List<NodeTile> all, bool autoSelect)
    {
        if (all.Count == 0) return;
        if (_realTesting) return;
        _realTesting = true;
        userPickedDuringTest = false;   // 本轮测速的用户干预标记从零开始
        bool canceled = false;
        _realCts = new CancellationTokenSource();
        RealTestBtnText.Text = "停止";

        var sw = System.Diagnostics.Stopwatch.StartNew();
        LogBus.App($"真连测速开始（{all.Count} 个节点 · 单实例批量）");
        //   null 表示「本次没做复核」（补测/未开启），空集合表示「做了复核但没节点通过」。
        IReadOnlyCollection<NodeItem>? usableChecked = null;
        try
        {
            var tileMap = all.GroupBy(t => t.Node).ToDictionary(g => g.Key, g => g.First());
            var done = 0;
            var preselected = false;
            // 测速过程中已测到可用的节点（用于「预热式优选」，不必等全部测完）。
            var usableDuringTest = new List<(NodeItem Node, int Ms)>();
            var map = await RealDelayTester.RealPingBatchAsync(
                all.Select(t => t.Node).ToList(), NeoPaths.RuntimeDir,
                // 批量超时 4.5s：Core 侧已改为单请求计时，4.5s 足够；再长只会拖慢整批
                //（批量是并行的，最后一名决定总耗时）。
                timeoutMs: 4500,
                onResult: (node, ms) => _ = DispatcherQueue.TryEnqueue(() =>
                {
                    if (App.IsShuttingDown) return;
                    if (tileMap.TryGetValue(node, out var tile))
                    {
                        tile.SetResult(ms);
                        done++;
                        // 每个节点都刷新页脚进度（原先每 10 个刷一次，用户以为卡住）。
                        FootTime.Text = $"真连测速中 {done}/{all.Count}…";
                        FootAvg.Text = "";

                        if (ms > 0) usableDuringTest.Add((node, ms));
                        // 预热式优选：已完成 ≥6 个且其中已有 ≥2 个可用 → 先把最低延迟的设为当前节点，
                        // 让用户更早拿到可用节点（仍只取已完成里延迟最低的可用节点）。
                        // 只在全量真连测速（autoSelect）且用户没手动点过时做；结束后仍走最终优选。
                        if (autoSelect && !preselected && !userPickedDuringTest
                            && done >= 6 && usableDuringTest.Count >= 2)
                        {
                            var best = usableDuringTest.OrderBy(x => x.Ms).First();
                            preselected = true;
                            var cur = AppState.CurrentNode;
                            if (cur == null || !string.Equals(AppState.NodeKey(cur), AppState.NodeKey(best.Node), StringComparison.Ordinal))
                            {
                                AppState.SetCurrentNode(best.Node);
                                ApplyTileSelection(best.Node);
                                // 注意别调 UpdateFooter()：它会把页脚改成「上次真连测速…」，冲掉进行中的进度文本。
                                LogBus.App($"真连测速中已预选：{best.Node.Name}（{best.Ms} ms）");
                            }
                        }
                    }
                }),
                ct: _realCts.Token,
                // 复核只对全量真连测速做（autoSelect=true）；补测不复核。
                availabilityRecheck: autoSelect,
                onUsableChecked: usable => { usableChecked = usable; });

            // 批量里超时（-1）/没测到的节点做一次**有限**的单独重试：
            //   只重试前 6 个、总预算 6s、单次 5s —— 避免几个死节点把整批拖到最后。
            var retry = all.Where(t => t.RealValue <= 0).Take(6).ToList();
            if (retry.Count > 0 && _realCts is { IsCancellationRequested: false })
            {
                LogBus.App($"真连测速：重试 {retry.Count} 个（上限 6，预算 6s，单次 5s 超时）…");
                using var retryCts = CancellationTokenSource.CreateLinkedTokenSource(_realCts.Token);
                retryCts.CancelAfter(TimeSpan.FromSeconds(6));
                var retried = 0;
                try
                {
                    await Parallel.ForEachAsync(retry,
                        new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = retryCts.Token },
                        async (tile, ict) =>
                        {
                            var ms = await RealDelayTester.RealPingAsync(
                                tile.Node, NeoPaths.RuntimeDir, timeoutMs: 5000, ct: ict);
                            Interlocked.Increment(ref retried);
                            _ = DispatcherQueue.TryEnqueue(() =>
                            {
                                if (!App.IsShuttingDown) tile.SetResult(ms);
                            });
                        });
                }
                catch (OperationCanceledException)
                {
                    if (_realCts is { IsCancellationRequested: true }) throw;   // 用户点了停止：交给外层标记 canceled
                    LogBus.App($"真连测速：重试预算 6s 用尽，已重试 {retried}/{retry.Count} 个（其余保持未测）", "Warning");
                }
            }
        }
        catch (OperationCanceledException)
        {
            canceled = true;
            LogBus.App("真连测速已取消", "Warning");
        }
        catch (Exception ex)
        {
            LogBus.Exception("真连测速", ex);
        }
        finally
        {
            _realTesting = false;
            RealTestBtnText.Text = "真连测速";
            _realCts?.Dispose();
            _realCts = null;
        }

        if (!canceled) _lastRealTestAt = DateTime.Now;

        var ok = all.Count(t => t.RealValue > 0);
        LogBus.App($"真连测速完成：{ok}/{all.Count} 可达 · 耗时 {sw.Elapsed.TotalSeconds:F1}s");
        // 结束时不重建卡片（重建会整页闪一下）：只按新延迟排序后重新绑定现有卡片对象。
        _tiles = _tiles.OrderBy(t => t.RealValue <= 0 ? int.MaxValue : t.RealValue).ToList();
        ApplyFilter();

        // 自动优选：测速结束后把「延迟最低的可达节点」设为当前节点。
        //   只改当前选中节点 —— AppState.SetCurrentNode 只改选择、不重连；连接中的静默切换由 ConnectionPage 负责。
        //   只在「全量真连测速」（autoSelect=true）时生效：补测未测/失败按钮不走自动优选。
        //   取消测速、或测速期间用户手动点过节点时也不做（尊重用户干预）。
        if (!canceled)
        {
            if (!autoSelect)
                LogBus.App("真连测速：本次为补测，不自动切换节点", "Debug");
            else if (userPickedDuringTest)
                LogBus.App("真连测速：测速期间用户手动选过节点，本次跳过自动优选", "Debug");
            else
                AutoSelectFastestNode(all, usableChecked);
        }

        UpdateFooter();
    }

    /// <summary>
    ///   AWS日本06 延迟更高却正常）。所以优先在「可用性复核通过」的集合里取延迟最低者；
    ///   复核无结果（整体失败/未开启/前 3 名全部不可用）才退回纯延迟，并打日志注明按延迟选择。
    /// 并列时取列表靠前者（LINQ OrderBy 稳定）；-1（超时）与 0（未测）都排除。
    /// 不重连：仅改「当前选中节点」；连接中的静默切换由 ConnectionPage 自己处理。
    /// </summary>
    private void AutoSelectFastestNode(List<NodeTile> measured, IReadOnlyCollection<NodeItem>? usable)
    {
        var reachable = measured.Select(t => t.Node).Where(n => n.RealDelay > 0).ToList();
        if (reachable.Count == 0)
        {
            // 没有任何可达节点：不动作，也不打错误日志（测速本身已记录 0/N 可达）。
            return;
        }

        NodeItem? best = null;
        var viaRecheck = false;
        if (usable is { Count: > 0 })
        {
            // 复核通过的节点按稳定 NodeKey 比对，避免对象身份差异导致漏选。
            var usableKeys = new HashSet<string>(usable.Select(AppState.NodeKey), StringComparer.Ordinal);
            best = reachable.OrderBy(n => n.RealDelay).FirstOrDefault(n => usableKeys.Contains(AppState.NodeKey(n)));
            viaRecheck = best != null;
        }
        if (best == null)
        {
            // 走到这里说明 autoSelect 路径请求过复核（usable 为 null 或空），统一按延迟退回并留痕。
            LogBus.App("真连测速：可用性复核无结果，按延迟选择", "Warning");
            best = reachable.OrderBy(n => n.RealDelay).First();
        }

        var cur = AppState.CurrentNode;
        if (cur != null && string.Equals(AppState.NodeKey(cur), AppState.NodeKey(best), StringComparison.Ordinal))
        {
            LogBus.App(viaRecheck
                ? $"真连测速：复核通过的延迟最低节点「{best.Name}」已是当前节点，无需切换"
                : $"真连测速：延迟最低节点「{best.Name}」已是当前节点，无需切换", "Debug");
            return;
        }

        AppState.SetCurrentNode(best);
        ApplyTileSelection(best);
        LogBus.App(viaRecheck
            ? $"真连测速完成：已自动选择「{best.Name}」（{best.RealDelay} ms · 可用性复核通过）"
            : $"真连测速完成：已自动选择「{best.Name}」（{best.RealDelay} ms · 仅按延迟）");
        UpdateFooter();

        //   已连接且实际出口不是 best 时，走无感切换（失败内部会回退普通重连）。
        var live = ConnectionService.ConnectedNode;
        if (ConnectionService.IsConnected && live != null
            && !string.Equals(AppState.NodeKey(live), AppState.NodeKey(best), StringComparison.Ordinal))
        {
            _ = ConnectionService.SwitchNodeFastAsync(best);
            LogBus.App($"真连测速：当前已连接，正在无感切换到「{best.Name}」", "Debug");
        }
    }

    /// <summary>
    /// 把当前节点高亮同步到 _tiles：只亮 best 对应那张卡片（遍历全部卡片，避免筛选掉的残留旧高亮）。
    /// 若 best 不在 _tiles（订阅刷新后对象被重建等），_selected 置 null 也比留着旧引用正确。
    /// </summary>
    private NodeTile? ApplyTileSelection(NodeItem best)
    {
        var bestKey = AppState.NodeKey(best);
        NodeTile? bestTile = null;
        foreach (var t in _tiles)
        {
            var on = string.Equals(AppState.NodeKey(t.Node), bestKey, StringComparison.Ordinal);
            t.SetSelected(on);
            if (on) bestTile = t;
        }
        _selected = bestTile;
        return bestTile;
    }

    private void UpdateFooter()
    {
        var preferReal = AppState.State.RealDelayTestEnabled;
        NodeCountRun.Text = preferReal
            ? $"共 {AppState.Nodes.Count} 个节点 · 按真连延迟排序"
            : $"共 {AppState.Nodes.Count} 个节点 · 按 TCP 延迟排序";

        var ok = AppState.Nodes.Where(n => n.DisplayDelay(preferReal) > 0).ToList();
        if (ok.Count > 0)
        {
            var avg = ok.Average(n => n.DisplayDelay(preferReal));
            if (preferReal)
            {
                FootTime.Text = _lastRealTestAt is { } t
                    ? "上次真连测速 " + t.ToString("HH:mm:ss")
                    : "真连测速时间未知（延迟来自缓存）";
                FootAvg.Text = $"· 平均真连 {avg:F0} ms";
            }
            else
            {
                FootTime.Text = _lastTcpTestAt is { } t
                    ? "上次 TCP 测速 " + t.ToString("HH:mm:ss")
                    : "TCP 测速时间未知（延迟来自缓存）";
                FootAvg.Text = $"· 平均 TCP {avg:F0} ms";
            }
        }
        else
        {
            FootTime.Text = preferReal ? "真连尚未测速" : "TCP 尚未测速";
            FootAvg.Text = "";
        }
    }

    /// <summary>
    /// 一键注册 Cloudflare WARP：生成 X25519 密钥 → 调 Cloudflare 注册接口 →
    /// 把返回的 peer/endpoint/reserved 拼成 wireguard:// 链接，走「手动节点」通道加入列表。
    /// 仅作为「没有自建 WG 服务端」时的补充出口（免费共享、速度一般）。
    /// </summary>
    private async void OnAddWarpClick(object sender, RoutedEventArgs e)
    {
        if (_realTesting) return;
        try
        {
            var confirm = new ContentDialog
            {
                Title = "添加 WARP 节点",
                Content = "WARP 是 Cloudflare 的免费公网出口（WireGuard）。\n\n"
                        + "· 会向 Cloudflare 注册一个匿名免费账号，生成一个 WireGuard 节点加入「手动节点」\n"
                        + "· 免费共享出口，速度/可用性受地区与策略影响，适合作为没有自建 WG 服务端时的补充\n"
                        + "· 注册请求会自动走当前代理（未连接时直连重试）",
                PrimaryButtonText = "注册并添加",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };
            if (await Neo.App.Ui.DialogGuard.ShowAsync(confirm) != ContentDialogResult.Primary) return;

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            panel.Children.Add(new ProgressRing { IsActive = true, Width = 18, Height = 18 });
            panel.Children.Add(new TextBlock { Text = "正在向 Cloudflare 注册…", FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
            var busy = new ContentDialog { Title = "WARP", Content = panel, XamlRoot = XamlRoot };
            var showTask = Neo.App.Ui.DialogGuard.ShowAsync(busy);
            var (ok, msg, link) = await WarpService.RegisterAsync();
            try { busy.Hide(); } catch { }
            try { await showTask; } catch { }

            if (!ok || string.IsNullOrEmpty(link))
            {
                await Neo.App.Ui.DialogGuard.ShowAsync(new ContentDialog
                {
                    Title = "WARP 注册失败",
                    Content = msg,
                    CloseButtonText = "知道了",
                    XamlRoot = XamlRoot,
                });
                return;
            }

            var (added, failed) = AppState.AddManualLinks(new[] { link });
            RebuildList();
            LogBus.App($"WARP 节点已添加（added={added}, failed={failed}）：{msg}", "Info");
            await Neo.App.Ui.DialogGuard.ShowAsync(new ContentDialog
            {
                Title = "WARP 节点已添加",
                Content = msg + "\n\n请在节点列表选择刚添加的「WARP」节点后连接（重复注册会生成不同后缀的节点，可分别测速后保留快的）。",
                CloseButtonText = "知道了",
                XamlRoot = XamlRoot,
            });
        }
        catch (Exception ex)
        {
            LogBus.App($"WARP 注册失败：{ex.GetType().Name} {ex.Message}", "Warning");
            try
            {
                await Neo.App.Ui.DialogGuard.ShowAsync(new ContentDialog
                {
                    Title = "WARP 注册失败",
                    Content = ex.Message,
                    CloseButtonText = "知道了",
                    XamlRoot = XamlRoot,
                });
            }
            catch { }
        }
    }

    private async void OnAddManualClick(object sender, RoutedEventArgs e)
    {
        var input = new TextBox
        {
            PlaceholderText = "每行一条分享链接：vmess:// vless:// trojan:// ss:// hysteria2:// tuic://",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Height = 110,
        };
        var status = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeUtil.Get("NeoText2", Color.FromArgb(255, 0x6B, 0x72, 0x80)),
        };
        var manualList = new StackPanel { Spacing = 6 };

        void RefreshManualList()
        {
            manualList.Children.Clear();
            if (AppState.ManualLinks.Count == 0)
            {
                manualList.Children.Add(new TextBlock
                {
                    Text = "暂无手动节点",
                    FontSize = 11,
                    Foreground = ThemeUtil.Get("NeoText3", Color.FromArgb(255, 0x98, 0xA0, 0xAA)),
                });
                return;
            }

            for (var i = 0; i < AppState.ManualLinks.Count; i++)
            {
                var link = AppState.ManualLinks[i];
                var node = Neo.Core.Parsing.ShareLinkParser.Parse(link);

                var row = new Grid { ColumnSpacing = 10 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var label = new TextBlock
                {
                    FontSize = 11.5,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = ThemeUtil.Get("NeoText1", Color.FromArgb(255, 0x23, 0x28, 0x2E)),
                    Text = node == null
                        ? "（无法解析的链接）"
                        : $"{(string.IsNullOrWhiteSpace(node.Name) ? node.Server : node.Name)} · {AppState.ProtocolName(node.Protocol)} · {node.Server}:{node.Port}",
                };
                Grid.SetColumn(label, 0);
                row.Children.Add(label);

                var del = new Button
                {
                    Content = "删除",
                    FontSize = 11,
                    Padding = new Thickness(10, 4, 10, 4),
                    CornerRadius = new CornerRadius(7),
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(del, $"ManualDel_{i}");
                var captured = link;
                del.Click += (_, _) =>
                {
                    AppState.RemoveManualLink(captured);
                    RefreshManualList();
                    status.Text = "已删除 1 个手动节点";
                    status.Foreground = ThemeUtil.Get("NeoRed", Color.FromArgb(255, 0xDC, 0x26, 0x26));
                };
                Grid.SetColumn(del, 1);
                row.Children.Add(del);

                manualList.Children.Add(row);
            }
        }

        var content = new StackPanel { Spacing = 10, MinWidth = 460 };
        content.Children.Add(new TextBlock { Text = "粘贴分享链接（可一次多行）：", FontSize = 12 });
        content.Children.Add(input);
        content.Children.Add(status);
        content.Children.Add(new Border
        {
            Height = 1,
            Background = ThemeUtil.Get("NeoHairline2", Color.FromArgb(0x0C, 0x1A, 0x1D, 0x21)),
        });
        content.Children.Add(new TextBlock { Text = "已有手动节点：", FontSize = 12 });
        content.Children.Add(manualList);
        RefreshManualList();

        var dlg = new ContentDialog
        {
            Title = "手动节点",
            Content = new ScrollViewer
            {
                Content = content,
                MaxHeight = 440,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
            PrimaryButtonText = "添加",
            CloseButtonText = "完成",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        dlg.PrimaryButtonClick += (_, args) =>
        {
            var (added, failed) = AppState.AddManualLinks(input.Text.Split(new[] { (char)13, (char)10 }, StringSplitOptions.RemoveEmptyEntries));
            args.Cancel = true;
            if (added == 0 && failed == 0)
            {
                status.Text = "没有新内容 — 粘贴分享链接后点「添加」";
                status.Foreground = ThemeUtil.Get("NeoYellow", Color.FromArgb(255, 0xD9, 0x77, 0x06));
                return;
            }
            input.Text = "";
            status.Text = failed > 0 ? $"已添加 {added} 个节点（{failed} 行无法解析）" : $"已添加 {added} 个节点";
            status.Foreground = ThemeUtil.Get("NeoGreen", Color.FromArgb(255, 0x16, 0xA3, 0x4A));
            RefreshManualList();
        };
        await Neo.App.Ui.DialogGuard.ShowAsync(dlg);
    }
}
