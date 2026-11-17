using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Neo.App.Services;
using Neo.App.Ui;
using Neo.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace Neo.App.Pages;

public sealed partial class SubsPage : UserControl
{
    // Only the visible page samples UsageTracker's in-memory totals; no core subprocess.
    private readonly DispatcherTimer _usageTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _syncingAuto;
    private bool _addingSub;
    private PopupList? _intervalList;
    private bool _trendBuilt;
    private readonly List<Border> _monthBars = new();

    public SubsPage()
    {
        InitializeComponent();
        AppState.NodesChanged += UpdateDisplay;
        ConnectionService.StateChanged += UpdateDisplay;
        // 换主题时把档位缓存清掉，下一拍重刷：格子样式里的 ThemeResource 需要一次重新应用，
        // 否则亮暗切换后可能停在旧主题解析出来的颜色上。
        ActualThemeChanged += (_, _) => QueueThemeRefresh();
        Loaded += (_, _) => { UpdateDisplay(); OnPageShown(); };
        Unloaded += (_, _) => OnPageHidden();
        //   而它内部会 PaintTrend()（重绘 7+30 列格子）。本页用 Visibility 切页、
        //   不卸载，停在别的页时这 3 秒一拍的开销纯属浪费（ConnectionPage 早有
        //   IsPageVisible() 保护，本页漏了）。现补上同样的可见性判断。
        _usageTimer.Tick += (_, _) => { if (Visibility == Visibility.Visible) UpdateUsagePanel(); };
    }


    private void UpdateDisplay()
    {
        RebuildSubCards();

        _syncingAuto = true;
        SwitchAutoUpdate.IsOn = AppState.State.AutoUpdate;
        _syncingAuto = false;
        RefreshAutoUpdateUi();

        // 「可用节点」的 AvailNodesText 已从指标网格移除：节点总数本来就由「地区分布」
        // 的 RegionSummaryText 显示（「共 N 个节点 · 覆盖 M 个地区」），指标网格里重复一次没有意义。
        SubCountText.Text = $"{AppState.Subscriptions.Count} 个订阅 · {AppState.Nodes.Count} 个节点";

        UpdateRegions();
        UpdateUsagePanel();
    }


    private void RebuildSubCards()
    {
        var signature = string.Join("\u001e", AppState.Subscriptions.Select(sub =>
            $"{sub.Id}|{sub.Name}|{sub.Url}|{sub.Enabled}|{sub.NodeCount}|{sub.SubUpload}|{sub.SubDownload}|{sub.SubTotal}|{sub.SubExpireUnix}|{sub.TrafficInfo}|{sub.LastFetchUnix}"))
            + $"|{AppState.State.AutoUpdate}|{AppState.State.AutoUpdateHours}";
        if (_subCardsSignature == signature) return;
        _subCardsSignature = signature;

        SubCardList.Children.Clear();
        PrimarySubHost.Children.Clear();

        if (AppState.Subscriptions.Count == 0)
        {
            SubCardList.Children.Add(Neo.App.Ui.AdaptiveText.WithSize(new TextBlock
            {
                Text = "尚未添加订阅 — 点击下方「添加订阅」填入订阅链接",
                Foreground = ThemeUtil.Get("NeoText2", Color.FromArgb(255, 0x6B, 0x72, 0x80)),
                Margin = new Thickness(2, 6, 2, 6),
            }, 12.5));
            return;
        }

        for (var i = 0; i < AppState.Subscriptions.Count; i++)
        {
            if (i == 0) PrimarySubHost.Children.Add(BuildSubCard(AppState.Subscriptions[i], true));
            SubCardList.Children.Add(BuildSubSummary(AppState.Subscriptions[i], i == 0));
        }
    }

    private Border BuildSubCard(AppState.SubItem sub, bool isPrimary)
    {
        var root = new StackPanel { Spacing = 0 };
        var heading = new Grid { ColumnSpacing = 12 };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new Grid { ColumnSpacing = 10 };
        name.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        name.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        name.HorizontalAlignment = HorizontalAlignment.Left;
        var title = Ui.AdaptiveText.WithSize(new TextBlock { Text = sub.Name, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }, 16);
        name.Children.Add(title);
        var badge = MakeBadge(sub.Enabled ? "当前使用" : "已停用");
        badge.Background = ThemeUtil.Get(sub.Enabled ? "NeoGreenSoft" : "NeoTrack", Colors.Transparent);
        badge.BorderThickness = new Thickness(0); badge.CornerRadius = new CornerRadius(8);
        if (badge.Child is TextBlock badgeText) badgeText.Foreground = ThemeUtil.Get(sub.Enabled ? "NeoGreen" : "NeoText3", Colors.Gray);
        Grid.SetColumn(badge, 1); name.Children.Add(badge); heading.Children.Add(name);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        actions.Children.Add(Ui.AdaptiveText.WithSize(new TextBlock { Text = $"{sub.NodeCount} 节点",
            Foreground = ThemeUtil.Get("NeoText2", Colors.Gray), VerticalAlignment = VerticalAlignment.Center }, 11));
        Button? refresh = null;
        refresh = new Button { Content = "更新", MinWidth = 0, MinHeight = 0, Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(0), Background = ThemeUtil.Get("NeoTrack", Colors.Transparent) };
        Ui.AdaptiveText.SetBaseSize(refresh, 11.5); Ui.AdaptiveText.SetBaseHeight(refresh, 30); AutomationProperties.SetAutomationId(refresh, $"SubRefresh_{sub.Id}");
        refresh.Click += async (_, _) =>
        {
            if (!refresh.IsEnabled) return;
            refresh.IsEnabled = false; refresh.Content = "更新中";
            try { var (ok, error) = await AppState.RefreshOneAsync(sub.Id); if (!ok) await ShowInfo("更新失败", error ?? "未知错误"); }
            finally { refresh.IsEnabled = true; refresh.Content = "更新"; UpdateDisplay(); }
        };
        actions.Children.Add(refresh); Grid.SetColumn(actions, 1); heading.Children.Add(actions); root.Children.Add(heading);
        var url = Ui.AdaptiveText.WithSize(new TextBlock { Text = MaskLink(sub.Url), TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = ThemeUtil.Get("NeoText3", Colors.Gray), Margin = new Thickness(0, 5, 0, 0) }, 11);
        ToolTipService.SetToolTip(url, "订阅链接已隐藏敏感部分；在列表菜单中复制完整链接"); root.Children.Add(url);
        var (used, total, remaining) = AppState.SubUsage(sub.SubUpload, sub.SubDownload, sub.SubTotal, sub.TrafficInfo);
        var percent = total > 0 ? Math.Clamp(used * 100d / total, 0, 100) : 0;
        var track = new Grid { Height = 8, Margin = new Thickness(0, 11, 0, 8), CornerRadius = new CornerRadius(4), Background = ThemeUtil.Get("NeoTrack", Colors.Transparent) };
        var fill = new Border { HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(4), Background = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0,.5), EndPoint = new Windows.Foundation.Point(1,.5), GradientStops = { new GradientStop { Color = QuotaColor(Math.Max(.05, percent / 100 * .55)), Offset=0 }, new GradientStop { Color = QuotaColor(percent / 100), Offset=1 } } } };
        track.Children.Add(fill);
        BindQuotaProgress(track, fill, percent / 100, sub.Id);
        root.Children.Add(track);
        var numbers = new Grid();
        var usedText = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        var (value, unit) = UsageTracker.Split(used);
        usedText.Children.Add(Ui.AdaptiveText.WithSize(new TextBlock { Text = value, FontWeight = FontWeights.SemiBold }, 20));
        usedText.Children.Add(Ui.AdaptiveText.WithSize(new TextBlock { Text = unit + (total > 0 ? " / " + UsageTracker.Format(total) : ""),
            Foreground = ThemeUtil.Get("NeoText2", Colors.Gray), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2) }, 11));
        numbers.Children.Add(usedText);
        numbers.Children.Add(Ui.AdaptiveText.WithSize(new TextBlock { Text = total > 0 ? $"剩余 {UsageTracker.Format(remaining)} · 已用 {percent:0}%" : "未提供额度信息",
            Foreground = ThemeUtil.Get("NeoText2", Colors.Gray), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom }, 11));
        root.Children.Add(numbers);
        var meta = new Border { Margin = new Thickness(0, 11, 0, 0), Padding = new Thickness(0, 9, 0, 0),
            BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = ThemeUtil.Get("NeoHairline2", Colors.Transparent) };
        var metadata = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20 };
        meta.Child = metadata;
        var (expires, colour) = ExpireInfo(sub);
        var details = new[] { ("到期 " + expires, colour), ("更新于 " + (sub.LastFetchUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(sub.LastFetchUnix).LocalDateTime.ToString("MM-dd HH:mm") : "—"), "NeoText2"),
            (AppState.State.AutoUpdate ? $"自动更新 每 {AppState.State.AutoUpdateHours} 小时" : "自动更新 已关闭", "NeoText2") };
        for (var i = 0; i < details.Length; i++) { var text = Ui.AdaptiveText.WithSize(new TextBlock { Text = details[i].Item1,
            Foreground = ThemeUtil.Get(details[i].Item2, Colors.Gray), TextTrimming = TextTrimming.CharacterEllipsis }, 11); metadata.Children.Add(text); }
        root.Children.Add(meta);
        var card = new Border { CornerRadius = new CornerRadius(26), Background = ThemeUtil.Get("NeoCardSurface", Color.FromArgb(255, 0x16, 0x16, 0x1A)),
            BorderBrush = ThemeUtil.Get("NeoRim", Colors.Transparent), BorderThickness = new Thickness(1), Padding = new Thickness(18, 16, 18, 16), Child = root };
        Ui.SurfaceEdge.SetEnabled(card, true); return card;
    }

    private void OnAddSubClick(object sender, RoutedEventArgs e) => OnAddSubTapped(sender, null!);

    private async void OnRefreshAllClick(object sender, RoutedEventArgs e)
    {
        if (!RefreshAllBtn.IsEnabled) return;
        RefreshAllBtn.IsEnabled = false;
        try { foreach (var id in AppState.Subscriptions.Select(s => s.Id).ToArray()) await AppState.RefreshOneAsync(id); }
        finally { RefreshAllBtn.IsEnabled = true; UpdateDisplay(); }
    }

    private void OnSubsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (TrendAndSchedule == null) return;
        var wide = e.NewSize.Width > 918;
        RefreshAllBtn.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        TrendAndSchedule.ColumnDefinitions[1].Width = wide ? new GridLength(320 * Neo.App.Ui.AdaptiveText.Scale) : new GridLength(0);
        Grid.SetColumn(TrendAndSchedule.Children[1] as FrameworkElement, wide ? 1 : 0);
        Grid.SetRow(TrendAndSchedule.Children[1] as FrameworkElement, wide ? 0 : 1);
    }

    private Border BuildSubSummary(AppState.SubItem sub, bool primary)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Neo.App.Ui.AdaptiveText.WithSize(new TextBlock { Text = sub.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = ThemeUtil.Get("NeoText1", Colors.White) }, 12.5));
        text.Children.Add(Neo.App.Ui.AdaptiveText.WithSize(new TextBlock { Text = MaskLink(sub.Url), TextTrimming = TextTrimming.CharacterEllipsis, Foreground = ThemeUtil.Get("NeoText3", Colors.Gray) }, 10.5));
        grid.Children.Add(text);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(Neo.App.Ui.AdaptiveText.WithSize(new TextBlock { Text = $"{sub.NodeCount} 节点", Foreground = ThemeUtil.Get("NeoText2", Colors.Gray), VerticalAlignment = VerticalAlignment.Center }, 11));
        actions.Children.Add(BuildSubscriptionEnabledButton(sub));
        var refresh = MakeGhostButton("\uE72C", "更新", "NeoText1", async () => { await AppState.RefreshOneAsync(sub.Id); UpdateDisplay(); });
        actions.Children.Add(refresh);
        var more = new Button { Content = "···", CornerRadius = new CornerRadius(16), Padding = new Thickness(10, 3, 10, 3), MinWidth = 0, MinHeight = 0, Background = ThemeUtil.Get("NeoTrack", Colors.Transparent) };
        var menu = new MenuFlyout();
        var copy = new MenuFlyoutItem { Text = "复制订阅链接" }; copy.Click += (_, _) => CopyText(sub.Url); menu.Items.Add(copy);
        var delete = new MenuFlyoutItem { Text = "删除订阅" }; delete.Click += async (_, _) => await ConfirmDeleteAsync(sub); menu.Items.Add(delete);
        more.Flyout = menu; actions.Children.Add(more); Grid.SetColumn(actions, 1); grid.Children.Add(actions);
        var row = new Border { Tag = sub.Id, Child = grid, Padding = new Thickness(12, 10, 12, 10), BackgroundTransition = new BrushTransition { Duration = TimeSpan.FromMilliseconds(160) }, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), BorderBrush = ThemeUtil.Get(primary ? "NeoAccentLine" : "NeoRim", Colors.Transparent), Background = ThemeUtil.Get(primary ? "NeoAccentSoft" : "NeoCardSurface", Colors.Transparent) };
        row.PointerEntered += (_, _) => { if (!primary) row.Background = ThemeUtil.Get("NeoGlass2", Colors.Transparent); };
        row.PointerExited += (_, _) => { if (!primary) row.Background = ThemeUtil.Get("NeoCardSurface", Colors.Transparent); };
        AutomationProperties.SetName(more, "订阅选项");
        return row;
    }

    private Button BuildSubscriptionEnabledButton(AppState.SubItem sub)
    {
        var button = new Button { Content = sub.Enabled ? "已启用" : "启用", Style = (Style)Resources["GhostBtn"],
            MinWidth = 0, Tag = sub.Id,
            Background = ThemeUtil.Get(sub.Enabled ? "NeoAccentSoft" : "NeoTrack", Colors.Transparent),
            Foreground = ThemeUtil.Get(sub.Enabled ? "NeoAccent" : "NeoText2", Colors.Gray),
            BorderBrush = ThemeUtil.Get(sub.Enabled ? "NeoAccentLine" : "NeoHairline", Colors.Transparent) };
        AutomationProperties.SetAutomationId(button, "SubEnabled_" + sub.Id);
        AutomationProperties.SetName(button, (sub.Enabled ? "停用" : "启用") + "订阅 " + sub.Name);
        button.Click += OnSubscriptionEnabledClick;
        return button;
    }

    private void OnSubscriptionEnabledClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var sub = AppState.Subscriptions.FirstOrDefault(s => s.Id == id);
        if (sub == null) return;
        // Independent toggle: enabling one subscription does not disable the others.
        AppState.SetSubscriptionEnabled(id, !sub.Enabled);
        UpdateDisplay();
    }

    private static Border MakeBadge(string text) => new()
    {
        CornerRadius = new CornerRadius(5),
        Padding = new Thickness(6, 1.5, 6, 1.5),
        Background = ThemeUtil.Get("NeoAccentSoft", Color.FromArgb(0x1A, 0x25, 0x63, 0xEB)),
        BorderBrush = ThemeUtil.Get("NeoAccentLine", Color.FromArgb(0x38, 0x25, 0x63, 0xEB)),
        BorderThickness = new Thickness(1),
        VerticalAlignment = VerticalAlignment.Center,
        Child = Neo.App.Ui.AdaptiveText.WithSize(new TextBlock
        {
            Text = text,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeUtil.Get("NeoAccent", Color.FromArgb(255, 0x25, 0x63, 0xEB)),
        }, 10),
    };

    /// 供调用方在异步操作期间改文案（如「更新」→「更新中」）。
    /// 为什么需要它：WinUI 的 Button 没有 Text 属性，文字是 Content 里的子 TextBlock，
    /// 外部拿不到引用就没法改。
    private static (Button Btn, TextBlock Text) MakeGhostButtonWithText(
        string glyph, string text, string fgKey, Func<Task>? onClick)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        stack.Children.Add(Neo.App.Ui.AdaptiveText.WithSize(new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            VerticalAlignment = VerticalAlignment.Center,
        }, 12));
        var textBlock = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(textBlock);

        var btn = new Button
        {
            Height = 30,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(7),
            Background = ThemeUtil.Get("NeoGlass2", Color.FromArgb(0x85, 0xFF, 0xFF, 0xFF)),
            BorderBrush = ThemeUtil.Get("NeoHairline", Color.FromArgb(0x14, 0x1A, 0x1D, 0x21)),
            BorderThickness = new Thickness(1),
            FontSize = 11.5,
            FontWeight = FontWeights.Medium,
            Foreground = ThemeUtil.Get(fgKey, fgKey == "NeoRed" ? Color.FromArgb(255, 0xDC, 0x26, 0x26) : Color.FromArgb(255, 0x23, 0x28, 0x2E)),
            Content = stack,
        };

        if (onClick != null)
            btn.Click += async (_, _) => await onClick();
        return (btn, textBlock);
    }

    private static Button MakeGhostButton(string glyph, string text, string fgKey, Func<Task>? onClick)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        stack.Children.Add(Neo.App.Ui.AdaptiveText.WithSize(new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            VerticalAlignment = VerticalAlignment.Center,
        }, 12));
        stack.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });

        var btn = new Button
        {
            Height = 30,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(7),
            Background = ThemeUtil.Get("NeoGlass2", Color.FromArgb(0x85, 0xFF, 0xFF, 0xFF)),
            BorderBrush = ThemeUtil.Get("NeoHairline", Color.FromArgb(0x14, 0x1A, 0x1D, 0x21)),
            BorderThickness = new Thickness(1),
            FontSize = 11.5,
            FontWeight = FontWeights.Medium,
            Foreground = ThemeUtil.Get(fgKey, fgKey == "NeoRed" ? Color.FromArgb(255, 0xDC, 0x26, 0x26) : Color.FromArgb(255, 0x23, 0x28, 0x2E)),
            Content = stack,
        };

        if (onClick != null)
            btn.Click += async (_, _) => await onClick();
        return btn;
    }

    private static StackPanel MakeInfoCol(string label, UIElement value, int col)
    {
        var panel = new StackPanel { Spacing = 5 };
        panel.Children.Add(Neo.App.Ui.AdaptiveText.WithSize(new TextBlock
        {
            Text = label,
            Foreground = ThemeUtil.Get("NeoText2", Color.FromArgb(255, 0x6B, 0x72, 0x80)),
        }, 10.5));
        panel.Children.Add(value);
        Grid.SetColumn(panel, col);
        return panel;
    }

    private static TextBlock MakeValueText(string text, string colorKey) => new()
    {
        Text = text,
        FontSize = 12.5,
        FontWeight = FontWeights.Medium,
        Foreground = ThemeUtil.Get(colorKey, Color.FromArgb(255, 0x23, 0x28, 0x2E)),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private Border MakeLinkBox(string url)
    {
        var grid = new Grid();
        var hint = Neo.App.Ui.AdaptiveText.WithSize(new TextBlock
        {
            Text = "复制",
            FontWeight = FontWeights.Medium,
            Foreground = ThemeUtil.Get("NeoAccent", Color.FromArgb(255, 0x25, 0x63, 0xEB)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        }, 11);
        var captured = url;
        hint.Tapped += async (_, _) =>
        {
            CopyText(captured);
            hint.Text = "已复制";
            await Task.Delay(1500);
            hint.Text = "复制";
        };

        grid.Children.Add(Neo.App.Ui.AdaptiveText.WithSize(new TextBlock
        {
            Text = MaskLink(url),
            FontFamily = new FontFamily("Consolas"),
            Foreground = ThemeUtil.Get("NeoText2", Color.FromArgb(255, 0x6B, 0x72, 0x80)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 50, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        }, 11));
        grid.Children.Add(hint);

        return new Border
        {
            Height = 32,
            CornerRadius = new CornerRadius(7),
            Background = ThemeUtil.Get("NeoGlass2", Color.FromArgb(0x85, 0xFF, 0xFF, 0xFF)),
            BorderBrush = ThemeUtil.Get("NeoHairline", Color.FromArgb(0x14, 0x1A, 0x1D, 0x21)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 0, 10, 0),
            Child = grid,
        };
    }

    private static StackPanel MakeTrafficBlock(string flowText, string pctText, double pct)
    {
        var root = new StackPanel { Spacing = 4 };

        var head = new Grid();
        head.Children.Add(Neo.App.Ui.AdaptiveText.WithSize(new TextBlock
        {
            Text = "流量使用",
            Foreground = ThemeUtil.Get("NeoText2", Color.FromArgb(255, 0x6B, 0x72, 0x80)),
            VerticalAlignment = VerticalAlignment.Bottom,
        }, 11.5));
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(Neo.App.Ui.AdaptiveText.WithSize(new TextBlock
        {
            Text = flowText,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeUtil.Get("NeoText1", Color.FromArgb(255, 0x23, 0x28, 0x2E)),
        }, 12.5));
        right.Children.Add(Neo.App.Ui.AdaptiveText.WithSize(new TextBlock
        {
            Text = pctText,
            FontWeight = FontWeights.Medium,
            Foreground = ThemeUtil.Get("NeoText2", Color.FromArgb(255, 0x6B, 0x72, 0x80)),
        }, 10.5));
        head.Children.Add(right);
        root.Children.Add(head);

        var barGrid = new Grid { Height = 8, Margin = new Thickness(0, 4, 0, 0) };
        barGrid.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(5),
            Background = ThemeUtil.Get("NeoTrack", Color.FromArgb(0x0D, 0x1A, 0x1D, 0x21)),
        });
        var fillCols = new Grid();
        fillCols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(pct, 0.5), GridUnitType.Star) });
        fillCols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(100 - pct, 0.5), GridUnitType.Star) });
        var fill = new Border { CornerRadius = new CornerRadius(5) };
        Grid.SetColumn(fill, 0);
        var grad = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 0) };
        grad.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 0x25, 0x63, 0xEB), Offset = 0 });
        grad.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 0x60, 0xA5, 0xFA), Offset = 1 });
        fill.Background = grad;
        fillCols.Children.Add(fill);
        barGrid.Children.Add(fillCols);
        root.Children.Add(barGrid);

        return root;
    }

    private static (string Text, string ColorKey) ExpireInfo(AppState.SubItem sub)
    {
        DateTime? d = null;
        if (sub.SubExpireUnix > 0)
            d = DateTimeOffset.FromUnixTimeSeconds(sub.SubExpireUnix).LocalDateTime;
        else
        {
            var expire = AppState.TrafficParts(sub.TrafficInfo).Expire;
            if (expire != "—" && DateTime.TryParse(expire, out var parsed)) d = parsed;
        }

        if (d == null) return ("—", "NeoText1");
        if (d < DateTime.Now) return (d.Value.ToString("yyyy-MM-dd") + " · 已过期", "NeoRed");
        if (d - DateTime.Now < TimeSpan.FromDays(30)) return (d.Value.ToString("yyyy-MM-dd") + " · 即将到期", "NeoYellow");
        return (d.Value.ToString("yyyy-MM-dd"), "NeoText1");
    }


    private async void OnAddSubTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_addingSub) return;

        var input = new TextBox
        {
            PlaceholderText = "https://…（订阅链接或本地文件路径）",
            FontSize = 12.5,
            MinWidth = 400,
        };
        var dlg = new ContentDialog
        {
            Title = "添加订阅",
            Content = input,
            PrimaryButtonText = "添加并拉取",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await Neo.App.Ui.DialogGuard.ShowAsync(dlg) != ContentDialogResult.Primary)
            return;

        var url = input.Text.Trim();
        if (url.Length == 0)
            return;

        var id = AppState.AddSubscription(url);

        //   用户会以为没反应而反复点。现在禁用入口并显示「拉取中…」，finally 恢复。
        _addingSub = true;
        try
        {
            AddSubRow.IsHitTestVisible = false;
            AddSubRow.Opacity = 0.6;
            AddSubTitle.Text = "拉取中…";

            var (ok, err) = await AppState.RefreshOneAsync(id);
            if (!ok)
            {
                await ShowInfo("拉取失败", err ?? "未知错误");
            }
            else
            {
                var imported = AppState.Subscriptions.FirstOrDefault(s => s.Id == id);
                if (imported is { Enabled: false })
                {
                    await ShowInfo(
                        "已导入，未启用",
                        $"订阅「{imported.Name}」已保存并拉取成功。已有订阅时新订阅默认不启用，不会参与当前节点列表；需要时在订阅卡片打开开关即可。");
                }
            }
        }
        finally
        {
            AddSubTitle.Text = "添加订阅";
            AddSubRow.Opacity = 1.0;
            AddSubRow.IsHitTestVisible = true;
            _addingSub = false;
            UpdateDisplay();
        }
    }

    private async Task ConfirmDeleteAsync(AppState.SubItem sub)
    {
        var dlg = new ContentDialog
        {
            Title = "删除订阅",
            Content = $"确定删除订阅「{sub.Name}」吗？其节点会从列表移除（缓存一并清除）。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot,
        };
        if (await Neo.App.Ui.DialogGuard.ShowAsync(dlg) != ContentDialogResult.Primary)
            return;

        await AnimateSubscriptionRemovalAsync(sub.Id);
        AppState.RemoveSubscriptionById(sub.Id);
        UpdateDisplay();
    }

    private static void CopyText(string text)
    {
        try
        {
            var dp = new DataPackage();
            dp.SetText(text);
            Clipboard.SetContent(dp);
        }
        catch { }
    }

    private static string MaskLink(string url)
    {
        try
        {
            var u = new Uri(url);
            var tail = url.Length >= 4 ? url[^4..] : "";
            return $"{u.Scheme}://{u.Host}/••••••••{tail}";
        }
        catch { return url; }
    }

    private void OnAutoUpdateToggled(object sender, RoutedEventArgs e)
    {
        if (_syncingAuto) return;
        AppState.State.AutoUpdate = SwitchAutoUpdate.IsOn;
        AppState.Save();
    }

    private void OnAutoUpdateIntervalTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        _intervalList ??= new Neo.App.Ui.PopupList();
        _intervalList.ClearItems();
        foreach (var h in new[] { 1, 3, 6, 12, 24 })
        {
            var captured = h;
            _intervalList.AddItem($"每 {h} 小时", null, () =>
            {
                AppState.State.AutoUpdateHours = captured;
                AppState.Save();
                RefreshAutoUpdateUi();
            });
        }
        _intervalList.AddSeparator();
        _intervalList.AddDisabled("应用运行期间生效");
        var fe = sender as Microsoft.UI.Xaml.FrameworkElement ?? AutoUpdateIntervalBox;
        Neo.App.Ui.PopupList.ShowBelow(_intervalList, fe, fe.ActualWidth > 0 ? fe.ActualWidth : 260);
    }

    private void RefreshAutoUpdateUi()
    {
        var h = Math.Clamp(AppState.State.AutoUpdateHours, 1, 24);
        AutoUpdateIntervalText.Text = $"{h} 小时";
        var last = AppState.Subscriptions.Select(sub => sub.LastFetchUnix).DefaultIfEmpty(0).Max();
        LastSubscriptionRefresh.Text = last > 0 ? DateTimeOffset.FromUnixTimeSeconds(last).LocalDateTime.ToString("MM-dd HH:mm") : "尚未更新";
    }

    private async Task ShowInfo(string title, string message)
    {
        var dlg = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "知道了",
            XamlRoot = XamlRoot,
        };
        await Neo.App.Ui.DialogGuard.ShowAsync(dlg);
    }


    private static readonly (string Key, string Label)[] Regions =
    {
        ("hk", "香港"), ("tw", "台湾"), ("jp", "日本"), ("sg", "新加坡"), ("us", "美国"), ("kr", "韩国"),
        ("gb", "英国"), ("de", "德国"), ("nl", "荷兰"), ("au", "澳大利亚"), ("other", "其他"),
    };

    private void UpdateRegions()
    {
        var counts = new Dictionary<string, int>();
        foreach (var n in AppState.Nodes)
        {
            var k = AppState.GuessFlag(n.Name);
            counts[k] = counts.GetValueOrDefault(k) + 1;
        }

        RegionRow1.Children.Clear();
        RegionRow2.Children.Clear();

        var present = Regions.Where(r => counts.GetValueOrDefault(r.Key) > 0).ToList();
        RegionSummaryText.Text = AppState.Nodes.Count == 0
            ? "—"
            : $"共 {AppState.Nodes.Count} 个节点 · 覆盖 {present.Count} 个地区";

        int half = (present.Count + 1) / 2;
        for (int i = 0; i < present.Count; i++)
        {
            var chip = BuildRegionChip(present[i].Key, present[i].Label, counts[present[i].Key]);
            (i < half ? RegionRow1 : RegionRow2).Children.Add(chip);
        }
    }

    private static Border BuildRegionChip(string flag, string label, int count)
    {
        var flagInner = new Grid();
        var flagBox = new Border
        {
            Width = 16,
            Height = 11,
            CornerRadius = new CornerRadius(2.5),
            Background = new SolidColorBrush(Colors.White),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x1A, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = flagInner,
        };
        FlagPainter.Draw(flagInner, flag);

        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(flagBox);
        stack.Children.Add(Neo.App.Ui.AdaptiveText.WithSize(new TextBlock
        {
            Text = label,
            Foreground = ThemeUtil.Get("NeoText1", Color.FromArgb(255, 0x1A, 0x1D, 0x21)),
            VerticalAlignment = VerticalAlignment.Center,
        }, 10.5));
        stack.Children.Add(Neo.App.Ui.AdaptiveText.WithSize(new TextBlock
        {
            Text = count.ToString(),
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeUtil.Get("NeoAccent", Color.FromArgb(255, 0x25, 0x63, 0xEB)),
            VerticalAlignment = VerticalAlignment.Center,
        }, 10.5));

        return new Border
        {
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = ThemeUtil.Get("NeoChipBg", Color.FromArgb(0x85, 0xFF, 0xFF, 0xFF)),
            BorderBrush = ThemeUtil.Get("NeoHairline2", Color.FromArgb(0x0C, 0x1A, 0x1D, 0x21)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(7, 0, 7, 0),
            Child = stack,
        };
    }


    /// 刷新指标网格里的「本周 / 本月 / 总计」与两处用量趋势。
    ///   旧的 138.59 GB 只存在于 Total 里、没有按天拆分。所以本周 / 本月上线首日必然偏小，
    ///   趋势图前几格会是空的 —— 这是数据本身的缺失，不是显示或算错，界面不做特殊处理。
    private void UpdateUsagePanel()
    {
        var (wv, wu) = UsageTracker.Split(UsageTracker.WeekBytes);
        WeekUsageText.Text = wv; WeekUsageUnit.Text = wu;
        var (mv, mu) = UsageTracker.Split(UsageTracker.MonthBytes);
        MonthUsageText.Text = mv; MonthUsageUnit.Text = mu;
        var (tv, tu) = UsageTracker.Split(UsageTracker.TotalBytes);
        TotalUsageText.Text = tv; TotalUsageUnit.Text = tu;

        PaintTrend();
    }

    /// 导出每日用量明细（CSV，最近 60 天）。文件落在 data 目录，弹窗给出完整路径。
    private async void OnExportUsageClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var days = 60;
            var bytes = UsageTracker.DailySeries(days);
            var today = DateTime.Now.Date;
            var dir = NeoPaths.DataDir;
            var path = System.IO.Path.Combine(dir, $"usage-export-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("日期,字节,GB");
            for (var i = 0; i < days; i++)
            {
                var d = today.AddDays(i - (days - 1));
                sb.AppendLine($"{d:yyyy-MM-dd},{bytes[i]},{(bytes[i] / 1024.0 / 1024 / 1024):F3}");
            }
            System.IO.File.WriteAllText(path, sb.ToString());
            LogBus.App($"用量明细已导出：{path}");
            var dlg = new ContentDialog
            {
                Title = "导出完成",
                Content = Neo.App.Ui.AdaptiveText.WithSize(new TextBlock
                {
                    Text = $"已导出最近 {days} 天用量明细：\n{path}",
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                }, 12.5),
                CloseButtonText = "知道了",
                XamlRoot = XamlRoot,
            };
            await Neo.App.Ui.DialogGuard.ShowAsync(dlg);
        }
        catch (Exception ex)
        {
            LogBus.Exception("导出用量明细", ex);
        }
    }

    /// 把 7 日 / 30 日两条趋势画出来。    /// 把 7 日 / 30 日两条趋势画出来。列与格子在首次进入时建一次，之后只改格子的亮灭样式
    /// （列数固定为 7 / 30，不会变）。
    /// 同样的 20 GB/日 顶格，每格代表 5 GB。
    private void OnTrendSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Math.Abs(e.NewSize.Height - e.PreviousSize.Height) > .5) PaintTrend();
    }

    public void Release()
    {
        StopTimers();
        try { AppState.NodesChanged -= UpdateDisplay; } catch { }
        try { ConnectionService.StateChanged -= UpdateDisplay; } catch { }
        try { SubCardList.Children.Clear(); } catch { }
    }

    public void StopTimers()
    {
        try { _usageTimer.Stop(); StopEntryMotion(); } catch { }
    }
}
