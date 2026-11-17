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
    private readonly (string Name, NodeProtocol Protocol, string Server, int Port, string? Source, string Transport, bool Compact) _presentation;
    internal bool PresentationMatches => _presentation == (Node.Name, Node.Protocol, Node.Server, Node.Port, Node.SourceSubscription, AppState.TransportName(Node), AppState.State.CompactLayout);
    public string NameText { get; }
    private string _metaText = "";
    public string MetaText { get => _metaText; private set { if (_metaText == value) return; _metaText = value; Raise(); } }
    private string _tooltip = "";
    private string _locText = "";
    private Brush? _locBrush;
    private bool _locMismatch;
    private readonly string _fullInfo;
    private readonly string _metaBase = "";
    /// 位置检测带来的信息行后缀（真实出口/不符标记）；由 RefreshLocation 维护。
    private string _locMetaSuffix = "";

    public string Tooltip { get => _tooltip; private set { if (_tooltip == value) return; _tooltip = value; Raise(); } }
    public string LocText { get => _locText; private set { if (_locText == value) return; _locText = value; Raise(); Raise(nameof(LocVisibility)); } }
    public Brush? LocBrush { get => _locBrush; private set { if (ReferenceEquals(_locBrush, value)) return; _locBrush = value; Raise(); } }
    public Visibility LocVisibility => _locText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string FlagKey { get; }
    public string SearchText { get; }
    public double CardHeight { get; }

    private string _delayText = "● 未测";
    private Brush? _delayBrush;
    private Brush? _cardBg;
    private Brush? _cardBorder;
    private Thickness _cardThickness = new(1);

    public string DelayText { get => _delayText; private set { if (_delayText == value) return; _delayText = value; Raise(); Raise(nameof(DelayActionLabel)); } }
    public Brush? DelayBrush { get => _delayBrush; private set { if (ReferenceEquals(_delayBrush, value)) return; _delayBrush = value; Raise(); } }
    public Brush? CardBg { get => _cardBg; private set { _cardBg = value; Raise(); } }
    public Brush? CardBorder { get => _cardBorder; private set { _cardBorder = value; Raise(); } }
    public Thickness CardThickness { get => _cardThickness; private set { _cardThickness = value; Raise(); } }

    public bool IsSelected { get; private set; }
    public Visibility SelectedVisibility => IsSelected ? Visibility.Visible : Visibility.Collapsed;
    public string DelayActionLabel => $"重新测试{NameText}的延迟，当前{DelayText}";
    public bool IsTesting { get; private set; }
    /// <summary>排序/过滤用的「显示口径」延迟：与卡片主徽标完全一致（见 NodeItem.DisplayDelay）。</summary>
    public int RealValue => Node.DisplayDelay(AppState.State.RealDelayTestEnabled);

    public event PropertyChangedEventHandler? PropertyChanged;

    public NodeTile(NodeItem node)
    {
        Node = node;
        _presentation = (node.Name, node.Protocol, node.Server, node.Port, node.SourceSubscription, AppState.TransportName(node), AppState.State.CompactLayout);
        NameText = Neo.App.Ui.NodeDisplayName.WithoutFlag(node.Name);
        // 「真实 IP 显示」打开时卡片多一行（真实出口国家 + IP），高度 +12。
        var showReal = AppState.State.ShowRealIp;
        // 卡片固定两行（名称 + 信息行）：真实出口并入信息行，不额外加行，避免被容器裁切。
        // Two text rows plus the independent latency button must fit at 100%
        // text scale as well; 58px clipped its descent in compact mode.
        CardHeight = AppState.State.CompactLayout ? 63 : 72;

        var proto = AppState.ProtocolName(node.Protocol);
        var transport = AppState.TransportName(node);
        // 卡片只留「地区 · 类型」；服务器/传输/来源/真实出口都在 Tooltip。
        _metaBase = proto + (string.IsNullOrEmpty(transport) ? "" : " · " + transport);
        MetaText = _metaBase;
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
        CardBg = ThemeUtil.Get("NeoNodeSurface", Color.FromArgb(255, 0x1C, 0x1C, 0x20));
        CardBorder = ThemeUtil.Get("NeoRim", Color.FromArgb(0x0C, 0x1A, 0x1D, 0x21));
        CardThickness = new Thickness(1);
    }

    private void ApplySelectedColor()
    {
        CardBg = ThemeUtil.Get("NeoNodeSelected", Color.FromArgb(0x1A, 0x25, 0x63, 0xEB));
        CardBorder = ThemeUtil.Get("NeoAccentLine", Color.FromArgb(0x66, 0x5A, 0xA9, 0xFF));
        CardThickness = new Thickness(1);
    }

    public void SetSelected(bool on)
    {
        if (IsSelected == on) return;
        IsSelected = on;
        Raise(nameof(IsSelected));
        Raise(nameof(SelectedVisibility));
        if (on) ApplySelectedColor(); else ApplyIdleColor();
    }

    public void RefreshDelay()
    {
        string text, fgKey;
        Color fgFallback;
        // 关闭「真连测速」时主徽标显示 TCP 握手延迟（TCP 握手风格）；开启后按现有 RealDelay 语义显示。
        var useReal = AppState.State.RealDelayTestEnabled;
        // 主口径还没结果（0=未测）时回退另一口径：hy2/TUIC 没有 TCP 端口（TCP 永远跳过），
        // 只按开关取值会让「真连已测出」的节点仍显示未测速。
        var ms = Node.DisplayDelay(useReal);

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
            // 关闭真连测速时更明确地写「未测速」（此时徽标代表 TCP 握手延迟）。
            text = useReal ? "未测" : "未测速";
            fgKey = "NeoText3";
            fgFallback = Color.FromArgb(255, 0x98, 0xA0, 0xAA);
        }

        DelayText = "● " + text;
        DelayBrush = ThemeUtil.Get(fgKey, fgFallback);
        RefreshMeta();   // 真连延迟变化时同步信息行里的「· 真连 N ms」
    }

    /// <summary>
    /// 重新拼卡片信息行：地区·协议 + TCP 延迟 + 真连延迟 + 位置信息。
    /// 顺序固定「TCP 在前、真连在后」；0（未测）不显示，-1 显示「超时」。
    /// </summary>
    private void RefreshMeta()
    {
        var meta = _metaBase;
        var delayDetails = "";

        var tcp = Node.TcpDelay;
        if (tcp > 0) delayDetails += $" · TCP {tcp}ms";
        else if (tcp == -1) delayDetails += " · TCP 超时";

        var real = Node.RealDelay;
        var note = Node.RealDelayNote;
        var noteSuffix = string.IsNullOrEmpty(note) ? "" : $"（{note}）";
        if (real > 0) delayDetails += $" · 真连 {real}ms{noteSuffix}";
        else if (real == -1) delayDetails += " · 真连 超时" + noteSuffix;
        else if (real == RealDelayTester.BatchFailed) delayDetails += " · 真连 本机失败";

        meta += _locMetaSuffix;
        MetaText = meta;
        Tooltip = _fullInfo + delayDetails + (string.IsNullOrEmpty(Node.ExitCountry) ? "\n位置：未检测" : "\n实际出口：" + NodeGrouper.RegionDisplay(Node.ExitCountry) + " · " + Node.ExitIp + (_locMismatch ? "（与标记不符）" : ""));
    }

    /// <summary>回填 TCP 握手延迟测试结果（调用方保证在 UI 线程）。0=跳过（UDP/无效），不显示。</summary>
    public void SetTcpResult(int ms)
    {
        Node.TcpDelay = ms;
        Node.TcpDelayAt = DateTime.Now;
        DelayCache.SetTcp(Node, ms);
        RefreshDelay();   // 关闭真连时它就是主徽标；同时内部会刷新 meta
        LogBus.App($"TCP 延迟 {Node.Name} → " + (ms > 0 ? $"{ms} ms" : ms == -1 ? "超时" : "跳过"), "Debug");
    }

    public async Task<bool> TestAsync(CancellationToken ct = default)
    {
        if (IsTesting) return false;
        IsTesting = true;
        try
        {
            DelayText = "测速中…";
            DelayBrush = ThemeUtil.Get("NeoText2", Color.FromArgb(255, 0x6B, 0x72, 0x80));
            int ms;
            if (NativeUiReview.Enabled)
            {
                // Isolate the network boundary while exercising the real UI action.
                await Task.Delay(120, ct);
                ms = Node.RealDelay > 0 ? Node.RealDelay : 50;
            }
            else ms = await RealDelayTester.RealPingAsync(Node, NeoPaths.RuntimeDir, ct: ct);
            SetResult(ms);
            return ms > 0;
        }
        finally { IsTesting = false; RefreshDelay(); }
    }

    /// 回填批量测速结果（线程安全由调用方保证在 UI 线程调用）
    public void SetResult(int ms)
    {
        //   否则会把「本机临时失败」显示成「节点不可用」并影响排序/自动选择；保持未测速（0）待重测。
        Node.RealDelay = ms > 0 ? ms : (ms == -1 ? -1 : 0);
        DelayCache.Set(Node, ms, Node.RealDelayNote);
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

        // 真实出口并进信息行（不额外占一行，卡片高度固定，绝不会被裁）。
        _locMetaSuffix = AppState.State.ShowRealIp
            ? actual.Length > 0
                ? " · 实际" + NodeGrouper.RegionDisplay(actual) + (actualIp.Length > 0 ? " " + actualIp : "")
                : " · 位置未检测"
            : mismatch ? " · 实际" + NodeGrouper.RegionDisplay(actual) : "";
        RefreshMeta();

        LocText = "";

        LocBrush = mismatch
            ? ThemeUtil.Get("NeoRed", Color.FromArgb(255, 0xDC, 0x26, 0x26))
            : ThemeUtil.Get("NeoText2", Color.FromArgb(255, 0x6B, 0x72, 0x80));
    }

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
