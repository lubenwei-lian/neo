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

    /// <summary>anytls（xray 不支持，只能走 sing-box 内核）。</summary>
    AnyTls,

    /// <summary>socks5 出站节点（socks:// / socks5:// 分享链接）。</summary>
    Socks,

    /// <summary>
    /// Clash 直通节点：不拆节点，把订阅里**整份 Clash YAML** 交给 mihomo 内核跑。
    /// 用于 proxy-providers / 复杂 rule-provider 这类用 NodeItem 模型表达不了的订阅。
    /// </summary>
    MihomoConfig,

    /// <summary>
    /// WireGuard（sing-box 的 endpoints 形态；xray 不支持 WG 出站）。
    /// 分享链接：wireguard://&lt;私钥&gt;@host:port?publickey=...&amp;address=... 或整份 .conf。
    /// </summary>
    WireGuard,

    /// <summary>
    /// naiveproxy（HTTP/2 隧道）：项目里由「naive.exe 辅助进程 + sing-box 前置」两段组成
    /// （naive 只提供 socks 入口，系统代理/TUN 需要前置内核提供 http 入站）。
    /// 分享链接：naive+https://user:pass@host:port#name。
    /// </summary>
    Naive,

    /// <summary>
    /// 自定义配置直通：订阅里给的是**整份 xray/sing-box JSON**（不是单个节点）。
    /// 连接时只改写本地接入端口/统计接口，其余 inbounds 之外的 outbounds/routing 原样保留。
    /// Extras["config_path"] = 保存的 JSON 路径；Extras["engine"] = "xray" / "sing-box"。
    /// </summary>
    CustomConfig,
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

    /// <summary>
    /// UI 统一口径：按「真连测速」开关选主口径，主口径没有结果（0=未测）时回退到另一口径。
    /// 为什么必须回退：hy2/TUIC/mKCP 是 UDP 协议、没有 TCP 端口，TcpDelay 永远是 0（跳过）；
    /// 只按开关取值的话，即使真连测出了延迟，卡片/排序/连接页下拉也全都显示「未测速」
    /// （2026-10-23 用户反馈：测出来了但界面不显示）。
    /// </summary>
    public int DisplayDelay(bool preferReal)
        => preferReal
            ? (RealDelay != 0 ? RealDelay : TcpDelay)
            : (TcpDelay != 0 ? TcpDelay : RealDelay);

    // ================= 2026-10-23 第二批：协议能力字段 =================
    // 说明：以下字段全部可空/默认值，旧 state 与旧订阅缓存反序列化后保持原行为。

    /// <summary>socks5 用户名（仅 NodeProtocol.Socks 使用；也可用于其它需要认证的协议）。</summary>
    public string? Username { get; set; }

    /// <summary>
    /// VLESS 后量子加密串（xray 25+）：形如 "mlkem768x25519plus.native.0rtt.xxx"。
    /// 空 / "none" = 不加密。sing-box 1.14 不支持该字段 ⇒ 这类节点必须走 xray 内核。
    /// </summary>
    public string? VlessEncryption { get; set; }

    /// <summary>REALITY 的 ML-DSA-65 后量子校验（分享链接参数 pqv）。空 = 不校验。</summary>
    public string? Mldsa65Verify { get; set; }

    /// <summary>TLS ECH 配置列表（base64，xray 的 echConfigList / sing-box 的 tls.ech.config）。空 = 不用 ECH。</summary>
    public string? EchConfigList { get; set; }

    /// <summary>证书名校验（分享链接参数 vcn → xray tlsSettings.verifyPeerCertByName）。空 = 不启用。</summary>
    public string? VerifyPeerCertByName { get; set; }

    /// <summary>
    /// 证书 pin：pcs（xray pinnedPeerCertSha256）/ hy2 的 pinSHA256。
    /// xray 要求 64 位十六进制（sha256）；hy2 的 pin 另存 Extras["pinsha256"]。
    /// </summary>
    public string? CertSha256 { get; set; }

    /// <summary>mKCP 种子（分享链接 seed）。空 = 不用种子。</summary>
    public string? KcpSeed { get; set; }

    /// <summary>mKCP MTU（分享链接 mtu）；null = 用默认 1350。</summary>
    public int? KcpMtu { get; set; }

    /// <summary>xhttp 模式（auto / packet-up / stream-up / stream-one）。空 = auto。</summary>
    public string? XhttpMode { get; set; }

    /// <summary>xhttp 的 extra JSON 对象原文（分享链接 extra=）。空 = 不写。</summary>
    public string? XhttpExtra { get; set; }

    /// <summary>gRPC authority（分享链接 authority=）；空 = 用 Host 头/服务器名。</summary>
    public string? GrpcAuthority { get; set; }

    /// <summary>gRPC 模式（gun / multi）；空 = 默认。</summary>
    public string? GrpcMode { get; set; }

    /// <summary>hysteria2 gecko 混淆的最小分片（分享链接 minPacketSize）；空 = 非 gecko。</summary>
    public int? GeckoMinPacketSize { get; set; }

    /// <summary>hysteria2 gecko 混淆的最大分片（分享链接 maxPacketSize）；空 = 非 gecko。</summary>
    public int? GeckoMaxPacketSize { get; set; }

    // ================= WireGuard（sing-box endpoint）=================
    /// <summary>WireGuard 对端公钥（分享链接 publickey）。</summary>
    public string? WgPublicKey { get; set; }

    /// <summary>WireGuard 预共享密钥（分享链接 presharedkey）；空 = 不用。</summary>
    public string? WgPresharedKey { get; set; }

    /// <summary>本机接口地址，可多个用逗号分隔（分享链接 address / .conf 的 Address）。</summary>
    public string? WgAddress { get; set; }

    /// <summary>WireGuard MTU（.conf 的 MTU / 分享链接 mtu）；空 = 不写（用内核默认）。</summary>
    public int? WgMtu { get; set; }

    /// <summary>WARP reserved 字节（三字节 base64 或 "0,0,0"）；空 = 不写。</summary>
    public string? WgReserved { get; set; }

    /// <summary>WireGuard DNS（.conf 的 DNS）；空 = 不写。</summary>
    public string? WgDns { get; set; }

    /// <summary>持久保活秒数；null = 不写（sing-box 默认 0/关闭）。</summary>
    public int? WgKeepAlive { get; set; }

    /// <summary>
    /// 该节点是否用到 xray 专有能力（sing-box 1.14 不支持，实测见台账）：
    /// mKCP / xhttp 传输、VLESS 后量子加密、REALITY pqv、证书名校验 vcn。
    /// TUN 模式固定跑 sing-box ⇒ 这类节点在 TUN 下必须明确拒绝，不能静默降级成「能连上但走错传输」。
    /// </summary>
    public bool RequiresXray =>
        Network.ToLowerInvariant() is "kcp" or "xhttp"
        || (!string.IsNullOrEmpty(VlessEncryption) && !VlessEncryption.Equals("none", StringComparison.OrdinalIgnoreCase))
        || !string.IsNullOrEmpty(Mldsa65Verify)
        || !string.IsNullOrEmpty(VerifyPeerCertByName);

    /// <summary>
    /// 该节点是否必须走 sing-box（xray 无法承载）：
    /// anytls、ss SIP003 插件、h2（xray 26 已移除 h2/quic）、hy2 gecko、
    /// 以及「无 TLS 的明文 VLESS」——xray 26 实测直接拒绝启动（见台账 F1）。
    /// 注意：hysteria2 / tuic 恒走 sing-box，由调用方另行判断，不在此列。
    /// </summary>
    public bool RequiresSingBox =>
        Protocol is NodeProtocol.AnyTls or NodeProtocol.WireGuard
        || (Protocol is NodeProtocol.Shadowsocks && !string.IsNullOrEmpty(Plugin))
        || Network.ToLowerInvariant() is "h2"
        || GeckoMinPacketSize.HasValue || GeckoMaxPacketSize.HasValue
        // 明文 VLESS 只有 xray 26 会拒绝（公网地址），但带 xray 专有能力时仍归 xray —— 以 xray 为准。
        || (Protocol is NodeProtocol.VLESS
            && (Security ?? "none").Equals("none", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(VlessEncryption)
            && !RequiresXray);

    /// 实测出口国家代码（小写 ISO，如 "jp"）。空 = 未检测/检测失败。
    /// 由「位置检测」写入，仅本进程会话有效（不落盘）。
    public string? ExitCountry { get; set; }

    /// 实测出口 IP（「位置检测」写入，仅本进程会话有效）。
    public string? ExitIp { get; set; }

    /// <summary>是否为「Clash 直通」节点（整份 YAML 交给 mihomo，本身没有 server:port）。</summary>
    public bool IsClashPassthrough => Protocol is NodeProtocol.MihomoConfig;

    /// <summary>自定义整份配置直通节点。</summary>
    public bool IsCustomConfig => Protocol is NodeProtocol.CustomConfig;

    /// <summary>naiveproxy 节点（两段式：naive.exe 辅助 + 前置内核）。</summary>
    public bool IsNaive => Protocol is NodeProtocol.Naive;

    /// <summary>自定义配置使用的内核（"xray" / "sing-box"；由导入时探测）。</summary>
    public string CustomEngine => Extras.TryGetValue("engine", out var e) ? e : "xray";

    public string EndPoint => IsClashPassthrough
        ? "Clash 配置"
        : IsCustomConfig
            ? "自定义配置"
            : $"{Server}:{Port}";

    public bool IsValid => IsClashPassthrough
        ? Extras.TryGetValue("yaml_path", out var y) && !string.IsNullOrWhiteSpace(y) && File.Exists(y)
        : IsCustomConfig
        ? Extras.TryGetValue("config_path", out var cp) && !string.IsNullOrWhiteSpace(cp) && File.Exists(cp)
        : !string.IsNullOrWhiteSpace(Server) && Port > 0;

    public override string ToString() =>
        $"[{Protocol}] {Name} @ {Server}:{Port} (net={Network}, sec={Security})";
}
