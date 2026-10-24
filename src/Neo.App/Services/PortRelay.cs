using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Neo.App.Services;

/// <summary>
/// 本地端口中继：监听用户配置的 socks/http 端口（21808/21809），把连接转发到
/// 当前「活跃内核」的内部端口（21908/21909）。
///
///   切换节点原本必须「停旧内核 → 起新内核」，中间 3～4 秒完全断网。
///   加了这一层之后，用户端口固定由本类监听，内核端口只是「后端」；
///   切换时只改后端指针 —— 新连接立刻走新内核，已建立的连接继续用旧内核直到自然结束，
///   旧内核延迟关闭。用户全程不需要重连，也不需要改系统代理。
///
///   旧实现只转发 TCP：客户端握手被透传给后端内核，拿到的 BND 是内核自己的回环 UDP 端口。
///   后果：① 本机应用「碰巧能用」（直接连 127.0.0.1 的内核端口），但流量绕过了中继；
///         ② 「允许局域网连接」时局域网客户端拿到 127.0.0.1，UDP 直接不可用；
///         ③ 无感切换只覆盖 TCP，UDP 关联仍绑在旧内核上。
///   现在中继自己应答 UDP ASSOCIATE（BND = 本中继的监听地址/端口），把客户端的 SOCKS5 UDP
///   封包原样转发给后端内核的 UDP 中继端口；控制连接关闭即回收。HTTP 端口没有 UDP，保持纯 TCP。
/// </summary>
public sealed class PortRelay : IDisposable
{
    private const byte Socks5Version = 0x05;
    private const byte Socks5MethodNoAuth = 0x00;
    private const byte Socks5MethodNone = 0xFF;
    private const byte Socks5CmdUdpAssociate = 0x03;
    private const byte Socks5AtypIpv4 = 0x01;
    private const byte Socks5AtypDomain = 0x03;
    private const byte Socks5AtypIpv6 = 0x04;
    /// <summary>等客户端发首字节的上限；超时按「不说话的老式客户端」退化到纯 TCP 透传。</summary>
    private static readonly TimeSpan FirstByteTimeout = TimeSpan.FromSeconds(30);

    private readonly int _listenPort;
    private readonly IPAddress _listenAddress;
    private readonly Action<int>? _onBackendFail;
    // AcceptLoopAsync 拿到的是已取消的 token，端口在监听但永不接受连接。
    private CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private volatile int _backendPort;
    private volatile bool _running;
    private long _connCount;
    /// <summary>按后端端口统计的活跃连接数（用于「等旧内核的连接全部结束再退役」）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> _activeByBackend = new();

    private readonly object _udpSync = new();
    private Socket? _udpRelay;
    private Task? _udpLoop;
    /// <summary>已学到 UDP 源端点的关联（客户端端点 → 关联）。</summary>
    private readonly ConcurrentDictionary<IPEndPoint, UdpAssociation> _udpByClient = new();
    /// <summary>还没收到客户端首个 UDP 包的关联（按客户端 IP 认领）。</summary>
    private readonly List<UdpAssociation> _udpPending = new();

    /// <param name="listenAddress">监听地址：默认 127.0.0.1；「允许局域网连接」时传 IPAddress.Any。</param>
    public PortRelay(int listenPort, int backendPort, Action<int>? onBackendFail = null, IPAddress? listenAddress = null)
    {
        _listenPort = listenPort;
        _backendPort = backendPort;
        _listenAddress = listenAddress ?? IPAddress.Loopback;
        _onBackendFail = onBackendFail;
    }

    public int ListenPort => _listenPort;
    public IPAddress ListenAddress => _listenAddress;
    public int BackendPort => _backendPort;
    public bool IsRunning => _running;
    public long ConnCount => Interlocked.Read(ref _connCount);

    /// <summary>指定后端端口当前还有多少条连接（切换后旧内核靠它判断何时可以退役）。</summary>
    public int ActiveFor(int backendPort)
        => _activeByBackend.TryGetValue(backendPort, out var n) ? n : 0;

    public void Start()
    {
        if (_running) return;
        // 否则新循环立刻读到取消状态退出（表现为「端口在监听、连上就卡死」）。
        if (_cts.IsCancellationRequested)
        {
            try { _cts.Dispose(); } catch { }
            _cts = new CancellationTokenSource();
        }
        _listener = new TcpListener(_listenAddress, _listenPort);
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
        TcpClient? backend = null;
        UdpAssociation? udp = null;
        var target = _backendPort;
        var counted = false;
        try
        {
            client.NoDelay = true;
            var clientStream = client.GetStream();

            // ---- 1) 探测首个字节：不是 SOCKS5 就按旧逻辑原样透传 ----
            byte[]? first;
            try
            {
                first = await ReadExactAsync(clientStream, 1, ct, FirstByteTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // 客户端连上 30 秒不发数据：保持旧行为（连后端，双向透传，等它自己发）。
                backend = await ConnectBackendAsync(target, ct).ConfigureAwait(false);
                if (backend == null) return;
                CountBackend(target); counted = true;
                await PumpBothAsync(client, backend, ct).ConfigureAwait(false);
                return;
            }
            if (first == null) return; // 连上就断：不留半开连接

            if (first[0] != Socks5Version)
            {
                // HTTP 代理（CONNECT/GET）等：直接透传，不解析。
                backend = await ConnectBackendAsync(target, ct).ConfigureAwait(false);
                if (backend == null) return;
                backend.NoDelay = true;
                CountBackend(target); counted = true;
                try { await backend.GetStream().WriteAsync(first.AsMemory(), ct).ConfigureAwait(false); }
                catch { return; }
                await PumpBothAsync(client, backend, ct).ConfigureAwait(false);
                return;
            }

            // ---- 2) SOCKS5 方法协商 ----
            var methodCount = await ReadExactAsync(clientStream, 1, ct, FirstByteTimeout).ConfigureAwait(false);
            if (methodCount == null || methodCount[0] == 0) return;
            var methods = await ReadExactAsync(clientStream, methodCount[0], ct, FirstByteTimeout).ConfigureAwait(false);
            if (methods == null) return;
            var greeting = new byte[2 + methods.Length];
            greeting[0] = Socks5Version;
            greeting[1] = (byte)methods.Length;
            methods.CopyTo(greeting, 2);

            backend = await ConnectBackendAsync(target, ct).ConfigureAwait(false);
            if (backend == null)
            {
                await WriteMethodFailAsync(clientStream, ct).ConfigureAwait(false);
                return;
            }
            backend.NoDelay = true;
            CountBackend(target); counted = true;
            var backendStream = backend.GetStream();
            await backendStream.WriteAsync(greeting.AsMemory(), ct).ConfigureAwait(false);
            var methodReply = await ReadExactAsync(backendStream, 2, ct, FirstByteTimeout).ConfigureAwait(false);
            if (methodReply == null || methodReply[0] != Socks5Version) return;
            await clientStream.WriteAsync(methodReply.AsMemory(), ct).ConfigureAwait(false);

            if (methodReply[1] != Socks5MethodNoAuth)
            {
                // 后端要求用户名/密码等协商：中继不解析，退化为纯字节透传（客户端自己完成认证）。
                await PumpBothAsync(client, backend, ct).ConfigureAwait(false);
                return;
            }

            // ---- 3) 读客户端请求（VER CMD RSV ATYP ADDR PORT）----
            var head = await ReadExactAsync(clientStream, 4, ct, FirstByteTimeout).ConfigureAwait(false);
            if (head == null) return;
            if (head[0] != Socks5Version) { await ForwardThenPumpAsync(client, backend, head, ct).ConfigureAwait(false); return; }
            var body = await ReadSocks5AddrPortAsync(clientStream, head[3], ct).ConfigureAwait(false);
            if (body == null) return;
            var request = new byte[4 + body.Length];
            head.CopyTo(request, 0);
            body.CopyTo(request, 4);

            if (head[1] != Socks5CmdUdpAssociate)
            {
                await ForwardThenPumpAsync(client, backend, request, ct).ConfigureAwait(false);
                return;
            }

            // ---- 4) UDP ASSOCIATE：后端握手已完成，把请求转给后端拿它的 UDP 中继端点 ----
            await backendStream.WriteAsync(request.AsMemory(), ct).ConfigureAwait(false);
            var reply = await ReadSocks5ReplyAsync(backendStream, ct).ConfigureAwait(false);
            if (reply == null) return;
            if (reply[1] != 0x00)
            {
                await clientStream.WriteAsync(reply.AsMemory(), ct).ConfigureAwait(false);
                return;
            }
            var backendUdp = ParseSocks5ReplyEndpoint(reply);
            if (!EnsureUdpRelay() || backendUdp == null)
            {
                await WriteSocks5CmdFailAsync(clientStream, ct).ConfigureAwait(false);
                return;
            }

            var backendUdpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                backendUdpSocket.Connect(NormalizeUdpBackend(backendUdp));
            }
            catch
            {
                try { backendUdpSocket.Dispose(); } catch { }
                await WriteSocks5CmdFailAsync(clientStream, ct).ConfigureAwait(false);
                return;
            }

            // 告诉客户端：把 SOCKS5 UDP 封包发到本中继的这个端点。
            var clientPeer = (IPEndPoint)client.Client.RemoteEndPoint!;
            var ourUdp = (IPEndPoint)_udpRelay!.LocalEndPoint!;
            var advertiseAddress = ourUdp.Address;
            if (advertiseAddress.Equals(IPAddress.Any) || advertiseAddress.Equals(IPAddress.IPv6Any))
                advertiseAddress = ((IPEndPoint)client.Client.LocalEndPoint!).Address; // 局域网：用客户端实际连到的那块网卡地址
            var ourReply = BuildSocks5Reply(advertiseAddress, ourUdp.Port);
            await clientStream.WriteAsync(ourReply.AsMemory(), ct).ConfigureAwait(false);

            udp = new UdpAssociation(client, backend, backendUdpSocket, clientPeer.Address, target);
            RegisterAssociation(udp, request);
            LogBus.App($"端口中继 {_listenPort}：UDP ASSOCIATE 已建立（后端 TCP {target}，后端 UDP {backendUdp}，向客户端通告 {advertiseAddress}:{ourUdp.Port}）", "Debug");
            _ = BackendUdpLoopAsync(udp, _udpRelay!, ct);
            // 控制连接是 UDP 关联的生命周期：任一端关闭即回收。
            await WaitControlCloseAsync(clientStream, backendStream, ct).ConfigureAwait(false);
        }
        catch { }
        finally
        {
            if (udp != null) CloseAssociation(udp);
            if (counted) _activeByBackend.AddOrUpdate(target, 0, (_, v) => Math.Max(0, v - 1));
            try { client.Dispose(); } catch { }
            try { backend?.Dispose(); } catch { }
        }
    }

    private void CountBackend(int target)
    {
        Interlocked.Increment(ref _connCount);
        _activeByBackend.AddOrUpdate(target, 1, (_, v) => v + 1);
    }

    private async Task<TcpClient?> ConnectBackendAsync(int target, CancellationToken ct)
    {
        var backend = new TcpClient();
        try
        {
            await backend.ConnectAsync(IPAddress.Loopback, target, ct).ConfigureAwait(false);
            return backend;
        }
        catch
        {
            try { backend.Dispose(); } catch { }
            _onBackendFail?.Invoke(target);
            return null;
        }
    }

    // ==================== SOCKS5 协议小工具 ====================

    /// <summary>读满 n 字节；对端提前 EOF 返回 null；超时抛 TimeoutException。</summary>
    private static async Task<byte[]?> ReadExactAsync(NetworkStream stream, int n, CancellationToken ct, TimeSpan? timeout = null)
    {
        var buf = new byte[n];
        var offset = 0;
        using var timeoutCts = timeout.HasValue
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        if (timeoutCts != null) timeoutCts.CancelAfter(timeout!.Value);
        var token = timeoutCts?.Token ?? ct;
        try
        {
            while (offset < n)
            {
                var read = await stream.ReadAsync(buf.AsMemory(offset, n - offset), token).ConfigureAwait(false);
                if (read <= 0) return null;
                offset += read;
            }
            return buf;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }

    private static async Task<byte[]?> ReadSocks5AddrPortAsync(NetworkStream stream, byte atyp, CancellationToken ct)
    {
        switch (atyp)
        {
            case Socks5AtypIpv4: return await ReadExactAsync(stream, 6, ct, FirstByteTimeout).ConfigureAwait(false);
            case Socks5AtypIpv6: return await ReadExactAsync(stream, 18, ct, FirstByteTimeout).ConfigureAwait(false);
            case Socks5AtypDomain:
                var lenByte = await ReadExactAsync(stream, 1, ct, FirstByteTimeout).ConfigureAwait(false);
                if (lenByte == null) return null;
                var rest = await ReadExactAsync(stream, lenByte[0] + 2, ct, FirstByteTimeout).ConfigureAwait(false);
                if (rest == null) return null;
                var all = new byte[1 + rest.Length];
                all[0] = lenByte[0];
                rest.CopyTo(all, 1);
                return all;
            default:
                return null;
        }
    }

    private static async Task<byte[]?> ReadSocks5ReplyAsync(NetworkStream stream, CancellationToken ct)
    {
        var head = await ReadExactAsync(stream, 4, ct, FirstByteTimeout).ConfigureAwait(false);
        if (head == null) return null;
        var rest = await ReadSocks5AddrPortAsync(stream, head[3], ct).ConfigureAwait(false);
        if (rest == null) return null;
        var all = new byte[head.Length + rest.Length];
        head.CopyTo(all, 0);
        rest.CopyTo(all, head.Length);
        return all;
    }

    private static IPEndPoint? ParseSocks5ReplyEndpoint(byte[] reply)
    {
        // reply: VER REP RSV ATYP ADDR PORT
        if (reply.Length < 4) return null;
        var atyp = reply[3];
        if (atyp == Socks5AtypIpv4 && reply.Length >= 10)
        {
            var ip = new IPAddress(reply.AsSpan(4, 4));
            var port = (reply[8] << 8) | reply[9];
            return new IPEndPoint(ip, port);
        }
        if (atyp == Socks5AtypIpv6 && reply.Length >= 22)
        {
            var ip = new IPAddress(reply.AsSpan(4, 16));
            var port = (reply[20] << 8) | reply[21];
            return new IPEndPoint(ip, port);
        }
        return null;
    }

    private static byte[] BuildSocks5Reply(IPAddress address, int port)
    {
        var addrBytes = address.GetAddressBytes();
        var atyp = addrBytes.Length == 16 ? Socks5AtypIpv6 : Socks5AtypIpv4;
        var reply = new byte[4 + addrBytes.Length + 2];
        reply[0] = Socks5Version;
        reply[1] = 0x00;
        reply[2] = 0x00;
        reply[3] = atyp;
        addrBytes.CopyTo(reply, 4);
        reply[4 + addrBytes.Length] = (byte)(port >> 8);
        reply[5 + addrBytes.Length] = (byte)(port & 0xFF);
        return reply;
    }

    private static async Task WriteMethodFailAsync(NetworkStream stream, CancellationToken ct)
    {
        try { await stream.WriteAsync(new byte[] { Socks5Version, Socks5MethodNone }.AsMemory(), ct).ConfigureAwait(false); } catch { }
    }

    private static async Task WriteSocks5CmdFailAsync(NetworkStream stream, CancellationToken ct)
    {
        try { await stream.WriteAsync(BuildSocks5Reply(IPAddress.Any, 0).AsMemory(), ct).ConfigureAwait(false); } catch { }
    }

    /// <summary>后端 UDP 端点如果是 0.0.0.0/[::]，换成本机回环（sing-box/xray 常这样回复）。</summary>
    private static IPEndPoint NormalizeUdpBackend(IPEndPoint endpoint)
    {
        if (endpoint.Address.Equals(IPAddress.Any) || endpoint.Address.Equals(IPAddress.IPv6Any))
            return new IPEndPoint(IPAddress.Loopback, endpoint.Port);
        return endpoint;
    }

    private async Task ForwardThenPumpAsync(TcpClient client, TcpClient backend, byte[] prefix, CancellationToken ct)
    {
        try { await backend.GetStream().WriteAsync(prefix.AsMemory(), ct).ConfigureAwait(false); }
        catch { return; }
        await PumpBothAsync(client, backend, ct).ConfigureAwait(false);
    }

    // ==================== UDP 中继 ====================

    /// <summary>按需绑定 UDP 中继端口（与 TCP 监听端口同号，UDP/TCP 是两套命名空间），失败则本类 UDP 不可用。</summary>
    private bool EnsureUdpRelay()
    {
        lock (_udpSync)
        {
            if (_udpRelay != null) return true;
            try
            {
                var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                sock.Bind(new IPEndPoint(_listenAddress, _listenPort));
                _udpRelay = sock;
                _udpLoop = UdpLoopAsync(sock, _cts.Token);
                return true;
            }
            catch (Exception ex)
            {
                LogBus.App($"端口中继 {_listenPort}：UDP 中继端口绑定失败（{ex.GetType().Name}: {ex.Message}），SOCKS5 UDP 暂不可用", "Warning");
                return false;
            }
        }
    }

    private async Task UdpLoopAsync(Socket relay, CancellationToken ct)
    {
        var buf = new byte[65535];
        var any = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try { result = await relay.ReceiveFromAsync(buf.AsMemory(), SocketFlags.None, any, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            var from = (IPEndPoint)result.RemoteEndPoint;
            var assoc = FindAssociation(from);
            if (assoc == null) continue;
            try
            {
                await assoc.BackendUdp.SendAsync(buf.AsMemory(0, result.ReceivedBytes), SocketFlags.None, ct).ConfigureAwait(false);
            }
            catch { }
        }
    }

    private UdpAssociation? FindAssociation(IPEndPoint from)
    {
        if (_udpByClient.TryGetValue(from, out var known)) return known;
        lock (_udpSync)
        {
            foreach (var candidate in _udpPending)
            {
                if (!candidate.ClientAddress.Equals(from.Address)) continue;
                candidate.ClientEndpoint = from;
                _udpPending.Remove(candidate);
                _udpByClient[from] = candidate;
                return candidate;
            }
        }
        return null;
    }

    private async Task BackendUdpLoopAsync(UdpAssociation assoc, Socket relay, CancellationToken ct)
    {
        var buf = new byte[65535];
        while (!ct.IsCancellationRequested)
        {
            int n;
            try { n = await assoc.BackendUdp.ReceiveAsync(buf.AsMemory(), SocketFlags.None, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }
            if (n <= 0) break;
            var endpoint = assoc.ClientEndpoint;
            if (endpoint == null) continue;
            try { relay.SendTo(buf.AsSpan(0, n), endpoint); } catch { }
        }
    }

    private void RegisterAssociation(UdpAssociation assoc, byte[] request)
    {
        // 有些客户端会在请求里带上自己的 UDP 源端点；能解析出来就先按它归档，省一次认领。
        var expected = TryParseRequestEndpoint(request);
        lock (_udpSync)
        {
            if (expected != null && !expected.Address.Equals(IPAddress.Any))
            {
                assoc.ClientEndpoint = expected;
                _udpByClient[expected] = assoc;
            }
            else
            {
                _udpPending.Add(assoc);
            }
        }
    }

    private static IPEndPoint? TryParseRequestEndpoint(byte[] request)
    {
        // request: VER CMD RSV ATYP ADDR PORT
        if (request.Length < 4) return null;
        var atyp = request[3];
        if (atyp == Socks5AtypIpv4 && request.Length >= 10)
            return new IPEndPoint(new IPAddress(request.AsSpan(4, 4)), (request[8] << 8) | request[9]);
        if (atyp == Socks5AtypIpv6 && request.Length >= 22)
            return new IPEndPoint(new IPAddress(request.AsSpan(4, 16)), (request[20] << 8) | request[21]);
        return null;
    }

    private void CloseAssociation(UdpAssociation assoc)
    {
        try { assoc.Dispose(); } catch { }
        lock (_udpSync)
        {
            _udpPending.Remove(assoc);
        }
        if (assoc.ClientEndpoint != null) _udpByClient.TryRemove(assoc.ClientEndpoint, out _);
    }

    private static async Task WaitControlCloseAsync(NetworkStream clientStream, NetworkStream backendStream, CancellationToken ct)
    {
        var clientDrain = DrainUntilEofAsync(clientStream, ct);
        var backendDrain = DrainUntilEofAsync(backendStream, ct);
        await Task.WhenAny(clientDrain, backendDrain).ConfigureAwait(false);
    }

    private static async Task DrainUntilEofAsync(NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[512];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var n = await stream.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false);
                if (n <= 0) return;
            }
        }
        catch { }
    }

    /// <summary>一条 SOCKS5 UDP 关联：客户端控制连接 + 后端控制连接 + 后端 UDP 套接字。</summary>
    private sealed class UdpAssociation : IDisposable
    {
        public UdpAssociation(TcpClient control, TcpClient backendTcp, Socket backendUdp, IPAddress clientAddress, int backendPort)
        {
            Control = control;
            BackendTcp = backendTcp;
            BackendUdp = backendUdp;
            ClientAddress = clientAddress;
            BackendPort = backendPort;
        }

        public TcpClient Control { get; }
        public TcpClient BackendTcp { get; }
        public Socket BackendUdp { get; }
        public IPAddress ClientAddress { get; }
        public int BackendPort { get; }
        public IPEndPoint? ClientEndpoint { get; set; }

        public void Dispose()
        {
            try { BackendUdp.Dispose(); } catch { }
        }
    }

    // ==================== TCP 双向搬运 ====================

    private async Task PumpBothAsync(TcpClient client, TcpClient backend, CancellationToken ct)
    {
        try
        {
            var up = PumpAsync(client.GetStream(), backend.GetStream(), ct);
            var down = PumpAsync(backend.GetStream(), client.GetStream(), ct);
            await Task.WhenAny(up, down).ConfigureAwait(false);
            //   FTP、部分游戏/长轮询）。原实现立即 Dispose 会截断；这里给另一方向最多 5 秒
            //   把剩余数据吐完再收尾，超时则按原行为关闭。
            try { await Task.WhenAll(up, down).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch { }
        }
        catch { }
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
        Socket? udpRelay;
        lock (_udpSync)
        {
            udpRelay = _udpRelay;
            _udpRelay = null;
            _udpLoop = null;
            _udpPending.Clear();
            _udpByClient.Clear();
        }
        try { udpRelay?.Dispose(); } catch { }
    }

    public void Dispose()
    {
        Stop();
        try { _cts.Dispose(); } catch { }
    }
}
