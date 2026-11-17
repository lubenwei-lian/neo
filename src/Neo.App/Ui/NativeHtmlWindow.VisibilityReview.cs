#if NEO_UI_REVIEW
using Microsoft.UI.Dispatching;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Neo.App.Ui;

internal sealed partial class NativeHtmlWindow
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool ReviewEnumWindow(nint handle,nint parameter);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(ReviewEnumWindow callback,nint parameter);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint handle,out uint process);

    // Only the isolated, compile-time-gated review executable can run this.
    // It exercises the actual composition controller and native browser windows.
    internal async Task VerifyVisibilityAsync(DispatcherQueue queue)
    {
        var checks=new List<object>();var ticks=0;
        var clock=queue.CreateTimer();clock.Interval=TimeSpan.FromMilliseconds(1000d/24);
        clock.Tick+=(_,_)=>{Core!.PostWebMessageAsJson("{\"type\":\"traffic.frame\"}");ticks++;};
        void Check(bool ok,string name,object? detail=null)
        {
            checks.Add(new{ok,name,detail});
            if(!ok)throw new InvalidOperationException(name+": "+JsonSerializer.Serialize(detail));
        }
        List<object> VisibleBrowserWindows()
        {
            var windows=new List<object>();var pid=Core!.BrowserProcessId;
            ReviewEnumWindow callback=(h,_)=>
            {
                GetWindowThreadProcessId(h,out var owner);
                if(owner==pid&&IsWindowVisible(h))
                {
                    GetWindowRect(h,out var r);
                    windows.Add(new{handle=(long)h,bounds=new[]{r.Left,r.Top,r.Right,r.Bottom}});
                }
                return true;
            };
            EnumWindows(callback,0);GC.KeepAlive(callback);return windows;
        }
        async Task<JsonDocument> Inspect()
        {
            var raw=await Core!.ExecuteScriptAsync("JSON.stringify({hidden:document.hidden,traffic:window.neoTraffic.inspect()})");
            return JsonDocument.Parse(JsonSerializer.Deserialize<string>(raw)!);
        }
        async Task Seed()
        {
            await Core!.ExecuteScriptAsync("window.neoTraffic.push({connected:true,connectedAt:123456,background:false,stats:{session:9,id:3,sampledAt:Date.now(),upBps:1234,downBps:5678,history:[{id:1,sampledAt:Date.now()-2000,upBps:1000,downBps:3000},{id:2,sampledAt:Date.now()-1000,upBps:1500,downBps:5000}]}})");
        }
        try
        {
            Resize(1100,800);Show(false);await Task.Delay(300);await Seed();
            Check(_controller!.IsVisible,"visible parent exposes browser content");
            using(var visible=await Inspect())Check(visible.RootElement.GetProperty("traffic").GetProperty("targetFps").GetInt32()==120,"foreground keeps 120 FPS target");
            ToggleMaximize();Show(false);await Task.Delay(300);
            Minimize();await Task.Delay(300);
            Check(!_controller!.IsVisible,"minimize hides native browser input surface");
            var browser=VisibleBrowserWindows();Check(browser.Count==0,"no visible browser popup over desktop after maximize/minimize",browser);
            // Reproduce the former forced-visible call. The native gate must
            // reject it even if a future bridge caller requests visibility.
            SetContentVisible(true);
            Check(!_controller.IsVisible,"iconic parent cannot force native browser visible");
            using var before=await Inspect();
            var start=before.RootElement.GetProperty("traffic").GetProperty("performance").GetProperty("draws").GetInt32();
            clock.Start();await Task.Delay(2200);clock.Stop();
            using(var after=await Inspect())
            {
                var traffic=after.RootElement.GetProperty("traffic");
                var draws=traffic.GetProperty("performance").GetProperty("draws").GetInt32()-start;
                Check(after.RootElement.GetProperty("hidden").GetBoolean(),"browser remains natively hidden during canvas updates");
                Check(traffic.GetProperty("targetFps").GetInt32()==24,"minimized canvas keeps 24 FPS target");
                Check(ticks>=35&&draws>=35,"native 24 Hz messages continue actual canvas drawing while hidden",new{ticks,draws});
                Check(traffic.GetProperty("samples").GetArrayLength()==3,"minimizing preserves upload and download samples");
            }
            for(var i=0;i<4;i++)
            {
                Show(false);await Task.Delay(100);
                Check(_controller.IsVisible,$"restore cycle {i+1} exposes content");
                Minimize();await Task.Delay(100);
                browser=VisibleBrowserWindows();
                Check(!_controller.IsVisible&&browser.Count==0,$"minimize cycle {i+1} leaves no desktop input overlay",browser);
            }
            Show(false);await Task.Delay(150);Hide();await Task.Delay(150);
            SetContentVisible(true);browser=VisibleBrowserWindows();
            Check(!_controller.IsVisible&&browser.Count==0,"hidden parent cannot leave browser input visible",browser);
            Show(false);await Task.Delay(150);
            Check(_controller.IsVisible,"restore after hide enables native input again");
            using(var restored=await Inspect())Check(restored.RootElement.GetProperty("traffic").GetProperty("samples").GetArrayLength()==3,"restore retains the same traffic history");
            File.WriteAllText(Path.Combine(NativeUiReview.DirectoryPath,"visibility-checks.json"),JsonSerializer.Serialize(new{passed=true,checks},new JsonSerializerOptions{WriteIndented=true}));
        }
        catch(Exception ex)
        {
            File.WriteAllText(Path.Combine(NativeUiReview.DirectoryPath,"visibility-checks.json"),JsonSerializer.Serialize(new{passed=false,checks,error=ex.ToString()},new JsonSerializerOptions{WriteIndented=true}));
            throw;
        }
        finally{clock.Stop();}
    }
}
#endif
