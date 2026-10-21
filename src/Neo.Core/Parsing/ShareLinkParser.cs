using System.Text;
using System.Text.Json;
using Neo.Core.Models;

namespace Neo.Core.Parsing;

public static class ShareLinkParser
{
    public static NodeItem? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var link = raw.Trim();
        if (link.Contains((char)13) || link.Contains((char)10)) return null;
        try
        {
            if (link.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase)) return ParseVmess(link);
            if (link.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)) return ParseVless(link);
            if (link.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase)) return ParseTrojan(link);
            if (link.StartsWith("ss://", StringComparison.OrdinalIgnoreCase)) return ParseShadowsocks(link);
            if (link.StartsWith("hysteria2://", StringComparison.OrdinalIgnoreCase) ||
                link.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase)) return ParseHysteria2(link);
            if (link.StartsWith("tuic://", StringComparison.OrdinalIgnoreCase)) return ParseTuic(link);
            return null;
        }
        catch
        {
            return null;
        }
    }

    public static List<NodeItem> ParseMany(IEnumerable<string> lines)
    {
        var result = new List<NodeItem>();
        foreach (var line in lines)
        {
            var node = Parse(line);
            if (node != null) result.Add(node);
        }
        return result;
    }

    private static NodeItem? ParseVmess(string link)
    {
        var b64 = link.Substring("vmess://".Length).Trim();
        var json = TryB64Decode(b64);
        if (json == null) return null;

        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;

        string S(string key)
        {
            if (!r.TryGetProperty(key, out var v)) return "";
            return v.ValueKind switch
            {
                JsonValueKind.Number => v.GetRawText(),
                JsonValueKind.String => v.GetString() ?? "",
                _ => ""
            };
        }

        var node = new NodeItem
        {
            Protocol = NodeProtocol.VMess,
            Name = S("ps"),
            Server = S("add"),
            Uuid = S("id"),
            AlterId = int.TryParse(S("aid"), out var aid) ? aid : 0,
            Cipher = string.IsNullOrEmpty(S("scy")) ? "auto" : S("scy"),
            Network = string.IsNullOrEmpty(S("net")) ? "tcp" : S("net").ToLowerInvariant(),
        };
        node.Port = int.TryParse(S("port"), out var p) ? p : 0;

        if (string.Equals(S("tls"), "tls", StringComparison.OrdinalIgnoreCase)) node.Security = "tls";
        node.Sni = OrNull(S("sni")) ?? OrNull(S("peer"));
        node.Fingerprint = OrNull(S("fp"));
        node.Path = OrNull(S("path"));
        node.HostHeader = OrNull(S("host"));
        node.HeaderType = OrNull(S("type"));

        //   原先只存 Path、从不写 ServiceName ⇒ xray/sing-box 都生成空 serviceName，
        //   节点必定连不上且内核不报错（静默失败）。已有 ServiceName 时不覆盖。
        if (string.Equals(node.Network, "grpc", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(node.ServiceName))
            node.ServiceName = OrNull(S("path"));

        var alpn = S("alpn");
        if (!string.IsNullOrEmpty(alpn)) node.Extras["alpn"] = alpn;

        return node;
    }

    private static NodeItem? ParseVless(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) return null;

        var node = new NodeItem
        {
            Protocol = NodeProtocol.VLESS,
            Uuid = Uri.UnescapeDataString(uri.UserInfo),
            Server = uri.Host,
            Port = uri.Port,
            Name = Uri.UnescapeDataString(uri.Fragment.TrimStart('#')),
        };
        ApplyCommonQuery(node, ParseQuery(uri.Query));
        return node;
    }

    private static NodeItem? ParseTrojan(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) return null;

        var node = new NodeItem
        {
            Protocol = NodeProtocol.Trojan,
            Password = Uri.UnescapeDataString(uri.UserInfo),
            Server = uri.Host,
            Port = uri.Port,
            Name = Uri.UnescapeDataString(uri.Fragment.TrimStart('#')),
            Security = "tls",
        };
        ApplyCommonQuery(node, ParseQuery(uri.Query));
        return node;
    }

    private static NodeItem? ParseShadowsocks(string link)
    {
        var body = link.Substring("ss://".Length);

        string? name = null;
        var hashIdx = body.IndexOf('#');
        if (hashIdx >= 0)
        {
            name = Uri.UnescapeDataString(body[(hashIdx + 1)..]);
            body = body[..hashIdx];
        }

        string? query = null;
        var qIdx = body.IndexOf('?');
        if (qIdx >= 0)
        {
            query = body[(qIdx + 1)..];
            body = body[..qIdx];
        }

        string method, password, host;
        int port;

        var atIdx = body.LastIndexOf('@');
        if (atIdx >= 0)
        {
            var userInfoRaw = body[..atIdx];
            var hostPort = body[(atIdx + 1)..];
            var userInfo = userInfoRaw.Contains(':')
                ? Uri.UnescapeDataString(userInfoRaw)
                : (TryB64Decode(userInfoRaw) ?? "");
            var colon = userInfo.IndexOf(':');
            if (colon < 0) return null;
            method = userInfo[..colon];
            password = userInfo[(colon + 1)..];
            (host, port) = SplitHostPort(hostPort);
        }
        else
        {
            var decoded = TryB64Decode(body);
            if (decoded == null) return null;
            var at2 = decoded.LastIndexOf('@');
            if (at2 < 0) return null;
            var ui = decoded[..at2];
            var hp = decoded[(at2 + 1)..];
            var colon = ui.IndexOf(':');
            if (colon < 0) return null;
            method = ui[..colon];
            password = ui[(colon + 1)..];
            (host, port) = SplitHostPort(hp);
        }

        var node = new NodeItem
        {
            Protocol = NodeProtocol.Shadowsocks,
            Name = name ?? "",
            Server = host,
            Port = port,
            Method = method,
            Password = password,
        };
        if (!string.IsNullOrEmpty(query)) node.Extras["query"] = Uri.UnescapeDataString(query);
        return node;
    }

    private static NodeItem? ParseHysteria2(string link)
    {
        var body = link[(link.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var (auth, host, port, query, name) = SplitAuthority(body);
        if (host.Length == 0 || port <= 0) return null;

        var q = ParseQuery(query);
        var node = new NodeItem
        {
            Protocol = NodeProtocol.Hysteria2,
            Password = auth,
            Server = host,
            Port = port,
            Name = name,
            Security = "tls",
            Sni = OrNull(G(q, "sni")) ?? OrNull(G(q, "peer")),
        };
        node.AllowInsecure = ParseBool(G(q, "insecure")) || ParseBool(G(q, "allowinsecure"));
        Copy(node, q, "obfs");
        Copy(node, q, "obfs-password");
        Copy(node, q, "alpn");
        Copy(node, q, "mport");
        Copy(node, q, "pinsha256");
        Copy(node, q, "up");
        Copy(node, q, "down");
        return node;
    }

    private static NodeItem? ParseTuic(string link)
    {
        var body = link[(link.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var (auth, host, port, query, name) = SplitAuthority(body);
        if (host.Length == 0 || port <= 0) return null;

        var q = ParseQuery(query);
        var node = new NodeItem
        {
            Protocol = NodeProtocol.TUIC,
            Server = host,
            Port = port,
            Name = name,
            Security = "tls",
            Sni = OrNull(G(q, "sni")) ?? OrNull(G(q, "peer")),
        };

        var colon = auth.IndexOf(':');
        if (colon >= 0) { node.Uuid = auth[..colon]; node.Password = auth[(colon + 1)..]; }
        else
        {
            node.Uuid = auth;
            node.Password = OrNull(G(q, "password")) ?? "";
        }

        node.AllowInsecure = ParseBool(G(q, "insecure")) || ParseBool(G(q, "allowinsecure"));
        Copy(node, q, "congestion_control");
        Copy(node, q, "udp_relay_mode");
        Copy(node, q, "alpn");
        return node;
    }

    private static (string auth, string host, int port, string query, string name) SplitAuthority(string body)
    {
        string name = "";
        var hash = body.IndexOf('#');
        if (hash >= 0) { name = Uri.UnescapeDataString(body[(hash + 1)..]); body = body[..hash]; }

        string query = "";
        var qm = body.IndexOf('?');
        if (qm >= 0) { query = body[(qm + 1)..]; body = body[..qm]; }

        body = body.TrimEnd('/');
        var at = body.LastIndexOf('@');
        if (at < 0) return ("", "", 0, query, name);
        var auth = Uri.UnescapeDataString(body[..at]);
        var (host, port) = SplitHostPort(body[(at + 1)..]);
        return (auth, host, port, query, name);
    }

    private static string G(Dictionary<string, string> q, string k) =>
        q.TryGetValue(k, out var v) ? v : "";

    private static bool ParseBool(string s) =>
        s == "1" || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);

    private static void Copy(NodeItem node, Dictionary<string, string> q, string key)
    {
        var v = G(q, key);
        if (v.Length > 0) node.Extras[key] = v;
    }

    private static void ApplyCommonQuery(NodeItem node, Dictionary<string, string> q)
    {
        string G(string k) => q.TryGetValue(k, out var v) ? v : "";

        var type = G("type");
        if (!string.IsNullOrEmpty(type)) node.Network = type.ToLowerInvariant();

        var security = G("security");
        if (!string.IsNullOrEmpty(security)) node.Security = security.ToLowerInvariant();

        node.Sni = OrNull(G("sni")) ?? OrNull(G("peer")) ?? node.Sni;
        node.Fingerprint = OrNull(G("fp")) ?? node.Fingerprint;
        node.PublicKey = OrNull(G("pbk"));
        node.ShortId = OrNull(G("sid"));
        node.SpiderX = OrNull(G("spx"));
        node.Flow = OrNull(G("flow"));
        node.Path = OrNull(G("path")) ?? node.Path;
        node.HostHeader = OrNull(G("host")) ?? node.HostHeader;
        node.ServiceName = OrNull(G("serviceName"));
        node.HeaderType = OrNull(G("headerType"));

        var allow = G("allowInsecure");
        node.AllowInsecure = allow == "1" || string.Equals(allow, "true", StringComparison.OrdinalIgnoreCase);

        foreach (var kv in q)
        {
            switch (kv.Key.ToLowerInvariant())
            {
                case "type":
                case "security":
                case "sni":
                case "peer":
                case "fp":
                case "pbk":
                case "sid":
                case "spx":
                case "flow":
                case "path":
                case "host":
                case "servicename":
                case "headertype":
                case "allowinsecure":
                    break;
                default:
                    node.Extras[kv.Key] = kv.Value;
                    break;
            }
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return dict;

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx < 0) dict[Uri.UnescapeDataString(pair)] = "";
            else dict[Uri.UnescapeDataString(pair[..idx])] = Uri.UnescapeDataString(pair[(idx + 1)..]);
        }
        return dict;
    }

    private static (string host, int port) SplitHostPort(string hostPort)
    {
        hostPort = hostPort.Trim().Trim('/');
        if (hostPort.StartsWith('['))
        {
            var close = hostPort.IndexOf(']');
            if (close > 0)
            {
                var h6 = hostPort[1..close];
                var rest = hostPort[(close + 1)..].TrimStart(':');
                return (h6, int.TryParse(rest, out var p6) ? p6 : 0);
            }
        }
        var idx = hostPort.LastIndexOf(':');
        if (idx < 0) return (hostPort, 0);
        return (hostPort[..idx], int.TryParse(hostPort[(idx + 1)..], out var p) ? p : 0);
    }

    public static string? TryB64Decode(string s)
    {
        s = s.Trim().Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(s));
        }
        catch
        {
            return null;
        }
    }

    private static string? OrNull(string s) => string.IsNullOrEmpty(s) ? null : s;
}
