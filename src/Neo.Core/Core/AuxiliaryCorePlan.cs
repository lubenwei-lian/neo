using System.Text.Json;
using System.Text.Json.Nodes;
using Neo.Core.Models;

namespace Neo.Core.Core;

/// <summary>Protocol engine behind a sing-box frontend. The frontend alone owns TUN, rules, statistics and local user ports.</summary>
public sealed record AuxiliaryCorePlan(string Engine, string Config, bool OwnsRouting)
{
    public static bool Needed(NodeItem node, bool tun) => node.IsNaive || node.Protocol == NodeProtocol.ShadowsocksR
        || tun && (node.RequiresXray || node.IsClashPassthrough || node.IsCustomConfig && node.CustomEngine == "xray");

    public static AuxiliaryCorePlan Create(NodeItem node, int socksPort, int httpPort, int apiPort)
    {
        if (node.IsNaive) return new("naive", NaiveConfigBuilder.Build(node, socksPort), false);
        if (node.Protocol == NodeProtocol.ShadowsocksR) return new("mihomo", MihomoNodeConfigBuilder.Build(node, socksPort), false);
        if (node.IsClashPassthrough) return new("mihomo", MihomoConfigBuilder.Build(node, httpPort, socksPort, apiPort, "warning"), true);
        if (node.IsCustomConfig && node.CustomEngine == "xray")
            return new("xray", CustomConfigBuilder.BuildXray(File.ReadAllText(node.Extras["config_path"]), socksPort, httpPort, apiPort, false, "warning"), true);
        var config = JsonNode.Parse(XrayConfigBuilder.BuildTest(node, socksPort))!.AsObject();
        config["inbounds"]![0]!["settings"]!["udp"] = true;
        return new("xray", config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), false);
    }

    public IReadOnlyList<string> Arguments(string path, string runtimeDir) => Engine switch
    {
        "naive" => [path], "mihomo" => ["-f", path, "-d", runtimeDir], _ => ["run", "-c", path],
    };

    public static NodeItem FrontNode(int port, string name) => new()
    { Protocol = NodeProtocol.Socks, Server = "127.0.0.1", Port = port, Name = name, Security = "none" };

    public static string ProtectTunTransport(string frontJson, string executablePath)
    {
        var root = JsonNode.Parse(frontJson)!.AsObject();
        var rules = root["route"]!["rules"]!.AsArray();
        rules.Insert(0, new JsonObject
        {
            ["process_path"] = new JsonArray(Path.GetFullPath(executablePath)),
            ["action"] = "route", ["outbound"] = "direct",
        });
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
