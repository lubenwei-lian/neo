using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Neo.App.Ui;
using Neo.Core;
using Neo.Core.Core;
using Neo.Core.Models;
using System.Diagnostics;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;

namespace Neo.App.Services;

/// <summary>Typed local commands and real service snapshots for the single HTML surface.</summary>
internal sealed partial class HtmlDesktopBridge : IDisposable
{
    private const string Origin="https://neo.ui/index.html";
    private readonly NativeHtmlWindow _window;
    private readonly DispatcherQueue _queue;
    private readonly Action _close;
    private readonly CoreWebView2 _core;
    private readonly DispatcherQueueTimer _timer;
    private readonly DispatcherQueueTimer _backgroundFrames;
    private CancellationTokenSource _lifetime=new(),_testing=new();
    private bool _disposed,_ready,_catalogDirty=true,_settingsDirty=true,_logDirty=true,_sublogDirty=true,_diagDirty=true;
    private bool _testRunning;
    private int _activeRequests;
    internal bool HasPendingWork=>_testRunning||_updating.Count>0||_activeRequests>0;
    private int _testDone,_testTotal;
    private readonly HashSet<string> _testKeys=new(StringComparer.Ordinal);
    private readonly HashSet<string> _updating=new(StringComparer.Ordinal);
    private long _lastTrafficId;
    private int _lastTrafficEpoch=-1;
    private int _visibilityVersion;
    private bool _sentFirst;
    private long _lastPublish;
    private DispatcherQueueTimer? _flushTimer;
    private object[] _kernels=[];

    internal HtmlDesktopBridge(NativeHtmlWindow window,DispatcherQueue queue,Action close)
    {
        _window=window;_queue=queue;_close=close;_core=window.Core!;
        _core.WebMessageReceived+=OnMessage;
        _core.NavigationStarting+=OnNavigation;
        _core.NewWindowRequested+=OnNewWindow;
        _core.Settings.AreDevToolsEnabled=NativeUiReview.Enabled;
        _core.Settings.AreDefaultContextMenusEnabled=false;
        _core.Settings.IsStatusBarEnabled=false;
        _core.Settings.IsZoomControlEnabled=false;
        _core.Settings.AreBrowserAcceleratorKeysEnabled=false;
        AppState.NodesChanged+=CatalogChanged;
        AppState.SubscriptionUpdated+=CatalogChanged;
        AppState.UiSettingsChanged+=SettingsChanged;
        ConnectionService.StateChanged+=ConnectionChanged;
        NetworkDiagnosticsService.Changed+=DiagnosticsChanged;
        CurrentExitService.Changed+=DiagnosticsChanged;
        LogBus.Appended+=LogAppended;
        SubscriptionLogService.Changed+=SublogChanged;
        _timer=queue.CreateTimer();_timer.Interval=TimeSpan.FromSeconds(1);_timer.Tick+=(_,_)=>Publish();_timer.Start();
        // A host clock keeps minimized canvas frames independent of browser timer/RAF throttling.
        _backgroundFrames=queue.CreateTimer();_backgroundFrames.Interval=TimeSpan.FromMilliseconds(1000d/24);
        _backgroundFrames.Tick+=(_,_)=>
        {
            if(!_disposed&&_ready&&!App.IsShuttingDown&&_window.IsMinimized&&CurrentPage=="conn"&&ConnectionService.IsConnected)
            {
                try{Post(new{type="traffic.frame"});}
                catch(Exception ex){_backgroundFrames.Stop();LogBus.App("后台流量绘制投递失败："+ex.GetType().Name,"Debug");}
            }
        };
        _window.BoundsChanged+=WindowBoundsChanged;
        _=Task.Run(()=>SubscriptionLogService.EnsureTodayLoaded());
        _=Task.Run(()=>
        {
            var kernels=KernelCatalog.All.Select(k=>(object)new{name=k.Display,installed=KernelCatalog.IsInstalled(k.Engine),version=NativeUiReview.Enabled?null:KernelCatalog.ReadVersion(k.Engine)}).ToArray();
            Enqueue(()=>{_kernels=kernels;_settingsDirty=true;Publish();});
        });
    }

    internal async Task ConfigureAsync()
    {
        _core.SetVirtualHostNameToFolderMapping("neo.ui",Path.Combine(AppContext.BaseDirectory,"Assets","HtmlShell"),CoreWebView2HostResourceAccessKind.DenyCors);
        await _core.AddScriptToExecuteOnDocumentCreatedAsync("window.__NEO_DESKTOP=true;");
    }
    private void OnNavigation(CoreWebView2 sender,CoreWebView2NavigationStartingEventArgs e)
    {
        if(!string.Equals(e.Uri,Origin,StringComparison.OrdinalIgnoreCase)){e.Cancel=true;return;}
        // A new document must receive its full catalog before any incremental ticks.
        _ready=false;_sentFirst=false;
    }
    private void OnNewWindow(CoreWebView2 sender,CoreWebView2NewWindowRequestedEventArgs e)
    {e.Handled=true;if(e.IsUserInitiated&&Uri.TryCreate(e.Uri,UriKind.Absolute,out var uri)&&uri.Scheme is "https" or "http")Process.Start(new ProcessStartInfo(uri.AbsoluteUri){UseShellExecute=true});}
    private void Enqueue(Action action)
    {if(_disposed||App.IsShuttingDown)return;if(_queue.HasThreadAccess)action();else _queue.TryEnqueue(()=>{if(!_disposed&&!App.IsShuttingDown)action();});}
    private void CatalogChanged()=>Enqueue(()=>{_catalogDirty=true;Publish();});
    private void SettingsChanged()=>Enqueue(()=>{_settingsDirty=true;_catalogDirty=true;Publish();});
    private void ConnectionChanged()=>Enqueue(()=>{_catalogDirty=true;_diagDirty=true;Publish();});
    private void DiagnosticsChanged()=>Enqueue(()=>{_diagDirty=true;Publish();});
    private void LogAppended(LogEntry _)=>Enqueue(()=>_logDirty=true);
    private void SublogChanged()=>Enqueue(()=>{_sublogDirty=true;_catalogDirty=true;Publish();});
    private void Post(object value)
    {if(!_disposed&&!App.IsShuttingDown)_core.PostWebMessageAsJson(JsonSerializer.Serialize(value));}
    internal void Notify(string text)=>Enqueue(()=>Post(new{type="toast",text}));
    internal void Navigate(string page)=>Enqueue(()=>Post(new{type="navigate",page}));
    internal string CurrentPage {get;private set;}="conn";

    private async void OnMessage(CoreWebView2 sender,CoreWebView2WebMessageReceivedEventArgs e)
    {
        if(_disposed||!string.Equals(e.Source,Origin,StringComparison.OrdinalIgnoreCase))return;
        string? id=null;
        _activeRequests++;
        try
        {
            var raw=e.WebMessageAsJson;if(raw.Length>1_048_576)throw new InvalidOperationException("请求过大");
            using var doc=JsonDocument.Parse(raw);var root=doc.RootElement;id=root.GetProperty("id").GetString();
            if(id==null||id.Length>64)throw new InvalidOperationException("无效请求");
            var action=root.GetProperty("action").GetString()??"";var payload=root.GetProperty("payload");
            var result=await DispatchAsync(action,payload);
            Post(new{id,ok=true,data=result});Publish();
        }
        catch(Exception ex)
        {
            if(id!=null)Post(new{id,ok=false,error=ex.Message});
            LogBus.App("界面操作失败："+ex.GetType().Name+" · "+ex.Message,"Warning");
        }
        finally{_activeRequests--;}
    }
    private static string Text(JsonElement p,string name)=>p.GetProperty(name).GetString()?.Trim()??"";
    private static NodeItem FindNode(string key)=>AppState.Nodes.FirstOrDefault(n=>AppState.NodeKey(n)==key)??throw new InvalidOperationException("节点已更新，请重新选择");

    private async Task<object?> DispatchAsync(string action,JsonElement p)
    {
        switch(action)
        {
#if NEO_UI_REVIEW
            case "review.collect":
                GC.Collect(GC.MaxGeneration,GCCollectionMode.Forced,blocking:true,compacting:true);
                return new {collections=Enumerable.Range(0,3).Select(GC.CollectionCount).ToArray()};
#endif
            case "ready":_ready=true;_catalogDirty=_settingsDirty=_logDirty=_sublogDirty=_diagDirty=true;Publish();return null;
            case "view.ready":return null;
            case "view.page":var page=Text(p,"page");if(page is "conn" or "nodes" or "subs" or "sublog" or "log" or "settings")CurrentPage=page;return null;
            case "window.drag":_window.BeginDrag();return null;
            case "window.maximize":_window.ToggleMaximize();return null;
            case "window.minimize":_window.Minimize();return null;
            case "window.close":_close();return null;
            case "connection.toggle":
                if(NativeUiReview.Enabled)throw new InvalidOperationException("隔离审核不连接实际内核");
                if(ConnectionService.IsConnecting){ConnectionService.Disconnect("用户取消连接");return null;}
                if(ConnectionService.IsConnected)ConnectionService.Disconnect("用户断开");
                else{var selected=AppState.CurrentNode??throw new InvalidOperationException("请先选择节点");var result=await ConnectionService.ConnectAsync(selected,ct:_lifetime.Token);if(!result.Ok)throw new InvalidOperationException(result.Error??"连接失败");}
                return null;
            case "node.select":
                var node=FindNode(Text(p,"key"));
                var activeNode=ConnectionService.IsConnected?ConnectionService.ConnectedNode:AppState.CurrentNode;
                if(activeNode!=null&&AppState.NodeKey(activeNode)==AppState.NodeKey(node))return null;
                if(ConnectionService.IsConnecting)throw new InvalidOperationException("正在建立连接，请稍后切换节点");
                if(ConnectionService.IsConnected)
                {var switched=await ConnectionService.SwitchNodeFastAsync(node,ct:_lifetime.Token);if(!switched.Ok)throw new InvalidOperationException(switched.Err??"切换失败");}
                AppState.SetCurrentNode(node);AppState.NotifyNodesChanged();return null;
            case "connection.mode":
                var mode=Text(p,"mode");if(mode is not("rule" or "global" or "direct"))throw new InvalidOperationException("无效代理模式");
                var old=AppState.State.ProxyMode;AppState.State.ProxyMode=mode;
                if(ConnectionService.IsConnected&&!await ConnectionService.ReloadForSettingsAsync()){AppState.State.ProxyMode=old;throw new InvalidOperationException("应用代理模式失败，已恢复原设置");}
                AppState.Save();AppState.NotifyUiSettingsChanged();return null;
            case "diagnostics.run":
                if(!ConnectionService.IsConnected)throw new InvalidOperationException("连接后再检测当前节点");
                _=NetworkDiagnosticsService.RunAsync();return null;
            case "exit.refresh":if(!ConnectionService.IsConnected)throw new InvalidOperationException("连接后再检测当前出口");_ = CurrentExitService.RunAsync(true);return null;
            case "nodes.test":StartTest(p);return null;
            case "nodes.exitTest":return await TestExitRegionsAsync(p);
            case "nodes.import":var added=AppState.AddManualLinks(Text(p,"links").Split('\n'));return new{added=added.Added,failed=added.Failed};
            case "nodes.importContent":return await ImportNodeContentAsync(p);
            case "nodes.importFile":return await ImportNodeFileAsync();
            case "nodes.warp":
                if(NativeUiReview.Enabled)throw new InvalidOperationException("隔离审核不注册外部服务");
                var warp=await WarpService.RegisterAsync(_lifetime.Token);if(!warp.Ok||warp.Link==null)throw new InvalidOperationException(warp.Message);AppState.AddManualLinks([warp.Link]);return new{message=warp.Message};
            case "subscription.enabled":AppState.SetSubscriptionEnabled(Text(p,"id"),p.GetProperty("enabled").GetBoolean());_catalogDirty=true;return null;
            case "subscription.primary":
                var primaryId=Text(p,"id");
                if(primaryId.Length>0&&!AppState.Subscriptions.Any(sub=>sub.Id==primaryId))throw new InvalidOperationException("订阅已移除");
                AppState.State.PrimarySubscriptionId=primaryId;AppState.Save();_catalogDirty=true;return null;
            case "subscription.save":
                var url=Neo.Core.Parsing.SubscriptionSource.Normalize(Text(p,"url"));
                var name=Text(p,"name");if(name.Length>80)throw new InvalidOperationException("订阅名称最多 80 个字符");
                var subId=p.TryGetProperty("id",out var supplied)&&supplied.ValueKind==JsonValueKind.String?supplied.GetString():null;
                var sourceName=Uri.TryCreate(url,UriKind.Absolute,out var sourceUri)&&sourceUri.Scheme is "http" or "https"?sourceUri.Host:Path.GetFileNameWithoutExtension(url);
                if(subId==null)AppState.AddSubscription(url,name.Length==0?sourceName:name);
                else AppState.EditSubscription(subId,url,name.Length==0?sourceName:name);
                AppState.NotifyNodesChanged();_catalogDirty=true;return null;
            case "subscription.delete":AppState.RemoveSubscriptionById(Text(p,"id"));_catalogDirty=true;return null;
            case "subscription.update":
                var updateId=Text(p,"id");if(!_updating.Add(updateId))throw new InvalidOperationException("该订阅正在更新");_catalogDirty=true;Publish();
                try{var result=await AppState.RefreshOneAsync(updateId);if(!result.Ok)throw new InvalidOperationException(result.Error??"订阅更新失败");}
                finally{_updating.Remove(updateId);_catalogDirty=true;}return null;
            case "subscription.updateAll":
                foreach(var sub in AppState.Subscriptions.Where(s=>s.Enabled))_updating.Add(sub.Id);_catalogDirty=true;Publish();
                try{var result=await AppState.RefreshSubscriptionAsync();if(!result.Ok)throw new InvalidOperationException(result.Error??"订阅更新失败");}
                finally{_updating.Clear();_catalogDirty=true;}return null;
            case "settings.set":await SetSettingAsync(Text(p,"title"),p.GetProperty("value"));return null;
            case "ports.apply":await ApplyPortsAsync(p.GetProperty("socks").GetInt32(),p.GetProperty("http").GetInt32());return null;
            case "settings.coreMap":ApplyCoreMap(p.GetProperty("map"));return null;
            case "settings.action":return await RunSettingsActionAsync(Text(p,"title"));
            case "settings.install":return await InstallCheckedUpdateAsync(Text(p,"token"));
            case "clipboard.copy":var data=new DataPackage();data.SetText(Text(p,"text"));Clipboard.SetContent(data);Clipboard.Flush();return null;
            case "file.saveText":return await SaveTextAsync(Text(p,"name"),p.GetProperty("text").GetString()??"");
            case "logs.clear":LogBus.Clear();_logDirty=true;return null;
            case "sublogs.clear":SubscriptionLogService.Clear();_sublogDirty=true;return null;
            case "logs.directory":Process.Start(new ProcessStartInfo("explorer.exe",NeoPaths.LogDir){UseShellExecute=true});return null;
            default:throw new InvalidOperationException("不支持的界面操作");
        }
    }

    private void StartTest(JsonElement payload)
    {
        if(_testRunning||_exitRegionsRunning)throw new InvalidOperationException("已有节点测试正在进行");
        if(NativeUiReview.Enabled)throw new InvalidOperationException("隔离审核不执行实际节点测速");
        var keys=payload.GetProperty("keys").EnumerateArray().Select(k=>k.GetString()??"").ToHashSet();
        var nodes=AppState.Nodes.Where(n=>keys.Contains(AppState.NodeKey(n))).ToArray();if(nodes.Length==0)throw new InvalidOperationException("没有可测节点");
        var tcp=!AppState.State.RealDelayTestEnabled||(payload.TryGetProperty("tcp",out var v)&&v.GetBoolean());
        _testing.Dispose();_testing=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);_testRunning=true;_testDone=0;_testTotal=nodes.Length;
        foreach(var key in keys)_testKeys.Add(key);Publish();
        _=Task.Run(async()=>
        {
            try
            {
                if(tcp)
                {
                    await Parallel.ForEachAsync(nodes,new ParallelOptions{CancellationToken=_testing.Token,MaxDegreeOfParallelism=6},async(n,ct)=>
                    {
                        var watch=Stopwatch.StartNew();var ms=-1;
                        try{using var socket=new System.Net.Sockets.TcpClient();await socket.ConnectAsync(n.Server,n.Port,ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5),ct);ms=(int)Math.Max(1,watch.ElapsedMilliseconds);}catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}catch{}
                        Enqueue(()=>{n.TcpDelay=ms;DelayCache.SetTcp(n,ms);_testKeys.Remove(AppState.NodeKey(n));_testDone++;_catalogDirty=true;Publish();});
                    });
                }
                else await DelayTestRunner.RunAsync(nodes,NeoPaths.RuntimeDir,(n,ms)=>Enqueue(()=>
                {n.RealDelay=ms;DelayCache.Set(n,ms);_testKeys.Remove(AppState.NodeKey(n));_testDone++;_catalogDirty=true;Publish();}),_testing.Token,true,null,null);
            }
            catch(OperationCanceledException){}
            catch(Exception ex){Notify("测速失败："+ex.Message);}
            finally{Enqueue(()=>{_testRunning=false;_testKeys.Clear();_catalogDirty=true;Publish();});}
        });
    }

    private async void WindowBoundsChanged()
    {
        if(_disposed)return;
        var version=++_visibilityVersion;
        if(_window.IsMinimized)
        {
            // Keep JavaScript, real samples and the 24 Hz host clock alive, but
            // hide Chromium's native input surface so it cannot cover the desktop.
            UiActivity.SetVisible(false);_window.SetContentVisible(false);
            if(_core.IsSuspended)_core.Resume();
            _backgroundFrames.Start();Publish();return;
        }
        _backgroundFrames.Stop();
        if(!_window.IsVisible)
        {
            _window.SetContentVisible(false);UiActivity.SetVisible(false);
            try{await Task.Delay(1500,_lifetime.Token);if(!_disposed&&version==_visibilityVersion){await _core.TrySuspendAsync();if(!_disposed&&_window.IsVisible&&!_window.IsMinimized)_core.Resume();}}
            catch(OperationCanceledException){}catch(Exception ex){LogBus.App("界面后台挂起失败："+ex.GetType().Name,"Debug");}
            return;
        }
        UiActivity.SetVisible(true);
        _window.SetContentVisible(true);
        if(_core.IsSuspended)_core.Resume();
        Publish();
    }
    internal void Publish()
    {
        if(_disposed||!_ready||App.IsShuttingDown||(_sentFirst&&!_window.IsVisible&&!_window.IsMinimized))return;
        if(_sentFirst&&Environment.TickCount64-_lastPublish<80)
        {
            if(_flushTimer==null){_flushTimer=_queue.CreateTimer();_flushTimer.IsRepeating=false;_flushTimer.Interval=TimeSpan.FromMilliseconds(85);_flushTimer.Tick+=(_,_)=>Publish();}
            if(!_flushTimer.IsRunning)_flushTimer.Start();return;
        }
        try{Post(new{type="snapshot",data=BuildSnapshot()});_sentFirst=true;_lastPublish=Environment.TickCount64;}
        catch(Exception ex){_lastTrafficEpoch=-1;_catalogDirty=_settingsDirty=_logDirty=_sublogDirty=_diagDirty=true;LogBus.App("界面数据刷新失败："+ex.GetType().Name,"Warning");}
    }

    private Dictionary<string,object?> BuildSnapshot()
    {
        var s=AppState.State;var connected=ConnectionService.IsConnected;var selected=connected?ConnectionService.ConnectedNode??AppState.CurrentNode:AppState.CurrentNode;
        var sample=StatsSampler.Latest;var epoch=ConnectionService.SessionEpoch;
        var traffic=StatsSampler.CaptureTraffic();var latest=traffic.Samples.LastOrDefault();
        var includeHistory=!_sentFirst||_lastTrafficEpoch!=epoch||latest!=null&&(latest.Id>_lastTrafficId+1||latest.Id<_lastTrafficId);
        var exit=CurrentExitService.Current;var confirmed=connected&&exit.Epoch==epoch&&exit.Result.Confirmed;
        var today=UsageTracker.Split(UsageTracker.TodayBytes);
        var data=new Dictionary<string,object?>{
            ["background"]=_window.IsMinimized,
            ["connected"]=connected,["connecting"]=ConnectionService.IsConnecting,["connectedAt"]=connected&&ConnectionService.ConnectedAt.HasValue?new DateTimeOffset(ConnectionService.ConnectedAt.Value).ToUnixTimeMilliseconds():0,
            ["selected"]=selected==null?null:AppState.NodeKey(selected),["mode"]=s.ProxyMode,["floating"]=s.FloatingTrafficEnabled,["tun"]=s.UseTun,
            ["proxyTaken"]=connected&&SystemProxy.IsEffective,["ports"]=new{socks=LocalPorts.Socks,http=LocalPorts.Http},
            ["exit"]=new{confirmed,region=confirmed?CurrentExitDisplay.Region(exit.Result.Country):"—",ip=confirmed?exit.Result.Ip:"",detail=exit.IsRunning?"正在检测当前出口":connected?exit.Result.Detail:"连接后检测当前出口"},
            ["usage"]=new{todayValue=today.Value,todayUnit=today.Unit,total=UsageTracker.Format(UsageTracker.TotalBytes),week=UsageTracker.Format(UsageTracker.WeekBytes),month=UsageTracker.Format(UsageTracker.MonthBytes)},
            ["stats"]=new{id=latest?.Id??0,session=epoch,sampledAt=latest?.SampledAt??0,upBps=latest?.UpBps??0,downBps=latest?.DownBps??0,peakBps=traffic.PeakBps,
                history=includeHistory?traffic.Samples.Select(p=>new{id=p.Id,sampledAt=p.SampledAt,upBps=p.UpBps,downBps=p.DownBps}).ToArray():null},
            ["days"]=UsageTracker.DailySeries(30).Select(x=>x/1073741824d).ToArray(),
            ["core"]=new{name=ConnectionService.CoreKind,version=ConnectionService.CoreKind=="sing-box"?ConnectionService.CachedSingBoxVersion:ConnectionService.CachedXrayVersion,memory=StatsSampler.CoreMemoryMb,active=connected?StatsSampler.ActiveConnections:0,participants=connected?1:0,strategy=ConnectionService.FixedExitActive?"固定出口":"单节点",upload=UsageTracker.Format(sample?.Uplink??0),download=UsageTracker.Format(sample?.Downlink??0)},
            ["testing"]=new{running=_testRunning,done=_testDone,total=_testTotal,keys=_testKeys.ToArray()}
        };
        if(_catalogDirty)
        {
            var nodeList=AppState.Nodes.ToList();
            if(connected&&selected!=null&&!nodeList.Any(n=>AppState.NodeKey(n)==AppState.NodeKey(selected)))nodeList.Add(selected);
            var nodes=nodeList.ToArray();
            data["nodes"]=nodes.Select(n=>new{id=AppState.NodeKey(n),f=NodeGrouper.RegionOf(n.Name),name=NodeDisplayName.WithoutFlag(n.Name),rawName=n.Name,region=NodeGrouper.RegionDisplay(NodeGrouper.RegionOf(n.Name)),proto=AppState.ProtocolName(n.Protocol),trans=AppState.TransportName(n),real=s.RealDelayTestEnabled?n.RealDelay:n.TcpDelay,rawReal=n.RealDelay,tcp=n.TcpDelay,server=n.Server,port=n.Port,src=n.SourceSubscription??"手动添加"}).ToArray();
            data["subs"]=AppState.Subscriptions.Select(sub=>new{id=sub.Id,name=sub.Name,url=Uri.TryCreate(sub.Url,UriKind.Absolute,out var su)?su.GetLeftPart(UriPartial.Path):sub.Url,fullUrl=sub.Url,nodes=sub.NodeCount,used=Math.Round((sub.SubUpload+(double)sub.SubDownload)/1073741824,1),cap=sub.SubTotal>0?Math.Round(sub.SubTotal/1073741824d,1):(double?)null,exp=sub.SubExpireUnix>0?DateTimeOffset.FromUnixTimeSeconds(sub.SubExpireUnix).LocalDateTime.ToString("yyyy-MM-dd"):null,state=sub.Enabled?"正常":"已停用",tone=sub.Enabled?"g":"dim",primary=s.PrimarySubscriptionId is null?selected!=null&&selected.SourceSubscription==sub.Name:s.PrimarySubscriptionId==sub.Id,enabled=sub.Enabled,last=sub.LastFetchUnix>0?DateTimeOffset.FromUnixTimeSeconds(sub.LastFetchUnix).LocalDateTime.ToString("MM-dd HH:mm"):"从未更新",cost="—",regions="",updating=_updating.Contains(sub.Id)}).ToArray();
            var last=AppState.LastFetchLocal;
            data["plan"]=new{auto=s.AutoUpdate,hours=s.AutoUpdateHours,last=last?.ToString("MM-dd HH:mm"),next=s.AutoUpdate?last?.AddHours(s.AutoUpdateHours).ToString("MM-dd HH:mm"):null};
            _catalogDirty=false;
        }
        if(_settingsDirty){data["settings"]=SettingsSnapshot();data["kernels"]=_kernels;data["coreMap"]=s.CoreMap;data["gameProcesses"]=s.GameDirectProcesses;_settingsDirty=false;}
        if(_logDirty){data["logs"]=LogBus.TailSnapshot(600).Select(l=>new[]{l.Time.ToString("HH:mm:ss"),l.Level.ToLowerInvariant() switch{"warning"=>"warn",var level=>level},l.Text}).ToArray();_logDirty=false;}
        if(_sublogDirty){data["sublogs"]=SubscriptionLogService.Snapshot().Reverse().Take(500).Select(l=>new[]{l.StartTime.ToString("MM-dd HH:mm"),l.Subscription,l.Result,l.Error??l.ParseError??(l.NodeCount.HasValue?l.NodeCount+" 个节点":"—"),l.ElapsedMs.HasValue?(l.ElapsedMs.Value/1000).ToString("0.0")+" s":"—",l.HttpStatus,l.Host}).ToArray();_sublogDirty=false;}
        if(_diagDirty)
        {
            var diagnostic=NetworkDiagnosticsService.Current;var valid=connected&&diagnostic.Epoch==epoch;
            var sum=ConnectivityDisplay.Summarize(diagnostic.Results.Select(r=>r.State).ToArray(),valid,diagnostic.IsRunning,diagnostic.CompletedAt.HasValue);
            data["diagnostics"]=new{running=valid&&diagnostic.IsRunning,percentage=sum.Percentage,when=valid?diagnostic.CompletedAt?.LocalDateTime.ToString("HH:mm"):null,summary=valid?(diagnostic.IsRunning?$"检测中 {sum.Finished} / {sum.Total}":$"{sum.Passed} / {sum.Total} 通过"):"尚未检测",results=diagnostic.Results.Select(r=>new{name=r.Target.Name,group=r.Target.Group,url=r.Target.Url.AbsoluteUri,ms=r.ElapsedMs,status=!valid?"pending":r.State=="可达"?"ok":r.State=="检测中"?"testing":r.State=="未检测"?"pending":"fail",label=valid?ConnectivityDisplay.Site(r.State):"未检测",route=valid?r.Route.Path:"未确认",response=valid?$"{r.ElapsedMs} ms · HTTP {r.HttpStatus?.ToString()??"—"}":"—",detail=valid?r.Detail:""}).ToArray()};_diagDirty=false;
        }
        _lastTrafficId=latest?.Id??0;_lastTrafficEpoch=epoch;
        return data;
    }

    public void Dispose()
    {
        if(_disposed)return;_disposed=true;_lifetime.Cancel();_testing.Cancel();_timer.Stop();_backgroundFrames.Stop();_flushTimer?.Stop();
        _core.WebMessageReceived-=OnMessage;_core.NavigationStarting-=OnNavigation;_core.NewWindowRequested-=OnNewWindow;
        AppState.NodesChanged-=CatalogChanged;AppState.SubscriptionUpdated-=CatalogChanged;AppState.UiSettingsChanged-=SettingsChanged;
        ConnectionService.StateChanged-=ConnectionChanged;NetworkDiagnosticsService.Changed-=DiagnosticsChanged;CurrentExitService.Changed-=DiagnosticsChanged;
        LogBus.Appended-=LogAppended;SubscriptionLogService.Changed-=SublogChanged;_window.BoundsChanged-=WindowBoundsChanged;
    }
}
