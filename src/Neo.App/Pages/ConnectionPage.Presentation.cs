using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Neo.App.Services;
using Neo.Core.Core;
using Windows.UI;

namespace Neo.App.Pages;

public sealed partial class ConnectionPage
{
    private readonly List<TextBlock> _homeSiteResults = new();
    private LogEntry? _lastHomeLog;
    private int _lastSuccessEpoch;
    private Storyboard? _successSplash;
    private (FrameworkElement Element, int Row, int Column)[]? _coreFieldPositions;
    private bool? _coreWideLayout;

    private void ConfigureCoreRows(bool wide)
    {
        if (_coreWideLayout == wide) return;
        _coreFieldPositions ??= CoreRows.Children.OfType<FrameworkElement>().Select(e => (e, Grid.GetRow(e), Grid.GetColumn(e))).ToArray();
        _coreWideLayout = wide;
        CoreRows.RowDefinitions.Clear(); CoreRows.ColumnDefinitions.Clear();
        CoreRows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        CoreRows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (wide)
        {
            CoreRows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            CoreRows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            CoreRows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        for (var i = 0; i < (wide ? 4 : 7); i++) CoreRows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        foreach (var item in _coreFieldPositions)
        {
            Grid.SetRow(item.Element, wide ? item.Row / 2 : item.Row);
            Grid.SetColumn(item.Element, wide ? item.Row % 2 * 3 + item.Column : item.Column);
        }
        CoreRows.RowSpacing = wide ? 6 : 1;
        CoreRows.VerticalAlignment = VerticalAlignment.Top;
    }

    private void BuildHomeSiteRows()
    {
        for (var i = 0; i < NetworkDiagnosticTargets.All.Count; i++)
        {
            var target = NetworkDiagnosticTargets.All[i];
            var row = new Grid { ColumnSpacing = 4, MinHeight = 20 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = Neo.App.Ui.AdaptiveText.WithSize(new TextBlock { Text = target.Name, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = GetThemeBrush("NeoText2", Microsoft.UI.Colors.Gray), VerticalAlignment = VerticalAlignment.Center }, 11);
            var status = Neo.App.Ui.AdaptiveText.WithSize(new TextBlock { Text = "—", VerticalAlignment = VerticalAlignment.Center }, 11);
            Ui.AdaptiveText.SetBaseSize(name, 11);
            Ui.AdaptiveText.SetBaseSize(status, 11);
            row.Children.Add(name); row.Children.Add(status); Grid.SetColumn(status, 1);
            Grid.SetRow(row, i / 2); Grid.SetColumn(row, i % 2);
            HomeSiteGrid.Children.Add(row); _homeSiteResults.Add(status);
        }
    }

    private void ConfigureHomeSiteRows(int columns)
    {
        var rows = (HomeSiteGrid.Children.Count + columns - 1) / columns;
        if (HomeSiteGrid.ColumnDefinitions.Count == columns && HomeSiteGrid.RowDefinitions.Count == rows) return;
        HomeSiteGrid.ColumnDefinitions.Clear();
        HomeSiteGrid.RowDefinitions.Clear();
        for (var i = 0; i < columns; i++)
            HomeSiteGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < rows; i++)
            HomeSiteGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < HomeSiteGrid.Children.Count; i++)
        {
            var row = (FrameworkElement)HomeSiteGrid.Children[i];
            Grid.SetRow(row, i / columns);
            Grid.SetColumn(row, i % columns);
        }
    }

    private void OnHomeLogClicked(object sender, RoutedEventArgs e) => OpenLogRequested?.Invoke(this, EventArgs.Empty);

    private void RefreshHomeLog()
    {
        if (!IsPageVisible()) return;
        var last = LogBus.Latest;
        if (ReferenceEquals(last, _lastHomeLog)) return;
        _lastHomeLog = last; HomeLogRows.Children.Clear();
        foreach (var entry in LogBus.TailSnapshot(7))
        {
            var row = new Grid { ColumnSpacing = 8, MinHeight = 20 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(Ui.AdaptiveText.WithSize(new TextBlock { Text = entry.Time.ToString("HH:mm:ss"),
                Foreground = GetThemeBrush("NeoText3", Microsoft.UI.Colors.Gray), VerticalAlignment = VerticalAlignment.Center }, 11));
            var key = entry.Level switch { "Error" => "NeoRed", "Warning" => "NeoYellow", "Debug" => "NeoText2", _ => "NeoGreen" };
            var name = entry.Level switch { "Error" => "error", "Warning" => "warn", "Debug" => "debug", _ => "info" };
            var level = Ui.AdaptiveText.WithSize(new TextBlock { Text = name, Foreground = GetThemeBrush(key, Microsoft.UI.Colors.Gray) }, 9);
            var badge = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(5, 1, 5, 1),
                Background = GetThemeBrush(key + "Soft", Microsoft.UI.Colors.Transparent), Child = level, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(badge, 1); row.Children.Add(badge);
            var text = Ui.AdaptiveText.WithSize(new TextBlock { Text = entry.Text, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = GetThemeBrush("NeoText2", Microsoft.UI.Colors.Gray), VerticalAlignment = VerticalAlignment.Center }, 12);
            ToolTipService.SetToolTip(text, entry.Display); Grid.SetColumn(text, 2); row.Children.Add(text);
            HomeLogRows.Children.Add(row);
        }
    }

    private void StopConnectionSplash()
    {
        _successSplash?.Stop(); _successSplash = null; ConnectionSplashLayer.Children.Clear();
    }

    // js/connection-motion.js: one finite halo/ring/three-colour burst, 520ms.
    private void PlayConnectionSplash()
    {
        StopConnectionSplash();
        if (!Ui.PrototypeMotion.Enabled || !IsPageVisible() || RingHit.ActualWidth <= 0) return;
        var center = RingHit.TransformToVisual(ConnectionSplashLayer).TransformPoint(new Windows.Foundation.Point(RingHit.ActualWidth / 2, RingHit.ActualHeight / 2));
        var radius = Math.Min(96, Math.Max(50, RingHit.ActualWidth * .5 + 28));
        var board = new Storyboard();
        var cubic = new KeySpline { ControlPoint1 = new Windows.Foundation.Point(1d / 3, 1), ControlPoint2 = new Windows.Foundation.Point(2d / 3, 1) };
        var quadratic = new KeySpline { ControlPoint1 = new Windows.Foundation.Point(1d / 3, 2d / 3), ControlPoint2 = new Windows.Foundation.Point(2d / 3, 1) };
        Ellipse Circle(double r, string colour, bool outline)
        {
            var brush = new SolidColorBrush(colour == "cyan" ? Color.FromArgb(255, 0x74, 0xD9, 0xE8) : Color.FromArgb(255, 0x5A, 0xA9, 0xFF));
            var shape = new Ellipse { Width = r * 2, Height = r * 2, Fill = outline ? null : brush,
                Stroke = outline ? brush : null, StrokeThickness = outline ? 1.5 : 0, IsHitTestVisible = false };
            Canvas.SetLeft(shape, center.X - r); Canvas.SetTop(shape, center.Y - r); ConnectionSplashLayer.Children.Add(shape);
            return shape;
        }
        void Expand(Ellipse shape, double from, double to, double opacity)
        {
            Ui.PrototypeMotion.Add(board, shape, "Width", from * 2, to * 2, 520, cubic);
            Ui.PrototypeMotion.Add(board, shape, "Height", from * 2, to * 2, 520, cubic);
            Ui.PrototypeMotion.Add(board, shape, "(Canvas.Left)", center.X - from, center.X - to, 520, cubic);
            Ui.PrototypeMotion.Add(board, shape, "(Canvas.Top)", center.Y - from, center.Y - to, 520, cubic);
            Ui.PrototypeMotion.Add(board, shape, "Opacity", opacity, 0, 520, quadratic);
        }
        Expand(Circle(8, "blue", false), 8, 8 + radius * .7, .28);
        Expand(Circle(5, "cyan", true), 5, 5 + radius * .9, .6);
        var colours = new[] { Color.FromArgb(255, 0x5A, 0xA9, 0xFF), Color.FromArgb(255, 0x74, 0xD9, 0xE8), Color.FromArgb(255, 0xA7, 0xB8, 0xFF) };
        for (var i = 0; i < 12; i++)
        {
            var size = 3 + i % 3 * .75; var angle = i * Math.PI / 6 - .24;
            var reach = .72 + i % 4 * .08;
            var transform = new CompositeTransform { CenterX = size, CenterY = size };
            var particle = new Ellipse { Width = size * 2, Height = size * 2, Fill = new SolidColorBrush(colours[i % 3]), RenderTransform = transform };
            Canvas.SetLeft(particle, center.X - size); Canvas.SetTop(particle, center.Y - size); ConnectionSplashLayer.Children.Add(particle);
            Ui.PrototypeMotion.Add(board, transform, "TranslateX", Math.Cos(angle) * 12, Math.Cos(angle) * (12 + radius * reach), 520, cubic);
            Ui.PrototypeMotion.Add(board, transform, "TranslateY", Math.Sin(angle) * 12, Math.Sin(angle) * (12 + radius * reach), 520, cubic);
            Ui.PrototypeMotion.Add(board, transform, "ScaleX", 1, .3, 520, cubic);
            Ui.PrototypeMotion.Add(board, transform, "ScaleY", 1, .3, 520, cubic);
            Ui.PrototypeMotion.Add(board, particle, "Opacity", .85, 0, 520,
                new KeySpline { ControlPoint1 = new Windows.Foundation.Point(1d / 3, 0), ControlPoint2 = new Windows.Foundation.Point(2d / 3, 1) }, .55);
        }
        _successSplash = board;
        board.Completed += (_, _) => { if (ReferenceEquals(_successSplash, board)) StopConnectionSplash(); };
        board.Begin();
    }
}
