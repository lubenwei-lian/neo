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
    private string _keyword = "";
    private bool _autoScroll = true;
    private const int MaxLogRows = 2000;
    private bool _loaded;
    private const int MaxPendingLines = 4000;
    private readonly object _pendingGate = new();
    private readonly Queue<LogEntry> _pending = new();
    private bool _flushRequested;
    private bool _released;
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


    private void OnLevelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        _level = (LevelFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "All";
        Rebuild();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loaded) return;
        _keyword = SearchBox.Text.Trim();
        Rebuild();
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
        lock (_pendingGate)
        {
            _pending.Clear();
            _flushRequested = false;
            _flush.Stop();
        }
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
        lock (_pendingGate)
        {
            if (App.IsShuttingDown || _released) return;
            // 显示缓冲与 LogBus 内存历史同样有界；文件日志仍由 LogBus 独立保存。
            if (_pending.Count >= MaxPendingLines) _pending.Dequeue();
            _pending.Enqueue(e);
            if (_flushRequested) return;
            _flushRequested = true;
        }
        // 整个突发只投递一次。计时器运行期间，新日志只入缓冲，不逐条唤醒 UI。
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            lock (_pendingGate)
            {
                if (App.IsShuttingDown || _released)
                {
                    _pending.Clear();
                    _flushRequested = false;
                    return;
                }
                if (!_flush.IsRunning) _flush.Start();
            }
        }))
        {
            lock (_pendingGate) _flushRequested = false;
        }
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

        List<LogEntry> batch;
        lock (_pendingGate)
        {
            if (_released || _pending.Count == 0)
            {
                _flush.Stop();
                _flushRequested = false;
                return;
            }
            batch = new List<LogEntry>(_pending);
            _pending.Clear();
            // 保持已唤醒标记；重复计时器在下一拍排空，空闲后才允许新的 UI 投递。
        }

        foreach (var e in batch)
        {
            if (Match(e)) LogList.Items.Add(new Neo.App.Ui.LogDisplayRow(e));
        }

        // 超容量整块修剪（保留最新 2000 条），修剪频率随批量稀释
        if (LogList.Items.Count > 2400)
            TrimList(MaxLogRows);

        UpdateEmpty();
        if (_autoScroll && LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[^1]);
    }

    private bool Match(LogEntry e) => (_level == "All" || e.Level == _level)
        && (_keyword.Length == 0 || e.Text.Contains(_keyword, StringComparison.OrdinalIgnoreCase));

    /// <summary>退出时停掉本页刷新计时器（Exit 后它会继续触发并写 XAML）。</summary>
    /// <summary>空闲释放：退订日志流 + 停刷新计时器（日志页的条目最多，释放收益明显）。</summary>
    public void Release()
    {
        try { LogBus.Appended -= OnAppended; } catch { }
        lock (_pendingGate)
        {
            _released = true;
            _pending.Clear();
            _flushRequested = false;
        }
        try { _flush.Stop(); } catch { }
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
        foreach (var e in keep) LogList.Items.Add(new Neo.App.Ui.LogDisplayRow(e));

        UpdateEmpty();
        if (_autoScroll && LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
    }

    private void UpdateEmpty()
    {
        EmptyHint.Visibility = LogList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = $"{LogList.Items.Count} 条记录";
        FileText.Text = "实时记录 · 完整历史可通过导出查看";
    }

    private void UpdateChips() => ChipScroll.IsChecked = _autoScroll;
}
