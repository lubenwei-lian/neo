using System.Net;
using System.Net.Sockets;
using System.Text;

internal sealed class HttpFixture : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accept;
    private readonly List<Task> _clients = [];
    public string Url { get; }
    public Func<string, (string Status, string Headers, byte[] Body)> Response { get; set; }
    public HttpFixture(Func<string, (string Status, string Headers, byte[] Body)> response)
    {
        Response = response; _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/release";
        _accept = Accept();
    }
    private async Task Accept()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _clients.Add(Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            var stream = client.GetStream(); var request = new StringBuilder(); var one = new byte[1];
                            while (!request.ToString().EndsWith("\r\n\r\n"))
                            { await stream.ReadExactlyAsync(one, _stop.Token); request.Append((char)one[0]); }
                            var r = Response(request.ToString());
                            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {r.Status}\r\nContent-Length: {r.Body.Length}\r\nConnection: close\r\n{r.Headers}\r\n");
                            await stream.WriteAsync(header, _stop.Token);
                            await stream.WriteAsync(r.Body, _stop.Token);
                        }
                        catch (Exception) when (_stop.IsCancellationRequested) { }
                        catch (IOException) { }
                    }
                }));
            }
        }
        catch (OperationCanceledException) { }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Stop(); await _accept; await Task.WhenAll(_clients); _stop.Dispose();
    }
}
