using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Neo.App.Pages;

public sealed partial class NodesPage
{
    internal NodeTile[] ReviewTiles => _tiles.ToArray();
    internal object? ReviewItemsSource => NodeList.ItemsSource;
    internal object? ReviewFirstContainer => NodeList.ContainerFromIndex(0);
    internal int ReviewColumns => ComputeCols(NodeList.ActualWidth);
    internal int ReviewVisibleCount => _visibleTiles.Count;
    internal object[] ReviewState => _visibleTiles.Select(t => new { name = t.NameText, delay = t.RealValue, selected = t.IsSelected }).Cast<object>().ToArray();
    internal IEnumerable<string> ReviewLayoutErrors()
    {
        var errors = new List<string>();
        foreach (var tile in _tiles)
        {
            if (NodeList.ContainerFromItem(tile) is not GridViewItem container) continue;
            void Walk(DependencyObject parent)
            {
                if (parent is Button { Content: string label } delay && label == tile.DelayText)
                {
                    var scale=Ui.AdaptiveText.Scale;
                    var point=delay.TransformToVisual(container).TransformPoint(new Windows.Foundation.Point());
                    if (Math.Abs(delay.ActualHeight-Ui.ResponsiveViewport.FontSize(26,scale))>.6
                        || Math.Abs(delay.FontSize-Ui.ResponsiveViewport.FontSize(11.5,scale))>.1
                        || point.Y<0 || point.Y+delay.ActualHeight>container.ActualHeight+.5
                        || point.X+delay.ActualWidth>container.ActualWidth+.5)
                        errors.Add("Node latency action size or bounds invalid: "+label);
                }
                if (parent is TextBlock text && (text.Text == tile.NameText || text.Text == tile.MetaText || text.Text == tile.DelayText))
                {
                    if (text.ActualHeight + .5 < text.FontSize * 1.1)
                        errors.Add("Node text clipped: " + text.Text);
                    var p = text.TransformToVisual(container).TransformPoint(new Windows.Foundation.Point());
                    if (p.Y < -1 || p.Y + text.ActualHeight > container.ActualHeight + 1)
                        errors.Add("Node text exceeds container: " + text.Text);
                }
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) Walk(VisualTreeHelper.GetChild(parent, i));
            }
            Walk(container);
        }
        return errors;
    }
    internal void InvokeStatusForReview(Button button)
    {
        if (Ui.NativeUiReview.Enabled) OnStatusFilterClick(button, null!);
    }
    internal void InvokeLatencyForReview(Button button)
    {
        if (Ui.NativeUiReview.Enabled) OnTileTestClick(button, null!);
    }

    internal void SetLatencyStateForReview(string state)
    {
        if(!Ui.NativeUiReview.Enabled)throw new InvalidOperationException();
        Button? Find(DependencyObject parent)
        {
            if(parent is Button {Content:string value} button && value.StartsWith("● "))return button;
            for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
                if(Find(VisualTreeHelper.GetChild(parent,i)) is {} found)return found;
            return null;
        }
        if(NodeList.ContainerFromIndex(0) is {} container && Find(container) is {} target)
            VisualStateManager.GoToState(target,state,true);
        else throw new InvalidOperationException("Latency action is not realized");
    }

    internal async Task<object> ReviewStreamingOrderAsync()
    {
        if (!Ui.NativeUiReview.Enabled) throw new InvalidOperationException();
        var checks = new List<string>();
        void Check(bool ok, string label)
        {
            if (!ok) throw new InvalidOperationException("Streaming latency regression: " + label);
            checks.Add(label);
        }
        var source = NodeList.ItemsSource;
        var selected = Services.AppState.CurrentNode;
        var preferReal = Services.AppState.State.RealDelayTestEnabled;
        var original = _tiles.Select(t => (Tile: t, t.Node.TcpDelay, t.Node.RealDelay, t.Node.RealDelayNote)).ToArray();
        var nodes = original.Select(v => v.Tile).ToArray();
        var notifications = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        void Changed(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => notifications.Add(e.Action);
        _visibleTiles.CollectionChanged += Changed;
        bool Sorted() => _visibleTiles.Select(t => t.RealValue > 0 ? t.RealValue : int.MaxValue)
            .SequenceEqual(_visibleTiles.Select(t => t.RealValue > 0 ? t.RealValue : int.MaxValue).OrderBy(v => v));
        try
        {
            Check(FindName("NodeSortButton") == null && NodeFilterBar.ColumnDefinitions.Count == 3
                && Grid.GetColumn(FilterBtn) == 1, "sort button and empty column removed");
            Services.AppState.State.RealDelayTestEnabled = false;
            foreach (var t in nodes) { t.Node.TcpDelay = 0; t.Node.RealDelay = 0; t.RefreshDelay(); }
            RefreshLatencyOrder();
            ApplyLatencyResult(nodes[^1], 210, tcp: true);
            Check(ReferenceEquals(_visibleTiles[0], nodes[^1]) && nodes.Count(t => t.RealValue == 0) == nodes.Length - 1,
                "first TCP result moves before the rest of the batch finishes");
            ApplyLatencyResult(nodes[^2], 65, tcp: true);
            Check(ReferenceEquals(_visibleTiles[0], nodes[^2]) && Sorted(), "next faster result moves immediately");
            notifications.Clear();
            ApplyLatencyResult(nodes[^2], 410, tcp: true);
            Check(Sorted() && notifications.Count == 1 && notifications[0] == System.Collections.Specialized.NotifyCollectionChangedAction.Move,
                "slower retest moves only its own card once");
            var equalOrder = _visibleTiles.Where(t => t.RealValue == 0).ToArray();
            RefreshLatencyOrder();
            Check(equalOrder.SequenceEqual(_visibleTiles.Where(t => t.RealValue == 0)), "equal and unmeasured delays keep a stable order");
            ApplyLatencyResult(nodes[^3], -1, tcp: true);
            Check(_visibleTiles.Take(2).All(t => t.RealValue > 0) && Sorted(), "failed and unmeasured results follow positive delays");
            ApplyLatencyResult(nodes[^4], 90);
            Check(nodes[^4].RealValue == 90 && Sorted(), "UDP real-delay fallback sorts while TCP remains unmeasured");
            ApplyLatencyResult(nodes[^4], 15, tcp: true);
            ApplyLatencyResult(nodes[^4], 900);
            Check(nodes[^4].RealValue == 15 && ReferenceEquals(_visibleTiles[0], nodes[^4]), "sort follows the displayed TCP value, not a hidden real result");
            Services.AppState.State.RealDelayTestEnabled = true;
            RefreshLatencyOrder();
            ApplyLatencyResult(nodes[^4], 12);
            Check(nodes[^4].RealValue == 12 && ReferenceEquals(_visibleTiles[0], nodes[^4]) && Sorted(), "real-delay preference uses the same live order");
            nodes[^5].Node.RealDelay = 6;
            Services.StartupSpeedTest.NotifyLatencyChanged(nodes[^5].Node);
            Check(ReferenceEquals(_visibleTiles[0], nodes[^5]) && nodes[^5].DelayText.Contains("6 ms"), "individual startup result updates without waiting for a batch notification");
            _onlyUntested = true; ApplyFilter();
            var single = _visibleTiles[^1];
            await RunSingleTestAsync(single);
            Check(!_visibleTiles.Contains(single) && single.RealValue > 0 && Sorted(), "single-node result immediately leaves the untested filter");
            _onlyUntested = false; _onlyAvailable = true; ApplyFilter();
            ApplyLatencyResult(nodes[^3], 28);
            Check(_visibleTiles.Contains(nodes[^3]) && _visibleTiles.All(t => t.RealValue > 0) && Sorted(), "newly available result enters the active filter in order");
            _onlyAvailable = false; SearchBox.Text = nodes[0].Node.Server;
            ApplyLatencyResult(nodes[^1], 1);
            Check(_visibleTiles.Count == 1 && ReferenceEquals(_visibleTiles[0], nodes[0]), "search filter survives unrelated live results");
            SearchBox.Text = "";
            _regionFilter = Neo.Core.Core.NodeGrouper.RegionOf(nodes[0].Node.Name); ApplyFilter();
            ApplyLatencyResult(nodes[^2], 2);
            Check(_visibleTiles.All(t => Neo.Core.Core.NodeGrouper.RegionOf(t.Node.Name) == _regionFilter) && Sorted(), "region filter survives live results");
            _regionFilter = ""; ApplyFilter();
            RebuildList(); nodes = _tiles.ToArray();
            _realTesting = true; RebuildList(); ApplyLatencyResult(nodes[^6], 3);
            Check(_pendingRebuild && Sorted() && nodes[^6].RealValue == 3, "partial real results stay sorted while subscription rebuild is deferred");
            _realTesting = false; RebuildList();
            _tcpTesting = true; RebuildList(); ApplyLatencyResult(nodes[^7], 4, tcp: true);
            Check(_pendingRebuild && nodes.All(t => _tiles.Contains(t)) && Sorted(), "TCP scan also retains its captured tile models during refresh");
            _tcpTesting = false; RebuildList();
            Check(ReferenceEquals(source, NodeList.ItemsSource) && !notifications.Contains(System.Collections.Specialized.NotifyCollectionChangedAction.Reset),
                "all streaming changes retain one source without collection reset");
            Check(ReferenceEquals(selected, Services.AppState.CurrentNode) && _visibleTiles.Count(t => t.IsSelected) == 1
                && ReferenceEquals(_visibleTiles.Single(t => t.IsSelected).Node, selected), "sorting preserves the manually selected node and unique highlight");
            UpdateLayout();
            ScrollViewer? FindScroll(DependencyObject item)
            {
                if (item is ScrollViewer viewer) return viewer;
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++)
                    if (FindScroll(VisualTreeHelper.GetChild(item, i)) is { } child) return child;
                return null;
            }
            var scroll = FindScroll(NodeList) ?? throw new InvalidOperationException("Node scroll viewer missing");
            scroll.ChangeView(null, Math.Min(120, scroll.ScrollableHeight), null, true);
            await Task.Delay(80);
            var offset = scroll.VerticalOffset;
            ApplyLatencyResult(nodes[1], 5); UpdateLayout(); await Task.Delay(80);
            Check(Math.Abs(offset - scroll.VerticalOffset) < 1, $"result movement preserves the current scroll offset ({offset:F1} -> {scroll.VerticalOffset:F1})");
            return new { checks, count = checks.Count, scrollOffset = offset, nodeCount = nodes.Length };
        }
        finally
        {
            _visibleTiles.CollectionChanged -= Changed;
            _realTesting = false; _tcpTesting = false; _onlyAvailable = false; _onlyUntested = false; _regionFilter = ""; SearchBox.Text = "";
            Services.AppState.State.RealDelayTestEnabled = preferReal;
            foreach (var v in original)
            {
                v.Tile.Node.TcpDelay = v.TcpDelay; v.Tile.Node.RealDelay = v.RealDelay; v.Tile.Node.RealDelayNote = v.RealDelayNote;
                Services.DelayCache.Set(v.Tile.Node, v.RealDelay, v.RealDelayNote); Services.DelayCache.SetTcp(v.Tile.Node, v.TcpDelay); v.Tile.RefreshDelay();
            }
            foreach (var tile in _tiles) tile.RefreshDelay();
            RefreshLatencyOrder(); UpdateFooter();
        }
    }
}
