using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Neo.App.Pages;

public sealed partial class ConnectionPage
{
    internal void SetFloatSwitchForReview(bool enabled)
    {
        if(!Ui.NativeUiReview.Enabled)throw new InvalidOperationException();
        FloatBallSwitch.IsOn=enabled;
    }
    internal IEnumerable<string> ReviewLayoutErrors()
    {
        var errors = new List<string>();
        void Inside(FrameworkElement parent, FrameworkElement child)
        {
            if (child.Visibility != Visibility.Visible || child.ActualWidth <= 0 || child.ActualHeight <= 0) return;
            var p = child.TransformToVisual(parent).TransformPoint(new Windows.Foundation.Point());
            if (p.X < -1.1 || p.Y < -1.1 || p.X + child.ActualWidth > parent.ActualWidth + 1.1 || p.Y + child.ActualHeight > parent.ActualHeight + 1.1)
                errors.Add(child.Name + " exceeds " + parent.Name);
        }
        Inside(ConnectionHeroLayout, RingHit); Inside(HeroCard, ConnectionHeroLayout);
        var gaps = ReviewHeroGaps;
        if (gaps.Min() < -.6 || gaps.Max() - gaps.Min() > 1.1) errors.Add("Connection hero information spacing is uneven or overlaps");
        Inside(CurrentNodeCard, CurrentNodeLayout); Inside(CurrentNodeCard, NodePickerBtn);
        Inside(CurrentNodeCard, SelNodeName); Inside(CurrentNodeCard, NodeMetaHost);
        Inside(CurrentNodeCard, FloatBallRow); Inside(FloatBallRow, FloatBallSwitch);
        var footer=FloatBallRow.TransformToVisual(HomeGrid).TransformPoint(new());
        var connectButton=RingHit.TransformToVisual(HomeGrid).TransformPoint(new());
        if(Math.Abs(footer.Y+FloatBallRow.ActualHeight/2-connectButton.Y-RingHit.ActualHeight/2)>1.1)
            errors.Add("Floating switch row does not align with the connection button");
        var picker=NodePickerBtn.TransformToVisual(CurrentNodeShell).TransformPoint(new());
        var switchRow=FloatBallRow.TransformToVisual(CurrentNodeShell).TransformPoint(new());
        if(picker.Y+NodePickerBtn.ActualHeight>switchRow.Y+.5)
            errors.Add("Node picker intercepts the floating switch row");
        foreach (var row in HomeSiteGrid.Children.OfType<Microsoft.UI.Xaml.Controls.Grid>())
        {
            Inside(HomeSiteGrid, row);
            foreach (var text in row.Children.OfType<Microsoft.UI.Xaml.Controls.TextBlock>())
            {
                Inside(row, text);
                if (text.IsTextTrimmed) errors.Add("Network detection text trimmed: " + text.Text);
            }
        }
        if (_homeSiteResults.Count != Neo.Core.Core.NetworkDiagnosticTargets.All.Count) errors.Add("Home card does not show every detection target");
        if (HomeSiteViewport.Parent is Microsoft.UI.Xaml.Controls.Grid healthLayout)
        {
            Inside(healthLayout, HomeSiteViewport);
            var rows = HomeSiteGrid.Children.OfType<FrameworkElement>().ToArray();
            if (rows.Max(row => row.ActualHeight) > 25)
                errors.Add("Network detection rows are stretched beyond compact line height");
            if (ActualHeight >= 680 && ActualWidth >= 1200 && HomeSiteViewport.ScrollableHeight > 1.1)
                errors.Add("Network detection targets require scrolling in a large desktop window");
            var unusedHeight = HomeSiteViewport.ActualHeight - HomeSiteGrid.ActualHeight;
            if (ActualHeight >= 680 && unusedHeight > 30)
                errors.Add("Network detection list leaves excess unused space");
        }
        if (HomeGrid.Parent is FrameworkElement homeHost && Math.Abs(HomeGrid.ActualWidth - homeHost.ActualWidth) > 1.1)
            errors.Add("Connection dashboard leaves extra horizontal gutters inside its host");
        return errors;
    }
    internal object ReviewSiteLayout
    {
        get
        {
            var rows = HomeSiteGrid.Children.OfType<FrameworkElement>().ToArray();
            var parent = (Microsoft.UI.Xaml.Controls.Grid)HomeSiteViewport.Parent;
            var lastTextBottom = _homeSiteResults.Max(text => text.TransformToVisual(parent).TransformPoint(new()).Y + text.ActualHeight);
            return new
            {
                columns = HomeSiteGrid.ColumnDefinitions.Count, rows = HomeSiteGrid.RowDefinitions.Count,
                availableHeight = HomeSiteViewport.ActualHeight, listHeight = HomeSiteGrid.ActualHeight,
                scrollableHeight = HomeSiteViewport.ScrollableHeight, dashboardWidth = HomeGrid.ActualWidth,
                hostWidth = ((FrameworkElement)HomeGrid.Parent).ActualWidth,
                firstRowTop = rows.Min(row => row.TransformToVisual(HomeSiteGrid).TransformPoint(new()).Y),
                lastRowBottom = rows.Max(row => row.TransformToVisual(HomeSiteGrid).TransformPoint(new()).Y + row.ActualHeight),
                footerGap = HealthActionHost.TransformToVisual(parent).TransformPoint(new()).Y - lastTextBottom,
                rowHeights = rows.Select(row => row.ActualHeight).ToArray()
            };
        }
    }
    internal double[] ReviewHeroGaps
    {
        get
        {
            FrameworkElement[] rows = [ConnectionStatusLine, ConnectionTimeHost, ExitRegionRow, RingHit];
            return rows.Zip(rows.Skip(1), (before, after) => after.TransformToVisual(ConnectionHeroLayout).TransformPoint(new()).Y
                - before.TransformToVisual(ConnectionHeroLayout).TransformPoint(new()).Y - before.ActualHeight).ToArray();
        }
    }
    internal void PlaySuccessForReview() { if (Ui.NativeUiReview.Enabled) PlayConnectionSplash(); }
    internal void ReviewNodePickerHover() { if (Ui.NativeUiReview.Enabled) VisualStateManager.GoToState(NodePickerBtn, "PointerOver", false); }
    internal void OpenDetailsForReview() { if (Ui.NativeUiReview.Enabled) OnNetworkDetailsClicked(this, null!); }
    internal void OpenNodePickerForReview() { if (Ui.NativeUiReview.Enabled) OnNodePickerClick(NodePickerBtn, null!); }
    // Display fixtures are reachable only in the explicitly isolated review entry.
    // They never change ConnectionService, its session, or the system proxy.
    internal void ApplyReviewFixture()
    {
        if (!Ui.NativeUiReview.Enabled) return;
        _statsTimer.Stop();
        RingStateText.Text = "已连接";
        RingStateText.Foreground = ThemeUtil.Get("NeoText1", Microsoft.UI.Colors.White);
        ConnectionDot.Fill = ThemeUtil.Get("NeoGreen", Microsoft.UI.Colors.Green);
        RingTimeText.Text = "00:12:48"; ExitRegionText.Text = "日本 · 203.0.113.42";
        RingHit.Content = "断开连接";
        RingHit.IsEnabled = true;
        RingHit.Background = ThemeUtil.Get("NeoAccentSoft", Microsoft.UI.Colors.Transparent);
        RingHit.BorderBrush = ThemeUtil.Get("NeoAccentLine", Microsoft.UI.Colors.Gray);
        RingHit.Foreground = ThemeUtil.Get("NeoAccent", Microsoft.UI.Colors.DodgerBlue);
        UpSpeedText.Text = "224"; UpSpeedUnit.Text = "KB/s";
        DownSpeedText.Text = "2.23"; DownSpeedUnit.Text = "MB/s";
        PeakText.Text = "4.2"; PeakUnit.Text = "MB/s";
        CoreStateText.Text = "内核"; CoreTagText.Text = "xray";
        CoreKindText.Text = "xray"; CoreVersionText.Text = "26.10.21";
        KernelHint.Text = "xray 支持 VLESS · TCP";
        StatsCoreMemText.Text = "42.6"; CoreStrategyText.Text = "单节点"; CoreNodesText.Text = "1 / 36";
        TotalUpText.Text = "12.4"; TotalUpUnit.Text = "MB";
        TotalDownText.Text = "172.2"; TotalDownUnit.Text = "MB"; ConnCountText.Text = "3";
        var summary = Ui.ConnectivityDisplay.Summarize(Neo.Core.Core.NetworkDiagnosticTargets.All.Select((_, i) => i == 2 ? "站点限制" : "可达").ToArray(), true, false, true);
        NetworkHealthText.Text = summary.Percentage; NetworkHealthText.Foreground = ThemeUtil.Get("NeoText2", Microsoft.UI.Colors.Gray);
        NetworkHealthDetailText.Text = $"通过 {summary.Passed}/{summary.Total}"; NetworkCheckButton.IsEnabled = true;
        for (var i = 0; i < _homeSiteResults.Count; i++)
        {
            _homeSiteResults[i].Text = Ui.ConnectivityDisplay.Site(i == 2 ? "站点限制" : "可达");
            _homeSiteResults[i].Foreground = i == 2 ? ThemeUtil.Get("NeoYellow", Microsoft.UI.Colors.Orange) : ConnectionDot.Fill;
        }
        _dnHist.Clear(); _upHist.Clear(); _times.Clear();
        double[] down = [2.8,3,3.3,3.2,3.6,4.2,4,3.4,3,2.7,3.2,3.8,3.4,2.9,2.6,2.8,3.1,2.9,2.5,2.8,2.65,2.4,2.32,2.23];
        double[] up = [.18,.19,.21,.2,.24,.25,.24,.21,.19,.18,.2,.23,.22,.19,.18,.2,.22,.21,.19,.18,.2,.22,.23,.219];
        for (var i = 0; i < down.Length; i++)
        {
            _dnHist.Add(down[i] * 1024 * 1024); _upHist.Add(up[i] * 1024 * 1024);
            _times.Add(DateTime.Now.AddSeconds(i - down.Length + 1));
        }
        RedrawChart();
    }
}
