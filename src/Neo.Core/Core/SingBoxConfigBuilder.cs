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

    public const int ClashApiPort = 10086;
    public const string ClashApiSecret = "neo-local-stats";

    /// balancer 的 URLTest 目标。**必须显式指定**，否则内核用默认的
    /// `https://www.gstatic.com/generate_204`（每次含端到端 TLS 握手，约 32KB）。
    ///   URLTest 只需要"能否拿到有效响应 + 往返耗时"来给成员排延迟，
    ///   不依赖 TLS 语义；而它的开销随**成员数 × 频率**线性增长，
    ///   加权模式下成员可达 26 份，所以单次成本下降会直接放大成月度节省。
    /// 字段名 `url` 已对补丁源码确认（`link: options.URL`，balancer.go:118）。
    public const string ProbeUrlForBalancer = "http://cp.cloudflare.com/generate_204";

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
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// TUN + 多节点并发。需要**自编译的 sing-box**：官方版没有 balancer 出站
    /// neo 使用的自编译版（1.14.x + protocol/group/balancer.go 补丁）支持它。
    /// 加权做法与 xray 侧完全同构：给同一节点建**多个不同 tag** 的出站，
    /// 每个 tag 在 balancer 的 outbounds 列表里占一份。
    /// 与 xray 唯一的差别是内核实现方式：
    ///   xray     —— selector 经 outbound.Manager.Select() 会**去重**，故同一 tag 写多遍无效。
    ///   sing-box —— Balancer.Start() 直接遍历 tags 依次 Outbound(tag) 收集，**不去重**。
    ///               这里仍用不同 tag，保持与 xray 侧相同的构造方式，便于两处行为一致。
    /// 策略用 roundRobin：补丁的 balancer 只有 roundRobin / leastPing / random，
    /// 其中 leastPing 是「选延迟最低的唯一一个」= 单节点（假并发），
    /// roundRobin 才是在**健康成员**间依次轮转 —— 而「份额」正是通过让快节点在
    /// 成员列表里出现更多次来放大其被轮到的概率，从而多吃流量。
    public static string BuildTunMulti(IReadOnlyList<NodeItem> nodes, IReadOnlyList<int>? delaysMs,
        int socksPort, int httpPort, string proxyMode, bool bypassPrivate,
        bool bypassCn, bool blockAds, bool allowLan, IReadOnlyList<RouteRule>? customRules, string logLevel, string dnsServer,
        bool gameDirectEnabled = false, IReadOnlyList<string>? gameDirectProcesses = null,
        IReadOnlyList<int>? speedsKbps = null)
    {
        var valid = nodes.Where(n => n.IsValid).ToList();
        if (valid.Count == 0) throw new ArgumentException("TUN 多节点：没有可用节点");
        if (valid.Count == 1)
            return BuildTun(valid[0], socksPort, httpPort, proxyMode, bypassPrivate, bypassCn, blockAds,
                allowLan, customRules, logLevel, dnsServer, gameDirectEnabled, gameDirectProcesses);

        var weights = ComputeWeights(valid.Count, delaysMs, speedsKbps);
        var members = new List<string>();
        var outbounds = new JsonArray();
        for (var i = 0; i < valid.Count; i++)
        {
            var mainTag = $"proxy{i}";
            var ob = BuildOutbound(valid[i]);
            ob["tag"] = mainTag;
            outbounds.Add(ob);
            members.Add(mainTag);
            for (var k = 1; k < weights[i]; k++)
            {
                var t = $"w{i}{(char)('b' + k - 1)}";
                var extra = BuildOutbound(valid[i]);
                extra["tag"] = t;
                outbounds.Add(extra);
                members.Add(t);
            }
        }
        outbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = "direct" });

        //   `if s.poolSize > 0 && len(alive) > s.poolSize { alive = alive[:s.poolSize] }`
        // 而 `C.BalancerDefaultPoolSize = 3`（constant/timeout.go:33）。
        // 不设时池只留「最快 3 个」，与用户「所有节点一起工作」的要求直接冲突：
        // 设为成员总数 = 关闭这层截断，让全部份额成员都参与轮转。
        // 加权仍有效：份额多的节点在列表里出现次数多，轮到的概率就高。
        // 仅当用户开启「游戏直连保护」并配了进程名单时，才改为**只对游戏进程粘滞**。
        // 补丁的 stickyKeys 默认值是 `[process, domain]`（balancer.go:167），
        // 且 stickyDisabled() 只在列表**含 "none" 或为空**时才关闭粘滞 ——
        // 默认值两者都不是，于是粘滞默认**开启**。
        // 后果：同一进程访问同一域名时 stickyKeyFor 恒返回同一个键
        //   ⇒ 那批连接全被绑到第一次选中的那个节点。
        //   clash_api 采样 32 次**全部是 proxy0**，池里其余 11 份一次没轮到。
        // 这恰好就是用户抱怨的「不是真并发」。
        // 两个必须避开的陷阱：
        // ① 不能「配了 sticky_processes 却不配 sticky_key」——白名单外的流量不粘滞，
        //    白名单内的仍按 sticky_key 粘滞；若 sticky_key 留默认 [process,domain]，
        //    游戏的不同域名连接仍会各自粘到不同节点。游戏要的是「一个进程的所有连接
        //    固定同一出口」，故游戏场景用 ["process"]。
        // ② 进程名单为空时**绝不能传空数组**：那样 stickyDisabled() 返回 false
        var balancerObj = new JsonObject
        {
            ["type"] = "balancer",
            ["tag"] = "proxy",
            ["outbounds"] = new JsonArray(members.Select(m => (JsonNode)m!).ToArray()),
            // 策略名必须是补丁里定义的 roundRobin（写成 round-robin 会报 unknown balancer strategy）。
            ["strategy"] = "roundRobin",
            ["pool_size"] = members.Count,
            //   `https://www.gstatic.com/generate_204`（每次含端到端 TLS 握手，约 32KB）。
            //   只需要"能否拿到有效响应"来测延迟，不依赖 TLS 语义。
            ["url"] = ProbeUrlForBalancer,
            // 探活间隔与 idle_timeout：Interval 必须 ≤ IdleTimeout，否则内核拒绝启动。
            //   「成员数 × 频率」的理论值；但仍随成员数线性增长（加权模式可达 26 份），
            //   放宽到 90s 能再省一半，且 90s 对"发现死节点"仍够快。
            ["interval"] = "90s",
            ["idle_timeout"] = "30m",
        };

        if (gameDirectEnabled && gameDirectProcesses is { Count: > 0 })
        {
            balancerObj["sticky_key"] = new JsonArray("process");
            balancerObj["sticky_processes"] = new JsonArray(
                gameDirectProcesses.Where(p => !string.IsNullOrWhiteSpace(p))
                                    .Select(p => (JsonNode)p!).ToArray());
            // 粘滞表项存活时长：覆盖一局游戏时长，避免中途换出口。
            balancerObj["sticky_timeout"] = "30m";
        }
        else
        {
            balancerObj["sticky_key"] = new JsonArray("none");
        }

        outbounds.Add(balancerObj);

        var root = BuildTunCore(outbounds, socksPort, httpPort, proxyMode, bypassPrivate,
            bypassCn, blockAds, allowLan, customRules, logLevel, dnsServer,
            gameDirectEnabled, gameDirectProcesses);
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// 加权份额。与 XrayConfigBuilder.ComputeWeights 同一套规则（快节点份额多、慢节点不为 0），
    /// 刻意保持一致，避免两处并发行为不一致让用户困惑。
    private static int[] ComputeWeights(int count, IReadOnlyList<int>? delaysMs,
        IReadOnlyList<int>? speedsKbps = null)
    {
        const int MaxWeightShares = 6;
        const int MinWeightShares = 1;
        var w = new int[count];

        var delayOk = delaysMs != null && delaysMs.Count >= count;
        if (speedsKbps != null && speedsKbps.Count >= count)
        {
            var positiveSpeed = new List<int>();
            for (var i = 0; i < count; i++) if (speedsKbps[i] > 0) positiveSpeed.Add(speedsKbps[i]);
            if (positiveSpeed.Count > 0)
            {
                var bestSpeed = positiveSpeed.Max();
                for (var i = 0; i < count; i++)
                {
                    var s = speedsKbps[i];
                    var reachable = !delayOk || delaysMs![i] > 0;
                    if (s <= 0 || !reachable) { w[i] = MinWeightShares; continue; }
                    var shares = (int)Math.Round(MaxWeightShares * (double)s / bestSpeed, MidpointRounding.AwayFromZero);
                    w[i] = Math.Clamp(shares, MinWeightShares, MaxWeightShares);
                }
                return w;
            }
        }

        // 没有吞吐数据 → 退回按延迟分份额（每节点至少 1 份）
        if (delaysMs == null || delaysMs.Count < count)
        {
            for (var i = 0; i < count; i++) w[i] = 1;
            return w;
        }
        var positive = new List<int>();
        for (var i = 0; i < count; i++) if (delaysMs[i] > 0) positive.Add(delaysMs[i]);
        if (positive.Count == 0)
        {
            for (var i = 0; i < count; i++) w[i] = 1;
            return w;
        }
        var best = positive.Min();
        for (var i = 0; i < count; i++)
        {
            var d = delaysMs[i];
            if (d <= 0) { w[i] = Math.Max(MinWeightShares, MaxWeightShares / 2); continue; }
            var shares = (int)Math.Round(MaxWeightShares * (double)best / d, MidpointRounding.AwayFromZero);
            w[i] = Math.Clamp(shares, MinWeightShares, MaxWeightShares);
        }
        return w;
    }

    /// <summary>构造 TUN 配置主体，出站数组由调用方给出（单节点或多节点已组装好）。</summary>
    private static JsonObject BuildTunCore(JsonArray outbounds,
        int socksPort, int httpPort, string proxyMode, bool bypassPrivate,
        bool bypassCn, bool blockAds, bool allowLan, IReadOnlyList<RouteRule>? customRules, string logLevel, string dnsServer,
        bool gameDirectEnabled, IReadOnlyList<string>? gameDirectProcesses)
    {
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
            ["outbounds"] = new JsonArray(
                BuildOutbound(node),
                new JsonObject { ["type"] = "direct", ["tag"] = "direct" }),
            ["route"] = new JsonObject { ["default_domain_resolver"] = "dns-direct" },
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
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

    private static JsonArray BuildOutbounds(NodeItem n) => new(
        BuildOutbound(n),
        new JsonObject { ["type"] = "direct", ["tag"] = "direct" });

    private static JsonObject BuildOutbound(NodeItem n)
    {
        var o = new JsonObject
        {
            ["tag"] = "proxy",
            ["server"] = n.Server,
            ["server_port"] = n.Port,
            ["domain_resolver"] = "dns-direct",
        };

        switch (n.Protocol)
        {
            case NodeProtocol.VLESS:
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
                break;

            case NodeProtocol.Hysteria2:
                o["type"] = "hysteria2";
                o["password"] = n.Password ?? "";
                ApplyTls(o, n, forceTls: true);
                if (n.Extras.TryGetValue("obfs", out var obfs) && !string.IsNullOrEmpty(obfs))
                {
                    var ob = new JsonObject { ["type"] = obfs };
                    if (n.Extras.TryGetValue("obfs-password", out var obPwd) && !string.IsNullOrEmpty(obPwd))
                        ob["password"] = obPwd;
                    o["obfs"] = ob;
                }
                if (n.Extras.TryGetValue("mport", out var mport) && mport.Contains('-'))
                {
                    var parts = mport.Split('-', 2);
                    o["server_ports"] = new JsonArray($"{parts[0].Trim()}:{parts[1].Trim()}");
                }
                if (n.Extras.TryGetValue("up", out var up) && int.TryParse(up, out var upMbps)) o["up_mbps"] = upMbps;
                if (n.Extras.TryGetValue("down", out var down) && int.TryParse(down, out var downMbps)) o["down_mbps"] = downMbps;
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
            ["server_name"] = n.Sni ?? n.HostHeader ?? n.Server,
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
                host.Add(n.HostHeader ?? n.Server);
                o["transport"] = new JsonObject
                {
                    ["type"] = "http",
                    ["host"] = host,
                    ["path"] = string.IsNullOrEmpty(n.Path) ? "/" : n.Path,
                };
                break;
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
