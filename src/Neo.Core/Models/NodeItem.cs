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

    public string Security { get; set; } = "none";
    public string? Sni { get; set; }
    public string? Fingerprint { get; set; }
    public string? PublicKey { get; set; }
    public string? ShortId { get; set; }
    public string? SpiderX { get; set; }
    public bool AllowInsecure { get; set; }

    public Dictionary<string, string> Extras { get; } = new();

    public int RealDelay { get; set; }

    /// 实测出口国家代码（小写 ISO，如 "jp"）。空 = 未检测/检测失败。
    /// 由「位置检测」写入，仅本进程会话有效（不落盘）。
    public string? ExitCountry { get; set; }

    public string EndPoint => $"{Server}:{Port}";

    public bool IsValid => !string.IsNullOrWhiteSpace(Server) && Port > 0;

    public override string ToString() =>
        $"[{Protocol}] {Name} @ {Server}:{Port} (net={Network}, sec={Security})";
}
