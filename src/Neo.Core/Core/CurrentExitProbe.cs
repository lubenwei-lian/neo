using System.Net;
using System.Net.Sockets;

namespace Neo.Core.Core;

public sealed record CurrentExitResult(string Country, string Ip, string Source, NetworkRouteEvidence Route, string Detail)
{
    public bool Confirmed => Country.Length == 2 && IPAddress.TryParse(Ip, out _);
    public static CurrentExitResult Unknown(string detail) => new("", "", "", NetworkRouteEvidence.Unknown(detail), detail);
}

/// <summary>Queries only the committed session's local ingress. Never starts a test kernel or falls back to the system proxy.</summary>
public static class CurrentExitProbe
{
    private static readonly Uri[] Targets = [new("https://cp.cloudflare.com/cdn-cgi/trace"), new("https://www.cloudflare.com/cdn-cgi/trace")];

    public static async Task<CurrentExitResult> CheckAsync(NetworkDiagnosticSession session, CancellationToken ct = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(12));
        var context = KernelRouteContext.Load(session);
        // Even in TUN mode use this session's own loopback ingress. A browser/system
        // proxy fallback could return the hotspot/ISP exit and falsely label it as the node.
        var last = CurrentExitResult.Unknown("当前出口暂未确认");
        foreach (var target in Targets)
        {
            ct.ThrowIfCancellationRequested();
            var source = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var handler = new SocketsHttpHandler
            {
                UseProxy = true, Proxy = new WebProxy($"http://127.0.0.1:{context.HttpPort}"),
                AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(3),
                ConnectCallback = async (connection, token) =>
                {
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(connection.DnsEndPoint, token).ConfigureAwait(false);
                        source.TrySetResult(((IPEndPoint)socket.LocalEndPoint!).Port);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch { socket.Dispose(); throw; }
                }
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            Task<NetworkRouteEvidence>? evidence = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, target);
                request.Headers.CacheControl = new() { NoCache = true, NoStore = true };
                var send = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
                evidence = WebsiteDiagnosticProbe.ObserveRouteAsync(context, target, source.Task, send, budget.Token);
                using var response = await send.ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) { last = CurrentExitResult.Unknown($"出口服务返回 HTTP {(int)response.StatusCode}"); continue; }
                await response.Content.LoadIntoBufferAsync(8192, budget.Token).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false);
                var parsed = ParseTrace(text);
                if (parsed == null) { last = CurrentExitResult.Unknown("出口服务响应未包含有效地区与公网 IP"); continue; }
                var route = await evidence.ConfigureAwait(false);
                if (session.Mode != "direct" && route.Path != "代理")
                { last = CurrentExitResult.Unknown("未确认位置请求走当前代理出口：" + route.Path + "；未把本机出口作为节点出口"); continue; }
                return new(parsed.Value.Country, parsed.Value.Ip, target.AbsoluteUri, route,
                    "Cloudflare HTTPS trace 实际观测；通过当前会话内核，未使用节点名称或历史位置缓存");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            { last = CurrentExitResult.Unknown("当前出口检测失败：" + ex.GetType().Name); }
            finally { if (evidence != null) try { await evidence.ConfigureAwait(false); } catch { } }
            if (budget.IsCancellationRequested) break;
        }
        ct.ThrowIfCancellationRequested();
        return last;
    }

    internal static (string Country, string Ip)? ParseTrace(string text)
    {
        string? country = null, ip = null;
        foreach (var line in text.Split('\n'))
        {
            var value = line.Trim();
            if (value.StartsWith("loc=", StringComparison.Ordinal)) country = value[4..].Trim().ToLowerInvariant();
            if (value.StartsWith("ip=", StringComparison.Ordinal)) ip = value[3..].Trim();
        }
        if (country is not { Length: 2 } || !country.All(c => c is >= 'a' and <= 'z') || country == "xx"
            || !IPAddress.TryParse(ip, out var address) || IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal
            || address.IsIPv4MappedToIPv6 || address.GetAddressBytes()[0] is 0xFC or 0xFD) return null;
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            if (bytes[0] is 0 or 10 or 127 || bytes[0] >= 224 || bytes[0] == 169 && bytes[1] == 254
                || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168
                || bytes[0] == 100 && bytes[1] is >= 64 and <= 127) return null;
        }
        return (country, address.ToString());
    }
}
