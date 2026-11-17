using Neo.Core.Models;

namespace Neo.Core.Core;

/// <summary>节点可用的内核：只用 sing-box / 只用 xray / 两者都行。</summary>
public enum KernelSupportKind { SingBoxOnly, XrayOnly, Both, Other }

/// <summary>
/// 节点 → Neo 当前已实现的配置路径。不能将应用限制解释为内核所有版本的协议能力。
/// 避免用户看到「没开 TUN 却是 sing-box」时无法理解。
/// </summary>
public static class KernelSupport
{
    public static KernelSupportKind Of(NodeItem? n)
    {
        if (n == null) return KernelSupportKind.Other;
        if (n.IsClashPassthrough || n.IsCustomConfig || n.IsNaive || n.Protocol is NodeProtocol.ShadowsocksR) return KernelSupportKind.Other;

        var sbOnly = n.RequiresSingBox
            || n.Protocol is NodeProtocol.Hysteria2 or NodeProtocol.TUIC or NodeProtocol.AnyTls or NodeProtocol.WireGuard;
        var xrayOnly = n.RequiresXray;

        if (sbOnly && xrayOnly) return KernelSupportKind.Other;
        if (sbOnly && !xrayOnly) return KernelSupportKind.SingBoxOnly;
        if (xrayOnly && !sbOnly) return KernelSupportKind.XrayOnly;
        return KernelSupportKind.Both;
    }

    public static string Describe(NodeItem? n)
    {
        if (n == null) return "";
        if (n.IsClashPassthrough) return "内核：mihomo（整份 Clash 配置）";
        if (n.IsCustomConfig) return "内核：" + n.CustomEngine + "（按自定义配置）";
        if (n.IsNaive) return "内核：sing-box + naive 辅助进程";
        if (n.Protocol is NodeProtocol.ShadowsocksR) return "内核：sing-box + mihomo 辅助进程";

        return Of(n) switch
        {
            KernelSupportKind.SingBoxOnly => "内核：sing-box（Neo 当前配置路径）",
            KernelSupportKind.XrayOnly => "内核：xray（Neo 当前配置路径）",
            KernelSupportKind.Other => "内核：节点能力冲突，当前没有兼容的配置路径",
            _ => "内核：sing-box / xray 均可",
        };
    }
}
