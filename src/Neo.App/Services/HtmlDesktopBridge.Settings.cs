using Microsoft.Win32;
using Neo.App.Ui;
using Neo.Core;
using Neo.Core.Core;
using Neo.Core.Models;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Windows.Storage.Pickers;

namespace Neo.App.Services;

internal sealed partial class HtmlDesktopBridge
{
    private const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly Dictionary<string,object> _checkedUpdates=new();
    private static bool AutoStartEnabled()
    {using var key=Registry.CurrentUser.OpenSubKey(RunKey);return key?.GetValue("neo") is string value&&value.Length>0;}

    private Dictionary<string,object?> SettingsSnapshot()
    {
        var s=AppState.State;
        return new(){
            ["开机自动启动"]=AutoStartEnabled(),["启动后自动连接"]=s.AutoConnect,["启动自动测速"]=s.StartupSpeedTest,
            ["启动时最小化到托盘"]=s.StartMinimized,["关闭窗口时最小化到托盘"]=s.MinimizeToTray,
            ["真实 IP 显示"]=s.ShowRealIp,["真连测速"]=s.RealDelayTestEnabled,["紧凑布局"]=s.CompactLayout,
            ["界面语言"]=s.Language=="en-US"?"English":s.Language=="system"?"跟随系统":"简体中文",
            ["SOCKS5 端口"]=s.SocksPort.ToString(),["HTTP 端口"]=s.HttpPort.ToString(),
            ["允许来自局域网的连接"]=s.AllowLan,["自动修复系统代理"]=s.AutoRepairSystemProxy,["自动故障转移"]=s.FailoverEnabled,
            ["检查预发布内核"]=s.CheckKernelPreRelease,["内核异常退出后自动重启"]=s.AutoRestartCore,
            ["国内地址直连"]=s.BypassCn,["DNS 服务器"]=s.DnsServer,["分流策略"]="内置策略",
            ["游戏直连保护"]=s.GameDirectEnabled,["用 Rust 生成配置（实验）"]=s.UseRustConfig,
            ["日志级别"]=s.LogLevel switch{"debug"=>"调试","info"=>"信息","error"=>"错误",_=>"警告"},
            ["日志目录"]="选择",["日志保留"]=s.LogRetentionDays+" 天",["版本"]=AppUpdateService.CurrentVersion,
            ["运行框架"]="C# · .NET 10 · WebView2",["自动检查更新"]=s.AutoCheckUpdate
        };
    }

    private async Task SetSettingAsync(string title,JsonElement value)
    {
        var s=AppState.State;
        var toggles=new Dictionary<string,(Func<bool> Read,Action<bool> Write,bool Reload)>{
            ["启动后自动连接"]=( ()=>s.AutoConnect,v=>s.AutoConnect=v,false),
            ["启动自动测速"]=( ()=>s.StartupSpeedTest,v=>s.StartupSpeedTest=v,false),
            ["启动时最小化到托盘"]=( ()=>s.StartMinimized,v=>s.StartMinimized=v,false),
            ["关闭窗口时最小化到托盘"]=( ()=>s.MinimizeToTray,v=>s.MinimizeToTray=v,false),
            ["真实 IP 显示"]=( ()=>s.ShowRealIp,v=>s.ShowRealIp=v,false),
            ["真连测速"]=( ()=>s.RealDelayTestEnabled,v=>s.RealDelayTestEnabled=v,false),
            ["紧凑布局"]=( ()=>s.CompactLayout,v=>s.CompactLayout=v,false),
            ["允许来自局域网的连接"]=( ()=>s.AllowLan,v=>s.AllowLan=v,true),
            ["自动修复系统代理"]=( ()=>s.AutoRepairSystemProxy,v=>s.AutoRepairSystemProxy=v,false),
            ["自动故障转移"]=( ()=>s.FailoverEnabled,v=>s.FailoverEnabled=v,false),
            ["检查预发布内核"]=( ()=>s.CheckKernelPreRelease,v=>s.CheckKernelPreRelease=v,false),
            ["内核异常退出后自动重启"]=( ()=>s.AutoRestartCore,v=>s.AutoRestartCore=v,false),
            ["国内地址直连"]=( ()=>s.BypassCn,v=>s.BypassCn=v,true),
            ["游戏直连保护"]=( ()=>s.GameDirectEnabled,v=>s.GameDirectEnabled=v,true),
            ["用 Rust 生成配置（实验）"]=( ()=>s.UseRustConfig,v=>s.UseRustConfig=v,true),
            ["自动检查更新"]=( ()=>s.AutoCheckUpdate,v=>s.AutoCheckUpdate=v,false),
            ["自动更新订阅"]=( ()=>s.AutoUpdate,v=>s.AutoUpdate=v,false)
        };
        if(toggles.TryGetValue(title,out var toggle))
        {
            var old=toggle.Read();var next=value.GetBoolean();toggle.Write(next);
            if(toggle.Reload&&ConnectionService.IsConnected&&!await ConnectionService.ReloadForSettingsAsync())
            {toggle.Write(old);throw new InvalidOperationException("应用设置失败，已恢复原值");}
        }
        else switch(title)
        {
            case "开机自动启动":
                if(NativeUiReview.Enabled)throw new InvalidOperationException("隔离审核不修改开机启动");
                using(var key=Registry.CurrentUser.CreateSubKey(RunKey))
                {if(value.GetBoolean())key.SetValue("neo",'"'+NeoPaths.LauncherPath+'"');else key.DeleteValue("neo",false);}break;
            case "悬浮球":FloatingTrafficService.SetEnabled(value.GetBoolean());break;
            case "TUN 模式":
                var tunChange=await ConnectionService.SetTunEnabledAsync(value.GetBoolean(),_lifetime.Token);
                if(!tunChange.Ok)throw new InvalidOperationException(tunChange.Error);
                break;
            case "DNS 服务器":
                var dns=value.GetString()?.Trim()??"";
                if(!System.Net.IPAddress.TryParse(dns,out _)&&!(Uri.TryCreate(dns,UriKind.Absolute,out var uri)&&uri.Scheme=="https"))throw new InvalidOperationException("请输入有效 DNS 地址或 HTTPS DNS URL");
                var oldDns=s.DnsServer;s.DnsServer=dns;if(ConnectionService.IsConnected&&!await ConnectionService.ReloadForSettingsAsync()){s.DnsServer=oldDns;throw new InvalidOperationException("应用 DNS 失败");}break;
            case "界面语言":s.Language=value.GetString() switch{"English"=>"en-US","跟随系统"=>"system",_=>"zh-CN"};break;
            case "日志级别":s.LogLevel=value.GetString() switch{"调试"=>"debug","信息"=>"info","警告"=>"warning","错误"=>"error",_=>throw new InvalidOperationException("无效日志级别")};break;
            case "日志保留":var days=int.Parse((value.GetString()??"").Split(' ')[0]);if(days is not(7 or 15 or 30))throw new InvalidOperationException("日志保留可选 7、15 或 30 天");s.LogRetentionDays=days;AppState.ApplyLogSettingsToRuntime();break;
            case "更新间隔":var hours=value.GetInt32();if(hours<1||hours>720)throw new InvalidOperationException("更新间隔范围为 1–720 小时");s.AutoUpdateHours=hours;break;
            case "自定义游戏进程":
                var processes=(value.GetString()??"").Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if(processes.Any(p=>p.Length>128||!p.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)||p.IndexOfAny(Path.GetInvalidFileNameChars())>=0))throw new InvalidOperationException("请输入不含路径的 .exe 进程名");
                var oldProcesses=s.GameDirectProcesses;s.GameDirectProcesses=processes;
                if(ConnectionService.IsConnected&&!await ConnectionService.ReloadForSettingsAsync()){s.GameDirectProcesses=oldProcesses;throw new InvalidOperationException("应用游戏直连进程失败");}break;
            default:throw new InvalidOperationException("该设置不可修改");
        }
        AppState.Save();AppState.NotifyUiSettingsChanged();_catalogDirty=_settingsDirty=true;
    }

    private async Task ApplyPortsAsync(int socks,int http)
    {
        var error=LocalPorts.Validate(socks,http);if(error!=null)throw new InvalidOperationException(error);
        var s=AppState.State;var oldSocks=s.SocksPort;var oldHttp=s.HttpPort;
        if(socks==LocalPorts.Socks&&http==LocalPorts.Http)return;
        foreach(var port in new[]{socks,http})
        {
            if(ConnectionService.IsConnected&&(port==LocalPorts.Socks||port==LocalPorts.Http))continue;
            var listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,port);
            try{listener.Start();}catch(System.Net.Sockets.SocketException){throw new InvalidOperationException($"端口 {port} 已被占用");}finally{listener.Stop();}
        }
        // Keep the running endpoints unchanged until ConnectionService has torn
        // down the old session and is ready to apply the desired ports.
        s.SocksPort=socks;s.HttpPort=http;
        if(!await ConnectionService.ReloadForSettingsAsync())
        {s.SocksPort=oldSocks;s.HttpPort=oldHttp;AppState.Save();throw new InvalidOperationException("应用端口失败，已恢复原设置；请查看运行日志");}
        AppState.Save();AppState.NotifyUiSettingsChanged();_settingsDirty=true;
    }
    private void ApplyCoreMap(JsonElement map)
    {
        var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in map.EnumerateObject())
        {
            if(!Enum.TryParse<NodeProtocol>(entry.Name,true,out var protocol))throw new InvalidOperationException("无效协议");
            var engine=entry.Value.GetString()??"";if(engine=="自动")engine="auto";
            if(engine is not("auto" or "xray" or "sing-box" or "mihomo" or "naive"))throw new InvalidOperationException("无效内核");
            result[protocol.ToString()]=engine;
        }
        AppState.State.CoreMap=result;AppState.SyncCoreMap();AppState.Save();AppState.NotifyUiSettingsChanged();_settingsDirty=true;
    }
    private FileSavePicker SavePicker(string name,string extension)
    {
        var picker=new FileSavePicker{SuggestedFileName=Path.GetFileNameWithoutExtension(name),SuggestedStartLocation=PickerLocationId.Desktop};
        picker.FileTypeChoices.Add(extension.TrimStart('.').ToUpperInvariant(),new List<string>{extension});
        WinRT.Interop.InitializeWithWindow.Initialize(picker,_window.Handle);return picker;
    }
    private async Task<object?> SaveTextAsync(string name,string text)
    {
        if(NativeUiReview.Enabled)throw new InvalidOperationException("隔离审核不打开系统保存窗口");
        name=Path.GetFileName(name);if(name.Length==0||text.Length>16_777_216)throw new InvalidOperationException("无效文件内容");
        var extension=Path.GetExtension(name);if(extension.Length==0)extension=".txt";
        var file=await SavePicker(name,extension).PickSaveFileAsync();if(file==null)return new{cancelled=true};
        await File.WriteAllTextAsync(file.Path,text,new UTF8Encoding(true),_lifetime.Token);return new{saved=true};
    }

    private async Task<object?> RunSettingsActionAsync(string title)
    {
        if(NativeUiReview.Enabled)throw new InvalidOperationException("隔离审核不执行系统操作或下载");
        switch(title)
        {
            case "xray":var xray=await UpdateChecker.CheckXrayAsync(await RefreshKernelVersionAsync(CoreEngine.Xray),AppState.State.CheckKernelPreRelease);return CheckedUpdate(title,xray.Ok,xray.Message,xray.Release);
            case "sing-box":var sing=await UpdateChecker.CheckSingBoxAsync(await RefreshKernelVersionAsync(CoreEngine.SingBox));return CheckedUpdate(title,sing.Ok,sing.Message,sing.Release);
            case "mihomo":var mihomo=await UpdateChecker.CheckMihomoAsync(await RefreshKernelVersionAsync(CoreEngine.Mihomo));return CheckedUpdate(title,mihomo.Ok,mihomo.Message,mihomo.Release);
            case "naive":var naive=await UpdateChecker.CheckNaiveAsync(await RefreshKernelVersionAsync(CoreEngine.Naive));return CheckedUpdate(title,naive.Ok,naive.Message,naive.Release);
            case "软件更新":var update=await AppUpdateService.CheckAsync(_lifetime.Token);return CheckedUpdate(title,update.Ok,update.Ok?(update.HasNew?"发现新版本 "+update.LatestTag:"当前已是最新版本"):update.Error,update.HasNew?update:null);
            case "分流数据":var geo=await GeoDataUpdater.UpdateAllAsync(Notify,_lifetime.Token);if(!geo.Ok)throw new InvalidOperationException(geo.Message);return new{title,message=geo.Message};
            case "查看日志":case "日志目录":
                if(title=="查看日志"){Process.Start(new ProcessStartInfo("explorer.exe",NeoPaths.LogDir){UseShellExecute=true});return null;}
                var folderPicker=new FolderPicker();folderPicker.FileTypeFilter.Add("*");WinRT.Interop.InitializeWithWindow.Initialize(folderPicker,_window.Handle);
                var folder=await folderPicker.PickSingleFolderAsync();if(folder!=null){AppState.State.LogDirectory=folder.Path;AppState.ApplyLogSettingsToRuntime();AppState.Save();}return null;
            case "开源许可":
                var license=Path.Combine(NeoPaths.RootDir,"NOTICE");if(!File.Exists(license))license=Path.Combine(NeoPaths.RootDir,"说明.txt");
                return new{title,message=File.Exists(license)?await File.ReadAllTextAsync(license,_lifetime.Token):"未找到随软件分发的 NOTICE 文件，请检查安装目录。"};
            case "反馈问题":Process.Start(new ProcessStartInfo($"https://github.com/{AppUpdateService.RepoOwner}/{AppUpdateService.RepoName}/issues"){UseShellExecute=true});return null;
            case "清理内核残留":
                if(ConnectionService.IsConnected||ConnectionService.IsConnecting)throw new InvalidOperationException("请先断开连接再清理残留");
                await Task.Run(()=>{ConnectionService.CleanupOrphanCores();ConnectionService.CleanupOrphanedTun();ConnectionService.CleanupOrphanedSystemProxy();});return new{title,message="残留清理已执行，请查看运行日志中的结果。"};
            case "恢复安全启动":var safe=AppState.State;safe.UseTun=safe.SafeModePrevTun;safe.FixedExitEnabled=safe.SafeModePrevFixedExit;safe.SafeModeAt="";StartupGuard.Clear();AppState.Save();AppState.NotifyUiSettingsChanged();if(ConnectionService.IsConnected&&!await ConnectionService.ReloadForSettingsAsync())throw new InvalidOperationException("恢复设置后内核重载失败，请查看日志");return new{title,message="已恢复安全模式前的 TUN 与固定出口设置。"};
            case "导出配置":
                var backup=await SavePicker(ConfigBackup.DefaultFileName(),".zip").PickSaveFileAsync();if(backup==null)return null;
                var exported=await Task.Run(()=>ConfigBackup.Export(backup.Path));if(!exported.Ok)throw new InvalidOperationException(exported.Message);return new{title,message=exported.Message};
            case "导入配置":
                var picker=new FileOpenPicker();picker.FileTypeFilter.Add(".zip");WinRT.Interop.InitializeWithWindow.Initialize(picker,_window.Handle);
                var file=await picker.PickSingleFileAsync();if(file==null)return null;var validation=ConfigBackup.Validate(file.Path);if(!validation.Ok)throw new InvalidOperationException(validation.Message);
                var imported=ConfigBackup.Import(file.Path);if(!imported.Ok)throw new InvalidOperationException(imported.Message);App.Restart(false);return null;
            case "导出诊断":
                var zip=await SavePicker("neo-diagnostics-"+DateTime.Now.ToString("yyyyMMdd-HHmm")+".zip",".zip").PickSaveFileAsync();if(zip==null)return null;
                await Task.Run(()=>LogExportService.BuildZip(zip.Path,3));return new{title,message="诊断包已导出。"};
            case "恢复默认":await ResetDefaultsAsync();return new{title,message="已恢复默认设置，订阅与手选节点保留。"};
            default:throw new InvalidOperationException("不支持的设置操作");
        }
    }
    private async Task<string?> RefreshKernelVersionAsync(CoreEngine engine)
    {
        var version=await Task.Run(()=>KernelCatalog.ReadVersion(engine,refresh:true));
        _kernels=KernelCatalog.All.Select(k=>(object)new{name=k.Display,
            installed=KernelCatalog.IsInstalled(k.Engine),version=KernelCatalog.CachedVersion(k.Engine)}).ToArray();
        _settingsDirty=true;Publish();return version;
    }
    private object CheckedUpdate(string title,bool ok,string message,object? release)
    {if(!ok)throw new InvalidOperationException(message);var token=release!=null?Guid.NewGuid().ToString("N"):null;if(token!=null)_checkedUpdates[token]=release!;return new{title,message,installToken=token};}
    private async Task<object?> InstallCheckedUpdateAsync(string token)
    {
        if(!_checkedUpdates.Remove(token,out var release))throw new InvalidOperationException("更新检查已失效，请重新检查");
        void Progress(string text)=>Enqueue(()=>Post(new{type="update.progress",token,text}));
        void Bytes(DownloadProgress value)=>Progress("下载 · "+value.Text());
        (bool Ok,string Message) result=release switch
        {
            XrayRelease r=>await UpdateChecker.DownloadAndInstallAsync(r,Progress,_lifetime.Token,Bytes),
            SingBoxRelease r=>await UpdateChecker.DownloadAndInstallSingBoxAsync(r,Progress,_lifetime.Token,Bytes),
            MihomoRelease r=>await UpdateChecker.DownloadAndInstallMihomoAsync(r,Progress,_lifetime.Token,Bytes),
            UpdateChecker.NaiveRelease r=>await UpdateChecker.DownloadAndInstallNaiveAsync(r,Progress,_lifetime.Token,Bytes),
            AppUpdateService.UpdateInfo r=>await InstallAppAsync(r),
            _=>throw new InvalidOperationException("无效更新类型")
        };
        if(release is not AppUpdateService.UpdateInfo)
        {
            _kernels=await Task.Run(()=>KernelCatalog.All.Select(k=>(object)new{name=k.Display,
                installed=KernelCatalog.IsInstalled(k.Engine),version=KernelCatalog.ReadVersion(k.Engine)}).ToArray());
            _settingsDirty=true;
        }
        if(!result.Ok)throw new InvalidOperationException(result.Message);return new{title="更新",message=result.Message};
    }
    private async Task<(bool Ok,string Message)> InstallAppAsync(AppUpdateService.UpdateInfo update)
    {
        var path=await AppUpdateService.DownloadAsync(update.AssetUrl,update.AssetName,p=>Notify($"下载更新 {p:0}%"),update.AssetSize,update.Sha256,_lifetime.Token);
        if(path==null)return(false,"下载更新失败");var result=await AppUpdateService.LaunchInstallerAndExitAsync(path,update.LatestTag);return(result.Ok,result.Error??"安装程序已启动");
    }
    private async Task ResetDefaultsAsync()
    {
        if(ConnectionService.IsConnected||ConnectionService.IsConnecting)throw new InvalidOperationException("请先断开连接，再恢复默认设置");
        var s=AppState.State;s.ProxyMode="rule";s.BypassPrivate=s.BypassCn=s.BlockAds=true;s.AllowLan=false;
        s.LogLevel="warning";s.DnsServer="223.5.5.5";s.Language="zh-CN";s.UseTun=s.UseRustConfig=false;
        s.MinimizeToTray=true;s.CompactLayout=s.AutoConnect=s.StartMinimized=false;s.AutoUpdate=true;s.FailoverEnabled=false;
        s.AutoRepairSystemProxy=s.AutoRestartCore=true;s.FixedExitEnabled=false;s.FixedExitExitKey="";s.FixedExitEntryCount=6;
        s.AutoSpeedTest=true;s.StartupSpeedTest=s.ShowRealIp=false;s.RealDelayTestEnabled=false;s.AutoCheckUpdate=s.GameDirectEnabled=true;
        AppState.Save();AppState.NotifyUiSettingsChanged();if(ConnectionService.IsConnected)await ConnectionService.ReloadForSettingsAsync();
    }
}
