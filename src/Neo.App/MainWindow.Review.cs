using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Text.Json;
using Windows.Graphics;

namespace Neo.App;

public sealed partial class MainWindow
{
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _reviewTimer;
    private string? _reviewLastCommand;
    private bool _reviewBusy;
    private int _floatMainRequests;

    private void InitializeReviewWindow()
    {
        Services.FloatingTrafficService.Initialize(DispatcherQueue, () => { _floatMainRequests++; ShowMainWindow(); });
        _dark = true;
        Root.RequestedTheme = ElementTheme.Dark;
        Title = "neo · 离线 UI 验收";
        ExtendsContentIntoTitleBar = true; SetTitleBar(TitleBarRoot);
        Ui.NativeUiReview.MakeNonActivating(this);
        AppWindow.ResizeClient(new SizeInt32(1101, 883));
        AppWindow.Move(new PointInt32(20, 20));
        App.MainWindowInstanceForReview(this);
        if (TitleBarRoot.Children.OfType<StackPanel>().FirstOrDefault(panel => Grid.GetColumn(panel) == 0) is { } heading)
            heading.Children.Add(new TextBlock { Text = "离线对照 · 模拟数据", FontSize = 10,
                Foreground = ThemeUtil.Get("NeoText3", Microsoft.UI.Colors.Gray), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        Root.Loaded += (_, _) => { _rootLoaded = true; NavigateTo("conn"); };
        AppWindow.Changed += (_, _) => SchedulePresentationVisibilitySync();
        Activated += (_, _) => SchedulePresentationVisibilitySync();
        Closed += (_, _) => { Services.FloatingTrafficService.Shutdown(); App.IsShuttingDown = true; _reviewTimer?.Stop(); _pageConn?.StopTimers(); Application.Current.Exit(); };
        _reviewTimer = DispatcherQueue.CreateTimer();
        _reviewTimer.Interval = TimeSpan.FromMilliseconds(150);
        _reviewTimer.Tick += async (_, _) => await ReviewCommandAsync();
        _reviewTimer.Start();
        // No tray, guard, proxy, updater, relay, auto-connect, or instance takeover.
        AppWindow.Show(false); Ui.NativeUiReview.KeepBehind(this);
        File.WriteAllText(Path.Combine(Ui.NativeUiReview.DirectoryPath, "ready.json"), JsonSerializer.Serialize(new { pid = Environment.ProcessId, home = Ui.NativeUiReview.DirectoryPath }));
    }

    private async Task ReviewCommandAsync()
    {
        var file = Path.Combine(Ui.NativeUiReview.DirectoryPath, "command.json");
        if (_reviewBusy || !File.Exists(file)) return;
        string json;
        try { json = File.ReadAllText(file); } catch { return; }
        if (json == _reviewLastCommand) return;
        _reviewLastCommand = json; _reviewBusy = true;
        try
        {
            using var document = JsonDocument.Parse(json); var cmd = document.RootElement;
            var action = cmd.GetProperty("action").GetString();
            if (cmd.TryGetProperty("compact", out var compact) && Services.AppState.State.CompactLayout != compact.GetBoolean())
            {
                Services.AppState.State.CompactLayout = compact.GetBoolean();
                Services.AppState.NotifyUiSettingsChanged();
            }
            if (cmd.TryGetProperty("width", out var w) && cmd.TryGetProperty("height", out var h))
            {
                AppWindow.ResizeClient(new SizeInt32(w.GetInt32(), h.GetInt32()));
                await Task.Delay(80);
                var scale = Root.XamlRoot.RasterizationScale;
                AppWindow.Resize(new SizeInt32(AppWindow.Size.Width + (int)Math.Round((w.GetInt32() - Root.ActualWidth) * scale),
                    AppWindow.Size.Height + (int)Math.Round((h.GetInt32() - Root.ActualHeight) * scale)));
            }
            object? performance = null;
            switch (action)
            {
                case "floatball-check": performance=await ReviewFloatBallAsync(); break;
                case "floatball-optimized": performance=await ReviewFloatBallOptimizedAsync(); break;
                case "floatball-click-check": performance=await ReviewFloatBallClicksAsync(); break;
                case "floatball-motion": performance=await ReviewFloatBallMotionAsync(cmd.GetProperty("id").GetString()!); break;
                case "floatball":
                    NavigateTo("conn");
                    Services.FloatingTrafficService.SetEnabled(cmd.GetProperty("on").GetBoolean());
                    if (Services.FloatingTrafficService.WindowForReview is { } floating)
                    {
                        floating.SetReviewData(new Ui.FloatingTrafficData(true,false,"香港 01 · IPLC",2.34*1024*1024,(long)(12.4*1024*1024),(long)(172.2*1024*1024),154L*1024*1024*1024,465L*1024*1024*1024));
                        floating.MoveForReview(800,400);
                        floating.OpenForReview(cmd.TryGetProperty("open",out var openBall) && openBall.GetBoolean());
                        await Task.Delay(550);
                        floating.CaptureForReview(Ui.NativeUiReview.DirectoryPath,cmd.GetProperty("id").GetString()!);
                        performance=floating.Inspect();
                    }
                    break;
                case "page": NavigateTo(cmd.GetProperty("page").GetString()!); break;
                case "latency-state": NavigateTo("nodes"); Root.UpdateLayout(); EnsureNodesPage().SetLatencyStateForReview(cmd.GetProperty("group").GetString()!); break;
                case "theme": Root.RequestedTheme = cmd.GetProperty("group").GetString() == "light" ? ElementTheme.Light : ElementTheme.Dark; break;
                case "benchmark-nodes": performance = await BenchmarkNodesForReviewAsync(); break;
                case "streaming-nodes": NavigateTo("nodes"); Root.UpdateLayout(); await Task.Delay(100); performance = await EnsureNodesPage().ReviewStreamingOrderAsync(); break;
                case "restore-nodes": performance = await ReviewNodeRestoreAsync(); break;
                case "subscription-buttons": NavigateTo("subs"); Root.UpdateLayout(); await Task.Delay(100); performance = EnsureSubsPage().ReviewSubscriptionButtons(); break;
                case "subs-hover": NavigateTo("subs"); EnsureSubsPage().SetTrendHover(cmd.TryGetProperty("index", out var index) ? index.GetInt32() : 29); break;
                case "settings-toggle": SetSettingsExpanded(!_settingsExpanded); if (_settingsExpanded) NavigateTo("settings"); break;
                case "settings-group":
                    SetSettingsExpanded(true, false); NavigateTo("settings"); EnsureSettingsPage().ShowGroup(cmd.GetProperty("group").GetString()!); break;
                case "settings-language-hover":
                    SetSettingsExpanded(true, false); NavigateTo("settings"); EnsureSettingsPage().ShowGroup("general"); EnsureSettingsPage().ReviewLanguageHover(); break;
                case "settings-pill-state":
                    SetSettingsExpanded(true, false); NavigateTo("settings");
                    var stateGroup = cmd.GetProperty("group").GetString()!;
                    var statePill = SettingsRevealContent.Children.OfType<Button>().First(b => b.Tag as string == stateGroup);
                    if (_pageSettings?.OpenGroup != stateGroup) OnSettingsGroupClick(statePill, null!);
                    VisualStateManager.GoToState(statePill, cmd.TryGetProperty("state", out var pillState) ? pillState.GetString()! : "PointerOver", false);
                    break;
                case "node-hover": NavigateTo("conn"); EnsureConnPage().ReviewNodePickerHover(); break;
                case "node-menu": NavigateTo("conn"); Root.UpdateLayout(); await Task.Delay(250); EnsureConnPage().OpenNodePickerForReview(); break;
                case "interval-menu": NavigateTo("subs"); Root.UpdateLayout(); await Task.Delay(250); EnsureSubsPage().OpenIntervalForReview(); break;
                case "close-menus":
                    foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot)) popup.IsOpen = false;
                    break;
                case "switch":
                case "switch-motion":
                case "switch-stress":
                    NavigateTo(cmd.GetProperty("page").GetString()!);
                    if (_currentPage == "settings") EnsureSettingsPage().ShowGroup(cmd.GetProperty("group").GetString()!);
                    Root.UpdateLayout(); await Task.Delay(500);
                    ToggleSwitch? FindSwitch(DependencyObject parent, string name)
                    {
                        if (parent is ToggleSwitch candidate && candidate.Name == name) return candidate;
                        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                            if (FindSwitch(VisualTreeHelper.GetChild(parent, i), name) is { } found) return found;
                        return null;
                    }
                    var testSwitch = FindSwitch(Root, cmd.GetProperty("target").GetString()!) ?? throw new InvalidOperationException("Switch not found");
                    var on = cmd.GetProperty("on").GetBoolean();
                    if (action == "switch-stress")
                    {
                        for (var i = 0; i < 24; i++) { testSwitch.IsOn = !testSwitch.IsOn; await Task.Delay(18); }
                        testSwitch.IsOn = on;
                    }
                    else if (action == "switch-motion")
                    {
                        testSwitch.IsOn = !on; await Task.Delay(400);
                        Ui.SwitchMotion.PressForReview(testSwitch, true); await Task.Delay(110);
                        await Ui.NativeUiReview.CaptureWindowAsync(this, cmd.GetProperty("id").GetString()! + "-pressed");
                        Ui.SwitchMotion.PressForReview(testSwitch, false); testSwitch.IsOn = on;
                        var clock = System.Diagnostics.Stopwatch.StartNew();
                        foreach (var at in new[] { 0, 45, 100, 180, 260, 400 })
                        {
                            var wait = at - clock.Elapsed.TotalMilliseconds;
                            if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait));
                            await Ui.NativeUiReview.CaptureWindowAsync(this, cmd.GetProperty("id").GetString()! + "-" + at.ToString("000"));
                        }
                    }
                    else testSwitch.IsOn = on;
                    break;
                case "native-menu":
                case "combo-menu":
                    NavigateTo(cmd.GetProperty("page").GetString()!); Root.UpdateLayout(); await Task.Delay(300);
                    FrameworkElement? FindMenu(DependencyObject parent)
                    {
                        if (parent is FrameworkElement hidden && hidden.Visibility != Visibility.Visible) return null;
                        if (action == "native-menu" && parent is Button b && b.Flyout is MenuFlyout && b.ActualWidth > 0) return b;
                        if (action == "combo-menu" && parent is ComboBox box && box.ActualWidth > 0) return box;
                        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                            if (FindMenu(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
                        return null;
                    }
                    var menuTarget = FindMenu(Root) ?? throw new InvalidOperationException("Menu not found");
                    if (menuTarget is Button menuButton) menuButton.Flyout.ShowAt(menuButton);
                    else if (menuTarget is ComboBox box) box.IsDropDownOpen = true;
                    break;
                case "menu-hover":
                    Control? FindRow(DependencyObject parent)
                    {
                        if (parent is Button or MenuFlyoutItem or ToggleMenuFlyoutItem) return (Control)parent;
                        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                            if (FindRow(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
                        return null;
                    }
                    foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot))
                        if (popup.Child is { } content && FindRow(content) is { } row) VisualStateManager.GoToState(row, "PointerOver", false);
                    break;
                case "details": _ = ShowNetworkDetailsAsync(); break;
                case "details-expand":
                    if (_networkDetailsDialog?.Content is Pages.NetworkDiagnosticsPage expandDetails) expandDetails.PrepareScrollForReview(true);
                    break;
                case "details-scroll":
                    if (_networkDetailsDialog?.Content is Pages.NetworkDiagnosticsPage scrollDetails) scrollDetails.ScrollForReview(cmd.GetProperty("on").GetBoolean());
                    break;
                case "settings-open": OnNavClick(NavSettings, null!); break;
                case "settings-close": _pageSettings?.HideDetails(); break;
                case "success": _pageConn?.PlaySuccessForReview(); break;
                case "motion": await RecordMotionForReviewAsync(cmd.GetProperty("group").GetString()!, cmd.GetProperty("id").GetString()!); break;
                case "stress-motion": performance = await StressMotionForReviewAsync(); break;
                case "close-details": _networkDetailsDialog?.Hide(); break;
                case "invoke":
                    Button? Find(DependencyObject item, string name, int nodeIndex)
                    {
                        if (item is Button candidate && (candidate.Name == name || name == "node-latency" && candidate.DataContext is Pages.NodeTile tile && ReferenceEquals(tile.Node, Services.AppState.Nodes[nodeIndex]) && candidate.Content is string text && (text.Contains("ms") || text.Contains("未测")))) return candidate;
                        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(item); index++)
                            if (Find(VisualTreeHelper.GetChild(item, index), name, nodeIndex) is { } result) return result;
                        return null;
                    }
                    var targetName = cmd.GetProperty("target").GetString()!;
                    var nodeIndex = cmd.TryGetProperty("nodeIndex", out var indexValue) ? indexValue.GetInt32() : 0;
                    var button = Find(Root, targetName, nodeIndex) ?? (_networkDetailsDialog is { } currentDialog ? Find(currentDialog, targetName, nodeIndex) : null);
                    if (button == null) throw new InvalidOperationException("Review button not found: " + targetName);
                    // UI Automation Invoke can activate a window even when Show(false) was used.
                    // Exercise the same handlers with a controlled sender without taking focus.
                    if (targetName == "NavSettings") OnNavClick(button, null!);
                    else if (targetName == "NetworkDetailsButton") _pageConn?.OpenDetailsForReview();
                    else if (targetName == "node-latency") _pageNodes?.InvokeLatencyForReview(button);
                    else if (button.Tag is string status && status is "all" or "available" or "timeout" or "untested") _pageNodes?.InvokeStatusForReview(button);
                    else throw new InvalidOperationException("Unsupported isolated action: " + targetName);
                    break;
                case "quit": Close(); return;
            }
            await Task.Delay(action == "settings-open" ? 800 : 450);
            if (cmd.TryGetProperty("fixture", out var fixture) && fixture.GetString() == "connected") _pageConn?.ApplyReviewFixture();
            if (cmd.TryGetProperty("fixture", out fixture) && fixture.GetString() == "connected")
            {
                NavConnStatus.Text = "已连接"; NavConnDot.Fill = ThemeUtil.Get("NeoGreen", Microsoft.UI.Colors.Green);
                if (_networkDetailsDialog?.Content is Pages.NetworkDiagnosticsPage fixtureDetails) fixtureDetails.ApplyReviewFixture();
            }
            var errors = new List<string>();
            if (_currentPage == "conn" && _pageConn != null) errors.AddRange(_pageConn.ReviewLayoutErrors());
            if (_currentPage == "nodes" && _pageNodes != null) errors.AddRange(_pageNodes.ReviewLayoutErrors());
            var elements = new List<object>();
            var typography = new List<object>();
            var controls = new List<object>();
            var switches = new List<object>();
            void Walk(DependencyObject item)
            {
                if (item is FrameworkElement hidden && hidden.Visibility != Visibility.Visible) return;
                if (item is ToggleSwitch toggle && toggle.ActualWidth > 0)
                    switches.Add(new { name = toggle.Name, width = toggle.ActualWidth, height = toggle.ActualHeight, on = toggle.IsOn, enabled = toggle.IsEnabled });
                if (item is Button control && control.ActualWidth > 0 && control.ActualHeight > 0)
                    controls.Add(new { name = control.Name, tag = control.Tag as string, width = control.ActualWidth,
                        height = control.ActualHeight, radius = control.CornerRadius.TopLeft,
                        verticalAlignment = control.VerticalAlignment.ToString(),
                        color = (control.Foreground as SolidColorBrush)?.Color.ToString() });
                if (item is TextBlock fontText && fontText.Visibility == Visibility.Visible && !string.IsNullOrEmpty(fontText.Text))
                    typography.Add(new { name = fontText.Name, text = fontText.Text, size = fontText.FontSize,
                        baseline = double.IsFinite(Ui.AdaptiveText.GetBaseSize(fontText)) ? Ui.AdaptiveText.GetBaseSize(fontText) : (double?)null,
                        width = fontText.ActualWidth, height = fontText.ActualHeight, trimmed = fontText.IsTextTrimmed });
                if (item is FrameworkElement fe && !string.IsNullOrEmpty(fe.Name) && fe.Visibility == Visibility.Visible)
                {
                    var point = fe.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point());
                    elements.Add(new { name = fe.Name, type = fe.GetType().Name, x = point.X, y = point.Y, width = fe.ActualWidth, height = fe.ActualHeight,
                        text = fe is TextBlock text ? text.Text : fe is Button button ? button.Content as string : null });
                }
                if (item is Border nodeCard && nodeCard.DataContext is Pages.NodeTile nodeTile
                    && double.IsFinite(Ui.AdaptiveText.GetBaseHeight(nodeCard)))
                {
                    var point = nodeCard.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point());
                    elements.Add(new { name = "NodeCard", type = "Border", x = point.X, y = point.Y,
                        width = nodeCard.ActualWidth, height = nodeCard.ActualHeight, text = nodeTile.NameText });
                }
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++) Walk(VisualTreeHelper.GetChild(item, i));
            }
            Walk(Root);
            if (_networkDetailsDialog is { } reviewDialog) Walk(reviewDialog);
            var id = cmd.GetProperty("id").GetString()!;
            var popups = new List<object>();
            var popupIndex = 0;
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot))
                if (popup.Child is FrameworkElement content)
                {
                    await Ui.NativeUiReview.CaptureElementAsync(content, id + "-popup-" + popupIndex++);
                    object Describe(DependencyObject item)
                    {
                        var children = new List<object>();
                        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++) children.Add(Describe(VisualTreeHelper.GetChild(item, i)));
                        var element = item as FrameworkElement;
                        var radius = item is Control c ? c.CornerRadius.TopLeft : item is Border b ? b.CornerRadius.TopLeft : item is Grid g ? g.CornerRadius.TopLeft : 0;
                        var brush = item is Control cc ? cc.Background : item is Border bb ? bb.Background : item is Grid gg ? gg.Background : null;
                        return new { type = item.GetType().Name, name = element?.Name, width = element?.ActualWidth, height = element?.ActualHeight,
                            radius, background = (brush as SolidColorBrush)?.Color.ToString(), text = (item as TextBlock)?.Text, children };
                    }
                    popups.Add(Describe(content));
                }
            await Ui.NativeUiReview.CaptureWindowAsync(this, id);
            File.WriteAllText(Path.Combine(Ui.NativeUiReview.DirectoryPath, "response.json"), JsonSerializer.Serialize(new { id, page = _currentPage, width = Root.ActualWidth, height = Root.ActualHeight,
                captureMethod = "PrintWindow/client",
                settingsExpanded = _settingsExpanded, detailsOpen = _networkDetailsDialog != null,
                settingsGroup = _pageSettings?.OpenGroup, settingsMotion = _pageSettings?.DetailMotion, revealMotion = _settingsRevealMotion?.Inspect(),
                detailMotion = (_networkDetailsDialog as Ui.CapsuleDialog)?.ReviewMotion,
                detailsScroll = (_networkDetailsDialog?.Content as Pages.NetworkDiagnosticsPage)?.InspectScrollForReview(),
                selectedNode = Services.AppState.CurrentNode?.Name, scale = Ui.AdaptiveText.Scale,
                heroGaps = _currentPage == "conn" ? _pageConn?.ReviewHeroGaps : null, siteLayout = _currentPage == "conn" ? _pageConn?.ReviewSiteLayout : null,
                nodeColumns = _currentPage == "nodes" ? _pageNodes?.ReviewColumns : null,
                nodeState = _currentPage == "nodes" ? _pageNodes?.ReviewState : null, elements, typography, controls, switches, popups, errors, performance }));
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(Ui.NativeUiReview.DirectoryPath, "command-error.log"), ex.ToString()); } catch { }
        }
        finally { _reviewBusy = false; }
    }

    private async Task RecordMotionForReviewAsync(string motion, string id)
    {
        if (!Ui.NativeUiReview.Enabled) throw new InvalidOperationException();
        if (motion == "settings") { NavigateTo("conn"); SetSettingsExpanded(false, false); }
        else if (motion.StartsWith("settings-group-", StringComparison.Ordinal))
        { SetSettingsExpanded(true, false); NavigateTo("settings"); EnsureSettingsPage().HideDetails(false); }
        else if (motion is "details" or "success" or "nodes") NavigateTo("conn");
        else if (motion == "subs") { NavigateTo("conn"); EnsureSubsPage().ResetEntryForReview(); }
        Root.UpdateLayout(); await Task.Delay(120);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var samples = new List<object>();
        void Start()
        {
            switch (motion)
            {
                case "settings": OnNavClick(NavSettings, null!); break;
                case "settings-close": _pageSettings?.HideDetails(); break;
                case "details": _ = ShowNetworkDetailsAsync(); break;
                case "details-close": _networkDetailsDialog?.Hide(); break;
                case "success": _pageConn?.PlaySuccessForReview(); break;
                case "nodes": NavigateTo("nodes"); break;
                case "subs": NavigateTo("subs"); break;
                case var groupMotion when groupMotion.StartsWith("settings-group-", StringComparison.Ordinal):
                    var key = groupMotion["settings-group-".Length..];
                    var pill = SettingsRevealContent.Children.OfType<Button>().First(b => b.Tag as string == key);
                    OnSettingsGroupClick(pill, null!); break;
                default: throw new InvalidOperationException("Unknown motion " + motion);
            }
        }
        Start();
        foreach (var at in new[] { 0, 40, 80, 140, 220, 320, 420, 540, 680, 820 })
        {
            var wait = at - timer.Elapsed.TotalMilliseconds;
            if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait));
            Root.UpdateLayout();
            object? Surface(FrameworkElement? element)
            {
                if (element == null) return null;
                var t = element.RenderTransform as CompositeTransform;
                return new { element.ActualWidth, element.ActualHeight, element.Opacity,
                    visible = element.Visibility.ToString(), x = t?.TranslateX, y = t?.TranslateY,
                    sx = t?.ScaleX, sy = t?.ScaleY };
            }
            samples.Add(new { at, elapsed = timer.Elapsed.TotalMilliseconds,
                reveal = _settingsRevealMotion?.ExposedHeight ?? 0, revealMotion = _settingsRevealMotion?.Inspect(),
                group = _pageSettings?.OpenGroup, settings = Surface(_pageSettings?.ActiveSurface), settingsMotion = _pageSettings?.DetailMotion,
                detailsOpen = _networkDetailsDialog != null, detailMotion = (_networkDetailsDialog as Ui.CapsuleDialog)?.ReviewMotion });
            var name = id + "-" + at.ToString("000");
            await Ui.NativeUiReview.CaptureWindowAsync(this, name);
        }
        await File.WriteAllTextAsync(Path.Combine(Ui.NativeUiReview.DirectoryPath, id + "-timeline.json"), JsonSerializer.Serialize(new { motion, samples }));
    }

    private async Task<object> StressMotionForReviewAsync()
    {
        if (!Ui.NativeUiReview.Enabled) throw new InvalidOperationException();
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException("Motion regression: " + name); checks.Add(name); }
        NavigateTo("conn"); SetSettingsExpanded(false, false);
        OnNavClick(NavSettings, null!); await Task.Delay(50);
        var reserved = SettingsReveal.Height;
        Check(_settingsRevealMotion != null && reserved > 0 && _settingsRevealMotion.ExposedHeight <= reserved + .5,
            "sidebar reveal reserves full layout once and animates on the compositor");
        await Task.Delay(420);
        Check(_settingsRevealMotion?.Running == false && _pageSettings?.OpenGroup != null && Math.Abs(SettingsReveal.Height - reserved) < .5,
            "sidebar settles without a dependent height clock and dispatches its pending group");
        await Task.Delay(650);
        Check(_pageSettings?.ActiveSurface?.IsHitTestVisible == true,
            "pending detail settles and restores input after the sidebar reveal");
        NavigateTo("conn"); SetSettingsExpanded(false, false);
        OnNavClick(NavSettings, null!); await Task.Delay(70); OnNavClick(NavSettings, null!); await Task.Delay(500);
        Check(!_settingsExpanded && _pageSettings?.OpenGroup == null, "close during sidebar reveal");
        OnNavClick(NavSettings, null!); await Task.Delay(750);
        var pillContents = SettingsRevealContent.Children.OfType<Button>().Select(b => b.Content).ToArray();
        var general = SettingsRevealContent.Children.OfType<Button>().First(b => b.Tag as string == "general");
        var network = SettingsRevealContent.Children.OfType<Button>().First(b => b.Tag as string == "network");
        OnSettingsGroupClick(network, null!); await Task.Delay(70);
        OnSettingsGroupClick(general, null!); await Task.Delay(70);
        OnSettingsGroupClick(general, null!); await Task.Delay(500);
        Check(_pageSettings?.OpenGroup == null && _pageSettings?.ActiveSurface?.Visibility == Visibility.Collapsed, "same pill closes interrupted group growth");
        var normalColor = (ThemeUtil.Get("NeoText2", Microsoft.UI.Colors.Gray) as SolidColorBrush)?.Color;
        Check(SettingsRevealContent.Children.OfType<Button>().All(b => (b.Foreground as SolidColorBrush)?.Color == normalColor),
            "closing a detail clears its pill selection immediately");
        OnSettingsGroupClick(network, null!); await Task.Delay(80);
        NavigateTo("nodes"); await Task.Delay(500);
        Check(!_settingsExpanded && _pageSettings?.ActiveSurface?.Visibility == Visibility.Collapsed, "switch away while detail grows");
        OnNavClick(NavSettings, null!); await Task.Delay(80);
        AppWindow.ResizeClient(new SizeInt32(900,600)); await Task.Delay(800);
        Check(_settingsExpanded && _pageSettings?.OpenGroup != null && _pageSettings?.ActiveSurface?.IsHitTestVisible == true, "resize preserves pending settings open and input");
        NavigateTo("conn"); await Task.Delay(250);
        var first = ShowNetworkDetailsAsync(); await Task.Delay(100); _networkDetailsDialog?.Hide(); await first;
        Check(_networkDetailsDialog == null, "dialog closes during growth and releases modality");
        var second = ShowNetworkDetailsAsync(); await Task.Delay(400); _networkDetailsDialog?.Hide(); _networkDetailsDialog?.Hide(); await second;
        Check(_networkDetailsDialog == null, "reopen and double close release modality");
        Check(pillContents.SequenceEqual(SettingsRevealContent.Children.OfType<Button>().Select(b => b.Content)), "settings selection reuses all six pill content trees");
        return new { checks, count=checks.Count };
    }

    private async Task<object> ReviewNodeRestoreAsync()
    {
        if (!Ui.NativeUiReview.Enabled) throw new InvalidOperationException();
        var checks = new List<string>();
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException("Node restore regression: " + label); checks.Add(label); }
        NavigateTo("nodes"); Root.UpdateLayout();
        var page = EnsureNodesPage();
        var models = page.ReviewTiles;
        var selected = Services.AppState.CurrentNode;
        var presenter = AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter ?? throw new InvalidOperationException();
        presenter.Minimize(); await Task.Delay(3400);
        Check(!_connPageVisible && page.ReviewItemsSource == null, "real minimize releases native node containers after the idle delay");
        presenter.Restore(false); Ui.NativeUiReview.KeepBehind(this); await Task.Delay(350); Root.UpdateLayout();
        Check(_connPageVisible && page.ReviewItemsSource != null && page.ReviewVisibleCount == models.Length,
            $"restoring the same page repopulates every card without navigating away (visible={_connPageVisible}, shown={AppWindow.IsVisible}, state={presenter.State}, source={page.ReviewItemsSource != null}, count={page.ReviewVisibleCount}/{models.Length})");
        Check(models.SequenceEqual(page.ReviewTiles) && ReferenceEquals(selected, Services.AppState.CurrentNode), "restore retains tile models, order and the selected node");
        ApplyPresentationVisibility(false); ReleaseIdlePages(force: true); AppWindow.Hide();
        Check(page.ReviewItemsSource == null, "tray hide releases node containers");
        AppWindow.Show(false); Ui.NativeUiReview.KeepBehind(this); ApplyPresentationVisibility(true); await Task.Delay(150); Root.UpdateLayout();
        Check(page.ReviewVisibleCount == models.Length && page.ReviewItemsSource != null, "tray restore repopulates the existing page");
        page.ReleaseTiles(); NavigateTo("nodes"); Root.UpdateLayout();
        Check(page.ReviewItemsSource != null && page.ReviewVisibleCount == models.Length, "clicking the already selected node tab also repairs a released binding");
        return new { checks, count = checks.Count, nodeCount = page.ReviewVisibleCount, columns = page.ReviewColumns };
    }

    private async Task<object> BenchmarkNodesForReviewAsync()
    {
        if (!Ui.NativeUiReview.Enabled) throw new InvalidOperationException();
        var firstVisitWasCold = _pageNodes == null;
        var cold = System.Diagnostics.Stopwatch.StartNew();
        NavigateTo("nodes"); Root.UpdateLayout(); cold.Stop();
        await Task.Delay(80);
        var page = EnsureNodesPage();
        var tiles = page.ReviewTiles;
        var source = page.ReviewItemsSource;
        var container = page.ReviewFirstContainer;
        var times = new List<double>();
        var sameSource = true; var sameModels = true; var sameContainer = true;
        for (var i = 0; i < 20; i++)
        {
            NavigateTo("conn"); Root.UpdateLayout();
            await Task.Delay(20);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            NavigateTo("nodes"); Root.UpdateLayout(); watch.Stop();
            times.Add(watch.Elapsed.TotalMilliseconds);
            sameSource &= ReferenceEquals(source, page.ReviewItemsSource);
            sameModels &= tiles.SequenceEqual(page.ReviewTiles);
            sameContainer &= container != null && ReferenceEquals(container, page.ReviewFirstContainer);
            await Task.Delay(20);
        }
        Services.AppState.NotifyNodesChanged(); Root.UpdateLayout();
        var resultUpdateReusesModels = tiles.SequenceEqual(page.ReviewTiles);
        var resultUpdateReusesContainers = ReferenceEquals(container, page.ReviewFirstContainer);
        NavigateTo("conn"); ReleaseIdlePages(); NavigateTo("nodes"); Root.UpdateLayout();
        var idleRetainsSource = ReferenceEquals(source, page.ReviewItemsSource);
        NavigateTo("conn"); ReleaseIdlePages(force: true); NavigateTo("nodes"); Root.UpdateLayout();
        var trayRetainsModels = tiles.SequenceEqual(EnsureNodesPage().ReviewTiles);
        var restoredCount = (EnsureNodesPage().ReviewItemsSource as System.Collections.ICollection)?.Count ?? -1;
        if (!sameSource || !sameModels || !sameContainer || !resultUpdateReusesModels || !resultUpdateReusesContainers || !idleRetainsSource || !trayRetainsModels || restoredCount != tiles.Length)
            throw new InvalidOperationException("Node retention regression");
        var sorted = times.OrderBy(x => x).ToArray();
        return new { scope = "navigation plus synchronous XAML layout; excludes compositor frame time", firstVisitWasCold, coldMs = cold.Elapsed.TotalMilliseconds,
            samplesMs = times, medianMs = sorted[sorted.Length / 2], maxMs = sorted[^1], nodeCount = tiles.Length,
            sameSource, sameModels, sameContainer, resultUpdateReusesModels, resultUpdateReusesContainers, idleRetainsSource, trayRetainsModels, restoredCount };
    }
}
