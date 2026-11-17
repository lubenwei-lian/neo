using Microsoft.UI.Dispatching;
using Neo.App.Ui;

namespace Neo.App.Services;

/// <summary>One optional presentation surface; never starts a core or a second stats sampler.</summary>
internal static class FloatingTrafficService
{
    internal static event Action? Changed;
    private static DispatcherQueue? _dispatcher;
    private static Action? _showMain;
    private static FloatingTrafficWindow? _window;
    private static CoreStats.Snapshot? _previous;
    private static DateTime _previousAt;
    private static int _epoch = -1;
    private static double? _download, _upload;
    private static bool _observing;
    private static int _refreshQueued;

    internal static void Initialize(DispatcherQueue dispatcher, Action showMain)
    {
        _dispatcher = dispatcher; _showMain = showMain;
        if (AppState.State.FloatingTrafficEnabled) ApplyEnabled();
    }

    internal static void SetEnabled(bool enabled)
    {
        if (App.IsShuttingDown) return;
        AppState.State.FloatingTrafficEnabled = enabled;
        ApplyEnabled(); AppState.Save(); Changed?.Invoke();
    }

    private static void ApplyEnabled()
    {
        if (!AppState.State.FloatingTrafficEnabled) { Shutdown(); return; }
        if (_window != null || _dispatcher == null) return;
        try
        {
            _previous = null; _previousAt = default; _epoch = -1; _download = _upload = null;
            _window = new FloatingTrafficWindow(_dispatcher, Read, () => _showMain?.Invoke(), ToggleConnection,
                (x,y) => { AppState.State.FloatingTrafficX=x; AppState.State.FloatingTrafficY=y; AppState.Save(); },
                error => { LogBus.Exception("悬浮球绘制失败，已关闭悬浮球",error); SetEnabled(false); });
            _window.Show(AppState.State.FloatingTrafficX, AppState.State.FloatingTrafficY);
            ConnectionService.StateChanged += RequestRefresh;
            AppState.NodesChanged += RequestRefresh;
            AppState.UiSettingsChanged += RequestRefresh;
            _observing = true;
        }
        catch (Exception ex)
        {
            Shutdown(); AppState.State.FloatingTrafficEnabled=false;
            LogBus.Exception("悬浮球显示失败", ex);
        }
    }

    private static FloatingTrafficData Read()
    {
        var connected=ConnectionService.IsConnected;
        var epoch=ConnectionService.SessionEpoch;
        var at=StatsSampler.SampledAt;
        var latest=StatsSampler.Latest;
        if (!connected || _epoch!=epoch)
        {
            _epoch=epoch; _previous=null; _previousAt=default; _download=_upload=null;
        }
        if (connected && latest is { } current && at!=default && at!=_previousAt)
        {
            if (_previous is { } before && at>_previousAt)
            {
                _download=Math.Max(0,current.Downlink-before.Downlink)/(at-_previousAt).TotalSeconds;
                _upload=Math.Max(0,current.Uplink-before.Uplink)/(at-_previousAt).TotalSeconds;
            }
            _previous=current; _previousAt=at;
        }
        var node=ConnectionService.ConnectedNode ?? AppState.CurrentNode;
        if (ConnectionService.FixedExitActive)
            node=AppState.Nodes.FirstOrDefault(n=>n.Name==ConnectionService.FixedExitName) ?? node;
        var source=node?.SourceSubscription;
        var matches=AppState.State.Subscriptions.Where(s=>s.Name==source || s.Url==source).Take(2).ToArray();
        var sub=matches.Length==1?matches[0]:null;
        // A node without a matching source must not inherit some other subscription's quota.
        var used=sub==null?0:(double)Math.Max(0,sub.SubUpload)+Math.Max(0,sub.SubDownload);
        var total=sub?.SubTotal ?? 0;
        var fresh=connected && at!=default && (DateTime.Now-at).TotalSeconds<=4;
        return new(connected,ConnectionService.IsConnecting,node==null?"未选择节点":NodeDisplayName.WithoutFlag(node.Name),
            fresh?_download:null,latest?.Uplink ?? 0,latest?.Downlink ?? 0,used,total,fresh?_upload:null);
    }

    // State events can arrive from a core worker. Coalesce them onto the UI queue;
    // the one-second timer only reads the existing sampler's cached statistics.
    private static void RequestRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued,1)!=0) return;
        if (_dispatcher?.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _refreshQueued,0);
            _window?.RefreshNow();
        })!=true) Interlocked.Exchange(ref _refreshQueued,0);
    }

    private static async void ToggleConnection()
    {
        if (App.IsShuttingDown || ConnectionService.IsConnecting || NativeUiReview.Enabled) return;
        try
        {
            if (ConnectionService.IsConnected) ConnectionService.Disconnect("悬浮球手动断开");
            else if (AppState.CurrentNode is { } node)
            {
                var (ok,error)=await ConnectionService.ConnectAsync(node);
                if (!ok) { LogBus.App("悬浮球连接失败："+error,"Warning"); _showMain?.Invoke(); }
            }
            else _showMain?.Invoke();
        }
        catch (Exception ex) { LogBus.Exception("悬浮球连接操作失败",ex); }
    }

    internal static FloatingTrafficWindow? WindowForReview => NativeUiReview.Enabled ? _window : null;
    internal static void Shutdown()
    {
        if (_observing)
        {
            ConnectionService.StateChanged -= RequestRefresh;
            AppState.NodesChanged -= RequestRefresh;
            AppState.UiSettingsChanged -= RequestRefresh;
            _observing=false;
        }
        _window?.Dispose(); _window=null; _previous=null; _download=_upload=null;
    }
}
