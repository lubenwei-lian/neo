using System.Text;
using Neo.Core.Models;

namespace Neo.Core.Core;

/// <summary>
/// mihomo（Clash.Meta）配置生成：把订阅里的**整份 Clash YAML** 原样交给 mihomo 跑，
/// 只覆盖本地接入相关的顶层键（端口/控制器/日志级别/绑定地址）。
/// 为什么这么做：成熟客户端对 Clash 订阅是「整份交给内核」，不拆节点——
///   proxy-providers、复杂 rule-provider、脚本模式这些用 NodeItem 模型表达不了；
/// 我们的做法：仍然把订阅拆成节点（给测速/故障转移用），**额外**多一个「Clash 直通」节点，
///   用户想用完整 Clash 语义时选它，不想用就忽略（互不影响）。
/// 安全边界：
///   · 只改顶层键；proxies / proxy-groups / rules / dns 等一律原样保留；
///   · 绑定地址强制 127.0.0.1（不允许 allow-lan，避免把端口暴露到局域网）；
///   · 端口固定用 neo 的 socks/http 端口，系统代理/看门狗/测速逻辑无需特殊处理。
/// </summary>
public static class MihomoConfigBuilder
{
    /// <summary>需要被覆盖的顶层键（存在即删，再统一写新值）。</summary>
    private static readonly HashSet<string> OverriddenKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "port", "socks-port", "mixed-port", "redir-port", "tproxy-port",
        "allow-lan", "bind-address", "external-controller", "external-ui",
        "secret", "mode", "log-level", "ipv6",
    };

    public static string Build(NodeItem node, int httpPort, int socksPort, int apiPort, string logLevel)
    {
        var yamlPath = node.Extras.TryGetValue("yaml_path", out var p) ? p : "";
        if (string.IsNullOrEmpty(yamlPath) || !File.Exists(yamlPath))
            throw new FileNotFoundException("Clash 直通配置不存在（订阅缓存可能被清理），请重新更新订阅", yamlPath);

        var text = File.ReadAllText(yamlPath).Replace("\r\n", "\n").Replace('\r', '\n');
        var kept = new List<string>(text.Length / 40 + 16);
        foreach (var line in text.Split('\n'))
        {
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && line[0] != '#')
            {
                var colon = line.IndexOf(':');
                if (colon > 0)
                {
                    var key = line[..colon].Trim();
                    if (OverriddenKeys.Contains(key)) continue;
                }
            }
            kept.Add(line);
        }

        var level = logLevel is "none" or "error" or "warning" or "info" or "debug" ? logLevel : "warning";

        // GEOIP 规则需要 mmdb（mihomo 默认），没有 mmdb 但有 geoip.dat 时退回 geodata-mode，
        // 避免 mihomo 启动时联网下载 MMDB（下载失败会 fatal，整份配置起不来）。
        bool hasMmdb, hasGeoIpDat;
        try
        {
            hasMmdb = File.Exists(Path.Combine(NeoPaths.RuntimeDir, "Country.mmdb"));
            hasGeoIpDat = File.Exists(Path.Combine(NeoPaths.RuntimeDir, "geoip.dat"));
        }
        catch { hasMmdb = true; hasGeoIpDat = false; }

        var sb = new StringBuilder();
        sb.Append("port: ").Append(httpPort).Append('\n');
        sb.Append("socks-port: ").Append(socksPort).Append('\n');
        sb.Append("mixed-port: 0\n");
        sb.Append("bind-address: \"127.0.0.1\"\n");
        sb.Append("allow-lan: false\n");
        sb.Append("external-controller: \"127.0.0.1:").Append(apiPort).Append("\"\n");
        sb.Append("secret: \"").Append(SingBoxConfigBuilder.ClashApiSecret).Append("\"\n");
        sb.Append("mode: rule\n");
        sb.Append("log-level: ").Append(level).Append('\n');
        sb.Append("ipv6: false\n");
        sb.Append("geo-auto-update: false\n");
        if (!hasMmdb && hasGeoIpDat) sb.Append("geodata-mode: true\n");
        sb.Append(string.Join('\n', kept));
        if (!sb.ToString().EndsWith('\n')) sb.Append('\n');
        return sb.ToString();
    }
}
