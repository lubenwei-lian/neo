using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Neo.App.Services;
using Neo.Core.Core;
using Windows.UI;

namespace Neo.App.Pages;

public sealed partial class NetworkDiagnosticsPage : UserControl
{
    public event EventHandler? BackRequested;
    private readonly List<(TextBlock Title, TextBlock Url, TextBlock Status, TextBlock Route, TextBlock Evidence)> _rows = new();
    private bool _released;
    public NetworkDiagnosticsPage()
    {
        InitializeComponent();
        foreach (var target in NetworkDiagnosticTargets.All) AddRow(target);
        NetworkDiagnosticsService.Changed += OnChanged;
        CurrentExitService.Changed += OnChanged;
        ConnectionService.StateChanged += Refresh;
        Loaded += (_, _) => Refresh();
        ActualThemeChanged += (_, _) => Refresh();
        Refresh();
    }
    public Task RunAsync() => NetworkDiagnosticsService.RunAsync();
    public void Release()
    {
        _released = true;
        NetworkDiagnosticsService.Changed -= OnChanged;
        CurrentExitService.Changed -= OnChanged;
        ConnectionService.StateChanged -= Refresh;
    }
    private void OnBackClicked(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);
    private void OnCancelClicked(object sender, RoutedEventArgs e) => NetworkDiagnosticsService.Cancel();
    private async void OnRunClicked(object sender, RoutedEventArgs e)
    {
        try { await RunAsync(); }
        catch (Exception ex) { if (!App.IsShuttingDown && !_released) DiagnosticSummaryText.Text = "检测失败：" + ex.GetType().Name; }
    }
    private void OnChanged()
    {
        if (App.IsShuttingDown || _released) return;
        try { DispatcherQueue.TryEnqueue(() => { if (!App.IsShuttingDown && !_released) Refresh(); }); } catch { }
    }
    private Brush Theme(string key) => ThemeUtil.Get(key, Colors.Gray);
    private TextBlock Text(string value, double size, string brush) => Ui.AdaptiveText.WithSize(new TextBlock
    { Text = value, Foreground = Theme(brush), TextWrapping = TextWrapping.Wrap }, size);
    private void AddRow(NetworkDiagnosticTarget target)
    {
        var left = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var title = Text(target.Name, 13, "NeoText1"); title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        var url = Text(target.Url.AbsoluteUri, 11, "NeoText3");
        var status = Text("—", 11, "NeoText2");
        status.SetValue(AutomationProperties.AutomationIdProperty, "SiteStatus" + _rows.Count);
        var route = Text("未确认", 11, "NeoText2");
        route.SetValue(AutomationProperties.AutomationIdProperty, "SiteRoute" + _rows.Count);
        var evidence = Text("开始检测后显示内核证据和结果说明", 11, "NeoText3");
        evidence.SetValue(AutomationProperties.AutomationIdProperty, "SiteEvidence" + _rows.Count);
        evidence.Visibility = Visibility.Collapsed;
        var disclosure = new Button { Content = "校验与路由证据", FontSize = 10.5, MinHeight = 0,
            HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0), CornerRadius = new CornerRadius(10),
            Foreground = Theme("NeoText3"), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0) };
        disclosure.Click += (_, _) =>
        {
            var expanded = evidence.Visibility == Visibility.Collapsed;
            evidence.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            disclosure.Content = expanded ? "收起证据" : "校验与路由证据";
        };
        Ui.AdaptiveText.SetBaseSize(disclosure, 10.5);
        left.Children.Add(title); left.Children.Add(url); left.Children.Add(disclosure);
        var row = new Grid { ColumnSpacing = 16, Padding = new Thickness(0, 8, 0, 8), MinHeight = 67 };
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130 * Ui.AdaptiveText.Scale) });
        row.Children.Add(left);
        var routePill = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 5, 12, 5),
            Background = ThemeUtil.Get("NeoTrack", Colors.Transparent), Child = route, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(routePill, 1); row.Children.Add(routePill);
        status.HorizontalAlignment = HorizontalAlignment.Right; status.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(status, 2); row.Children.Add(status);
        Grid.SetRow(evidence, 1); Grid.SetColumnSpan(evidence, 3); row.Children.Add(evidence);
        DiagnosticRows.Children.Add(new Border { BorderBrush = ThemeUtil.Get("NeoRim", Colors.Transparent), BorderThickness = new Thickness(0, 1, 0, 0), Child = row });
        _rows.Add((title, url, status, route, evidence));
    }
    public void Refresh()
    {
        if (_released || App.IsShuttingDown) return;
        var snapshot = NetworkDiagnosticsService.Current;
        var exit = CurrentExitService.Current;
        var exitValid = ConnectionService.IsConnected && exit.Epoch == ConnectionService.SessionEpoch;
        CurrentExitText.Text = "当前出口  " + (!ConnectionService.IsConnected ? "未连接" : !exitValid ? "待检测" : exit.IsRunning ? "检测中…"
            : exit.Result.Confirmed ? Ui.CurrentExitDisplay.Region(exit.Result.Country) + " · " + exit.Result.Ip : "未确认");
        ToolTipService.SetToolTip(CurrentExitText, exitValid ? exit.Result.Detail + "\n" + exit.Result.Source : null);
        var valid = snapshot.Epoch == ConnectionService.SessionEpoch && ConnectionService.IsConnected;
        SessionText.Text = valid ? ConnectionService.IsTunSession ? "当前连接 · TUN" : "当前连接" : "连接后检测";
        ToolTipService.SetToolTip(SessionText, snapshot.Session);
        var summary = Ui.ConnectivityDisplay.Summarize(snapshot.Results.Select(r => r.State).ToArray(), valid, snapshot.IsRunning, snapshot.CompletedAt.HasValue);
        DiagnosticSummaryText.Text = summary.Percentage;
        DiagnosticSummaryText.Foreground = ThemeUtil.Get(summary.Complete && summary.Passed == summary.Total ? "NeoGreen" : "NeoText3", Colors.Gray);
        DetailCountText.Text = !valid ? "尚未检测" : snapshot.IsRunning ? $"检测中 {summary.Finished}/{summary.Total}"
            : summary.Complete ? $"通过 {summary.Passed}/{summary.Total}" : "检测未完成";
        DetailWhenText.Text = valid && snapshot.CompletedAt is { } at ? at.ToLocalTime().ToString("HH:mm:ss") : "";
        ToolTipService.SetToolTip(DiagnosticSummaryText, snapshot.Summary);
        RunDiagnosticsButton.IsEnabled = ConnectionService.IsConnected && !snapshot.IsRunning;
        CancelCheckButton.IsEnabled = snapshot.IsRunning;
        CancelCheckButton.Visibility = snapshot.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < _rows.Count; i++)
        {
            var result = snapshot.Results[i]; var row = _rows[i];
            row.Title.Foreground = Theme("NeoText1"); row.Url.Foreground = Theme("NeoText2");
            row.Route.Foreground = Theme("NeoText1"); row.Evidence.Foreground = Theme("NeoText3");
            if (!valid)
            {
                row.Status.Text = "未检测"; row.Status.Foreground = Theme("NeoText2");
                row.Route.Text = "出口：未确认"; row.Evidence.Text = "连接后检测当前会话，旧会话结果不沿用";
                continue;
            }
            row.Status.Text = Ui.ConnectivityDisplay.Site(result.State)
                + (result.ElapsedMs > 0 ? $" · {result.ElapsedMs} ms" : "") + (result.RecoveredAfterRetry ? " · 波动" : "");
            ToolTipService.SetToolTip(row.Status, result.State + (result.HttpStatus is { } status ? $" · HTTP {status}" : "") + " · " + result.Detail);
            row.Status.Foreground = result.RecoveredAfterRetry ? new SolidColorBrush(Color.FromArgb(255, 0xD9, 0x77, 0x06))
                : result.State == "可达" ? new SolidColorBrush(Color.FromArgb(255, 0x16, 0xA3, 0x4A))
                : result.State is "失败" or "检测异常" ? new SolidColorBrush(Color.FromArgb(255, 0xDC, 0x26, 0x26)) : Theme("NeoText2");
            row.Route.Text = result.Route.Path;
            ToolTipService.SetToolTip(row.Route, "出口：" + result.Route.Path + " · " + result.Route.Outbound + " · " + result.Route.Source);
            var routeKey = result.Route.Path.Contains("直连") ? "NeoGreen" : result.Route.Path.Contains("代理") ? "NeoAccent" : "NeoText3";
            row.Route.Foreground = ThemeUtil.Get(routeKey, Colors.Gray);
            if (row.Route.Parent is Border pill) pill.Background = ThemeUtil.Get(routeKey + "Soft", Colors.Transparent);
            row.Evidence.Text = result.Detail + "\n" + result.Route.Detail;
        }
    }
}
