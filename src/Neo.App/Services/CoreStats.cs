namespace Neo.App.Services;

public static class CoreStats
{
    public readonly record struct Snapshot(long Uplink, long Downlink);

    public static Snapshot? Query()
    {
        // mihomo 的 Clash API 与 sing-box 的 clash_api 同协议（/connections、Bearer secret），
        // 端口/密钥也是同一组常量，因此直接复用 SingBoxStats。
        //   统计/连接数必须按 clash_api 口径取，否则永远返回 null、界面冻结在 0。
        if (ConnectionService.CoreKind is "sing-box" or "mihomo" or "naive")
        {
            var s = SingBoxStats.Query();
            return s is { } v ? new Snapshot(v.Uplink, v.Downlink) : null;
        }

        var x = XrayStats.Query();
        return x is { } xv ? new Snapshot(xv.Uplink, xv.Downlink) : null;
    }

    public static int ActiveConnections()
    {
        if (ConnectionService.CoreKind is "sing-box" or "mihomo" or "naive")
            return SingBoxStats.Query()?.ActiveConnections ?? 0;

        return TcpConnCounter.CountEstablished(Neo.Core.LocalPorts.Socks);
    }
}
