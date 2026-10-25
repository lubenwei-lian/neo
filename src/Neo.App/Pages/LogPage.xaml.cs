using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Neo.App.Services;
using Neo.Core;
using Windows.UI;

namespace Neo.App.Pages;

public sealed partial class LogPage : UserControl
{
    private string _level = "All";
    private bool _autoScroll = true;
    private const int MaxLogRows = 2000;
    private bool _loaded;
    private readonly List<LogEntry> _pending = new();
    private readonly DispatcherQueueTimer _flush;

    public LogPage()
    {
        InitializeComponent();
        LogBus.Appended += OnAppended;
        ActualThemeChanged += (_, _) => { UpdateChips(); Rebuild(); };
        _flush = DispatcherQueue.CreateTimer();
        _flush.Interval = TimeSpan.FromMilliseconds(150);
        _flush.Tick += (_, _) => FlushPending();
        Loaded += (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            UpdateChips();
            Rebuild();
        };
    }


    private void OnLevelClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag })
        {
            _level = tag;
            UpdateChips();
            Rebuild();
        }
    }

    private void OnAutoScrollClick(object sender, RoutedEventArgs e)
    {
        _autoScroll = !_autoScroll;
        UpdateChips();
        if (_autoScroll && LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        LogBus.Clear();
        Rebuild();
    }

    /// <summary>导出最近 3 天日志为 zip（主日志/内核输出/看门狗/订阅日志 + info.txt）。</summary>
    private async void OnExportLogsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedFileName = "neo-logs-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"),
            };
            picker.FileTypeChoices.Add("ZIP 压缩包", new List<string> { ".zip" });
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance!));
            var file = await picker.PickSaveFileAsync();
            if (file == null) return;

            var path = LogExportService.BuildZip(file.Path, 3);
            LogBus.App("日志已导出（最近 3 天）：" + path);
        }
        catch (Exception ex)
        {
            LogBus.Exception("导出日志", ex);
        }
    }

    private void OnOpenLogDirClick(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo { FileName = NeoPaths.LogDir, UseShellExecute = true }); }
        catch { }
    }


    private void OnAppended(LogEntry e)
    {
        // 退出中不再投递：DispatcherQueue 正在销毁，投递会以
        // RPC_E_WRONG_THREAD 的 fail-fast 终止进程（不可捕获）。
        if (App.IsShuttingDown) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (App.IsShuttingDown) return;
            // 高频日志逐条 Add+ScrollIntoView 会打爆 UI 线程并反复触发虚拟化重建，
            // 改为缓冲后 150ms 批量刷一次，滚动只在每批结束做一次。
            _pending.Add(e);
            if (!_flush.IsRunning) _flush.Start();
        });
    }

    private void FlushPending()
    {
        // 退出中必须立刻停表：本方法会写 XAML（LogList.Items / ScrollIntoView），
        // 而 Exit 之后 XAML 正在销毁 —— 这正是 fail-fast 的触发条件。
        // OnAppended 早有守卫，但 _flush 计时器一旦被启动，它的 Tick 会继续跑，
        // 所以守卫必须也放在这里（此前是漏的）。
        if (App.IsShuttingDown)
        {
            try { _flush.Stop(); } catch { }
            return;
        }

        if (_pending.Count == 0) { _flush.Stop(); return; }

        // 换出缓冲，避免刷的过程与入队互相穿插
        var batch = new List<LogEntry>(_pending);
        _pending.Clear();

        foreach (var e in batch)
        {
            if (Match(e)) LogList.Items.Add(e);
        }

        // 超容量整块修剪（保留最新 2000 条），修剪频率随批量稀释
        if (LogList.Items.Count > 2400)
            TrimList(MaxLogRows);

        UpdateEmpty();
        if (_autoScroll && LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[^1]);
    }

    private bool Match(LogEntry e) => _level == "All" || e.Level == _level;

    /// <summary>退出时停掉本页刷新计时器（Exit 后它会继续触发并写 XAML）。</summary>
    /// <summary>空闲释放：退订日志流 + 停刷新计时器（日志页的条目最多，释放收益明显）。</summary>
    public void Release()
    {
        try { LogBus.Appended -= OnAppended; } catch { }
        try { _flush.Stop(); } catch { }
        try { _pending.Clear(); } catch { }
    }

    public void StopTimers()
    {
        try { _flush.Stop(); } catch { }
    }

    /// 把列表裁到 limit 条（保留最新）。
    /// 旧写法 while(Count>2000) RemoveAt(0) 每条都要整体搬移 O(n) ⇒ O(n²) 全在 UI 线程。
    /// 这里改为「取出尾部 → Clear → 重新 Add」：一次重建，不再逐条搬移。
    private void TrimList(int limit)
    {
        int over = LogList.Items.Count - limit;
        if (over <= 0) return;

        var keep = new object[limit];
        for (int i = 0; i < limit; i++) keep[i] = LogList.Items[over + i];

        LogList.Items.Clear();
        foreach (var item in keep) LogList.Items.Add(item);
    }

    private void Rebuild()
    {
        //   要等下一次 flush 才裁，且裁的时候还是逐条 RemoveAt(0)）。
        var keep = new List<LogEntry>();
        foreach (var e in LogBus.Snapshot())
        {
            if (Match(e))
                keep.Add(e);
        }
        if (keep.Count > MaxLogRows)
            keep.RemoveRange(0, keep.Count - MaxLogRows);

        LogList.Items.Clear();
        foreach (var e in keep) LogList.Items.Add(e);

        UpdateEmpty();
        if (_autoScroll && LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
    }

    private void UpdateEmpty()
        => EmptyHint.Visibility = LogList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateChips()
    {
        Apply(ChipAll, _level == "All");
        Apply(ChipDebug, _level == "Debug");
        Apply(ChipInfo, _level == "Info");
        Apply(ChipWarn, _level == "Warning");
        Apply(ChipError, _level == "Error");
        Apply(ChipScroll, _autoScroll);
    }

    private static void Apply(Button chip, bool on)
    {
        var accent = ThemeUtil.Get("NeoAccent", Color.FromArgb(255, 0x25, 0x63, 0xEB));
        var onText = ThemeUtil.Get("NeoOnAccent", Colors.White);
        var text2 = ThemeUtil.Get("NeoText2", Color.FromArgb(255, 0x6B, 0x72, 0x80));
        chip.Background = on ? accent : new SolidColorBrush(Colors.Transparent);
        chip.Foreground = on ? onText : text2;
        chip.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
    }
}
