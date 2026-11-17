using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Neo.App.Services;
using Neo.App.Ui;
using Windows.Foundation;
using Windows.UI;

namespace Neo.App.Pages;

public sealed partial class SubsPage
{
    internal void OpenIntervalForReview()
    {
        if (NativeUiReview.Enabled) OnAutoUpdateIntervalTapped(AutoUpdateIntervalBox, null!);
    }
    internal object ReviewSubscriptionButtons()
    {
        if (!NativeUiReview.Enabled) throw new InvalidOperationException();
        var original = AppState.Subscriptions.Select(s => (Sub: s, s.Enabled)).ToArray();
        var nodes = AppState.Nodes.ToArray(); var selected = AppState.CurrentNode;
        var checks = new List<string>();
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException("Subscription button regression: " + label); checks.Add(label); }
        Button? Find(DependencyObject parent, string id)
        {
            if (parent is Button b && AutomationProperties.GetAutomationId(b) == "SubEnabled_" + id) return b;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) if (Find(VisualTreeHelper.GetChild(parent, i), id) is { } child) return child;
            return null;
        }
        try
        {
            Check(original.Length >= 3 && original.All(v => Find(SubCardList, v.Sub.Id) != null), "each subscription has a visible enable button");
            var first = original[0].Sub; var second = original[1].Sub;
            Check(first.Enabled && !second.Enabled, "fixture starts with one enabled subscription");
            OnSubscriptionEnabledClick(Find(SubCardList, second.Id)!, null!);
            Check(first.Enabled && second.Enabled && !original[2].Sub.Enabled, "enabling a second subscription preserves other subscriptions");
            Check(Find(SubCardList, second.Id)?.Content is "已启用", "button reflects the newly enabled state immediately");
            OnSubscriptionEnabledClick(Find(SubCardList, first.Id)!, null!);
            Check(!first.Enabled && second.Enabled && Find(SubCardList, first.Id)?.Content is "启用", "disabling one subscription preserves the other enabled subscription");
            return new { checks, count = checks.Count };
        }
        finally
        {
            foreach (var v in original) v.Sub.Enabled = v.Enabled;
            AppState.Nodes.Clear(); AppState.Nodes.AddRange(nodes); AppState.CurrentNode = selected;
            AppState.Save(); AppState.NotifyNodesChanged(); UpdateDisplay();
        }
    }
    private const double Gibibyte = 1024d * 1024 * 1024;
    private readonly List<Border> _trendColumns = new();
    private readonly List<(Visual Visual, bool Card)> _entryVisuals = new();
    private readonly long[] _trendValues = new long[30];
    private CompositionScopedBatch? _entryBatch;
    private bool _subscriptionEntered;
    private int _entryGeneration;
    private int _hoveredTrend = -1;
    private DateTime _trendDate;
    private string _trendSummary = "";
    private string? _subCardsSignature;
    private string? _lastQuotaId;
    private double _lastQuotaRatio;
    private Storyboard? _quotaMotion;
    private bool _themeRefreshPending;

    private void QueueThemeRefresh()
    {
        if (_themeRefreshPending) return;
        _themeRefreshPending = true;
        // The parent window's ActualTheme can still be the old value inside this event.
        // Resolve dynamic brushes after theme propagation finishes, in one deferred pass.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _themeRefreshPending = false;
            _trendBuilt = false; _subCardsSignature = null;
            UpdateDisplay();
        });
    }

    private void BindQuotaProgress(Grid track, Border fill, double ratio, string subscriptionId)
    {
        _quotaMotion?.Stop(); _quotaMotion = null;
        var previous = _lastQuotaId == subscriptionId ? _lastQuotaRatio : ratio;
        _lastQuotaId = subscriptionId; _lastQuotaRatio = ratio;
        track.SizeChanged += (_, e) =>
        {
            _quotaMotion?.Stop(); _quotaMotion = null;
            fill.Width = e.NewSize.Width * ratio;
        };
        fill.Loaded += (_, _) =>
        {
            var width = track.ActualWidth;
            fill.Width = width * ratio;
            if (Visibility != Visibility.Visible || !PrototypeMotion.Enabled || Math.Abs(previous - ratio) < .0001) return;
            var board = new Storyboard();
            PrototypeMotion.Add(board, fill, "Width", width * previous, width * ratio, 750,
                new KeySpline { ControlPoint1 = new Point(.2, .8), ControlPoint2 = new Point(.2, 1) });
            _quotaMotion = board;
            board.Completed += (_, _) => { if (ReferenceEquals(_quotaMotion, board)) { board.Stop(); _quotaMotion = null; } };
            board.Begin();
        };
    }

    internal void ResetEntryForReview()
    {
        if (!NativeUiReview.Enabled) throw new InvalidOperationException();
        _subscriptionEntered = false;
    }

    // subs-v1.js: first entry only, cards 220ms / 30ms stagger; bars 280ms / 7ms.
    // The source's countUp/renderQuota(true) functions are never invoked at entry.
    internal void OnPageShown()
    {
        if (!IsLoaded || Visibility != Visibility.Visible) return;
        _usageTimer.Start();
        UpdateDisplay();
        if (_subscriptionEntered) return;
        _subscriptionEntered = true;
        UpdateLayout();
        PaintTrend();
        if (!PrototypeMotion.Enabled) return;

        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        var ease = compositor.CreateCubicBezierEasingFunction(Vector2.Zero, new Vector2(.58f, 1));
        var generation = ++_entryGeneration;
        _entryBatch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        var cards = PrimarySubHost.Children.OfType<FrameworkElement>().Concat(new FrameworkElement[] { UsageCard, ScheduleCard }).ToArray();
        for (var i = 0; i < cards.Length; i++)
        {
            ElementCompositionPreview.SetIsTranslationEnabled(cards[i], true);
            var visual = ElementCompositionPreview.GetElementVisual(cards[i]);
            visual.Opacity = 1;
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            var opacity = compositor.CreateScalarKeyFrameAnimation();
            opacity.InsertKeyFrame(0, 0); opacity.InsertKeyFrame(1, 1, ease);
            opacity.Duration = TimeSpan.FromMilliseconds(220); opacity.DelayTime = TimeSpan.FromMilliseconds(i * 30);
            opacity.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            var move = compositor.CreateScalarKeyFrameAnimation();
            move.InsertKeyFrame(0, 6); move.InsertKeyFrame(1, 0, ease);
            move.Duration = opacity.Duration; move.DelayTime = opacity.DelayTime; move.DelayBehavior = opacity.DelayBehavior;
            visual.StartAnimation("Opacity", opacity); visual.StartAnimation("Translation.Y", move);
            _entryVisuals.Add((visual, true));
        }
        for (var i = 0; i < _monthBars.Count; i++)
        {
            var bar = _monthBars[i];
            var visual = ElementCompositionPreview.GetElementVisual(bar);
            visual.Scale = Vector3.One;
            visual.CenterPoint = new Vector3(0, (float)bar.ActualHeight, 0);
            var grow = compositor.CreateScalarKeyFrameAnimation();
            grow.InsertKeyFrame(0, 0); grow.InsertKeyFrame(1, 1, ease);
            grow.Duration = TimeSpan.FromMilliseconds(280); grow.DelayTime = TimeSpan.FromMilliseconds(i * 7);
            grow.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            visual.StartAnimation("Scale.Y", grow); _entryVisuals.Add((visual, false));
        }
        _entryBatch.Completed += (_, _) => { if (generation == _entryGeneration) StopEntryMotion(); };
        _entryBatch.End();
        _ = SettleEntryAsync(generation);
    }

    private async Task SettleEntryAsync(int generation)
    {
        // A covered/background window can suspend compositor completion notifications.
        // Finish the finite entry anyway; it must never replay when uncovered much later.
        await Task.Delay(620);
        if (generation == _entryGeneration) StopEntryMotion();
    }

    internal void OnPageHidden()
    {
        _usageTimer.Stop();
        _quotaMotion?.Stop(); _quotaMotion = null;
        StopEntryMotion();
        // Unloaded also runs after the desktop window has closed. Release
        // animation state then, but do not query that disposed window's theme.
        if (App.IsShuttingDown) _hoveredTrend = -1;
        else SetTrendHover(-1);
    }

    private void StopEntryMotion()
    {
        ++_entryGeneration;
        foreach (var (visual, card) in _entryVisuals)
        {
            if (card)
            {
                visual.StopAnimation("Opacity"); visual.StopAnimation("Translation.Y");
                visual.Opacity = 1; visual.Properties.InsertVector3("Translation", Vector3.Zero);
            }
            else { visual.StopAnimation("Scale.Y"); visual.Scale = Vector3.One; }
        }
        _entryVisuals.Clear();
        _entryBatch?.Dispose(); _entryBatch = null;
    }

    private void PaintTrend()
    {
        if (TrendMonthGrid == null) return;
        var themeChanged = !_trendBuilt;
        if (themeChanged)
        {
            StopEntryMotion();
            TrendMonthGrid.Children.Clear(); TrendMonthGrid.ColumnDefinitions.Clear();
            _monthBars.Clear(); _trendColumns.Clear();
            for (var i = 0; i < 30; i++)
            {
                var index = i;
                TrendMonthGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var bar = new Border { CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
                var content = new Grid(); content.Children.Add(bar);
                var column = new Border { CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Colors.Transparent),
                    Child = content, BackgroundTransition = new BrushTransition { Duration = TimeSpan.FromMilliseconds(120) } };
                if (i == 29)
                {
                    var today = new Border { Height = 2, CornerRadius = new CornerRadius(1), HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Bottom, RenderTransform = new TranslateTransform { Y = 6 }, Background = ThemeUtil.Get("NeoText3", Colors.Gray), IsHitTestVisible = false };
                    content.Children.Add(today);
                    column.SizeChanged += (_, e) => today.Width = e.NewSize.Width * .6;
                }
                column.PointerEntered += (_, _) => SetTrendHover(index);
                column.PointerExited += (_, _) => { if (_hoveredTrend == index) SetTrendHover(-1); };
                Grid.SetColumn(column, i); TrendMonthGrid.Children.Add(column);
                _monthBars.Add(bar); _trendColumns.Add(column);
            }
            _trendBuilt = true;
        }
        var days = UsageTracker.DailySeries(30);
        var height = TrendMonthGrid.ActualHeight > 0 ? TrendMonthGrid.ActualHeight : 88 * AdaptiveText.Scale;
        var subscription = AppState.Subscriptions.FirstOrDefault();
        var total = subscription == null ? 0 : AppState.SubUsage(subscription.SubUpload, subscription.SubDownload, subscription.SubTotal, subscription.TrafficInfo).Total;
        // Same quota/30 daily scale as the source; never normalize each day to a changing peak.
        var dailyQuota = total > 0 ? Math.Max(1, Math.Round(total / Gibibyte / 30, MidpointRounding.AwayFromZero)) * Gibibyte : Math.Max(1, days.Max());
        TrendScaleText.Text = total > 0 ? $"近 30 日 · 日均额度 {UsageTracker.Format((long)dailyQuota)}" : "近 30 日";
        _trendDate = DateTime.Today;
        for (var i = 0; i < _monthBars.Count; i++)
        {
            var size = Math.Clamp(days[i] / dailyQuota, 0, 1) * height;
            var changed = themeChanged || _trendValues[i] != days[i] || double.IsNaN(_monthBars[i].Height) || Math.Abs(_monthBars[i].Height - size) > .1;
            _trendValues[i] = days[i];
            AutomationProperties.SetName(_trendColumns[i], $"{_trendDate.AddDays(i - 29):M月d日}，{UsageTracker.Format(days[i])}");
            if (!changed) continue;
            _monthBars[i].Height = size;
            // Absolute coordinates anchor the whole color spectrum to chart height,
            // so short columns expose blue only and tall columns reach yellow/red.
            _monthBars[i].Background = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, size), EndPoint = new Point(0, size - height),
                GradientStops = {
                    new GradientStop { Offset = 0, Color = Hsl(205, .92, .72) },
                    new GradientStop { Offset = .34, Color = Hsl(205, .92, .48) },
                    new GradientStop { Offset = .44, Color = Hsl(44, .92, .74) },
                    new GradientStop { Offset = .68, Color = Hsl(44, .92, .52) },
                    new GradientStop { Offset = .78, Color = Hsl(8, .8, .60) },
                    new GradientStop { Offset = 1, Color = Hsl(8, .8, .44) }
                }
            };
        }
        _trendSummary = $"30 日合计 {UsageTracker.Format(days.Sum())}";
        RefreshTrendHint();
    }

    internal void SetTrendHover(int index)
    {
        if (_hoveredTrend >= 0 && _hoveredTrend < _trendColumns.Count)
            _trendColumns[_hoveredTrend].Background = new SolidColorBrush(Colors.Transparent);
        _hoveredTrend = index >= 0 && index < _trendColumns.Count ? index : -1;
        if (_hoveredTrend >= 0)
            _trendColumns[_hoveredTrend].Background = ThemeUtil.Get("NeoGlass2", Color.FromArgb(19, 255, 255, 255));
        RefreshTrendHint();
    }

    private void RefreshTrendHint()
    {
        TrendMonthHint.Text = _hoveredTrend < 0 ? _trendSummary
            : $"{_trendDate.AddDays(_hoveredTrend - 29):M/d} · {UsageTracker.Format(_trendValues[_hoveredTrend])}";
        TrendMonthHint.Foreground = ThemeUtil.Get(_hoveredTrend < 0 ? "NeoText3" : "NeoText2", Colors.Gray);
    }

    private static Color Hsl(double hue, double saturation, double lightness)
    {
        var a = saturation * Math.Min(lightness, 1 - lightness);
        byte Channel(double n) { var k = (n + hue / 30) % 12; return (byte)Math.Round(255 * (lightness - a * Math.Max(-1, Math.Min(k - 3, Math.Min(9 - k, 1))))); }
        return Color.FromArgb(255, Channel(0), Channel(8), Channel(4));
    }

    private static Color QuotaColor(double ratio)
    {
        ratio = Math.Clamp(ratio, 0, 1);
        double hue, light, saturation;
        if (ratio < .4) { hue = 205; saturation = .92; light = 70 - 32 * ratio / .4; }
        else if (ratio < .7) { hue = 44; saturation = .92; light = 78 - 34 * (ratio - .4) / .3; }
        else { hue = 6; saturation = .8; light = 72 - 28 * (ratio - .7) / .3; }
        return Hsl(hue, saturation, Math.Round(light / 4, MidpointRounding.AwayFromZero) * 4 / 100);
    }

    private async Task AnimateSubscriptionRemovalAsync(string id)
    {
        var row = SubCardList.Children.OfType<Border>().FirstOrDefault(item => Equals(item.Tag, id));
        if (row == null || Visibility != Visibility.Visible || !PrototypeMotion.Enabled) return;
        var board = new Storyboard();
        PrototypeMotion.Add(board, row, "Height", row.ActualHeight, 0, 220);
        PrototypeMotion.Add(board, row, "Opacity", 1, 0, 180);
        var completed = new TaskCompletionSource<bool>();
        board.Completed += (_, _) => completed.TrySetResult(true);
        board.Begin();
        await Task.WhenAny(completed.Task, Task.Delay(320));
        row.Visibility = Visibility.Collapsed; board.Stop();
    }
}
