using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Neo.Core.Core;

/// <summary>只从本次已提交会话拥有的配置读取接入/控制器和出口类型；不按国家或设置猜出口。</summary>
public sealed class KernelRouteContext
{
    public string Kind { get; private init; } = "";
    public int HttpPort { get; private init; }
    public int ApiPort { get; private init; }
    public string InboundTag { get; private init; } = "http-in";
    public string DefaultOutbound { get; private init; } = "";
    private bool SniffTls { get; init; }
    private string Secret { get; init; } = "";
    private Dictionary<string, string> OutboundTypes { get; init; } = new(StringComparer.Ordinal);
    private static readonly HttpClient Api = new(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(1) })
    { Timeout = TimeSpan.FromSeconds(1), MaxResponseContentBufferSize = 2 * 1024 * 1024 };

    public static KernelRouteContext Load(NetworkDiagnosticSession session)
    {
        var text = File.ReadAllText(session.ConfigPath);
        if (session.Kind == "mihomo")
        {
            // MihomoConfigBuilder owns and writes these exact top-level header values.
            string Value(string key) => text.Split('\n').FirstOrDefault(x => x.StartsWith(key + ":", StringComparison.Ordinal))?
                .Split(':', 2)[1].Trim().Trim('"', '\'') ?? "";
            var endpoint = new Uri("http://" + Value("external-controller"));
            if (!endpoint.IsLoopback) throw new InvalidDataException("控制器不是本机回环");
            return new() { Kind = session.Kind, HttpPort = int.Parse(Value("port")), ApiPort = endpoint.Port, Secret = Value("secret") };
        }
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var xray = session.Kind == "xray";
        var inbounds = root.GetProperty("inbounds").EnumerateArray().ToArray();
        var http = inbounds.First(x => String(x, xray ? "protocol" : "type") is "http" or "mixed");
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("outbounds", out var outs))
            foreach (var outbound in outs.EnumerateArray()) types[String(outbound, "tag")] = String(outbound, xray ? "protocol" : "type");
        if (root.TryGetProperty("endpoints", out var endpoints))
            foreach (var endpoint in endpoints.EnumerateArray()) types[String(endpoint, "tag")] = String(endpoint, "type");
        var port = 0; var secret = "";
        if (xray)
        {
            var api = inbounds.FirstOrDefault(x => String(x, "protocol") == "dokodemo-door");
            if (api.ValueKind == JsonValueKind.Object) port = api.GetProperty("port").GetInt32();
        }
        else if (root.TryGetProperty("experimental", out var exp) && exp.TryGetProperty("clash_api", out var clash))
        {
            var endpoint = new Uri("http://" + String(clash, "external_controller"));
            if (!endpoint.IsLoopback) throw new InvalidDataException("控制器不是本机回环");
            port = endpoint.Port; secret = String(clash, "secret");
        }
        var defaultTag = xray ? String(root.GetProperty("outbounds")[0], "tag") : "";
        var sniffTls = xray && http.TryGetProperty("sniffing", out var sniffing)
            && sniffing.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True;
        return new() { Kind = session.Kind, HttpPort = http.GetProperty(xray ? "port" : "listen_port").GetInt32(), ApiPort = port, SniffTls = sniffTls,
            InboundTag = String(http, "tag"), DefaultOutbound = defaultTag, Secret = secret, OutboundTypes = types };
    }

    public NetworkRouteEvidence Describe(string tag, string source, string detail)
    {
        OutboundTypes.TryGetValue(tag, out var type);
        var path = type switch
        {
            "freedom" or "direct" => "直连",
            "blackhole" or "block" => "拦截",
            "loopback" => "内核转交",
            "dns" => "DNS 出口",
            "vless" or "vmess" or "trojan" or "shadowsocks" or "socks" or "http" or "hysteria2" or "tuic" or "anytls" or "wireguard" or "ssh" => "代理",
            _ => Kind == "mihomo" ? tag.ToUpperInvariant() switch { "DIRECT" => "直连", "REJECT" or "REJECT-DROP" => "拦截", "" => "未确认", _ => "代理" } : "未确认",
        };
        return new(path, tag, source, detail + (string.IsNullOrEmpty(type) ? "" : $"；出口类型 {type}"));
    }

    public async Task<NetworkRouteEvidence> QueryXrayAsync(Uri target, int sourcePort, CancellationToken ct)
    {
        if (ApiPort is < 1 or > 65535) return NetworkRouteEvidence.Unknown("当前配置没有可用的本机路由 API");
        try
        {
            var result = await XrayRouteClient.QueryAsync(ApiPort, InboundTag, target, sourcePort, ct, SniffTls ? "tls" : "").ConfigureAwait(false);
            var tag = result.Outbound ?? (result.UsesDefault ? DefaultOutbound : "");
            if (string.IsNullOrEmpty(tag)) return NetworkRouteEvidence.Unknown(result.Detail);
            return Describe(tag, "内核规则判定", result.Detail + (result.UsesDefault ? $"：{tag}" : ""));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        { return NetworkRouteEvidence.Unknown($"路由 API 不可用：{ex.GetType().Name}；不据此猜测出口"); }
    }

    public async Task<NetworkRouteEvidence?> ObserveClashAsync(Uri target, int sourcePort, CancellationToken ct)
    {
        if (ApiPort is < 1 or > 65535 || sourcePort <= 0) return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{ApiPort}/connections");
            if (!string.IsNullOrEmpty(Secret)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Secret);
            using var response = await Api.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
            return FindClashConnection(doc.RootElement, target, sourcePort);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException)
        { return null; }
    }

    public NetworkRouteEvidence? FindClashConnection(JsonElement root, Uri target, int sourcePort)
    {
        if (!root.TryGetProperty("connections", out var connections) || connections.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in connections.EnumerateArray())
        {
            if (!item.TryGetProperty("metadata", out var metadata)) continue;
            if (!int.TryParse(metadata.TryGetProperty("sourcePort", out var p) ? p.ToString() : "", out var port) || port != sourcePort) continue;
            // A source port alone is insufficient evidence when TUN/other apps have concurrent connections.
            if (!string.Equals(String(metadata, "host"), target.DnsSafeHost, StringComparison.OrdinalIgnoreCase)) continue;
            if (!item.TryGetProperty("chains", out var chains) || chains.ValueKind != JsonValueKind.Array || chains.GetArrayLength() == 0) continue;
            var chain = chains.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            return Describe(chain[0], "实际连接观测", "当前内核 /connections：源端口及目标域名匹配；链路 "
                + string.Join(" ← ", chain) + "；规则 " + String(item, "rule") + " " + String(item, "rulePayload"));
        }
        return null;
    }

    private static string String(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
