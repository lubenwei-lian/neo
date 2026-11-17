using Microsoft.UI.Xaml;
using Neo.App.Services;
using Neo.Core;

namespace Neo.App;

public sealed partial class MainWindow
{
    private Ui.NativeHtmlWindow? _htmlWindow;
    private HtmlDesktopBridge? _htmlBridge;
    private bool _htmlDesktop,_htmlFirstPaint,_htmlLoading;
    private string _htmlLastPage="conn";
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _htmlEvictionTimer;

    // The hidden WinUI owner retains the existing tray and ordered shutdown.
    // Only the HTML composition window is shown; no legacy pages are built.
    private void InitializeHtmlDesktop()
    {
        _htmlDesktop=true;_dark=true;
        Root.RequestedTheme=ElementTheme.Dark;
        AppWindow.IsShownInSwitchers=false;
        if(!Ui.NativeUiReview.Enabled){SetupTray();TrayIcon.ForceCreate(false);}
        FloatingTrafficService.Initialize(DispatcherQueue,ShowMainWindow);
        ConnectionService.UiDispatcher=DispatcherQueue;
        App.ExitRequested=ExitForRelaunch;
        ConnectionService.StateChanged+=()=>DispatcherQueue.TryEnqueue(()=>{if(!App.IsShuttingDown)UpdateTrayMenu();});
        ConnectionService.SettingsReloadFailed+=message=>DispatcherQueue.TryEnqueue(()=>ShowStatusText(message));
        ConnectionService.UserNotice+=message=>DispatcherQueue.TryEnqueue(()=>
        {if(App.IsShuttingDown)return;ShowStatusText(message);try{TrayIcon.ShowNotification("neo",message);}catch{}});
        if(!Ui.NativeUiReview.Enabled)
        {
            StatsSampler.Start();NetworkDiagnosticsService.Init();CurrentExitService.Init();
            _=ConnectionService.WarmVersionCacheAsync();
            FailoverService.Init(action=>DispatcherQueue.TryEnqueue(()=>{if(!App.IsShuttingDown)action();}));
        }
        FailoverService.Switched+=message=>DispatcherQueue.TryEnqueue(()=>ShowStatusText(message));
        _autoTimer.Tick+=async(_,_)=>{if(App.IsShuttingDown)return;await MaybeAutoUpdateAsync();if(!App.IsShuttingDown)await MaybeAutoCheckAppUpdateAsync();};
        if(!Ui.NativeUiReview.Enabled)_autoTimer.Start();
        AppWindow.Closing+=(_,args)=>{args.Cancel=true;CloseHtmlDesktop();};
        DispatcherQueue.TryEnqueue(async()=>await StartHtmlDesktopAsync());
    }

    private async Task StartHtmlDesktopAsync()
    {
        try
        {
            _htmlWindow=new Ui.NativeHtmlWindow(Ui.NativeUiReview.Enabled);
            _htmlWindow.CloseRequested+=CloseHtmlDesktop;
            _htmlWindow.BoundsChanged+=OnHtmlVisibilityChanged;
            var state=AppState.State;
            var area=GetWorkAreaFor(state.WinX,state.WinY,state.WinW,state.WinH);
            var w=Math.Clamp(state.WinW>0?state.WinW:1280,Math.Min(900,area.Width),area.Width);
            var h=Math.Clamp(state.WinH>0?state.WinH:800,Math.Min(600,area.Height),area.Height);
            var x=state.WinX==int.MinValue?area.X+(area.Width-w)/2:Math.Clamp(state.WinX,area.X,area.X+area.Width-w);
            var y=state.WinY==int.MinValue?area.Y+(area.Height-h)/2:Math.Clamp(state.WinY,area.Y,area.Y+area.Height-h);
            _htmlWindow.Move(x,y);_htmlWindow.Resize(w,h);
            await LoadHtmlContentAsync(false);
            if(!Ui.NativeUiReview.Enabled)
            {
                ConnectionService.CleanupOrphanCores();ConnectionService.CleanupOrphanedSystemProxy();
                if(state.AutoConnect&&AppState.CurrentNode is{} node)
                {var result=await ConnectionService.ConnectAsync(node);if(!result.Ok)ShowStatusText(result.Error??"自动连接失败");}
                StartupSpeedTest.Schedule();
            }
        }
        catch(Exception ex)
        {
            LogBus.App("HTML 窗口启动失败："+ex,"Error");
            if(Ui.NativeUiReview.Enabled)Ui.NativeUiReview.WriteError(ex.ToString());
            _htmlBridge?.Dispose();_htmlWindow?.Dispose();_htmlBridge=null;_htmlWindow=null;
            try{TrayIcon.ShowNotification("neo 界面启动失败","请从托盘退出后重启。日志已保存："+ex.Message);}catch{}
        }
    }

    private async Task LoadHtmlContentAsync(bool restore)
    {
        if(_htmlWindow==null||_htmlLoading||App.IsShuttingDown)return;
        _htmlLoading=true;_htmlFirstPaint=false;
        try
        {
            await _htmlWindow.InitializeAsync(Path.Combine(NeoPaths.DataDir,"webview-ui"),new Uri("https://neo.ui/index.html"),Ui.NativeUiReview.Enabled,async core=>
            {
                if(Ui.NativeUiReview.Enabled)core.NavigationCompleted+=(_,e)=>File.WriteAllText(Path.Combine(Ui.NativeUiReview.DirectoryPath,"html-ready.json"),System.Text.Json.JsonSerializer.Serialize(new{e.IsSuccess,pid=Environment.ProcessId}));
                _htmlBridge=new HtmlDesktopBridge(_htmlWindow,DispatcherQueue,CloseHtmlDesktop);
                await _htmlBridge.ConfigureAsync();
                core.WebMessageReceived+=(_,e)=>
                {
                    if(e.Source!="https://neo.ui/index.html")return;
                    using var message=System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);
                    if(!message.RootElement.TryGetProperty("action",out var a)||a.GetString()!="view.ready"||_htmlFirstPaint)return;
                    _htmlFirstPaint=true;
                    if(restore)_htmlBridge?.Navigate(_htmlLastPage);
                    if(restore||!AppState.State.StartMinimized)
                    { _htmlWindow.Show(false);if(!restore&&!Ui.NativeUiReview.Enabled&&AppState.State.StartMaximized)_htmlWindow.ToggleMaximize(); }
                    else _htmlWindow.Hide();
                    StartupGuard.Clear();App.StartupMark("HTML 首帧与真实服务已就绪");
                };
            });
        }
        finally{_htmlLoading=false;}
    }

    private void OnHtmlVisibilityChanged()
    {
        if(_htmlWindow==null||App.IsShuttingDown)return;
        if(Ui.NativeUiReview.Enabled)File.AppendAllText(Path.Combine(Ui.NativeUiReview.DirectoryPath,"html-lifecycle.trace"),$"visibility visible={_htmlWindow.IsVisible} minimized={_htmlWindow.IsMinimized} core={_htmlWindow.Core!=null} loading={_htmlLoading}\n");
        if(_htmlWindow.IsVisible&&!_htmlWindow.IsMinimized)
        {
            _htmlEvictionTimer?.Stop();
            if(_htmlWindow.Core==null&&!_htmlLoading)_=RestoreHtmlContentAsync();
        }
        else if(!_htmlWindow.IsVisible&&!_htmlWindow.IsMinimized)
        {
            _htmlEvictionTimer??=DispatcherQueue.CreateTimer();
            if(!_htmlEvictionHooked){_htmlEvictionHooked=true;_htmlEvictionTimer.IsRepeating=false;_htmlEvictionTimer.Interval=TimeSpan.FromSeconds(Ui.NativeUiReview.Enabled?3:30);_htmlEvictionTimer.Tick+=(_,_)=>
            {
                if(App.IsShuttingDown||_htmlWindow==null||_htmlWindow.IsVisible||_htmlLoading)return;
                if(_htmlBridge?.HasPendingWork==true){_htmlEvictionTimer.Start();return;}
                _htmlLastPage=_htmlBridge?.CurrentPage??"conn";
                _htmlBridge?.Dispose();_htmlBridge=null;_htmlWindow.ReleaseContent();
                LogBus.App("主窗口隐藏，已释放网页渲染进程；代理与悬浮球继续运行","Debug");
            };}
            if(!_htmlEvictionTimer.IsRunning)_htmlEvictionTimer.Start();
        }
    }
    private bool _htmlEvictionHooked;
    private async Task RestoreHtmlContentAsync()
    {try{await LoadHtmlContentAsync(true);}catch(Exception ex){LogBus.App("恢复界面失败："+ex,"Error");}}

    private async void CloseHtmlDesktop()
    {
        if(App.IsShuttingDown)return;
        SaveHtmlBounds();
        if(AppState.State.MinimizeToTray)_htmlWindow?.Hide();
        else if(Ui.NativeUiReview.Enabled){_htmlBridge?.Dispose();_htmlWindow?.Dispose();Application.Current.Exit();}
        else await ExitApplicationAsync();
    }

    private void SaveHtmlBounds()
    {
        if(_htmlWindow==null||_htmlWindow.Handle==0)return;
        if(!_htmlWindow.IsMinimized)AppState.State.StartMaximized=_htmlWindow.IsMaximized;
        if(!_htmlWindow.IsMinimized&&!_htmlWindow.IsMaximized)
        {var r=_htmlWindow.Bounds;var s=AppState.State;s.WinX=r.Left;s.WinY=r.Top;s.WinW=r.Right-r.Left;s.WinH=r.Bottom-r.Top;}
        AppState.Save();
    }
}
