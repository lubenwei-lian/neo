using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Neo.App.Pages;

public sealed partial class NetworkDiagnosticsPage
{
    private int _reviewWheelEvents;
    internal void ApplyReviewFixture()
    {
        if (!Ui.NativeUiReview.Enabled) return;
        CurrentExitText.Text = "当前出口  日本 · 203.0.113.42";
        SessionText.Text = "离线展示样例";
        var summary = Ui.ConnectivityDisplay.Summarize(Neo.Core.Core.NetworkDiagnosticTargets.All.Select((_, i) => i == 2 ? "站点限制" : "可达").ToArray(), true, false, true);
        DiagnosticSummaryText.Text = summary.Percentage; DetailCountText.Text = $"通过 {summary.Passed}/{summary.Total}"; DetailWhenText.Text = "21:30:00";
        for (var i = 0; i < _rows.Count; i++)
        {
            _rows[i].Status.Text = i == 2 ? "受限 · 340 ms" : "通过 · 210 ms";
            var domestic = Neo.Core.Core.NetworkDiagnosticTargets.All[i].Group == "国内";
            _rows[i].Route.Text = domestic ? "直连" : "代理";
            _rows[i].Evidence.Text = i == 2 ? "HTTP 403 · 站点限制；TLS 握手完成，但访问校验未通过。\n内核规则判定 · proxy"
                : "HTTPS 响应校验通过；离线样例只核对排版和交互。\n" + (domestic ? "内核规则判定 · direct" : "内核规则判定 · proxy");
        }
    }
    internal void PrepareScrollForReview(bool evidence)
    {
        if (!Ui.NativeUiReview.Enabled) return;
        foreach (var row in _rows) row.Evidence.Visibility = evidence ? Visibility.Visible : Visibility.Collapsed;
        DetailsScroller.AddHandler(UIElement.PointerWheelChangedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => _reviewWheelEvents++), true);
        UpdateLayout();
    }
    internal void ScrollForReview(bool bottom)
    {
        if (!Ui.NativeUiReview.Enabled) return;
        DetailsScroller.ChangeView(null, bottom ? DetailsScroller.ScrollableHeight : 0, null, true);
    }
    internal object InspectScrollForReview()
    {
        if (!Ui.NativeUiReview.Enabled) return new { enabled = false };
        var all = new List<string>();
        void Find(DependencyObject parent)
        {
            if (parent is ScrollViewer viewer) all.Add(viewer.Name);
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) Find(VisualTreeHelper.GetChild(parent, i));
        }
        Find(this);
        return new { viewport = DetailsScroller.ViewportHeight, extent = DetailsScroller.ExtentHeight,
            scrollable = DetailsScroller.ScrollableHeight, offset = DetailsScroller.VerticalOffset,
            bodyHeight = DetailsScroller.ActualHeight, headerHeight = DetailsHeader.ActualHeight,
            pageHeight = ActualHeight, wheelEvents = _reviewWheelEvents, scrollers = all };
    }
}
