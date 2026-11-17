using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Neo.App.Services;
using Neo.Core;

if (args.Length != 2) throw new ArgumentException("Usage: Neo.Performance <isolated-output-directory> <label>");
var output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
foreach (var field in new[] { "_rootDir", "_dataDir" })
    typeof(NeoPaths).GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, output);
var metrics = new List<object>();
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var token = deadline.Token;
await using var server = new PayloadServer();
var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
var relayPort = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
using var relay = new PortRelay(relayPort, server.Port); relay.Start();
var baselineType = typeof(PortRelay).Assembly.GetType("Neo.Baseline.Services.PortRelay");
var baselinePort = 0;
using var baselineRelay = baselineType == null ? null : (IDisposable)Activator.CreateInstance(baselineType,
    new object?[] { baselinePort = PayloadServer.FreePort(), server.Port, null, null })!;
baselineType?.GetMethod("Start")!.Invoke(baselineRelay, null);

async Task Transfer(int port, int bytes)
{
    using var client = new TcpClient { NoDelay = true };
    await client.ConnectAsync(IPAddress.Loopback, port, token);
    var request = new byte[5]; request[0] = 0x42;
    BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(1), bytes);
    var stream = client.GetStream(); await stream.WriteAsync(request, token);
    var buffer = new byte[64 * 1024]; var total = 0;
    while (total < bytes)
    {
        var n = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, bytes - total)), token);
        if (n == 0) throw new IOException("premature EOF");
        if (buffer[0] != 0xA7 || buffer[n - 1] != 0xA7) throw new IOException("corrupted payload");
        total += n;
    }
}
async Task Measure(string name, int port, int count, int bytes, int concurrency)
{
    for (var i = 0; i < 8; i++) await Transfer(port, 1024);
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    var allocated = GC.GetTotalAllocatedBytes(true);
    var cpu = Process.GetCurrentProcess().TotalProcessorTime;
    var pauses = GC.CollectionCount(0); var timer = Stopwatch.StartNew();
    var durations = new ConcurrentBag<double>();
    await Parallel.ForEachAsync(Enumerable.Range(0, count), new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = token },
        async (_, _) => { var start = Stopwatch.GetTimestamp(); await Transfer(port, bytes); durations.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds); });
    timer.Stop();
    var ordered = durations.Order().ToArray();
    var result = new { name, count, bytesPerConnection = bytes, concurrency, elapsedMs = timer.Elapsed.TotalMilliseconds,
        mibPerSecond = (double)count * bytes / (1024 * 1024) / timer.Elapsed.TotalSeconds,
        allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated,
        cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds,
        gen0Collections = GC.CollectionCount(0) - pauses,
        medianMs = ordered[ordered.Length / 2], p95Ms = ordered[(int)((ordered.Length - 1) * .95)] };
    metrics.Add(result); Console.WriteLine(JsonSerializer.Serialize(result));
}
for (var round = 0; round < 3; round++)
{
    if (baselineRelay != null)
    {
        if (args[1].StartsWith("sustained", StringComparison.Ordinal))
        {
            foreach (var mode in round % 2 == 0 ? new[] {"baseline", "relay"} : new[] {"relay", "baseline"})
                await Measure($"{mode}-sustained-{round}", mode == "baseline" ? baselinePort : relayPort, 16, 256 * 1024 * 1024, 4);
            continue;
        }
        // Baseline and optimized source share one process, runtime, server and sampling window.
        foreach (var mode in round % 2 == 0 ? new[] {"direct", "baseline", "relay"} : new[] {"relay", "baseline", "direct"})
        {
            var port = mode == "direct" ? server.Port : mode == "baseline" ? baselinePort : relayPort;
            await Measure($"{mode}-bulk-{round}", port, 8, 16 * 1024 * 1024, 4);
            await Measure($"{mode}-short-{round}", port, 512, 1024, 32);
        }
        continue;
    }
    // Alternate order so warmup, CPU frequency and antivirus do not always favor one path.
    foreach (var throughRelay in round % 2 == 0 ? new[] { false, true } : new[] { true, false })
    {
        var port = throughRelay ? relayPort : server.Port; var mode = throughRelay ? "relay" : "direct";
        await Measure($"{mode}-bulk-{round}", port, 8, 16 * 1024 * 1024, 4);
        await Measure($"{mode}-short-{round}", port, 1024, 1024, 32);
    }
}
var logTimer = Stopwatch.StartNew();
for (var i = 0; i < 2000; i++) LogBus.App($"synthetic performance log {i:D6}");
logTimer.Stop();
var submitMs = logTimer.Elapsed.TotalMilliseconds;
var flush = typeof(LogBus).GetMethod("Flush", new[] { typeof(TimeSpan) });
var flushTimer = Stopwatch.StartNew();
if (flush?.Invoke(null, new object[] { TimeSpan.FromSeconds(10) }) is false) throw new IOException("log flush timed out");
flushTimer.Stop();
var logFiles = Directory.GetFiles(NeoPaths.LogDir, "neo-*.log");
var lineCount = logFiles.Sum(file => File.ReadLines(file).Count(line => line.Contains("synthetic performance log")));
if (lineCount != 2000) throw new IOException($"lost logs: {lineCount}/2000");
var logResult = new { name = "log-submit-and-drain", count = 2000, submitMs, drainMs = flushTimer.Elapsed.TotalMilliseconds, persisted = lineCount };
metrics.Add(logResult); Console.WriteLine(JsonSerializer.Serialize(logResult));
AppState.State.LogLevel = "warning";
var filteredAllocated = GC.GetTotalAllocatedBytes(true); var filteredTimer = Stopwatch.StartNew();
for (var i=0;i<100_000;i++) LogBus.Core("2026/11/07 00:00:00.000 [123456] [Info] synthetic accepted connection");
filteredTimer.Stop();
var filterResult = new { name = "filtered-core-logs", count = 100_000, elapsedMs = filteredTimer.Elapsed.TotalMilliseconds,
    allocatedBytes = GC.GetTotalAllocatedBytes(true) - filteredAllocated };
metrics.Add(filterResult); Console.WriteLine(JsonSerializer.Serialize(filterResult));
var report = new { label = args[1], runtime = Environment.Version.ToString(), os = Environment.OSVersion.ToString(),
    utc = DateTimeOffset.UtcNow, scope = "Private loopback only; synthetic payloads and logs; no installed app or external node modification.", metrics };
await File.WriteAllTextAsync(Path.Combine(output, "benchmark.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
return 0;

sealed class PayloadServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<int, Task> _clients = new();
    private readonly byte[] _payload = Enumerable.Repeat((byte)0xA7, 64 * 1024).ToArray();
    private readonly Task _loop; private int _next;
    public int Port { get; }
    public PayloadServer() { _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port; _loop = Accept(); }
    public static int FreePort() { var listener = new TcpListener(IPAddress.Loopback,0); listener.Start();var port = ((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();return port; }
    private async Task Accept()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var peer = await _listener.AcceptTcpClientAsync(_stop.Token); var id = Interlocked.Increment(ref _next);
                var task = Serve(peer); _clients[id] = task;
                _ = task.ContinueWith(done => _clients.TryRemove(id, out _), TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
    }
    private async Task Serve(TcpClient peer)
    {
        using (peer)
        {
            try
            {
                peer.NoDelay = true; var stream = peer.GetStream(); var request = new byte[5];
                await stream.ReadExactlyAsync(request, _stop.Token);
                if (request[0] != 0x42) throw new IOException("invalid request");
                var remaining = BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(1));
                while (remaining > 0)
                {
                    var n = Math.Min(remaining, _payload.Length);
                    await stream.WriteAsync(_payload.AsMemory(0, n), _stop.Token); remaining -= n;
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Stop(); await _loop;
        await Task.WhenAll(_clients.Values).WaitAsync(TimeSpan.FromSeconds(5)); _stop.Dispose();
    }
}
