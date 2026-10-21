using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Neo.App.Services;
using Neo.App.Ui;
using Neo.Core;
using Neo.Core.Core;
using Neo.Core.Models;
using Windows.UI;

namespace Neo.App.Pages;

public sealed partial class NodesPage : UserControl
{
    private List<NodeTile> _tiles = new();
    private NodeTile? _selected;
    private bool _realTesting;
    private CancellationTokenSource? _realCts;
    private int _lastCols;
    private DateTime _lastLayoutWidthApply = DateTime.MinValue;

    /// 显示的是「当前时间」，并非测速时间 —— 界面上的「上次真连测速 HH:mm:ss」因此永远等于现在。
    /// null = 本进程内还没跑过真连测速（节点上的延迟可能来自 DelayCache 缓存）。
    private DateTime? _lastRealTestAt;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _resizeTrailTimer;

    public NodesPage()
    {
        InitializeComponent();
        AppState.NodesChanged += RebuildList;
        AppState.UiSettingsChanged += OnUiSettingsChanged;
        Loaded += (_, _) =>
        {
            RebuildList();
            try { Ui.Ripple.Attach(RealTestBtn, App.MainWindowInstance?.RippleLayerHost); } catch { }
            TryAutoDetectLocations();
        };
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

    /// 「真实 IP 显示」等界面设置变化时：重建卡片，并补一次出口检测（若还没测过）。
    private void OnUiSettingsChanged()
    {
        RebuildList();
        TryAutoDetectLocations();
    }

    private void RebuildList()
    {
        if (_realTesting) return;

        var nodes = AppState.Nodes
            .OrderBy(n => n.RealDelay <= 0 ? int.MaxValue : n.RealDelay)
            .ToList();

        _tiles = nodes.Select(n => new NodeTile(n)).ToList();
        _selected = null;
        ApplyFilter();
        UpdateFooter();
    }

    private void ApplyFilter()
    {
        var q = SearchBox.Text.Trim().ToLowerInvariant();
        var list = q.Length == 0 ? _tiles : _tiles.Where(t => t.SearchText.Contains(q)).ToList();

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
        await RunBatchAsync(list);
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
            var nodes = AppState.Nodes.ToList();
            if (nodes.Count == 0) return;
            if (nodes.All(n => !string.IsNullOrEmpty(n.ExitCountry))) return;

            LogBus.App("真实 IP 显示已开启：后台检测节点出口位置…");
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
            var ok = AppState.Nodes.Count(n => !string.IsNullOrEmpty(n.ExitCountry));
            RebuildList();
            LogBus.App($"真实 IP 显示：出口检测完成 {ok}/{nodes.Count}（超时的节点保持未检测）");
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

    private async Task RunBatchAsync(List<NodeTile> all)
    {
        if (all.Count == 0) return;
        if (_realTesting) return;
        _realTesting = true;
        bool canceled = false;
        _realCts = new CancellationTokenSource();
        RealTestBtnText.Text = "停止";

        var sw = System.Diagnostics.Stopwatch.StartNew();
        LogBus.App($"真连测速开始（{all.Count} 个节点 · 单实例批量）");
        try
        {
            var tileMap = all.GroupBy(t => t.Node).ToDictionary(g => g.Key, g => g.First());
            var done = 0;
            var map = await RealDelayTester.RealPingBatchAsync(
                all.Select(t => t.Node).ToList(), NeoPaths.RuntimeDir,
                // 优化：超时从 10s 收到 7s —— 真连超过 7s 的节点实际体验已不可用，
                // 继续等 10s 只会拖慢整批（批量是并行的，最后一名决定总耗时）。
                timeoutMs: 7000,
                onResult: (node, ms) => _ = DispatcherQueue.TryEnqueue(() =>
                {
                    if (App.IsShuttingDown) return;
                    if (tileMap.TryGetValue(node, out var tile))
                    {
                        tile.SetResult(ms);
                        done++;
                        FootTime.Text = $"真连测速中 {done}/{all.Count}…";
                        RealTestBtnText.Text = $"停止 {done}/{all.Count}";
                        FootAvg.Text = "";
                    }
                }),
                ct: _realCts.Token);

            // 批量里超时（-1）/没测到的节点用更长超时单独重试一次：
            //   订阅里确实有死节点，但「其实能用、只是很慢」的节点会被 7s 批量超时误判。
            var retry = all.Where(t => t.RealValue <= 0).ToList();
            if (retry.Count > 0 && _realCts is { IsCancellationRequested: false })
            {
                LogBus.App($"真连测速：批量超时 {retry.Count} 个，正在单独重试（12s 超时）…");
                try
                {
                    await Parallel.ForEachAsync(retry,
                        new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = _realCts.Token },
                        async (tile, ict) =>
                        {
                            var ms = await RealDelayTester.RealPingAsync(
                                tile.Node, NeoPaths.RuntimeDir, timeoutMs: 12000, ct: ict);
                            _ = DispatcherQueue.TryEnqueue(() =>
                            {
                                if (!App.IsShuttingDown) tile.SetResult(ms);
                            });
                        });
                }
                catch (OperationCanceledException) { }
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
        RebuildList();
    }

    private void UpdateFooter()
    {
        NodeCountRun.Text = $"共 {AppState.Nodes.Count} 个节点 · 按真连延迟排序";

        var ok = AppState.Nodes.Where(n => n.RealDelay > 0).ToList();
        if (ok.Count > 0)
        {
            FootTime.Text = _lastRealTestAt is { } t
                ? "上次真连测速 " + t.ToString("HH:mm:ss")
                : "真连测速时间未知（延迟来自缓存）";
            FootAvg.Text = $"· 平均真连 {ok.Sum(n => n.RealDelay) / ok.Count} ms";
        }
        else
        {
            FootTime.Text = "真连尚未测速";
            FootAvg.Text = "";
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
