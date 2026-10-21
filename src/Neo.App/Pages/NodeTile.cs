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
    public string Tooltip { get; }
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
        CardHeight = AppState.State.CompactLayout ? 58 : 66;

        var proto = AppState.ProtocolName(node.Protocol);
        var transport = AppState.TransportName(node);
        var meta = string.IsNullOrEmpty(transport) ? proto : proto + " · " + transport;
        if (!string.IsNullOrEmpty(node.SourceSubscription)) meta += " · " + node.SourceSubscription;
        MetaText = meta;

        FlagKey = AppState.GuessFlag(node.Name);
        SearchText = $"{node.Name} {proto} {transport} {node.Server}".ToLowerInvariant();
        Tooltip = $"{node.Name}\n{node.Server}:{node.Port}\n{proto} · {transport}";

        ApplyIdleColor();
        RefreshDelay();
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

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
