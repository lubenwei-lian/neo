using Neo.Core.Models;

namespace Neo.Core.Core;

/// <summary>
/// 「协议 → 内核」手动映射（对标成熟客户端的 CoreTypeItem 机制）。
/// 默认 auto：按实测能力自动选（xhttp/kcp/VLESS 加密/pqv/vcn → xray；hy2/tuic/anytls/ss-plugin/h2 → sing-box）。
/// 手动映射只在**能力允许**时生效，冲突时静默回退 auto（并在调用处记日志），
/// 避免用户把 hy2 指到 xray 之后得到一个"能生成但内核拒绝启动"的配置。
/// </summary>
public static class KernelRouter
{
    public enum Choice { Auto, Xray, SingBox }

    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase);

    public static void Clear() => Map.Clear();

    public static void Set(string protocol, string kernel)
    {
        if (string.IsNullOrWhiteSpace(protocol)) return;
        var k = (kernel ?? "").Trim().ToLowerInvariant();
        if (k is "xray" or "sing-box" or "singbox") Map[protocol] = k == "singbox" ? "sing-box" : k;
        else Map.Remove(protocol);
    }

    /// <summary>解析某个协议的手动映射（不做能力校验）。</summary>
    public static Choice Resolve(string protocol)
    {
        if (!Map.TryGetValue(protocol, out var k)) return Choice.Auto;
        return k switch
        {
            "xray" => Choice.Xray,
            "sing-box" => Choice.SingBox,
            _ => Choice.Auto,
        };
    }

    /// <summary>
    /// 对具体节点解析映射并做能力校验：能力不允许时返回 Auto（调用方保持自动判定）。
    /// </summary>
    public static Choice ResolveFor(NodeItem n)
    {
        var c = Resolve(n.Protocol.ToString());
        if (c == Choice.Xray)
        {
            if (n.RequiresSingBox || n.Protocol is NodeProtocol.Hysteria2 or NodeProtocol.TUIC
                || n.Protocol is NodeProtocol.AnyTls || n.IsClashPassthrough) return Choice.Auto;
            return Choice.Xray;
        }
        if (c == Choice.SingBox)
        {
            if (n.RequiresXray || n.IsClashPassthrough) return Choice.Auto;
            return Choice.SingBox;
        }
        return Choice.Auto;
    }

    /// <summary>
    /// 把「自动判定结果」与手动映射合起来：Auto 用 baseValue，否则用映射结果。
    /// baseValue 由调用方按能力/TUN 算好（例如 TUN 恒 true）。
    /// </summary>
    public static bool Apply(NodeItem n, bool baseValue)
    {
        var c = ResolveFor(n);
        return c switch
        {
            Choice.Xray => false,
            Choice.SingBox => true,
            _ => baseValue,
        };
    }
}
