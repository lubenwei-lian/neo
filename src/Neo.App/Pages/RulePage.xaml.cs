using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Neo.App.Services;
using Neo.Core.Models;
using Windows.UI;

namespace Neo.App.Pages;

public sealed partial class RulePage : UserControl
{
    private bool _syncing;
    private string _action = "direct";
    private readonly Microsoft.UI.Xaml.DispatcherTimer _errorTimer = new() { Interval = TimeSpan.FromSeconds(3.5) };

    public RulePage()
    {
        InitializeComponent();
        _errorTimer.Tick += (_, _) => { _errorTimer.Stop(); RuleError.Visibility = Visibility.Collapsed; };
        ActualThemeChanged += (_, _) => { UpdateActionChips(); RefreshRules(); };
        Loaded += (_, _) => RefreshAll();
        ConnectionService.StateChanged += RefreshModeCard;
    }


    public void RefreshFromState() => RefreshAll();

    /// <summary>退出时停掉本页计时器（Exit 后它仍会触发并写 XAML）。</summary>
    /// <summary>空闲释放：停计时器 + 退订 ConnectionService 静态事件。</summary>
    public void Release()
    {
        StopTimers();
        try { ConnectionService.StateChanged -= RefreshModeCard; } catch { }
    }

    public void StopTimers()
    {
        try { _errorTimer.Stop(); } catch { }
    }

    private void RefreshAll()
    {
        RefreshModeCard();
        _syncing = true;
        SwitchPrivate.IsOn = AppState.State.BypassPrivate;
        SwitchCn.IsOn = AppState.State.BypassCn;
        SwitchAds.IsOn = AppState.State.BlockAds;
        _syncing = false;
        UpdateActionChips();
        RefreshRules();
    }

    private void RefreshModeCard()
    {
        var mode = AppState.State.ProxyMode;
        ModePillText.Text = mode switch { "global" => "全局", "direct" => "直连", _ => "规则" };
        ModeDescText.Text = mode switch
        {
            "global" => "所有流量都经节点转发",
            "direct" => "不使用节点，全部直连",
            _ => "国内域名与 IP 直连，其余流量走节点",
        };
    }


    //   旧实现一个 OnRuleToggled 一次写回三个字段 —— 在设置页拨过「BypassCn」后，
    //   规则页里 SwitchCn/Private/Ads 的 IsOn 可能还是页面首次 Loaded 时的旧值，
    //   此时拨任意一个开关都会把另外两个**过期值**写回 state.json，静默回滚用户设置。
    private void OnPrivateToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        AppState.State.BypassPrivate = SwitchPrivate.IsOn;
        SaveRuleSettings();
    }

    private void OnCnToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        AppState.State.BypassCn = SwitchCn.IsOn;
        SaveRuleSettings();
    }

    private void OnAdsToggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        AppState.State.BlockAds = SwitchAds.IsOn;
        SaveRuleSettings();
    }

    private static void SaveRuleSettings()
    {
        AppState.Save();
        _ = ConnectionService.ReloadForSettingsAsync();
    }


    private void OnActionTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is Grid { Tag: string tag })
        {
            _action = tag;
            UpdateActionChips();
        }
    }

    private void UpdateActionChips()
    {
        Apply(ActDirectBg, ActDirectText, _action == "direct");
        Apply(ActProxyBg, ActProxyText, _action == "proxy");
        Apply(ActBlockBg, ActBlockText, _action == "block");
    }

    private void OnRuleInputChanged(object sender, TextChangedEventArgs e)
        => RuleInputHint.Visibility = string.IsNullOrEmpty(RuleInput.Text) ? Visibility.Visible : Visibility.Collapsed;

    private void OnAddRuleClick(object sender, RoutedEventArgs e)
    {
        var value = (RuleInput.Text ?? "").Trim();
        if (value.Length == 0 || value.Contains(' ') || (!value.Contains('.') && !value.Contains(':')))
        {
            ShowError("输入无效 — 填域名（如 example.com）或 IP（如 10.0.0.0/8）");
            return;
        }

        var kind = LooksLikeIp(value) ? "ip" : "domain";
        if (AppState.State.CustomRules.Any(r => r.Kind == kind && r.Value.Equals(value, StringComparison.OrdinalIgnoreCase)))
        {
            ShowError("这条规则已存在");
            return;
        }

        AppState.State.CustomRules.Add(new RouteRule { Kind = kind, Value = value, Action = _action });
        AppState.Save();
        RuleInput.Text = "";
        RefreshRules();
        _ = ConnectionService.ReloadForSettingsAsync();
    }

    private static bool LooksLikeIp(string v)
    {
        if (v.Contains(':')) return true;
        return System.Text.RegularExpressions.Regex.IsMatch(v, @"^\d{1,3}(\.\d{1,3}){3}(/\d{1,2})?$");
    }

    private void ShowError(string msg)
    {
        RuleError.Text = msg;
        RuleError.Visibility = Visibility.Visible;
        _errorTimer.Stop();
        _errorTimer.Start();
    }

    private void RefreshRules()
    {
        var rules = AppState.State.CustomRules;
        RuleCountText.Text = $"{rules.Count} 条";
        RuleEmpty.Visibility = rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        RuleList.Children.Clear();
        for (int i = 0; i < rules.Count; i++)
            RuleList.Children.Add(BuildRow(rules[i], i));
    }

    private UIElement BuildRow(RouteRule r, int index)
    {
        var row = new Border
        {
            Height = 38,
            CornerRadius = new CornerRadius(8),
            Background = ThemeUtil.Get("NeoGlass2", Color.FromArgb(0x85, 0xFF, 0xFF, 0xFF)),
            BorderBrush = ThemeUtil.Get("NeoHairline", Color.FromArgb(0x12, 0x1A, 0x1D, 0x21)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(9, 0, 6, 0),
            Margin = new Thickness(0, 4, 0, 0),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var kindPill = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7, 2, 7, 2),
            Background = ThemeUtil.Get("NeoChipBg", Color.FromArgb(0x0C, 0x1A, 0x1D, 0x21)),
            BorderBrush = ThemeUtil.Get("NeoHairline", Color.FromArgb(0x12, 0x1A, 0x1D, 0x21)),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = r.Kind == "ip" ? "IP" : "域名",
                FontSize = 10,
                Foreground = ThemeUtil.Get("NeoText2", Color.FromArgb(255, 0x6B, 0x72, 0x80)),
            },
        };
        Grid.SetColumn(kindPill, 0);

        var valueText = new TextBlock
        {
            Text = r.Value,
            FontSize = 12,
            Foreground = ThemeUtil.Get("NeoText1", Color.FromArgb(255, 0x1A, 0x1D, 0x21)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 8, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(valueText, 1);

        var (actText, actFgKey, actFgFallback, actBgKey, actBgFallback) = r.Action switch
        {
            "proxy" => ("代理", "NeoAccent", Color.FromArgb(255, 0x25, 0x63, 0xEB), "NeoAccentSoft", Color.FromArgb(0x1A, 0x25, 0x63, 0xEB)),
            "block" => ("拦截", "NeoRed", Color.FromArgb(255, 0xDC, 0x26, 0x26), "NeoRedSoft", Color.FromArgb(0x1A, 0xDC, 0x26, 0x26)),
            _ => ("直连", "NeoGreen", Color.FromArgb(255, 0x16, 0xA3, 0x4A), "NeoGreenSoft", Color.FromArgb(0x1A, 0x16, 0xA3, 0x4A)),
        };

        var actPill = new Border
        {
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 2.5, 8, 2.5),
            Background = ThemeUtil.Get(actBgKey, actBgFallback),
            BorderBrush = ThemeUtil.Get("NeoHairline", Color.FromArgb(0x12, 0x1A, 0x1D, 0x21)),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = actText,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeUtil.Get(actFgKey, actFgFallback),
            },
        };
        Grid.SetColumn(actPill, 2);

        var del = new Button
        {
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new TextBlock
            {
                Text = "\uE711",
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 11,
                Foreground = ThemeUtil.Get("NeoText3", Color.FromArgb(255, 0x98, 0xA0, 0xAA)),
            },
        };
        del.Click += (_, _) =>
        {
            if (index >= 0 && index < AppState.State.CustomRules.Count)
            {
                AppState.State.CustomRules.RemoveAt(index);
                AppState.Save();
                RefreshRules();
                _ = ConnectionService.ReloadForSettingsAsync();
            }
        };
        Grid.SetColumn(del, 3);

        grid.Children.Add(kindPill);
        grid.Children.Add(valueText);
        grid.Children.Add(actPill);
        grid.Children.Add(del);
        row.Child = grid;
        return row;
    }

    private static void Apply(Border bg, TextBlock text, bool on)
    {
        var accent = ThemeUtil.Get("NeoAccent", Color.FromArgb(255, 0x25, 0x63, 0xEB));
        var onText = ThemeUtil.Get("NeoOnAccent", Colors.White);
        var text2 = ThemeUtil.Get("NeoText2", Color.FromArgb(255, 0x6B, 0x72, 0x80));
        bg.Background = on ? accent : new SolidColorBrush(Colors.Transparent);
        text.Foreground = on ? onText : text2;
        text.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
    }
}
