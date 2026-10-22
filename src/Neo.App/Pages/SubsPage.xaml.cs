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
    /// 用量与趋势的刷新计时器。原先它 3 秒一次去轮询 StatsSampler 填「本次会话上传 / 下载」；
    /// 那两项已随首页改版移入内核卡，本页改为用它刷新「本周 / 本月 / 总计」与用量趋势。
    /// 现在的 Tick 只读 UsageTracker 的内存态（已加锁的字段读，微秒级）+ 最多 176 个格子的
    /// 样式比较，**不再起 xray 子进程**，因此不存在原先注释里担心的 UI 线程冻结问题。
    private readonly Microsoft.UI.Xaml.DispatcherTimer _usageTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _syncingAuto;
    private bool _addingSub;

    private Neo.App.Ui.PopupList? _intervalList;

    private readonly List<Border> _weekCells = new();
    private readonly List<Grid> _weekCols = new();
    private readonly List<Grid> _monthCols = new();
    private readonly List<Border> _monthCells = new();
    private bool _trendBuilt;

    /// <summary>上一帧算出的档位，用于「值没变就不写样式」——避免每 3 秒重复写上百个依赖属性。</summary>
    private int[]? _weekLevels;
    private int[]? _monthLevels;

    private const double TrendWatermarkBytes = 20d * 1024 * 1024 * 1024 / 8;

    public SubsPage()
    {
        InitializeComponent();
        AppState.NodesChanged += UpdateDisplay;
        ConnectionService.StateChanged += UpdateDisplay;
        // 换主题时把档位缓存清掉，下一拍重刷：格子样式里的 ThemeResource 需要一次重新应用，
        // 否则亮暗切换后可能停在旧主题解析出来的颜色上。
        ActualThemeChanged += (_, _) => { _weekLevels = null; _monthLevels = null; };
        Loaded += (_, _) => UpdateDisplay();
        //   而它内部会 PaintTrend()（重绘 7+30 列格子）。本页用 Visibility 切页、
        //   不卸载，停在别的页时这 3 秒一拍的开销纯属浪费（ConnectionPage 早有
        //   IsPageVisible() 保护，本页漏了）。现补上同样的可见性判断。
        _usageTimer.Tick += (_, _) => { if (Visibility == Visibility.Visible) UpdateUsagePanel(); };
        _usageTimer.Start();
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
        var tested = AppState.Nodes.Where(n => n.RealDelay > 0).ToList();
        AvgLatText.Text = tested.Count > 0 ? (tested.Sum(n => n.RealDelay) / tested.Count).ToString() : "—";

        UpdateRegions();
        UpdateUsagePanel();
    }


    private void RebuildSubCards()
    {
        SubCardList.Children.Clear();

        if (AppState.Subscriptions.Count == 0)
        {
            SubCardList.Children.Add(new TextBlock
            {
                Text = "尚未添加订阅 — 点击下方「添加订阅」填入订阅链接",
                FontSize = 12.5,
                Foreground = ThemeUtil.Get("NeoText2", Color.FromArgb(255, 0x6B, 0x72, 0x80)),
                Margin = new Thickness(2, 6, 2, 6),
            });
            return;
        }

        for (var i = 0; i < AppState.Subscriptions.Count; i++)
        {
            var card = BuildSubCard(AppState.Subscriptions[i], i == 0);
            if (i > 0) card.Margin = new Thickness(0, 12, 0, 0);
            SubCardList.Children.Add(card);
        }
    }

    private Border BuildSubCard(AppState.SubItem sub, bool isPrimary)
    {
        var root = new StackPanel { Spacing = 12 };

        var row1 = new Grid();
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new Ellipse
        {
            Width = 7,
            Height = 7,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = ThemeUtil.Get(sub.Enabled ? "NeoGreen" : "NeoText3", Color.FromArgb(255, 0x98, 0xA0, 0xAA)),
        });
        left.Children.Add(new TextBlock
        {
            Text = sub.Name,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeUtil.Get("NeoText1", Color.FromArgb(255, 0x23, 0x28, 0x2E)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        left.Children.Add(MakeBadge($"{sub.NodeCount} 节点"));
        if (isPrimary) left.Children.Add(MakeBadge("默认"));

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };

        var toggle = new ToggleSwitch
        {
            MinWidth = 0,
            OnContent = "",
            OffContent = "",
            IsOn = sub.Enabled,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetAutomationId(toggle, $"SubToggle_{sub.Id}");
        var sidToggle = sub.Id;
        toggle.Toggled += (_, _) =>
        {
            AppState.SetSubscriptionEnabled(sidToggle, toggle.IsOn);
            UpdateDisplay();
        };
        right.Children.Add(toggle);

        var sid = sub.Id;

        //   期间按钮毫无变化 —— 用户以为没反应，会反复点。现在：置灰 + 文案改
        //   「更新中」，finally 里恢复；并防重入。
        //   注意：WinUI 的 Button **没有 Text 属性**，MakeGhostButton 把文字放在
        //   Content(StackPanel) 的第二个 TextBlock 里，所以这里直接持引用改它。
        Button? refreshBtnRef = null;
        TextBlock? refreshTextRef = null;
        var (refreshBtn, refreshText) = MakeGhostButtonWithText("\uE72C", "更新", "NeoText1", async () =>
        {
            if (refreshBtnRef is not { IsEnabled: true }) return;
            refreshBtnRef.IsEnabled = false;
            if (refreshTextRef != null) refreshTextRef.Text = "更新中";
            try
            {
                var (ok, err) = await AppState.RefreshOneAsync(sid);
                if (!ok) await ShowInfo("更新失败", err ?? "未知错误");
            }
            finally
            {
                if (refreshTextRef != null) refreshTextRef.Text = "更新";
                refreshBtnRef.IsEnabled = true;
                UpdateDisplay();
            }
        });
        refreshBtnRef = refreshBtn;
        refreshTextRef = refreshText;
        AutomationProperties.SetAutomationId(refreshBtn, $"SubRefresh_{sid}");
        right.Children.Add(refreshBtn);

        var copyBtn = MakeGhostButton("\uE8C8", "复制", "NeoText1", null);
        AutomationProperties.SetAutomationId(copyBtn, $"SubCopy_{sid}");
        copyBtn.Click += async (_, _) =>
        {
            CopyText(sub.Url);
            if (copyBtn.Content is StackPanel sp && sp.Children.Count > 1 && sp.Children[1] is TextBlock tb)
            {
                tb.Text = "已复制";
                await Task.Delay(1500);
                tb.Text = "复制";
            }
        };
        right.Children.Add(copyBtn);

        var delBtn = MakeGhostButton("\uE74D", "删除", "NeoRed", null);
        AutomationProperties.SetAutomationId(delBtn, $"SubDelete_{sid}");
        delBtn.Click += async (_, _) => await ConfirmDeleteAsync(sub);
        right.Children.Add(delBtn);

        Grid.SetColumn(right, 1);
        row1.Children.Add(left);
        row1.Children.Add(right);
        root.Children.Add(row1);

        var row2 = new Grid { ColumnSpacing = 14 };
        row2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
        row2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var (expireText, expireColor) = ExpireInfo(sub);
        row2.Children.Add(MakeInfoCol("订阅链接", MakeLinkBox(sub.Url), 0));
        row2.Children.Add(MakeInfoCol("到期时间", MakeValueText(expireText, expireColor), 1));
        row2.Children.Add(MakeInfoCol("上次更新", MakeValueText(
            sub.LastFetchUnix > 0
                ? DateTimeOffset.FromUnixTimeSeconds(sub.LastFetchUnix).LocalDateTime.ToString("yyyy-MM-dd HH:mm")
                : "—",
            "NeoText1"), 2));
        root.Children.Add(row2);

        var (usedB, totalB, remainingB) = AppState.SubUsage(sub.SubUpload, sub.SubDownload, sub.SubTotal, sub.TrafficInfo);
        var (flowMeta, totalMeta, _) = AppState.TrafficParts(sub.TrafficInfo);
        string flowText, pctText;
        double pct;
        if (totalB > 0)
        {
            flowText = UsageTracker.Format(usedB);
            pctText = $"/ {UsageTracker.Format(totalB)} · 剩余 {UsageTracker.Format(remainingB)}";
            pct = Math.Clamp(usedB * 100.0 / totalB, 0, 100);
        }
        else
        {
            flowText = flowMeta;
            pctText = totalMeta == "—" ? "" : $"/ {totalMeta}";
            pct = 0;
        }
        root.Children.Add(MakeTrafficBlock(flowText, pctText, pct));

        return new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = ThemeUtil.Get("NeoGlass2", Color.FromArgb(0x85, 0xFF, 0xFF, 0xFF)),
            BorderBrush = ThemeUtil.Get("NeoHairline2", Color.FromArgb(0x0C, 0x1A, 0x1D, 0x21)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14),
            Child = root,
        };
    }


    private static Border MakeBadge(string text) => new()
    {
        CornerRadius = new CornerRadius(5),
        Padding = new Thickness(6, 1.5, 6, 1.5),
        Background = ThemeUtil.Get("NeoAccentSoft", Color.FromArgb(0x1A, 0x25, 0x63, 0xEB)),
        BorderBrush = ThemeUtil.Get("NeoAccentLine", Color.FromArgb(0x38, 0x25, 0x63, 0xEB)),
        BorderThickness = new Thickness(1),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeUtil.Get("NeoAccent", Color.FromArgb(255, 0x25, 0x63, 0xEB)),
        },
    };

    /// 供调用方在异步操作期间改文案（如「更新」→「更新中」）。
    /// 为什么需要它：WinUI 的 Button 没有 Text 属性，文字是 Content 里的子 TextBlock，
    /// 外部拿不到引用就没法改。
    private static (Button Btn, TextBlock Text) MakeGhostButtonWithText(
        string glyph, string text, string fgKey, Func<Task>? onClick)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        stack.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        });
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
        stack.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        });
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
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 10.5,
            Foreground = ThemeUtil.Get("NeoText2", Color.FromArgb(255, 0x6B, 0x72, 0x80)),
        });
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
        var hint = new TextBlock
        {
            Text = "复制",
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Foreground = ThemeUtil.Get("NeoAccent", Color.FromArgb(255, 0x25, 0x63, 0xEB)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var captured = url;
        hint.Tapped += async (_, _) =>
        {
            CopyText(captured);
            hint.Text = "已复制";
            await Task.Delay(1500);
            hint.Text = "复制";
        };

        grid.Children.Add(new TextBlock
        {
            Text = MaskLink(url),
            FontSize = 11,
            FontFamily = new FontFamily("Consolas"),
            Foreground = ThemeUtil.Get("NeoText2", Color.FromArgb(255, 0x6B, 0x72, 0x80)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 50, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
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
        head.Children.Add(new TextBlock
        {
            Text = "流量使用",
            FontSize = 11.5,
            Foreground = ThemeUtil.Get("NeoText2", Color.FromArgb(255, 0x6B, 0x72, 0x80)),
            VerticalAlignment = VerticalAlignment.Bottom,
        });
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(new TextBlock
        {
            Text = flowText,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeUtil.Get("NeoText1", Color.FromArgb(255, 0x23, 0x28, 0x2E)),
        });
        right.Children.Add(new TextBlock
        {
            Text = pctText,
            FontSize = 10.5,
            FontWeight = FontWeights.Medium,
            Foreground = ThemeUtil.Get("NeoText2", Color.FromArgb(255, 0x6B, 0x72, 0x80)),
        });
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
                await ShowInfo("拉取失败", err ?? "未知错误");
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
        AutoUpdateHint.Text = $"每 {h} 小时自动拉取一次（应用运行期间）";
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
            Width = 20,
            Height = 14,
            CornerRadius = new CornerRadius(2.5),
            Background = new SolidColorBrush(Colors.White),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x1A, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = flagInner,
        };
        FlagPainter.Draw(flagInner, flag);

        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(flagBox);
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = ThemeUtil.Get("NeoText1", Color.FromArgb(255, 0x1A, 0x1D, 0x21)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        stack.Children.Add(new TextBlock
        {
            Text = count.ToString(),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeUtil.Get("NeoAccent", Color.FromArgb(255, 0x25, 0x63, 0xEB)),
            VerticalAlignment = VerticalAlignment.Center,
        });

        return new Border
        {
            Height = 30,
            CornerRadius = new CornerRadius(8),
            Background = ThemeUtil.Get("NeoGlass2", Color.FromArgb(0x85, 0xFF, 0xFF, 0xFF)),
            BorderBrush = ThemeUtil.Get("NeoHairline2", Color.FromArgb(0x0C, 0x1A, 0x1D, 0x21)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(11, 0, 11, 0),
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
                Content = new TextBlock
                {
                    Text = $"已导出最近 {days} 天用量明细：\n{path}",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12.5,
                    IsTextSelectionEnabled = true,
                },
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
    private void PaintTrend()
    {
        if (!_trendBuilt) BuildTrend();

        var week = UsageTracker.DailySeries(7);
        var month = UsageTracker.DailySeries(30);

        // 汇总行：合计 / 日均 / 今日 / 峰值（带日期）
        var today = DateTime.Now.Date;
        long wPeak = 0, wSum = 0;
        foreach (var v in week) { if (v > wPeak) wPeak = v; wSum += v; }
        var wPeakDate = today.AddDays(Array.IndexOf(week, wPeak) - 6);
        TrendWeekHint.Text = $"最近 7 天 · 合计 {UsageTracker.Format(wSum)} · 日均 {UsageTracker.Format(wSum / 7)} · 今日 {UsageTracker.Format(week[^1])} · 峰值 {UsageTracker.Format(wPeak)}（{wPeakDate:M/d}）";
        long mPeak = 0, mSum = 0;
        foreach (var v in month) { if (v > mPeak) mPeak = v; mSum += v; }
        var mPeakDate = today.AddDays(Array.IndexOf(month, mPeak) - 29);
        TrendMonthHint.Text = $"最近 30 天 · 合计 {UsageTracker.Format(mSum)} · 日均 {UsageTracker.Format(mSum / 30)} · 峰值 {UsageTracker.Format(mPeak)}（{mPeakDate:M/d}）· 每列 1 天，满格 20 GB";

        // 每列 tooltip：日期 + 当天用量（格子太小，不悬停看不到具体值）
        for (int i = 0; i < _weekCols.Count && i < week.Length; i++)
            ToolTipService.SetToolTip(_weekCols[i], $"{today.AddDays(i - (week.Length - 1)):M月d日} · {UsageTracker.Format(week[i])}");
        for (int i = 0; i < _monthCols.Count && i < month.Length; i++)
            ToolTipService.SetToolTip(_monthCols[i], $"{today.AddDays(i - (month.Length - 1)):M月d日} · {UsageTracker.Format(month[i])}");

        var wLv = new int[7];
        for (int i = 0; i < wLv.Length; i++) wLv[i] = LevelOf(week[i], 8);
        ApplyLevels(_weekCells, wLv, ref _weekLevels);

        var mLv = new int[30];
        for (int i = 0; i < mLv.Length; i++) mLv[i] = LevelOf(month[i], 4);
        ApplyLevels(_monthCells, mLv, ref _monthLevels);
    }

    /// 按日用量算点亮档位。
    /// 先按统一的 8 档阈值算出 <c>lv8</c>（顶格 20 GB/日 → 每档 2.5 GB），
    /// 再按目标格数缩放：8 格直接用 lv8；4 格（近 30 日）用 <c>ceil(lv8 / 2)</c> 压成 4 档。
    ///   后者在 10 GB 处就顶到 4 格全亮，意味着 10 GB 与 20 GB 看起来一模一样，
    ///   而「顶格 20 GB/日」这个刻度就名存实亡了。先算 8 档再压，10 GB → lv8=4 → 2 格，
    ///   20 GB → lv8=8 → 4 格，刻度关系保持正确。
    /// 只放大不缩小：只要有量就至少点亮 1 格，避免「用了但看不出来」。
    private static int LevelOf(long bytes, int rows)
    {
        if (bytes <= 0) return 0;
        int lv8 = Math.Clamp((int)Math.Round(bytes / TrendWatermarkBytes), 1, 8);
        if (rows >= 8) return lv8;
        // 8 档 → 更少档：向上取整，让「刚好过一档」的量也能看出来，且保证 lv8=1 时至少 1 格。
        return Math.Clamp((int)Math.Ceiling(lv8 * rows / 8d), 1, rows);
    }

    /// 建列与格子。7 日每条列 8 格、30 日每条列 4 格，列内**自底部向上**点亮
    private void BuildTrend()
    {
        BuildTrendColumns(TrendWeekGrid, 7, 8, _weekCells, _weekCols);
        BuildTrendColumns(TrendMonthGrid, 30, 4, _monthCells, _monthCols);
        // 用「日期」而不是「周几」：滚动 7 天从六天前开始，写周几会让人误以为从周一开始。
        BuildTrendLabels(TrendWeekLabels, 7, (date, ordinal) => ordinal == 6 ? "今天" : $"{date.Month}/{date.Day}");
        // 30 天：从右往左每 5 列标一个，保证最后一个标签与右端间隔也是 5 列。
        BuildTrendLabels(TrendMonthLabels, 30, (date, ordinal) => (29 - ordinal) % 5 == 0 ? $"{date.Month}/{date.Day}" : "");
        _trendBuilt = true;
    }

    /// 趋势图下方的日期标签（周：周几；月：每 5 天一个日期）。
    private void BuildTrendLabels(Grid host, int cols, Func<DateTime, int, string> text)
    {
        host.ColumnDefinitions.Clear();
        host.Children.Clear();
        var today = DateTime.Now.Date;
        for (int c = 0; c < cols; c++)
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int c = 0; c < cols; c++)
        {
            var d = today.AddDays(c - (cols - 1));
            var isToday = c == cols - 1;
            var tb = new TextBlock
            {
                Text = text(d, c),
                FontSize = 9.5,
                FontWeight = isToday ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = isToday
                    ? ThemeUtil.Get("NeoAccent", Color.FromArgb(255, 0x25, 0x63, 0xEB))
                    : ThemeUtil.Get("NeoText3", Color.FromArgb(255, 0x98, 0xA0, 0xAA)),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            Grid.SetColumn(tb, c);
            host.Children.Add(tb);
        }
    }

    private void BuildTrendColumns(Grid host, int cols, int rows, List<Border> sink, List<Grid> colSink)
    {
        sink.Clear();
        colSink.Clear();
        host.ColumnDefinitions.Clear();
        host.Children.Clear();

        for (int c = 0; c < cols; c++)
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 样式从本页 UserControl.Resources 取（FrameworkElement.Resources 只查自己那一层，
        // 不会向上找祖先，所以必须走 this.Resources）。
        var offStyle = (Style)Resources["TrendCellOff"];

        for (int c = 0; c < cols; c++)
        {
            // 列用 Grid 而不是 StackPanel：单元格没内容，只有背景与圆角，
            // 若交给 StackPanel 排，每个 Border 的期望高度是 0，格子会整片塌陷看不见。
            // Grid 的 * 行会把宿主高度（7 日用 74、30 日用 40）均分下去，
            var col = new Grid { RowSpacing = 2 };
            for (int r = 0; r < rows; r++)
                col.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            for (int r = 0; r < rows; r++)
            {
                var cell = new Border { Style = offStyle };
                Grid.SetRow(cell, r);
                col.Children.Add(cell);
                // 注意：这里按行序（上→下）加入，sink 里下标 0 就是**最上面**那格。
                sink.Add(cell);
            }

            Grid.SetColumn(col, c);
            host.Children.Add(col);
            colSink.Add(col);
        }
    }

    /// 把档位写进格子。sink 是「按列、列内自上而下」依次追加的扁平列表，
    /// 故第 c 列第 k 个格子在 sink 里的下标是 c * rows + k；点亮判定要翻转成
    private void ApplyLevels(List<Border> cells, int[] levels, ref int[]? cache)
    {
        if (cells.Count == 0) return;
        int cols = levels.Length;
        int rows = cells.Count / cols;
        if (rows <= 0) return;

        // 样式取本页 UserControl.Resources 上的两份（FrameworkElement.Resources 只查自己
        // 那一层、不会向上找祖先，所以必须从 this.Resources 取，不能从格子上取）。
        var onStyle = (Style)Resources["TrendCellOn"];
        var offStyle = (Style)Resources["TrendCellOff"];

        if (cache == null || cache.Length != levels.Length)
        {
            cache = new int[levels.Length];
            for (int i = 0; i < cache.Length; i++) cache[i] = -1;   // 首帧必须全部重新上色
        }

        for (int c = 0; c < cols; c++)
        {
            int lv = levels[c];
            if (cache[c] == lv) continue;   // 档位没变就不写这 rows 个依赖属性
            cache[c] = lv;
            // 「用得越多颜色越深」：整列点亮格子的不透明度随档位升高（最低 35% → 最高 100%）。
            double depth = lv <= 0 ? 1.0 : 0.35 + 0.65 * lv / rows;
            for (int k = 0; k < rows; k++)
            {
                // k=0 是最上面那格 → 距底部第 (rows-1-k) 格；
                // 点亮「底部起 lv 格」等价于 k >= rows - lv，方向因此是自下而上。
                bool on = k >= rows - lv;
                cells[c * rows + k].Style = on ? onStyle : offStyle;
                cells[c * rows + k].Opacity = on ? depth : 1.0;
            }
        }
    }


    /// 退出时停掉本页计时器。原先它没有任何 Stop 点，Exit 之后仍会继续 Tick 并写 XAML，
    /// 属于「退出期后台活动碰已销毁 UI」的一类，必须一并停掉。
    /// <summary>空闲释放：停计时器 + 退订静态事件 + 清空卡片，避免整页被事件源拖住。</summary>
    public void Release()
    {
        StopTimers();
        try { AppState.NodesChanged -= UpdateDisplay; } catch { }
        try { ConnectionService.StateChanged -= UpdateDisplay; } catch { }
        try { SubCardList.Children.Clear(); } catch { }
    }

    public void StopTimers()
    {
        try { _usageTimer.Stop(); } catch { }
    }
}
