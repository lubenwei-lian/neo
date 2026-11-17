using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Neo.Core;
using Neo.Core.Core;
using Neo.Core.Models;
using Neo.Core.Parsing;

if (args.Length != 2) throw new ArgumentException("Usage: <runtime> <isolated output>");
var runtime = Path.GetFullPath(args[0]); var output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
foreach (var (field, value) in new[] { ("_runtimeDir", runtime), ("_rootDir", output), ("_dataDir", Path.Combine(output, "data")) })
    typeof(NeoPaths).GetField(field, BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, value);
Directory.CreateDirectory(NeoPaths.DataDir);
var results = new List<object>(); var failed = 0;
void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
async Task Test(string name, Func<Task> action)
{
    try { await action(); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failed++; results.Add(new { name, passed = false, error = e.ToString() }); Console.WriteLine("FAIL " + name + ": " + e.Message); }
    File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { failed, results }, new JsonSerializerOptions { WriteIndented = true }));
}
Task Check(Action check) { check(); return Task.CompletedTask; }
string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
string Ssr(string host = "127.0.0.1", int port = 443) => "ssr://" + B64($"{host}:{port}:origin:aes-128-cfb:plain:{B64("fixture-password")}/?remarks={B64("日本测试 🌙")}&protoparam={B64("12:test")}&obfsparam={B64("test.invalid")}");
NodeItem Parse(string s) => ShareLinkParser.Parse(s) ?? throw new Exception("parse failed");
string Save(string name, string text) { var p = Path.Combine(output, name + ".json"); File.WriteAllText(p, text); return p; }
async Task Validate(string engine, string path)
{
    var psi = new ProcessStartInfo(Path.Combine(runtime, engine + ".exe")) { WorkingDirectory = runtime, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var a in engine == "sing-box" ? new[] { "check", "-c", path } : engine == "mihomo" ? new[] { "-t", "-f", path, "-d", output } : new[] { "run", "-test", "-c", path }) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    var stdout = p.StandardOutput.ReadToEndAsync(); var stderr = p.StandardError.ReadToEndAsync();
    try { await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
    catch { p.Kill(true); throw; }
    Require(p.ExitCode == 0, engine + " check failed: " + await stdout + await stderr);
}
var ssr = Parse(Ssr());
await Test("SSR URL-safe base64 and unicode remarks", () => Check(() => Require(ssr.Protocol == NodeProtocol.ShadowsocksR && ssr.Name == "日本测试 🌙" && ssr.Password == "fixture-password", "SSR fields lost")));
await Test("SSR protocol and obfuscation parameters", () => Check(() => Require(ssr.Extras["ssr_protocol_param"] == "12:test" && ssr.Extras["ssr_obfs_param"] == "test.invalid", "SSR parameters lost")));
await Test("SSR IPv6 address", () => Check(() => Require(Parse(Ssr("2001:db8::1")).Server == "2001:db8::1", "IPv6 authority split incorrectly")));
await Test("SSR invalid port and malformed base64 rejected", () => Check(() => Require(ShareLinkParser.Parse(Ssr(port: 65536)) == null && ShareLinkParser.Parse("ssr://%%%") == null, "invalid SSR accepted")));
await Test("SSR password and options affect configuration identity", () => Check(() => { var b = Parse(Ssr()); b.Extras["ssr_protocol_param"] = "other"; Require(NodeIdentity.Fingerprint(ssr) != NodeIdentity.Fingerprint(b), "SSR options absent from identity"); }));
await Test("SSR mixed link and base64 subscriptions", () => Check(() => { var text = Ssr() + "\nsocks5://user:pass@127.0.0.1:8888#local"; Require(SubscriptionFetcher.ParseContent(text).Nodes.Count == 2 && SubscriptionFetcher.ParseContent(B64(text)).Nodes.Count == 2, "mixed subscriptions incomplete"); }));
var yaml = "proxies:\n  - name: SSR fixture\n    type: ssr\n    server: 127.0.0.1\n    port: 443\n    cipher: aes-128-cfb\n    password: fixture-password\n    protocol: origin\n    obfs: plain\n    protocol-param: '12:test'\n    obfs-param: test.invalid\n";
await Test("Clash SSR parser preserves options", () => Check(() => { var n = ClashYamlParser.Parse(yaml).Single(); Require(n.Protocol == NodeProtocol.ShadowsocksR && n.Extras["ssr_protocol_param"] == "12:test", "Clash SSR fields lost"); }));
await Test("Hysteria v1 is not silently interpreted as v2", () => Check(() => Require(ClashYamlParser.Parse(yaml.Replace("type: ssr", "type: hysteria")).Count == 0, "Hysteria1 coerced to incompatible Hysteria2")));
var naive = Parse("naive+https://user:p%40ss@[2001:db8::1]:443#Naive");
var quic = Parse("naive+quic://user:p%40ss@127.0.0.1:443#QUIC");
await Test("Naive HTTPS IPv6 and escaped authentication", () => Check(() => Require(JsonNode.Parse(NaiveConfigBuilder.Build(naive, 40001))!["proxy"]!.GetValue<string>() == "https://user:p%40ss@[2001:db8::1]:443", "Naive URL corrupted")));
await Test("Naive QUIC configuration and UDP classification", () => Check(() => Require(JsonNode.Parse(NaiveConfigBuilder.Build(quic, 40002))!["proxy"]!.GetValue<string>().StartsWith("quic://") && DelayTestPlanner.TransportOf(quic) == DelayTransport.Udp && !DelayTestPlanner.HasTcpPort(quic), "QUIC misclassified as TCP")));
await Test("Naive HTTPS identity remains compatible", () => Check(() => Require(!naive.Extras.ContainsKey("naive_transport"), "legacy Naive fingerprints unnecessarily changed")));
await Test("Naive HTTPS and QUIC do not share cache identity", () => Check(() => { var https = Parse("naive+https://user:p%40ss@127.0.0.1:443#QUIC"); Require(NodeIdentity.Fingerprint(https) != NodeIdentity.Fingerprint(quic), "different transport shares identity"); }));
var local = Path.Combine(output, "local subscription.txt"); File.WriteAllText(local, Ssr());
await Test("local source accepts absolute path and file URI", () => Check(() => Require(SubscriptionSource.Normalize(local) == local && SubscriptionSource.Normalize(new Uri(local).AbsoluteUri) == local, "local normalization incorrect")));
await Test("local source rejects missing files and arbitrary schemes", () => Check(() => { foreach (var s in new[] { "relative.txt", "ftp://example.invalid/x", Path.Combine(output, "missing.txt") }) { var rejected = false; try { SubscriptionSource.Normalize(s); } catch { rejected = true; } Require(rejected, "invalid source accepted"); } }));
await Test("local subscription fetch and refresh read new file content", async () => { Require((await SubscriptionFetcher.FetchAsync(local)).Nodes.Single().Protocol == NodeProtocol.ShadowsocksR, "first local fetch failed"); await File.WriteAllTextAsync(local, "socks5://a:b@127.0.0.1:12345"); Require((await SubscriptionFetcher.FetchAsync(local)).Nodes.Single().Port == 12345, "file refresh reused old content"); });
await Test("oversized local subscriptions rejected before parse", async () => { var p = Path.Combine(output, "large.txt"); using (var f = File.Create(p)) f.SetLength(SubscriptionSource.MaxImportBytes + 1); Require(!(await SubscriptionFetcher.FetchAsync(p)).Success, "size limit ignored"); File.Delete(p); });
await Test("local read cancellation propagates", async () => { using var c = new CancellationTokenSource(); c.Cancel(); var canceled = false; try { await SubscriptionSource.ReadLocalAsync(local, c.Token); } catch (OperationCanceledException) { canceled = true; } Require(canceled, "read cancellation swallowed"); });
await Test("reserved auxiliary ports cannot collide with user ports", () => Check(() => Require(new[] { LocalPorts.NaiveListen, LocalPorts.AuxiliaryHttp, LocalPorts.AuxiliaryApi }.All(p => LocalPorts.Validate(p, 42000) != null), "reserved port accepted")));
await Test("SSR native mihomo configuration check", async () => await Validate("mihomo", Save("ssr", MihomoNodeConfigBuilder.Build(ssr, Ports.Free()))));
foreach (var n in new[] { naive, quic, ssr })
await Test(n.Protocol + " " + (n == quic ? "QUIC " : "") + "auxiliary listener readiness and cleanup", async () => {
    var port = Ports.Free(); var plan = AuxiliaryCorePlan.Create(n, port, Ports.Free(), Ports.Free()); var p = Path.Combine(Path.GetTempPath(), "neo-realtest-aux-" + Guid.NewGuid().ToString("N") + ".json"); File.WriteAllText(p, plan.Config);
    var tail = new List<string>(); int pid; using (var core = new CoreProcess(Path.Combine(runtime, plan.Engine + ".exe"), runtime)) { core.LogLine += s => { lock(tail)tail.Add(s); }; core.Start(p, plan.Arguments(p, output)); pid = core.Pid; try { Require(await ProxyReadiness.WaitAsync(() => core.IsRunning, port, port, TimeSpan.FromSeconds(5), negotiateSocks: plan.Engine != "naive"), "SOCKS not ready: " + string.Join(" | ", tail)); Require(core.StopAndVerify(), "core did not stop"); } finally { core.StopAndVerify(); File.Delete(p); } }
    Require(Ports.IsFree(port) && !Process.GetProcesses().Any(x => { using (x) return x.Id == pid; }), "auxiliary leaked");
});
var exclusive = Parse("vless://11111111-1111-1111-1111-111111111111@127.0.0.1:443?security=none&type=xhttp&mode=auto#xhttp");
await Test("Xray exclusive TUN keeps its transport in auxiliary config", async () => { var plan = AuxiliaryCorePlan.Create(exclusive, Ports.Free(), Ports.Free(), Ports.Free()); Require(AuxiliaryCorePlan.Needed(exclusive, true) && plan.Engine == "xray" && plan.Config.Contains("xhttp"), "exclusive transport downgraded"); await Validate(plan.Engine, Save("xhttp-aux", plan.Config)); });
await Test("TUN frontend exempts only the exact auxiliary executable", async () => {
    var exe = Path.Combine(runtime, "xray.exe"); var front = SingBoxConfigBuilder.BuildTun(AuxiliaryCorePlan.FrontNode(Ports.Free(), "xhttp"), Ports.Free(), Ports.Free(), "global", false, false, false, false, null, "error", "223.5.5.5");
    var config = AuxiliaryCorePlan.ProtectTunTransport(front, exe); var r = JsonNode.Parse(config)!["route"]!["rules"]![0]!;
    Require(r["process_path"]![0]!.GetValue<string>() == Path.GetFullPath(exe) && r["outbound"]!.GetValue<string>() == "direct" && r["process_name"] == null, "overbroad or absent transport protection"); await Validate("sing-box", Save("xhttp-tun", config));
});
await Test("custom Xray TUN preserves original outbounds and routing", async () => { var path = Save("custom-source", "{\"outbounds\":[{\"protocol\":\"freedom\",\"tag\":\"keep-me\"}],\"routing\":{\"rules\":[{\"type\":\"field\",\"domain\":[\"example.invalid\"],\"outboundTag\":\"keep-me\"}]}}"); var n = new NodeItem { Protocol = NodeProtocol.CustomConfig }; n.Extras["config_path"] = path; n.Extras["engine"] = "xray"; var plan = AuxiliaryCorePlan.Create(n, Ports.Free(), Ports.Free(), Ports.Free()); Require(plan.OwnsRouting && plan.Config.Contains("keep-me") && plan.Config.Contains("example.invalid"), "custom routing replaced"); await Validate(plan.Engine, Save("custom-aux", plan.Config)); });
await Test("Clash TUN has only one owner and no orphaned overridden fields", async () => { var path = Path.Combine(output, "clash-source.yaml"); File.WriteAllText(path, "tun:\n  enable: true\n  auto-route: true\nlisteners:\n  - name: original\n    type: socks\n    port: 40000\nexternal-controller-tls: 0.0.0.0:9091\ngeo-auto-update: true\nproxies: []\nrules:\n  - MATCH,DIRECT\n"); var n = new NodeItem { Protocol = NodeProtocol.MihomoConfig }; n.Extras["yaml_path"] = path; var plan = AuxiliaryCorePlan.Create(n, Ports.Free(), Ports.Free(), Ports.Free()); Require(plan.OwnsRouting && !plan.Config.Contains("enable: true") && !plan.Config.Contains("auto-route: true") && !plan.Config.Contains("original"), "two TUN owners or leftover child keys"); await Validate("mihomo", Save("clash-aux", plan.Config)); });
var batchNodes = new[] { Parse("hysteria2://pass@127.0.0.1:443#h2"), Parse("tuic://11111111-1111-1111-1111-111111111111:pass@127.0.0.1:443#tuic"), Parse("anytls://pass@127.0.0.1:443#anytls"), new NodeItem { Protocol = NodeProtocol.WireGuard, Name = "WG", Server = "127.0.0.1", Port = 51820, Password = Convert.ToBase64String(new byte[32]), WgPublicKey = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray()), WgAddress = "10.0.0.2/32" } };
var batchPorts = Enumerable.Range(0, batchNodes.Length).Select(_ => Ports.Free()).ToArray();
await Test("sing-box batch maps every ingress to its own outbound or WireGuard endpoint", async () => { var config = SingBoxConfigBuilder.BuildTestBatch(batchNodes, batchPorts); var root = JsonNode.Parse(config)!; var rules = root["route"]!["rules"]!.AsArray(); for (var i = 0; i < batchNodes.Length; i++) Require(rules[i]!["inbound"]![0]!.GetValue<string>() == "in" + i && rules[i]!["outbound"]!.GetValue<string>() == "out" + i, "incorrect batch mapping"); Require(rules.Last()!["action"]!.GetValue<string>() == "reject" && root["endpoints"]![0]!["tag"]!.GetValue<string>() == "out3", "unmatched batch traffic can bypass"); await Validate("sing-box", Save("batch-all", config)); });
await Test("batch invalid and duplicate ports rejected", () => Check(() => { var rejected = false; try { SingBoxConfigBuilder.BuildTestBatch(batchNodes, new[] { 40000, 40000, 40000, 40000 }); } catch (ArgumentException) { rejected = true; } Require(rejected, "duplicate listeners accepted"); }));
await Test("SSR and Naive can run per-node exit checks without live selection", () => Check(() => Require(new[] { ssr, naive, quic }.All(RealDelayTester.CanDetectExitLocation), "new protocols excluded from exit probes")));
await Test("sing-box batch traffic remains isolated per node", async () => {
    await using var a = new SocksFixture("A"); await using var b = new SocksFixture("B");
    var nodes = new[] { new NodeItem { Protocol = NodeProtocol.Socks, Server = "127.0.0.1", Port = a.Port }, new NodeItem { Protocol = NodeProtocol.Socks, Server = "127.0.0.1", Port = b.Port } };
    var ports = new[] { Ports.Free(), Ports.Free() }; var path = Save("batch-routing", SingBoxConfigBuilder.BuildTestBatch(nodes, ports)); using var core = new CoreProcess(Path.Combine(runtime, "sing-box.exe"), runtime); core.Start(path);
    Require(await ProxyReadiness.WaitAsync(() => core.IsRunning, ports[0], ports[1], TimeSpan.FromSeconds(5)), "batch startup failed");
    for (var i = 0; i < 2; i++) { using var client = new HttpClient(new SocketsHttpHandler { Proxy = new WebProxy("socks5://127.0.0.1:" + ports[i]), UseProxy = true }) { Timeout = TimeSpan.FromSeconds(4) }; Require(await client.GetStringAsync("http://1.1.1.1/") == (i == 0 ? "A" : "B"), "one ingress used another node or direct network"); }
});
await Test("SSR native encrypted loopback forwarding through sing-box frontend", async () => {
    await using var server = new SsrFixture("fixture-password"); var n = Parse(Ssr(port: server.Port));
    var auxiliaryPort = Ports.Free(); var plan = AuxiliaryCorePlan.Create(n, auxiliaryPort, Ports.Free(), Ports.Free()); var auxPath = Save("ssr-loopback", plan.Config);
    using var aux = new CoreProcess(Path.Combine(runtime, "mihomo.exe"), output); aux.Start(auxPath, plan.Arguments(auxPath, output)); Require(await ProxyReadiness.WaitAsync(() => aux.IsRunning, auxiliaryPort, auxiliaryPort, TimeSpan.FromSeconds(5)), "SSR startup failed");
    var socks = Ports.Free(); var http = Ports.Free(); var config = SingBoxConfigBuilder.Build(AuxiliaryCorePlan.FrontNode(auxiliaryPort, "SSR"), socks, http, "global", false, false, false, false, null, "error", "223.5.5.5", clashApiPort: Ports.Free());
    using var front = new CoreProcess(Path.Combine(runtime, "sing-box.exe"), runtime); front.Start(Save("ssr-front", config)); Require(await ProxyReadiness.WaitAsync(() => front.IsRunning, socks, http, TimeSpan.FromSeconds(5)), "SSR frontend startup failed");
    using var client = new HttpClient(new SocketsHttpHandler { Proxy = new WebProxy("http://127.0.0.1:" + http), UseProxy = true }) { Timeout = TimeSpan.FromSeconds(5) }; Require(await client.GetStringAsync("http://1.1.1.1/") == "SSR-OK", "SSR encrypted request failed");
});
await Test("SSR production real-delay path uses its own encrypted proxy", async () => { await using var server = new SsrFixture("fixture-password"); var n = Parse(Ssr(port: server.Port)); Require(await RealDelayTester.ProbeUrlAsync(n, runtime, "http://1.1.1.1/", 5000) > 0, "SSR real-delay path failed"); });
await Test("Xray XHTTP auxiliary and sing-box TUN frontend forward actual loopback traffic", async () => {
    await using var target = new Server(async (s,c) => { var request = new List<byte>(); while(request.Count < 16384) { var b = await Server.Read(s,1,c); request.Add(b[0]); if(request.Count>=4 && Encoding.ASCII.GetString(request.TakeLast(4).ToArray())=="\r\n\r\n")break; } File.AppendAllText(Path.Combine(output,"xhttp-target.log"),Encoding.ASCII.GetString(request.ToArray())+"\n"); await s.WriteAsync(Server.Reply("XHTTP-OK"),c); });
    var transportPort = Ports.Free(); var n = Parse($"vless://11111111-1111-1111-1111-111111111111@127.0.0.1:{transportPort}?type=xhttp&path=%2Faudit&mode=auto");
    var serverJson = new JsonObject { ["log"] = new JsonObject { ["loglevel"] = "error" }, ["inbounds"] = new JsonArray(new JsonObject { ["listen"] = "127.0.0.1", ["port"] = transportPort, ["protocol"] = "vless", ["settings"] = new JsonObject { ["clients"] = new JsonArray(new JsonObject { ["id"] = n.Uuid }), ["decryption"] = "none" }, ["streamSettings"] = new JsonObject { ["network"] = "xhttp", ["xhttpSettings"] = new JsonObject { ["path"] = "/audit", ["mode"] = "auto" } } }), ["outbounds"] = new JsonArray(new JsonObject { ["protocol"] = "freedom" }) };
    serverJson["log"]!["loglevel"] = "debug";
    // Xray 26 blocks private targets on server inbounds by default. Permit only this fixture endpoint.
    serverJson["outbounds"]![0]!["settings"] = new JsonObject { ["finalRules"] = new JsonArray(new JsonObject { ["action"] = "allow", ["ip"] = new JsonArray("127.0.0.1/32"), ["port"] = target.Port.ToString() }) };
    using var server = new CoreProcess(Path.Combine(runtime,"xray.exe"),runtime); server.LogLine += s => File.AppendAllText(Path.Combine(output,"xhttp-server.log"),s+"\n"); server.Start(Save("xhttp-server",serverJson.ToJsonString()));
    var auxiliaryPort = Ports.Free(); var plan = AuxiliaryCorePlan.Create(n,auxiliaryPort,Ports.Free(),Ports.Free()); using var aux = new CoreProcess(Path.Combine(runtime,"xray.exe"),runtime); aux.LogLine += s => File.AppendAllText(Path.Combine(output,"xhttp-client.log"),s+"\n"); var debugClient = JsonNode.Parse(plan.Config)!; debugClient["log"]!["loglevel"]="debug"; aux.Start(Save("xhttp-client",debugClient.ToJsonString()));
    Require(await ProxyReadiness.WaitAsync(() => aux.IsRunning && server.IsRunning,auxiliaryPort,auxiliaryPort,TimeSpan.FromSeconds(5)),"XHTTP transport startup failed");
    using(var directClient = new HttpClient(new SocketsHttpHandler { Proxy=new WebProxy("socks5://127.0.0.1:"+auxiliaryPort),UseProxy=true }) {Timeout=TimeSpan.FromSeconds(5)})Require(await directClient.GetStringAsync("http://127.0.0.1:"+target.Port+"/")=="XHTTP-OK","XHTTP direct auxiliary forwarding failed");
    var socks = Ports.Free(); var http = Ports.Free(); var original = SingBoxConfigBuilder.BuildTun(AuxiliaryCorePlan.FrontNode(auxiliaryPort,"XHTTP"),socks,http,"global",false,false,false,false,null,"error","223.5.5.5");
    var root = JsonNode.Parse(AuxiliaryCorePlan.ProtectTunTransport(original,Path.Combine(runtime,"xray.exe")))!; root["log"]!["level"] = "debug"; var tun = root["inbounds"]![0]!; var adapter = "neo-audit-" + Environment.ProcessId; tun["interface_name"] = adapter; tun["address"] = new JsonArray("172.31.255.1/30"); tun["auto_route"] = false; tun["strict_route"] = false;
    root["experimental"]!["clash_api"]!["external_controller"] = "127.0.0.1:" + Ports.Free();
    using (var front = new CoreProcess(Path.Combine(runtime,"sing-box.exe"),runtime)) {
        var tail = new List<string>(); front.LogLine += s => { lock(tail)tail.Add(s); File.AppendAllText(Path.Combine(output,"xhttp-front.log"),s+"\n"); }; front.Start(Save("xhttp-isolated-tun",root.ToJsonString()));
        Require(await ProxyReadiness.WaitAsync(() => front.IsRunning,socks,http,TimeSpan.FromSeconds(8)),"isolated TUN frontend failed: "+string.Join(" | ",tail));
        Require(System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Any(i=>i.Name==adapter),"owned isolated TUN adapter missing");
        using var client = new HttpClient(new SocketsHttpHandler { Proxy = new WebProxy("http://127.0.0.1:"+http),UseProxy=true }) {Timeout=TimeSpan.FromSeconds(5)};
        Require(await client.GetStringAsync("http://127.0.0.1:"+target.Port+"/")=="XHTTP-OK","TUN-configured frontend did not retain Xray transport");
        Require(front.StopAndVerify(),"TUN frontend failed to stop");
    }
    for(var i=0;i<100 && System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Any(n=>n.Name==adapter);i++)await Task.Delay(100);
    var remaining = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.Name==adapter).ToArray();
    File.WriteAllText(Path.Combine(output,"isolated-tun-cleanup.json"),JsonSerializer.Serialize(new { adapter, remaining = remaining.Select(i=>new { i.Name, status=i.OperationalStatus.ToString() }) }));
    Require(remaining.All(n=>n.OperationalStatus!=System.Net.NetworkInformation.OperationalStatus.Up),"isolated TUN adapter remains active after exit");
});
Console.WriteLine($"RESULT {results.Count - failed}/{results.Count}"); return failed == 0 ? 0 : 1;

static class Ports
{
    public static int Free() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
    public static bool IsFree(int port) { try { var l = new TcpListener(IPAddress.Loopback, port); l.Start(); l.Stop(); return true; } catch { return false; } }
}
class Server : IAsyncDisposable
{
    readonly TcpListener listener = new(IPAddress.Loopback, 0); readonly CancellationTokenSource stop = new(); readonly List<Task> peers = new(); readonly Task accept;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public Server(Func<NetworkStream, CancellationToken, Task> handle) { listener.Start(); accept = Accept(handle); }
    async Task Accept(Func<NetworkStream, CancellationToken, Task> handle) { try { while (true) { var c = await listener.AcceptTcpClientAsync(stop.Token); var task = Task.Run(async () => { using (c) { try { await handle(c.GetStream(), stop.Token); } catch (Exception e) when (e is IOException or OperationCanceledException or SocketException) { } } }); peers.Add(task); } } catch (OperationCanceledException) { } }
    public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); await accept; await Task.WhenAll(peers); stop.Dispose(); }
    public static async Task<byte[]> Read(Stream s, int count, CancellationToken c) { var b = new byte[count]; await s.ReadExactlyAsync(b, c); return b; }
    public static byte[] Reply(string body) => Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");
}
sealed class SocksFixture(string marker) : IAsyncDisposable
{
    readonly Server server = new(async (s, c) => { await Server.Read(s, 3, c); await s.WriteAsync(new byte[] { 5, 0 }, c); var head = await Server.Read(s, 4, c); var count = head[3] == 1 ? 4 : head[3] == 4 ? 16 : (await Server.Read(s, 1, c))[0]; await Server.Read(s, count + 2, c); await s.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }, c); var request = new byte[4096]; await s.ReadAsync(request, c); await s.WriteAsync(Server.Reply(marker), c); });
    public int Port => server.Port; public ValueTask DisposeAsync() => server.DisposeAsync();
}
sealed class SsrFixture : IAsyncDisposable
{
    readonly Server server;
    public SsrFixture(string password) { server = new(async (s, c) => { var key = MD5.HashData(Encoding.UTF8.GetBytes(password)); var iv = await Server.Read(s, 16, c); using var decrypt = new Cfb(key, iv); var buf = new byte[8192]; using var plain = new MemoryStream(); while (true) { var count = await s.ReadAsync(buf, c); if (count == 0) return; plain.Write(decrypt.Transform(buf.AsSpan(0, count), false)); if (Encoding.ASCII.GetString(plain.GetBuffer(), 0, (int)plain.Length).Contains("\r\n\r\n")) break; } var responseIv = RandomNumberGenerator.GetBytes(16); await s.WriteAsync(responseIv, c); using var encrypt = new Cfb(key, responseIv); await s.WriteAsync(encrypt.Transform(Server.Reply("SSR-OK"), true), c); }); }
    public int Port => server.Port; public ValueTask DisposeAsync() => server.DisposeAsync();
}
sealed class Cfb : IDisposable
{
    readonly Aes aes = Aes.Create(); readonly byte[] feedback; readonly byte[] keyStream = new byte[16]; int offset;
    public Cfb(byte[] key, byte[] iv) { aes.Key = key; feedback = iv.ToArray(); }
    public byte[] Transform(ReadOnlySpan<byte> input, bool encrypt) { var result = new byte[input.Length]; for (var i = 0; i < input.Length; i++) { if (offset == 0) aes.EncryptEcb(feedback, keyStream, PaddingMode.None); var value = (byte)(input[i] ^ keyStream[offset]); feedback[offset] = encrypt ? value : input[i]; result[i] = value; offset = (offset + 1) % 16; } return result; }
    public void Dispose() => aes.Dispose();
}
