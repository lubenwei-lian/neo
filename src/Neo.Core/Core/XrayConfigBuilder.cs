using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Neo.Core.Models;

namespace Neo.Core.Core;

public static class XrayConfigBuilder
{
    // 2026-10-22 内核性能调整清单（稳定优先；未改动的项附官方依据）
    //   已落地：
    //     1) 关闭 policy 的 user 级流量统计（App 只读 outbound>>>/inbound>>>，见 XrayStats.cs）
    //     2) routing 规则重排：把系统内置的「IP 条件」规则整体移到「域名条件」规则之后
    //        —— 配合现有 domainStrategy=IPOnDemand，命中域名规则的连接不再先做一次本地 DNS 解析
    //        依据：https://xtls.github.io/config/routing.html（IPOnDemand 语义）
    //   评估后明确不采用：
    //     · sockopt.tcpFastOpen：官方注明 Windows 10 1607 起「实现不正确」→ 不启用
    //       https://xtls.github.io/config/transports/sockopt.html
    //     · sockopt.tcpcongestion / tcpMptcp / mark / tcpWindowClamp：仅 Linux
    //     · sockopt.bufferSize：桌面默认已是 512KB，调大属内存换吞吐且收益未实测 → 不动
    //     · mux：官方明确「减少握手延迟但不提高吞吐，看视频/下载/测速通常反效果」→ 不动
    //       https://xtls.github.io/config/outbound.html
    //     · sniffing.routeOnly：会把客户端（可能被污染的）DNS 结果发给服务端 → 风险高于收益
    //     · dns.serveStale：默认永不淘汰的陈旧记录可能导致连接失败 → 不动
    //   待实测（见 planning\2026-10-22-稳定路线与测速改造\子报告-内核性能调优.md）：mux、bufferSize、dns 乐观缓存

    // ★ 2026-10-23 改默认端口：10808/10809 是同类客户端最常见的默认值，
    //   同时装两个客户端必然抢端口（实测 v2rayN 正在运行 → neo 连接被拒「10808 被占用」）。
    //   换成 21xxx 段是为了「不与现有代理软件冲突」，不是随机值：
    //   · 21xxx 没有主流代理客户端的默认占用（1080/1087/7890–7897/10808–10810/20171 全部避开）
    //   · 四个端口连续（socks/http/stats/clash-api）便于记忆与防火墙放行
    public const int DefaultSocksPort = 21808;
    public const int DefaultHttpPort = 21809;

    /// xray 统计 API 端口（仅本机回环）。
    /// 2026-10-23 无感切换：内核移到内部端口后，统计 API 也跟着移到 CoreStatsApi（21910），
    /// 否则 XrayStats 查 21910、内核监听 21810，流量统计会一直取不到数（日志表现为读 stdout 超时）。
    public const int StatsApiPort = 21810;

    /// <summary>内核实际监听统计 API 的端口（用户端口已被中继占用，统计端口顺延到 +100）。</summary>
    public static int CoreStatsPort => LocalPorts.CoreStatsApi;

    /// observatory（leastLoad/leastPing 的延迟与存活探测）所用目标。
    ///   observatory 每个探测周期会把**全部出站**各探一次，单次含 TLS 握手约 32 KB。
    ///   换成明文 HTTP 后省掉端到端 TLS 握手，单次成本大幅下降；
    ///   而 observatory 只需"能否拿到有效 HTTP 响应"来判定存活与测延迟，不依赖 TLS。
    /// 选 cloudflare 的 generate_204：国内可达性相对稳、响应体为 0 字节。
    public const string ProbeUrlForObservatory = "http://cp.cloudflare.com/generate_204";

    public static string Build(NodeItem node, int socksPort = DefaultSocksPort, int httpPort = DefaultHttpPort,
        string proxyMode = "rule", bool bypassPrivate = true, bool bypassCn = true, bool blockAds = true,
        bool allowLan = false, IReadOnlyList<RouteRule>? customRules = null, string logLevel = "warning",
        string dnsServer = "223.5.5.5", int? statsPort = null)
    {
        if (!node.IsValid) throw new ArgumentException("节点无效（缺地址或端口）");
        if (socksPort == httpPort) throw new ArgumentException("socks 与 http 端口不能相同（xray 无混合入站）");

        var listen = allowLan ? "0.0.0.0" : "127.0.0.1";
        var level = logLevel is "none" or "error" or "warning" or "info" or "debug" ? logLevel : "warning";
        var dns = NormalizeDnsServer(dnsServer);

        var root = new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["loglevel"] = level, ["access"] = "none",
            },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray(dns),
            },
            ["api"] = new JsonObject
            {
                ["tag"] = "api",
                ["services"] = new JsonArray("StatsService"),
            },
            ["stats"] = new JsonObject(),
            ["policy"] = new JsonObject
            {
                // ★ 2026-10-22 性能调整：关闭「每用户」流量统计（statsUserUplink/Downlink）。
                //   依据：xray 官方 policy 文档 https://xtls.github.io/config/policy.html
                //   该项官方默认 false，此前被显式打开；但 App 的统计（XrayStats.cs）只解析
                //   outbound>>>.../inbound>>>... 计数器，从不读取 user>>>...，打开它只是给每条连接
                //   多加两份原子计数与内存。system 级入站/出站统计保持不变（App 依赖）。
                ["levels"] = new JsonObject { ["0"] = new JsonObject { ["statsUserUplink"] = false, ["statsUserDownlink"] = false } },
                ["system"] = new JsonObject { ["statsInboundUplink"] = true, ["statsInboundDownlink"] = true, ["statsOutboundUplink"] = true, ["statsOutboundDownlink"] = true },
            },
            ["inbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["listen"] = "127.0.0.1",
                    ["port"] = statsPort ?? CoreStatsPort,
                    ["protocol"] = "dokodemo-door",
                    ["settings"] = new JsonObject { ["address"] = "127.0.0.1" },
                    ["tag"] = "api-in",
                },
                new JsonObject
                {
                    ["tag"] = "socks-in",
                    ["listen"] = listen,
                    ["port"] = socksPort,
                    ["protocol"] = "socks",
                    ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = true },
                    ["sniffing"] = new JsonObject
                    {
                        ["enabled"] = true,
                        ["destOverride"] = new JsonArray("http", "tls"),
                    },
                },
                new JsonObject
                {
                    ["tag"] = "http-in",
                    ["listen"] = listen,
                    ["port"] = httpPort,
                    ["protocol"] = "http",
                    ["sniffing"] = new JsonObject
                    {
                        ["enabled"] = true,
                        ["destOverride"] = new JsonArray("http", "tls"),
                    },
                },
            },
            ["outbounds"] = new JsonArray
            {
                BuildProxyOutbound(node),
                new JsonObject { ["tag"] = "direct", ["protocol"] = "freedom" },
                new JsonObject { ["tag"] = "block", ["protocol"] = "blackhole" },
                new JsonObject { ["tag"] = "api", ["protocol"] = "freedom" },
            },
            ["routing"] = new JsonObject
            {
                // ★ 2026-10-22 关键修复：改为 AsIs（只按域名路由，绝不为路由去解析域名）。
    //   真实故障现场（23:17–23:29 实测）：x.com / google / youtube / steam 全部 10–15 秒超时，
    //   而同一节点用「不解析域名」的最小配置跑 x.com 200（4.5s）、gstatic 204 —— 节点本身是好的。
    //   根因：IPOnDemand 会在遇到 ip 条件规则时用**国内 DNS（223.5.5.5）**解析被墙域名，
    //   拿回的是污染地址（实测 abs.twimg.com→108.160.165.9、api.telegram.org→31.13.94.10、
    //   x.com 里混进 204.74.111.101），随后代理被要求去连这个假 IP ⇒ 必然超时/卡死。
    //   AsIs：带域名的连接完全不做本地解析（域名原样交给代理，由节点侧解析）；
    //   只有本来就是 IP 目标的连接才走 ip 规则（geoip:cn 直连能力保留）。
    //   代价：域名不在 geosite:cn 但托管在国内的站点会走代理（多花一点流量，换来稳定）。
                //   AsIs 下 geoip:cn 规则对「域名目标」永远不生效，于是 geosite:cn 之外的
                //   国内站点会全部走代理（实测 realme.com 就是这样，用户感知「国内站很慢」）。
                //   改成 IPOnDemand 后：命中 geosite:cn 的照旧直连，没命中的按解析出的 CN IP 直连，
                //   国外域名解析后仍落 balancer 走代理（已实测 google/youtube/chatgpt 不受影响）。
                // AsIs：不解析域名（见方法头注释「关键修复」），避免被污染 DNS 带偏。
            ["domainStrategy"] = "AsIs",
                ["rules"] = BuildRoutingRules(proxyMode, bypassPrivate, bypassCn, blockAds, customRules),
            },
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static string BuildTest(NodeItem node, int socksPort)
    {
        if (!node.IsValid) throw new ArgumentException("节点无效（缺地址或端口）");

        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "warning", ["access"] = "none" },
            ["inbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["tag"] = "socks-in",
                    ["listen"] = "127.0.0.1",
                    ["port"] = socksPort,
                    ["protocol"] = "socks",
                    ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = false },
                },
            },
            ["outbounds"] = new JsonArray
            {
                BuildProxyOutbound(node),
                new JsonObject { ["tag"] = "direct", ["protocol"] = "freedom" },
                new JsonObject { ["tag"] = "block", ["protocol"] = "blackhole" },
            },
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// 批量测速配置（单实例批量）：一个实例承载全部节点，每节点独立 socks 入站端口，路由按入站 tag 分流到对应出站。
    public static string BuildTestBatch(IReadOnlyList<NodeItem> nodes, IReadOnlyList<int> ports)
    {
        var inbounds = new JsonArray();
        var outbounds = new JsonArray
        {
            new JsonObject { ["tag"] = "direct", ["protocol"] = "freedom" },
            new JsonObject { ["tag"] = "block", ["protocol"] = "blackhole" },
        };
        var routingRules = new JsonArray();

        for (var i = 0; i < nodes.Count; i++)
        {
            if (!nodes[i].IsValid) continue;
            var inTag = $"in{i}";
            var outTag = $"out{i}";
            inbounds.Add(new JsonObject
            {
                ["tag"] = inTag,
                ["listen"] = "127.0.0.1",
                ["port"] = ports[i],
                ["protocol"] = "socks",
                ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = false },
            });
            var ob = BuildProxyOutbound(nodes[i]);
            ob["tag"] = outTag;
            outbounds.Add(ob);
            routingRules.Add(new JsonObject
            {
                ["type"] = "field",
                ["inboundTag"] = new JsonArray(inTag),
                ["outboundTag"] = outTag,
            });
        }

        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "warning", ["access"] = "none" },
            ["inbounds"] = inbounds,
            ["outbounds"] = outbounds,
            ["routing"] = new JsonObject
            {
                ["domainStrategy"] = "AsIs",
                ["rules"] = routingRules,
            },
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// 该节点能否作为「固定出口」。
    /// 依据实测（2026-10-22，xray 26.10.17）：REALITY 不能作为**内层**链路（入口→出口），
    ///   会报 REALITY: received real certificate (potential MITM or redirection)，
    ///   重试几次后连接被重置；ws/tls 这类普通 TLS 传输可以正常串联。
    /// 另外 h2/quic 传输已被 xray 26 移除，不能用于任何出站。
    public static bool CanBeFixedExit(NodeItem n) =>
        n.IsValid
        && !string.Equals(n.Security, "reality", StringComparison.OrdinalIgnoreCase)
        && n.Network is not ("h2" or "quic")
        // ★ 2026-10-23 第二批：链式出站只有 xray 跑；必须 sing-box 的节点（ss-plugin/明文 vless 等）不能当 chain
        && !n.RequiresSingBox
        && n.Protocol is NodeProtocol.VMess or NodeProtocol.VLESS or NodeProtocol.Trojan or NodeProtocol.Shadowsocks;

    /// 固定出口（多入口加速）配置：出口固定为一个节点，其他节点作为入口链路。
    /// 链路 = 客户端 → hop{i}(入口) → chain{i}(出口) → 目标。
    /// 所有 chain 的出口是同一个节点 ⇒ 对端网站看到的出口 IP 固定；
    /// balancer 在 chain 之间分摊连接 ⇒ 多入口的带宽可以叠加（单条 TCP 仍只走一条链）。
    /// 代价：流量要在入口与出口各走一遍（同一订阅可能按两次计费），并多一跳延迟。
    public static string BuildFixedExit(NodeItem exitNode, IReadOnlyList<NodeItem> entryNodes,
        int socksPort = DefaultSocksPort, int httpPort = DefaultHttpPort,
        string proxyMode = "rule", bool bypassPrivate = true, bool bypassCn = true, bool blockAds = true,
        bool allowLan = false, IReadOnlyList<RouteRule>? customRules = null, string logLevel = "warning",
        string dnsServer = "223.5.5.5", string strategy = "leastLoad", int? statsPort = null)
    {
        var entries = entryNodes.Where(n => n.IsValid).ToList();
        if (!CanBeFixedExit(exitNode))
            throw new ArgumentException("节点「" + exitNode.Name + "」不能作为固定出口（REALITY 或已被移除的传输方式不支持串联）");
        if (entries.Count == 0) throw new ArgumentException("固定出口：没有可用的入口节点");
        if (socksPort == httpPort) throw new ArgumentException("socks 与 http 端口不能相同（xray 无混合入站）");

        var listen = allowLan ? "0.0.0.0" : "127.0.0.1";
        var level = logLevel is "none" or "error" or "warning" or "info" or "debug" ? logLevel : "warning";
        var dns = NormalizeDnsServer(dnsServer);
        strategy = NormalizeStrategy(strategy);

        var outbounds = new JsonArray();
        var tags = new List<string>();

        for (var i = 0; i < entries.Count; i++)
        {
            var hopTag = "hop" + i;
            var hop = BuildProxyOutbound(entries[i]);
            hop["tag"] = hopTag;
            outbounds.Add(hop);

            var chainTag = "chain" + i;
            var chain = BuildProxyOutbound(exitNode);
            chain["tag"] = chainTag;
            if (chain["streamSettings"] is JsonObject stream)
            {
                stream["sockopt"] = new JsonObject { ["dialerProxy"] = hopTag };
            }
            outbounds.Add(chain);
            tags.Add(chainTag);
        }

        outbounds.Add(new JsonObject { ["tag"] = "direct", ["protocol"] = "freedom" });
        outbounds.Add(new JsonObject { ["tag"] = "block", ["protocol"] = "blackhole" });
        outbounds.Add(new JsonObject { ["tag"] = "api", ["protocol"] = "freedom" });

        var rules = BuildRoutingRules(proxyMode, bypassPrivate, bypassCn, blockAds, customRules, proxyTag: "bal");
        if (proxyMode != "direct")
        {
            rules.Add(new JsonObject
            {
                ["type"] = "field",
                ["network"] = "tcp,udp",
                ["balancerTag"] = "bal",
            });
        }

        var strategyObj = new JsonObject { ["type"] = strategy };
        if (strategy is "leastLoad")
        {
            strategyObj["settings"] = new JsonObject
            {
                ["expected"] = tags.Count,
                ["tolerance"] = 0.3,
            };
        }

        var balancer = new JsonObject
        {
            ["tag"] = "bal",
            ["selector"] = new JsonArray(tags.Select(t => (JsonNode)t!).ToArray()),
            ["strategy"] = strategyObj,
            ["fallbackTag"] = tags[0],
        };

        var routing = new JsonObject
        {
            // AsIs：不解析域名（见方法头注释「关键修复」），避免被污染 DNS 带偏。
            ["domainStrategy"] = "AsIs",
            ["balancers"] = new JsonArray(balancer),
            ["rules"] = rules,
        };

        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = level },
            ["dns"] = new JsonObject { ["servers"] = new JsonArray(dns) },
            ["api"] = new JsonObject { ["tag"] = "api", ["services"] = new JsonArray("StatsService") },
            ["stats"] = new JsonObject(),
            ["policy"] = new JsonObject
            {
                // ★ 2026-10-22 性能调整：关闭「每用户」流量统计（statsUserUplink/Downlink）。
                //   依据：xray 官方 policy 文档 https://xtls.github.io/config/policy.html
                //   该项官方默认 false，此前被显式打开；但 App 的统计（XrayStats.cs）只解析
                //   outbound>>>.../inbound>>>... 计数器，从不读取 user>>>...，打开它只是给每条连接
                //   多加两份原子计数与内存。system 级入站/出站统计保持不变（App 依赖）。
                ["levels"] = new JsonObject { ["0"] = new JsonObject { ["statsUserUplink"] = false, ["statsUserDownlink"] = false } },
                ["system"] = new JsonObject { ["statsInboundUplink"] = true, ["statsInboundDownlink"] = true, ["statsOutboundUplink"] = true, ["statsOutboundDownlink"] = true },
            },
            ["inbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["listen"] = "127.0.0.1",
                    ["port"] = statsPort ?? CoreStatsPort,
                    ["protocol"] = "dokodemo-door",
                    ["settings"] = new JsonObject { ["address"] = "127.0.0.1" },
                    ["tag"] = "api-in",
                },
                new JsonObject
                {
                    ["tag"] = "socks-in",
                    ["listen"] = listen,
                    ["port"] = socksPort,
                    ["protocol"] = "socks",
                    ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = true },
                    ["sniffing"] = new JsonObject
                    {
                        ["enabled"] = true,
                        ["destOverride"] = new JsonArray("http", "tls"),
                    },
                },
                new JsonObject
                {
                    ["tag"] = "http-in",
                    ["listen"] = listen,
                    ["port"] = httpPort,
                    ["protocol"] = "http",
                    ["sniffing"] = new JsonObject
                    {
                        ["enabled"] = true,
                        ["destOverride"] = new JsonArray("http", "tls"),
                    },
                },
            },
            ["outbounds"] = outbounds,
            ["routing"] = routing,
        };

        root["observatory"] = new JsonObject
        {
            ["subjectSelector"] = new JsonArray(tags.Select(t => (JsonNode)t!).ToArray()),
            ["probeUrl"] = ProbeUrlForObservatory,
            ["probeInterval"] = "90s",
            ["enableConcurrency"] = true,
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private const string DefaultDns = "223.5.5.5";

    /// 把 DNS 值规范成内核能吃、且不会把整条链路搞死的形态。
    /// 为什么需要：设置页「DNS 服务器 → 自定义…」是自由输入，值会原样写进内核配置。
    /// 但域名解析会失效；TUN 路径还会把它塞给 sing-box 当 DNS server。
    /// 规则：空 / 非法 → 223.5.5.5；IPv4/IPv6 字面量 → 原样；https:// / tls:// → 必须是合法 URL；
    /// 其他只接受合法主机名（字母数字点横线，每段非空、不连续点、不以点或横线开头结尾）。
    public static string NormalizeDnsServer(string? dnsServer)
    {
        var dns = (dnsServer ?? "").Trim();
        if (dns.Length == 0) return DefaultDns;

        if (dns.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || dns.StartsWith("tls://", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(dns, UriKind.Absolute, out var u) && u.Host.Length > 0 ? dns : DefaultDns;
        }

        if (IPAddress.TryParse(dns.Trim('[', ']'), out _)) return dns;

        if (dns.Length > 253) return DefaultDns;
        if (dns.StartsWith('.') || dns.EndsWith('.') || dns.Contains("..")) return DefaultDns;
        foreach (var label in dns.Split('.'))
        {
            if (label.Length is 0 or > 63) return DefaultDns;
            if (label.StartsWith('-') || label.EndsWith('-')) return DefaultDns;
            foreach (var c in label)
                if (!char.IsAsciiLetterOrDigit(c) && c != '-') return DefaultDns;
        }
        return dns;
    }

    /// balancer 策略白名单：只接受 leastLoad。
    /// （exit=23 unknown balancing strategy）→ 用户表现就是「点连接没反应 / 连不上」。
    public static string NormalizeStrategy(string? strategy)
    {
        _ = strategy;
        return "leastLoad";
    }

    private static JsonArray BuildRoutingRules(string mode, bool bypassPrivate, bool bypassCn, bool blockAds,        IReadOnlyList<RouteRule>? customRules, string proxyTag = "proxy")
    {
        var rules = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "field",
                ["inboundTag"] = new JsonArray("api-in"),
                ["outboundTag"] = "api",
            },
        };

        if (mode == "global")
            return rules;

        if (mode == "direct")
        {
            rules.Add(new JsonObject
            {
                ["type"] = "field",
                ["network"] = "tcp,udp",
                ["outboundTag"] = "direct",
            });
            return rules;
        }

        if (customRules != null)
        {
            foreach (var r in customRules)
            {
                var value = (r.Value ?? "").Trim().TrimStart('*').TrimStart('.');
                if (value.Length == 0) continue;
                var tag = r.Action switch { "proxy" => proxyTag, "block" => "block", _ => "direct" };
                var rule = new JsonObject { ["type"] = "field", ["outboundTag"] = tag };
                if (r.Kind == "ip") rule["ip"] = new JsonArray(value);
                else rule["domain"] = new JsonArray("domain:" + value);
                rules.Add(rule);
            }
        }

        // 记录用户自定义规则的边界：后面的性能重排只调整系统内置规则，不动用户规则的优先级。
        var customRuleCount = rules.Count;

        if (blockAds)
        {
            rules.Add(new JsonObject
            {
                ["type"] = "field",
                ["domain"] = new JsonArray("geosite:category-ads-all"),
                ["outboundTag"] = "block",
            });
        }

        if (bypassPrivate)
        {
            rules.Add(new JsonObject
            {
                ["type"] = "field",
                ["ip"] = new JsonArray("geoip:private"),
                ["outboundTag"] = "direct",
            });
            rules.Add(new JsonObject
            {
                ["type"] = "field",
                ["domain"] = new JsonArray("geosite:private"),
                ["outboundTag"] = "direct",
            });
        }

        // Windows 连通性探测（NCSI）必须走直连。
        //   这些域名不在 geosite:cn 里 ⇒ 落进 balancer 走代理 ⇒ **探测直接失败**。
        //     ipv6.msftconnecttest.com   直连 200 / 0.66s   经代理 000 失败 / 1.77s
        //     ipv6.msftncsi.com          直连 200 / 0.25s   经代理 000 失败 / 1.61s
        //     www.msftconnecttest.com    直连 200 / 0.60s   经代理 200 / 1.57s（慢 2.6×）
        //     www.msftncsi.com           直连 200 / 0.44s   经代理 200 / 1.36s（慢 3.1×）
        //   neo 日志一天内累计 32 条 `proxy/http: failed to read response from
        //   ipv6.msftconnecttest.com / ipv6.msftncsi.com`。
        // 后果不止"多花流量"：Windows 反复重试连通性探测，
        // 还会把网络判定为状态反复变化 —— 日志里紧跟的
        // 「网络地址已变化 — 立即复查节点连通性」正是被它触发的。
        // 这些是微软自家的小体积探测端点，直连严格优于走代理，故不挂在 bypassCn 下。
        //   xray 的 `"domain:x.test"` 前缀写法**本身就匹配子域**：
        //     裸域名 x.test        -> 命中
        //     子域名 sub.x.test    -> 命中
        //   （对照：写 `"domain:.x.test"` 时裸域名反而不命中）
        //   因此下面两条已覆盖 www / ipv6 / dns 等全部子域，无需逐个罗列。
        // 曾经的误判：先在实盘 socks 上测出"www.msftconnecttest.com 仍走代理"，
        // 但那是**背景流量噪声**（实盘 10808 上还有其他应用在连）。
        // 改用隔离内核读 access log 后，五个 NCSI 域名全部 -> direct，证明规则本就生效。
        rules.Add(new JsonObject
        {
            ["type"] = "field",
            ["domain"] = new JsonArray(
                "domain:msftconnecttest.com",
                "domain:msftncsi.com"),
            ["outboundTag"] = "direct",
        });

        // Steam 的连通性探测与内容下载 CDN 走直连。
        //   实测日志里一晚 10 条「proxy/http: failed to read response from
        //   ipv6check-http.steamserver.net > unexpected EOF」——Steam 探测被塞进代理后失败，
        //   客户端因此反复重试、商店/库页面转圈。这两个域在国内可直连且更快，走代理纯负收益。
        //   注意：store.steampowered.com / steamcommunity.com 仍走代理（国内被墙），不放这里。
        rules.Add(new JsonObject
        {
            ["type"] = "field",
            ["domain"] = new JsonArray(
                "domain:steamserver.net",
                "domain:steamcontent.com"),
            ["outboundTag"] = "direct",
        });

        if (bypassCn)
        {
            // 对齐「绕过大陆」模板的单一职责写法：xray 规则内多字段是 AND 语义，
            // domain+ip 合并在 AsIs（不解析域名）下永不命中，必须拆条。
            rules.Add(new JsonObject
            {
                ["type"] = "field",
                ["ip"] = new JsonArray(
                    "223.5.5.5", "223.6.6.6", "2400:3200::1", "2400:3200:baba::1",
                    "119.29.29.29", "1.12.12.12", "120.53.53.53", "2402:4e00::", "2402:4e00:1::",
                    "180.76.76.76", "2400:da00::6666", "114.114.114.114", "114.114.115.115",
                    "114.114.114.119", "114.114.115.119", "114.114.114.110", "114.114.115.110",
                    "180.184.1.1", "180.184.2.2", "101.226.4.6", "218.30.118.6", "123.125.81.6",
                    "140.207.198.6", "1.2.4.8", "210.2.4.8", "52.80.66.66", "117.50.22.22",
                    "2400:7fc0:849e:200::4", "2404:c2c0:85d8:901::4", "117.50.10.10", "52.80.52.52",
                    "2400:7fc0:849e:200::8", "2404:c2c0:85d8:901::8", "117.50.60.30", "52.80.60.30"),
                ["outboundTag"] = "direct",
            });
            rules.Add(new JsonObject
            {
                ["type"] = "field",
                ["domain"] = new JsonArray(
                    "domain:alidns.com", "domain:doh.pub", "domain:dot.pub",
                    "domain:360.cn", "domain:onedns.net"),
                ["outboundTag"] = "direct",
            });
            rules.Add(new JsonObject
            {
                ["type"] = "field",
                ["ip"] = new JsonArray("geoip:cn"),
                ["outboundTag"] = "direct",
            });
            rules.Add(new JsonObject
            {
                ["type"] = "field",
                ["domain"] = new JsonArray("geosite:cn"),
                ["outboundTag"] = "direct",
            });
        }

        // ★ 2026-10-22 性能调整（稳定优先）：把系统内置的「IP 条件」规则整体后移。
        //   原因：routing.domainStrategy 已是 IPOnDemand —— 官方文档：
        //     “IPOnDemand：若目标包含域名，遇到包含 ip 条件的规则时，使用内置 DNS 将域名解析为 IP 后进行匹配”
        //     https://xtls.github.io/config/routing.html
        //   原顺序中 geoip:private / DNS IP 列表 / geoip:cn 这些 IP 规则排在
        //   geosite:private / NCSI / geosite:cn 等域名规则之前，于是每个带域名的连接
        //   在第一条 IP 规则处就要先做一次本地 DNS 解析 —— 哪怕它马上会被后面的域名规则命中。
        //   调整后：域名规则先判，命中即出结果（国内站点/广告/NCSI 全部不再解析）；
        //   只有域名规则全不命中时才落到 IP 规则做 geoip 判断（原有能力不变）。
        //   等价性：被后移的 IP 规则与前面的域名规则都指向 direct（geoip:private / DNS IP 列表 /
        //   geoip:cn）或 block，改的只是匹配顺序；用户自定义规则仍在最前，不参与重排。
        //   实现用 DeepClone 重建数组，避免 JsonNode 的 parent 归属限制。
        var reordered = new JsonArray();
        var ipTail = new List<JsonNode>();
        for (var i = 0; i < rules.Count; i++)
        {
            var node = rules[i]!;
            if (i >= customRuleCount && node is JsonObject obj && obj.ContainsKey("ip"))
            {
                // ★ 2026-10-22 关键修复：IP 规则必须加 routeOnly。
                //   不加时 xray 会把「为匹配 geoip 而解析出的 IP」当作真正目标去连
                //   （官方文档 routing.md：解析结果默认会替换目标地址）。
                //   而我们的 DNS 是 223.5.5.5（国内 DNS），对 x.com / api.telegram.org 等
                //   返回的是被污染地址（实测 abs.twimg.com -> 108.160.165.9，
                //   api.telegram.org -> 31.13.94.10），于是代理被要求去连一个假 IP，
                //   表现就是「所有网站 15 秒超时、Steam 转圈」。
                //   routeOnly: true ⇒ 解析结果只用于路由判断，出站仍用原域名（远端解析）。
                var clone = (JsonObject)node.DeepClone();
                clone["routeOnly"] = true;
                ipTail.Add(clone);
            }
            else
                reordered.Add(node.DeepClone());
        }
        foreach (var ipRule in ipTail) reordered.Add(ipRule);
        return reordered;
    }

    /// <summary>
    /// 去掉 IPv6 字面量两侧的方括号（旧 state/subscription-cache 里的 Server 可能带 "[::1]"）。
    /// 只剥首尾 []，不做其它规范化。
    /// </summary>
    private static string StripBrackets(string host) =>
        host.Length >= 2 && host[0] == '[' && host[^1] == ']' ? host[1..^1] : host;

    /// <summary>64 位十六进制（sha256）判定：xray 的 pinnedPeerCertSha256 只接受这种形式，其它（如 hy2 的 base64 pin）忽略。</summary>
    private static bool IsHexSha256(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Length != 64) return false;
        foreach (var c in s)
        {
            if (!char.IsAsciiHexDigit(c)) return false;
        }
        return true;
    }

    private static JsonObject BuildProxyOutbound(NodeItem n)
    {
        var stream = BuildStreamSettings(n);

        // ★ 2026-10-23 IPv6 方括号兜底：vless/trojan 的 uri.Host 或旧 state/cache 里的 Server
        //   可能是 "[::1]" 形式，而 xray 的 address 字段要裸地址；这里统一去掉首尾方括号。
        //   BuildFixedExit 复用本方法（入口 hop 与出口 chain 都走这里），因此同样生效。
        var server = StripBrackets(n.Server);

        switch (n.Protocol)
        {
            case NodeProtocol.VMess:
                return new JsonObject
                {
                    ["tag"] = "proxy",
                    ["protocol"] = "vmess",
                    ["settings"] = new JsonObject
                    {
                        ["vnext"] = new JsonArray(new JsonObject
                        {
                            ["address"] = server,
                            ["port"] = n.Port,
                            ["users"] = new JsonArray(new JsonObject
                            {
                                ["id"] = n.Uuid ?? "",
                                ["alterId"] = n.AlterId,
                                ["security"] = string.IsNullOrEmpty(n.Cipher) ? "auto" : n.Cipher,
                            }),
                        }),
                    },
                    ["streamSettings"] = stream,
                };

            case NodeProtocol.VLESS:
                var vlessUser = new JsonObject
                {
                    ["id"] = n.Uuid ?? "",
                    // ★ 2026-10-23 第二批：VLESS 后量子加密（xray 25+，链接参数 encryption=mlkem768x25519plus...）。
                    //   旧实现写死 "none" → 这类节点能导入、能测速（测速配置同样写死 none 会失败）、连不上。
                    //   xray 26.10.17 实测接受该字段（台账 F1）；空/ none 时行为与旧版一致。
                    ["encryption"] = string.IsNullOrEmpty(n.VlessEncryption) ? "none" : n.VlessEncryption,
                };
                if (!string.IsNullOrEmpty(n.Flow)) vlessUser["flow"] = n.Flow;
                return new JsonObject
                {
                    ["tag"] = "proxy",
                    ["protocol"] = "vless",
                    ["settings"] = new JsonObject
                    {
                        ["vnext"] = new JsonArray(new JsonObject
                        {
                            ["address"] = server,
                            ["port"] = n.Port,
                            ["users"] = new JsonArray(vlessUser),
                        }),
                    },
                    ["streamSettings"] = stream,
                };

            case NodeProtocol.Trojan:
                return new JsonObject
                {
                    ["tag"] = "proxy",
                    ["protocol"] = "trojan",
                    ["settings"] = new JsonObject
                    {
                        ["servers"] = new JsonArray(new JsonObject
                        {
                            ["address"] = server,
                            ["port"] = n.Port,
                            ["password"] = n.Password ?? "",
                        }),
                    },
                    ["streamSettings"] = stream,
                };

            case NodeProtocol.Shadowsocks:
                return new JsonObject
                {
                    ["tag"] = "proxy",
                    ["protocol"] = "shadowsocks",
                    ["settings"] = new JsonObject
                    {
                        ["servers"] = new JsonArray(new JsonObject
                        {
                            ["address"] = server,
                            ["port"] = n.Port,
                            ["password"] = n.Password ?? "",
                            ["method"] = n.Method ?? "aes-128-gcm",
                        }),
                    },
                    ["streamSettings"] = stream,
                };

            case NodeProtocol.Socks:
                // ★ 2026-10-23 第二批：socks5 出站（socks:// 分享链接）。xray 实测接受（台账 F1）。
                var socksServer = new JsonObject
                {
                    ["address"] = server,
                    ["port"] = n.Port,
                };
                if (!string.IsNullOrEmpty(n.Username))
                {
                    socksServer["users"] = new JsonArray(new JsonObject
                    {
                        ["user"] = n.Username,
                        ["pass"] = n.Password ?? "",
                    });
                }
                return new JsonObject
                {
                    ["tag"] = "proxy",
                    ["protocol"] = "socks",
                    ["settings"] = new JsonObject { ["servers"] = new JsonArray(socksServer) },
                };

            case NodeProtocol.AnyTls:
                // xray 26.10.17 实测没有 anytls 出站（台账 F1：unknown config id: anytls）→ 必须走 sing-box。
                // 这里抛出可读原因，由 ConnectionService / 内核能力判定提前拦下，不会走到这个分支。
                throw new NotSupportedException(
                    $"节点「{n.Name}」是 anytls 协议，xray 内核不支持（实测 unknown config id: anytls），请走 sing-box 内核");

            default:
                throw new NotSupportedException($"协议 {n.Protocol} 暂不支持 xray 内核（等待 sing-box 支持）");
        }
    }

    private static JsonObject BuildStreamSettings(NodeItem n)
    {
        var s = new JsonObject { ["network"] = n.Network };

        switch (n.Network)
        {
            case "ws":
                var ws = new JsonObject
                {
                    ["path"] = string.IsNullOrEmpty(n.Path) ? "/" : n.Path,
                };
                if (!string.IsNullOrEmpty(n.HostHeader))
                    ws["host"] = n.HostHeader;
                s["wsSettings"] = ws;
                break;

            case "grpc":
                var grpc = new JsonObject
                {
                    ["serviceName"] = n.ServiceName ?? "",
                };
                // ★ 2026-10-23 第二批：gRPC authority / multi 模式（分享链接 authority=、mode=multi）。
                //   旧实现只写 serviceName → 需要 authority 的节点（多数 CDN 前置）握手 Host 不对，连不上。
                if (!string.IsNullOrEmpty(n.GrpcAuthority)) grpc["authority"] = n.GrpcAuthority;
                if (!string.IsNullOrEmpty(n.GrpcMode) && n.GrpcMode.Equals("multi", StringComparison.OrdinalIgnoreCase))
                    grpc["multiMode"] = true;
                s["grpcSettings"] = grpc;
                break;

            case "httpupgrade":
                // ★ 2026-10-23 第二批：HTTPUpgrade 传输（xray 支持但已标记 deprecated，实测仍可用）。
                var httpup = new JsonObject
                {
                    ["path"] = string.IsNullOrEmpty(n.Path) ? "/" : n.Path,
                };
                if (!string.IsNullOrEmpty(n.HostHeader)) httpup["host"] = n.HostHeader;
                s["httpupgradeSettings"] = httpup;
                break;

            case "kcp":
            case "mkcp":
                // ★ 2026-10-23 第二批：mKCP 传输（xray 专有，sing-box 不支持）。
                //   xray 26 实测要求 maxSendingWindow >= 1350（旧默认 0 会被拒绝启动）；
                //   头混淆 / 种子已从 kcpSettings 移到 finalmask.udp（xray 新格式即此写法），
                //   kcpSettings 里的 header 字段在 26 已被移除。
                s["network"] = "kcp";
                s["kcpSettings"] = new JsonObject
                {
                    ["mtu"] = n.KcpMtu is > 0 ? n.KcpMtu.Value : 1350,
                    ["tti"] = 20,
                    ["uplinkCapacity"] = 5,
                    ["downlinkCapacity"] = 20,
                    ["cwndMultiplier"] = 1,
                    ["maxSendingWindow"] = 1350,
                };
                var masks = new JsonArray();
                if (!string.IsNullOrEmpty(n.HeaderType) &&
                    !n.HeaderType.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    masks.Add(new JsonObject
                    {
                        ["type"] = "mkcp-legacy",
                        ["settings"] = new JsonObject { ["header"] = n.HeaderType.ToLowerInvariant() },
                    });
                }
                if (!string.IsNullOrEmpty(n.KcpSeed))
                {
                    masks.Add(new JsonObject
                    {
                        ["type"] = "mkcp-legacy",
                        ["settings"] = new JsonObject { ["value"] = n.KcpSeed },
                    });
                }
                if (masks.Count > 0) s["finalmask"] = new JsonObject { ["udp"] = masks };
                break;

            case "http":
            case "h2":
            case "quic":
                //   `The feature HTTP transport ... has been removed` / `QUIC transport ... removed`）。
                //   继续按老格式生成会让整份配置被内核拒绝，界面只显示
                //   「内核启动后立即退出」。改为在生成阶段抛出可读原因，由调用方提示用户。
                throw new NotSupportedException(
                    $"节点「{n.Name}」的传输方式 {n.Network} 已被 xray 26 移除，无法通过 xray 内核连接；" +
                    "请开启 TUN（sing-box 内核）或更换节点");

            case "tcp":
                if (!string.IsNullOrEmpty(n.HeaderType) && !string.Equals(n.HeaderType, "none", StringComparison.OrdinalIgnoreCase))
                {
                    s["tcpSettings"] = new JsonObject
                    {
                        ["header"] = new JsonObject { ["type"] = n.HeaderType },
                    };
                }
                break;

            case "xhttp":
                // ★ 2026-10-23 第二批：xhttp 的 mode / extra（分享链接 mode=stream-one&extra={...}）。
                //   旧实现写死 mode=auto、丢 extra → 服务端要求固定模式/分片参数时直接连不上。
                var xhttp = new JsonObject
                {
                    ["path"] = string.IsNullOrEmpty(n.Path) ? "/" : n.Path,
                    ["host"] = string.IsNullOrEmpty(n.HostHeader) ? StripBrackets(n.Server) : n.HostHeader,
                };
                var xm = (n.XhttpMode ?? "").Trim().ToLowerInvariant();
                if (xm is "auto" or "packet-up" or "stream-up" or "stream-one") xhttp["mode"] = xm;
                if (!string.IsNullOrEmpty(n.XhttpExtra))
                {
                    try
                    {
                        var extraNode = JsonNode.Parse(n.XhttpExtra);
                        if (extraNode is JsonObject extraObj) xhttp["extra"] = extraObj;
                    }
                    catch { /* extra 不是合法 JSON：丢弃，不影响主配置 */ }
                }
                s["xhttpSettings"] = xhttp;
                break;
        }

        switch ((n.Security ?? "none").ToLowerInvariant())
        {
            case "tls":
                var tls = new JsonObject
                {
                    ["serverName"] = n.Sni ?? n.HostHeader ?? n.Server,
                };
                if (!string.IsNullOrEmpty(n.Fingerprint)) tls["fingerprint"] = n.Fingerprint;
                if (n.AllowInsecure) tls["allowInsecure"] = true;

                // ★ 2026-10-23 第二批（分享链接标准参数）：
                //   · ech → echConfigList（base64 ECHConfigList）；xray 26 还要求 echForceQuery=full 才真正查询
                //   · vcn → verifyPeerCertByName（按证书里的名字校验，不依赖 SNI）
                //   · pcs → pinnedPeerCertSha256（证书链 pin，xray 要求 64 位十六进制）
                //   三者 xray 26.10.17 实测均接受（台账 F1）；旧实现全部丢失 → 这类节点证书校验必失败。
                if (!string.IsNullOrEmpty(n.EchConfigList))
                {
                    tls["echConfigList"] = n.EchConfigList;
                    tls["echForceQuery"] = "full";
                }
                if (!string.IsNullOrEmpty(n.VerifyPeerCertByName)) tls["verifyPeerCertByName"] = n.VerifyPeerCertByName;
                if (IsHexSha256(n.CertSha256)) tls["pinnedPeerCertSha256"] = n.CertSha256;
                if (n.Extras.TryGetValue("alpn", out var alpn) && !string.IsNullOrEmpty(alpn))
                {
                    tls["alpn"] = new JsonArray(alpn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(x => (JsonNode)x!).ToArray());
                }
                s["security"] = "tls";
                s["tlsSettings"] = tls;
                break;

            case "reality":
                s["security"] = "reality";
                var reality = new JsonObject
                {
                    ["serverName"] = n.Sni ?? "",
                    ["fingerprint"] = string.IsNullOrEmpty(n.Fingerprint) ? "chrome" : n.Fingerprint,
                    ["publicKey"] = n.PublicKey ?? "",
                    ["shortId"] = n.ShortId ?? "",
                    ["spiderX"] = n.SpiderX ?? "",
                };
                // ★ 2026-10-23 第二批：REALITY 的 ML-DSA-65 后量子校验（分享链接 pqv=）。
                //   xray 26.10.17 实测接受该字段（用 xray mldsa65 生成的真实验证串验过，台账 F1）。
                if (!string.IsNullOrEmpty(n.Mldsa65Verify)) reality["mldsa65Verify"] = n.Mldsa65Verify;
                s["realitySettings"] = reality;
                break;

            default:
                s["security"] = "none";
                break;
        }

        return s;
    }
}
