using System.Runtime.InteropServices;
using Neo.App.Services;
using Neo.App.Ui;

namespace Neo.App;

public sealed partial class MainWindow
{
    private async Task<object> ReviewFloatBallOptimizedAsync()
    {
        if(!NativeUiReview.Enabled)throw new InvalidOperationException();
        var checks=new List<string>();
        void Check(bool ok,string label){if(!ok)throw new InvalidOperationException("Floating optimization: "+label);checks.Add(label);}
        var selected=AppState.CurrentNode;
        var foreground=FloatReviewNative.GetForegroundWindow();
        FloatingTrafficService.SetEnabled(true);
        var window=FloatingTrafficService.WindowForReview ?? throw new InvalidOperationException("Floating window unavailable");
        var data=new FloatingTrafficData(true,false,"香港 01 · IPLC",2.34*1024*1024,13002342,180564787,154L*1024*1024*1024,465L*1024*1024*1024,224*1024);
        window.SetReviewData(data);window.MoveForReview(800,400);window.OpenForReview(true);await Task.Delay(650);
        var settled=window.CountersForReview;
        for(var i=0;i<100;i++)window.RefreshNow();
        await Task.Delay(2200);
        Check(window.CountersForReview==settled,"100 unchanged refreshes plus two timer ticks produce no presents, bitmap rebuilds or layout passes");
        for(var i=0;i<100;i++)window.SetReviewData(data with {DownloadBps=data.DownloadBps+i*.001,UploadBps=data.UploadBps+i*.001,Up=data.Up+i});
        Check(window.CountersForReview==settled,"subpixel ring and identically formatted traffic changes reuse both surfaces");
        window.SetReviewData(data with {UploadBps=640*1024});
        var upload=window.CountersForReview;
        Check(upload.Ball==settled.Ball && upload.Faces==settled.Faces && upload.Panel==settled.Panel+1 && upload.Contents==settled.Contents+1,"an upload-only change redraws the panel once and leaves the ball untouched");
        var uploadMetrics=window.Inspect();
        window.SetReviewData(data);await Task.Delay(50);
        var captures=new List<object>();
        foreach(var scale in new[]{1d,1.25,1.5,1.75,2,2.5,3})
        {
            window.ScaleForReview(scale);await Task.Delay(30);
            var name="float-optimized-"+scale.ToString(System.Globalization.CultureInfo.InvariantCulture);
            window.CaptureForReview(NativeUiReview.DirectoryPath,name);captures.Add(new {name,state=window.Inspect()});
            Check(!window.HitForReview(65,65).Panel && window.HitForReview(82,65).Panel,"panel follows its rounded contour at scale "+scale);
            Check(!window.HitForReview(80,249).Button && window.HitForReview(182,264).Button,"connection pill excludes its transparent corners at scale "+scale);
            window.SetReviewData(data with {SubscriptionTotal=0});
            Check(window.HitForReview(182,242).Button && !window.HitForReview(182,264).Button,"button hit area follows the compact no-quota layout at scale "+scale);
            window.CaptureForReview(NativeUiReview.DirectoryPath,name+"-no-quota");
            window.SetReviewData(data);
        }
        window.ScaleForReview(1);window.SetReviewData(data with {Connected=false,DownloadBps=null,UploadBps=null,SubscriptionTotal=0});
        await Task.Delay(550);window.CaptureForReview(NativeUiReview.DirectoryPath,"float-optimized-disconnected");
        var disconnected=window.Inspect();
        window.OpenForReview(false);await Task.Delay(260);
        var hidden=window.CountersForReview;
        window.SetReviewData(data with {Connected=false,Up=50*1024*1024,SubscriptionTotal=0});
        Check(window.CountersForReview.Panel==hidden.Panel,"hidden panel data changes never render a hidden surface");
        window.CancelDragForReview();await Task.Delay(30);
        Check(!window.PanelOpenForReview,"capture cancellation does not become a click");
        window.SetReviewData(data);await Task.Delay(550);
        for(var i=0;i<8;i++){window.OpenForReview(true);await Task.Delay(25);window.OpenForReview(false);await Task.Delay(25);}
        await Task.Delay(300);
        Check(!window.PanelOpenForReview && !window.FrameTimerForReview,"eight rapid direction reversals settle without a running frame timer");
        window.OpenForReview(true);await Task.Delay(300);
        Check(window.HitForReview(182,264).Button,"reopening restores the current button hit area");
        window.ResetFrameMetricsForReview();
        for(var i=0;i<8;i++){window.OpenForReview(false);await Task.Delay(300);window.OpenForReview(true);await Task.Delay(300);}
        Check(!window.FrameTimerForReview,"sixteen complete panel transitions stop their frame timer");
        var final=window.Inspect();var handles=window.HandlesForReview;
        FloatingTrafficService.SetEnabled(false);await Task.Delay(30);
        Check(!window.AnyTimerForReview && !FloatReviewNative.IsWindow(handles.Ball) && !FloatReviewNative.IsWindow(handles.Panel),"disable releases both native windows and every timer");
        Check(ReferenceEquals(selected,AppState.CurrentNode),"optimization tests preserve the selected node");
        Check(FloatReviewNative.GetForegroundWindow()==foreground,"optimization tests preserve foreground focus");
        return new {checks,count=checks.Count,settled,uploadMetrics,disconnected,captures,final};
    }

    private async Task<object> ReviewFloatBallClicksAsync()
    {
        if(!NativeUiReview.Enabled)throw new InvalidOperationException();
        var checks=new List<string>();
        void Check(bool ok,string text){if(!ok)throw new InvalidOperationException("Floating click regression: "+text);checks.Add(text);}
        var selected=AppState.CurrentNode;
        var foreground=FloatReviewNative.GetForegroundWindow();
        var draws=0;
        using(var bitmap=new System.Drawing.Bitmap(320,320,System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
        using(var graphics=System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode=System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            var reproduced=false;
            using(var pen=new System.Drawing.Pen(System.Drawing.Color.CornflowerBlue,3)
                {StartCap=System.Drawing.Drawing2D.LineCap.Round,EndCap=System.Drawing.Drawing2D.LineCap.Round})
                try{graphics.DrawArc(pen,51,51,58,58,-90,.0012f);}
                catch(System.Runtime.InteropServices.ExternalException ex) when(ex.HResult==unchecked((int)0x8000ffff)){reproduced=true;}
            Check(reproduced,"original short round-capped arc reproduces the dump's E_UNEXPECTED");
            foreach(var scale in new[]{1f,1.25f,1.5f,1.75f,2f})
            {
                graphics.ResetTransform();graphics.ScaleTransform(scale,scale);
                for(var i=0;i<4000;i++)
                {
                    FloatingTrafficWindow.DrawProgressArc(graphics,System.Drawing.Color.CornflowerBlue,Math.Pow(10,-20+i/200d)/360);
                    draws++;
                }
                foreach(var sweep in new[]{0d,.0012,.0158,.99,1,1.01,90,180,359.99,360,double.NaN,double.PositiveInfinity})
                {FloatingTrafficWindow.DrawProgressArc(graphics,System.Drawing.Color.CornflowerBlue,sweep/360);draws++;}
            }
        }
        Check(draws>20000,"short-arc sweep and boundary values draw safely across five DPI scales");
        NavigateTo("nodes");Root.UpdateLayout();
        FloatingTrafficService.SetEnabled(true);
        var window=FloatingTrafficService.WindowForReview ?? throw new InvalidOperationException("Floating window unavailable");
        var data=new FloatingTrafficData(true,false,"香港 01 · IPLC",2.34*1024*1024,13002342,180564787,154L*1024*1024*1024,465L*1024*1024*1024);
        window.SetReviewData(data);window.MoveForReview(800,400);
        var presenter=AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter ?? throw new InvalidOperationException("Presenter missing");
        presenter.Minimize();ApplyPresentationVisibility(false);
        await Task.Delay(3250);
        Check(_pageNodes?.ReviewItemsSource==null,"minimizing for three seconds releases the actual node card containers");
        for(var i=0;i<12;i++)
        {
            if(i>0){if(i%2==0){presenter.Minimize();ApplyPresentationVisibility(false);}else HideToTray();}
            var before=_floatMainRequests;
            window.HoverForReview(i%2==0);
            window.SetReviewData(data with {DownloadBps=i%2==0?0:2.34*1024*1024});
            window.ClickForReview();
            Check(window.PanelOpenForReview,"single release opens panel, cycle "+i);
            window.ClickForReview();await Task.Delay(260);
            Check(_floatMainRequests==before+1 && !window.PanelOpenForReview && _connPageVisible
                && AppWindow.IsVisible && presenter.State!=Microsoft.UI.Windowing.OverlappedPresenterState.Minimized
                && _pageNodes?.ReviewItemsSource!=null,"double release restores the real main window and node cards, cycle "+i);
        }
        await Task.Delay(550);
        Check(!window.FrameTimerForReview,"repeated clicks and traffic decay stop their animation timers");
        Check(ReferenceEquals(selected,AppState.CurrentNode),"clicks and restoration preserve the manually selected node");
        var handles=window.HandlesForReview;
        window.FailNextFrameForReview();await Task.Delay(180);
        Check(FloatingTrafficService.WindowForReview==null && !AppState.State.FloatingTrafficEnabled
            && !window.AnyTimerForReview && !FloatReviewNative.IsWindow(handles.Ball) && !FloatReviewNative.IsWindow(handles.Panel),
            "a real timer callback failure closes only the floating surface and stops its timers");
        FloatingTrafficService.SetEnabled(true);await Task.Delay(50);FloatingTrafficService.SetEnabled(false);
        Check(!App.IsShuttingDown && ReferenceEquals(selected,AppState.CurrentNode),"main process remains usable and floating switch works after isolated failure");
        Check(FloatReviewNative.GetForegroundWindow()==foreground,"all click and restore checks preserve the user's foreground window");
        NavigateTo("nodes");Root.UpdateLayout();
        return new {checks,count=checks.Count,arcDraws=draws,restoreCycles=12};
    }

    private async Task<object> ReviewFloatBallMotionAsync(string name)
    {
        FloatingTrafficService.SetEnabled(true);
        var window=FloatingTrafficService.WindowForReview ?? throw new InvalidOperationException("Floating window unavailable");
        var data=new FloatingTrafficData(true,false,"香港 01 · IPLC",0,13002342,180564787,154L*1024*1024*1024,465L*1024*1024*1024);
        window.SetReviewData(data);window.MoveForReview(800,400);window.OpenForReview(false);await Task.Delay(550);
        var frames=new List<object>();
        async Task Record(string group,int[] times)
        {
            var clock=System.Diagnostics.Stopwatch.StartNew();
            foreach(var at in times)
            {
                var delay=at-clock.Elapsed.TotalMilliseconds;
                if(delay>0)await Task.Delay(TimeSpan.FromMilliseconds(delay));
                var frame=name+"-"+group+"-"+at.ToString("000");
                window.CaptureForReview(NativeUiReview.DirectoryPath,frame);
                frames.Add(new {group,requestedMs=at,elapsedMs=clock.Elapsed.TotalMilliseconds,state=window.Inspect(),file=frame});
            }
        }
        window.OpenForReview(true);await Record("open",[0,35,75,120,180,240]);
        window.OpenForReview(false);await Record("close",[0,35,75,120,180,240]);
        window.OpenForReview(true);await Task.Delay(65);window.OpenForReview(false);await Record("reverse",[0,35,75,120,180,240]);
        window.SetReviewData(data with {DownloadBps=2.34*1024*1024});await Record("ring",[0,90,180,270,360,480]);
        var settled=!window.PanelOpenForReview && !window.FrameTimerForReview;
        FloatingTrafficService.SetEnabled(false);
        return new {frames,settled,note="Finite render-buffer samples include layer opacity; capture work is not a presentation frame-rate measurement."};
    }

    private async Task<object> ReviewFloatBallAsync()
    {
        if(!NativeUiReview.Enabled)throw new InvalidOperationException();
        NavigateTo("conn");Root.UpdateLayout();
        var checks=new List<string>();
        void Check(bool ok,string text){if(!ok)throw new InvalidOperationException("Floating traffic regression: "+text);checks.Add(text);}
        var selected=AppState.CurrentNode;
        var foreground=FloatReviewNative.GetForegroundWindow();
        FloatingTrafficService.SetEnabled(false);await Task.Delay(50);
        EnsureConnPage().SetFloatSwitchForReview(true);await Task.Delay(80);
        var window=FloatingTrafficService.WindowForReview ?? throw new InvalidOperationException("Switch did not create floating windows");
        Check(AppState.State.FloatingTrafficEnabled,"connection card switch creates the real floating window");
        Check(ReferenceEquals(selected,AppState.CurrentNode),"floating switch preserves node selection");
        Check(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Count==0,"floating switch does not invoke the node picker");
        var handles=window.HandlesForReview;
        var ex=FloatReviewNative.GetWindowLongPtr(handles.Ball,-20).ToInt64();
        Check((ex&0x08080080)==0x08080080,"ball uses layered/no-activate/tool-window styles");
        Check(FloatReviewNative.GetWindow(handles.Ball,4)!=0,"floating ball has an independent hidden owner and cannot replace the main process window");
        var data=new FloatingTrafficData(true,false,"香港 01 · IPLC",2.34*1024*1024,(long)(12.4*1024*1024),(long)(172.2*1024*1024),154L*1024*1024*1024,465L*1024*1024*1024);
        window.SetReviewData(data);window.MoveForReview(800,400);await Task.Delay(600);
        Check(!window.FrameTimerForReview,"arc animation stops after reaching its target");
        window.ClickForReview();await Task.Delay(80);
        Check(window.PanelOpenForReview,"first release opens immediately without waiting for the double-click timeout");
        var prior=_floatMainRequests;window.ClickForReview();await Task.Delay(600);
        Check(!window.PanelOpenForReview && _floatMainRequests==prior+1,"double-click closes the panel and opens the main window once");
        window.ClickForReview();await Task.Delay((int)FloatReviewNative.GetDoubleClickTime()+260);
        Check(window.PanelOpenForReview,"single click opens the information panel");
        window.CaptureForReview(NativeUiReview.DirectoryPath,"float-regression-open");
        window.OpenForReview(false);await Task.Delay(45);window.OpenForReview(true);await Task.Delay(45);window.OpenForReview(false);await Task.Delay(300);
        Check(!window.PanelOpenForReview && !window.FrameTimerForReview,"rapid reversal settles closed and releases the frame timer");
        window.DragForReview(44,28);await Task.Delay(80);
        Check(AppState.State.FloatingTrafficX==844 && AppState.State.FloatingTrafficY==428,"drag stores the release position without changing the mouse position");
        window.SetReviewData(data with {SubscriptionTotal=0});window.OpenForReview(true);await Task.Delay(250);
        window.CaptureForReview(NativeUiReview.DirectoryPath,"float-regression-no-quota");
        window.MoveForReview(100000,-100000);await Task.Delay(30);
        Check(!window.FrameTimerForReview,"monitor clamping does not leave continuous rendering running");
        var metrics=window.Inspect();
        EnsureConnPage().SetFloatSwitchForReview(false);await Task.Delay(30);
        Check(!FloatReviewNative.IsWindow(handles.Ball) && !FloatReviewNative.IsWindow(handles.Panel) && !window.AnyTimerForReview,"turning off destroys both windows and stops every timer");
        for(var i=0;i<3;i++){FloatingTrafficService.SetEnabled(true);FloatingTrafficService.SetEnabled(false);}
        var before=FloatReviewNative.GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle,0);
        for(var i=0;i<20;i++){FloatingTrafficService.SetEnabled(true);FloatingTrafficService.SetEnabled(false);}
        var after=FloatReviewNative.GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle,0);
        Check(after<=before+2,"twenty repeated enable/disable cycles do not accumulate GDI handles");
        Check(FloatingTrafficService.WindowForReview==null,"disabled state retains no floating window");
        Check(FloatReviewNative.GetForegroundWindow()==foreground,"review preserves the user's foreground window");
        return new {checks,count=checks.Count,gdiBefore=before,gdiAfter=after,metrics};
    }

    private static class FloatReviewNative
    {
        [DllImport("user32.dll")]internal static extern nint GetForegroundWindow();
        [DllImport("user32.dll")]internal static extern bool IsWindow(nint h);
        [DllImport("user32.dll")]internal static extern nint GetWindow(nint h,uint command);
        [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]internal static extern nint GetWindowLongPtr(nint h,int index);
        [DllImport("user32.dll")]internal static extern uint GetDoubleClickTime();
        [DllImport("user32.dll")]internal static extern uint GetGuiResources(nint process,uint type);
    }
}
