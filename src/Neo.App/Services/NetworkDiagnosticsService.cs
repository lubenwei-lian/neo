using Neo.Core.Core;

namespace Neo.App.Services;

public sealed record NetworkDiagnosticsSnapshot(int Epoch, bool IsRunning, string Session,
    string Summary, DateTimeOffset? CompletedAt, IReadOnlyList<WebsiteDiagnosticResult> Results);

/// <summary>用户触发的逐站检测；不改规则、代理模式、节点或自动故障转移计数。</summary>
public static class NetworkDiagnosticsService
{
    private sealed class Request(NetworkDiagnosticSession session,
        Func<NetworkDiagnosticTarget, KernelRouteContext, bool, CancellationToken, Task<WebsiteDiagnosticResult>> probe)
    {
        public readonly NetworkDiagnosticSession Session = session;
        public readonly Func<NetworkDiagnosticTarget, KernelRouteContext, bool, CancellationToken, Task<WebsiteDiagnosticResult>> Probe = probe;
        private readonly CancellationTokenSource _source = new();
        public CancellationToken Token => _source.Token;
        private readonly object _gate = new();
        private bool _cancelling, _finished, _disposed;
        public void Cancel()
        {
            lock (_gate) { if (_disposed || _cancelling) return; _cancelling = true; }
            try { _source.Cancel(); }
            finally { lock (_gate) { _cancelling = false; DisposeIfFinished(); } }
        }
        public void Finish() { lock (_gate) { _finished = true; DisposeIfFinished(); } }
        private void DisposeIfFinished()
        { if (_finished && !_cancelling && !_disposed) { _disposed = true; _source.Dispose(); } }
    }
    private static readonly object Gate = new();
    private static Request? _request;
    private static Task _running = Task.CompletedTask;
    private static NetworkDiagnosticsSnapshot _current = Empty();
    private static bool _initialized;
    public static event Action? Changed;
    public static NetworkDiagnosticsSnapshot Current { get { lock (Gate) return _current; } }
    private static WebsiteDiagnosticResult[] Pending() => NetworkDiagnosticTargets.All.Select(target =>
        new WebsiteDiagnosticResult(target, "未检测", null, 0, NetworkRouteEvidence.Unknown("等待本轮内核证据"), "点击开始检测全部网址")).ToArray();
    private static NetworkDiagnosticsSnapshot Empty() => new(-1, false, "连接后检测当前内核与分流规则", "尚未检测", null, Pending());

    public static void Init()
    {
        if (_initialized) return;
        _initialized = true;
        ConnectionService.StateChanged += OnSessionChanged;
    }
    private static void OnSessionChanged()
    {
        Request? old;
        lock (Gate)
        {
            if (ConnectionService.IsConnected && _current.Epoch == ConnectionService.SessionEpoch) return;
            old = _request; _request = null;
            _current = Empty() with { Summary = "会话已变化，请重新检测；旧结果不代表当前连接" };
        }
        old?.Cancel();
        RaiseChanged();
    }
    public static void Cancel()
    {
        Request? active;
        lock (Gate) active = _request;
        active?.Cancel();
        CurrentExitService.Cancel();
    }

    public static Task RunAsync() => Task.WhenAll(RunAsync(WebsiteDiagnosticProbe.CheckAsync), CurrentExitService.RunAsync(refresh: true));

    // The transport boundary is injectable for lifecycle tests; production always uses the overload above.
    internal static Task RunAsync(Func<NetworkDiagnosticTarget, KernelRouteContext, bool, CancellationToken, Task<WebsiteDiagnosticResult>> probe)
    {
        var session = ConnectionService.CaptureDiagnosticSession();
        if (session == null || App.IsShuttingDown) return Task.CompletedTask;
        Request? previous;
        Task running;
        lock (Gate)
        {
            if (_request != null && _current.IsRunning && _current.Epoch == session.Epoch) return _running;
            previous = _request;
            var request = new Request(session, probe);
            _request = request;
            var mode = session.Mode switch { "rule" => "规则", "global" => "全局", "direct" => "直连", _ => session.Mode };
            _current = new(session.Epoch, true, $"{mode}模式 · {session.Kind} · {(session.Tun ? "系统 TUN 路由" : "当前内核 HTTP 入口")} · 仅检测，不更改连接",
                "正在逐站检测…", null, Pending());
            running = _running = Task.Run(() => RunCoreAsync(request));
        }
        previous?.Cancel();
        RaiseChanged();
        return running;
    }

    private static async Task RunCoreAsync(Request request)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(request.Token);
        // Each target keeps its own ten-second limit; queued targets need the same budget.
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Ceiling(NetworkDiagnosticTargets.All.Count / 3d) * 10 + 5));
        try
        {
            var context = KernelRouteContext.Load(request.Session);
            using var gate = new SemaphoreSlim(3, 3);
            await Task.WhenAll(NetworkDiagnosticTargets.All.Select(async (target, index) =>
            {
                var acquired = false;
                try
                {
                    await gate.WaitAsync(budget.Token).ConfigureAwait(false); acquired = true;
                    Publish(request, index, new(target, "检测中", null, 0, NetworkRouteEvidence.Unknown("正在查询内核"), "HTTPS 请求进行中"));
                    var result = await request.Probe(target, context, request.Session.Tun, budget.Token).ConfigureAwait(false);
                    Publish(request, index, result);
                }
                catch (OperationCanceledException)
                { Publish(request, index, new(target, "已取消", null, 0, NetworkRouteEvidence.Unknown("检测未完成"), "用户停止、会话变化或整轮预算用尽")); }
                catch (Exception ex)
                { Publish(request, index, new(target, "检测异常", null, 0, NetworkRouteEvidence.Unknown("未取得可靠证据"), $"检测异常：{ex.GetType().Name}")); }
                finally { if (acquired) gate.Release(); }
            })).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            lock (Gate)
            {
                if (IsCurrent(request)) _current = _current with { Summary = $"无法读取当前运行配置：{ex.GetType().Name}；未猜测出口" };
            }
        }
        finally
        {
            lock (Gate)
            {
                if (IsCurrent(request))
                {
                    var results = _current.Results;
                    var completed = results.Count(x => x.State is not ("未检测" or "检测中"));
                    var reachable = results.Count(x => x.State == "可达");
                    var recovered = results.Count(x => x.RecoveredAfterRetry);
                    var restricted = results.Count(x => x.State == "站点限制");
                    _current = _current with { IsRunning = false, CompletedAt = DateTimeOffset.UtcNow,
                        Summary = completed == 0 ? _current.Summary : $"已完成 {completed}/{results.Count} 项 · 可达 {reachable}（复测恢复 {recovered}） · 站点限制 {restricted} · 其他 {completed - reachable - restricted}" };
                    _request = null;
                }
            }
            request.Finish();
            RaiseChanged();
        }
    }
    private static bool IsCurrent(Request request) => ReferenceEquals(_request, request)
        && request.Session.Epoch == ConnectionService.SessionEpoch && ConnectionService.IsConnected && !App.IsShuttingDown;
    private static void Publish(Request request, int index, WebsiteDiagnosticResult result)
    {
        lock (Gate)
        {
            if (!IsCurrent(request)) return;
            var results = _current.Results.ToArray(); results[index] = result;
            _current = _current with { Results = results };
        }
        RaiseChanged();
    }
    private static void RaiseChanged()
    {
        if (App.IsShuttingDown) return;
        var handlers = Changed;
        if (handlers == null) return;
        foreach (Action handler in handlers.GetInvocationList())
            try { handler(); } catch (Exception ex) { LogBus.App($"网络详情订阅者异常：{ex.GetType().Name}", "Warning"); }
    }
}
