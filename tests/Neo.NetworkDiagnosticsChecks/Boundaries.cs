using Neo.Core.Core;

namespace Neo.App { public static class App { public static bool IsShuttingDown; } }
namespace Neo.App.Services
{
    public static class LogBus { public static void App(string text, string level) { } }
    public static class ConnectionService
    {
        public static bool IsConnected = true;
        public static int SessionEpoch;
        public static NetworkDiagnosticSession? Session;
        public static event Action? StateChanged;
        public static NetworkDiagnosticSession? CaptureDiagnosticSession() => IsConnected ? Session : null;
        public static void Change(bool connected, NetworkDiagnosticSession? session)
        { IsConnected = connected; Session = session; SessionEpoch++; StateChanged?.Invoke(); }
    }
}
