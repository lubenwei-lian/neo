using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Neo.App.Ui;

/// <summary>Captures the actual DWM surface, including alpha, without moving a window.</summary>
internal static class NativeCaptureReview
{
    [ComImport, Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICaptureInterop
    {
        void CreateForWindow(nint hwnd, ref Guid iid, out nint result);
        void CreateForMonitor(nint monitor, ref Guid iid, out nint result);
    }
    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(nint adapter, int driver, nint software, uint flags,
        nint levels, uint count, uint sdk, out nint device, out int feature, out nint context);
    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgi, out nint device);

    internal static async Task CaptureAsync(nint hwnd, string path)
    {
        if (!NativeUiReview.Enabled) throw new InvalidOperationException();
        var factory = GraphicsCaptureItem.As<ICaptureInterop>();
        var iid = new Guid("79c3f95b-31f7-4ec2-a464-632ef5d30760");
        factory.CreateForWindow(hwnd, ref iid, out var itemPointer);
        GraphicsCaptureItem item;
        try { item = WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPointer); }
        finally { Marshal.Release(itemPointer); }

        Marshal.ThrowExceptionForHR(D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7, out var d3d, out _, out var context));
        IDirect3DDevice device;
        try
        {
            var dxgiIid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3d, ref dxgiIid, out var dxgi));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var pointer));
                try { device = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(pointer); }
                finally { Marshal.Release(pointer); }
            }
            finally { Marshal.Release(dxgi); }
        }
        finally { Marshal.Release(context); Marshal.Release(d3d); }

        using (device)
        using (var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size))
        using (var session = pool.CreateCaptureSession(item))
        {
            session.IsCursorCaptureEnabled = false;
            try { session.IsBorderRequired = false; } catch { }
            var ready = new TaskCompletionSource<Direct3D11CaptureFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            pool.FrameArrived += (_, _) =>
            {
                var frame = pool.TryGetNextFrame();
                if (frame != null && !ready.TrySetResult(frame)) frame.Dispose();
            };
            session.StartCapture();
            using var frame = await ready.Task.WaitAsync(TimeSpan.FromSeconds(8));
            using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface);
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync(); stream.Seek(0);
            using var output = File.Create(path);
            await stream.AsStreamForRead().CopyToAsync(output);
        }
    }
}
