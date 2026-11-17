namespace Neo.App.Services;

// Only the application/session boundary is replaced. Release discovery, HTTP,
// hashing, archive extraction, executable checks and file replacement are real.
public static class ConnectionService
{
    public static bool IsConnected { get; set; }
    public static int SessionEpoch => 0;
    public static void Disconnect(string reason) { }
    internal static Task<(bool Ok, string Message)> ReplaceKernelFilesAsync(int epoch,
        Func<(bool Ok, string Message)> replace, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(replace()); }
    public static void ResetXrayVersion() => KernelCatalog.ResetVersion(CoreEngine.Xray);
    public static void ResetSingBoxVersion() => KernelCatalog.ResetVersion(CoreEngine.SingBox);
}
public static class LogBus
{
    public static void App(string message, string level = "Info") => Console.WriteLine($"[{level}] {message}");
}
public static class IncidentLog
{
    public static void Record(string kind, string message, string? detail = null) { }
}
