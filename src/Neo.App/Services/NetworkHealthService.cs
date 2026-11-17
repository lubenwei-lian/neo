using Neo.Core;
using Neo.Core.Core;

namespace Neo.App.Services;

public enum NetworkHealthStatus { Pending, Available, Limited, Unavailable }

public sealed record NetworkHealthSnapshot(int Epoch, NetworkHealthStatus Status, bool IsChecking,
    DateTimeOffset? CheckedAt, NetworkAvailabilityResult? Result, bool SystemProxyEffective,
    int FailureStreak, string Summary, string Detail);

/// <summary>
/// 网络检测状态与会话绑定；只由故障转移的同一个串行检查器驱动，不再创建第二个定时器。
/// 切换/断开会取消旧检测，迟到结果不能把后继会话标成可用，也不能复活已断开的连接。
/// </summary>
public static class NetworkHealthService
{
    private sealed class Session(int epoch, NetworkAvailabilityProbe probe)
    {
        public readonly int Epoch = epoch;
        public readonly bool Tun = ConnectionService.IsTunSession;
        public readonly NetworkAvailabilityProbe Probe = probe;
        public readonly CancellationTokenSource Cancellation = new();
        public bool Busy;
        public bool Retired;
        public bool CancellationIssued;
        public bool Disposed;
    }

    private static readonly object Gate = new();
    private static Session? _session;
    private static NetworkHealthSnapshot _current = Idle();
    public static NetworkHealthSnapshot Current { get { lock (Gate) return _current; } }
    public static event Action? Changed;

    private static NetworkHealthSnapshot Idle() => new(-1, NetworkHealthStatus.Pending, false,
        null, null, false, 0, "连接后自动检测网络", "检测外网 HTTPS 转发及 Windows 实际代理配置；不代表每个网站均可访问。");

    internal static bool BeginSession(int epoch, NetworkAvailabilityProbe? probe = null)
    {
        Session? old;
        lock (Gate)
        {
            if (!ConnectionService.IsConnected || epoch != ConnectionService.SessionEpoch)
            {
                probe?.Dispose();
                return false;
            }
            if (_session?.Epoch == epoch && _session.Tun == ConnectionService.IsTunSession)
            {
                probe?.Dispose();
                return false;
            }
            old = _session;
            if (old != null) old.Retired = true;
            _session = new(epoch, probe ?? new NetworkAvailabilityProbe(LocalPorts.Http, LocalPorts.Socks,
                ConnectionService.IsTunSession));
            _current = new(epoch, NetworkHealthStatus.Pending, false, null, null, false, 0,
                "等待网络检测", "已启动内核，尚未确认网络可用。");
        }
        Retire(old);
        RaiseChanged();
        return true;
    }

    public static void Reset()
    {
        Session? old;
        lock (Gate)
        {
            old = _session;
            if (old != null) old.Retired = true;
            _session = null;
            _current = Idle();
        }
        Retire(old);
        RaiseChanged();
    }

    // Cancellation may synchronously run HTTP continuations; never invoke it while holding Gate.
    private static void Retire(Session? session)
    {
        if (session == null) return;
        try { session.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        lock (Gate)
        {
            session.CancellationIssued = true;
            DisposeRetired(session);
        }
    }

    // Gate held. Do not dispose the source while Cancel is still executing callbacks.
    private static void DisposeRetired(Session session)
    {
        if (!session.Retired || !session.CancellationIssued || session.Busy || session.Disposed) return;
        session.Disposed = true;
        session.Probe.Dispose();
        session.Cancellation.Dispose();
    }

    internal static async Task<NetworkAvailabilityResult?> CheckAsync(int epoch, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!ConnectionService.IsConnected || epoch != ConnectionService.SessionEpoch || App.IsShuttingDown) return null;
        BeginSession(epoch);
        Session session;
        lock (Gate)
        {
            if (_session is not { } active || active.Epoch != epoch || active.Busy || active.Retired) return null;
            session = active;
            session.Busy = true;
            _current = _current with { IsChecking = true };
        }
        RaiseChanged();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Cancellation.Token);
        try
        {
            var result = await session.Probe.CheckAsync(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            var effective = ConnectionService.IsTunSession || SystemProxy.IsEffective;
            NetworkHealthSnapshot previous;
            NetworkHealthSnapshot next;
            lock (Gate)
            {
                if (!IsCurrent(session)) return null;
                previous = _current;
                var failures = result.Available ? 0 : previous.FailureStreak + 1;
                var status = result.AllAvailable && effective ? NetworkHealthStatus.Available
                    : !result.Available && failures >= 2 ? NetworkHealthStatus.Unavailable
                    : NetworkHealthStatus.Limited;
                var summary = !effective ? "系统代理未生效"
                    : result.AllAvailable ? "网络可用"
                    : result.Available ? "部分代理路径异常"
                    : failures < 2 ? "网络检测未通过，等待复测" : "网络不可用";
                var lines = result.Routes.Select(route => route.Route + "：" +
                    string.Join("；", route.Attempts.Select(a => $"{a.Target} · {a.Detail} · {a.ElapsedMs} ms")));
                var detail = string.Join(Environment.NewLine, lines)
                    + Environment.NewLine + (ConnectionService.IsTunSession ? "已检测 TUN 系统路由。"
                        : effective ? "Windows 实际代理配置已核验。" : "本机代理探测与系统代理生效是两个条件；请检查代理设置或重连。")
                    + Environment.NewLine + "检测目标可达不代表 Steam 等所有网站均可用。";
                next = new(epoch, status, false, DateTimeOffset.UtcNow, result, effective, failures, summary, detail);
                _current = next;
            }
            RaiseChanged();
            lock (Gate) { if (!IsCurrent(session)) return null; }
            if (previous.Status != next.Status || previous.Summary != next.Summary)
            {
                LogBus.App("网络检测：" + next.Summary + Environment.NewLine + next.Detail,
                    next.Status == NetworkHealthStatus.Available ? "Info" : "Warning");
                if (next.Status == NetworkHealthStatus.Unavailable)
                {
                    IncidentLog.Record("network-health", next.Summary, next.Detail);
                    if (!AppState.State.FailoverEnabled)
                        ConnectionService.NotifyUser("内核仍在运行，但连续网络检测未通过；自动切换已关闭，请检查本机网络或更换节点");
                }
            }
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && session.Cancellation.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            var changed = false;
            lock (Gate)
            {
                session.Busy = false;
                if (ReferenceEquals(_session, session) && _current.IsChecking)
                {
                    _current = _current with { IsChecking = false };
                    changed = true;
                }
                DisposeRetired(session);
            }
            if (changed) RaiseChanged();
        }
    }

    private static bool IsCurrent(Session session) => ReferenceEquals(_session, session) && !session.Retired
        && session.Epoch == ConnectionService.SessionEpoch && ConnectionService.IsConnected && !App.IsShuttingDown;

    private static void RaiseChanged()
    {
        if (App.IsShuttingDown) return;
        var handlers = Changed;
        if (handlers == null) return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch (Exception ex) { LogBus.App($"网络状态订阅者异常：{ex.GetType().Name}", "Warning"); }
        }
    }
}
