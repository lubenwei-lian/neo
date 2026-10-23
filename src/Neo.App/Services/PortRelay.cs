using System.Net;
using System.Net.Sockets;

namespace Neo.App.Services;

/// <summary>
/// 本地端口中继：监听用户配置的 socks/http 端口（21808/21809），把 TCP 连接转发到
/// 当前「活跃内核」的内部端口（21908/21909）。
///
/// 为什么需要（2026-10-23 无感切换改造）：
///   切换节点原本必须「停旧内核 → 起新内核」，中间 3～4 秒完全断网。
///   加了这一层之后，用户端口固定由本类监听，内核端口只是「后端」；
///   切换时只改后端指针 —— 新连接立刻走新内核，已建立的连接继续用旧内核直到自然结束，
///   旧内核延迟关闭。用户全程不需要重连，也不需要改系统代理。
///
/// 约束：只转发 TCP。socks5 的 UDP ASSOCIATE 不走这里（TUN 模式不经过端口；
///   普通模式下 UDP 代理本来就极少用，目前功能不受影响）。
/// </summary>
public sealed class PortRelay : IDisposable
{
    private readonly int _listenPort;
    private readonly Action<int>? _onBackendFail;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private volatile int _backendPort;
    private volatile bool _running;
    private long _connCount;
    /// <summary>按后端端口统计的活跃连接数（用于「等旧内核的连接全部结束再退役」）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> _activeByBackend = new();

    public PortRelay(int listenPort, int backendPort, Action<int>? onBackendFail = null)
    {
        _listenPort = listenPort;
        _backendPort = backendPort;
        _onBackendFail = onBackendFail;
    }

    public int ListenPort => _listenPort;
    public int BackendPort => _backendPort;
    public bool IsRunning => _running;
    public long ConnCount => Interlocked.Read(ref _connCount);

    /// <summary>指定后端端口当前还有多少条连接（切换后旧内核靠它判断何时可以退役）。</summary>
    public int ActiveFor(int backendPort)
        => _activeByBackend.TryGetValue(backendPort, out var n) ? n : 0;

    public void Start()
    {
        if (_running) return;
        _listener = new TcpListener(IPAddress.Loopback, _listenPort);
        _listener.Start(512);
        _running = true;
        _ = AcceptLoopAsync(_cts.Token);
    }

    /// <summary>切换后端端口：立即对新连接生效；已建立的连接保持原后端（无感的关键）。</summary>
    public void SwitchBackend(int port)
    {
        var old = _backendPort;
        _backendPort = port;
        if (old != port)
            LogBus.App($"端口中继 {_listenPort}：后端 {old} → {port}（老连接继续走原内核，新连接立刻走新内核）", "Debug");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            _ = HandleAsync(client, ct);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        var backend = new TcpClient();
        try
        {
            client.NoDelay = true;
            backend.NoDelay = true;
            var target = _backendPort;
            try
            {
                await backend.ConnectAsync(IPAddress.Loopback, target, ct).ConfigureAwait(false);
            }
            catch
            {
                // 后端连不上 = 当前内核已经不可用：交给上层（切换逻辑）处理，本连接直接失败。
                _onBackendFail?.Invoke(target);
                return;
            }

            Interlocked.Increment(ref _connCount);
            _activeByBackend.AddOrUpdate(target, 1, (_, v) => v + 1);
            try
            {
                var up = PumpAsync(client.GetStream(), backend.GetStream(), ct);
                var down = PumpAsync(backend.GetStream(), client.GetStream(), ct);
                await Task.WhenAny(up, down).ConfigureAwait(false);
            }
            finally
            {
                _activeByBackend.AddOrUpdate(target, 0, (_, v) => Math.Max(0, v - 1));
            }
        }
        catch { }
        finally
        {
            try { client.Dispose(); } catch { }
            try { backend.Dispose(); } catch { }
        }
    }

    /// <summary>单向搬运；结束方向对端 Shutdown(Send)，让另一端读到 EOF 后自然收尾。</summary>
    private static async Task PumpAsync(NetworkStream from, NetworkStream to, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var n = await from.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false);
                if (n <= 0) break;
                await to.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            }
        }
        catch { }
        finally
        {
            try { to.Socket?.Shutdown(SocketShutdown.Send); } catch { }
        }
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        try { _cts.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _listener = null; } catch { }
    }

    public void Dispose()
    {
        Stop();
        try { _cts.Dispose(); } catch { }
    }
}
