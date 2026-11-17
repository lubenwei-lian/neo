using System.Linq;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
    private readonly ObservableCollection<NodeTile> _visibleTiles = new();
    private ScrollViewer? _nodeScrollViewer;
    private double? _scrollOffsetBeforeSort;
    private NodeTile? _selected;
    private bool _realTesting;
    private int _phaseNo;
    private string? _phaseLabel;

    /// TCP 握手延迟测试进行中（与真连测速互不干扰：两边各自判断/落值）。
    private bool _tcpTesting;
    private bool userPickedDuringTest;
    private CancellationTokenSource? _realCts;
    private DateTime _lastLayoutWidthApply = DateTime.MinValue;

    /// 显示的是「当前时间」，并非测速时间 —— 界面上的「上次真连测速 HH:mm:ss」因此永远等于现在。
    /// null = 本进程内还没跑过真连测速（节点上的延迟可能来自 DelayCache 缓存）。
    private DateTime? _lastRealTestAt;
    private DateTime? _lastTcpTestAt;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _resizeTrailTimer;
    // 节点列表筛选：只看可用 / 只看未测 / 限定地区（空 = 全部）
    private bool _onlyAvailable;
    private bool _onlyUntested;
    private bool _onlyTimeout;
    private string _regionFilter = "";
    private Neo.App.Ui.PopupList? _filterList;

    public NodesPage()
    {
        InitializeComponent();
        AppState.NodesChanged += RebuildList;
        StartupSpeedTest.LatencyChanged += OnStartupLatencyChanged;
        AppState.UiSettingsChanged += OnUiSettingsChanged;
        CurrentExitService.Changed += OnCurrentExitChanged;
        Loaded += (_, _) =>
        {
            UpdateTestButtonMode();
            // NavigateTo may already have populated the list before Loaded. Do not replace
            // every item/container a second time during the same first navigation.
            if (_tiles.Count == 0 || _pendingRebuild) RebuildList();
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
        NodeList.LayoutUpdated -= RestoreScrollAfterSort;
        _scrollOffsetBeforeSort = null;
    }

    /// <summary>
    /// 仅释放卡片树（保留页面本身）：节点页的内存大头是 68 张卡片（控件+画刷+模板），
    /// 离开页面一段时间后把 ItemsSource 置空，让卡片可被回收；回来时 EnsureTiles 重建。
    /// 实测比「整页移除」更有效——WinUI3 的页面对象/原生资源被框架保留，整页移除工作集不回落。
    /// </summary>
    public void ReleaseTiles()
    {
        // 测速/检测进行中不能清卡片树：RunBatchAsync 持有这些 NodeTile 的引用，
        // 清空后即使测速结束也只剩空列表（实测：切到连接页再回来，节点整页空白）。
        if (_realTesting || _tcpTesting || _geoTesting)
        {
            LogBus.App("内存优化：测速/检测进行中，跳过节点卡片释放", "Debug");
            return;
        }
        // 只清 ItemsSource 没用：本页 _tiles 还持有全部 NodeTile，且订阅刷新会立刻重建列表。
        // 必须同时清 _tiles 并在释放期间忽略 NodesChanged，才能真正把卡片树交出去。
        _tilesReleased = true;
        try { NodeList.ItemsSource = null; } catch { }
        // Retain the small view-model list. Clearing ItemsSource releases native
        // containers; rebuilding all models/brushes is unnecessary on the next visit.
    }

    /// 释放期间忽略「节点列表变化」导致的自动重建（否则后台刷新一跑就白释放了）。
    private bool _tilesReleased;

    /// 测速期间收到了重建请求（订阅刷新等）：测速结束后补一次，避免列表停在旧数据。
    private bool _pendingRebuild;
    private bool? _builtPreferReal;

    /// <summary>回到节点页时若卡片树已被释放则重建（幂等）。</summary>
    public void EnsureTiles()
    {
        try
        {
            var released = _tilesReleased;
            _tilesReleased = false;
            if (released || NodeList.ItemsSource == null || _tiles.Count == 0 || _pendingRebuild) RebuildList();
            else { ApplyFilter(); UpdateLayoutWidth(); }
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
        StartupSpeedTest.LatencyChanged -= OnStartupLatencyChanged;
        try { AppState.UiSettingsChanged -= OnUiSettingsChanged; } catch { }
        CurrentExitService.Changed -= OnCurrentExitChanged;
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
            TcpTestBtn.Visibility = real ? Visibility.Collapsed : Visibility.Visible;
            TcpTestBtn.Style = (Style)Resources["ToolBtnPrimary"];
        }
        catch (Exception ex)
        {
            LogBus.App($"切换测速按钮失败（不影响测速）：[{ex.GetType().Name}] {ex.Message}", "Warning");
        }
    }

    private void RebuildList()
    {
        if (_realTesting || _tcpTesting)
        {
            // 测速期间不能换卡片对象（tileMap 持有的是旧引用，换了结果就落不到界面）；
            // 但必须记住这次请求，测速结束统一重建，否则订阅刷新/切页回来会一直空白。
            _pendingRebuild = true;
            return;
        }
        // 空闲释放期间不重建（后台订阅刷新会频繁触发 NodesChanged），
        // 等用户回到节点页时 EnsureTiles 再统一重建。
        if (Visibility != Visibility.Visible)
        {
            _pendingRebuild = true;
            return;
        }

        _pendingRebuild = false;
        // A subscription refresh can replace NodeItem instances while startup probes still
        // complete against the previous snapshot. Hydrate the current models by fingerprint
        // before sorting/rendering; Apply preserves any result already on a current model.
        foreach (var node in AppState.Nodes) DelayCache.Apply(node);
        var currentModels = AppState.Nodes.ToHashSet();
        if (_builtPreferReal == AppState.State.RealDelayTestEnabled && _tiles.Count == currentModels.Count && _tiles.All(t => currentModels.Contains(t.Node) && t.PresentationMatches))
        {
            // Cached/background results change order without replacing the models or source.
            foreach (var tile in _tiles) { tile.RefreshDelay(); tile.RefreshLocation(); }
            RefreshLatencyOrder();
            UpdateFooter();
            return;
        }
        // 排序口径必须与卡片显示一致（NodeItem.DisplayDelay）：关闭真连测速时界面显示的是 TCP，
        var preferReal = AppState.State.RealDelayTestEnabled;
        _builtPreferReal = preferReal;
        var nodes = AppState.Nodes
            .OrderBy(n => { var d = n.DisplayDelay(preferReal); return d <= 0 ? int.MaxValue : d; })
            .ToList();

        _tiles = nodes.Select(n => new NodeTile(n)).ToList();
        _selected = null;
        ApplyFilter();
        UpdateFooter();
    }

    private void RefreshLatencyOrder(NodeTile? updatedTile = null)
    {
        // OrderBy is stable: equal delays and unmeasured/failed results retain their order.
        // RealValue uses the same TCP/real preference and fallback as the visible badge.
        _tiles = _tiles.OrderBy(t => t.RealValue > 0 ? t.RealValue : int.MaxValue).ToList();
        ApplyFilter(updatedTile);
    }

    private void ApplyLatencyResult(NodeTile tile, int ms, bool tcp = false)
    {
        if (tcp) tile.SetTcpResult(ms); else tile.SetResult(ms);
        RefreshLatencyOrder(tile);
    }

    private void OnStartupLatencyChanged(NodeItem node)
    {
        if (App.IsShuttingDown) return;
        if (_tilesReleased || Visibility != Visibility.Visible) { _pendingRebuild = true; return; }
        // Subscription refresh may have replaced the model the background probe captured.
        var tile = _tiles.FirstOrDefault(t => ReferenceEquals(t.Node, node))
            ?? _tiles.FirstOrDefault(t => AppState.NodeKey(t.Node) == AppState.NodeKey(node));
        if (tile == null) return;
        tile.Node.RealDelay = node.RealDelay;
        tile.Node.RealDelayNote = node.RealDelayNote;
        tile.RefreshDelay();
        RefreshLatencyOrder(tile);
    }

    private void ApplyFilter(NodeTile? updatedTile = null)
    {
        var q = SearchBox.Text.Trim().ToLowerInvariant();
        IEnumerable<NodeTile> qry = _tiles;
        if (q.Length > 0) qry = qry.Where(t => t.SearchText.Contains(q));
        if (_onlyAvailable) qry = qry.Where(t => t.RealValue > 0);
        if (_onlyUntested) qry = qry.Where(t => t.RealValue == 0);
        if (_onlyTimeout) qry = qry.Where(t => t.RealValue < 0);
        if (_regionFilter.Length > 0) qry = qry.Where(t => NodeGrouper.RegionOf(t.Node.Name) == _regionFilter);
        var list = qry.ToList();

        var cur = AppState.CurrentNode;
        // A connection can complete against the pre-refresh snapshot. Match the stable
        // identity for presentation without replacing or switching the connected node.
        var selected = cur == null ? null : list.FirstOrDefault(t => ReferenceEquals(t.Node, cur));
        if (selected == null && cur != null)
        {
            var currentKey = AppState.NodeKey(cur);
            selected = list.FirstOrDefault(t => AppState.NodeKey(t.Node) == currentKey);
        }
        foreach (var t in list)
            t.SetSelected(ReferenceEquals(t, selected));
        _selected = selected;

        // Keep a single observable source. A completed probe moves its own card once,
        // rather than rebinding the grid or moving every card it passes on the way down.
        var wanted = list.ToHashSet();
        if (_visibleTiles.Count == list.Count && _visibleTiles.All(wanted.Contains) && !_visibleTiles.SequenceEqual(list))
            PreserveScrollDuringSort();
        for (var i = _visibleTiles.Count - 1; i >= 0; i--)
            if (!wanted.Contains(_visibleTiles[i])) _visibleTiles.RemoveAt(i);
        if (updatedTile != null)
        {
            var from = _visibleTiles.IndexOf(updatedTile);
            var to = list.IndexOf(updatedTile);
            if (from >= 0 && to >= 0 && to < _visibleTiles.Count && from != to) _visibleTiles.Move(from, to);
        }
        for (var i = 0; i < list.Count; i++)
        {
            if (i < _visibleTiles.Count && ReferenceEquals(_visibleTiles[i], list[i])) continue;
            var from = _visibleTiles.IndexOf(list[i]);
            if (from >= 0) _visibleTiles.Move(from, i);
            else _visibleTiles.Insert(i, list[i]);
        }
        if (!ReferenceEquals(NodeList.ItemsSource, _visibleTiles)) NodeList.ItemsSource = _visibleTiles;
        // 空状态必须按「筛选后的列表」判断：只看可用/只看未测/搜索可能把 39 个节点全过滤掉，
        // 原先按 _tiles.Count 判断会留下整页空白且没有任何提示。
        var noTiles = _tiles.Count == 0;
        EmptyState.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (EmptyState.Children.Count >= 2)
        {
            if (EmptyState.Children[0] is TextBlock title)
                title.Text = noTiles ? "还没有节点" : "没有匹配的节点";
            if (EmptyState.Children[1] is TextBlock hint)
                hint.Text = noTiles
                    ? "去「订阅」页添加订阅，或用工具栏「手动节点」粘贴分享链接导入"
                    : "点击「全部」，或清空搜索与地区筛选";
        }
        UpdateLayoutWidth();
    }

    private void PreserveScrollDuringSort()
    {
        if (_scrollOffsetBeforeSort != null || _tilesReleased || Visibility != Visibility.Visible) return;
        static ScrollViewer? FindScroll(DependencyObject parent)
        {
            if (parent is ScrollViewer viewer) return viewer;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                if (FindScroll(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
            return null;
        }
        _nodeScrollViewer ??= FindScroll(NodeList);
        if (_nodeScrollViewer == null) return;
        _scrollOffsetBeforeSort = _nodeScrollViewer.VerticalOffset;
        NodeList.LayoutUpdated += RestoreScrollAfterSort;
    }

    private void RestoreScrollAfterSort(object? sender, object e)
    {
        NodeList.LayoutUpdated -= RestoreScrollAfterSort;
        var offset = _scrollOffsetBeforeSort;
        _scrollOffsetBeforeSort = null;
        if (offset is not { } value || _nodeScrollViewer == null || App.IsShuttingDown) return;
        // ItemsWrapGrid anchors the previous item when a collection moves. Preserve the
        // user's viewport instead; coalesce a burst into one correction after layout.
        if (Math.Abs(_nodeScrollViewer.VerticalOffset - value) > .5)
            _nodeScrollViewer.ChangeView(null, value, null, true);
    }

    private int ComputeCols(double avail)
    {
        // Seven columns on a wide desktop; keep readable cards in smaller windows.
        var viewportWidth = XamlRoot?.Size.Width ?? avail;
        return viewportWidth <= 900 ? 2 : viewportWidth <= 1199 ? 3 : viewportWidth < 1600 ? 5 : 7;
    }

    private void UpdateLayoutWidth()
    {
        var avail = NodeList.ActualWidth;
        if (avail <= 0) return;

        var compact = AppState.State.CompactLayout;
        var cols = ComputeCols(avail);

        if (NodeList.ItemsPanelRoot is not ItemsWrapGrid wg) return;

        var cellW = avail / cols;
        var cellH = (compact ? 63.0 : 72.0) * Ui.AdaptiveText.Scale + 10;

        if (double.IsNaN(wg.ItemWidth) || Math.Abs(wg.ItemWidth - cellW) >= 1.5) wg.ItemWidth = cellW;
        if (double.IsNaN(wg.ItemHeight) || Math.Abs(wg.ItemHeight - cellH) >= 0.5) wg.ItemHeight = cellH;
        var count = (NodeList.ItemsSource as System.Collections.ICollection)?.Count ?? 0;
        var availableHeight = NodesLayout.ActualHeight - NodeToolbar.ActualHeight - NodeFooter.ActualHeight - NodesLayout.RowSpacing * 2;
        if (availableHeight > 1)
        {
            var extent = count == 0 ? 180 : Math.Ceiling((double)count / cols) * cellH + NodeList.Padding.Top + NodeList.Padding.Bottom;
            var height = Math.Max(0, Math.Min(availableHeight, extent));
            if (NodesLayout.RowDefinitions[1].Height.IsStar || Math.Abs(NodesLayout.RowDefinitions[1].Height.Value - height) >= .5)
                NodesLayout.RowDefinitions[1].Height = new GridLength(height);
        }
    }

    private void OnScrollSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 0.5 && Math.Abs(e.NewSize.Height - e.PreviousSize.Height) < 0.5) return;
        var avail = NodeList.ActualWidth;
        if (avail <= 0) return;
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

    private void OnNodeListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)) return;
        if (e.OriginalSource is not GridViewItem { Content: NodeTile tile }) return;
        userPickedDuringTest = true;
        foreach (var t in _tiles) t.SetSelected(ReferenceEquals(t, tile));
        _selected = tile;
        AppState.SetCurrentNode(tile.Node);
        e.Handled = true;
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
        // Selection is the original colour/edge change; it does not enlarge neighbouring cards.
    }

    private void OnTileTestTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not NodeTile tile) return;
        e.Handled = true;
        _ = RunSingleTestAsync(tile);
    }

    // A latency action never bubbles into card selection, including the selected card.
    private void OnDelayTapHandled(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    private void OnTileTestClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NodeTile tile })
            _ = RunSingleTestAsync(tile);
    }

    private async Task RunSingleTestAsync(NodeTile tile)
    {
        await tile.TestAsync();
        _lastRealTestAt = DateTime.Now;
        RefreshLatencyOrder(tile);
        UpdateFooter();
    }

    private void OnTileEnter(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border { DataContext: NodeTile tile } border) return;
        Ui.SurfaceEdge.SetNodeHovered(border, true);
        border.Background = tile.IsSelected ? tile.CardBg : ThemeUtil.Get("NeoNodeHover", Microsoft.UI.Colors.Transparent);
        border.BorderBrush = new SolidColorBrush(Color.FromArgb(tile.IsSelected ? (byte)166 : (byte)26, tile.IsSelected ? (byte)90 : (byte)255, tile.IsSelected ? (byte)169 : (byte)255, 255));
        if (NodeList.ContainerFromItem(tile) is UIElement container) Canvas.SetZIndex(container, 2);
    }

    private void OnTileLeave(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border { DataContext: NodeTile tile } border) return;
        Ui.SurfaceEdge.SetNodeHovered(border, false);
        border.Background = tile.CardBg; border.BorderBrush = tile.CardBorder;
        if (NodeList.ContainerFromItem(tile) is UIElement container) Canvas.SetZIndex(container, tile.IsSelected ? 1 : 0);
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
    private void OnNodePageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (NodeHeader == null) return;
        var compact = e.NewSize.Width < 800;
        NodeHeader.RowSpacing = compact ? 8 : 0;
        Grid.SetRow(NodeSearch, compact ? 1 : 0);
        Grid.SetColumn(NodeSearch, compact ? 0 : 1);
        Grid.SetColumnSpan(NodeSearch, compact ? 2 : 1);
        Grid.SetRow(NodeActions, compact ? 1 : 0);
        Grid.SetColumnSpan(NodeTitle, compact ? 3 : 1);
        if (compact) { Ui.AdaptiveText.SetBaseWidth(NodeSearch, double.NaN); NodeSearch.Width = double.NaN; }
        else Ui.AdaptiveText.SetBaseWidth(NodeSearch, 240);
        NodeActionHint.Visibility = e.NewSize.Width < 1100 ? Visibility.Collapsed : Visibility.Visible;
        UpdateLayoutWidth();
    }

    private void OnStatusFilterClick(object sender, RoutedEventArgs e)
    {
        var status = (sender as FrameworkElement)?.Tag as string ?? "all";
        _onlyAvailable = status == "available";
        _onlyUntested = status == "untested";
        _onlyTimeout = status == "timeout";
        foreach (var button in NodeStatusFilters.Children.OfType<Button>())
        {
            var active = button.Tag as string == status;
            button.Background = active ? ThemeUtil.Get("NeoAccent", Microsoft.UI.Colors.Transparent) : null;
            button.Foreground = active ? new SolidColorBrush(Microsoft.UI.Colors.White) : ThemeUtil.Get("NeoText2", Microsoft.UI.Colors.Gray);
        }
        ApplyFilter();
    }

    private void OnFilterTapped(object sender, RoutedEventArgs e)
    {
        _filterList ??= new Ui.PopupList();
        _filterList.ClearItems();
        _filterList.AddItem("全部地区", null, () => { _regionFilter = ""; FilterBtnText.Text = "全部地区"; ApplyFilter(); });
        foreach (var region in _tiles.Select(t => NodeGrouper.RegionOf(t.Node.Name)).Distinct())
        {
            var value = region;
            _filterList.AddItem(NodeGrouper.RegionDisplay(region), null, () => {
                _regionFilter = value; FilterBtnText.Text = NodeGrouper.RegionDisplay(value); ApplyFilter();
            });
        }
        _filterList.AddDisabled("筛选只影响显示，不影响连接");
        Ui.PopupList.ShowBelow(_filterList, FilterBtn, 220);
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
        await RunBatchAsync(list, autoSelect: false);
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
                    if (tileByNode.TryGetValue(node, out var tile)) ApplyLatencyResult(tile, ms, tcp: true);
                    done++;
                    FootTime.Text = $"TCP 延迟测试中 {done}/{list.Count}…";
                }));

            // onResult 的 UI 投递可能还没落地：用返回字典兜底（相同值不重复写）。
            foreach (var kv in result)
            {
                if (tileByNode.TryGetValue(kv.Key, out var tile) && tile.Node.TcpDelay != kv.Value)
                    ApplyLatencyResult(tile, kv.Value, tcp: true);
            }

            //   原实现于是「快速测延迟」永远测不到这些节点（v2rayN 能测是因为它一律走真实代理请求），
            //   而且界面上没有任何解释。这里对这批节点自动补跑一次真连测速（sing-box 临时实例，
            //   内部并发上限 4），让一次点击覆盖全部节点；结果写 RealDelay，
            //   卡片与连接页下拉通过 NodeItem.DisplayDelay 回退显示。
            var udpNodes = list.Select(t => t.Node)
                .Where(n => n.IsValid
                    && !n.IsClashPassthrough && !n.IsCustomConfig
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
                        udpNodes, NeoPaths.RuntimeDir,
                        onResult: (node, ms) => _ = DispatcherQueue.TryEnqueue(() =>
                        {
                            if (App.IsShuttingDown) return;
                            if (tileByNode.TryGetValue(node, out var tile)) ApplyLatencyResult(tile, ms);
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

            RefreshLatencyOrder();

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
            if (_pendingRebuild) RebuildList();
        }
    }

    // Location belongs to the committed connection. Visiting this page must not
    // spawn a second kernel to scan every subscription node.
    private void TryAutoDetectLocations()
    {
        if (!AppState.State.ShowRealIp) return;
        ApplyCurrentExit();
        _ = CurrentExitService.RunAsync();
    }

    private void OnCurrentExitChanged()
    {
        if (App.IsShuttingDown) return;
        DispatcherQueue.TryEnqueue(() => { if (!App.IsShuttingDown) ApplyCurrentExit(); });
    }

    private void ApplyCurrentExit()
    {
        var exit = CurrentExitService.Current;
        var connected = ConnectionService.ConnectedNode;
        if (!ConnectionService.IsConnected || exit.Epoch != ConnectionService.SessionEpoch || !exit.Result.Confirmed
            || connected == null || ConnectionService.FixedExitActive || AppState.State.ProxyMode == "direct") return;
        var key = AppState.NodeKey(connected);
        foreach (var node in AppState.Nodes.Where(n => AppState.NodeKey(n) == key))
        {
            node.ExitCountry = exit.Result.Country; node.ExitIp = exit.Result.Ip;
            _tiles.FirstOrDefault(t => ReferenceEquals(t.Node, node))?.RefreshLocation();
        }
    }

    private bool _geoTesting;
    private async void OnGeoTestClick(object sender, RoutedEventArgs e)
    {
        if (_geoTesting) { CurrentExitService.Cancel(); return; }
        if (!ConnectionService.IsConnected) { FootTime.Text = "连接节点后检测当前出口"; return; }
        _geoTesting = true; GeoTestBtnText.Text = "停止出口检测";
        try
        {
            await CurrentExitService.RunAsync(refresh: true);
            if (App.IsShuttingDown) return;
            ApplyCurrentExit();
            var exit = CurrentExitService.Current;
            FootTime.Text = exit.Epoch == ConnectionService.SessionEpoch && exit.Result.Confirmed
                ? "当前出口：" + Ui.CurrentExitDisplay.Region(exit.Result.Country) + " · " + exit.Result.Ip : "当前出口未确认";
            FootAvg.Text = "";
        }
        catch (Exception ex) { LogBus.Exception("当前出口检测", ex); }
        finally { _geoTesting = false; if (!App.IsShuttingDown) GeoTestBtnText.Text = "检测当前出口"; }
    }

    /// <summary>底部居中的进度文本：阶段/已测数、重试轮次，完成消息停留 3 秒后清空。</summary>
    private async void UpdateFootProgress(DelayTestProgress p)
    {
        if (App.IsShuttingDown || FootCenter == null) return;
        if (p.Phase is DelayTestPhase.Tcp or DelayTestPhase.Udp)
        {
            if (!string.Equals(_phaseLabel, p.Label, StringComparison.Ordinal))
            {
                _phaseNo = _phaseNo <= 0 ? 1 : 2;
                _phaseLabel = p.Label;
            }
            FootCenter.Text = $"阶段 {_phaseNo}/2 · {p.Label} 测速中 {p.Done}/{p.Total}";
            FootTime.Text = $"真连测速中 {p.Done}/{p.Total}…";
            return;
        }
        if (p.Phase == DelayTestPhase.Retry)
        {
            FootCenter.Text = $"失败重试 {p.RetryRound}/{p.RetryLimit} · 剩余 {p.Remaining} 个";
            FootTime.Text = $"真连测速中 {p.Done}/{p.Total}…";
            return;
        }
        if (p.Phase == DelayTestPhase.Done)
        {
            var msg = p.Message;
            FootCenter.Text = msg;
            await Task.Delay(3000);
            if (!App.IsShuttingDown && !_realTesting && string.Equals(FootCenter.Text, msg, StringComparison.Ordinal))
                FootCenter.Text = "";
        }
    }

    private async Task RunBatchAsync(List<NodeTile> all, bool autoSelect)
    {
        if (all.Count == 0) return;
        if (_realTesting) return;
        _realTesting = true;
        _phaseNo = 0;
        _phaseLabel = null;
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
                onResult: (node, ms) => _ = DispatcherQueue.TryEnqueue(() =>
                {
                    if (App.IsShuttingDown) return;
                    if (tileMap.TryGetValue(node, out var tile))
                    {
                        ApplyLatencyResult(tile, ms);
                        done++;
                        // 每个节点都刷新页脚进度（原先每 10 个刷一次，用户以为卡住）。
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
                onProgress: p => _ = DispatcherQueue.TryEnqueue(() => UpdateFootProgress(p)),
                // 复核只对全量真连测速做（autoSelect=true）；补测不复核。
                availabilityRecheck: autoSelect,
                onUsableChecked: usable => { usableChecked = usable; });

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
            if (FootCenter != null) FootCenter.Text = "";
        }

        if (!canceled) _lastRealTestAt = DateTime.Now;

        var ok = all.Count(t => t.RealValue > 0);
        LogBus.App($"真连测速完成：{ok}/{all.Count} 可达 · 耗时 {sw.Elapsed.TotalSeconds:F1}s");
        // 测速期间若卡片树被释放（旧版本）或收到过重建请求，这里必须补建；
        // 否则 _tiles 为空时 ApplyFilter 只会把空列表绑上去，节点页永久空白。
        if (_pendingRebuild || _tiles.Count == 0)
        {
            RebuildList();
        }
        else
        {
            RefreshLatencyOrder();
        }

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
        var ok = AppState.Nodes.Where(n => n.DisplayDelay(preferReal) > 0).ToList();
        NodeCountRun.Text = $"{AppState.Nodes.Count} 个节点 · {ok.Count} 可用"
            + (ok.Count > 0 ? $" · 平均 {ok.Average(n => n.DisplayDelay(preferReal)):F0} ms" : "");
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
            panel.Children.Add(Neo.App.Ui.AdaptiveText.WithSize(new TextBlock { Text = "正在向 Cloudflare 注册…", VerticalAlignment = VerticalAlignment.Center }, 12.5));
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
        var status = Neo.App.Ui.AdaptiveText.WithSize(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeUtil.Get("NeoText2", Color.FromArgb(255, 0x6B, 0x72, 0x80)),
        }, 11);
        var manualList = new StackPanel { Spacing = 6 };

        void RefreshManualList()
        {
            manualList.Children.Clear();
            if (AppState.ManualLinks.Count == 0)
            {
                manualList.Children.Add(Neo.App.Ui.AdaptiveText.WithSize(new TextBlock
                {
                    Text = "暂无手动节点",
                    Foreground = ThemeUtil.Get("NeoText3", Color.FromArgb(255, 0x98, 0xA0, 0xAA)),
                }, 11));
                return;
            }

            for (var i = 0; i < AppState.ManualLinks.Count; i++)
            {
                var link = AppState.ManualLinks[i];
                var node = Neo.Core.Parsing.ShareLinkParser.Parse(link);

                var row = new Grid { ColumnSpacing = 10 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var label = Neo.App.Ui.AdaptiveText.WithSize(new TextBlock
                {
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = ThemeUtil.Get("NeoText1", Color.FromArgb(255, 0x23, 0x28, 0x2E)),
                    Text = node == null
                        ? "（无法解析的链接）"
                        : $"{(string.IsNullOrWhiteSpace(node.Name) ? node.Server : node.Name)} · {AppState.ProtocolName(node.Protocol)} · {node.Server}:{node.Port}",
                }, 11.5);
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
        content.Children.Add(Neo.App.Ui.AdaptiveText.WithSize(new TextBlock { Text = "粘贴分享链接（可一次多行）：", }, 12));
        content.Children.Add(input);
        content.Children.Add(status);
        content.Children.Add(new Border
        {
            Height = 1,
            Background = ThemeUtil.Get("NeoHairline2", Color.FromArgb(0x0C, 0x1A, 0x1D, 0x21)),
        });
        content.Children.Add(Neo.App.Ui.AdaptiveText.WithSize(new TextBlock { Text = "已有手动节点：", }, 12));
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
