using System.Runtime.CompilerServices;

namespace Neo.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // The guard survives the UI process. It must not initialize a second XAML app,
        // renderer or dispatcher just to wait on process/event handles.
        var exportIndex = Array.IndexOf(args, "--export-logs");
        if (exportIndex >= 0 && exportIndex + 1 < args.Length)
        {
            try { Services.LogExportService.BuildZip(args[exportIndex + 1], 3); } catch { }
            return 0;
        }

        var guardIndex = Array.IndexOf(args, "--guard");
        if (guardIndex >= 0 && guardIndex + 1 < args.Length
            && int.TryParse(args[guardIndex + 1], out var neoPid))
        {
            try { return Services.GuardMode.Run(neoPid); } catch { return 1; }
        }

        if (args.Contains("--cleanup-tun"))
        {
            try { return Services.GuardMode.RunCleanupTun(); } catch { return 2; }
        }

        // A review request must fail closed in formal builds, never fall through to
        // the production bootstrap if a test script selects the wrong executable.
        if (args.Contains("--ui-review") && !Ui.NativeUiReview.Enabled) return 64;
#if !NEO_UI_REVIEW
        if(args.Contains("--verify-installed"))return 64;
#endif
        if (Ui.NativeUiReview.Enabled) Ui.NativeUiReview.Initialize();
        try { RunUi(); }
        catch (Exception ex) when (Ui.NativeUiReview.Enabled)
        {
            Ui.NativeUiReview.WriteError(ex.ToString());
            return 3;
        }
        return 0;
    }

    // Keep WinUI references out of the headless entry method, including JIT inlining.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunUi()
    {
        // Match the WinUI-generated entry point for normal interactive launches.
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(p =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }
}
