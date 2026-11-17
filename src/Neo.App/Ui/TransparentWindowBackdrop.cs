using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;

namespace Neo.App.Ui;

/// <summary>A clear compositor surface, without acrylic, wallpaper or a colour-key.</summary>
internal sealed class TransparentWindowBackdrop(nint hwnd) : SystemBackdrop
{
    private Windows.UI.Composition.Compositor? _compositor;
    private Windows.UI.Composition.CompositionColorBrush? _clear;
    private static Windows.System.DispatcherQueueController? _windowsQueue;
    private SubclassProc? _subclass;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint window, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint window, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BlurBehind
    { internal uint Flags; internal int Enable; internal nint Region; internal int Transition; }
    [DllImport("dwmapi.dll")] private static extern int DwmEnableBlurBehindWindow(nint window, ref BlurBehind blur);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern nint GetStockObject(int index);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern int FillRect(nint dc, ref Rect rect, nint brush);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);

    [StructLayout(LayoutKind.Sequential)]
    private struct QueueOptions { internal int Size, ThreadType, ApartmentType; }
    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(QueueOptions options, out nint controller);

    private static void EnsureWindowsDispatcher()
    {
        // WinUI's Microsoft.UI queue and the Windows compositor's Windows.System
        // queue are separate. Create the latter on this same UI thread once.
        if (Windows.System.DispatcherQueue.GetForCurrentThread() != null) return;
        var options = new QueueOptions { Size = Marshal.SizeOf<QueueOptions>(), ThreadType = 2, ApartmentType = 2 };
        Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out var pointer));
        try { _windowsQueue = WinRT.MarshalInterface<Windows.System.DispatcherQueueController>.FromAbi(pointer); }
        finally { Marshal.Release(pointer); }
    }
    internal static void EnsureDispatcherForComposition() => EnsureWindowsDispatcher();

    // DWM must own the entire client frame for the zero-alpha compositor surface
    // to reveal the windows behind it. Transparency is not a screenshot of them.
    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { internal int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint window, ref Margins margins);

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        _subclass = OnWindowMessage;
        if (!SetWindowSubclass(hwnd, _subclass, 0x4E454F, 0))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        ConfigureDwm();
        EnsureWindowsDispatcher();
        _compositor = new Windows.UI.Composition.Compositor();
        _clear = _compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        target.SystemBackdrop = _clear;
        base.OnTargetConnected(target, root);
        var dc = GetDC(hwnd);
        try { ClearNativeBackground(dc); }
        finally { ReleaseDC(hwnd, dc); }
    }

    private void ConfigureDwm()
    {
        var margins = new Margins();
        Marshal.ThrowExceptionForHR(DwmExtendFrameIntoClientArea(hwnd, ref margins));
        // Enable per-pixel composition while restricting blur to an empty region.
        // No colour-key, screen image or wallpaper material participates here.
        var region = CreateRectRgn(-2, -2, -1, -1);
        try
        {
            var blur = new BlurBehind { Flags = 3, Enable = 1, Region = region };
            Marshal.ThrowExceptionForHR(DwmEnableBlurBehindWindow(hwnd, ref blur));
        }
        finally { if (region != 0) DeleteObject(region); }
    }

    private bool ClearNativeBackground(nint dc)
    {
        if (dc == 0 || !GetClientRect(hwnd, out var rect)) return false;
        return FillRect(dc, ref rect, GetStockObject(4)) != 0;
    }

    private nint OnWindowMessage(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == 0x14 && ClearNativeBackground((nint)wParam)) return 1;
        if (message == 0x31E) ConfigureDwm();
        return DefSubclassProc(window, message, wParam, lParam);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        target.SystemBackdrop = null;
        if (_subclass != null) { RemoveWindowSubclass(hwnd, _subclass, 0x4E454F); _subclass = null; }
        _clear?.Dispose(); _clear = null;
        _compositor?.Dispose(); _compositor = null;
        var margins = new Margins();
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
        var blur = new BlurBehind { Flags = 1 };
        DwmEnableBlurBehindWindow(hwnd, ref blur);
        base.OnTargetDisconnected(target);
    }
}
