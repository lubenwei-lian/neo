namespace Neo.App.Services;

public static class CoreStats
{
    public readonly record struct Snapshot(long Uplink, long Downlink);

    public static Snapshot? Query()
    {
        // mihomo 的 Clash API 与 sing-box 的 clash_api 同协议（/connections、Bearer secret），
        // 端口/密钥也是同一组常量，因此直接复用 SingBoxStats。
        if (ConnectionService.CoreKind is "sing-box" or "mihomo")
        {
            var s = SingBoxStats.Query();
            return s is { } v ? new Snapshot(v.Uplink, v.Downlink) : null;
        }

        var x = XrayStats.Query();
        return x is { } xv ? new Snapshot(xv.Uplink, xv.Downlink) : null;
    }

    public static int ActiveConnections()
    {
        if (ConnectionService.CoreKind is "sing-box" or "mihomo")
            return SingBoxStats.Query()?.ActiveConnections ?? 0;

        return TcpConnCounter.CountEstablished(Neo.Core.LocalPorts.Socks);
    }
}
