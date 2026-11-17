using System.Text.Json;
using System.Text.Json.Nodes;
using Neo.Core.Models;

namespace Neo.Core.Core;

/// <summary>A single SSR node behind a loopback SOCKS ingress; Neo's frontend retains routing and TUN ownership.</summary>
public static class MihomoNodeConfigBuilder
{
    public static string Build(NodeItem node, int socksPort)
    {
        if (node.Protocol != NodeProtocol.ShadowsocksR || !node.IsValid) throw new ArgumentException("不是有效的 SSR 节点");
        string Extra(string key, string fallback = "") => node.Extras.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
        var proxy = new JsonObject
        {
            ["name"] = "proxy", ["type"] = "ssr", ["server"] = node.Server, ["port"] = node.Port,
            ["cipher"] = node.Method, ["password"] = node.Password,
            ["protocol"] = Extra("ssr_protocol", "origin"), ["obfs"] = Extra("ssr_obfs", "plain"),
            ["protocol-param"] = Extra("ssr_protocol_param"), ["obfs-param"] = Extra("ssr_obfs_param"), ["udp"] = true,
        };
        return new JsonObject
        {
            ["socks-port"] = socksPort, ["port"] = 0, ["mixed-port"] = 0,
            ["allow-lan"] = false, ["bind-address"] = "127.0.0.1", ["external-controller"] = "",
            ["log-level"] = "error", ["mode"] = "rule", ["ipv6"] = false,
            ["proxies"] = new JsonArray(proxy), ["rules"] = new JsonArray("MATCH,proxy"),
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
