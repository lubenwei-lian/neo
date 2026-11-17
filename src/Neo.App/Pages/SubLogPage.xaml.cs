using System.Text;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Neo.App.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace Neo.App.Pages;

public sealed partial class SubLogPage : UserControl
{
    private readonly Microsoft.UI.Xaml.DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private List<SubscriptionLogEntry> _filtered = new();
    private string _resultFilter = "";
    private string _keyword = "";
    private bool _loaded;
    private bool _refreshing;

    public SubLogPage()
    {
        InitializeComponent();
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            if (!App.IsShuttingDown) UpdateHeader();
        };
        Loaded += (_, _) =>
        {
            if (!_loaded)
            {
                _loaded = true;
                SubscriptionLogService.EnsureTodayLoaded();
                SubscriptionLogService.Changed += OnLogChanged;
            }
            RefreshList();
        };
    }

    public void Release()
    {
        try { SubscriptionLogService.Changed -= OnLogChanged; } catch { }
        try { _statusTimer.Stop(); } catch { }
        _loaded = false;
    }

    public void StopTimers()
    {
        try { _statusTimer.Stop(); } catch { }
    }

    private void OnLogChanged()
    {
        if (App.IsShuttingDown) return;
        try
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!App.IsShuttingDown) RefreshList();
            });
        }
        catch { }
    }

    private void OnResultSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        _resultFilter = (LevelFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        RefreshList();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loaded) return;
        _keyword = (SearchBox.Text ?? "").Trim().ToLowerInvariant();
        RefreshList();
    }

    private void RefreshList()
    {
        if (_refreshing || App.IsShuttingDown) return;
        _refreshing = true;
        try
        {
            var all = SubscriptionLogService.Snapshot();
            _filtered = new List<SubscriptionLogEntry>();
            foreach (var e in all.OrderByDescending(x => x.StartTime))
            {
                if (_resultFilter.Length > 0 && !string.Equals(e.Result, _resultFilter, StringComparison.Ordinal))
                    continue;
                if (_keyword.Length > 0
                    && !e.Subscription.ToLowerInvariant().Contains(_keyword)
                    && !e.Host.ToLowerInvariant().Contains(_keyword))
                    continue;
                _filtered.Add(e);
            }

            LogList.ItemsSource = _filtered.Select(SubLogRow.From).ToList();
            UpdateHeader();
            EmptyHint.Visibility = _filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _refreshing = false; }
    }

    private void UpdateHeader()
    {
        CountText.Text = $"共 {_filtered.Count} 条";
        FileText.Text = "· " + SubscriptionLogService.TodayFileName;
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (_filtered.Count == 0) { ShowStatus("没有可复制的日志"); return; }
        try
        {
            var dp = new DataPackage();
            dp.SetText(SubscriptionLogService.FormatText(_filtered));
            Clipboard.SetContent(dp);
            Clipboard.Flush();
            ShowStatus($"已复制 {_filtered.Count} 条");
        }
        catch { ShowStatus("复制失败"); }
    }

    private async void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (_filtered.Count == 0 && SubscriptionLogService.Snapshot().Count == 0) return;
        var dlg = new ContentDialog
        {
            Title = "清空订阅日志",
            Content = "清空当前列表并删除今天的订阅日志文件。此操作不可撤销。",
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await Neo.App.Ui.DialogGuard.ShowAsync(dlg) != ContentDialogResult.Primary) return;
        SubscriptionLogService.Clear();
        RefreshList();
    }

    private async void OnExportTxtClick(object sender, RoutedEventArgs e)
        => await ExportAsync("txt");

    private async void OnExportCsvClick(object sender, RoutedEventArgs e)
        => await ExportAsync("csv");

    private async Task ExportAsync(string kind)
    {
        if (_filtered.Count == 0)
        {
            await ShowMessage("导出订阅日志", "当前没有可导出的日志。");
            return;
        }
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedFileName = $"subscription-log-{DateTime.Now:yyyyMMdd-HHmmss}",
            };
            if (kind == "csv")
                picker.FileTypeChoices.Add("CSV 表格", new List<string> { ".csv" });
            else
                picker.FileTypeChoices.Add("文本文件", new List<string> { ".txt" });
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance!));
            var file = await picker.PickSaveFileAsync();
            if (file == null) return;

            var text = kind == "csv"
                ? SubscriptionLogService.FormatCsv(_filtered)
                : SubscriptionLogService.FormatText(_filtered);
            System.IO.File.WriteAllBytes(file.Path, new UTF8Encoding(true).GetBytes(text));
            ShowStatus("已导出到 " + file.Path);
        }
        catch (Exception ex)
        {
            await ShowMessage("导出订阅日志", "导出失败：" + ex.Message);
        }
    }

    private async Task ShowMessage(string title, string message)
    {
        var dlg = new ContentDialog
        {
            Title = title,
            Content = Neo.App.Ui.AdaptiveText.WithSize(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, }, 12.5),
            CloseButtonText = "知道了",
            XamlRoot = XamlRoot,
        };
        await Neo.App.Ui.DialogGuard.ShowAsync(dlg);
    }

    private void ShowStatus(string text)
    {
        if (App.IsShuttingDown) return;
        FileText.Text = "· " + text;
        _statusTimer.Stop();
        _statusTimer.Start();
    }
}

/// <summary>订阅日志列表的展示行（由日志条目格式化而来）。</summary>
public sealed class SubLogRow
{
    public string TimeText { get; init; } = "";
    public string LevelText { get; init; } = "";
    public Brush BadgeBackground { get; init; } = new SolidColorBrush(Colors.Transparent);
    public string HeadText { get; init; } = "";
    public string DetailText { get; init; } = "";
    public Visibility DetailVisibility { get; init; } = Visibility.Collapsed;
    public Brush HeadBrush { get; init; } = new SolidColorBrush(Colors.Gray);

    public static SubLogRow From(SubscriptionLogEntry e)
    {
        var parts = new List<string>
        {
            e.StartTime.ToString("MM-dd HH:mm:ss"),
            e.Result,
            e.Subscription,
            e.Host,
            "HTTP " + e.HttpStatus,
            e.NodeCount is { } n ? n + " 节点" : "—",
            e.ElapsedMs is { } ms ? (ms / 1000.0).ToString("0.0") + "s" : "—",
        };

        var detail = new List<string>();
        if (e.EndTime is { } end && !e.Running)
            detail.Add("结束 " + end.ToString("HH:mm:ss"));
        if (e.Trigger.Length > 0) detail.Add("触发 " + e.Trigger);
        if (!string.IsNullOrWhiteSpace(e.Error)) detail.Add("错误：" + e.Error);
        if (!string.IsNullOrWhiteSpace(e.ParseError)) detail.Add("解析失败：" + e.ParseError);
        if (e.Placeholder.Length > 0 && e.Placeholder != "否" && e.Placeholder != "未判定")
            detail.Add("占位判定：" + e.Placeholder);

        var brush = e.Result switch
        {
            "成功" => ThemeUtil.Get("NeoGreen", Color.FromArgb(255, 0x16, 0xA3, 0x4A)),
            "失败" => ThemeUtil.Get("NeoRed", Color.FromArgb(255, 0xDC, 0x26, 0x26)),
            "占位" => ThemeUtil.Get("NeoYellow", Color.FromArgb(255, 0xD9, 0x77, 0x06)),
            _ => ThemeUtil.Get("NeoAccent", Color.FromArgb(255, 0x25, 0x63, 0xEB)),
        };

        return new SubLogRow
        {
            TimeText = e.StartTime.ToString("HH:mm:ss"),
            LevelText = e.Result,
            BadgeBackground = new SolidColorBrush(brush is SolidColorBrush solid ? Color.FromArgb(28, solid.Color.R, solid.Color.G, solid.Color.B) : Color.FromArgb(28, 90, 169, 255)),
            HeadText = string.Join(" · ", parts.Skip(3)),
            DetailText = "来源 · " + e.Subscription + (detail.Count > 0 ? " · " + string.Join(" · ", detail) : ""),
            DetailVisibility = Visibility.Visible,
            HeadBrush = brush,
        };
    }
}
