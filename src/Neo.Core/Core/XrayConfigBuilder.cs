using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Neo.Core.Models;

namespace Neo.Core.Core;

public static class XrayConfigBuilder
{
    public const int DefaultSocksPort = 10808;
    public const int DefaultHttpPort = 10809;

    public const int StatsApiPort = 10085;

    /// observatory（leastLoad/leastPing 的延迟与存活探测）所用目标。
    ///   observatory 每个探测周期会把**全部出站**各探一次，单次含 TLS 握手约 32 KB。
    ///   换成明文 HTTP 后省掉端到端 TLS 握手，单次成本大幅下降；
    ///   而 observatory 只需"能否拿到有效 HTTP 响应"来判定存活与测延迟，不依赖 TLS。
    /// 选 cloudflare 的 generate_204：国内可达性相对稳、响应体为 0 字节。
    public const string ProbeUrlForObservatory = "http://cp.cloudflare.com/generate_204";

    public static string Build(NodeItem node, int socksPort = DefaultSocksPort, int httpPort = DefaultHttpPort,
        string proxyMode = "rule", bool bypassPrivate = true, bool bypassCn = true, bool blockAds = true,
        bool allowLan = false, IReadOnlyList<RouteRule>? customRules = null, string logLevel = "warning",
        string dnsServer = "223.5.5.5")
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
                ["loglevel"] = level,
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
                ["levels"] = new JsonObject { ["0"] = new JsonObject { ["statsUserUplink"] = true, ["statsUserDownlink"] = true } },
                ["system"] = new JsonObject { ["statsInboundUplink"] = true, ["statsInboundDownlink"] = true, ["statsOutboundUplink"] = true, ["statsOutboundDownlink"] = true },
            },
            ["inbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["listen"] = "127.0.0.1",
                    ["port"] = StatsApiPort,
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
                // IPOnDemand：域名规则没命中时，遇到 IP 规则（geoip:cn）再解析域名。
                //   AsIs 下 geoip:cn 规则对「域名目标」永远不生效，于是 geosite:cn 之外的
                //   国内站点会全部走代理（实测 realme.com 就是这样，用户感知「国内站很慢」）。
                //   改成 IPOnDemand 后：命中 geosite:cn 的照旧直连，没命中的按解析出的 CN IP 直连，
                //   国外域名解析后仍落 balancer 走代理（已实测 google/youtube/chatgpt 不受影响）。
                ["domainStrategy"] = "IPOnDemand",
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
            ["log"] = new JsonObject { ["loglevel"] = "warning" },
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

    /// 批量测速配置（v2rayN 式）：一个实例承载全部节点，每节点独立 socks 入站端口，路由按入站 tag 分流到对应出站。
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
            ["log"] = new JsonObject { ["loglevel"] = "warning" },
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

    ///   Balanced  —— 均衡分摊：连接均匀打到全部存活节点，追求「所有节点一起工作」。
    public enum ConcurrencyMode
    {
        Balanced,
        Weighted,
    }

    /// 加权模式下，一个节点最多占多少份（份额上限，防止单个超快节点独占全部流量）。
    private const int MaxWeightShares = 6;

    /// 加权模式下，相对最慢者的权重下限。比最快节点慢 k 倍的节点至少留 1 份。
    /// 不设 0 份 —— 用户要求「慢的也要参与打辅助」，不能把慢节点完全踢出去。
    private const int MinWeightShares = 1;

    /// 多节点加速配置：一个 xray 进程内建 N 个出站 + balancer，由内核按策略分摊连接。
    ///   leastPing 的 PickOutbound 是「遍历所有候选、选 delay 最小的那一个」，
    ///   返回的是**唯一最优点**，与连接数无关。因此无论并发多少条连接，
    ///   全部流量都会集中到同一个最快的节点上。
    ///     leastPing · 6 条并行流 → 下载 100.0 MB，proxy0 独占 +100.22 MB，其余出站 +0.00
    ///   即「界面显示多节点、实际全部走一个」——用户反馈的「不是真并发」正是此因。
    /// 现用 leastLoad，并靠 strategy.settings / selector 塑形分摊比例：
    ///   leastLoad 的 selectLeastLoad 在 Baselines 为空时走 `return nodes[:expected]`，
    ///   令 expected = 候选数 ⇒ 候选集 = 全部存活节点；PickOutbound 再以 `dice.Roll(count)`
    ///   在候选集内**均匀随机**选一个 ⇒ 谁在候选集里占的份额多，谁就多吃流量。
    ///   它同时排除 observatory 判定 Alive=false 的**死节点**。
    /// 两个模式都**不设** maxRTT：那是按速度门槛踢人，与「慢节点也要打辅助」冲突。
    /// 不能用「selector 里重复写同一个 tag」来加权：xray 的
    ///（把 proxy0 写 4 遍，30 次连接仍全部只走 proxy0）。
    /// 正确做法是给同一个节点建**多个不同 tag 的出站**（proxy0、w0b、w0c…），
    /// 每个 tag 在 selector 里占一份 ⇒ 该节点在候选集中占多份 ⇒ 分到的流量按份额正比。
    /// 该节点合计拿到 99.6% 流量，另两个各 0.2% ⇒ 加权按份额生效且非常显著。
    /// 保留 stats api 与 policy：流量统计（UsageTracker/XrayStats）依赖它，删了会丢统计。
    /// 注意 XrayStats 按 `outbound>>>&lt;tag&gt;>>>traffic>>>&lt;dir&gt;` 解析且只排除
    /// direct/block/api，因此加权克隆出的 w0b 等 tag 会被正常计入，不会漏统计。
    public static string BuildMulti(IReadOnlyList<NodeItem> nodes,
        int socksPort = DefaultSocksPort, int httpPort = DefaultHttpPort,
        string proxyMode = "rule", bool bypassPrivate = true, bool bypassCn = true, bool blockAds = true,
        bool allowLan = false, IReadOnlyList<RouteRule>? customRules = null, string logLevel = "warning",
        string dnsServer = "223.5.5.5", string strategy = "leastLoad",
        ConcurrencyMode mode = ConcurrencyMode.Balanced, IReadOnlyList<int>? delaysMs = null,
        IReadOnlyList<int>? speedsKbps = null)
    {
        var valid = nodes.Where(n => n.IsValid).ToList();
        if (valid.Count == 0) throw new ArgumentException("多节点加速：没有可用节点");
        if (socksPort == httpPort) throw new ArgumentException("socks 与 http 端口不能相同（xray 无混合入站）");

        var listen = allowLan ? "0.0.0.0" : "127.0.0.1";
        var level = logLevel is "none" or "error" or "warning" or "info" or "debug" ? logLevel : "warning";
        var dns = NormalizeDnsServer(dnsServer);
        strategy = NormalizeStrategy(strategy);

        var outbounds = new JsonArray();
        var tags = new List<string>();
        var weights = ComputeWeights(valid.Count, mode, delaysMs, speedsKbps);

        for (var i = 0; i < valid.Count; i++)
        {
            // 主出站：每个节点一个，tag 固定为 proxy{i}（单节点/均衡模式即用这一批）。
            var mainTag = $"proxy{i}";
            var ob = BuildProxyOutbound(valid[i]);
            ob["tag"] = mainTag;
            outbounds.Add(ob);
            tags.Add(mainTag);

            // 加权模式：额外的份额出站，tag 用 w{i}{字母}，内容与主出站完全相同。
            for (var k = 1; k < weights[i]; k++)
            {
                var extra = BuildProxyOutbound(valid[i]);
                extra["tag"] = $"w{i}{(char)('b' + k - 1)}";
                outbounds.Add(extra);
                tags.Add((string)extra["tag"]!);
            }
        }

        outbounds.Add(new JsonObject { ["tag"] = "direct", ["protocol"] = "freedom" });
        outbounds.Add(new JsonObject { ["tag"] = "block", ["protocol"] = "blackhole" });
        outbounds.Add(new JsonObject { ["tag"] = "api", ["protocol"] = "freedom" });

        // 路由规则复用单节点版本，但把发往 proxy 的规则改指向 balancer
        var rules = BuildRoutingRules(proxyMode, bypassPrivate, bypassCn, blockAds, customRules, proxyTag: "bal");

        // 关键：xray 对「未命中任何规则」的流量走**第一个出站**。
        // 单节点时第一个出站就是 proxy（正好正确），但多节点时第一个是 proxy0 ——
        // 若不补兜底规则，未匹配流量会全部挤在 proxy0 上，balancer 形同虚设。
        // 放在最后，保证前面的 direct/block 等具体规则仍优先。
        if (proxyMode != "direct")
        {
            rules.Add(new JsonObject
            {
                ["type"] = "field",
                ["network"] = "tcp,udp",
                ["balancerTag"] = "bal",
            });
        }

        // strategy.settings：leastLoad 的 settings 走 StrategyLeastLoadConfig。
        // expected = 候选 tag 总数（含加权份额）⇒ 候选集 = 全部存活 tag。
        // tolerance 只剔除「探测失败率过高」的**坏节点**（不是慢节点），0.3 = 失败率超三成才剔。
        // 刻意**不设** maxRTT（见方法注释）。
        // 注意 maxRTT 若要用必须写成 duration 字符串（"2500ms"），写纯数字会被内核拒绝
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
            // 极端兜底：若全部节点都被 observatory 判死，策略会返回空 tag 并报错断网。
            // 退回第一个出站至少保证「有网可用」，比整条链路失败好。
            ["fallbackTag"] = tags[0],
        };

        var routing = new JsonObject
        {
            // 同上：多节点配置也用 IPOnDemand，让 geosite:cn 之外的国内站点按 IP 判直连。
            ["domainStrategy"] = "IPOnDemand",
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
                ["levels"] = new JsonObject { ["0"] = new JsonObject { ["statsUserUplink"] = true, ["statsUserDownlink"] = true } },
                ["system"] = new JsonObject { ["statsInboundUplink"] = true, ["statsInboundDownlink"] = true, ["statsOutboundUplink"] = true, ["statsOutboundDownlink"] = true },
            },
            ["inbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["listen"] = "127.0.0.1",
                    ["port"] = StatsApiPort,
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

        // leastLoad / leastPing 依赖 observatory 主动探测各出站延迟；
        // leastLoad 还额外用它剔除 Alive=false 的死节点（慢但活着的保留）。
        // 没有它，内核无法判断节点是否可用，也就无从避开死节点。
        // 依据（读 xray 源码 app/observatory/observer.go 确认）：
        //     subjectSelector 选中的**全部出站**各探一次（每个 outbound 一个 goroutine）。
        //     所以开销 = 出站数 × 频率，加权模式下出站可达 26 个。
        //   · 单次探活含端到端 TLS 握手约 32 KB。
        //     10s × 26 个 ≈ 285 MB/小时 ≈ **200 GB/月** —— 这是用户"流量一直在跑"的主因。
        //   · 改 90s 后同一算式降到约 22 GB/月；再叠加明文 HTTP（省掉 TLS 握手）进一步下降。
        // 为什么是 90s 而不是更长：observatory 的结论同时用于 leastLoad 的 Alive 判定，
        //   间隔太长会让死节点被继续选用。90s 是"开销可控"与"及时发现死节点"的折中。
        //   且 xray observatory 只关心"能否拿到有效 HTTP 响应"，不需要 TLS 语义。
        if (strategy is "leastPing" or "leastLoad")
        {
            root["observatory"] = new JsonObject
            {
                ["subjectSelector"] = new JsonArray(tags.Select(t => (JsonNode)t!).ToArray()),
                ["probeUrl"] = ProbeUrlForObservatory,
                ["probeInterval"] = "90s",
                ["enableConcurrency"] = true,
            };
        }

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
        string dnsServer = "223.5.5.5", string strategy = "leastLoad")
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
            ["domainStrategy"] = "IPOnDemand",
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
                ["levels"] = new JsonObject { ["0"] = new JsonObject { ["statsUserUplink"] = true, ["statsUserDownlink"] = true } },
                ["system"] = new JsonObject { ["statsInboundUplink"] = true, ["statsInboundDownlink"] = true, ["statsOutboundUplink"] = true, ["statsOutboundDownlink"] = true },
            },
            ["inbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["listen"] = "127.0.0.1",
                    ["port"] = StatsApiPort,
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

    /// 计算每个节点在 balancer 候选集里占的**份额**（份数）。
    /// 份额 = 该节点在候选集中出现的次数，内核在候选集内均匀随机取，
    /// 因此份额比 ≈ 流量比。
    /// Balanced 模式：全部返回 1 ⇒ 均匀分摊，所有节点等权参与。
    ///   份额 = round(MaxWeightShares × 最快延迟 / 本节点延迟)，
    ///   并夹在 [MinWeightShares, MaxWeightShares]。
    ///   · 延迟越接近最快者，份额越接近上限 6 份；
    ///   · 慢 6 倍以上的节点压到下限 1 份，但**绝不为 0** —— 用户明确要求
    ///     「慢的节点也要打辅助」，不能把它踢出池子。
    ///   · 用**相对倍数**而非绝对毫秒：绝对阈值（如 >1500ms 才降权）在弱网/蜂窝下
    ///     会把当下最快的节点也判成慢节点；相对值随链路自适应。
    ///   · 延迟未知（≤0，未测速或测速失败）时给中间份额，既不偏向也不惩罚，
    ///     等 observatory 探明真实延迟后由内核侧继续按健康度筛选。
    /// 30 次独立连接累计该节点拿到 99.6% 流量 —— 份额确实线性映射到流量占比。
    private static int[] ComputeWeights(int count, ConcurrencyMode mode, IReadOnlyList<int>? delaysMs,
        IReadOnlyList<int>? speedsKbps = null)
    {
        var w = new int[count];

        if (mode != ConcurrencyMode.Weighted)
        {
            for (var i = 0; i < count; i++) w[i] = 1;
            return w;
        }

        //   最低 1 份（绝不为 0）。多节点并发下载时所有节点的带宽一起出力，节点越多总带宽越大。
        //   为什么不再「延迟定份额」：延迟只反映握手快慢，带宽好但延迟高的节点会被压成 1 份，
        //   等于没参与并发 —— 与用户要的「多缸都出力」相反。
        //   延迟只保留为**可用性门槛**：RealDelay<=0（连 google 204 都握不上手）的节点压到 1 份，
        //   不至于完全断流，但也不会拖累大流量。
        //   份额 = clamp(round(MaxWeightShares × 本节点吞吐 / 最快节点吞吐), 1, 6)
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
                    if (s <= 0 || !reachable) { w[i] = MinWeightShares; continue; }   // 仍留 1 份，每缸都出力
                    var shares = (int)Math.Round(MaxWeightShares * (double)s / bestSpeed, MidpointRounding.AwayFromZero);
                    w[i] = Math.Clamp(shares, MinWeightShares, MaxWeightShares);
                }
                return w;
            }
        }

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

        if (bypassCn)
        {
            // 对齐 v2rayN「绕过大陆」模板的单一职责写法：xray 规则内多字段是 AND 语义，
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

        return rules;
    }

    private static JsonObject BuildProxyOutbound(NodeItem n)
    {
        var stream = BuildStreamSettings(n);

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
                            ["address"] = n.Server,
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
                    ["encryption"] = "none",
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
                            ["address"] = n.Server,
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
                            ["address"] = n.Server,
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
                            ["address"] = n.Server,
                            ["port"] = n.Port,
                            ["password"] = n.Password ?? "",
                            ["method"] = n.Method ?? "aes-128-gcm",
                        }),
                    },
                    ["streamSettings"] = stream,
                };

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
                s["grpcSettings"] = new JsonObject
                {
                    ["serviceName"] = n.ServiceName ?? "",
                };
                break;

            case "http":
            case "h2":
            case "quic":
                //   `The feature HTTP transport ... has been removed` / `QUIC transport ... removed`）。
                //   继续按老格式生成会让整份配置（含多节点整组）被内核拒绝，界面只显示
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
                s["xhttpSettings"] = new JsonObject
                {
                    ["path"] = string.IsNullOrEmpty(n.Path) ? "/" : n.Path,
                    ["host"] = n.HostHeader ?? n.Server,
                    ["mode"] = "auto",
                };
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
                s["realitySettings"] = new JsonObject
                {
                    ["serverName"] = n.Sni ?? "",
                    ["fingerprint"] = string.IsNullOrEmpty(n.Fingerprint) ? "chrome" : n.Fingerprint,
                    ["publicKey"] = n.PublicKey ?? "",
                    ["shortId"] = n.ShortId ?? "",
                    ["spiderX"] = n.SpiderX ?? "",
                };
                break;

            default:
                s["security"] = "none";
                break;
        }

        return s;
    }
}
