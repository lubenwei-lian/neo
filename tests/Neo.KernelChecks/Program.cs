using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Neo.Core;
using Neo.Core.Core;
using Neo.Core.Models;
using Neo.App.Services;
using Neo.KernelChecks;

// 独立 fallback 夹具：同目录 apphost 副本作为 xray.exe，仅输出合成统计，无网络操作。
if (args.FirstOrDefault() == "api")
{
    Console.WriteLine("{\"stat\":[{\"name\":\"outbound>>>proxy>>>traffic>>>uplink\",\"value\":\"41\"},{\"name\":\"outbound>>>proxy>>>traffic>>>downlink\",\"value\":\"43\"}]}");
    return 0;
}
if (args.Length != 2) throw new ArgumentException("Usage: Neo.KernelChecks <runtime-directory> <isolated-output-directory>");
var runtime = Path.GetFullPath(args[0]); var output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
typeof(NeoPaths).GetField("_runtimeDir", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, runtime);
typeof(NeoPaths).GetField("_rootDir", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, output);
typeof(NeoPaths).GetField("_dataDir", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, output);
var results = new List<object>(); var failures = 0;
object? statsBenchmark = null;
var startupBenchmarks = new List<object>();
void Check(bool ok, string reason) { if (!ok) throw new InvalidOperationException(reason); }
async Task Test(string name, Func<Task> action)
{
    try { await action(); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; results.Add(new { name, passed = false, error = ex.ToString() }); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
string Build(NodeItem node, bool sb, int socks, int http, int api) => sb
    ? SingBoxConfigBuilder.Build(node, socks, http, "global", false, false, false, false, null, "debug", "223.5.5.5", clashApiPort: api)
    : XrayConfigBuilder.Build(node, socks, http, "global", false, false, false, false, null, "debug", "223.5.5.5", api);
string Save(string name, string config) { var path = Path.Combine(output, name + ".json"); File.WriteAllText(path, config); return path; }
string Exe(bool sb) => Path.Combine(runtime, sb ? "sing-box.exe" : "xray.exe");
await Test("website diagnostic list contains all requested foreign and domestic targets", () => {
    var targets = NetworkDiagnosticTargets.All;
    Check(targets.Count >= 8 && targets.Select(x => x.Name).Distinct().Count() == targets.Count && targets.All(x => x.Url.Scheme == "https")
        && new[] { "Google", "Cloudflare", "X", "Steam 商店", "Steam 社区", "百度", "哔哩哔哩", "腾讯" }.All(name => targets.Any(x => x.Name == name)), "site list incomplete");
    return Task.CompletedTask;
});
await Test("website responses distinguish reachable restricted and unexpected content", () => {
    var google = NetworkDiagnosticTargets.All.First(x => x.Name == "Google");
    var steam = NetworkDiagnosticTargets.All.First(x => x.Name == "Steam 商店");
    Check(WebsiteDiagnosticProbe.Classify(google, 204).State == "可达" && WebsiteDiagnosticProbe.Classify(google, 200).State == "响应异常", "204 contract weakened");
    Check(WebsiteDiagnosticProbe.Classify(steam, 200).State == "可达" && WebsiteDiagnosticProbe.Classify(steam, 302).State == "可达"
        && WebsiteDiagnosticProbe.Classify(steam, 403).State == "站点限制" && WebsiteDiagnosticProbe.Classify(steam, 429).State == "站点限制"
        && WebsiteDiagnosticProbe.Classify(steam, 503).State == "站点异常", "status classes collapsed");
    return Task.CompletedTask;
});
await Test("routing protobuf extracts outbound and rejects truncation", () => {
    Check(XrayRouteClient.DecodeOutbound(new byte[] { 10, 3, 97, 98, 99, 98, 6, 100, 105, 114, 101, 99, 116 }) == "direct", "outbound field 12 incorrect");
    foreach (var bytes in new byte[][] { [98, 200], [0], [98, 5, 65], [255,255,255,255,255,255,255,255,255,255,1] })
    { try { XrayRouteClient.DecodeOutbound(bytes); throw new InvalidOperationException("malformed routing protobuf accepted"); } catch (InvalidDataException) { } }
    Check(XrayRouteClient.Encode("http-in", "www.baidu.com", 443, 49152).Length > 30, "routing request missing context");
    return Task.CompletedTask;
});
await Test("stats protobuf decoding preserves 64-bit counters and unknown fields", () => {
    static byte[] Varint(ulong value) { var bytes = new List<byte>(); while (value >= 128) { bytes.Add((byte)(value | 128)); value >>= 7; } bytes.Add((byte)value); return bytes.ToArray(); }
    var name = Encoding.UTF8.GetBytes("outbound>>>proxy>>>traffic>>>uplink");
    var entry = new byte[] {10, (byte)name.Length}.Concat(name).Concat(new byte[] {16}).Concat(Varint(1UL << 45))
        .Concat(new byte[] {29, 1, 2, 3, 4}).ToArray();
    var payload = new byte[] {10, (byte)entry.Length}.Concat(entry).Concat(new byte[] {24, 1}).ToArray();
    var decoded = XrayStatsClient.Decode(payload);
    Check(decoded.Count == 1 && decoded[0].Value == 1L << 45 && decoded[0].Name == Encoding.UTF8.GetString(name), "protobuf counter altered");
    return Task.CompletedTask;
});
await Test("stats protobuf rejects truncation and overflow", () => {
    foreach (var bytes in new byte[][] { [10, 255], [10, 8, 1], [0], [255,255,255,255,255,255,255,255,255,255,1] })
    {
        try { XrayStatsClient.Decode(bytes); throw new InvalidOperationException("malformed protobuf accepted"); }
        catch (InvalidDataException) { }
    }
    return Task.CompletedTask;
});
await Test("gRPC failure falls back to CLI with retry cooldown", () => {
    var host = Environment.ProcessPath ?? throw new InvalidOperationException("missing test apphost");
    Check(Path.GetFileName(host).Equals("Neo.KernelChecks.exe", StringComparison.OrdinalIgnoreCase), "fallback fixture requires generated apphost");
    var fake = Path.Combine(AppContext.BaseDirectory, "xray.exe");
    File.Copy(host, fake, true);
    var field = typeof(NeoPaths).GetField("_runtimeDir", BindingFlags.NonPublic | BindingFlags.Static)!;
    var port = LoopbackServer.FreePort();
    field.SetValue(null, AppContext.BaseDirectory);
    try
    {
        var first = XrayStats.Query(port); Check(first is { Uplink: 41, Downlink: 43 }, "CLI fallback lost stats");
        var retries = (System.Collections.Concurrent.ConcurrentDictionary<int, long>)typeof(XrayStats)
            .GetField("GrpcRetryAt", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var retryAt = retries[port]; var second = XrayStats.Query(port);
        Check(second == first && retries[port] == retryAt, "cooldown retried failing gRPC on every sample");
    }
    finally { field.SetValue(null, runtime); }
    return Task.CompletedTask;
});
var protocols = new[] { NodeProtocol.Trojan, NodeProtocol.Hysteria2, NodeProtocol.TUIC, NodeProtocol.AnyTls, NodeProtocol.WireGuard };
foreach (var sb in new[] {false,true})
await Test($"real {(sb ? "sing-box" : "xray")} startup verifies both proxy listeners", async () => {
    var node = new NodeItem { Protocol = NodeProtocol.Trojan, Server = "127.0.0.1", Port = 9,
        Password = "synthetic-startup-only", Security = "tls", Sni = "example.invalid" };
    var samples = new List<double>();
    for(var i=0;i<5;i++)
    {
        var socks = LoopbackServer.FreePort(); int http;
        do { http = LoopbackServer.FreePort(); } while(http == socks);
        var config = Save($"startup-{sb}-{i}", Build(node,sb,socks,http,LoopbackServer.FreePort()));
        using var core = new Neo.Core.Core.CoreProcess(Exe(sb),runtime);
        var timer = Stopwatch.StartNew(); core.Start(config);
        Check(await ProxyReadiness.WaitAsync(() => core.IsRunning,socks,http,TimeSpan.FromSeconds(8)), "real proxy failed readiness");
        timer.Stop(); samples.Add(timer.Elapsed.TotalMilliseconds);
        Check(core.StopAndVerify(), "startup fixture did not exit");
    }
    startupBenchmarks.Add(new { core = sb ? "sing-box" : "xray", samplesMs = samples, medianMs = samples.Order().ElementAt(samples.Count/2),
        baselineFixedWaitMs = 800, scope = "Local process launch and proxy listeners only; no remote TLS connection, TUN or full UI connect timing." });
});
foreach (var protocol in protocols)
foreach (var slot in new[] { 0, 1 })
{
    var sb = protocol != NodeProtocol.Trojan;
    var node = new NodeItem { Name = "synthetic", Protocol = protocol, Server = "127.0.0.1", Port = 9,
        Password = "synthetic-password", Uuid = "11111111-1111-1111-1111-111111111111", Security = "tls", Sni = "example.invalid",
        WgPublicKey = Convert.ToBase64String(Enumerable.Range(33, 32).Select(x => (byte)x).ToArray()),
        WgAddress = "10.0.0.2/32",
    };
    if (protocol == NodeProtocol.WireGuard)
        node.Password = Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray());
    await Test($"generated {protocol} slot {slot} validates", () => {
        var json = Build(node, sb, LocalPorts.SlotSocks(slot), LocalPorts.SlotHttp(slot), sb ? LocalPorts.SlotClashApi(slot) : LocalPorts.SlotStats(slot));
        var path = Save($"generated-{protocol}-{slot}", json);
        var check = KernelCompat.RunCheck(Exe(sb), sb ? KernelKind.SingBox : KernelKind.Xray, path, 15000);
        Check(check.Ok, check.Output); return Task.CompletedTask;
    });
}
foreach (var slot in new[] {0, 1})
await Test($"generated TUN slot {slot} validates without creating adapter", () => {
    var node = new NodeItem { Protocol = NodeProtocol.Trojan, Server = "127.0.0.1", Port = 9, Password = "synthetic", Security = "tls", Sni = "example.invalid" };
    var previous = SingBoxConfigBuilder.ClashApiPortOverride;
    string json;
    try { SingBoxConfigBuilder.ClashApiPortOverride = LocalPorts.SlotClashApi(slot);
        json = SingBoxConfigBuilder.BuildTun(node, LocalPorts.SlotSocks(slot), LocalPorts.SlotHttp(slot), "global", false, false, false, false, null, "error", "223.5.5.5"); }
    finally { SingBoxConfigBuilder.ClashApiPortOverride = previous; }
    var check = KernelCompat.RunCheck(Exe(true), KernelKind.SingBox, Save($"tun-{slot}", json), 15000);
    Check(check.Ok, check.Output); return Task.CompletedTask;
});
await Test("parallel sing-box builders preserve explicit controller ports", async () => {
    var node = new NodeItem { Protocol = NodeProtocol.Trojan, Server = "127.0.0.1", Port = 9, Password = "synthetic", Security = "tls" };
    await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(() => {
        var port = 31000 + i; var config = JsonNode.Parse(Build(node, true, 32000 + i, 33000 + i, port))!;
        Check(config["experimental"]?["clash_api"]?["external_controller"]?.GetValue<string>() == "127.0.0.1:" + port, "controller port crossed builds");
    })));
});
await Test("unsupported Xray insecure policy is routed or rejected explicitly", () => {
    var node = new NodeItem { Protocol = NodeProtocol.Trojan, Server = "127.0.0.1", Port = 9, Password = "synthetic", Security = "tls", AllowInsecure = true };
    Check(node.RequiresSingBox && KernelSupport.Of(node) == KernelSupportKind.SingBoxOnly, "insecure node still chooses removed Xray field");
    try { Build(node, false, 30001, 30002, 30003); throw new InvalidOperationException("unsupported insecure config was silently generated"); }
    catch (ArgumentException ex) { Check(ex.Message.Contains("allowInsecure"), "missing actionable TLS policy error"); }
    return Task.CompletedTask;
});

var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
await using var origin = new LoopbackServer(async (peer, ct) => {
    var stream = peer.GetStream(); var request = await LoopbackServer.ReadHeaders(stream, ct);
    const string body = "complete-response";
    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), ct);
    if (request.StartsWith("GET /slow ", StringComparison.Ordinal)) {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(body[..4]), ct); await releaseSlow.Task.WaitAsync(ct);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(body[4..]), ct);
    } else await stream.WriteAsync(Encoding.ASCII.GetBytes(body), ct);
});
await using var trojan = new TrojanFixture(origin.Port);
var sameNode = new NodeItem { Protocol = NodeProtocol.Trojan, Name = "loopback-trojan", Server = "127.0.0.1", Port = trojan.Port,
    Password = TrojanFixture.Password, Security = "tls", Sni = "localhost", AllowInsecure = true, CertSha256 = trojan.CertificatePin };
var ports = new HashSet<int>(); int NewPort() { int port; do { port = LoopbackServer.FreePort(); } while (!ports.Add(port)); return port; }
var xs = NewPort(); var xh = NewPort(); var xa = NewPort(); var ss = NewPort(); var sh = NewPort(); var sa = NewPort();
var xp = Save("live-isolated-xray", Build(sameNode, false, xs, xh, xa));
var sp = Save("live-isolated-singbox", Build(sameNode, true, ss, sh, sa));
// These route queries never dial public sites. Preserve the API rule and default proxy used by TCP fixtures.
var diagnosticConfig = JsonNode.Parse(File.ReadAllText(xp))!.AsObject();
var diagnosticRules = diagnosticConfig["routing"]!["rules"]!.AsArray();
diagnosticRules.Add(new JsonObject { ["type"] = "field", ["domain"] = new JsonArray("full:www.baidu.com"), ["outboundTag"] = "direct" });
diagnosticRules.Add(new JsonObject { ["type"] = "field", ["domain"] = new JsonArray("full:x.com"), ["outboundTag"] = "block" });
diagnosticRules.Add(new JsonObject { ["type"] = "field", ["domain"] = new JsonArray("full:www.google.com"), ["outboundTag"] = "proxy" });
File.WriteAllText(xp, diagnosticConfig.ToJsonString());
async Task<string> Get(int proxyPort, string scheme = "socks5")
{
    using var handler = new SocketsHttpHandler { Proxy = new WebProxy($"{scheme}://127.0.0.1:{proxyPort}"), UseProxy = true };
    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    return await http.GetStringAsync($"http://127.0.0.1:{origin.Port}/fast");
}
await using (var xray = new RunningKernel(Exe(false), xp, false))
await using (var singbox = new RunningKernel(Exe(true), sp, true))
{
    await Test("isolated kernels start on private loopback ports", async () => { await xray.Ready(xs); await singbox.Ready(ss); });
    await Test("idle Xray API is responsive before any proxy request", async () => {
        await XrayStatsClient.QueryAsync(xa);
        Check(XrayStats.IsResponsive(xa), "idle API confused with missing user counters");
    });
    await Test("real Xray routing API confirms domestic direct rule", async () => {
        var result = await XrayRouteClient.QueryAsync(xa, "http-in", new("https://www.baidu.com/"), 49152);
        Check(result.Outbound == "direct" && !result.UsesDefault, "actual router did not confirm direct");
    });
    await Test("real Xray routing API confirms explicit proxy and blocked rules", async () => {
        var proxy = await XrayRouteClient.QueryAsync(xa, "http-in", new("https://www.google.com/"), 49152);
        var block = await XrayRouteClient.QueryAsync(xa, "http-in", new("https://x.com/"), 49152);
        Check(proxy.Outbound == "proxy" && block.Outbound == "block", "router rule results fabricated/misdecoded");
    });
    await Test("real Xray no-clue response permits only the configured default", async () => {
        var result = await XrayRouteClient.QueryAsync(xa, "http-in", new("https://default.invalid/"), 49152);
        var context = KernelRouteContext.Load(new(1, "xray", xp, false, "global"));
        var route = await context.QueryXrayAsync(new("https://default.invalid/"), 49152, CancellationToken.None);
        Check(result.UsesDefault && route.Path == "代理" && route.Outbound == "proxy", "default route fallback lacks proof: " + JsonSerializer.Serialize(new { result, route }));
    });
    await Test("outbound classification uses protocol rather than misleading tag names", () => {
        var config = JsonNode.Parse(File.ReadAllText(xp))!;
        config["outbounds"] = new JsonArray(new JsonObject { ["tag"] = "proxy", ["protocol"] = "freedom" }, new JsonObject { ["tag"] = "direct", ["protocol"] = "trojan" });
        var file = Save("misleading-outbound-tags", config.ToJsonString());
        var context = KernelRouteContext.Load(new(1, "xray", file, false, "rule"));
        Check(context.Describe("proxy", "test", "").Path == "直连" && context.Describe("direct", "test", "").Path == "代理"
            && context.Describe("missing", "test", "").Path == "未确认", "routing inferred from tag names");
        return Task.CompletedTask;
    });
    await Test("Clash route evidence requires matching host and source port", () => {
        var context = KernelRouteContext.Load(new(1, "sing-box", sp, false, "global"));
        using var json = JsonDocument.Parse("""{"connections":[{"metadata":{"sourcePort":"49152","host":"fixture.invalid"},"chains":["direct"],"rule":"DomainSuffix","rulePayload":"fixture.invalid"}]}""");
        Check(context.FindClashConnection(json.RootElement, new("https://fixture.invalid/"), 49152)?.Path == "直连"
            && context.FindClashConnection(json.RootElement, new("https://other.invalid/"), 49152) == null
            && context.FindClashConnection(json.RootElement, new("https://fixture.invalid/"), 49153) == null, "unrelated connection was used as route proof");
        return Task.CompletedTask;
    });
    await Test("live sing-box connection list confirms actual proxy chain", async () => {
        using var timeout = new CancellationTokenSource(5000);
        using var peer = new TcpClient();
        await peer.ConnectAsync(IPAddress.Loopback, sh, timeout.Token);
        var stream = peer.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT fixture.invalid:{origin.Port} HTTP/1.1\r\nHost: fixture.invalid:{origin.Port}\r\n\r\n"), timeout.Token);
        Check((await LoopbackServer.ReadHeaders(stream, timeout.Token)).Contains("200"), "synthetic CONNECT failed");
        var context = KernelRouteContext.Load(new(1, "sing-box", sp, false, "global"));
        NetworkRouteEvidence? route = null;
        for (var i = 0; i < 10 && route == null; i++)
        { route = await context.ObserveClashAsync(new($"https://fixture.invalid:{origin.Port}/"), ((IPEndPoint)peer.Client.LocalEndPoint!).Port, timeout.Token); if (route == null) await Task.Delay(50, timeout.Token); }
        Check(route?.Path == "代理" && route.Source == "实际连接观测", "actual sing-box chain not observed");
    });
    await Test("same Trojan node forwards through Xray SOCKS and HTTP", async () => {
        var before = trojan.Tunnels; Check(await Get(xs) == "complete-response", "Xray SOCKS response mismatch");
        Check(await Get(xh, "http") == "complete-response", "Xray HTTP response mismatch");
        Check(trojan.Tunnels >= before + 2, "test bypassed Trojan node");
    });
    await Test("same Trojan node forwards through sing-box SOCKS and HTTP", async () => {
        var before = trojan.Tunnels; Check(await Get(ss) == "complete-response", "sing-box SOCKS response mismatch");
        Check(await Get(sh, "http") == "complete-response", "sing-box HTTP response mismatch");
        Check(trojan.Tunnels >= before + 2, "test bypassed Trojan node");
    });
    async Task<(string Json, double Ms, double CpuMs)> QueryCli()
    {
        var psi = new ProcessStartInfo(Exe(false)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] {"api", "statsquery", $"--server=127.0.0.1:{xa}"}) psi.ArgumentList.Add(arg);
        var timer = Stopwatch.StartNew(); using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch { if (!process.HasExited) process.Kill(); throw; }
        await Task.WhenAll(stdout, stderr); Check(process.ExitCode == 0, stderr.Result);
        return (stdout.Result, timer.Elapsed.TotalMilliseconds, process.TotalProcessorTime.TotalMilliseconds);
    }
    await Test("direct stats equal CLI counters without resetting traffic", async () => {
        var before = await XrayStatsClient.QueryAsync(xa);
        var cli = await QueryCli(); using var document = JsonDocument.Parse(cli.Json);
        var expected = document.RootElement.GetProperty("stat").EnumerateArray()
            .Where(s => (s.GetProperty("name").GetString() ?? "").StartsWith("outbound>>>proxy>>>traffic>>>", StringComparison.Ordinal))
            .ToDictionary(s => s.GetProperty("name").GetString()!, s => long.Parse(s.GetProperty("value").ToString()));
        var actual = before.Where(s => s.Name.StartsWith("outbound>>>proxy>>>traffic>>>", StringComparison.Ordinal)).ToDictionary(s => s.Name, s => s.Value);
        Check(expected.Count > 0 && expected.Count == actual.Count && expected.All(p => actual[p.Key] == p.Value), "gRPC and CLI disagree");
        var after = await XrayStatsClient.QueryAsync(xa);
        Check(actual.All(p => after.Single(s => s.Name == p.Key).Value == p.Value), "stats read reset traffic");
        var snapshot = XrayStats.Query(xa); Check(snapshot is { Uplink: > 0, Downlink: > 0 }, "app stats aggregation failed");
    });
    await Test("compare stats transport overhead on same isolated core", async () => {
        var cliRows = new List<double>(); var childCpu = new List<double>();
        for (var i = 0; i < 20; i++) { var row = await QueryCli(); cliRows.Add(row.Ms); childCpu.Add(row.CpuMs); }
        var grpcRows = new List<double>();
        for (var i = 0; i < 100; i++) { var timer = Stopwatch.StartNew(); await XrayStatsClient.QueryAsync(xa); grpcRows.Add(timer.Elapsed.TotalMilliseconds); }
        statsBenchmark = new {
            cliSamples = cliRows.Count, cliMeanMs = cliRows.Average(), cliChildMeanCpuMs = childCpu.Average(),
            grpcSamples = grpcRows.Count, grpcMeanMs = grpcRows.Average(), grpcQueryChildProcesses = 0,
            wallReductionPercent = (1 - grpcRows.Average() / cliRows.Average()) * 100,
            note = "Same isolated Xray, sequential warm queries. Short benchmark measures stats transport, not proxy throughput or long-term leak freedom.",
        };
    });
    await Test("cross-engine relay switch preserves old stream and routes new connections", async () => {
        using var timeout = new CancellationTokenSource(15000);
        var userSocks = NewPort(); var userHttp = NewPort();
        using var socksRelay = new PortRelay(userSocks, xs); using var httpRelay = new PortRelay(userHttp, xh);
        socksRelay.Start(); httpRelay.Start();
        using var old = new TcpClient(); await old.ConnectAsync(IPAddress.Loopback, userHttp, timeout.Token); var stream = old.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{origin.Port} HTTP/1.1\r\nHost: 127.0.0.1:{origin.Port}\r\n\r\n"), timeout.Token);
        Check((await LoopbackServer.ReadHeaders(stream, timeout.Token)).Contains("200"), "CONNECT failed");
        await stream.WriteAsync(Encoding.ASCII.GetBytes("GET /slow HTTP/1.1\r\nHost: localhost\r\n\r\n"), timeout.Token);
        await LoopbackServer.ReadHeaders(stream, timeout.Token); var prefix = await LoopbackServer.ReadBytes(stream, 4, timeout.Token);
        Check(Encoding.ASCII.GetString(prefix) == "comp" && httpRelay.ActiveFor(xh) == 1, "old stream did not use Xray backend");
        socksRelay.SwitchBackend(ss); httpRelay.SwitchBackend(sh);
        Check(await Get(userSocks) == "complete-response", "new connection not forwarded via sing-box backend");
        Check(httpRelay.ActiveFor(xh) == 1, "old connection dropped during switch");
        releaseSlow.TrySetResult(); var suffix = await LoopbackServer.ReadBytes(stream, 13, timeout.Token);
        Check(Encoding.ASCII.GetString(prefix.Concat(suffix).ToArray()) == "complete-response", "in-flight response truncated");
        socksRelay.SwitchBackend(xs); httpRelay.SwitchBackend(xh);
        Check(await Get(userSocks) == "complete-response", "switch back to Xray failed");
    });
}
releaseSlow.TrySetResult();
await Test("SOCKS UDP associations survive backend switch and close with control channel", async () => {
    using var timeout = new CancellationTokenSource(10000);
    await using var a = new SocksUdpFixture((byte)'A'); await using var b = new SocksUdpFixture((byte)'B');
    var port = NewPort(); using var relay = new PortRelay(port, a.Port); relay.Start();
    async Task<(TcpClient Control, UdpClient Udp, IPEndPoint Relay)> Associate()
    {
        var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); var local = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        var control = new TcpClient(); await control.ConnectAsync(IPAddress.Loopback, port, timeout.Token); var stream = control.GetStream();
        await stream.WriteAsync(new byte[] {5, 1, 0}, timeout.Token);
        Check((await LoopbackServer.ReadBytes(stream, 2, timeout.Token)).SequenceEqual(new byte[] {5, 0}), "UDP method negotiation failed");
        await stream.WriteAsync(new byte[] {5, 3, 0, 1, 127, 0, 0, 1, (byte)(local >> 8), (byte)local}, timeout.Token);
        var reply = await LoopbackServer.ReadBytes(stream, 10, timeout.Token);
        Check(reply[1] == 0, "UDP ASSOCIATE failed");
        return (control, udp, new IPEndPoint(new IPAddress(reply.AsSpan(4, 4)), reply[8] * 256 + reply[9]));
    }
    var old = await Associate(); using var oldControl = old.Control; using var oldUdp = old.Udp;
    var packet = new byte[] {0, 0, 0, 1, 127, 0, 0, 1, 0, 9, (byte)'x'};
    async Task CheckPacket(UdpClient udp, IPEndPoint endpoint, byte marker)
    {
        await udp.SendAsync(packet, endpoint, timeout.Token); var response = await udp.ReceiveAsync(timeout.Token);
        Check(response.Buffer.SequenceEqual(packet.Append(marker)), "datagram crossed backend/association");
    }
    await CheckPacket(oldUdp, old.Relay, (byte)'A'); relay.SwitchBackend(b.Port);
    await CheckPacket(oldUdp, old.Relay, (byte)'A');
    var current = await Associate(); using var newControl = current.Control; using var newUdp = current.Udp;
    await CheckPacket(newUdp, current.Relay, (byte)'B'); oldControl.Dispose();
    while (relay.ActiveFor(a.Port) != 0) await Task.Delay(10, timeout.Token);
    await oldUdp.SendAsync(packet, old.Relay, timeout.Token);
    using var noEcho = new CancellationTokenSource(150);
    try { await oldUdp.ReceiveAsync(noEcho.Token); throw new InvalidOperationException("closed UDP association was reused"); }
    catch (OperationCanceledException) when (noEcho.IsCancellationRequested) { }
    Check(relay.ActiveFor(b.Port) == 1, "closing old control affected current association");
});
File.WriteAllText(Path.Combine(output, "synthetic-trojan-errors.json"), JsonSerializer.Serialize(trojan.Errors));
var report = new { passed = failures == 0, total = results.Count, failures,
    boundary = "Synthetic credentials, loopback TLS Trojan server and HTTP origin only. No live proxy, registry, adapter, user session or installed file changed.", statsBenchmark, startupBenchmarks, results };
File.WriteAllText(Path.Combine(output, "kernel-check-results.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{results.Count - failures}/{results.Count} passed");
return failures == 0 ? 0 : 1;
