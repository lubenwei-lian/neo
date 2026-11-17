using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Org.BouncyCastle.Crypto.Digests;

namespace Neo.App.Services
{
    public static class LogBus { public static void App(string message, string level = "Info") { } }
}

namespace Neo.KernelChecks
{
    public sealed class SocksUdpFixture : IAsyncDisposable
    {
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _cts = new();
        private readonly LoopbackServer _tcp;
        private readonly Task _echo;
        private readonly byte _marker;
        public int Port => _tcp.Port;
        public SocksUdpFixture(byte marker)
        {
            _marker = marker; _echo = EchoAsync();
            _tcp = new LoopbackServer(async (peer, ct) => {
                var stream = peer.GetStream();
                var greeting = await LoopbackServer.ReadBytes(stream, 3, ct);
                if (!greeting.SequenceEqual(new byte[] {5, 1, 0})) throw new IOException("bad test greeting");
                await stream.WriteAsync(new byte[] {5, 0}, ct);
                var request = await LoopbackServer.ReadBytes(stream, 10, ct);
                if (request[1] != 3) throw new IOException("expected UDP ASSOCIATE");
                var port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
                await stream.WriteAsync(new byte[] {5, 0, 0, 1, 127, 0, 0, 1, (byte)(port >> 8), (byte)port}, ct);
                var buf = new byte[32]; while (await stream.ReadAsync(buf, ct) > 0) { }
            });
        }
        private async Task EchoAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var packet = await _udp.ReceiveAsync(_cts.Token);
                    await _udp.SendAsync(packet.Buffer.Append(_marker).ToArray(), packet.RemoteEndPoint, _cts.Token);
                }
            }
            catch (OperationCanceledException) { }
        }
        public async ValueTask DisposeAsync()
        {
            _cts.Cancel(); await _echo; _udp.Dispose(); await _tcp.DisposeAsync(); _cts.Dispose();
        }
    }

    public sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentBag<Task> _clients = [];
        private readonly Func<TcpClient, CancellationToken, Task> _handle;
        private readonly Task _accept;
        public int Port { get; }
        public LoopbackServer(Func<TcpClient, CancellationToken, Task> handle)
        {
            _handle = handle; _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _accept = AcceptAsync();
        }
        private async Task AcceptAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var peer = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _clients.Add(Task.Run(async () => { using (peer) { try { await _handle(peer, _cts.Token); } catch { } } }));
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) when (_cts.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync()
        {
            _cts.Cancel(); _listener.Stop(); await _accept;
            await Task.WhenAll(_clients).WaitAsync(TimeSpan.FromSeconds(5)); _cts.Dispose();
        }
        public static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
        }
        public static async Task<byte[]> ReadBytes(Stream stream, int count, CancellationToken ct)
        {
            var data = new byte[count]; await stream.ReadExactlyAsync(data, ct); return data;
        }
        public static async Task<string> ReadHeaders(Stream stream, CancellationToken ct)
        {
            using var bytes = new MemoryStream();
            while (bytes.Length < 16384)
            {
                var next = await ReadBytes(stream, 1, ct); bytes.Write(next);
                if (bytes.Length >= 4 && Encoding.ASCII.GetString(bytes.GetBuffer(), (int)bytes.Length - 4, 4) == "\r\n\r\n")
                    return Encoding.ASCII.GetString(bytes.ToArray());
            }
            throw new IOException("oversize test header");
        }
    }

    public sealed class TrojanFixture : IAsyncDisposable
    {
        private readonly X509Certificate2 _certificate;
        private readonly LoopbackServer _server;
        private readonly byte[] _passwordHash;
        private readonly int _originPort;
        public readonly ConcurrentQueue<string> Errors = new();
        private int _tunnels;
        public int Port => _server.Port;
        public int Tunnels => Volatile.Read(ref _tunnels);
        public string CertificatePin => _certificate.GetCertHashString(HashAlgorithmName.SHA256);
        public const string Password = "synthetic-regression-password";
        public TrojanFixture(int originPort)
        {
            _originPort = originPort;
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost");
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            using var generated = request.CreateSelfSigned(DateTimeOffset.Now.AddMinutes(-5), DateTimeOffset.Now.AddHours(1));
            // Windows Schannel 服务端需要可用的持久密钥句柄；不导入信任存储。
            _certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
            var digest = new Sha224Digest(); var password = Encoding.UTF8.GetBytes(Password);
            digest.BlockUpdate(password, 0, password.Length); var hash = new byte[digest.GetDigestSize()]; digest.DoFinal(hash, 0);
            _passwordHash = Encoding.ASCII.GetBytes(Convert.ToHexString(hash).ToLowerInvariant());
            _server = new LoopbackServer(async (client, ct) => {
                try { await HandleAsync(client, ct); }
                catch (Exception ex) when (!ct.IsCancellationRequested) { Errors.Enqueue(ex.ToString()); throw; }
            });
        }
        private async Task HandleAsync(TcpClient client, CancellationToken ct)
        {
            using var tls = new SslStream(client.GetStream(), false);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                ServerCertificate = _certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            }, ct);
            var hash = await LoopbackServer.ReadBytes(tls, 56, ct);
            if (!hash.SequenceEqual(_passwordHash)) throw new IOException("test password mismatch");
            if (!Encoding.ASCII.GetString(await LoopbackServer.ReadBytes(tls, 2, ct)).Equals("\r\n")) throw new IOException("bad Trojan header");
            var command = await LoopbackServer.ReadBytes(tls, 2, ct);
            if (command[0] != 1) throw new IOException("test only permits TCP");
            IPAddress ip;
            if (command[1] == 1) ip = new IPAddress(await LoopbackServer.ReadBytes(tls, 4, ct));
            else if (command[1] == 3)
            {
                var size = (await LoopbackServer.ReadBytes(tls, 1, ct))[0];
                var name = Encoding.ASCII.GetString(await LoopbackServer.ReadBytes(tls, size, ct));
                if (name is not ("localhost" or "127.0.0.1")) throw new IOException("test denies non-loopback name");
                ip = IPAddress.Loopback;
            }
            else throw new IOException("test only permits loopback IPv4/name");
            var bytes = await LoopbackServer.ReadBytes(tls, 2, ct); var port = bytes[0] * 256 + bytes[1];
            await LoopbackServer.ReadBytes(tls, 2, ct);
            if (!IPAddress.IsLoopback(ip) || port != _originPort) throw new IOException("test denies non-fixture destination");
            using var origin = new TcpClient(); await origin.ConnectAsync(ip, port, ct);
            Interlocked.Increment(ref _tunnels);
            var upstream = tls.CopyToAsync(origin.GetStream(), ct);
            var downstream = origin.GetStream().CopyToAsync(tls, ct);
            try { await Task.WhenAny(upstream, downstream); }
            finally { client.Dispose(); origin.Dispose(); try { await Task.WhenAll(upstream, downstream); } catch { } }
        }
        public async ValueTask DisposeAsync() { await _server.DisposeAsync(); _certificate.Dispose(); }
    }

    public sealed class RunningKernel : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _output;
        private readonly Task<string> _error;
        private readonly string _logPath;
        public int Pid => _process.Id;
        public bool HasExited => _process.HasExited;
        public RunningKernel(string exe, string config, bool singbox)
        {
            _logPath = Path.ChangeExtension(config, ".run.log");
            var psi = new ProcessStartInfo(exe) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exe),
            };
            psi.ArgumentList.Add("run"); psi.ArgumentList.Add(singbox ? "-c" : "-config"); psi.ArgumentList.Add(config);
            _process = Process.Start(psi) ?? throw new IOException("test kernel did not start");
            _output = _process.StandardOutput.ReadToEndAsync(); _error = _process.StandardError.ReadToEndAsync();
        }
        public async Task Ready(int port)
        {
            using var cts = new CancellationTokenSource(8000);
            while (!cts.IsCancellationRequested)
            {
                if (_process.HasExited) throw new IOException("isolated kernel exited: " + await _error + await _output);
                try { using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, port, cts.Token); return; }
                catch (SocketException) { await Task.Delay(50, cts.Token); }
            }
            throw new TimeoutException("isolated kernel not ready");
        }
        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(); await Task.WhenAll(_output, _error);
            await File.WriteAllTextAsync(_logPath, _output.Result + _error.Result); _process.Dispose();
        }
    }
}
