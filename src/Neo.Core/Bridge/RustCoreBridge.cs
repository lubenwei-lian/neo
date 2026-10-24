using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Neo.Core.Models;

namespace Neo.Core.Bridge;

public sealed record RustPlaceholderResult(bool Parsed, bool Placeholder, string? Reason);

public sealed record RustFailoverResult(string Decision, int Fails, long? LastSwitchMs);

public sealed record RustParseResult(List<NodeItem> Nodes, string? TrafficInfo);

/// <summary>
/// M2 阶段 3：桌面版调用 Rust 纯逻辑的桥（CLI JSON 协议，失败一律返回 null 让调用方回退 C#）。
///
/// 安全边界：
///   1. 只调用 app\runtime\neo-cli.exe（可用环境变量 NEO_RUST_CLI 覆盖，便于测试）；
///   2. 单次超时 3 秒；退出码非 0 / 输出为空 / JSON 解析失败 → null；
///   3. 输入输出走临时文件（data\rustcore），用完即删。
/// </summary>
public static class RustCoreBridge
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public static string CliPath
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("NEO_RUST_CLI");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            return Path.Combine(NeoPaths.RuntimeDir, "neo-cli.exe");
        }
    }

    private static string? Run(string command, string inputPayload)
    {
        string? input = null;
        try
        {
            var cli = CliPath;
            if (!File.Exists(cli)) return null;
            var dir = Path.Combine(NeoPaths.DataDir, "rustcore");
            Directory.CreateDirectory(dir);
            input = Path.Combine(dir,
                $"in-{DateTime.Now:yyyyMMdd-HHmmssfff}-{Environment.ProcessId}-{Guid.NewGuid():N}.json");
            File.WriteAllText(input, inputPayload, new UTF8Encoding(false));

            var psi = new ProcessStartInfo
            {
                FileName = cli,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(command);
            psi.ArgumentList.Add(input);

            using var process = Process.Start(psi);
            if (process == null) return null;
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                try { process.Kill(true); } catch { }
                return null;
            }
            var stdout = stdoutTask.GetAwaiter().GetResult();
            _ = stderrTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout)) return null;
            return stdout;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (input != null)
            {
                try { File.Delete(input); } catch { }
            }
        }
    }

    /// <summary>占位订阅判定：原始正文（提示语）+ 物理节点（名字/本机地址）。</summary>
    public static RustPlaceholderResult? TryDetectPlaceholder(string rawText, IEnumerable<NodeItem>? physicalNodes)
    {
        try
        {
            var nodes = (physicalNodes ?? Array.Empty<NodeItem>())
                .Where(n => !n.IsClashPassthrough && !n.IsCustomConfig)
                .Select(n => new { name = n.Name ?? "", server = n.Server ?? "" })
                .ToArray();
            var payload = JsonSerializer.Serialize(new { text = rawText ?? "", nodes });
            var stdout = Run("detect-placeholder", payload);
            if (stdout == null) return null;
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            return new RustPlaceholderResult(
                root.TryGetProperty("parsed", out var parsed) && parsed.ValueKind == JsonValueKind.True,
                root.TryGetProperty("placeholder", out var ph) && ph.ValueKind == JsonValueKind.True,
                root.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String
                    ? reason.GetString()
                    : null);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Rust 订阅正文解析（分享链接 / base64）；失败返回 null。</summary>
    public static RustParseResult? TryParseSubscription(string body)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(body)) return null;
            var stdout = Run("parse-subscription", body);
            if (stdout == null) return null;
            using var doc = JsonDocument.Parse(stdout);
            if (!doc.RootElement.TryGetProperty("nodes", out var nodesElement)
                || nodesElement.ValueKind != JsonValueKind.Array)
                return null;
            var nodes = new List<NodeItem>();
            foreach (var element in nodesElement.EnumerateArray())
            {
                var node = FromRustNode(element);
                if (node == null) return null; // 有节点映射不了 → 整体回退 C#，避免半套节点
                nodes.Add(node);
            }
            string? traffic = doc.RootElement.TryGetProperty("trafficInfo", out var ti)
                              && ti.ValueKind == JsonValueKind.String
                ? ti.GetString()
                : null;
            return new RustParseResult(nodes, traffic);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>故障转移阈值判定：输入当前状态，返回下一步状态与决定。</summary>
    public static RustFailoverResult? TryFailoverDecision(
        long nowMs, bool ok, bool hadTraffic, int fails, long? lastSwitchMs,
        int thresholdIdle = 3, int thresholdActive = 4, int cooldownMs = 60_000)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                nowMs,
                ok,
                hadTraffic,
                fails,
                lastSwitchMs,
                thresholdIdle,
                thresholdActive,
                cooldownMs,
            });
            var stdout = Run("failover-decide", payload);
            if (stdout == null) return null;
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            var decision = root.TryGetProperty("decision", out var d) ? d.GetString() : null;
            if (string.IsNullOrEmpty(decision)) return null;
            var newFails = root.TryGetProperty("fails", out var f) && f.TryGetInt32(out var n) ? n : fails;
            long? last = null;
            if (root.TryGetProperty("lastSwitchMs", out var l) && l.ValueKind == JsonValueKind.Number
                && l.TryGetInt64(out var value))
                last = value;
            return new RustFailoverResult(decision, newFails, last);
        }
        catch
        {
            return null;
        }
    }

    private static string? Str(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int Int(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var n) ? n : 0;

    private static bool Bool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static NodeProtocol? MapProtocol(string? protocol) => protocol switch
    {
        "vmess" => NodeProtocol.VMess,
        "vless" => NodeProtocol.VLESS,
        "trojan" => NodeProtocol.Trojan,
        "ss" => NodeProtocol.Shadowsocks,
        "hysteria2" => NodeProtocol.Hysteria2,
        "tuic" => NodeProtocol.TUIC,
        "anytls" => NodeProtocol.AnyTls,
        "socks" => NodeProtocol.Socks,
        "naive" => NodeProtocol.Naive,
        "wireguard" => NodeProtocol.WireGuard,
        _ => null,
    };

    private static NodeItem? FromRustNode(JsonElement element)
    {
        var protocol = MapProtocol(Str(element, "protocol"));
        if (protocol == null) return null;
        var node = new NodeItem
        {
            Name = Str(element, "name") ?? "",
            Protocol = protocol.Value,
            Server = Str(element, "server") ?? "",
            Port = Int(element, "port"),
            Uuid = Str(element, "uuid"),
            Password = Str(element, "password"),
            Method = Str(element, "method"),
            Username = Str(element, "username"),
            AlterId = Int(element, "alterId"),
            Cipher = Str(element, "cipher"),
            Flow = Str(element, "flow"),
            Network = Str(element, "network") ?? "tcp",
            Path = Str(element, "path"),
            HostHeader = Str(element, "hostHeader"),
            ServiceName = Str(element, "serviceName"),
            HeaderType = Str(element, "headerType"),
            Security = Str(element, "security") ?? "none",
            Sni = Str(element, "sni"),
            Fingerprint = Str(element, "fingerprint"),
            PublicKey = Str(element, "publicKey"),
            ShortId = Str(element, "shortId"),
            SpiderX = Str(element, "spiderX"),
            AllowInsecure = Bool(element, "allowInsecure"),
            VlessEncryption = Str(element, "vlessEncryption"),
            Mldsa65Verify = Str(element, "mldsa65Verify"),
            EchConfigList = Str(element, "echConfigList"),
            VerifyPeerCertByName = Str(element, "verifyPeerCertByName"),
            CertSha256 = Str(element, "certSha256"),
            KcpSeed = Str(element, "kcpSeed"),
            KcpMtu = element.TryGetProperty("kcpMtu", out var mtu) && mtu.TryGetInt32(out var mtuValue)
                ? mtuValue
                : null,
            Plugin = Str(element, "plugin"),
            PluginOpts = Str(element, "pluginOpts"),
        };
        if (node.Server.Length == 0 || node.Port <= 0) return null;
        return node;
    }
}
