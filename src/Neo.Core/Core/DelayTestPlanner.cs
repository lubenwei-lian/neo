using Neo.Core.Models;

namespace Neo.Core.Core;

/// <summary>节点的传输层类型：TCP 有端口可握手，UDP 只能真连。</summary>
public enum DelayTransport { Tcp, Udp }

/// <summary>两套测速方案：TCP 握手（快）与真连（准）。</summary>
public enum DelayScheme { TcpHandshake, RealConnection }

/// <summary>
/// 测速方案选择器 —— 唯一出口。
///
/// 设计目标：不再按「今天哪个协议出问题」逐个打补丁，而是按节点能力自动分类：
///   · TCP 类（VMess/VLESS/Trojan/SS/SOCKS/HTTP/AnyTLS/Naive）→ 方案 1：TCP 握手，几百毫秒测完；
///   · UDP 类（TUIC/Hysteria2/WireGuard/mKCP）→ 方案 2：起临时内核真连；
///   · Clash 直通 / 自定义配置 / 无效节点 → 不测。
///
/// 两类自动测速均遵守节点声明的证书策略；证书不受信时只报告失败，不自动放宽。
/// </summary>
public sealed record DelayPlan(
    DelayScheme Scheme,
    DelayTransport Transport,
    bool RelaxTlsOnCertFailure,
    string Reason,
    int TimeoutMs = 9000,
    int Attempts = 3);

public static class DelayTestPlanner
{
    public static DelayTransport TransportOf(NodeItem n) =>
        n.Protocol is NodeProtocol.Hysteria2 or NodeProtocol.TUIC or NodeProtocol.WireGuard
            || n.IsNaive && n.Extras.TryGetValue("naive_transport", out var naiveTransport) && naiveTransport == "quic"
            || (n.Network ?? "").Trim().ToLowerInvariant() is "kcp" or "mkcp"
            ? DelayTransport.Udp
            : DelayTransport.Tcp;

    /// <summary>是否具备「TCP 端口可以握手」的条件（决定能否用方案 1）。</summary>
    public static bool HasTcpPort(NodeItem n)
    {
        if (n == null || !n.IsValid) return false;
        if (n.IsClashPassthrough || n.IsCustomConfig) return false;
        return TransportOf(n) == DelayTransport.Tcp;
    }

    /// <summary>UDP 类节点没有 TCP 端口，必须走方案 2。</summary>
    public static bool RequiresRealConnection(NodeItem n) => !HasTcpPort(n);

    public static DelayPlan PlanFor(NodeItem n)
    {
        if (n == null || !n.IsValid)
            return new DelayPlan(DelayScheme.RealConnection, DelayTransport.Tcp, false, "节点无效，跳过");
        if (n.IsClashPassthrough || n.IsCustomConfig)
            return new DelayPlan(DelayScheme.RealConnection, TransportOf(n), false, "直通配置不支持单点测速");

        var t = TransportOf(n);
        // 自动测速遵守显式证书策略；快速 TCP 握手由独立按钮提供。
        // 「TCP 握手」只服务于显式的快速测延迟按钮，不参与自动方案选择。
        return t == DelayTransport.Udp
            ? new DelayPlan(DelayScheme.RealConnection, t, RelaxTlsOnCertFailure: false,
                Reason: "UDP real connection obeys the explicit certificate policy",
                TimeoutMs: 15000, Attempts: 2)
            : new DelayPlan(DelayScheme.RealConnection, t, RelaxTlsOnCertFailure: false,
                Reason: "TCP 协议，走真连并严格校验证书",
                TimeoutMs: 9000, Attempts: 3);
    }
}
