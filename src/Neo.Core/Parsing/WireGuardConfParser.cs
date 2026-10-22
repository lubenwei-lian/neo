using Neo.Core.Models;

namespace Neo.Core.Parsing;

/// <summary>
/// WireGuard .conf 解析（对标主流客户端的 WireguardFmt.ResolveConfig）：
/// 识别 [Interface] / [Peer] 段，一个 Peer 产出一个节点。多个 Peer = 多个节点。
/// 只解析生成 sing-box endpoint 需要的字段，其余（AllowedIPs 等）由生成器固定为全量。
/// </summary>
public static class WireGuardConfParser
{
    public static bool LooksLike(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.Contains("[Interface]", StringComparison.OrdinalIgnoreCase)
               && text.Contains("PrivateKey", StringComparison.OrdinalIgnoreCase)
               && text.Contains("[Peer]", StringComparison.OrdinalIgnoreCase);
    }

    public static List<NodeItem> Parse(string text)
    {
        var nodes = new List<NodeItem>();
        if (!LooksLike(text)) return nodes;

        var iface = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var peers = new List<Dictionary<string, string>>();
        Dictionary<string, string>? cur = iface;
        foreach (var rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
            if (line.Equals("[Interface]", StringComparison.OrdinalIgnoreCase)) { cur = iface; continue; }
            if (line.Equals("[Peer]", StringComparison.OrdinalIgnoreCase))
            {
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                peers.Add(d);
                cur = d;
                continue;
            }
            if (line[0] == '[') continue;
            var idx = rawLine.IndexOf('=');
            if (idx <= 0) continue;
            var key = rawLine[..idx].Trim();
            var val = rawLine[(idx + 1)..].Trim();
            // 去掉行内注释（# / ;），但 base64 私钥不会包含这两个字符
            var c = val.IndexOfAny(new[] { '#', ';' });
            if (c >= 0) val = val[..c].TrimEnd();
            if (cur != null) cur[key] = val;
        }

        if (!iface.TryGetValue("PrivateKey", out var priv) || string.IsNullOrWhiteSpace(priv)) return nodes;
        iface.TryGetValue("Address", out var ifAddr);
        iface.TryGetValue("DNS", out var dns);
        var mtu = iface.TryGetValue("MTU", out var mtuStr) && int.TryParse(mtuStr, out var m) && m > 0 ? m : (int?)null;

        var index = 0;
        foreach (var p in peers)
        {
            index++;
            if (!p.TryGetValue("PublicKey", out var pub) || string.IsNullOrWhiteSpace(pub)) continue;
            if (!TryParseEndpoint(p.GetValueOrDefault("Endpoint") ?? "", out var host, out var port)) continue;
            var node = new NodeItem
            {
                Protocol = NodeProtocol.WireGuard,
                Name = $"WireGuard Peer {index}",
                Server = host,
                Port = port,
                Password = priv,
                Security = "none",
                Network = "udp",
                WgPublicKey = pub,
                WgPresharedKey = p.GetValueOrDefault("PresharedKey"),
                WgAddress = ifAddr,
                WgDns = dns,
                WgMtu = mtu,
                WgReserved = p.GetValueOrDefault("Reserved"),
            };
            if (p.TryGetValue("PersistentKeepalive", out var ka) && int.TryParse(ka, out var kav) && kav > 0)
                node.WgKeepAlive = kav;
            if (node.IsValid) nodes.Add(node);
        }
        return nodes;
    }

    private static bool TryParseEndpoint(string endpoint, out string host, out int port)
    {
        host = "";
        port = 51820;
        var s = endpoint.Trim();
        if (s.Length == 0) return false;
        if (s[0] == '[')
        {
            var close = s.IndexOf(']');
            if (close <= 0) return false;
            host = s[1..close];
            var rest = s[(close + 1)..].TrimStart(':');
            if (int.TryParse(rest, out var p6)) port = p6;
            return host.Length > 0;
        }
        var colon = s.LastIndexOf(':');
        if (colon <= 0) return false;
        host = s[..colon];
        return int.TryParse(s[(colon + 1)..], out port) && port > 0 && host.Length > 0;
    }
}
