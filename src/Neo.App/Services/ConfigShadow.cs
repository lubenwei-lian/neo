using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Neo.Core;
using Neo.Core.Core;
using Neo.Core.Models;

namespace Neo.App.Services;

/// <summary>
/// M2 阶段 1「只读影子模式」：每次连接生成 sing-box 配置后，把同一份「节点 + 选项」交给
/// Rust 版 neo-cli（同目录 neo-cli.exe）再生成一份配置，做字段级 diff，只写日志，**不生效**。
///
/// 目的：用真实订阅/真实设置持续发现 C# 与 Rust 配置生成的漂移；失败不影响连接（全部 try/catch，
/// 后台任务 15 秒超时）。已知有意差异只允许一处：C# 用 socks-in + http-in，Rust 用 mixed-in
/// （见 neo-rust/docs/09 §2.2）。
///
/// 部署前提：把 neo-cli.exe 放到 app\runtime\ 目录（M2 阶段 1 手工放置；阶段 2 再进安装包）。
/// </summary>
public static class ConfigShadow
{
    private const string KnownDiffPrefix = "/inbounds";
    private static int _sequence;
    private static int _missingCliWarned;

    private static string ShadowDir => Path.Combine(NeoPaths.DataDir, "shadow");

    /// <summary>连接线程调用：立即返回，diff 在后台跑。</summary>
    public static void QueueIfAvailable(string csharpJson, NodeItem node, bool tun)
    {
        try
        {
            var cli = Path.Combine(NeoPaths.RuntimeDir, "neo-cli.exe");
            if (!File.Exists(cli))
            {
                if (Interlocked.Exchange(ref _missingCliWarned, 1) == 0)
                    LogBus.App("配置影子模式（M2 阶段1）：未找到 neo-cli.exe，跳过 Rust 对照（不影响连接）", "Debug");
                return;
            }
            var sequence = Interlocked.Increment(ref _sequence);
            _ = Task.Run(() => RunAsync(cli, csharpJson, node, tun, sequence));
        }
        catch (Exception ex)
        {
            TryLog("配置影子模式启动失败：" + ex.Message, "Debug");
        }
    }

    private static async Task RunAsync(string cliPath, string csharpJson, NodeItem node, bool tun, int sequence)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var inputPath = Path.Combine(ShadowDir, $"shadow-input-{stamp}-{sequence}.json");
        var rustPath = Path.Combine(ShadowDir, $"shadow-rust-{stamp}-{sequence}.json");
        try
        {
            Directory.CreateDirectory(ShadowDir);
            await File.WriteAllTextAsync(inputPath, BuildInput(node, tun), new UTF8Encoding(false));

            var psi = new ProcessStartInfo
            {
                FileName = cliPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("shadow");
            psi.ArgumentList.Add(inputPath);
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(rustPath);

            using var process = Process.Start(psi);
            if (process == null) return;
            var stderrTask = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                TryLog($"配置影子模式（{node.Name}）：neo-cli 超时（15s），已跳过", "Debug");
                return;
            }
            if (process.ExitCode != 0)
            {
                var stderr = (await stderrTask).Trim();
                TryLog($"配置影子模式（{node.Name}）：neo-cli 退出 {process.ExitCode}，已跳过（{Shorten(stderr, 160)}）", "Debug");
                return;
            }
            var rustJson = await File.ReadAllTextAsync(rustPath);
            var diffs = new List<string>();
            DiffJson(csharpJson, rustJson, diffs);
            var blocking = diffs.Where(d => !d.StartsWith(KnownDiffPrefix, StringComparison.Ordinal)).ToList();
            var known = diffs.Count - blocking.Count;

            if (blocking.Count == 0)
            {
                TryLog($"配置影子模式（M2 阶段1）：与 Rust 一致（字段差异 0；仅已知 inbounds 差异 {known} 项）· {node.Name} · TUN={(tun ? "开" : "关")}", "Debug");
            }
            else
            {
                TryLog($"配置影子模式（M2 阶段1）：发现 {blocking.Count} 项 C#/Rust 差异 · {node.Name} · 首项：{Shorten(blocking[0], 200)}", "Warning");
            }
            AppendReport(node, tun, blocking, known, diffs, sequence);
        }
        catch (Exception ex)
        {
            TryLog("配置影子模式执行异常（不影响连接）：" + ex.Message, "Debug");
        }
        finally
        {
            try { if (File.Exists(inputPath)) File.Delete(inputPath); } catch { }
            try { if (File.Exists(rustPath)) File.Delete(rustPath); } catch { }
        }
    }

    private static void AppendReport(NodeItem node, bool tun, List<string> blocking, int known, List<string> diffs, int sequence)
    {
        try
        {
            var line = new JsonObject
            {
                ["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["sequence"] = sequence,
                ["node"] = node.Name,
                ["protocol"] = node.Protocol.ToString(),
                ["tun"] = tun,
                ["blockingCount"] = blocking.Count,
                ["knownInboundsDiffs"] = known,
                ["blocking"] = new JsonArray(blocking.Take(20).Select(d => (JsonNode?)d).ToArray()),
                ["knownSample"] = new JsonArray(diffs.Where(d => d.StartsWith(KnownDiffPrefix, StringComparison.Ordinal)).Take(3).Select(d => (JsonNode?)d).ToArray()),
            };
            Directory.CreateDirectory(ShadowDir);
            var file = Path.Combine(ShadowDir, $"shadow-diff-{DateTime.Now:yyyyMMdd}.jsonl");
            File.AppendAllText(file, line.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) + Environment.NewLine, new UTF8Encoding(false));
        }
        catch { }
    }

    private static string BuildInput(NodeItem node, bool tun)
    {
        var state = AppState.State;
        var options = new JsonObject
        {
            ["tun"] = tun,
            ["mixedPort"] = LocalPorts.CoreSocks,
            ["allowLan"] = state.AllowLan,
            ["logLevel"] = state.KernelLogLevel,
            ["proxyMode"] = state.ProxyMode,
            ["bypassPrivate"] = state.BypassPrivate,
            ["bypassCn"] = state.BypassCn,
            ["blockAds"] = state.BlockAds,
            ["clashApiPort"] = SingBoxConfigBuilder.ClashApiPortOverride,
            ["clashApiSecret"] = SingBoxConfigBuilder.ClashApiSecret,
            ["dnsServer"] = state.DnsServer,
            ["ruleSetDir"] = NeoPaths.RuntimeDir,
            ["emitGroups"] = false,
            ["gameDirectProcesses"] = new JsonArray(((IEnumerable<string>)AppState.GameDirectProcessList()).Select(p => (JsonNode?)p).ToArray()),
        };
        var root = new JsonObject
        {
            ["node"] = NodeToJson(node),
            ["options"] = options,
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private static JsonObject NodeToJson(NodeItem n)
    {
        var o = new JsonObject
        {
            ["name"] = n.Name,
            ["protocol"] = ProtocolName(n.Protocol),
            ["server"] = n.Server,
            ["port"] = n.Port,
        };
        void Set(string key, string? value) { if (!string.IsNullOrEmpty(value)) o[key] = value; }
        Set("uuid", n.Uuid);
        Set("password", n.Password);
        Set("method", n.Method);
        Set("username", n.Username);
        if (n.AlterId > 0) o["alterId"] = n.AlterId;
        Set("cipher", n.Cipher);
        Set("flow", n.Flow);
        o["network"] = n.Network;
        Set("path", n.Path);
        Set("hostHeader", n.HostHeader);
        Set("serviceName", n.ServiceName);
        Set("headerType", n.HeaderType);
        Set("grpcAuthority", n.GrpcAuthority);
        Set("grpcMode", n.GrpcMode);
        Set("xhttpMode", n.XhttpMode);
        Set("xhttpExtra", n.XhttpExtra);
        Set("kcpSeed", n.KcpSeed);
        if (n.KcpMtu.HasValue) o["kcpMtu"] = n.KcpMtu.Value;
        o["security"] = n.Security;
        Set("sni", n.Sni);
        Set("fingerprint", n.Fingerprint);
        Set("publicKey", n.PublicKey);
        Set("shortId", n.ShortId);
        Set("spiderX", n.SpiderX);
        if (n.AllowInsecure) o["allowInsecure"] = true;
        Set("vlessEncryption", n.VlessEncryption);
        Set("echConfigList", n.EchConfigList);
        Set("verifyPeerCertByName", n.VerifyPeerCertByName);
        Set("plugin", n.Plugin);
        Set("pluginOpts", n.PluginOpts);
        if (n.GeckoMinPacketSize.HasValue) o["geckoMinPacketSize"] = n.GeckoMinPacketSize.Value;
        if (n.GeckoMaxPacketSize.HasValue) o["geckoMaxPacketSize"] = n.GeckoMaxPacketSize.Value;
        if (n.Extras.Count > 0)
        {
            var extras = new JsonObject();
            foreach (var kv in n.Extras) extras[kv.Key] = kv.Value;
            o["extras"] = extras;
        }
        return o;
    }

    private static string ProtocolName(NodeProtocol protocol) => protocol switch
    {
        NodeProtocol.VMess => "vmess",
        NodeProtocol.VLESS => "vless",
        NodeProtocol.Trojan => "trojan",
        NodeProtocol.Shadowsocks => "ss",
        NodeProtocol.Hysteria2 => "hysteria2",
        NodeProtocol.TUIC => "tuic",
        NodeProtocol.AnyTls => "anytls",
        NodeProtocol.Socks => "socks",
        NodeProtocol.WireGuard => "wireguard",
        NodeProtocol.Naive => "naive",
        _ => "unknown",
    };

    private static void DiffJson(string csharpJson, string rustJson, List<string> diffs)
    {
        JsonNode? a;
        JsonNode? b;
        try
        {
            a = JsonNode.Parse(csharpJson);
            b = JsonNode.Parse(rustJson);
        }
        catch (Exception ex)
        {
            diffs.Add("/<json-parse>: " + ex.Message);
            return;
        }
        DiffNode(a, b, "", diffs);
    }

    private static void DiffNode(JsonNode? a, JsonNode? b, string path, List<string> diffs)
    {
        if (a is JsonObject ao && b is JsonObject bo)
        {
            foreach (var kv in ao)
            {
                if (bo.TryGetPropertyValue(kv.Key, out var bv)) DiffNode(kv.Value, bv, path + "/" + kv.Key, diffs);
                else diffs.Add($"{path}/{kv.Key}: 仅 C# 有");
            }
            foreach (var kv in bo)
                if (!ao.ContainsKey(kv.Key)) diffs.Add($"{path}/{kv.Key}: 仅 Rust 有");
            return;
        }
        if (a is JsonArray aa && b is JsonArray ba)
        {
            if (aa.Count != ba.Count)
            {
                diffs.Add($"{path}: 数组长度 {aa.Count}(C#) != {ba.Count}(Rust)");
                return;
            }
            for (var i = 0; i < aa.Count; i++) DiffNode(aa[i], ba[i], $"{path}[{i}]", diffs);
            return;
        }
        var sa = a?.ToJsonString() ?? "null";
        var sb = b?.ToJsonString() ?? "null";
        if (!string.Equals(sa, sb, StringComparison.Ordinal))
            diffs.Add($"{path}: {Shorten(sa, 120)} != {Shorten(sb, 120)}");
    }

    private static string Shorten(string value, int max)
        => string.IsNullOrEmpty(value) ? "" : (value.Length <= max ? value : value[..max] + "…");

    private static void TryLog(string message, string level)
    {
        try { LogBus.App(message, level); } catch { }
    }
}
