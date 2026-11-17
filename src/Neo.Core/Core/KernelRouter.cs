using Neo.Core.Models;

namespace Neo.Core.Core;

/// <summary>
/// 「协议 → 内核」手动映射（对标成熟客户端的 CoreTypeItem 机制）。
/// 默认 auto：按 Neo 已实现的配置路径选内核；具体二进制能力应另做版本化校验。
/// 手动映射只在**能力允许**时生效，冲突时静默回退 auto（并在调用处记日志），
/// 例如当前尚未实现 Xray 的 Hysteria2 / WireGuard 配置生成，不能直接放开映射。
/// </summary>
public static class KernelRouter
{
    public enum Choice { Auto, Xray, SingBox }

    private static readonly object MapGate = new();
    private static Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

    public static void Replace(IEnumerable<KeyValuePair<string, string>> entries)
    {
        var next = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (protocol, kernel) in entries)
        {
            var k = (kernel ?? "").Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(protocol) && k is "xray" or "sing-box" or "singbox")
                next[protocol] = k == "singbox" ? "sing-box" : k;
        }
        lock (MapGate) Volatile.Write(ref _map, next);
    }

    public static void Clear() => Replace(Array.Empty<KeyValuePair<string, string>>());

    public static void Set(string protocol, string kernel)
    {
        if (string.IsNullOrWhiteSpace(protocol)) return;
        lock (MapGate)
        {
            var next = new Dictionary<string, string>(_map, StringComparer.OrdinalIgnoreCase);
            var k = (kernel ?? "").Trim().ToLowerInvariant();
            if (k is "xray" or "sing-box" or "singbox") next[protocol] = k == "singbox" ? "sing-box" : k;
            else next.Remove(protocol);
            Volatile.Write(ref _map, next);
        }
    }

    /// <summary>解析某个协议的手动映射（不做能力校验）。</summary>
    public static Choice Resolve(string protocol)
    {
        if (!Volatile.Read(ref _map).TryGetValue(protocol, out var k)) return Choice.Auto;
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
        var support = KernelSupport.Of(n);
        if (n.IsClashPassthrough || n.IsCustomConfig || n.IsNaive || support == KernelSupportKind.Other)
            return Choice.Auto;
        if (c == Choice.Xray)
        {
            if (support == KernelSupportKind.SingBoxOnly) return Choice.Auto;
            return Choice.Xray;
        }
        if (c == Choice.SingBox)
        {
            if (support == KernelSupportKind.XrayOnly) return Choice.Auto;
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
