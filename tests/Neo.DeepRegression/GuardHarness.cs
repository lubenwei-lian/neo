using System.Diagnostics;
namespace Neo.App.Services;

public static partial class GuardUnderTest
{
    private static int _neoPid = 10;
    private static int _corePid;
    private static Process? _core;
    private sealed class Config { public int Socks = 41808; public int Http = 41809; }
    private static readonly Config _cfg = new();
    public static bool SuccessorAlive;
    public static int TunCleanups;
    public static bool Defer() => ShouldDeferCleanup();
    public static void Cleanup() => DoCleanup(0, "controlled cleanup");
    public static int Sync(CoreState.Snapshot snapshot) { _corePid = 0; SyncCore(snapshot); return _corePid; }
    private static bool IsLiveNeoInstance(int pid) => SuccessorAlive;
    private static bool IsListening(int port) => false;
    private static bool ProbeProxy() => false;
    private static void Log(string text) { }
    private static void HandleTunCleanup() => TunCleanups++;
    private static int CountLeftovers() => 0;
    private static bool IsCoreWatchedState(string? state) => state is "running" or "starting" or "restarting" or "switching" or "failover";
}
