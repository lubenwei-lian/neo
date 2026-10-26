using Neo.Core.Models;

namespace Neo.Core.Core;

/// <summary>节点可用的内核：只用 sing-box / 只用 xray / 两者都行。</summary>
public enum KernelSupportKind { SingBoxOnly, XrayOnly, Both, Other }

/// <summary>
/// 节点 → 内核能力的唯一说明出口。连接页用它告诉用户「当前节点只能用哪个内核」，
/// 避免用户看到「没开 TUN 却是 sing-box」时无法理解。
/// </summary>
public static class KernelSupport
{
    public static KernelSupportKind Of(NodeItem n)
    {
        if (n == null) return KernelSupportKind.Other;
        if (n.IsClashPassthrough || n.IsCustomConfig || n.IsNaive) return KernelSupportKind.Other;

        var sbOnly = n.RequiresSingBox
            || n.Protocol is NodeProtocol.Hysteria2 or NodeProtocol.TUIC or NodeProtocol.AnyTls or NodeProtocol.WireGuard;
        var xrayOnly = n.RequiresXray;

        if (sbOnly && !xrayOnly) return KernelSupportKind.SingBoxOnly;
        if (xrayOnly && !sbOnly) return KernelSupportKind.XrayOnly;
        return KernelSupportKind.Both;
    }

    public static string Describe(NodeItem n)
    {
        if (n == null) return "";
        if (n.IsClashPassthrough) return "内核：mihomo（整份 Clash 配置）";
        if (n.IsCustomConfig) return "内核：" + n.CustomEngine + "（按自定义配置）";
        if (n.IsNaive) return "内核：sing-box + naive 辅助进程";

        return Of(n) switch
        {
            KernelSupportKind.SingBoxOnly => "内核：仅 sing-box（该协议 xray 不支持）",
            KernelSupportKind.XrayOnly => "内核：仅 xray（该传输/加密 sing-box 不支持）",
            _ => "内核：sing-box / xray 均可",
        };
    }
}
