using System.Net.NetworkInformation;

namespace Neo.App.Services;

public static class TcpConnCounter
{
    public static int CountEstablished(int localPort)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpConnections()
                .Count(c => c.LocalEndPoint.Port == localPort && c.State == TcpState.Established);
        }
        catch { return 0; }
    }
}
