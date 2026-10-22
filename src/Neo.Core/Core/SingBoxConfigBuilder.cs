using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Neo.Core.Models;

namespace Neo.Core.Core;

public static class SingBoxConfigBuilder
{
    public const string RuleSetCn = "rule-sets/geosite-cn.srs";
    public const string RuleSetGeoipCn = "rule-sets/geoip-cn.srs";
    public const string RuleSetAds = "rule-sets/geosite-category-ads-all.srs";

    /// <summary>sing-box 的 Clash API 端口（仅本机回环；与 xray 的 21xxx 端口段一致）。</summary>
    public const int ClashApiPort = 21811;
    public const string ClashApiSecret = "neo-local-stats";

    public static string Build(NodeItem node, int socksPort, int httpPort, string proxyMode, bool bypassPrivate,
        bool bypassCn, bool blockAds, bool allowLan, IReadOnlyList<RouteRule>? customRules, string logLevel, string dnsServer,
        bool gameDirectEnabled = false, IReadOnlyList<string>? gameDirectProcesses = null)
    {
        if (!node.IsValid) throw new ArgumentException("节点无效（缺地址或端口）");

        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = MapLevel(logLevel), ["timestamp"] = true },
            ["dns"] = BuildDns(dnsServer),
            ["inbounds"] = new JsonArray(
                BuildSocksInbound(socksPort, allowLan),
                BuildHttpInbound(httpPort, allowLan)),
            ["outbounds"] = BuildOutbounds(node),
            ["route"] = BuildRoute(proxyMode, bypassPrivate, bypassCn, blockAds, customRules, tunMode: false, gameDirect: gameDirectEnabled, gameProcesses: gameDirectProcesses),
            ["experimental"] = BuildExperimental(),
        };
        ApplyWireGuardEndpoints(root, node);

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static string BuildTun(NodeItem node, int socksPort, int httpPort, string proxyMode, bool bypassPrivate,
        bool bypassCn, bool blockAds, bool allowLan, IReadOnlyList<RouteRule>? customRules, string logLevel, string dnsServer,
        bool gameDirectEnabled = false, IReadOnlyList<string>? gameDirectProcesses = null)
    {
        if (!node.IsValid) throw new ArgumentException("节点无效（缺地址或端口）");

        var root = BuildTunCore(BuildOutbounds(node), socksPort, httpPort, proxyMode, bypassPrivate,
            bypassCn, blockAds, allowLan, customRules, logLevel, dnsServer,
            gameDirectEnabled, gameDirectProcesses);
        ApplyWireGuardEndpoints(root, node);
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>构造 TUN 配置主体，出站数组由调用方给出。</summary>
    private static JsonObject BuildTunCore(JsonArray outbounds,
        int socksPort, int httpPort, string proxyMode, bool bypassPrivate,
        bool bypassCn, bool blockAds, bool allowLan, IReadOnlyList<RouteRule>? customRules, string logLevel, string dnsServer,
        bool gameDirectEnabled, IReadOnlyList<string>? gameDirectProcesses)
    {
        // 2026-10-22 TUN 性能评估（sing-box 1.14.2/1.14.3 官方文档；稳定优先，本轮未改运行参数）
        //   · stack=mixed：官方表格明确 mixed = system TCP + gvisor UDP，且 gvisor 构建下默认即 mixed；
        //     system 只是把 UDP 也交给系统栈，性能/兼容收益未实测 → 保持 mixed。
        //     https://github.com/SagerNet/sing-box/blob/v1.14.2/docs/configuration/inbound/tun.md#stack
        //   · mtu=9000：官方示例值，保持。
        //   · gso：1.11 起弃用且「对透明代理无优势」→ 不添加。
        //   · endpoint_independent_nat：1.11 起无效；1.14 起由 udp_mapping/udp_filtering 取代，
        //     且默认就是 endpoint_independent → 不添加无效字段。
        //   · tcp_fast_open：sing-box 用 tfo-go，Windows 在 Go 1.23+ 需 `tfogo_checklinkname0`
        //     构建标签，官方/本项目构建未开启 → Windows 上是无效字段 → 不添加。
        //   · tcp_multi_path：Go 的 MPTCP 仅在 Linux 实现（!linux 直接回退 TCP）→ 不添加。
        //   · multiplex：需服务端开启，且多条连接绑到一条 TCP 上会放大单点故障 → 待实测。
        //   · dns.optimistic（1.14 新，默认陈旧窗口 3d）：需实测再定 → 未启用（默认 DNS 缓存已开）。
        //   · route_exclude_address_set=["geoip-cn"]：可让国内 IP 绕开用户态栈，需真机验证 → 待实测。
        var tun = new JsonObject
        {
            ["type"] = "tun",
            ["tag"] = "tun-in",
            ["interface_name"] = "neo-tun",
            ["address"] = new JsonArray("172.19.0.1/30"),
            ["mtu"] = 9000,
            ["auto_route"] = true,
            ["strict_route"] = true,
            ["stack"] = "mixed",
        };

        return new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = MapLevel(logLevel), ["timestamp"] = true },
            ["dns"] = BuildDns(dnsServer),
            ["inbounds"] = new JsonArray(
                tun,
                BuildSocksInbound(socksPort, allowLan),
                BuildHttpInbound(httpPort, allowLan)),
            ["outbounds"] = outbounds,
            ["route"] = BuildRoute(proxyMode, bypassPrivate, bypassCn, blockAds, customRules, tunMode: true,
                gameDirect: gameDirectEnabled, gameProcesses: gameDirectProcesses),
            ["experimental"] = BuildExperimental(),
        };
    }

    public static string BuildTest(NodeItem node, int socksPort)
    {
        if (!node.IsValid) throw new ArgumentException("节点无效（缺地址或端口）");

        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "error", ["timestamp"] = false },
            ["dns"] = BuildDns("223.5.5.5", includeRuleSet: false),
            ["inbounds"] = new JsonArray(new JsonObject
            {
                ["type"] = "socks",
                ["tag"] = "socks-in",
                ["listen"] = "127.0.0.1",
                ["listen_port"] = socksPort,
            }),
            ["outbounds"] = BuildOutbounds(node),
            ["route"] = new JsonObject { ["default_domain_resolver"] = "dns-direct" },
        };
        ApplyWireGuardEndpoints(root, node);
        if (node.Protocol is NodeProtocol.WireGuard)
        {
            // WG 是 endpoint 而不是 outbound：测速配置必须显式把 final 指到它，否则流量走 direct。
            root["outbounds"] = new JsonArray(new JsonObject { ["type"] = "direct", ["tag"] = "direct" });
            root["route"] = new JsonObject
            {
                ["default_domain_resolver"] = "dns-direct",
                ["final"] = "proxy",
            };
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// WireGuard 用 sing-box 的 endpoint（1.11+）而不是 outbound：
    /// 把 endpoint 挂到 root.endpoints，tag 与普通节点一致用 "proxy"，路由/统计无需特殊处理。
    /// </summary>
    private static void ApplyWireGuardEndpoints(JsonObject root, NodeItem n)
    {
        if (n.Protocol is not NodeProtocol.WireGuard) return;
        root["endpoints"] = new JsonArray(BuildWireGuardEndpoint(n));
    }

    private static JsonObject BuildWireGuardEndpoint(NodeItem n)
    {
        var addresses = (n.WgAddress ?? "172.16.0.2/32")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(a => a.Length > 0)
            .Select(a => (JsonNode)a!)
            .ToArray();
        if (addresses.Length == 0) addresses = new JsonNode[] { "172.16.0.2/32" };

        var peer = new JsonObject
        {
            ["address"] = StripBrackets(n.Server),
            ["port"] = n.Port,
            ["public_key"] = n.WgPublicKey ?? "",
            ["allowed_ips"] = new JsonArray("0.0.0.0/0", "::/0"),
        };
        if (!string.IsNullOrEmpty(n.WgPresharedKey)) peer["pre_shared_key"] = n.WgPresharedKey;
        if (n.WgKeepAlive is > 0) peer["persistent_keepalive_interval"] = n.WgKeepAlive.Value;
        var reserved = ParseReserved(n.WgReserved);
        if (reserved != null) peer["reserved"] = reserved;

        var endpoint = new JsonObject
        {
            ["type"] = "wireguard",
            ["tag"] = "proxy",
            ["address"] = new JsonArray(addresses),
            ["private_key"] = n.Password ?? "",
            ["peers"] = new JsonArray(peer),
        };
        if (n.WgMtu is > 0) endpoint["mtu"] = n.WgMtu.Value;
        return endpoint;
    }

    /// <summary>reserved 支持 "0,0,0" 与 WARP 的三字节 base64 两种写法；解析失败返回 null（不写该字段）。</summary>
    private static JsonArray? ParseReserved(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (s.Contains(','))
        {
            var parts = s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var arr = new JsonArray();
            foreach (var part in parts)
            {
                if (!int.TryParse(part, out var v) || v < 0 || v > 255) return null;
                arr.Add(v);
            }
            return arr.Count == 3 ? arr : null;
        }
        try
        {
            var bytes = Convert.FromBase64String(s);
            if (bytes.Length != 3) return null;
            return new JsonArray(bytes[0], bytes[1], bytes[2]);
        }
        catch { return null; }
    }

    private static JsonObject BuildSocksInbound(int port, bool allowLan) => new()
    {
        ["type"] = "socks",
        ["tag"] = "socks-in",
        ["listen"] = allowLan ? "0.0.0.0" : "127.0.0.1",
        ["listen_port"] = port,
    };

    private static JsonObject BuildHttpInbound(int port, bool allowLan) => new()
    {
        ["type"] = "http",
        ["tag"] = "http-in",
        ["listen"] = allowLan ? "0.0.0.0" : "127.0.0.1",
        ["listen_port"] = port,
    };

    private static JsonArray BuildOutbounds(NodeItem n) => n.Protocol is NodeProtocol.WireGuard
        // WireGuard 走 endpoints（sing-box 1.11+），没有对应 outbound。
        ? new JsonArray(new JsonObject { ["type"] = "direct", ["tag"] = "direct" })
        : new JsonArray(
            BuildOutbound(n),
            new JsonObject { ["type"] = "direct", ["tag"] = "direct" });

    private static JsonObject BuildOutbound(NodeItem n)
    {
        var o = new JsonObject
        {
            ["tag"] = "proxy",
            // ★ 2026-10-23 IPv6 方括号：vless/trojan 的 uri.Host 会带 []（.NET 行为），写进 outbound 前统一剥掉。
            //   xray 侧同一问题由 ShareLinkParser/ClashYamlParser 在解析阶段剥（Xray 构建器不在本次写入范围）。
            ["server"] = StripBrackets(n.Server),
            ["server_port"] = n.Port,
            ["domain_resolver"] = "dns-direct",
        };

        switch (n.Protocol)
        {
            case NodeProtocol.VLESS:
                // ★ 2026-10-23 第二批：sing-box 1.14.3 的 vless 出站没有 encryption 字段
                //   （实测报错 json: unknown field "encryption"，台账 F1）→ 带 VLESS 后量子加密的节点
                //   绝不能静默生成一份「不带加密」的 outbound（那样能启动但握手必失败）。
                if (!string.IsNullOrEmpty(n.VlessEncryption) &&
                    !n.VlessEncryption.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    throw new NotSupportedException(
                        $"节点「{n.Name}」使用 VLESS 后量子加密，sing-box 1.14 不支持该字段，请改用 xray 内核（关闭 TUN）");
                }
                o["type"] = "vless";
                o["uuid"] = n.Uuid ?? "";
                if (!string.IsNullOrEmpty(n.Flow)) o["flow"] = n.Flow;
                ApplyTls(o, n);
                ApplyTransport(o, n);
                break;

            case NodeProtocol.VMess:
                o["type"] = "vmess";
                o["uuid"] = n.Uuid ?? "";
                o["alter_id"] = n.AlterId;
                o["security"] = string.IsNullOrEmpty(n.Cipher) ? "auto" : n.Cipher;
                ApplyTls(o, n);
                ApplyTransport(o, n);
                break;

            case NodeProtocol.Trojan:
                o["type"] = "trojan";
                o["password"] = n.Password ?? "";
                ApplyTls(o, n, forceTls: true);
                ApplyTransport(o, n);
                break;

            case NodeProtocol.Shadowsocks:
                o["type"] = "shadowsocks";
                o["method"] = n.Method ?? "aes-128-gcm";
                o["password"] = n.Password ?? "";
                // ★ 2026-10-23 ss-plugin 生效（审计 P2-17）：sing-box 1.14 的 shadowsocks 出站支持
                //   `plugin` / `plugin_opts` 两个字段（SIP003，如 obfs-local / v2ray-plugin / shadow-tls）。
                //   旧实现把 ss 的 plugin 只塞在 Extras["query"]、全仓无读取方 → 插件节点被静默降级成裸 SS
                //   （能导入、必连不上）。字段名已按 sing-box 1.14 文档核对（内核文档即此二字段）。
                //   注意：xray 不支持 SIP003 插件，xray 路径保持原样（这类节点需走 TUN/sing-box）。
                if (!string.IsNullOrEmpty(n.Plugin))
                {
                    o["plugin"] = n.Plugin;
                    if (!string.IsNullOrEmpty(n.PluginOpts)) o["plugin_opts"] = n.PluginOpts;
                }
                break;

            case NodeProtocol.Hysteria2:
                o["type"] = "hysteria2";
                o["password"] = n.Password ?? "";
                ApplyTls(o, n, forceTls: true);
                if (n.Extras.TryGetValue("obfs", out var obfs) && !string.IsNullOrEmpty(obfs))
                {
                    // ★ 2026-10-23 第二批：gecko 混淆。
                    //   sing-box 1.14.3 实测支持 obfs.type=gecko + min_packet_size/max_packet_size（台账 F1）；
                    //   旧实现只写 type=obfs 值，从不带分片大小 → 对端按 gecko 校验分片，必连不上。
                    var isGecko = obfs.Equals("gecko", StringComparison.OrdinalIgnoreCase)
                        || n.GeckoMinPacketSize.HasValue || n.GeckoMaxPacketSize.HasValue;
                    var ob = new JsonObject { ["type"] = isGecko ? "gecko" : obfs };
                    if (n.Extras.TryGetValue("obfs-password", out var obPwd) && !string.IsNullOrEmpty(obPwd))
                        ob["password"] = obPwd;
                    if (isGecko)
                    {
                        ob["min_packet_size"] = n.GeckoMinPacketSize is > 0 ? n.GeckoMinPacketSize.Value : 512;
                        ob["max_packet_size"] = n.GeckoMaxPacketSize is > 0 ? n.GeckoMaxPacketSize.Value : 1200;
                    }
                    o["obfs"] = ob;
                }
                // ★ 2026-10-23 hy2 端口跳跃（标准写法 host:443,5000-6000）：
                //   sing-box 1.14 的 hysteria2 出站字段是 `server_ports`（字符串数组），区间写成 "start:end"。
                //   优先用分享链接解析出的 server_ports；兼容旧的 mport=20000-30000 查询参数写法。
                var portsRaw = "";
                if (n.Extras.TryGetValue("server_ports", out var sp) && !string.IsNullOrWhiteSpace(sp)) portsRaw = sp;
                else if (n.Extras.TryGetValue("mport", out var mp) && !string.IsNullOrWhiteSpace(mp)) portsRaw = mp;
                var serverPorts = BuildServerPorts(portsRaw);
                if (serverPorts != null) o["server_ports"] = serverPorts;
                if (n.Extras.TryGetValue("up", out var up) && int.TryParse(up, out var upMbps)) o["up_mbps"] = upMbps;
                if (n.Extras.TryGetValue("down", out var down) && int.TryParse(down, out var downMbps)) o["down_mbps"] = downMbps;
                break;

            case NodeProtocol.AnyTls:
                // ★ 2026-10-23 第二批：anytls 出站（sing-box 1.10+，实测 1.14.3 接受；xray 无此协议）。
                o["type"] = "anytls";
                o["password"] = n.Password ?? "";
                ApplyTls(o, n, forceTls: true);
                break;

            case NodeProtocol.Socks:
                // ★ 2026-10-23 第二批：socks5 出站（socks:// 分享链接；sing-box 1.14 实测接受）。
                o["type"] = "socks";
                if (!string.IsNullOrEmpty(n.Username)) o["username"] = n.Username;
                if (!string.IsNullOrEmpty(n.Password)) o["password"] = n.Password;
                break;

            case NodeProtocol.TUIC:
                o["type"] = "tuic";
                o["uuid"] = n.Uuid ?? "";
                o["password"] = n.Password ?? "";
                if (n.Extras.TryGetValue("congestion_control", out var cc) && !string.IsNullOrEmpty(cc))
                    o["congestion_control"] = cc;
                if (n.Extras.TryGetValue("udp_relay_mode", out var urm) && !string.IsNullOrEmpty(urm))
                    o["udp_relay_mode"] = urm;
                ApplyTls(o, n, forceTls: true);
                break;

            default:
                throw new NotSupportedException($"协议 {n.Protocol} 无法生成 sing-box 配置");
        }

        return o;
    }

    private static void ApplyTls(JsonObject o, NodeItem n, bool forceTls = false)
    {
        var sec = (n.Security ?? "none").ToLowerInvariant();
        bool enabled = forceTls || sec is "tls" or "reality";
        if (!enabled) return;

        var tls = new JsonObject
        {
            ["enabled"] = true,
            ["server_name"] = n.Sni ?? n.HostHeader ?? StripBrackets(n.Server),
        };
        if (n.AllowInsecure) tls["insecure"] = true;

        //   曾判定「pinSHA256 是加强校验、insecure 是跳过校验，语义相反」并删除本行，
        //     无 insecure -> 000 失败，内核报 tls: failed to verify certificate:
        //                    x509: certificate signed by unknown authority
        //     有 insecure -> 204 成功（1.5s）
        //   原因：sing-box 1.14 并不支持 pinSHA256 字段（内核直接忽略它），
        //   而这类 hy2 节点的证书链无法被系统根验证。要让节点可用，只能放开校验。
        //   所以本行是**必要的工程 workaround**，不是语义错误 —— 保留。
        //   （真正该做的是把 pin 需求上报给内核 / 或改用一个支持 pin 的字段名，属后续议题。）
        if (n.Extras.ContainsKey("pinsha256")) tls["insecure"] = true;

        if (!string.IsNullOrEmpty(n.Fingerprint))
            tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = n.Fingerprint };

        if (sec == "reality" && !string.IsNullOrEmpty(n.PublicKey))
        {
            var reality = new JsonObject
            {
                ["enabled"] = true,
                ["public_key"] = n.PublicKey,
            };
            if (!string.IsNullOrEmpty(n.ShortId)) reality["short_id"] = n.ShortId;
            tls["reality"] = reality;
        }

        if (n.Extras.TryGetValue("alpn", out var alpn) && !string.IsNullOrEmpty(alpn))
        {
            var arr = new JsonArray();
            foreach (var a in alpn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                arr.Add(a);
            if (arr.Count > 0) tls["alpn"] = arr;
        }

        // ★ 2026-10-23 第二批：ECH（分享链接 ech= / Clash ech-opts）。
        //   sing-box 1.14.3 实测接受 tls.ech = { enabled, config: [PEM 块] }（台账 F1），
        //   PEM 包法按 sing-box 文档的 ECH 格式。
        //   带 "://" 的写法（用 DoH 查 ECH 配置）需要额外的 dns server 配置，本版不支持 → 跳过并维持原行为。
        if (!string.IsNullOrEmpty(n.EchConfigList) && !n.EchConfigList.Contains("://"))
        {
            tls["ech"] = new JsonObject
            {
                ["enabled"] = true,
                ["config"] = new JsonArray(
                    "-----BEGIN ECH CONFIGS-----\n" + n.EchConfigList + "\n-----END ECH CONFIGS-----"),
            };
        }

        o["tls"] = tls;
    }

    private static void ApplyTransport(JsonObject o, NodeItem n)
    {
        switch ((n.Network ?? "tcp").ToLowerInvariant())
        {
            case "ws":
                var ws = new JsonObject
                {
                    ["type"] = "ws",
                    ["path"] = string.IsNullOrEmpty(n.Path) ? "/" : n.Path,
                };
                if (!string.IsNullOrEmpty(n.HostHeader))
                {
                    var headers = new JsonObject { ["Host"] = n.HostHeader };
                    ws["headers"] = headers;
                }
                o["transport"] = ws;
                break;

            case "grpc":
                o["transport"] = new JsonObject
                {
                    ["type"] = "grpc",
                    ["service_name"] = n.ServiceName ?? "",
                };
                break;

            case "http":
            case "h2":
                var host = new JsonArray();
                host.Add(string.IsNullOrEmpty(n.HostHeader) ? StripBrackets(n.Server) : n.HostHeader);
                o["transport"] = new JsonObject
                {
                    ["type"] = "http",
                    ["host"] = host,
                    ["path"] = string.IsNullOrEmpty(n.Path) ? "/" : n.Path,
                };
                break;

            case "httpupgrade":
                // ★ 2026-10-23 第二批：HTTPUpgrade 传输（sing-box 实测接受，台账 F1）。
                var hu = new JsonObject
                {
                    ["type"] = "httpupgrade",
                    ["path"] = string.IsNullOrEmpty(n.Path) ? "/" : n.Path,
                };
                if (!string.IsNullOrEmpty(n.HostHeader)) hu["host"] = n.HostHeader;
                o["transport"] = hu;
                break;

            case "xhttp":
            case "kcp":
            case "mkcp":
                // ★ 2026-10-23 第二批：这两种传输只有 xray 支持（sing-box 1.14.3 实测：
                //   transport.type=xhttp → unknown transport type；mKCP 完全没有）。
                //   必须显式抛错，绝不能静默生成一份「走 tcp」的 outbound —— 那会表现为
                //   「TUN 下能连上但所有域名超时」，比直接报错难排查得多。
                throw new NotSupportedException(
                    $"节点「{n.Name}」的传输方式 {n.Network} 只有 xray 内核支持，sing-box 不支持；请关闭 TUN 再用该节点");
        }
    }

    //   · server 只能是**裸主机名或 IP**，绝不能带 scheme —— 原实现把 "https://1.1.1.1/dns-query"
    //     整串写进 server，内核把它当域名去解析，直接 FATAL：
    //     `initialize DNS server[0]: missing domain resolver for domain server address`
    //     （TUN / hy2 / tuic 路径整份配置起不来）；
    //   · DoT/DoH 的 path 是独立字段；
    //   · server 是域名时必须配 domain_resolver，指向一个 IP 型引导解析器（udp），否则同样 FATAL。
    private static JsonObject BuildDns(string dnsServer, bool includeRuleSet = true)
    {
        var dns = XrayConfigBuilder.NormalizeDnsServer(dnsServer);

        var type = "udp";
        var host = dns;
        int? port = null;
        string? path = null;
        var isDoH = false;

        if (dns.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(dns, UriKind.Absolute, out var dohUri) || dohUri.Host.Length == 0)
                throw new ArgumentException($"自定义 DoH 地址无效：{dns}");
            type = "https";
            isDoH = true;
            host = StripBrackets(dohUri.Host);
            if (!dohUri.IsDefaultPort) port = dohUri.Port;
            if (dohUri.AbsolutePath is { Length: > 1 } p && p != "/") path = p;
        }
        else if (dns.StartsWith("tls://", StringComparison.OrdinalIgnoreCase))
        {
            var raw = dns["tls://".Length..].Trim().TrimEnd('/');
            if (raw.Length == 0 || !Uri.TryCreate("tls://" + raw, UriKind.Absolute, out var dotUri) || dotUri.Host.Length == 0)
                throw new ArgumentException($"自定义 DoT 地址无效：{dns}");
            type = "tls";
            host = StripBrackets(dotUri.Host);
            if (!dotUri.IsDefaultPort) port = dotUri.Port;
        }

        // 域名解析服务器自身也要靠一个解析器引导：用纯 IP + udp，避免循环依赖；
        // 放在 servers 最前面（与探针 fix1 的顺序一致）。
        var servers = new JsonArray();
        JsonObject? bootstrap = null;
        if (!IsIpLiteral(host))
        {
            bootstrap = new JsonObject
            {
                ["type"] = "udp",
                ["tag"] = "dns-bootstrap",
                ["server"] = "223.5.5.5",
            };
            servers.Add(bootstrap);
        }

        var direct = new JsonObject { ["type"] = type, ["tag"] = "dns-direct", ["server"] = host };
        if (port.HasValue) direct["server_port"] = port.Value;
        if (path != null) direct["path"] = path;
        if (bootstrap != null) direct["domain_resolver"] = "dns-bootstrap";
        servers.Add(direct);

        JsonObject remote;
        if (isDoH)
        {
            remote = new JsonObject { ["type"] = "https", ["tag"] = "dns-remote", ["server"] = host, ["detour"] = "proxy" };
            if (port.HasValue) remote["server_port"] = port.Value;
            if (path != null) remote["path"] = path;
            if (bootstrap != null) remote["domain_resolver"] = "dns-bootstrap";
        }
        else
        {
            remote = new JsonObject { ["type"] = "https", ["tag"] = "dns-remote", ["server"] = "8.8.8.8", ["detour"] = "proxy" };
        }
        servers.Add(remote);

        var rules = new JsonArray(
            new JsonObject { ["ip_is_private"] = true, ["server"] = "dns-direct" });

        //   测速用的 BuildTest 不定义 route.rule_set；若这里仍引用，内核会在
        //   initialize dns router 阶段报 "rule-set not found: geosite-cn" 并 FATAL 退出 ——
        //   表现为「凡走 sing-box 的 hy2 / tuic 节点测速 100% 超时」。
        if (includeRuleSet)
            rules.Add(new JsonObject { ["rule_set"] = new JsonArray("geosite-cn"), ["server"] = "dns-direct" });

        // Windows 连通性探测走直连解析。
        // 不这样写，这些域名会落到 final=dns-remote（8.8.8.8 DoH，detour=proxy），
        rules.Add(new JsonObject
        {
            ["domain_suffix"] = new JsonArray(".msftconnecttest.com", ".msftncsi.com"),
            ["server"] = "dns-direct",
        });

        return new JsonObject
        {
            ["servers"] = servers,
            ["rules"] = rules,
            ["final"] = "dns-remote",
        };
    }

    private static bool IsIpLiteral(string host) => IPAddress.TryParse(StripBrackets(host), out _);

    private static string StripBrackets(string host) =>
        host.Length >= 2 && host[0] == '[' && host[^1] == ']' ? host[1..^1] : host;

    /// <summary>
    /// 把 "443,5000-6000" / "20000-30000" / "443,5000:6000" 归一化成 sing-box server_ports 数组
    /// （区间用 "start:end"，多个用逗号）；只有单个端口且无跳跃时返回 null（沿用 server_port，不重复写字段）。
    /// </summary>
    private static JsonArray? BuildServerPorts(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var arr = new JsonArray();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var segs = part.Replace('-', ':').Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segs.Length == 0 || !int.TryParse(segs[0], out var a) || a <= 0) continue;
            if (segs.Length >= 2 && int.TryParse(segs[1], out var b) && b > 0) arr.Add($"{a}:{b}");
            // ★ sing-box 的 server_ports 每项必须是 "start:end" 区间格式：
            //   实测（sing-box 1.14.2 check）写单端口 "443" 会 FATAL `bad port range: 443`，
            //   必须写成 "443:443"；这里把单端口也归一成同值区间。
            else arr.Add($"{a}:{a}");
        }
        if (arr.Count == 0) return null;
        var hasJump = raw.Contains(',') || raw.Contains('-') || raw.Contains(':') || arr.Count > 1;
        return hasJump ? arr : null;
    }

    private static JsonObject BuildRoute(string mode, bool bypassPrivate, bool bypassCn, bool blockAds,
        IReadOnlyList<RouteRule>? customRules, bool tunMode,
        bool gameDirect = false, IReadOnlyList<string>? gameProcesses = null)
    {
        var rules = new JsonArray();

        if (tunMode)
        {
            rules.Add(new JsonObject { ["action"] = "sniff" });
            rules.Add(new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" });
        }

        var final = mode switch
        {
            "direct" => "direct",
            _ => "proxy",
        };

        if (tunMode && gameDirect && gameProcesses is { Count: > 0 })
        {
            // 游戏进程直连：游戏流量（含 UDP）不经节点，防节点拒绝游戏流量
            rules.Add(new JsonObject
            {
                ["process_name"] = new JsonArray(gameProcesses.Select(p => (JsonNode?)p).ToArray()),
                ["outbound"] = "direct",
            });
        }

        if (mode == "rule")
        {
            if (customRules != null)
            {
                foreach (var r in customRules)
                {
                    var value = (r.Value ?? "").Trim().TrimStart('*').TrimStart('.');
                    if (value.Length == 0) continue;
                    var tag = r.Action switch { "proxy" => "proxy", "block" => "block", _ => "direct" };
                    var rule = new JsonObject();
                    if (r.Kind == "ip") rule["ip_cidr"] = new JsonArray(value);
                    else rule["domain_suffix"] = new JsonArray(value);

                    if (tag == "block") rule["action"] = "reject";
                    else rule["outbound"] = tag;
                    rules.Add(rule);
                }
            }

            if (blockAds)
                rules.Add(new JsonObject { ["rule_set"] = new JsonArray("geosite-ads"), ["action"] = "reject" });

            if (bypassPrivate)
                rules.Add(new JsonObject { ["ip_is_private"] = true, ["outbound"] = "direct" });

            // Windows 连通性探测（NCSI）必须走直连 —— 与 xray 侧同一规则。
            // 原因见 XrayConfigBuilder.BuildRoutingRules 的注释：这些域名不在 CN 清单里，
            // 落进 final(proxy) 后探测会失败或慢 2~3 倍。
            rules.Add(new JsonObject
            {
                ["domain_suffix"] = new JsonArray(".msftconnecttest.com", ".msftncsi.com"),
                ["outbound"] = "direct",
            });

            // Steam 连通性探测 + 内容 CDN 直连（与 xray 侧同一规则）。
            // 日志里反复出现 ipv6check-http.steamserver.net EOF 就是被塞进代理导致的；
            // 商店/社区（steampowered.com / steamcommunity.com）仍走代理，不放这里。
            rules.Add(new JsonObject
            {
                ["domain_suffix"] = new JsonArray(".steamserver.net", ".steamcontent.com"),
                ["outbound"] = "direct",
            });

            if (bypassCn)
                rules.Add(new JsonObject
                {
                    ["rule_set"] = new JsonArray("geosite-cn", "geoip-cn"),
                    ["outbound"] = "direct",
                });
        }

        var route = new JsonObject
        {
            ["rules"] = rules,
            ["final"] = final,
            ["auto_detect_interface"] = true,
            ["default_domain_resolver"] = "dns-direct",
        };

        //   原先 rule_set 的**定义**条件（mode==rule && (blockAds||bypassCn)）与
        //   **引用**条件（BuildDns 的 dns.rules 在 includeRuleSet=true 时无条件引用 geosite-cn）
        //   是两条独立条件 —— 于是当 bypassCn=false 或 mode=direct/global 时，
        //   dns 引用了未定义的 rule_set，内核启动即 FATAL：
        //     initialize dns router: dns rule[1]: rule-set not found: geosite-cn
        //   全部 check exit=1；而产品路径会把 AppState.BypassCn / ProxyMode 原样透传，
        //   所以用户一关「绕过大陆」或切直连/全局模式就会连不上（TUN 或 hy2/tuic 节点）。
        //   修法：**无条件定义**这三个 rule_set（定义总是安全的 —— 只是多加载几个 .srs 文件，
        //   不使用的规则集不会被路由器命中）。这样无论 bypassCn / blockAds / mode 怎么组合，
        //   dns 的引用都有对应定义，彻底消除「引用与定义不一致」。
        //   代价：direct/global 模式下会多加载 geosite-ads+geosite-cn+geoip-cn 三个文件
        //   （合计约 558 KB，本地文件读取，毫秒级），换来的是不会再出现必然 FATAL。
        {
            var sets = new JsonArray();
            sets.Add(LocalRuleSet("geosite-ads", AbsRuleSet(RuleSetAds)));
            sets.Add(LocalRuleSet("geosite-cn", AbsRuleSet(RuleSetCn)));
            sets.Add(LocalRuleSet("geoip-cn", AbsRuleSet(RuleSetGeoipCn)));
            route["rule_set"] = sets;
        }

        return route;
    }

    private static string AbsRuleSet(string rel) =>
        Path.Combine(NeoPaths.RuntimeDir, rel.Replace('/', Path.DirectorySeparatorChar));

    private static JsonObject LocalRuleSet(string tag, string path) => new()
    {
        ["tag"] = tag,
        ["type"] = "local",
        ["format"] = "binary",
        ["path"] = path,
    };

    private static string MapLevel(string level) => level switch
    {
        "none" => "error",
        "error" => "error",
        "warning" => "warn",
        "info" => "info",
        "debug" => "debug",
        _ => "warn",
    };

    private static JsonObject BuildExperimental() => new()
    {
        ["clash_api"] = new JsonObject
        {
            ["external_controller"] = $"127.0.0.1:{ClashApiPort}",
            ["secret"] = ClashApiSecret,
        },
    };
}
