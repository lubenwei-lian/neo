using Microsoft.Web.WebView2.Core;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace Neo.App.Ui;

/// <summary>One transparent native surface for the actual HTML, without a XAML canvas underneath.</summary>
internal sealed partial class NativeHtmlWindow : IDisposable
{
    private readonly WndProc _proc;
    private readonly string _className = "NeoHtml_" + Environment.ProcessId;
    private readonly Compositor _compositor;
    private readonly DesktopWindowTarget _target;
    private readonly ContainerVisual _root, _webVisual;
    private CoreWebView2CompositionController? _composition;
    private CoreWebView2Controller? _controller;
    private CoreWebView2Environment? _environment;
    private CoreWebView2? _core;
    private bool _disposed, _tracking;
    internal nint Handle { get; private set; }
    // The WinRT controller creates separate CoreWebView2 wrappers on repeated
    // property reads. A subscribed wrapper must remain alive for its callbacks;
    // otherwise GC can free its native event source while Chromium still calls
    // it. Share one strongly held wrapper for the whole controller lifetime.
    internal CoreWebView2? Core => _core;
    internal event Action? CloseRequested;
    internal event Action? BoundsChanged;

    [ComImport, Guid("29e691fa-4567-4dca-b319-d0f207eb6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopInterop
    {
        void CreateDesktopWindowTarget(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool topmost, out nint target);
        void EnsureOnThread(uint thread);
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint WndProc(nint h, uint m, nuint w, nint l);
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] private struct WindowClass
    {
        internal uint Size, Style; internal WndProc Proc; internal int ClassExtra, WindowExtra;
        internal nint Instance, Icon, Cursor, Brush; internal string? Menu; internal string Class; internal nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct NativeRect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X,Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Track { internal uint Size,Flags;internal nint Window;internal uint Hover; }
    [StructLayout(LayoutKind.Sequential)] private struct Margins { internal int Left,Right,Top,Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { internal NativePoint Reserved,MaxSize,MaxPosition,MinTrackSize,MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { internal uint Size;internal NativeRect Monitor,Work;internal uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint h,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern bool GetMonitorInfoW(nint monitor,ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint h);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern nint LoadImageW(nint instance,string file,uint type,int cx,int cy,uint flags);
    [DllImport("user32.dll")] private static extern nint SendMessageW(nint h,uint m,nuint w,nint l);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    private nint _icon;
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WindowClass c);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern nint CreateWindowExW(uint ex,string cls,string title,uint style,int x,int y,int w,int h,nint parent,nint menu,nint instance,nint param);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint h,uint m,nuint w,nint l);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint h,out NativeRect r);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint h,out NativeRect r);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint h,nint after,int x,int y,int w,int height,uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint h,int cmd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint h);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint h);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint h);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint h);
    [DllImport("user32.dll")] private static extern nint SetCursor(nint cursor);
    [DllImport("user32.dll")] private static extern nint LoadCursorW(nint instance,nint cursor);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint h,ref NativePoint p);
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref Track t);
    [DllImport("user32.dll")] private static extern nint SetCapture(nint h);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint h,uint m,nuint w,nint l);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint h);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index,uint dpi);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(nint h,ref Margins margins);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint h,int attr,ref int value,int size);

    internal NativeHtmlWindow(bool review)
    {
        TransparentWindowBackdrop.EnsureDispatcherForComposition();
        _proc=WindowProc;
        var instance=GetModuleHandleW(null);
        var c=new WindowClass { Size=(uint)Marshal.SizeOf<WindowClass>(),Style=8,Proc=_proc,Instance=instance,Class=_className };
        if(RegisterClassExW(ref c)==0)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        Handle=CreateWindowExW(0x00200000u|(review?0x08000080u:0u),_className,"neo",0x000f0000u,
            20,20,1440,950,0,0,instance,0);
        if(Handle==0)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        _icon=LoadImageW(0,Path.Combine(AppContext.BaseDirectory,"Assets","neo.ico"),1,0,0,0x10|0x40);
        if(_icon!=0){SendMessageW(Handle,0x80,0,_icon);SendMessageW(Handle,0x80,1,_icon);}
        // Extending the native glass frame across the client paints an opaque
        // fallback behind transparent HTML. The composition target already owns
        // alpha; keep the native glass margins empty.
        var margins=new Margins();
        Marshal.ThrowExceptionForHR(DwmExtendFrameIntoClientArea(Handle,ref margins));
        var noMaterial=1;
        DwmSetWindowAttribute(Handle,38,ref noMaterial,sizeof(int));
        // The custom client area removes the standard non-client frame, so DWM
        // cannot reliably infer rounded corners. Round the native HWND itself;
        // rounding HTML alone leaves an opaque square behind the page corners.
        ApplyWindowCorners();
        _compositor=new Compositor();
        _compositor.As<IDesktopInterop>().CreateDesktopWindowTarget(Handle,false,out var pointer);
        try{_target=MarshalInterface<DesktopWindowTarget>.FromAbi(pointer);}finally{Marshal.Release(pointer);}
        _root=_compositor.CreateContainerVisual();_webVisual=_compositor.CreateContainerVisual();
        _root.Children.InsertAtTop(_webVisual);_target.Root=_root;
    }

    internal async Task InitializeAsync(string profile,Uri source,bool debug=false,Func<CoreWebView2,Task>? configure=null)
    {
        Trace("initialize begin");
        var arguments=debug?"--remote-debugging-port=9223":"";
#if NEO_UI_REVIEW
        if(Environment.GetCommandLineArgs().Contains("--verify-installed"))arguments="--remote-debugging-port=9224";
#endif
        var options=new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments=arguments };
        _environment??=await CoreWebView2Environment.CreateWithOptionsAsync(null,profile,options);
        Trace("environment ready");
        _composition=await _environment.CreateCoreWebView2CompositionControllerAsync(
            CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)Handle));
        Trace("controller ready");
        _controller=_composition.As<CoreWebView2Controller>();
        _core=_composition.CoreWebView2;
        _controller.DefaultBackgroundColor=Microsoft.UI.Colors.Transparent;
        _controller.RasterizationScale=GetDpiForWindow(Handle)/96d;
        _controller.ShouldDetectMonitorScaleChanges=true;
        _composition.RootVisualTarget=_webVisual;
        _composition.CursorChanged+=(_,_)=>
        {
            var cursor=_composition.Cursor.Type.ToString() switch
            {
                "Hand"=>32649,"IBeam"=>32513,"Cross"=>32515,"Wait"=>32514,"Help"=>32651,
                "SizeAll"=>32646,"SizeNortheastSouthwest"=>32643,"SizeNorthSouth"=>32645,
                "SizeNorthwestSoutheast"=>32642,"SizeWestEast"=>32644,"UniversalNo"=>32648,_=>32512
            };
            SetCursor(LoadCursorW(0,new nint(cursor)));
        };
        ResizeController();
        SetContentVisible(true);
        if(configure!=null)await configure(Core!);
        Core!.Navigate(source.AbsoluteUri);
    }
    private static void Trace(string text)
    {if(NativeUiReview.Enabled)File.AppendAllText(Path.Combine(NativeUiReview.DirectoryPath,"html-lifecycle.trace"),DateTime.Now.ToString("HH:mm:ss.fff")+" "+text+Environment.NewLine);}

    private void ApplyWindowCorners()
    {
        if(Handle==0||!OperatingSystem.IsWindowsVersionAtLeast(10,0,22000))return;
        // The HTML shell is always dark; its native rim must not inherit the
        // light Windows theme when the system-rounded frame becomes visible.
        var darkFrame=1;
        DwmSetWindowAttribute(Handle,20,ref darkFrame,sizeof(int));
        const int windowCornerPreference=33,round=2;
        var preference=round;
        var result=DwmSetWindowAttribute(Handle,windowCornerPreference,ref preference,sizeof(int));
        if(result<0)Services.LogBus.App($"设置原生窗口圆角失败：0x{result:X8}","Warning");
    }

    private void ResizeController()
    {
        if(_controller==null||Handle==0)return;
        // Chromium owns native input windows as well as the composition visual.
        // Their screen position must follow the parent even while minimized.
        if(!IsIconic(Handle)&&GetClientRect(Handle,out var rect)&&rect.Right>0&&rect.Bottom>0)
        {
            _controller.Bounds=new Rect(0,0,rect.Right,rect.Bottom);
            _root.Size=_webVisual.Size=new System.Numerics.Vector2(rect.Right,rect.Bottom);
        }
        _controller.NotifyParentWindowPositionChanged();
    }
    internal void Resize(int width,int height)=>SetWindowPos(Handle,0,0,0,width,height,0x2|0x4|0x10);
    internal void Move(int x,int y)=>SetWindowPos(Handle,0,x,y,0,0,0x1|0x4|0x10);
    internal void Show(bool activate=true)
    { ShowWindow(Handle,IsIconic(Handle)?(activate?9:4):(activate?5:8));if(activate)SetForegroundWindow(Handle);else SetWindowPos(Handle,new nint(1),0,0,0,0,0x1|0x2|0x10);BoundsChanged?.Invoke(); }
    internal void Hide(){ShowWindow(Handle,0);BoundsChanged?.Invoke();}
    internal bool IsVisible=>Handle!=0&&IsWindowVisible(Handle);
    internal void SetContentVisible(bool visible)
    {
        if(_controller==null)return;
        // A composition controller kept natively visible under an iconic parent
        // can leave a transparent, desktop-sized Chromium input window behind.
        // Hiding its native surface does not suspend JavaScript or web messages;
        // the host's background clock can still update the canvas and history.
        var effective=visible&&IsVisible&&!IsMinimized;
        if(_controller.IsVisible!=effective)_controller.IsVisible=effective;
        _controller.NotifyParentWindowPositionChanged();
    }
    internal void ReleaseContent()
    {
        // Close can synchronously send native focus/size messages. Make those
        // callbacks observe no controller before releasing the native content.
        var composition=_composition;var controller=_controller;
        _controller=null;_composition=null;_core=null;
        if(composition!=null)composition.RootVisualTarget=null;
        controller?.Close();
    }
    internal void Minimize()=>ShowWindow(Handle,6);
    internal void ToggleMaximize()=>ShowWindow(Handle,IsZoomed(Handle)?9:3);
    internal bool IsMinimized=>IsIconic(Handle);
    internal bool IsMaximized=>IsZoomed(Handle);
    internal NativeRect Bounds { get {GetWindowRect(Handle,out var r);return r;} }
    internal void BeginDrag(){ReleaseCapture();PostMessageW(Handle,0xA1,2,0);}

    private nint WindowProc(nint h,uint message,nuint w,nint l)
    {
        try{return HandleMessage(h,message,w,l);}
        catch(Exception ex){Services.LogBus.App($"窗口消息处理失败：0x{message:X} · {ex.GetType().Name} · {ex.Message}","Warning");return DefWindowProcW(h,message,w,l);}
    }
    private nint HandleMessage(nint h,uint message,nuint w,nint l)
    {
        // Reapply when Windows recreates its composition/theme resources. DWM
        // handles the square maximized/snap frame and rounded restored frame.
        if(message is 0x31E or 0x31A)ApplyWindowCorners();
        if(message==0x83)
        {
            // A maximized thick-frame window retains invisible resize borders
            // outside the monitor. Remove them from the HTML client viewport,
            // otherwise every edge and pointer coordinate is displaced by 8px.
            if(IsZoomed(h)&&l!=0)
            {
                var rect=Marshal.PtrToStructure<NativeRect>(l);var dpi=GetDpiForWindow(h);
                var x=GetSystemMetricsForDpi(32,dpi)+GetSystemMetricsForDpi(92,dpi);
                var y=GetSystemMetricsForDpi(33,dpi)+GetSystemMetricsForDpi(92,dpi);
                rect.Left+=x;rect.Right-=x;rect.Top+=y;rect.Bottom-=y;Marshal.StructureToPtr(rect,l,false);
            }
            return 0;
        }
        if(message==0x14)return 1;
        if(message==0x24)
        {
            var mm=Marshal.PtrToStructure<MinMaxInfo>(l);var mi=new MonitorInfo{Size=(uint)Marshal.SizeOf<MonitorInfo>()};
            if(GetMonitorInfoW(MonitorFromWindow(h,2),ref mi))
            {mm.MaxPosition=new NativePoint{X=mi.Work.Left-mi.Monitor.Left,Y=mi.Work.Top-mi.Monitor.Top};mm.MaxSize=new NativePoint{X=mi.Work.Right-mi.Work.Left,Y=mi.Work.Bottom-mi.Work.Top};}
            var scale=GetDpiForWindow(h)/96d;mm.MinTrackSize=new NativePoint{X=(int)(900*scale),Y=(int)(600*scale)};
            Marshal.StructureToPtr(mm,l,false);return 0;
        }
        if(message==0x10){CloseRequested?.Invoke();return 0;}
        // SHOWWINDOW / SIZE can arrive before IsWindowVisible / IsIconic reflects
        // the new state. Re-read on the next native message, including external
        // single-instance activation after the renderer has been released.
        if(message==0x5||message==0x3||message==0x18||message==0x47){ResizeController();PostMessageW(h,0x8001,0,0);}
        if(message==0x8001){SetContentVisible(IsVisible&&!IsMinimized);BoundsChanged?.Invoke();return 0;}
        if(message==0x7)_controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
        if(message==0x2E0){var r=Marshal.PtrToStructure<NativeRect>(l);SetWindowPos(h,0,r.Left,r.Top,r.Right-r.Left,r.Bottom-r.Top,0x4|0x10);}
        if(message==0x84 && !IsZoomed(h))
        {
            GetWindowRect(h,out var rect);var x=(short)((long)l&0xffff)-rect.Left;var y=(short)(((long)l>>16)&0xffff)-rect.Top;
            var edge=Math.Max(4,(int)(6*GetDpiForWindow(h)/96));
            var left=x<edge;var right=x>=rect.Right-rect.Left-edge;var top=y<edge;var bottom=y>=rect.Bottom-rect.Top-edge;
            if(top)return left?13:right?14:12;if(bottom)return left?16:right?17:15;if(left)return 10;if(right)return 11;
        }
        if((message>=0x200&&message<=0x20E)||message==0x2A3)
        {
            // Delayed mouse messages can arrive after minimize/hide/suspend.
            // Moving focus or sending input to that controller can throw E_INVALIDARG.
            if(_composition==null||_controller==null||IsIconic(h)||!IsWindowVisible(h)||_core?.IsSuspended==true)
            {_tracking=false;ReleaseCapture();return 0;}
        }
        if(_composition!=null && message>=0x200 && message<=0x20E)
        {
            var point=new NativePoint{X=(short)((long)l&0xffff),Y=(short)(((long)l>>16)&0xffff)};
            if(message is 0x20A or 0x20E)ScreenToClient(h,ref point);
            if(message==0x200&&!_tracking){var track=new Track{Size=(uint)Marshal.SizeOf<Track>(),Flags=2,Window=h};TrackMouseEvent(ref track);_tracking=true;}
            if(message is 0x201 or 0x204 or 0x207){SetFocus(h);_controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);SetCapture(h);}
            if(message is 0x202 or 0x205 or 0x208)ReleaseCapture();
            var data=message is 0x20A or 0x20E?unchecked((uint)(short)(((ulong)w>>16)&0xffff)):
                message is 0x20B or 0x20C or 0x20D?(uint)(((ulong)w>>16)&0xffff):0;
            _composition.SendMouseInput((CoreWebView2MouseEventKind)message,(CoreWebView2MouseEventVirtualKeys)((uint)w&0x7f),data,new Point(point.X,point.Y));
            return 0;
        }
        if(message==0x2A3&&_composition!=null){_tracking=false;_composition.SendMouseInput(CoreWebView2MouseEventKind.Leave,0,0,new Point());return 0;}
        return DefWindowProcW(h,message,w,l);
    }
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;
        ReleaseContent();_environment=null;
        _target.Root=null;_webVisual.Dispose();_root.Dispose();_target.Dispose();_compositor.Dispose();
        if(Handle!=0){DestroyWindow(Handle);Handle=0;}GC.KeepAlive(_proc);
        if(_icon!=0){DestroyIcon(_icon);_icon=0;}
    }
}
