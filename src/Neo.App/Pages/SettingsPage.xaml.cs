using System.Diagnostics;
using System.IO;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using Neo.App.Services;
using Neo.Core;
using Neo.Core.Core;
using Windows.UI;

namespace Neo.App.Pages;

public sealed partial class SettingsPage : UserControl
{
    // 不再有点击主题分段的处理函数，也不再对外抛 ThemeRequested 事件
    //（留着它会因"从不触发"产生 CS0067 警告）。MainWindow 侧的订阅也已同步删除。
    public event EventHandler? OpenRulesRequested;

    private bool _syncing;
    private bool _checkingUpdate;
    private bool _checkingSingBoxUpdate;

    private Neo.App.Ui.PopupList? _langList;
    private Neo.App.Ui.PopupList? _logLevelList;
    private Neo.App.Ui.PopupList? _fixedExitNodeList;
    private Neo.App.Ui.PopupList? _fixedExitEntryList;
    private Neo.App.Ui.PopupList? _dnsList;

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static readonly (string Key, string Label)[] LogLevels =
    {
        ("none", "关闭"),
        ("error", "错误"),
        ("warning", "警告"),
        ("info", "信息"),
        ("debug", "调试"),
    };

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshState();
        ConnectionService.StateChanged += RefreshState;
    }

    public void RefreshFromState() => RefreshState();

    /// 退出时落盘一次，保证最后一次改动已持久化（原玻璃滑杆的防抖计时器已随设置项移除）。
    public void StopTimers()
    {
        try { AppState.Save(); } catch { }
    }

    private static bool IsAutoStartOn()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return k?.GetValue("neo") is string s && s.Length > 0;
        }
        catch { return false; }
    }

    private void OnAutoStartToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (SwitchAutoStart.IsOn)
            {
                var exe = NeoPaths.LauncherPath;
                k?.SetValue("neo", $"\"{exe}\"");
            }
            else
            {
                k?.DeleteValue("neo", throwOnMissingValue: false);
            }
        }
        catch { }
    }

    private void OnAutoConnectToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        AppState.State.AutoConnect = SwitchAutoConnect.IsOn;
        AppState.Save();
    }

    private void OnStartupSpeedTestToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        AppState.State.StartupSpeedTest = SwitchStartupSpeedTest.IsOn;
        AppState.Save();
    }

    private void OnShowRealIpToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        AppState.State.ShowRealIp = SwitchShowRealIp.IsOn;
        AppState.Save();
        // 节点卡片要按开关重建（显示/隐藏真实出口行）
        AppState.NotifyUiSettingsChanged();
    }

    private void OnMinTrayToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        AppState.State.MinimizeToTray = SwitchMinTray.IsOn;
        AppState.Save();
    }


    private void OnCompactToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        AppState.State.CompactLayout = SwitchCompact.IsOn;
        AppState.Save();
        AppState.NotifyUiSettingsChanged();
    }

    private void OnLangTapped(object sender, RoutedEventArgs e)
    {
        _langList ??= new Neo.App.Ui.PopupList();
        _langList.ClearItems();
        _langList.AddItem("简体中文", null, () => SetLanguage("zh-CN"));
        _langList.AddDisabled("English（翻译中，敬请期待）");
        _langList.AddSeparator();
        _langList.AddDisabled("当前仅提供简体中文");
        ShowListBelow(_langList, sender, LangBox);
    }

    private void SetLanguage(string code)
    {
        if (string.Equals(AppState.State.Language, code, StringComparison.OrdinalIgnoreCase)) return;
        AppState.State.Language = code;
        AppState.Save();
        UpdateLangUi();
    }

    private void UpdateLangUi()
    {
        LangText.Text = AppState.State.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? "English" : "简体中文";
    }

    private void OnFailoverToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        AppState.State.FailoverEnabled = SwitchFailover.IsOn;
        AppState.Save();
        LogBus.App(SwitchFailover.IsOn ? "自动故障转移：已开启" : "自动故障转移：已关闭");
    }

    private void OnAutoRestartCoreToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        AppState.State.AutoRestartCore = SwitchAutoRestartCore.IsOn;
        AppState.Save();
        LogBus.App(SwitchAutoRestartCore.IsOn ? "内核异常退出后自动重启：已开启" : "内核异常退出后自动重启：已关闭");
    }

    private async void OnRestoreSafeModeClick(object sender, RoutedEventArgs e)
    {
        var st = AppState.State;
        st.UseTun = st.SafeModePrevTun;
        st.FixedExitEnabled = st.SafeModePrevFixedExit;
        st.SafeModeAt = "";
        AppState.Save();
        LogBus.App("已恢复安全模式前的设置（TUN / 固定出口）");
        IncidentLog.Record("startup", "用户恢复了安全模式前的设置");
        RefreshFromState();
        if (ConnectionService.IsConnected)
        {
            try { await ConnectionService.ReloadForSettingsAsync(); } catch { }
        }
    }

    private void RefreshSafeModeRow()
    {
        var at = AppState.State.SafeModeAt ?? "";
        SafeModeRow.Visibility = at.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (at.Length > 0)
            SafeModeHint.Text = $"上次启动失败（{at}）时已临时关闭 TUN / 固定出口，可一键恢复";
    }

    private void OnAutoRepairProxyToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        AppState.State.AutoRepairSystemProxy = SwitchAutoRepairProxy.IsOn;
        AppState.Save();
        LogBus.App(SwitchAutoRepairProxy.IsOn ? "自动修复系统代理：已开启" : "自动修复系统代理：已关闭");
    }

    // ---- 固定出口（多入口加速）----
    // 出口固定为一个非 REALITY 节点，其他节点作为入口链路给它叠加带宽。
    // 说明：xray 的 REALITY 不能作内层链路（实测报 received real certificate），
    // 所以出口候选只列 ws/tls 节点；入口不受限制。
    private void OnFixedExitToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        bool on = SwitchFixedExit.IsOn;
        AppState.State.FixedExitEnabled = on;
        LogBus.App(on ? "固定出口：已开启" : "固定出口：已关闭");
        AppState.Save();
        UpdateFixedExitUi();
        if (on && AppState.State.UseTun)
            LogBus.App("固定出口：当前是 TUN 模式，该功能只在非 TUN（xray）下生效", "Warning");
        _ = ReloadForFixedExitAsync();
    }

    private async Task ReloadForFixedExitAsync()
    {
        if (!ConnectionService.IsConnected) return;
        try { await ConnectionService.ReloadForSettingsAsync(); } catch { }
    }

    private void OnFixedExitNodeTapped(object sender, TappedRoutedEventArgs e)
    {
        _fixedExitNodeList ??= new Neo.App.Ui.PopupList();
        _fixedExitNodeList.ClearItems();
        _fixedExitNodeList.AddItem("自动（最快）", null, async () =>
        {
            AppState.State.FixedExitExitKey = "";
            AppState.Save();
            UpdateFixedExitUi();
            LogBus.App("固定出口：出口节点改为自动选择");
            await ReloadForFixedExitAsync();
        });
        foreach (var n in AppState.Nodes.Where(XrayConfigBuilder.CanBeFixedExit).Take(30))
        {
            var node = n;
            var key = AppState.NodeKey(node);
            _fixedExitNodeList.AddItem(node.Name, null, async () =>
            {
                AppState.State.FixedExitExitKey = key;
                AppState.Save();
                UpdateFixedExitUi();
                LogBus.App("固定出口：出口已设为「" + node.Name + "」");
                await ReloadForFixedExitAsync();
            });
        }
        _fixedExitNodeList.AddDisabled("出口必须是 ws/tls 等非 REALITY 节点：REALITY 不能作为内层链路");
        ShowListBelow(_fixedExitNodeList, sender, FixedExitNodeBox);
    }

    private void OnFixedExitEntryTapped(object sender, TappedRoutedEventArgs e)
    {
        _fixedExitEntryList ??= new Neo.App.Ui.PopupList();
        _fixedExitEntryList.ClearItems();
        foreach (var v in new[] { 3, 6, 8 })
        {
            var val = v;
            _fixedExitEntryList.AddItem(val + " 个", null, async () =>
            {
                AppState.State.FixedExitEntryCount = val;
                AppState.Save();
                UpdateFixedExitUi();
                LogBus.App("固定出口：入口数量设为 " + val);
                await ReloadForFixedExitAsync();
            });
        }
        _fixedExitEntryList.AddItem("全部", null, async () =>
        {
            AppState.State.FixedExitEntryCount = 0;
            AppState.Save();
            UpdateFixedExitUi();
            LogBus.App("固定出口：入口使用全部可用节点");
            await ReloadForFixedExitAsync();
        });
        _fixedExitEntryList.AddDisabled("入口越多总带宽越高，但每条连接仍只走一条链路；上限 8 条（太多容易被商家限制）");
        ShowListBelow(_fixedExitEntryList, sender, FixedExitEntryBox);
    }

    private void UpdateFixedExitUi()
    {
        var cands = AppState.Nodes.Where(XrayConfigBuilder.CanBeFixedExit).ToList();
        var key = AppState.State.FixedExitExitKey ?? "";
        var node = key.Length > 0 ? cands.FirstOrDefault(n => AppState.NodeKey(n) == key) : null;
        FixedExitNodeText.Text = node?.Name ?? "自动";
        FixedExitNodeHint.Text = node != null
            ? "手动指定；出口 IP 固定，不随入口变化"
            : "自动：从 " + cands.Count + " 个非 REALITY 节点里挑最快的";
        var c = AppState.State.FixedExitEntryCount;
        FixedExitEntryText.Text = c <= 0 ? "全部" : c + " 个";

        bool fx = AppState.State.FixedExitEnabled;
        FixedExitHint.Text = fx
            ? "已开启：所有流量固定从「出口节点」出去，其他节点只负责帮它加速。流量按两跳计。"
            : "打开后：流量固定从一个节点出去，其他节点帮它加速；出口只支持 ws/tls 节点，流量按两跳计。";

        // 用不上的子选项直接灰掉，避免用户以为改了会生效
        SetRowEnabled(FixedExitNodeRow, fx);
        SetRowEnabled(FixedExitEntryRow, fx);
    }

    private static void SetRowEnabled(FrameworkElement row, bool on)
    {
        // Grid/Panel 没有 IsEnabled（那是 Control 的成员），
        // 用 IsHitTestVisible 挡住点击 + 透明度表达“这项现在用不上”。
        row.IsHitTestVisible = on;
        row.Opacity = on ? 1.0 : 0.45;
    }

    private bool _checkingAppUpdate;
    private bool _downloadingUpdate;
    private AppUpdateService.UpdateInfo? _pendingUpdate;

    public void NotifyUpdateAvailable(AppUpdateService.UpdateInfo info)
    {
        _pendingUpdate = info;
        BtnDownloadUpdate.Visibility = Visibility.Visible;
        var isNewBuild = (info.Notes ?? "").Contains("新构建", StringComparison.Ordinal);
        UpdateStatusText.Text = $"发现{(isNewBuild ? "同版本新构建" : "新版本")} {info.LatestTag}（当前 {AppUpdateService.CurrentVersion}）";
    }

    // B方案：自动检查发现新版本时弹公告对话框，展示更新说明并允许直接下载更新。
    // 这是**后台自动**触发的弹窗（30 分钟定时器），必须用 TryShowAsync 而非排队：
    // 用户此刻可能正开着别的对话框（添加订阅 / 切换节点确认…），若排队就会在用户
    // 处理完手头的事之后突然冒出一个更新公告，观感很突兀；直接跳过更合适 ——
    // NotifyUpdateAvailable 已经让设置页显示出「发现新版本」和下载按钮，用户主动去点即可。
    public async Task PromptAutoUpdateAsync(AppUpdateService.UpdateInfo info)
    {
        NotifyUpdateAvailable(info);
        var notes = (info.Notes ?? "").Trim();
        if (notes.Length > 600) notes = notes[..600] + "…";
        var extra = string.IsNullOrEmpty(notes) ? "" : $"\n\n更新说明：\n{notes}";
        var dlg = new ContentDialog
        {
            Title = (info.Notes ?? "").Contains("新构建", StringComparison.Ordinal) ? "发现新构建" : "发现新版本",
            Content = new TextBlock
            {
                Text = $"neo {info.LatestTag} 已发布（当前 {AppUpdateService.CurrentVersion}）。{extra}",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5,
            },
            PrimaryButtonText = "下载并更新",
            CloseButtonText = "稍后",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        var (shown, result) = await Neo.App.Ui.DialogGuard.TryShowAsync(dlg);
        if (!shown)
        {
            // 已有对话框打开：不打断用户。设置页已显示新版本提示，用户可自行点下载。
            LogBus.App($"发现新版本 {info.LatestTag}，但当前有对话框打开，已在设置页提示（未弹窗）");
            return;
        }
        if (result == ContentDialogResult.Primary)
            await StartUpdateDownloadAsync();
    }

    private void RefreshUpdateStatusHint()
    {
        var last = AppState.State.LastUpdateCheckUnix;
        if (last > 0)
        {
            var local = DateTimeOffset.FromUnixTimeSeconds(last).LocalDateTime;
            UpdateStatusText.Text = $"上次检查：{local:MM-dd HH:mm}";
        }
        else
        {
            UpdateStatusText.Text = "从 GitHub 获取最新版本";
        }
    }

    private async Task ShowSettingsInfo(string title, string message)
    {
        var dlg = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 },
            CloseButtonText = "知道了",
            XamlRoot = XamlRoot,
        };
        await Neo.App.Ui.DialogGuard.ShowAsync(dlg);
    }

    /// 统一切换「检查中」的 UI 状态：转圈显示/隐藏 + 按钮禁用 + 状态文案。
    /// 原先只有按钮禁用 + 文字变化，用户看不出程序在跑，还以为是没反应。
    private void SetCheckingUi(ProgressRing ring, Button btn, string? statusText, bool checking)
    {
        try
        {
            ring.IsActive = checking;
            ring.Visibility = checking ? Visibility.Visible : Visibility.Collapsed;
            btn.IsEnabled = !checking;
            if (statusText != null) UpdateStatusText.Text = statusText;
        }
        catch { }
    }

    private async void OnCheckAppUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_checkingAppUpdate) return;
        _checkingAppUpdate = true;
        SetCheckingUi(RingAppUpdate, BtnCheckAppUpdate, "正在检查…", true);
        try
        {
            var r = await AppUpdateService.CheckAsync();
            AppState.State.LastUpdateCheckUnix = DateTimeOffset.Now.ToUnixTimeSeconds();
            AppState.Save();

            if (!r.Ok)
            {
                UpdateStatusText.Text = "检查失败";
                await ShowSettingsInfo("检查更新",
                    $"无法连接 GitHub（{r.Error}）。\n\n" +
                    "已自动尝试「当前代理 → 系统代理 → 直连」三条路径。\n" +
                    "若三条都不通，请检查代理是否可用，或稍后重试。");
                return;
            }

            if (!r.HasNew)
            {
                _pendingUpdate = null;
                BtnDownloadUpdate.Visibility = Visibility.Collapsed;
                UpdateStatusText.Text = $"已是最新版本（{AppUpdateService.CurrentVersion}）";
                return;   // 结果直接显示在卡片里，不再弹窗打断
            }

            _pendingUpdate = r;
            BtnDownloadUpdate.Visibility = Visibility.Visible;
            UpdateStatusText.Text = $"发现{((r.Notes ?? "").Contains("新构建", StringComparison.Ordinal) ? "同版本新构建" : "新版本")} {r.LatestTag}（当前 {AppUpdateService.CurrentVersion}）";
        }
        finally
        {
            _checkingAppUpdate = false;
            SetCheckingUi(RingAppUpdate, BtnCheckAppUpdate, null, false);
        }
    }

    private async void OnDownloadUpdateClick(object sender, RoutedEventArgs e)
        => await StartUpdateDownloadAsync();

    private async Task StartUpdateDownloadAsync()
    {
        if (_downloadingUpdate) return;
        if (_pendingUpdate is not { HasNew: true } u || string.IsNullOrEmpty(u.AssetUrl)) return;
        _downloadingUpdate = true;
        try
        {
            var prog = new ProgressBar { Minimum = 0, Maximum = 1, Width = 320 };
            var txt = new TextBlock { Text = "正在从 GitHub 下载更新包…", FontSize = 12, TextWrapping = TextWrapping.Wrap };
            var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
            panel.Children.Add(txt);
            panel.Children.Add(prog);

            var cts = new CancellationTokenSource();
            var dlg = new ContentDialog
            {
                Title = "下载更新",
                Content = panel,
                CloseButtonText = "取消",
                XamlRoot = XamlRoot,
            };
            dlg.CloseButtonClick += (_, _) => cts.Cancel();

            var showTask = Neo.App.Ui.DialogGuard.ShowAsync(dlg);
            string? path = null;

            // 关键：必须用 try/finally 保证 dlg.Hide() 一定执行。
            // DialogGuard 的门闩由「对话框关闭」释放；只要有一条异常路径跳过 Hide()，
            // 这个对话框就永远开着、门闩永远不释放 → 此后**全 app 所有**走 DialogGuard
            // 的弹窗（添加订阅 / 删除确认 / 手动节点 / 切换节点 / TUN 提示 / 反馈…）
            // 全部永久挂起，用户看到「点什么都还是没反应」。
            // （原先这里只 catch OperationCanceledException，而 DownloadAsync 里
            try
            {
                //   全用命名参数，避免服务侧参数顺序调整时调用点错位。
                path = await Task.Run(() => AppUpdateService.DownloadAsync(
                    assetUrl: u.AssetUrl,
                    assetName: u.AssetName,
                    progress: p => DispatcherQueue.TryEnqueue(() =>
                    {
                        if (App.IsShuttingDown) return;
                        prog.Value = p;
                    }),
                    expectedSize: u.AssetSize,
                    ct: cts.Token));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogBus.Exception("下载更新包失败", ex);
            }
            finally
            {
                try { dlg.Hide(); } catch { }
            }
            //   但「不 await 完成」这一半是真问题，我确认并在此修）：
            //   原写法 `_ = showTask;` 表示「丢弃这个 Task、不关心它何时结束」。
            //   而 DialogGuard.ShowAsync 的 finally 才会 `Gate.Release()` ——
            //   上面执行的 `dlg.Hide()` 只是让对话框视觉关闭，门闩的释放**要等 showTask 跑完**。
            //   两者之间存在时间差：若紧接着调用另一个 DialogGuard.ShowAsync，
            //   它会卡在门闩上并最多等 8 秒（GateTimeoutMs），超时后被"强制放行"
            //   → 两个对话框并发显示 → 正是 DialogGuard 要防的 0x80000019。
            //   现在改为「等它结束」，消除该时间差。用 try/catch 包住：
            //   ShowAsync 内部已有 finally 保 Release，这里只需吞掉可能的异常
            //   （如 XamlRoot 失效），不影响后续流程。最多等 8 秒（内部上限），不阻塞界面。
            try { await showTask.ConfigureAwait(true); } catch { }

            if (path == null)
            {
                await ShowSettingsInfo("下载失败", "无法下载更新包。\n\n直连不通时请先开启代理，然后再重试。");
                return;
            }

            // 文件可能已被清理/被杀软删掉：这里也要容错，否则会在 async void 路径上逃逸。
            double sizeMb;
            try { sizeMb = new System.IO.FileInfo(path).Length / 1048576.0; }
            catch (Exception ex)
            {
                LogBus.Exception("读取更新包大小失败", ex);
                await ShowSettingsInfo("安装更新", "更新包已下载但无法读取（可能被清理或拦截），请重试。");
                return;
            }

            var confirm = new ContentDialog
            {
                Title = "安装更新",
                Content = new TextBlock
                {
                    Text = $"新版本 {u.LatestTag} 已下载完成（{sizeMb:F0} MB）。\n\n点「立即更新」后 neo 将自动关闭并开始安装；安装器的 [Run] 项会尝试重新启动 neo，若未自动出现，请手动打开。",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12.5,
                },
                PrimaryButtonText = "立即更新",
                CloseButtonText = "稍后",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };
            var res = await Neo.App.Ui.DialogGuard.ShowAsync(confirm);
            if (res == ContentDialogResult.Primary)
            {
                AppUpdateService.LaunchInstallerAndExit(path);
                App.MainWindowInstance?.ExitForUpdate();
            }
        }
        finally { _downloadingUpdate = false; }
    }

    private void OnAutoCheckUpdateToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        AppState.State.AutoCheckUpdate = SwitchAutoCheckUpdate.IsOn;
        AppState.Save();
    }

    private void OnUpdateTimeChanged(object sender, TimePickerValueChangedEventArgs args)
    {
        if (_syncing) return;
        AppState.State.UpdateCheckTime = $"{UpdateTimePicker.Time.Hours:00}:{UpdateTimePicker.Time.Minutes:00}";
        AppState.Save();
    }

    private void OnLogLevelTapped(object sender, TappedRoutedEventArgs e)
    {
        _logLevelList ??= new Neo.App.Ui.PopupList();
        _logLevelList.ClearItems();
        foreach (var (key, label) in LogLevels)
        {
            var k = key;
            _logLevelList.AddItem(label, null, async () =>
            {
                if (AppState.State.LogLevel == k) return;
                AppState.State.LogLevel = k;
                AppState.Save();
                LogLevelText.Text = label;
                await ConnectionService.ReloadForSettingsAsync();
            });
        }
        ShowListBelow(_logLevelList, sender, LogLevelBox);
    }

    /// 把下拉挂在被点元素正下方。传 sender 优先 —— 它是真正被点的那个控件，
    /// 用它定位比用命名的 Box 更贴合「点哪弹哪」；取不到才退回命名元素。
    private static void ShowListBelow(Neo.App.Ui.PopupList list, object? sender, FrameworkElement fallback)
    {
        var fe = sender as FrameworkElement ?? fallback;
        Neo.App.Ui.PopupList.ShowBelow(list, fe, fe.ActualWidth > 0 ? fe.ActualWidth : 260);
    }

    //   旧实现一个 OnAdvancedToggled 同时写 BypassCn 与 AllowLan —— 拨「允许局域网连接」时
    //   会顺带把 SwitchBypass 的**过期显示值**写回 BypassCn，静默回滚规则页/设置页的改动。
    private void OnBypassCnToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        AppState.State.BypassCn = SwitchBypass.IsOn;
        SaveAdvancedSettings();
    }

    private void OnAllowLanToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        AppState.State.AllowLan = SwitchLan.IsOn;
        SaveAdvancedSettings();
    }

    private void SaveAdvancedSettings()
    {
        AppState.Save();
        UpdateRuleSetCount();
        _ = ConnectionService.ReloadForSettingsAsync();
    }

    private void OnGameDirectToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        AppState.State.GameDirectEnabled = SwitchGameDirect.IsOn;
        AppState.Save();
        _ = ConnectionService.ReloadForSettingsAsync();
    }

    private void RefreshGameDirectSummary()
    {
        var custom = AppState.State.GameDirectProcesses
            .Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        GameDirectProcSummary.Text = custom.Count == 0
            ? "内置已覆盖 PUBG/CS2/L4D2/NTE 等"
            : $"内置 PUBG/CS2/L4D2/NTE 等 + 自定义 {custom.Count} 个";
    }

    private async void OnEditGameProcessesClicked(object sender, RoutedEventArgs e)
    {
        var box = new TextBox
        {
            AcceptsReturn = true,
            Height = 220,
            Text = string.Join(Environment.NewLine, AppState.State.GameDirectProcesses),
            PlaceholderText = "每行一个进程名，例如：MyGame.exe",
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
        };
        var panel = new StackPanel { Spacing = 8 };
        var hint = new TextBlock
        {
            Text = "内置已覆盖：TslGame.exe（PUBG）、cs2.exe、left4dead2.exe、HTGame.exe（NTE）等，无需重复填写。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(hint);
        panel.Children.Add(box);
        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = "自定义游戏进程（每行一个 exe 名）",
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await Neo.App.Ui.DialogGuard.ShowAsync(dialog) == ContentDialogResult.Primary)
        {
            AppState.State.GameDirectProcesses = box.Text
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            AppState.Save();
            RefreshGameDirectSummary();
            _ = ConnectionService.ReloadForSettingsAsync();
        }
    }

    private static readonly (string Value, string Label)[] DnsPresets =
    {
        ("223.5.5.5", "阿里公共 DNS"),
        ("119.29.29.29", "腾讯 DNSPod"),
        ("1.1.1.1", "Cloudflare"),
        ("8.8.8.8", "Google"),
    };

    private void OnDnsTapped(object sender, RoutedEventArgs e)
    {
        _dnsList ??= new Neo.App.Ui.PopupList();
        _dnsList.ClearItems();
        foreach (var (value, label) in DnsPresets)
        {
            var v = value;
            _dnsList.AddItem($"{value}（{label}）", null, () => SetDns(v));
        }
        _dnsList.AddSeparator();
        _dnsList.AddItem("自定义…", null, async () => await PromptCustomDnsAsync());
        ShowListBelow(_dnsList, sender, DnsBox);
    }

    private void SetDns(string value)
    {
        if (string.Equals(AppState.State.DnsServer, value, StringComparison.Ordinal)) return;
        AppState.State.DnsServer = value;
        AppState.Save();
        UpdateDnsUi();
        _ = ConnectionService.ReloadForSettingsAsync();
    }

    private async Task PromptCustomDnsAsync()
    {
        var box = new TextBox
        {
            PlaceholderText = "如 223.6.6.6 或 https://1.1.1.1/dns-query",
            Text = AppState.State.DnsServer,
        };
        var dlg = new ContentDialog
        {
            Title = "自定义 DNS 服务器",
            Content = box,
            PrimaryButtonText = "应用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await Neo.App.Ui.DialogGuard.ShowAsync(dlg) != ContentDialogResult.Primary) return;
        var v = box.Text.Trim();
        if (v.Length == 0) return;
        SetDns(v);
    }

    private void UpdateDnsUi()
    {
        var v = AppState.State.DnsServer;
        DnsText.Text = v;
        DnsTag.Text = v == "223.5.5.5" ? "· 内置" : "· 自定义";
    }

    private void OnRuleSetTapped(object sender, TappedRoutedEventArgs e)
        => OpenRulesRequested?.Invoke(this, EventArgs.Empty);

    private void UpdateRuleSetCount()
    {
        int n = 0;
        if (AppState.State.BypassPrivate) n++;
        if (AppState.State.BypassCn) n++;
        if (AppState.State.BlockAds) n++;
        RuleSetCountText.Text = $"{n} 组已启用";
    }

    private async void OnCheckXrayUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_checkingUpdate) return;
        _checkingUpdate = true;
        var wasConnected = ConnectionService.IsConnected;
        var prevNode = ConnectionService.ConnectedNode ?? AppState.CurrentNode;
        SetCheckingUi(RingXrayUpdate, BtnCheckXrayUpdate, null, true);
        XrayVersionHint.Text = "正在检查更新…";
        try
        {
            var xray = ConnectionService.XrayVersion ?? "—";
            var (ok, msg, rel) = await UpdateChecker.CheckXrayAsync(ConnectionService.XrayVersion);
            if (!ok)
            {
                XrayVersionHint.Text = "检查失败 · " + msg;
                await ShowDialogAsync("检查 xray 更新", msg, "知道了", "");
                return;
            }

            var hasNew = rel is { } r
                && xray != "—"
                && UpdateChecker.CompareVersions(r.Version, xray) > 0;

            var latest = rel?.Version ?? "—";
            XrayVersionHint.Text = hasNew
                ? $"发现新版本 {latest}（当前 {xray}）"
                : $"已是最新版本（{xray}）";

            var result = await ShowDialogAsync(
                "检查 xray 内核",
                $"内置内核：xray {xray}\n\n{msg}",
                hasNew ? "暂不" : "知道了",
                hasNew ? "立即更新" : "");

            if (hasNew && result == ContentDialogResult.Primary && rel is { } chosen)
            {
                await InstallXrayUpdateAsync(chosen);
                await TryReconnectAfterKernelUpdateAsync(wasConnected, prevNode);
            }
        }
        finally
        {
            _checkingUpdate = false;
            SetCheckingUi(RingXrayUpdate, BtnCheckXrayUpdate, null, false);
            RefreshState();
        }
    }

    /// 检查 sing-box 内核更新。通道 = 上游官方 SagerNet/sing-box 稳定发行版（releases/latest）。
    private async void OnCheckSingBoxUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_checkingSingBoxUpdate) return;
        _checkingSingBoxUpdate = true;
        var wasConnected = ConnectionService.IsConnected;
        var prevNode = ConnectionService.ConnectedNode ?? AppState.CurrentNode;
        SetCheckingUi(RingSingBoxUpdate, BtnCheckSingBoxUpdate, null, true);
        SingBoxVersionHint.Text = "正在检查更新…";
        try
        {
            var cur = ConnectionService.SingBoxVersion ?? "—";
            var (ok, msg, rel) = await UpdateChecker.CheckSingBoxAsync(ConnectionService.SingBoxVersion);
            if (!ok)
            {
                SingBoxVersionHint.Text = "检查失败 · " + msg;
                await ShowDialogAsync("检查 sing-box 更新", msg, "知道了", "");
                return;
            }

            var hasNew = rel is { } r
                && cur != "—"
                && UpdateChecker.CompareVersions(
                       UpdateChecker.StripSuffix(r.Version), UpdateChecker.StripSuffix(cur)) > 0;

            SingBoxVersionHint.Text = hasNew
                ? $"发现新版本 {rel!.Version}（当前 {cur}）"
                : $"已是最新版本（{cur}）";

            var extra = "";
            if (hasNew)
            {
                extra = "\n\n官方发行版内核：安装前会自校验版本号；失败会保留旧内核，"
                      + "可用 runtime\\sing-box.exe.bak-prev 回滚。";
            }

            var result = await ShowDialogAsync(
                "检查 sing-box 内核",
                $"内置内核：sing-box {cur}\n\n{msg}{extra}",
                hasNew ? "暂不" : "知道了",
                hasNew ? "立即更新" : "");

            if (hasNew && result == ContentDialogResult.Primary && rel is { } chosen)
            {
                await InstallSingBoxUpdateAsync(chosen);
                await TryReconnectAfterKernelUpdateAsync(wasConnected, prevNode);
            }
        }
        finally
        {
            _checkingSingBoxUpdate = false;
            SetCheckingUi(RingSingBoxUpdate, BtnCheckSingBoxUpdate, null, false);
            RefreshState();
        }
    }

    /// 内核更新完成后自动重连（更新流程会先断开当前连接）。
    /// 只在「更新前是连接状态、更新后没连上」时触发；失败只记 Warning，不打扰用户。
    private static async Task TryReconnectAfterKernelUpdateAsync(bool wasConnected, Neo.Core.Models.NodeItem? node)
    {
        if (!wasConnected || node == null || ConnectionService.IsConnected) return;
        try
        {
            var (ok, err) = await ConnectionService.ConnectAsync(node);
            if (!ok) LogBus.App($"内核更新后自动重连失败：{err}", "Warning");
        }
        catch (Exception ex)
        {
            LogBus.App($"内核更新后自动重连异常：{ex.GetType().Name} {ex.Message}", "Warning");
        }
    }

    /// <summary>一键更新 sing-box 内核：进度弹窗 → 下载 + 双重自校验 → 覆盖安装 → 结果弹窗。</summary>
    private async Task InstallSingBoxUpdateAsync(SingBoxRelease rel)
    {
        var tb = new Microsoft.UI.Xaml.Controls.TextBlock
        {
            Text = "准备中…",
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
        };
        //   解压与 RunVersion 期间进度窗冻结）；进度回调 marshal 回 UI 线程。
        //   与 xray 路径保持一致（原先只在 finally 里 await，时间差会撞 8 秒门闩超时）。
        var cts = new CancellationTokenSource();
        var prog = new ContentDialog
        {
            Title = $"更新 sing-box {rel.Version}",
            Content = tb,
            CloseButtonText = "取消",
            XamlRoot = XamlRoot,
        };
        prog.CloseButtonClick += (_, _) => cts.Cancel();
        var showTask = Neo.App.Ui.DialogGuard.ShowAsync(prog);
        bool ok;
        string msg;
        try
        {
            (ok, msg) = await Task.Run(() => UpdateChecker.DownloadAndInstallSingBoxAsync(
                rel,
                s => DispatcherQueue.TryEnqueue(() =>
                {
                    if (App.IsShuttingDown) return;
                    tb.Text = s;
                }),
                cts.Token));
        }
        catch (OperationCanceledException)
        {
            ok = false;
            msg = "已取消，未做任何改动。";
        }
        catch (Exception ex)
        {
            ok = false;
            msg = "安装过程中出现异常：" + ex.Message;
        }
        finally
        {
            try { cts.Dispose(); } catch { }
            try { prog.Hide(); } catch { }
        }

        try { await showTask.ConfigureAwait(true); } catch { }   
        if (ok) RefreshState();
        await ShowDialogAsync(ok ? "更新完成" : "更新失败", msg, "知道了", "");
    }

    /// 一键更新：进度弹窗 → 下载覆盖安装 → 结果弹窗。全程无需手动替换文件。
    private async Task InstallXrayUpdateAsync(XrayRelease rel)
    {
        var tb = new Microsoft.UI.Xaml.Controls.TextBlock
        {
            Text = "准备中…",
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
        };
        var cts = new CancellationTokenSource();
        var prog = new ContentDialog
        {
            Title = $"更新 xray {rel.Version}",
            Content = tb,
            CloseButtonText = "取消",
            XamlRoot = XamlRoot,
        };
        prog.CloseButtonClick += (_, _) => cts.Cancel();
        var showTask = Neo.App.Ui.DialogGuard.ShowAsync(prog);
        bool ok;
        string msg;
        try
        {
            (ok, msg) = await Task.Run(() => UpdateChecker.DownloadAndInstallAsync(
                rel,
                s => DispatcherQueue.TryEnqueue(() =>
                {
                    if (App.IsShuttingDown) return;
                    tb.Text = s;
                }),
                cts.Token));
        }
        catch (OperationCanceledException)
        {
            ok = false;
            msg = "已取消，未做任何改动。";
        }
        catch (Exception ex)
        {
            ok = false;
            msg = "安装过程中出现异常：" + ex.Message;
        }
        finally
        {
            try { cts.Dispose(); } catch { }
            try { prog.Hide(); } catch { }
        }

        try { await showTask.ConfigureAwait(true); } catch { }   
        if (ok) RefreshState();
        await ShowDialogAsync(ok ? "更新完成" : "更新失败", msg, "知道了", "");
    }

    private async Task<ContentDialogResult> ShowDialogAsync(string title, string content, string closeText, string primaryText)
    {
        var dlg = new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = closeText,
            PrimaryButtonText = primaryText,
            DefaultButton = string.IsNullOrEmpty(primaryText) ? ContentDialogButton.Close : ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        return await Neo.App.Ui.DialogGuard.ShowAsync(dlg);
    }

    private void OnViewLogClick(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo { FileName = NeoPaths.LogDir, UseShellExecute = true }); }
        catch { }
    }

    private async void OnFeedbackClick(object sender, RoutedEventArgs e)
    {
        var dlg = new ContentDialog
        {
            Title = "反馈问题",
            Content = "反馈渠道暂未接入 — 开发期直接找作者提就行。",
            CloseButtonText = "知道了",
            XamlRoot = XamlRoot,
        };
        await Neo.App.Ui.DialogGuard.ShowAsync(dlg);
    }

    /// 导出诊断包：最近日志 + 版本/内核/分流信息 + 脱敏后的配置，打包成 zip。
    private async void OnExportDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var outDir = System.IO.Path.Combine(NeoPaths.DataDir, "diagnostics");
            System.IO.Directory.CreateDirectory(outDir);
            var zipPath = System.IO.Path.Combine(outDir, $"neo-diagnostic-{stamp}.zip");
            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "neo-diag-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(tmp);
            try
            {
                var logDst = System.IO.Path.Combine(tmp, "logs");
                System.IO.Directory.CreateDirectory(logDst);
                foreach (var f in System.IO.Directory.GetFiles(NeoPaths.LogDir, "neo-*.log")
                             .OrderByDescending(f => f).Take(3))
                    System.IO.File.Copy(f, System.IO.Path.Combine(logDst, System.IO.Path.GetFileName(f)), true);

                var buildAt = File.GetLastWriteTime(typeof(SettingsPage).Assembly.Location);
                var info = new System.Text.StringBuilder();
                info.AppendLine($"neo {AppUpdateService.CurrentVersion}");
                info.AppendLine($"构建时间：{buildAt:yyyy-MM-dd HH:mm:ss}");
                info.AppendLine($"系统：{Environment.OSVersion}");
                info.AppendLine($"xray：{ConnectionService.XrayVersion ?? "?"}");
                try { info.AppendLine($"sing-box：{ConnectionService.SingBoxVersion ?? "?"}"); } catch { }
                info.AppendLine($"代理模式：{AppState.State.ProxyMode} · TUN={AppState.State.UseTun}");
                info.AppendLine($"分流：国内直连={AppState.State.BypassCn} 广告拦截={AppState.State.BlockAds} DNS={AppState.State.DnsServer} 日志级别={AppState.State.LogLevel}");
                info.AppendLine($"系统代理：{SystemProxy.IsEnabled}");
                try { info.AppendLine($"最近 24 小时自动记录事件：{IncidentLog.CountRecent(TimeSpan.FromHours(24))} 条"); } catch { }
                System.IO.File.WriteAllText(System.IO.Path.Combine(tmp, "info.txt"), info.ToString());

                // 人话版问题报告：不用看技术日志也能知道发生了什么
                try { System.IO.File.WriteAllText(System.IO.Path.Combine(tmp, "问题报告.txt"), IncidentLog.BuildReport(3)); } catch { }
                try
                {
                    var inc = System.IO.Path.Combine(NeoPaths.DataDir, "incidents.jsonl");
                    if (System.IO.File.Exists(inc))
                        System.IO.File.Copy(inc, System.IO.Path.Combine(tmp, "incidents.jsonl"), true);
                }
                catch { }

                // 配置脱敏：去掉订阅 URL / 手动节点链接 / 节点键（含服务器地址）
                try
                {
                    var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                    var node = System.Text.Json.Nodes.JsonNode.Parse(
                        System.Text.Json.JsonSerializer.Serialize(AppState.State, opts));
                    if (node is System.Text.Json.Nodes.JsonObject obj)
                    {
                        obj.Remove("SubscriptionUrl");
                        obj.Remove("ManualLinks");
                        obj.Remove("LastNodeKey");
                        if (obj["Subscriptions"] is System.Text.Json.Nodes.JsonArray subs)
                            foreach (var sub in subs)
                                if (sub is System.Text.Json.Nodes.JsonObject so) so["Url"] = "";
                    }
                    System.IO.File.WriteAllText(System.IO.Path.Combine(tmp, "state.redacted.json"),
                        node?.ToJsonString(opts) ?? "{}");
                }
                catch { }

                System.IO.Compression.ZipFile.CreateFromDirectory(tmp, zipPath);
            }
            finally
            {
                try { System.IO.Directory.Delete(tmp, true); } catch { }
            }
            LogBus.App($"诊断包已导出：{zipPath}");
            await ShowSettingsInfo("导出诊断", $"已生成诊断包（日志 + 自动事件报告 + 版本信息，配置已脱敏）：\n\n{zipPath}");
        }
        catch (Exception ex)
        {
            LogBus.Exception("导出诊断", ex);
            await ShowSettingsInfo("导出诊断", "导出失败：" + ex.Message);
        }
    }

    /// 恢复默认设置（保留订阅、节点、当前节点与窗口位置）。
    private async void OnResetSettingsClick(object sender, RoutedEventArgs e)
    {
        var res = await ShowDialogAsync(
            "恢复默认设置",
            "将把界面、代理、分流、测速等设置恢复为默认值。\n\n订阅、节点、当前所选节点和窗口位置保留。",
            "取消", "恢复默认");
        if (res != ContentDialogResult.Primary) return;

        var st = AppState.State;
        st.ProxyMode = "rule";
        st.BypassPrivate = true; st.BypassCn = true; st.BlockAds = true; st.AllowLan = false;
        st.LogLevel = "warning"; st.DnsServer = "223.5.5.5"; st.Language = "zh-CN"; st.UseTun = false;
        st.MinimizeToTray = true; st.CompactLayout = false; st.AutoConnect = false; st.AutoUpdate = true;
        st.FailoverEnabled = true;
        st.AutoRepairSystemProxy = true; st.AutoRestartCore = true;
        st.SafeModeAt = ""; st.SafeModePrevTun = false; st.SafeModePrevFixedExit = false;
        st.FixedExitEnabled = false; st.FixedExitExitKey = ""; st.FixedExitEntryCount = 6;
        st.AutoSpeedTest = true; st.StartupSpeedTest = false; st.ShowRealIp = false;
        st.AutoCheckUpdate = true; st.UpdateCheckTime = "12:00"; st.GameDirectEnabled = true;
        AppState.Save();
        AppState.NotifyUiSettingsChanged();
        RefreshState();
        if (ConnectionService.IsConnected)
        {
            try { await ConnectionService.ReloadForSettingsAsync(); } catch { }
        }
        LogBus.App("设置已恢复默认（订阅/节点/窗口位置保留）");
        await ShowSettingsInfo("恢复默认设置", "已恢复默认设置。");
    }
    private void RefreshState()
    {
        _syncing = true;
        SwitchAutoStart.IsOn = IsAutoStartOn();
        SwitchAutoConnect.IsOn = AppState.State.AutoConnect;
        SwitchStartupSpeedTest.IsOn = AppState.State.StartupSpeedTest;
        SwitchShowRealIp.IsOn = AppState.State.ShowRealIp;
        SwitchFailover.IsOn = AppState.State.FailoverEnabled;
        SwitchAutoRepairProxy.IsOn = AppState.State.AutoRepairSystemProxy;
        SwitchAutoRestartCore.IsOn = AppState.State.AutoRestartCore;
        SwitchFixedExit.IsOn = AppState.State.FixedExitEnabled;
        SwitchAutoCheckUpdate.IsOn = AppState.State.AutoCheckUpdate;
        UpdateTimePicker.Time = TimeSpan.TryParse(AppState.State.UpdateCheckTime, out var checkTime)
            ? checkTime
            : new TimeSpan(12, 0, 0);
        AppVersionText.Text = $"当前版本 {AppUpdateService.CurrentVersion}";
        RefreshUpdateStatusHint();
        SwitchMinTray.IsOn = AppState.State.MinimizeToTray;
        SwitchCompact.IsOn = AppState.State.CompactLayout;
        SwitchBypass.IsOn = AppState.State.BypassCn;
        SwitchLan.IsOn = AppState.State.AllowLan;
        SwitchGameDirect.IsOn = AppState.State.GameDirectEnabled;
        RefreshGameDirectSummary();
        _syncing = false;

        LogLevelText.Text = LogLevels.FirstOrDefault(l => l.Key == AppState.State.LogLevel).Label ?? "警告";
        UpdateFixedExitUi();
        RefreshSafeModeRow();
        UpdateRuleSetCount();
        UpdateDnsUi();
        UpdateLangUi();

        bool on = ConnectionService.IsConnected;
        int? mem = ConnectionService.CoreMemoryMb;
        KernelBadge.Text = on && mem is { } m ? $"运行中 · {m} MB" : (on ? "运行中" : "未运行");
        KernelBadge.Foreground = on
            ? ThemeUtil.Get("NeoGreen", Color.FromArgb(255, 0x16, 0xA3, 0x4A))
            : ThemeUtil.Get("NeoText2", Color.FromArgb(255, 0x6B, 0x72, 0x80));
        KernelBadgeBg.Background = on
            ? ThemeUtil.Get("NeoGreenSoft", Color.FromArgb(0x1A, 0x16, 0xA3, 0x4A))
            : ThemeUtil.Get("NeoChipBg", Color.FromArgb(0x0C, 0x1A, 0x1D, 0x21));
        KernelBadgeBg.BorderBrush = KernelBadgeBg.Background;

        var ver = ConnectionService.XrayVersion;
        if (ver != null)
        {
            XrayVersionText.Text = ver;
            AboutXrayText.Text = "xray-core " + ver;
            XrayVersionHint.Text = "本机内置内核 · 就绪";
        }

        // sing-box 单列一行显示版本与可用性。
        var sbVer = ConnectionService.SingBoxVersion;
        SingBoxVersionText.Text = sbVer ?? "—";
        SingBoxVersionHint.Text = sbVer == null ? "未检测到内核文件" : "本机内置内核 · 就绪";
        SingBoxVersionHint.Foreground = ThemeUtil.Get("NeoText2", Color.FromArgb(255, 0x6B, 0x72, 0x80));
        PortSocksText.Text = XrayConfigBuilder.DefaultSocksPort.ToString();
        PortHttpText.Text = XrayConfigBuilder.DefaultHttpPort.ToString();
        LogPathHint.Text = "写入 " + (NeoPaths.LogDir.StartsWith(NeoPaths.DataDir, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine("data", "logs")
            : NeoPaths.LogDir);

        AboutVersionText.Text = "neo " + (typeof(SettingsPage).Assembly.GetName().Version?.ToString(3) ?? "0.1.0");
        try
        {
            var bi = File.GetLastWriteTime(typeof(SettingsPage).Assembly.Location);
            AboutBuildText.Text = $"{bi:yyyy.MM.dd HH:mm} · win-x64";
        }
        catch { }
    }

    // 亮色主题已移除：不再有主题分段控件可点，UpdateThemeSeg 也随之删除。
    // 保留 OnThemeTapped 的空实现没有意义，故一并移除（XAML 里已无 Tapped 绑定）。

    private static void Apply(Border bg, TextBlock text, bool on)
    {
        var accent = ThemeUtil.Get("NeoAccent", Color.FromArgb(255, 0x25, 0x63, 0xEB));
        var onText = ThemeUtil.Get("NeoOnAccent", Colors.White);
        var text2 = ThemeUtil.Get("NeoText2", Color.FromArgb(255, 0x6B, 0x72, 0x80));

        bg.Background = on ? accent : new SolidColorBrush(Colors.Transparent);
        text.Foreground = on ? onText : text2;
        text.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private static System.Collections.Generic.List<string> DefaultGameProcs() => new List<string>
    {
        "TslGame.exe", "cs2.exe", "left4dead2.exe", "HTGame.exe", "Client-Win64-Shipping.exe", "Sugar.exe",
    };
}
