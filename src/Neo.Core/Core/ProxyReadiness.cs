using System.Net;
using System.Net.Sockets;

namespace Neo.Core.Core;

/// <summary>只探测新内核的本机监听器；不把外网站点的延迟加入启动关键路径。</summary>
public static class ProxyReadiness
{
    public static async Task<bool> WaitAsync(Func<bool> isRunning, int socksPort, int httpPort,
        TimeSpan timeout, CancellationToken ct = default, bool negotiateSocks = true,
        Func<bool>? isCurrent = null)
    {
        ArgumentNullException.ThrowIfNull(isRunning);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(timeout);
        try
        {
            while (true)
            {
                budget.Token.ThrowIfCancellationRequested();
                if (!isRunning() || isCurrent?.Invoke() == false) return false;
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                attempt.CancelAfter(TimeSpan.FromMilliseconds(250));
                try
                {
                    using var socks = new TcpClient();
                    using var http = httpPort != socksPort ? new TcpClient() : null;
                    var socksConnect = socks.ConnectAsync(IPAddress.Loopback, socksPort, attempt.Token).AsTask();
                    if (http != null)
                        await Task.WhenAll(socksConnect, http.ConnectAsync(IPAddress.Loopback, httpPort, attempt.Token).AsTask()).ConfigureAwait(false);
                    else await socksConnect.ConfigureAwait(false);
                    if (negotiateSocks)
                    {
                        var stream = socks.GetStream();
                        await stream.WriteAsync(new byte[] { 5, 1, 0 }, attempt.Token).ConfigureAwait(false);
                        var reply = new byte[2];
                        await stream.ReadExactlyAsync(reply, attempt.Token).ConfigureAwait(false);
                        if (reply[0] != 5 || reply[1] != 0) return false;
                    }
                    return !budget.IsCancellationRequested && isRunning() && isCurrent?.Invoke() != false;
                }
                catch (SocketException) { }
                catch (IOException) { }
                catch (OperationCanceledException) when (!budget.IsCancellationRequested) { }
                await Task.Delay(TimeSpan.FromMilliseconds(40), budget.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }
}
