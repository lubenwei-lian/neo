namespace Neo.Core;

/// <summary>连接、代理修复及残留清理共用的本地代理格式与归属判定。</summary>
public static class LocalProxySettings
{
    public static string Server => BuildServer(LocalPorts.Socks, LocalPorts.Http);

    public static string BuildServer(int socksPort, int httpPort) =>
        $"http=127.0.0.1:{httpPort};https=127.0.0.1:{httpPort};socks=127.0.0.1:{socksPort}";

    public static bool IsOwnedServer(string? server, params int[] ports)
    {
        if (string.IsNullOrWhiteSpace(server)) return false;
        var parts = server.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;
        foreach (var part in parts)
        {
            var endpoint = part;
            var equals = part.IndexOf('=');
            if (equals >= 0)
            {
                var protocol = part[..equals].Trim().ToLowerInvariant();
                if (protocol is not ("http" or "https" or "socks")) return false;
                endpoint = part[(equals + 1)..].Trim();
            }
            var colon = endpoint.LastIndexOf(':');
            if (colon < 0 || !int.TryParse(endpoint[(colon + 1)..], out var port) || !ports.Contains(port))
                return false;
            var host = endpoint[..colon].Trim();
            if (!host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                && !host.Equals("[::1]", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
}
