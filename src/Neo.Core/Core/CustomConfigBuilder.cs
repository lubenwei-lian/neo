using System.Text.Json;
using System.Text.Json.Nodes;
using Neo.Core.Models;

namespace Neo.Core.Core;

/// <summary>
/// 自定义整份配置直通（对标成熟客户端的「自定义配置」档）：
/// 订阅里给的是完整 xray/sing-box JSON 时，不拆节点，原样保留 outbounds/routing/dns，
/// 只把**本地接入层**（socks/http 端口、统计接口）改成本项目约定的值，
/// 这样系统代理、统计、看门狗等既有链路都能照常工作。
/// 限制：暂不支持 TUN（不做 tun inbound 注入），也不做固定出口链式改写。
/// </summary>
public static class CustomConfigBuilder
{
    public const string EngineXray = "xray";
    public const string EngineSingBox = "sing-box";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>订阅内容是不是「整份 JSON 配置」：对象 + outbounds 数组 + 每个 outbound 带 protocol/type。</summary>
    public static bool LooksLikeCustomConfig(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.TrimStart();
        if (t.Length == 0 || t[0] != '{') return false;
        try
        {
            if (JsonNode.Parse(t) is not JsonObject root) return false;
            return DetectEngine(root) != null;
        }
        catch { return false; }
    }

    /// <summary>探测内核：outbound 带 protocol → xray；带 type → sing-box；都拿不到返回 null。</summary>
    public static string? DetectEngine(string text)
    {
        try
        {
            return JsonNode.Parse(text) is JsonObject root ? DetectEngine(root) : null;
        }
        catch { return null; }
    }

    public static string? DetectEngine(JsonObject root)
    {
        if (root["outbounds"] is JsonArray outs)
        {
            foreach (var item in outs)
            {
                if (item is not JsonObject o) continue;
                if (o.ContainsKey("protocol")) return EngineXray;
                if (o.ContainsKey("type")) return EngineSingBox;
            }
        }
        return null;
    }

    /// <summary>
    /// xray 自定义配置：替换 inbounds 为 socks/http/统计 API，补 api + policy + 路由规则；
    /// outbounds / dns / routing.rules（其余部分）保持用户原样。
    /// </summary>
    public static string BuildXray(string sourceJson, int socksPort, int httpPort, int statsPort, bool allowLan, string logLevel)
    {
        var root = JsonNode.Parse(sourceJson) as JsonObject
                   ?? throw new ArgumentException("自定义 xray 配置不是 JSON 对象");
        if (root["outbounds"] is not JsonArray outs || outs.Count == 0)
            throw new ArgumentException("自定义 xray 配置缺少 outbounds");

        var listen = allowLan ? "0.0.0.0" : "127.0.0.1";
        root["inbounds"] = new JsonArray(
            new JsonObject
            {
                ["tag"] = "socks-in",
                ["protocol"] = "socks",
                ["listen"] = listen,
                ["port"] = socksPort,
                ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = true },
            },
            new JsonObject
            {
                ["tag"] = "http-in",
                ["protocol"] = "http",
                ["listen"] = listen,
                ["port"] = httpPort,
                ["settings"] = new JsonObject(),
            },
            new JsonObject
            {
                ["tag"] = "api",
                ["protocol"] = "dokodemo-door",
                ["listen"] = "127.0.0.1",
                ["port"] = statsPort,
                ["settings"] = new JsonObject { ["address"] = "127.0.0.1" },
            });

        root["api"] = new JsonObject { ["tag"] = "api", ["services"] = new JsonArray("StatsService") };

        // 统计开关（本项目「内核卡」的上下行就是读这里）
        var policy = root["policy"] as JsonObject ?? new JsonObject();
        var system = policy["system"] as JsonObject ?? new JsonObject();
        system["statsInboundUplink"] = true;
        system["statsInboundDownlink"] = true;
        policy["system"] = system;
        root["policy"] = policy;

        // 路由：把 api inbound 的握手规则放到最前，其余规则原样保留
        var routing = root["routing"] as JsonObject ?? new JsonObject();
        var rules = routing["rules"] as JsonArray;
        var merged = new JsonArray(new JsonObject
        {
            ["type"] = "field",
            ["inboundTag"] = new JsonArray("api"),
            ["outboundTag"] = "api",
        });
        if (rules != null)
            foreach (var rule in rules.ToList()) merged.Add(rule?.DeepClone());
        routing["rules"] = merged;
        root["routing"] = routing;

        // 日志级别沿用本项目的全局设置（自定义配置里通常没写 log，写了也以本地设置优先）。
        var log = root["log"] as JsonObject ?? new JsonObject();
        log["loglevel"] = string.IsNullOrWhiteSpace(logLevel) ? "warning" : logLevel;
        root["log"] = log;

        return root.ToJsonString(Indented);
    }

    /// <summary>
    /// sing-box 自定义配置：替换 inbounds 为 socks/http，补 experimental.clash_api（统计用）；
    /// outbounds / route / dns / endpoints 原样保留。
    /// </summary>
    public static string BuildSingBox(string sourceJson, int socksPort, int httpPort, int clashPort, string secret, bool allowLan)
    {
        var root = JsonNode.Parse(sourceJson) as JsonObject
                   ?? throw new ArgumentException("自定义 sing-box 配置不是 JSON 对象");
        if (root["outbounds"] is not JsonArray outs || outs.Count == 0)
            throw new ArgumentException("自定义 sing-box 配置缺少 outbounds");

        var listen = allowLan ? "0.0.0.0" : "127.0.0.1";
        root["inbounds"] = new JsonArray(
            new JsonObject
            {
                ["type"] = "socks",
                ["tag"] = "socks-in",
                ["listen"] = listen,
                ["listen_port"] = socksPort,
            },
            new JsonObject
            {
                ["type"] = "http",
                ["tag"] = "http-in",
                ["listen"] = listen,
                ["listen_port"] = httpPort,
            });

        var experimental = root["experimental"] as JsonObject ?? new JsonObject();
        experimental["clash_api"] = new JsonObject
        {
            ["external_controller"] = "127.0.0.1:" + clashPort,
            ["secret"] = secret,
        };
        root["experimental"] = experimental;

        return root.ToJsonString(Indented);
    }
}
