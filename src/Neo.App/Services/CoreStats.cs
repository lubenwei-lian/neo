namespace Neo.App.Services;

public static class CoreStats
{
    public readonly record struct Snapshot(long Uplink, long Downlink);

    public static Snapshot? Query()
    {
        if (ConnectionService.CoreKind == "sing-box")
        {
            var s = SingBoxStats.Query();
            return s is { } v ? new Snapshot(v.Uplink, v.Downlink) : null;
        }

        var x = XrayStats.Query();
        return x is { } xv ? new Snapshot(xv.Uplink, xv.Downlink) : null;
    }

    public static int ActiveConnections()
    {
        if (ConnectionService.CoreKind == "sing-box")
            return SingBoxStats.Query()?.ActiveConnections ?? 0;

        return TcpConnCounter.CountEstablished(Neo.Core.Core.XrayConfigBuilder.DefaultSocksPort);
    }
}
