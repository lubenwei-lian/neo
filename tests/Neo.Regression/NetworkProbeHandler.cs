using System.Net;

namespace Neo.App.Tests;

internal sealed class NetworkProbeHandler : HttpMessageHandler
{
    public readonly List<Uri> Requests = new();
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond =
        (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request.RequestUri!);
        return Respond(request, ct);
    }
}
