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
            // ★ 2026-10-23 第二批：anytls / socks5 分享链接。
            //   anytls 只能走 sing-box（xray 26 实测无 anytls 出站）；
            //   socks:// 是常见的分享链接写法，此前 neo 完全不认。
            if (link.StartsWith("anytls://", StringComparison.OrdinalIgnoreCase)) return ParseAnyTls(link);
            if (link.StartsWith("wireguard://", StringComparison.OrdinalIgnoreCase)
                || link.StartsWith("wg://", StringComparison.OrdinalIgnoreCase)) return ParseWireGuard(link);
            if (link.StartsWith("socks://", StringComparison.OrdinalIgnoreCase) ||
                link.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase) ||
                link.StartsWith("socks5h://", StringComparison.OrdinalIgnoreCase)) return ParseSocks(link);
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
            // ★ 2026-10-23 IPv6 方括号：.NET 的 uri.Host 对 IPv6 字面量会带 []（实测），
            //   写进 xray/sing-box 的 address/server 前统一剥掉；同时让 EndPoint/NodeKey 口径一致。
            Server = StripBrackets(uri.Host),
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
            // ★ 2026-10-23 IPv6 方括号：同 vless，解析阶段就去掉 []（xray/sing-box 共用）。
            Server = StripBrackets(uri.Host),
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
        if (!string.IsNullOrEmpty(query))
        {
            // ★ 2026-10-23 ss-plugin 生效（审计 P2-17）：ss:// 里常见写法是
            //   `plugin=obfs-local%3Bobfs%3Dhttp%3Bobfs-host%3Dbing.com`（SIP003）描述插件；
            //   旧实现只把整串 query 塞进 Extras["query"]、全仓无读取方 → 插件节点被当裸 SS（必连不上）。
            //   这里保留原串（兼容旧缓存/排障），并解析出 Plugin/PluginOpts 供 sing-box 出站使用。
            node.Extras["query"] = Uri.UnescapeDataString(query);
            ApplySsPlugin(node, ParseQuery(query));
        }
        return node;
    }

    private static NodeItem? ParseHysteria2(string link)
    {
        var body = link[(link.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var (auth, host, portExpr, query, name) = SplitAuthority(body);
        if (host.Length == 0) return null;

        // ★ 2026-10-23 hy2 端口跳跃（常见标准写法）：
        //   hysteria2://auth@host:443,5000-6000/?...
        //   旧实现用 int.TryParse 解析端口 → "443,5000-6000" → 0 → 整条节点被丢；
        //   省略端口（默认 443）也会被丢。这里归一化成 sing-box 的 server_ports 写法
        //   （区间用 "start:end"，多个用逗号），Port 取第一个端口供 UI/排序使用。
        var portSpec = NormalizePortSpec(portExpr);
        var firstPort = FirstPort(portSpec);
        var q = ParseQuery(query);
        var node = new NodeItem
        {
            Protocol = NodeProtocol.Hysteria2,
            Password = auth,
            Server = host,
            Port = firstPort > 0 ? firstPort : 443,
            Name = name,
            Security = "tls",
            Sni = OrNull(G(q, "sni")) ?? OrNull(G(q, "peer")),
        };
        if (portSpec.Length > 0) node.Extras["server_ports"] = portSpec;
        node.AllowInsecure = ParseBool(G(q, "insecure")) || ParseBool(G(q, "allowinsecure"));
        // ★ 2026-10-23 第二批：hy2 的 pinSHA256 / ech 此前只进 Extras：pin 从来没被读取（1.14 无该字段，走 insecure 兜底），
        //   ech 也没写进 sing-box 的 tls.ech。现在 pin 落 CertSha256（排障/未来内核支持），ech 落 EchConfigList。
        node.CertSha256 = OrNull(G(q, "pinSHA256")) ?? node.CertSha256;
        node.EchConfigList = OrNull(G(q, "ech")) ?? node.EchConfigList;
        Copy(node, q, "obfs");
        Copy(node, q, "obfs-password");
        Copy(node, q, "alpn");
        Copy(node, q, "mport");
        Copy(node, q, "pinsha256");
        Copy(node, q, "up");
        Copy(node, q, "down");

        // ★ 2026-10-23 第二批：gecko 混淆。
        //   链接写法 obfs=gecko&obfs-password=...&minPacketSize=512&maxPacketSize=1200；
        //   sing-box 1.14.3 实测支持 obfs.type=gecko + min/max_packet_size（见台账 F1），
        //   旧实现只把 obfs 值原样写入 → 没带分片参数，对端按 salamander 处理，直接连不上。
        if (string.Equals(G(q, "obfs"), "gecko", StringComparison.OrdinalIgnoreCase))
        {
            node.GeckoMinPacketSize = int.TryParse(G(q, "minPacketSize"), out var gmin) && gmin > 0 ? gmin : 512;
            node.GeckoMaxPacketSize = int.TryParse(G(q, "maxPacketSize"), out var gmax) && gmax > 0 ? gmax : 1200;
        }
        return node;
    }

    /// <summary>
    /// anytls 分享链接：anytls://password@host:port?sni=..&amp;insecure=1#name。
    /// 密码就是 URI 的 userinfo；xray 26 没有 anytls 出站，只能走 sing-box。
    /// </summary>
    private static NodeItem? ParseAnyTls(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) return null;
        var node = new NodeItem
        {
            Protocol = NodeProtocol.AnyTls,
            Password = Uri.UnescapeDataString(uri.UserInfo),
            Server = StripBrackets(uri.Host),
            Port = uri.Port > 0 ? uri.Port : 443,
            Name = Uri.UnescapeDataString(uri.Fragment.TrimStart('#')),
            Security = "tls",
        };
        ApplyCommonQuery(node, ParseQuery(uri.Query));
        node.Security = "tls";
        return node;
    }

    /// <summary>
    /// WireGuard 分享链接（对标主流客户端 WireguardFmt）：
    ///   wireguard://&lt;私钥&gt;@host:port?publickey=&amp;address=&amp;presharedkey=&amp;mtu=&amp;reserved=&amp;dns=#name
    /// 私钥放在 userinfo（URL 编码）；地址/端口是**对端** endpoint。
    /// </summary>
    private static NodeItem? ParseWireGuard(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) return null;
        var q = ParseQuery(uri.Query);
        var node = new NodeItem
        {
            Protocol = NodeProtocol.WireGuard,
            Password = Uri.UnescapeDataString(uri.UserInfo),
            Server = StripBrackets(uri.Host),
            Port = uri.Port > 0 ? uri.Port : 51820,
            Name = Uri.UnescapeDataString(uri.Fragment.TrimStart('#')),
            Security = "none",
            Network = "udp",
            WgPublicKey = OrNull(G(q, "publickey")),
            WgPresharedKey = OrNull(G(q, "presharedkey")),
            WgAddress = OrNull(G(q, "address")),
            WgReserved = OrNull(G(q, "reserved")),
            WgDns = OrNull(G(q, "dns")),
        };
        if (int.TryParse(G(q, "mtu"), out var mtu) && mtu > 0) node.WgMtu = mtu;
        if (int.TryParse(G(q, "keepalive"), out var ka) && ka > 0) node.WgKeepAlive = ka;
        if (node.Name.Length == 0) node.Name = "WireGuard " + node.EndPoint;
        return node.IsValid && !string.IsNullOrEmpty(node.Password) ? node : null;
    }

    /// <summary>
    /// socks5 分享链接。支持两种写法：
    ///   socks://base64(user:pass)@host:port   （部分面板生成的写法）
    ///   socks://user:pass@host:port / socks5://user:pass@host:port（标准 URI）
    /// 也兼容整段 base64（socks://<base64 of "user:pass@host:port">，早期老写法）。
    /// </summary>
    private static NodeItem? ParseSocks(string link)
    {
        var body = link[(link.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var node = new NodeItem { Protocol = NodeProtocol.Socks, Security = "none" };

        // 名称
        var hash = body.IndexOf('#');
        if (hash >= 0) { node.Name = Uri.UnescapeDataString(body[(hash + 1)..]); body = body[..hash]; }

        // 查询串（socks 一般没有，但保留 sni 之类奇怪写法的兼容）
        string query = "";
        var qm = body.IndexOf('?');
        if (qm >= 0) { query = body[(qm + 1)..]; body = body[..qm]; }

        body = body.Trim();
        var at = body.LastIndexOf('@');
        string hostPort;
        if (at >= 0)
        {
            var userInfoRaw = body[..at];
            hostPort = body[(at + 1)..];
            var userInfo = Uri.UnescapeDataString(userInfoRaw);
            if (!userInfo.Contains(':'))
            {
                // 部分面板用 base64("user:pass") 放 userinfo
                var dec = TryB64Decode(userInfo);
                if (dec != null && dec.Contains(':')) userInfo = dec;
            }
            var colon = userInfo.IndexOf(':');
            if (colon >= 0)
            {
                node.Username = userInfo[..colon];
                node.Password = userInfo[(colon + 1)..];
            }
            else if (userInfo.Length > 0) node.Username = userInfo;
        }
        else
        {
            // 老写法：socks://<base64("user:pass@host:port")>
            var dec = TryB64Decode(body);
            if (dec == null) return null;
            var at2 = dec.LastIndexOf('@');
            if (at2 < 0) return null;
            var ui = dec[..at2];
            var c2 = ui.IndexOf(':');
            if (c2 >= 0) { node.Username = ui[..c2]; node.Password = ui[(c2 + 1)..]; }
            hostPort = dec[(at2 + 1)..];
        }

        var (host, portExpr) = SplitHostPortExpr(hostPort);
        node.Server = StripBrackets(host);
        node.Port = int.TryParse(portExpr, out var p) && p > 0 ? p : 1080;
        if (node.Name.Length == 0) node.Name = $"{node.Server}:{node.Port}";
        if (!string.IsNullOrEmpty(query)) node.Extras["query"] = Uri.UnescapeDataString(query);
        return node.IsValid ? node : null;
    }

    private static NodeItem? ParseTuic(string link)
    {
        var body = link[(link.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var (auth, host, portExpr, query, name) = SplitAuthority(body);
        var port = FirstPort(NormalizePortSpec(portExpr));
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

    private static (string auth, string host, string portExpr, string query, string name) SplitAuthority(string body)
    {
        string name = "";
        var hash = body.IndexOf('#');
        if (hash >= 0) { name = Uri.UnescapeDataString(body[(hash + 1)..]); body = body[..hash]; }

        string query = "";
        var qm = body.IndexOf('?');
        if (qm >= 0) { query = body[(qm + 1)..]; body = body[..qm]; }

        body = body.TrimEnd('/');
        var at = body.LastIndexOf('@');
        if (at < 0) return ("", "", "", query, name);
        var auth = Uri.UnescapeDataString(body[..at]);
        var (host, portExpr) = SplitHostPortExpr(body[(at + 1)..]);
        return (auth, host, portExpr, query, name);
    }

    /// <summary>
    /// 拆分 host[:portExpr]。与 SplitHostPort 的区别：portExpr 原样保留（可为 "443,5000-6000"
    /// 这类端口跳跃写法，也可省略为空串由调用方给默认值）；IPv6 仍沿用 [..] 形式，端口从 "]" 之后取。
    /// </summary>
    private static (string host, string portExpr) SplitHostPortExpr(string hostPort)
    {
        hostPort = hostPort.Trim().Trim('/');
        if (hostPort.StartsWith('['))
        {
            var close = hostPort.IndexOf(']');
            if (close > 0)
            {
                var h6 = hostPort[1..close];
                var rest = hostPort[(close + 1)..].Trim();
                return (h6, rest.StartsWith(':') ? rest[1..].Trim() : "");
            }
        }
        var idx = hostPort.LastIndexOf(':');
        if (idx < 0) return (hostPort, "");
        return (hostPort[..idx], hostPort[(idx + 1)..].Trim());
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

        // ★ 2026-10-23 第二批：分享链接里这些参数此前全被丢进 Extras（全仓无读取方）→
        //   节点能导入但连不上/连上走错传输。现在逐个落到 NodeItem 的强类型字段，
        //   由 xray / sing-box 生成器消费。字段名与分享链接标准参数名一致。
        //   · encryption：VLESS 后量子加密串（空或 none = 不加密；xray 26 对公网明文 vless 直接拒绝启动）
        //   · pqv：REALITY 的 ML-DSA-65 校验；· ech：TLS ECH 配置；· vcn：证书名校验；· pcs：证书 pin
        var enc = OrNull(G("encryption"));
        if (enc != null && !enc.Equals("none", StringComparison.OrdinalIgnoreCase)) node.VlessEncryption = enc;
        node.Mldsa65Verify = OrNull(G("pqv")) ?? node.Mldsa65Verify;
        node.EchConfigList = OrNull(G("ech")) ?? node.EchConfigList;
        node.VerifyPeerCertByName = OrNull(G("vcn")) ?? node.VerifyPeerCertByName;
        node.CertSha256 = OrNull(G("pcs")) ?? node.CertSha256;

        // 传输层参数按网络类型落字段（kcp 的 seed/mtu、xhttp 的 mode/extra、grpc 的 authority/mode）
        switch (node.Network)
        {
            case "kcp":
                node.KcpSeed = OrNull(G("seed")) ?? node.KcpSeed;
                if (int.TryParse(G("mtu"), out var kmtu) && kmtu > 0) node.KcpMtu = kmtu;
                break;
            case "xhttp":
                node.XhttpMode = OrNull(G("mode")) ?? node.XhttpMode;
                node.XhttpExtra = OrNull(G("extra")) ?? node.XhttpExtra;
                break;
            case "grpc":
                node.GrpcAuthority = OrNull(G("authority")) ?? node.GrpcAuthority;
                node.GrpcMode = OrNull(G("mode")) ?? node.GrpcMode;
                break;
        }

        // ★ 2026-10-23 insecure 别名：分享链接同时存在 insecure=1 与 allowInsecure=1 两种写法，
        //   旧实现只认后者 → insecure=1 被静默忽略（证书校验没跳过，节点连不上）。两者任一为真即视为跳过校验。
        node.AllowInsecure = ParseBool(G("allowInsecure")) || ParseBool(G("insecure"));

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
                case "insecure":
                // 2026-10-23 第二批：已落到强类型字段的参数不再重复进 Extras（避免双份真相）
                case "encryption":
                case "pqv":
                case "ech":
                case "vcn":
                case "pcs":
                case "seed":
                case "mtu":
                case "extra":
                case "authority":
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

    /// <summary>去掉 IPv6 字面量两侧的方括号（.NET 的 uri.Host 会保留 []）。</summary>
    private static string StripBrackets(string host) =>
        host.Length >= 2 && host[0] == '[' && host[^1] == ']' ? host[1..^1] : host;

    /// <summary>
    /// 解析 Shadowsocks SIP003 插件：分享链接常见写法是
    /// `plugin=obfs-local%3Bobfs%3Dhttp%3Bobfs-host%3Dbing.com`（分号分隔）；
    /// 也兼容 `plugin=...&plugin-opts=k%3Dv%3B...` 的分离写法。
    /// 存入 NodeItem.Plugin / PluginOpts，由 SingBoxConfigBuilder 写进 sing-box 出站。
    /// </summary>
    private static void ApplySsPlugin(NodeItem node, Dictionary<string, string> q)
    {
        var raw = G(q, "plugin");
        if (raw.Length == 0) return;
        var parts = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return;
        node.Plugin = parts[0];
        var opts = new List<string>();
        for (var i = 1; i < parts.Length; i++)
            if (parts[i].Length > 0) opts.Add(parts[i]);
        var extra = G(q, "plugin-opts");
        if (extra.Length == 0) extra = G(q, "plugin_opts");
        if (extra.Length > 0)
        {
            foreach (var o in extra.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (o.Length > 0) opts.Add(o);
        }
        if (opts.Count > 0) node.PluginOpts = string.Join(";", opts);
    }

    /// <summary>
    /// 归一化 hy2 端口写法：逗号分隔的 "443,5000-6000" / "5000:6000" → "443,5000:6000"
    /// （sing-box server_ports 用冒号表示区间）；无法解析的片段直接丢弃。
    /// </summary>
    private static string NormalizePortSpec(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr)) return "";
        var list = new List<string>();
        foreach (var part in expr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var segs = part.Replace('-', ':').Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segs.Length == 0 || !int.TryParse(segs[0], out var a) || a <= 0) continue;
            if (segs.Length >= 2 && int.TryParse(segs[1], out var b) && b > 0) list.Add($"{a}:{b}");
            else list.Add(a.ToString());
        }
        return list.Count == 0 ? "" : string.Join(",", list);
    }

    /// <summary>取归一化端口串里的第一个端口（供 NodeItem.Port 使用；0 表示无）。</summary>
    private static int FirstPort(string spec)
    {
        if (spec.Length == 0) return 0;
        var first = spec.Split(',', 2)[0];
        var head = first.Split(':', 2)[0];
        return int.TryParse(head, out var p) ? p : 0;
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
