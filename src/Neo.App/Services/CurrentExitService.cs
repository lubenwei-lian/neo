using Neo.Core.Core;

namespace Neo.App.Services;

public sealed record CurrentExitSnapshot(int Epoch, bool IsRunning, CurrentExitResult Result, DateTimeOffset? CheckedAt);

/// <summary>One bounded lookup after each committed connection; no periodic polling or all-node scan.</summary>
public static class CurrentExitService
{
    private sealed class Request
    {
        private readonly CancellationTokenSource _source = new();
        internal readonly CancellationToken Token;
        private readonly object _gate = new();
        private bool _cancelling, _finished, _disposed;
        internal Request() { Token = _source.Token; }
        internal void Cancel()
        {
            lock (_gate) { if (_disposed || _cancelling) return; _cancelling = true; }
            try { _source.Cancel(); }
            finally { lock (_gate) { _cancelling = false; DisposeIfFinished(); } }
        }
        internal void Finish() { lock (_gate) { _finished = true; DisposeIfFinished(); } }
        private void DisposeIfFinished() { if (_finished && !_cancelling && !_disposed) { _disposed = true; _source.Dispose(); } }
    }
    private static readonly object Gate = new();
    private static CurrentExitSnapshot Empty() => new(-1, false, CurrentExitResult.Unknown("连接后检测当前出口"), null);
    private static CurrentExitSnapshot _current = Empty();
    private static Request? _request;
    private static Task _running = Task.CompletedTask;
    private static bool _initialized;
    private static Func<NetworkDiagnosticSession, CancellationToken, Task<CurrentExitResult>> _probe = CurrentExitProbe.CheckAsync;
    public static CurrentExitSnapshot Current { get { lock (Gate) return _current; } }
    public static event Action? Changed;
    public static void Init() => Init(CurrentExitProbe.CheckAsync);
    internal static void Init(Func<NetworkDiagnosticSession, CancellationToken, Task<CurrentExitResult>> probe)
    {
        if (_initialized) return;
        _initialized = true; _probe = probe;
        ConnectionService.StateChanged += OnSessionChanged;
        OnSessionChanged();
    }
    private static void OnSessionChanged()
    {
        Request? old;
        lock (Gate)
        {
            if (ConnectionService.IsConnected && _current.Epoch == ConnectionService.SessionEpoch) return;
            old = _request; _request = null; _current = Empty();
        }
        old?.Cancel(); RaiseChanged();
        if (ConnectionService.IsConnected && !App.IsShuttingDown) _ = RunAsync();
    }
    public static Task RunAsync(bool refresh = false) => RunAsync(_probe, refresh);
    internal static Task RunAsync(Func<NetworkDiagnosticSession, CancellationToken, Task<CurrentExitResult>> probe, bool refresh = false)
    {
        var session = ConnectionService.CaptureDiagnosticSession();
        if (session == null || App.IsShuttingDown) return Task.CompletedTask;
        Task work;
        Request? old;
        lock (Gate)
        {
            if (!ConnectionService.IsConnected || session.Epoch != ConnectionService.SessionEpoch) return Task.CompletedTask;
            if (_current.Epoch == session.Epoch && (_current.IsRunning || !refresh && _current.CheckedAt.HasValue)) return _running;
            var request = new Request();
            old = _request; _request = request;
            _current = new(session.Epoch, true, CurrentExitResult.Unknown("正在检测当前出口"), null);
            work = _running = Task.Run(async () =>
            {
                CurrentExitResult result;
                try { result = await probe(session, request.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { result = CurrentExitResult.Unknown("出口检测已停止"); }
                catch (Exception ex) { result = CurrentExitResult.Unknown("出口检测失败：" + ex.GetType().Name); }
                lock (Gate)
                {
                    if (ReferenceEquals(_request, request) && ConnectionService.IsConnected && session.Epoch == ConnectionService.SessionEpoch && !App.IsShuttingDown)
                    { _current = new(session.Epoch, false, result, DateTimeOffset.UtcNow); _request = null; }
                }
                request.Finish(); RaiseChanged();
            });
        }
        old?.Cancel(); RaiseChanged(); return work;
    }
    public static void Cancel() { Request? request; lock (Gate) request = _request; request?.Cancel(); }
    private static void RaiseChanged()
    {
        if (App.IsShuttingDown) return;
        foreach (Action handler in Changed?.GetInvocationList() ?? [])
            try { handler(); } catch (Exception ex) { LogBus.App("出口显示订阅者异常：" + ex.GetType().Name, "Warning"); }
    }
}
