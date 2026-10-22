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
    /// <summary>空闲释放：停计时器 + 退订 ConnectionService 静态事件。</summary>
    public void Release()
    {
        StopTimers();
        try { ConnectionService.StateChanged -= RefreshState; } catch { }
    }

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

    private void OnRealDelayTestToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        AppState.State.RealDelayTestEnabled = SwitchRealDelayTest.IsOn;
        AppState.Save();
        // 节点页立即切换：主按钮（真连/快速）与卡片主徽标（TCP/真连）都按开关重绘
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
                // AppUpdateService.CheckAsync 已按「网络不可达 / 无发布资产 / 解析失败」给出可执行建议。
                UpdateStatusText.Text = "检查失败";
                await ShowSettingsInfo("检查更新",
                    string.IsNullOrWhiteSpace(r.Error) ? "检查更新失败，请稍后重试。" : r.Error);
                return;
            }

            if (!r.HasNew)
            {
                _pendingUpdate = null;
                _downloadedSetupPath = null;
                BtnDownloadUpdate.Visibility = Visibility.Collapsed;
                if (BtnDownloadUpdate.Content is TextBlock tb0) tb0.Text = "下载并更新";
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

    private string? _downloadedSetupPath;

    private async void OnDownloadUpdateClick(object sender, RoutedEventArgs e)
        => await StartUpdateDownloadAsync();

    /// 2026-10-23：更新包下载改为「页内进度 + 断点续传 + SHA-256」，不再用弹窗；
    /// 下载完成后按钮变成「安装并重启」，再点才进入安装确认（避免下完直接退出）。
    private async Task StartUpdateDownloadAsync()
    {
        if (_downloadingUpdate || _kernelTaskActive) return;
        if (_pendingUpdate is not { HasNew: true } u || string.IsNullOrEmpty(u.AssetUrl)) return;

        // 已经下载过（且文件还在）：直接进入安装确认。
        if (!string.IsNullOrEmpty(_downloadedSetupPath) && System.IO.File.Exists(_downloadedSetupPath))
        {
            await ConfirmAndInstallUpdateAsync(u, _downloadedSetupPath);
            return;
        }

        _downloadingUpdate = true;
        BeginKernelTask($"下载更新 {u.LatestTag}");
        var cts = _kernelTaskCts!;
        try
        {
            var (ok, path, msg) = await Task.Run(() => AppUpdateService.DownloadResumableAsync(
                assetUrl: u.AssetUrl,
                assetName: u.AssetName,
                progress: p => UpdateKernelTask("下载更新包", p),
                expectedSha256: u.Sha256,
                ct: cts.Token));

            if (!ok || path == null)
            {
                EndKernelTask("失败：" + msg);
                await ShowSettingsInfo("下载失败",
                    msg + "\n\n直连不通时请先开启代理后重试；已下载的部分会保留，再点一次可断点续传。");
                return;
            }

            _downloadedSetupPath = path;
            EndKernelTask("完成：" + msg);
            UpdateStatusText.Text = $"已下载 {u.LatestTag} · 点「安装并重启」";
            if (BtnDownloadUpdate.Content is TextBlock tb) tb.Text = "安装并重启";
            await ConfirmAndInstallUpdateAsync(u, path);
        }
        catch (OperationCanceledException)
        {
            EndKernelTask("已暂停：更新包下载保留断点，下次点「下载并更新」继续。");
        }
        catch (Exception ex)
        {
            EndKernelTask("失败：" + ex.Message);
            LogBus.Exception("下载更新包失败", ex);
        }
        finally
        {
            _downloadingUpdate = false;
        }
    }

    private async Task ConfirmAndInstallUpdateAsync(AppUpdateService.UpdateInfo u, string path)
    {
        double sizeMb = 0;
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
                Text = $"新版本 {u.LatestTag} 已下载完成（{sizeMb:F0} MB）。\n"
                     + (string.IsNullOrEmpty(u.Sha256)
                         ? "本次发布未提供哈希，仅记录了实际 SHA-256。\n"
                         : "安装包 SHA-256 已校验通过。\n")
                     + "\n点「立即更新」后 neo 会先断开当前连接并自动关闭，然后开始安装；安装器的 [Run] 项会尝试重新启动 neo，若未自动出现，请手动打开。",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5,
            },
            PrimaryButtonText = "立即更新",
            CloseButtonText = "稍后",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        var res = await Neo.App.Ui.DialogGuard.ShowAsync(confirm);
        if (res != ContentDialogResult.Primary) return;

        var (launchOk, launchErr) = await AppUpdateService.LaunchInstallerAndExitAsync(path, u.LatestTag);
        if (!launchOk)
        {
            await ShowSettingsInfo("更新未开始", "无法安排「退出后安装」，本次未退出，更新已取消。\n\n"
                + (launchErr ?? "未知原因")
                + "\n\n可手动运行已下载的安装包：\n" + path);
            return;
        }
        // 等待器已启动：立刻有序退出；它会在本进程退出（AppMutex 释放）后再启动安装器。
        App.MainWindowInstance?.ExitForUpdate();
    }


    private void OnAutoCheckUpdateToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        AppState.State.AutoCheckUpdate = SwitchAutoCheckUpdate.IsOn;
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

    // ================= 2026-10-23：内核/数据更新的「页内进度」实现 =================
    // 用户要求：内核更新不要弹窗，改为在 UI 栏显示下载进度；支持断点续传；下载完校验哈希。
    // 所有下载任务的进度统一走 KernelTaskPanel（说明文字 + 百分比/速度 + ProgressBar + 取消）。

    private CancellationTokenSource? _kernelTaskCts;
    private bool _kernelTaskActive;
    private XrayRelease? _pendingXrayRelease;
    private SingBoxRelease? _pendingSingBoxRelease;
    private MihomoRelease? _pendingMihomoRelease;
    private UpdateChecker.NaiveRelease? _pendingNaiveRelease;

    private void BeginKernelTask(string title)
    {
        _kernelTaskActive = true;
        _kernelTaskCts = new CancellationTokenSource();
        KernelTaskPanel.Visibility = Visibility.Visible;
        KernelTaskText.Text = title;
        KernelTaskBar.IsIndeterminate = true;
        KernelTaskBar.Value = 0;
        BtnCancelKernelTask.IsEnabled = true;
        BtnCancelKernelTask.Content = "取消";
    }

    private void UpdateKernelTask(string? text, DownloadProgress? p = null)
    {
        void apply()
        {
            if (!string.IsNullOrEmpty(text))
                KernelTaskText.Text = p == null ? text : $"{text} · {p.Text()}";
            if (p is { Total: > 0 })
            {
                KernelTaskBar.IsIndeterminate = false;
                KernelTaskBar.Value = p.Percent;
            }
            else
            {
                KernelTaskBar.IsIndeterminate = true;
            }
        }
        try { if (DispatcherQueue.HasThreadAccess) apply(); else DispatcherQueue.TryEnqueue(apply); } catch { }
    }

    private void EndKernelTask(string finalText)
    {
        _kernelTaskActive = false;
        try { _kernelTaskCts?.Dispose(); } catch { }
        _kernelTaskCts = null;
        void apply()
        {
            KernelTaskText.Text = finalText;
            KernelTaskBar.IsIndeterminate = false;
            KernelTaskBar.Value = 0;
            BtnCancelKernelTask.IsEnabled = false;
        }
        try { if (DispatcherQueue.HasThreadAccess) apply(); else DispatcherQueue.TryEnqueue(apply); } catch { }
        _ = HideKernelTaskLaterAsync();
    }

    private async Task HideKernelTaskLaterAsync()
    {
        try
        {
            await Task.Delay(7000);
            if (_kernelTaskActive) return;
            KernelTaskPanel.Visibility = Visibility.Collapsed;
        }
        catch { }
    }

    private void OnCancelKernelTaskClick(object sender, RoutedEventArgs e)
    {
        try { _kernelTaskCts?.Cancel(); } catch { }
        BtnCancelKernelTask.IsEnabled = false;
        UpdateKernelTask("正在停止…（已下载的部分会保留，下次续传）");
    }

    /// 统一任务执行器：进度回调 → 页内进度条；取消 → 保留断点；结束刷新页面状态。
    private async Task RunKernelTaskAsync(
        string title,
        Func<Action<string>, Action<DownloadProgress>, CancellationToken, Task<(bool Ok, string Message)>> work)
    {
        if (_kernelTaskActive) return;
        BeginKernelTask(title);
        var cts = _kernelTaskCts!;
        try
        {
            var (ok, msg) = await Task.Run(async () => await work(
                s => UpdateKernelTask(s),
                p => UpdateKernelTask("下载中", p),
                cts.Token));
            EndKernelTask((ok ? "完成：" : "失败：") + msg);
            if (!ok) LogBus.App("内核/数据更新失败：" + msg, "Warning");
        }
        catch (OperationCanceledException)
        {
            EndKernelTask("已暂停：已下载的部分保留，下次点同一按钮会断点续传。");
        }
        catch (Exception ex)
        {
            EndKernelTask("失败：" + ex.Message);
            LogBus.App($"内核/数据更新异常：[{ex.GetType().Name}] {ex.Message}", "Warning");
        }
        finally
        {
            RefreshState();
        }
    }

    /// 检查 xray 更新（页内显示，不弹窗）；发现新版本后同一按钮再点一次开始下载安装。
    private async void OnCheckXrayUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_kernelTaskActive) return;

        if (_pendingXrayRelease is { } pending)
        {
            _pendingXrayRelease = null;
            var wasConnected = ConnectionService.IsConnected;
            var prevNode = ConnectionService.ConnectedNode ?? AppState.CurrentNode;
            await RunKernelTaskAsync($"更新 xray {pending.Version}", (stage, prog, ct) =>
                UpdateChecker.DownloadAndInstallAsync(pending, stage, ct, prog));
            await TryReconnectAfterKernelUpdateAsync(wasConnected, prevNode);
            XrayVersionHint.Text = ConnectionService.XrayVersion is { } v ? $"本机内置内核 · {v}" : "检查更新";
            return;
        }

        await RunKernelTaskAsync("检查 xray 更新", async (stage, _, ct) =>
        {
            stage("正在检查 xray 更新…");
            var cur = ConnectionService.XrayVersion ?? "—";
            var (ok, msg, rel) = await UpdateChecker.CheckXrayAsync(cur, AppState.State.CheckKernelPreRelease);
            if (!ok) return (false, msg);
            var hasNew = rel is { } r && cur != "—" && UpdateChecker.CompareVersions(r.Version, cur) > 0;
            if (!hasNew) return (true, $"xray 已是最新版本（{cur}）");
            _pendingXrayRelease = rel;
            return (true, $"发现 xray {rel!.Version}（当前 {cur}）：再点一次「检查更新」立即下载");
        });

        if (_pendingXrayRelease is { } p2)
            XrayVersionHint.Text = $"发现新版本 {p2.Version} · 再点「检查更新」开始下载";
    }

    /// 检查 sing-box 更新（页内显示，不弹窗）。
    private async void OnCheckSingBoxUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_kernelTaskActive) return;

        if (_pendingSingBoxRelease is { } pending)
        {
            _pendingSingBoxRelease = null;
            var wasConnected = ConnectionService.IsConnected;
            var prevNode = ConnectionService.ConnectedNode ?? AppState.CurrentNode;
            await RunKernelTaskAsync($"更新 sing-box {pending.Version}", (stage, prog, ct) =>
                UpdateChecker.DownloadAndInstallSingBoxAsync(pending, stage, ct, prog));
            await TryReconnectAfterKernelUpdateAsync(wasConnected, prevNode);
            SingBoxVersionHint.Text = ConnectionService.SingBoxVersion is { } v ? $"本机内置内核 · {v}" : "检查更新";
            return;
        }

        await RunKernelTaskAsync("检查 sing-box 更新", async (stage, _, ct) =>
        {
            stage("正在检查 sing-box 更新…");
            var cur = ConnectionService.SingBoxVersion ?? "—";
            var (ok, msg, rel) = await UpdateChecker.CheckSingBoxAsync(cur);
            if (!ok) return (false, msg);
            var hasNew = rel is { } r && cur != "—"
                         && UpdateChecker.CompareVersions(UpdateChecker.StripSuffix(r.Version), UpdateChecker.StripSuffix(cur)) > 0;
            if (!hasNew) return (true, $"sing-box 已是最新版本（{cur}）");
            _pendingSingBoxRelease = rel;
            return (true, $"发现 sing-box {rel!.Version}（当前 {cur}）：再点一次「检查更新」立即下载");
        });

        if (_pendingSingBoxRelease is { } p2)
            SingBoxVersionHint.Text = $"发现新版本 {p2.Version} · 再点「检查更新」开始下载";
    }

    /// 检查 mihomo 更新（可选内核；页内显示，不弹窗）。
    private async void OnCheckMihomoUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_kernelTaskActive) return;

        if (_pendingMihomoRelease is { } pending)
        {
            _pendingMihomoRelease = null;
            await RunKernelTaskAsync($"安装 mihomo {pending.Version}", (stage, prog, ct) =>
                UpdateChecker.DownloadAndInstallMihomoAsync(pending, stage, ct, prog));
            RefreshState();
            return;
        }

        await RunKernelTaskAsync("检查 mihomo 更新", async (stage, _, ct) =>
        {
            stage("正在检查 mihomo 更新…");
            var cur = KernelCatalog.ReadVersion(CoreEngine.Mihomo) ?? "";
            var (ok, msg, rel) = await UpdateChecker.CheckMihomoAsync(cur);
            if (!ok) return (false, msg);
            var hasNew = rel is { } r && (cur.Length == 0 || UpdateChecker.CompareVersions(r.Version, cur) > 0);
            if (!hasNew) return (true, $"mihomo 已是最新版本（{cur}）");
            _pendingMihomoRelease = rel;
            var curText = cur.Length == 0 ? "未安装" : cur;
            return (true, $"发现 mihomo {rel!.Version}（当前 {curText}）：再点一次「检查更新」开始下载安装");
        });

        if (_pendingMihomoRelease is { } p2)
            MihomoVersionHint.Text = $"发现新版本 {p2.Version} · 再点「检查更新」开始下载";
    }

    /// 检查 naive 更新（可选内核；naive.exe 只作为辅助进程，前置由 sing-box 承载）。
    private async void OnCheckNaiveUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_kernelTaskActive) return;

        if (_pendingNaiveRelease is { } pending)
        {
            _pendingNaiveRelease = null;
            await RunKernelTaskAsync($"安装 naive {pending.Version}", (stage, prog, ct) =>
                UpdateChecker.DownloadAndInstallNaiveAsync(pending, stage, ct, prog));
            RefreshState();
            return;
        }

        await RunKernelTaskAsync("检查 naive 更新", async (stage, _, ct) =>
        {
            stage("正在检查 naive 更新…");
            var cur = KernelCatalog.ReadVersion(CoreEngine.Naive) ?? "";
            var (ok, msg, rel) = await UpdateChecker.CheckNaiveAsync(cur);
            if (!ok) return (false, msg);
            var hasNew = rel is { } r && (cur.Length == 0 || UpdateChecker.CompareVersions(r.Version, cur) > 0);
            if (!hasNew) return (true, $"naive 已是最新版本（{cur}）");
            _pendingNaiveRelease = rel;
            var curText = cur.Length == 0 ? "未安装" : cur;
            return (true, $"发现 naive {rel!.Version}（当前 {curText}）：再点一次「检查更新」开始下载安装");
        });

        if (_pendingNaiveRelease is { } p2)
            NaiveVersionHint.Text = $"发现新版本 {p2.Version} · 再点「检查更新」开始下载";
    }

    /// 更新分流数据（geoip/geosite/Country.mmdb/rule-set）：页内进度、无弹窗、连接中拒绝。
    private async void OnUpdateGeoClick(object sender, RoutedEventArgs e)
    {
        if (_kernelTaskActive) return;
        // 连接中也可以更新：下载走本地代理（否则 GitHub 不可达）；
        // 若目标文件正被内核占用，GeodataUpdater 会暂存 *.pending，下次启动内核前自动应用。
        await RunKernelTaskAsync("更新分流数据", (stage, prog, ct) =>
            GeoDataUpdater.UpdateAllAsync(stage, ct, onProgress: prog));
    }

    /// <summary>
    /// 「协议指定内核」配置弹窗：每个协议一个下拉（自动 / xray / sing-box）。
    /// 默认自动；指定值只在能力允许时生效（KernelRouter 会校验，冲突静默回退自动）。
    /// </summary>
    private async void OnCoreMapClick(object sender, RoutedEventArgs e)
    {
        var protos = new (string Key, string Label)[]
        {
            ("VLESS", "VLESS"),
            ("VMess", "VMess"),
            ("Trojan", "Trojan"),
            ("Shadowsocks", "Shadowsocks"),
            ("Hysteria2", "Hysteria2"),
            ("TUIC", "TUIC"),
            ("AnyTls", "AnyTLS"),
            ("Socks", "Socks5"),
        };

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = "默认「自动」：按节点传输/加密能力选择内核。手动指定只在能力允许时生效"
                 + "（例如 Hysteria2 / TUIC / AnyTLS 只能 sing-box；xhttp / mKCP / VLESS 加密只能 xray）。"
                 + "TUN 模式固定 sing-box，映射会被忽略。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11.5,
            Opacity = 0.75,
            Margin = new Thickness(0, 0, 0, 6),
        });

        var combos = new List<(string Key, ComboBox Box)>();
        foreach (var (key, label) in protos)
        {
            var row = new Grid { Padding = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock { Text = label, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
            var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 140, FontSize = 11.5 };
            combo.Items.Add("自动");
            combo.Items.Add("xray");
            combo.Items.Add("sing-box");
            var cur = AppState.State.CoreMap.TryGetValue(key, out var v) ? v : "";
            combo.SelectedIndex = cur switch { "xray" => 1, "sing-box" => 2, _ => 0 };
            row.Children.Add(combo);
            panel.Children.Add(row);
            combos.Add((key, combo));
        }

        var dlg = new ContentDialog
        {
            Title = "协议指定内核",
            Content = new ScrollViewer { Content = panel, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        var res = await Neo.App.Ui.DialogGuard.ShowAsync(dlg);
        if (res != ContentDialogResult.Primary) return;

        AppState.State.CoreMap.Clear();
        foreach (var (key, box) in combos)
        {
            var val = box.SelectedIndex switch { 1 => "xray", 2 => "sing-box", _ => "" };
            if (val.Length > 0) AppState.State.CoreMap[key] = val;
        }
        AppState.Save();   // Save 内会同步到 KernelRouter

        var custom = AppState.State.CoreMap.Count;
        LogBus.App(custom == 0
            ? "协议→内核映射：已恢复全自动"
            : "协议→内核映射：" + string.Join("、", AppState.State.CoreMap.Select(kv => $"{kv.Key}→{kv.Value}")), "Info");
        RefreshState();
    }

    /// <summary>导出用户配置（订阅/节点/规则/用量）；不含内核与分流数据。</summary>
    private async void OnExportConfigClick(object sender, RoutedEventArgs e)
    {
        try
        {
            LogBus.App("导出配置：点击，准备打开保存对话框", "Debug");
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedFileName = ConfigBackup.DefaultFileName(),
            };
            picker.FileTypeChoices.Add("neo 配置备份", new List<string> { ".zip" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance!));
            var file = await picker.PickSaveFileAsync();
            if (file == null) { LogBus.App("导出配置：用户取消了保存对话框", "Debug"); return; }
            LogBus.App("导出配置：选择路径 " + file.Path, "Debug");
            var r = ConfigBackup.Export(file.Path);
            await ShowMessageAsync("导出配置", r.Message);
        }
        catch (Exception ex)
        {
            LogBus.App($"导出配置失败：{ex.GetType().Name} {ex.Message}", "Warning");
            await ShowMessageAsync("导出配置", "导出失败：" + ex.Message);
        }
    }

    /// <summary>导入配置：校验 → 确认 → 落盘（自动留回滚副本）→ 提示重启。</summary>
    private async void OnImportConfigClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".zip");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance!));
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            var v = ConfigBackup.Validate(file.Path);
            if (!v.Ok)
            {
                await ShowMessageAsync("导入配置", v.Message);
                return;
            }
            var confirm = new ContentDialog
            {
                Title = "导入配置",
                Content = "导入会覆盖当前的订阅 / 节点 / 规则 / 用量，并需要重启应用。导入前会自动留一份回滚副本。继续？",
                PrimaryButtonText = "导入并重启",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };
            if (await Neo.App.Ui.DialogGuard.ShowAsync(confirm) != ContentDialogResult.Primary) return;

            var r = ConfigBackup.Import(file.Path);
            if (!r.Ok)
            {
                await ShowMessageAsync("导入配置", r.Message);
                return;
            }
            var done = new ContentDialog
            {
                Title = "导入完成",
                Content = r.Message,
                PrimaryButtonText = "立即重启",
                CloseButtonText = "稍后",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };
            var choice = await Neo.App.Ui.DialogGuard.ShowAsync(done);
            LogBus.App($"导入完成对话框选择：{choice}（将重启={choice == ContentDialogResult.Primary}）", "Info");
            if (choice == ContentDialogResult.Primary) App.Restart(saveState: false);   // 不覆盖刚导入的配置
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("导入配置", "导入失败：" + ex.Message);
        }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        try
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
        catch { }
    }

    /// <summary>端口是否被占用；空闲返回 null。</summary>
    private static int? BusyPort(int port)
    {
        try
        {
            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            l.Start();
            l.Stop();
            return null;
        }
        catch { return port; }
    }

    private async void OnApplyPortsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        b.IsEnabled = false;
        try
        {
            if (!int.TryParse(PortSocksBox.Text.Trim(), out var socks) || !int.TryParse(PortHttpBox.Text.Trim(), out var http))
            {
                PortHintText.Text = "端口必须是数字（1024–65535）";
                return;
            }
            // 冲突预检先做（在真正应用之前）：被占用就直接提示，且不改动运行期端口。
            var busy = BusyPort(socks) ?? BusyPort(http);
            if (busy is int busyPort)
            {
                PortHintText.Text = $"端口 {busyPort} 已被其他程序占用，请换一个（可关闭占用该端口的程序后重试）";
                return;
            }
            var err = LocalPorts.TryApply(socks, http);
            if (err != null)
            {
                PortHintText.Text = err;
                return;
            }
            AppState.State.SocksPort = socks;
            AppState.State.HttpPort = http;
            AppState.Save();   // Save 内会 ApplyLocalPorts
            PortHintText.Text = $"已应用：SOCKS {socks} / HTTP {http}（内核将按新端口重建）";
            LogBus.App($"本地端口已修改：SOCKS {socks} / HTTP {http}", "Info");
            await ConnectionService.ReloadForSettingsAsync();
        }
        catch (Exception ex)
        {
            PortHintText.Text = "应用失败：" + ex.Message;
            LogBus.App($"应用端口失败：{ex.GetType().Name} {ex.Message}", "Warning");
        }
        finally
        {
            b.IsEnabled = true;
        }
    }

    private void OnKernelPreReleaseToggled(object sender, RoutedEventArgs e)
    {
        try
        {
            AppState.State.CheckKernelPreRelease = SwitchKernelPreRelease.IsOn;
            AppState.Save();
            LogBus.App($"检查预发布内核：{(SwitchKernelPreRelease.IsOn ? "开" : "关")}（仅对 xray 生效）", "Debug");
        }
        catch (Exception ex)
        {
            LogBus.App($"保存「检查预发布内核」失败：[{ex.GetType().Name}] {ex.Message}", "Warning");
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
            LogBus.App($"内核更新后自动重连异常：[{ex.GetType().Name}] {ex.Message}", "Warning");
        }
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
        st.RealDelayTestEnabled = false;
        st.AutoCheckUpdate = true; st.GameDirectEnabled = true;
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
        SwitchRealDelayTest.IsOn = AppState.State.RealDelayTestEnabled;
        SwitchFailover.IsOn = AppState.State.FailoverEnabled;
        SwitchAutoRepairProxy.IsOn = AppState.State.AutoRepairSystemProxy;
        SwitchAutoRestartCore.IsOn = AppState.State.AutoRestartCore;
        SwitchFixedExit.IsOn = AppState.State.FixedExitEnabled;
        SwitchAutoCheckUpdate.IsOn = AppState.State.AutoCheckUpdate;
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

        // mihomo（第三内核）：只展示安装状态/版本，不参与拨号。
        MihomoVersionHint.Text = KernelCatalog.IsInstalled(CoreEngine.Mihomo)
            ? $"可选内核 · {KernelCatalog.Describe(CoreEngine.Mihomo)}"
            : "可选内核 · 未安装（按需下载）";

        // naive（辅助内核）：naive.exe + sing-box 前置。
        var naiveInstalled = KernelCatalog.IsInstalled(CoreEngine.Naive);
        NaiveVersionText.Text = naiveInstalled ? (KernelCatalog.ReadVersion(CoreEngine.Naive) ?? "已安装") : "—";
        NaiveVersionHint.Text = naiveInstalled
            ? $"可选内核 · {KernelCatalog.Describe(CoreEngine.Naive)}（辅助进程）"
            : "可选内核 · 未安装（按需下载；辅助进程）";

        // 分流数据：显示上次通过本程序更新的时间。
        var geoAt = AppState.State.GeoUpdatedAt;
        GeoDataHint.Text = string.IsNullOrWhiteSpace(geoAt)
            ? "决定国内站点直连是否准确"
            : $"上次更新 {geoAt}";

        // 预发布开关：只在状态不一致时赋值，避免每次 RefreshState 都触发一次 Toggled/落盘。
        if (SwitchKernelPreRelease.IsOn != AppState.State.CheckKernelPreRelease)
            SwitchKernelPreRelease.IsOn = AppState.State.CheckKernelPreRelease;

        // 协议→内核映射摘要。
        var coreCustom = AppState.State.CoreMap.Count;
        CoreMapHint.Text = coreCustom == 0
            ? "默认自动：按节点传输/加密能力选择内核"
            : $"已指定 {coreCustom} 个协议 · 其余自动";

        PortSocksBox.Text = LocalPorts.Socks.ToString();
        PortHttpBox.Text = LocalPorts.Http.ToString();
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
