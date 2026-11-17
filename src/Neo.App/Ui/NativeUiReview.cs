using Microsoft.UI.Xaml;
using Neo.App.Services;
using Neo.Core.Models;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Neo.App.Ui;

/// <summary>An explicit offline review entry. Never enters the production App bootstrap.</summary>
internal static class NativeUiReview
{
    internal static bool Enabled
    {
        get
        {
#if NEO_UI_REVIEW
            return Environment.GetCommandLineArgs().Contains("--ui-review");
#else
            return false;
#endif
        }
    }
    internal static string DirectoryPath { get; private set; } = "";
    internal static async Task CaptureElementAsync(FrameworkElement element, string name)
    {
        if (!Enabled) throw new InvalidOperationException();
        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
        try { await bitmap.RenderAsync(element); }
        catch (ArgumentException) { return; } // Separate popup/adorner roots cannot be tree-rendered. Window capture below remains authoritative.
        // Platform popup hosts may be separate compositor roots and cannot be
        // captured by RenderTargetBitmap. Do not treat a zero bitmap as pixels.
        if (bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0) return;
        var pixels = (await bitmap.GetPixelsAsync()).ToArray();
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
        await encoder.FlushAsync(); stream.Seek(0);
        using var file = File.Create(Path.Combine(DirectoryPath, name + ".png"));
        await stream.AsStreamForRead().CopyToAsync(file);
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);

    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct ClientRect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapHeader
    {
        internal uint Size; internal int Width, Height; internal ushort Planes, Bits;
        internal uint Compression, ImageSize; internal int XPixels, YPixels; internal uint Colors, Important;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClientRect(nint window, out ClientRect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PrintWindow(nint window, nint dc, uint flags);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(nint dc, ref BitmapHeader info, uint usage, out nint pixels, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();

    internal static async Task CaptureWindowAsync(Window window, string name)
    {
        if (!Enabled) throw new InvalidOperationException("Window capture requires the isolated review entry.");
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (!GetClientRect(handle, out var rect) || rect.Right <= 0 || rect.Bottom <= 0)
            throw new InvalidOperationException("The review window has no client area.");
        var width = rect.Right; var height = rect.Bottom;
        var bytes = new byte[checked(width * height * 4)];
        var dc = CreateCompatibleDC(0);
        if (dc == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        nint bitmap = 0, previous = 0;
        try
        {
            var info = new BitmapHeader { Size = (uint)Marshal.SizeOf<BitmapHeader>(), Width = width,
                Height = -height, Planes = 1, Bits = 32 };
            bitmap = CreateDIBSection(dc, ref info, 0, out var pixels, 0, 0);
            if (bitmap == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            previous = SelectObject(dc, bitmap);
            // Capture the composed client without activating or moving the window.
            // Include native child Composition visuals and the actual animated
            // glyphs, rather than substituting a RenderTargetBitmap tree render.
            if (!PrintWindow(handle, dc, 3)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            GdiFlush(); Marshal.Copy(pixels, bytes, 0, bytes.Length);
        }
        finally
        {
            if (previous != 0) SelectObject(dc, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            DeleteDC(dc);
        }
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, bytes);
        await encoder.FlushAsync(); stream.Seek(0);
        using var file = File.Create(Path.Combine(DirectoryPath, name + ".png"));
        await stream.AsStreamForRead().CopyToAsync(file);
    }
    internal static void KeepBehind(Window window)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        SetWindowPos(handle, new nint(1), 20, 20, 0, 0, 0x11);
    }
    internal static void MakeNonActivating(Window window)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        SetWindowLongPtr(handle, -20, new nint(GetWindowLongPtr(handle, -20).ToInt64() | 0x08000000L | 0x80L));
        window.AppWindow.IsShownInSwitchers = false;
    }
    internal static void Initialize()
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), "neo-ui-review", Environment.ProcessId.ToString());
        Directory.CreateDirectory(DirectoryPath);
        // Set before the first NeoPaths access. No production config is copied or read.
        Environment.SetEnvironmentVariable("NEO_HOME", DirectoryPath);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteError(e.ExceptionObject.ToString() ?? "Unknown startup error");
    }

    internal static void SeedFixtures()
    {
        if (!Enabled) throw new InvalidOperationException("Offline fixtures require the review entry point.");
        var regions = new[] { "香港", "日本 东京", "新加坡", "美国 洛杉矶", "德国 法兰克福" };
        for (var i = 0; i < 36; i++)
            AppState.Nodes.Add(new NodeItem { Name = regions[i % regions.Length] + $" {i + 1:00} · 离线 UI 样例",
                Protocol = NodeProtocol.VLESS, Server = "fixture-" + i + ".invalid", Port = 443,
                TcpDelay = i % 9 == 8 ? -1 : 32 + i * 7, RealDelay = 45 + i * 7 });
        AppState.CurrentNode = AppState.Nodes[0];
        AppState.State.CompactLayout = false;
        AppState.State.FailoverEnabled = false;
        for (var i = 0; i < 3; i++)
            AppState.State.Subscriptions.Add(new AppState.SubItem { Id = "fixture-" + i,
                Name = i == 0 ? "机场 · 主力订阅（离线样例）" : "备用订阅 " + i,
                Url = "https://fixture.invalid/subscription/" + i, Enabled = i == 0,
                NodeCount = i == 0 ? 30 : 3, SubDownload = 154L * 1024 * 1024 * 1024,
                SubTotal = 465L * 1024 * 1024 * 1024, LastFetchUnix = DateTimeOffset.Now.ToUnixTimeSeconds() });
        LogBus.App("内核输出 · 此记录仅用于离线检查显示结构");
        LogBus.App("网络检测 · 保留当前手选节点", "Warning");
        // Isolated review-process memory only. The formal build cannot enable this entry.
        // Use the reference's same 30-day series to inspect the complete color spectrum.
        var sourceGb = new double[] { 3.2, 1.8, 4.6, 6.1, 2.4, .9, 5.5, 7.2, 3.8, 2.1, 1.2, 4.9, 8.4, 6.6, 3.1,
            2.7, 5.2, 9.1, 7.7, 4.2, 1.6, 2.2, 6.4, 11.3, 8.8, 4.6, 2.9, 5.1, 7.9, 12.6 };
        const double gb = 1024d * 1024 * 1024;
        var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
        var tracker = typeof(UsageTracker);
        tracker.GetField("_date", flags)!.SetValue(null, DateTime.Today.ToString("yyyy-MM-dd"));
        tracker.GetField("_today", flags)!.SetValue(null, (long)Math.Round(sourceGb[^1] * gb));
        tracker.GetField("_total", flags)!.SetValue(null, 412L * 1024 * 1024 * 1024);
        var daily = (Dictionary<string, long>)tracker.GetField("_daily", flags)!.GetValue(null)!;
        daily.Clear();
        for (var i = 0; i < sourceGb.Length - 1; i++)
            daily[DateTime.Today.AddDays(i - 29).ToString("yyyy-MM-dd")] = (long)Math.Round(sourceGb[i] * gb);
    }

    internal static void RegisterExceptionLogging(Application application)
    {
        application.UnhandledException += (_, e) => WriteError(e.Exception.ToString());
    }

    internal static void WriteError(string error)
    {
        try { File.AppendAllText(Path.Combine(DirectoryPath, "errors.log"), error + Environment.NewLine); } catch { }
    }
}
