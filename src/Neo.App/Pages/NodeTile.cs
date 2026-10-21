using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Neo.App.Services;
using Neo.App.Ui;
using Neo.Core;
using Neo.Core.Core;
using Neo.Core.Models;
using Windows.UI;

namespace Neo.App.Pages;

public sealed class NodeTile : INotifyPropertyChanged
{
    public NodeItem Node { get; }
    public string NameText { get; }
    public string MetaText { get; }
    private string _tooltip = "";
    private string _locText = "";
    private Brush? _locBrush;
    private bool _locMismatch;
    private readonly string _fullInfo;

    public string Tooltip { get => _tooltip; private set { _tooltip = value; Raise(); } }
    public string LocText { get => _locText; private set { _locText = value; Raise(); } }
    public Brush? LocBrush { get => _locBrush; private set { _locBrush = value; Raise(); } }
    public Visibility LocVisibility => _locText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string FlagKey { get; }
    public string SearchText { get; }
    public double CardHeight { get; }

    private string _delayText = "● 未测";
    private Brush? _delayBrush;
    private Brush? _cardBg;
    private Brush? _cardBorder;
    private Thickness _cardThickness = new(1);

    public string DelayText { get => _delayText; private set { _delayText = value; Raise(); } }
    public Brush? DelayBrush { get => _delayBrush; private set { _delayBrush = value; Raise(); } }
    public Brush? CardBg { get => _cardBg; private set { _cardBg = value; Raise(); } }
    public Brush? CardBorder { get => _cardBorder; private set { _cardBorder = value; Raise(); } }
    public Thickness CardThickness { get => _cardThickness; private set { _cardThickness = value; Raise(); } }

    public bool IsSelected { get; private set; }
    public bool IsTesting { get; private set; }
    public int RealValue => Node.RealDelay;

    public event PropertyChangedEventHandler? PropertyChanged;

    public NodeTile(NodeItem node)
    {
        Node = node;
        NameText = node.Name;
        // 「真实 IP 显示」打开时卡片多一行（真实出口国家 + IP），高度 +12。
        var showReal = AppState.State.ShowRealIp;
        CardHeight = AppState.State.CompactLayout ? (showReal ? 70 : 58) : (showReal ? 78 : 66);

        var proto = AppState.ProtocolName(node.Protocol);
        var transport = AppState.TransportName(node);
        // 卡片只留「地区 · 类型」；服务器/传输/来源/真实出口都在 Tooltip。
        MetaText = NodeGrouper.RegionDisplay(NodeGrouper.RegionOf(node.Name)) + " · " + proto;
        _fullInfo = $"{node.Name}\n{node.Server}:{node.Port}\n{proto}"
            + (string.IsNullOrEmpty(transport) ? "" : " · " + transport)
            + (string.IsNullOrEmpty(node.SourceSubscription) ? "" : "\n来源：" + node.SourceSubscription);

        FlagKey = AppState.GuessFlag(node.Name);
        SearchText = $"{node.Name} {proto} {transport} {node.Server}".ToLowerInvariant();

        ApplyIdleColor();
        RefreshDelay();
        RefreshLocation();
    }

    private void ApplyIdleColor()
    {
        CardBg = ThemeUtil.Get("NeoGlass2", Color.FromArgb(0x85, 0xFF, 0xFF, 0xFF));
        CardBorder = ThemeUtil.Get("NeoHairline2", Color.FromArgb(0x0C, 0x1A, 0x1D, 0x21));
        CardThickness = new Thickness(1);
    }

    private void ApplySelectedColor()
    {
        CardBg = ThemeUtil.Get("NeoAccentSoft", Color.FromArgb(0x1A, 0x25, 0x63, 0xEB));
        CardBorder = ThemeUtil.Get("NeoAccentLine", Color.FromArgb(0x38, 0x25, 0x63, 0xEB));
        CardThickness = new Thickness(1.5);
    }

    public void SetSelected(bool on)
    {
        if (IsSelected == on) return;
        IsSelected = on;
        if (on) ApplySelectedColor(); else ApplyIdleColor();
    }

    public void RefreshDelay()
    {
        string text, fgKey;
        Color fgFallback;
        var ms = Node.RealDelay;

        if (ms > 0)
        {
            text = $"{ms} ms";
            if (ms <= 800) { fgKey = "NeoGreen"; fgFallback = Color.FromArgb(255, 0x16, 0xA3, 0x4A); }
            else if (ms <= 2000) { fgKey = "NeoYellow"; fgFallback = Color.FromArgb(255, 0xD9, 0x77, 0x06); }
            else { fgKey = "NeoRed"; fgFallback = Color.FromArgb(255, 0xDC, 0x26, 0x26); }
        }
        else if (ms == -1)
        {
            text = "超时";
            fgKey = "NeoRed";
            fgFallback = Color.FromArgb(255, 0xDC, 0x26, 0x26);
        }
        else if (ms == RealDelayTester.BatchFailed)
        {
            //   与「超时」区分：显示为「本机失败」并配中性色，避免用户误判成节点问题。
            text = "本机失败";
            fgKey = "NeoYellow";
            fgFallback = Color.FromArgb(255, 0xD9, 0x77, 0x06);
        }
        else
        {
            text = "未测";
            fgKey = "NeoText3";
            fgFallback = Color.FromArgb(255, 0x98, 0xA0, 0xAA);
        }

        DelayText = "● " + text;
        DelayBrush = ThemeUtil.Get(fgKey, fgFallback);
    }

    public async Task<bool> TestAsync(CancellationToken ct = default)
    {
        if (IsTesting) return false;
        IsTesting = true;
        try
        {
            DelayText = "● …";
            DelayBrush = ThemeUtil.Get("NeoText2", Color.FromArgb(255, 0x6B, 0x72, 0x80));
            var ms = await RealDelayTester.RealPingAsync(Node, NeoPaths.RuntimeDir, ct: ct);
            SetResult(ms);
            return ms > 0;
        }
        finally { IsTesting = false; }
    }

    /// 回填批量测速结果（线程安全由调用方保证在 UI 线程调用）
    public void SetResult(int ms)
    {
        Node.RealDelay = ms;
        DelayCache.Set(Node, ms);
        RefreshDelay();
        //   必须与 -1（节点真的超时）在日志里区分，否则无法事后归因。
        LogBus.App($"测速 {Node.Name} → " + (ms > 0 ? $"{ms} ms"
            : ms == RealDelayTester.BatchFailed ? "本机测速失败（未测到节点）"
            : ms == -1 ? "超时" : "失败"), "Debug");
    }

    /// 位置检测结果回填。
    ///   · 「真实 IP 显示」打开且有检测结果：额外显示「实际国家 + IP」；
    ///   · 未打开时只在「标记与实际不符」的情况下标红「实际 XX」；
    ///   · Tooltip 始终带完整信息（服务器、传输、来源、真实出口）。
    public void RefreshLocation()
    {
        var actual = Node.ExitCountry ?? "";
        var actualIp = Node.ExitIp ?? "";
        var claimed = AppState.GuessFlag(Node.Name);
        var mismatch = actual.Length > 0 && claimed != "other"
            && !string.Equals(claimed, actual, StringComparison.OrdinalIgnoreCase);
        _locMismatch = mismatch;

        Tooltip = _fullInfo
            + (actual.Length == 0
                ? "\n位置：未检测"
                : $"\n实际出口：{NodeGrouper.RegionDisplay(actual)}"
                  + (actualIp.Length > 0 ? " · " + actualIp : "")
                  + (mismatch ? $"（标记为{NodeGrouper.RegionDisplay(claimed)}，不符）" : "（与标记一致）"));

        var showReal = AppState.State.ShowRealIp && actual.Length > 0;
        if (showReal)
            LocText = "实际" + NodeGrouper.RegionDisplay(actual) + (actualIp.Length > 0 ? " " + actualIp : "");
        else if (mismatch)
            LocText = "实际" + NodeGrouper.RegionDisplay(actual);
        else
            LocText = "";

        LocBrush = mismatch
            ? ThemeUtil.Get("NeoRed", Color.FromArgb(255, 0xDC, 0x26, 0x26))
            : ThemeUtil.Get("NeoText2", Color.FromArgb(255, 0x6B, 0x72, 0x80));
        Raise(nameof(LocVisibility));
    }

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
