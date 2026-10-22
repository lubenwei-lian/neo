namespace Neo.Core.Models;

public enum NodeProtocol
{
    Unknown,
    VMess,
    VLESS,
    Trojan,
    Shadowsocks,
    Hysteria2,
    TUIC,
}

public sealed class NodeItem
{
    public string Name { get; set; } = "";
    public NodeProtocol Protocol { get; set; } = NodeProtocol.Unknown;
    public string Server { get; set; } = "";
    public int Port { get; set; }

    public string? SourceSubscription { get; set; }

    public string? Uuid { get; set; }
    public string? Password { get; set; }
    public string? Method { get; set; }
    public int AlterId { get; set; }
    public string? Cipher { get; set; }
    public string? Flow { get; set; }

    public string Network { get; set; } = "tcp";
    public string? Path { get; set; }
    public string? HostHeader { get; set; }
    public string? ServiceName { get; set; }
    public string? HeaderType { get; set; }

    /// <summary>
    /// Shadowsocks SIP003 插件名（如 obfs-local / v2ray-plugin / shadow-tls）；null = 无插件。
    /// 仅 sing-box 路径会写入 outbound（xray 不支持 SIP003 插件）；可空，兼容旧 state/subscription-cache。
    /// </summary>
    public string? Plugin { get; set; }

    /// <summary>SIP003 插件参数（如 "obfs=http;obfs-host=bing.com"）；null/空 = 无参数。</summary>
    public string? PluginOpts { get; set; }

    public string Security { get; set; } = "none";
    public string? Sni { get; set; }
    public string? Fingerprint { get; set; }
    public string? PublicKey { get; set; }
    public string? ShortId { get; set; }
    public string? SpiderX { get; set; }
    public bool AllowInsecure { get; set; }

    public Dictionary<string, string> Extras { get; } = new();

    public int RealDelay { get; set; }

    /// TCP 握手延迟（毫秒）：0 = 未测，-1 = 超时，>0 = 握手毫秒。
    /// 与 <see cref="RealDelay"/> 的区别：RealDelay 是「经代理真实请求一次」的端到端延迟
    /// （含 DNS/TLS/代理链路），TcpDelay 只测到节点服务器的 TCP 握手，速度快、不经过代理；
    /// 两者互不覆盖，可同时显示供参考。
    public int TcpDelay { get; set; }

    /// 最近一次 TCP 握手延迟测试的时间（本地时间）；默认值表示从未测过。
    public DateTime TcpDelayAt { get; set; }

    /// 实测出口国家代码（小写 ISO，如 "jp"）。空 = 未检测/检测失败。
    /// 由「位置检测」写入，仅本进程会话有效（不落盘）。
    public string? ExitCountry { get; set; }

    /// 实测出口 IP（「位置检测」写入，仅本进程会话有效）。
    public string? ExitIp { get; set; }

    public string EndPoint => $"{Server}:{Port}";

    public bool IsValid => !string.IsNullOrWhiteSpace(Server) && Port > 0;

    public override string ToString() =>
        $"[{Protocol}] {Name} @ {Server}:{Port} (net={Network}, sec={Security})";
}
