using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Neo.Core.Models;

/// <summary>Configuration identity. Runtime measurements and display names never authorize policy.</summary>
public static class NodeIdentity
{
    private static readonly HashSet<string> RuntimeFields = new(StringComparer.Ordinal)
    {
        nameof(NodeItem.Name), nameof(NodeItem.SourceSubscription), nameof(NodeItem.RealDelay),
        nameof(NodeItem.RealDelayNote), nameof(NodeItem.TcpDelay), nameof(NodeItem.TcpDelayAt),
        nameof(NodeItem.TlsChecked), nameof(NodeItem.ExitCountry), nameof(NodeItem.ExitIp),
    };
    private static readonly PropertyInfo[] Fields = typeof(NodeItem).GetProperties()
        .Where(p => (p.CanWrite || p.Name == nameof(NodeItem.Extras)) && !RuntimeFields.Contains(p.Name))
        .OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();

    public static string Fingerprint(NodeItem node)
    {
        var values = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in Fields)
            values[p.Name] = p.Name == nameof(NodeItem.Extras)
                ? new SortedDictionary<string, string>(node.Extras, StringComparer.Ordinal) : p.GetValue(node);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(values)));
    }
}
