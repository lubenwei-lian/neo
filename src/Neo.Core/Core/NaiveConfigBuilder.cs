using System.Text.Json;
using System.Text.Json.Nodes;
using Neo.Core.Models;

namespace Neo.Core.Core;

/// <summary>
/// naiveproxy 客户端配置（naive.exe 的 JSON）：
///   { "listen": "socks://127.0.0.1:&lt;port&gt;", "proxy": "https://user:pass@host:port", ... }
/// naive 只提供 socks 入口，系统代理/TUN 由前置内核（sing-box）再包一层；
/// 因此这里只负责生成 naive 需要的这一段，前置配置由 ConnectionService 用「socks 出站」构造。
/// </summary>
public static class NaiveConfigBuilder
{
    public static string Build(NodeItem n, int listenPort)
    {
        if (n.Protocol is not NodeProtocol.Naive) throw new ArgumentException("不是 naive 节点");
        if (!n.IsValid) throw new ArgumentException("naive 节点无效（缺服务器/端口）");

        var host = n.Server;
        var user = Uri.EscapeDataString(n.Username ?? "");
        var pass = Uri.EscapeDataString(n.Password ?? "");
        var root = new JsonObject
        {
            ["listen"] = "socks://127.0.0.1:" + listenPort,
            ["proxy"] = $"https://{user}:{pass}@{host}:{n.Port}",
            ["log"] = "",
            // naive 的并发 HEAD/GET 预热（官方配置项；0 表示关闭）
            ["insecure_concurrency"] = 8,
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
