using System.Diagnostics;
using System.Text.Json;
using Neo.Core;
using Neo.Core.Core;
using Neo.Core.Parsing;

internal sealed class IsolatedTunnel : IDisposable
{
    private readonly Process _process;
    private IsolatedTunnel(Process process) => _process = process;
    internal static async Task<IsolatedTunnel> Start(string output)
    {
        var cachePath = Environment.GetEnvironmentVariable("NEO_UPDATE_TEST_SUBSCRIPTION_CACHE");
        var nodeName = Environment.GetEnvironmentVariable("NEO_UPDATE_TEST_NODE");
        if (string.IsNullOrWhiteSpace(cachePath) || string.IsNullOrWhiteSpace(nodeName))
            throw new ArgumentException("Isolated network checks require NEO_UPDATE_TEST_SUBSCRIPTION_CACHE and NEO_UPDATE_TEST_NODE; live application settings are never changed.");
        var cache = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(cachePath))!;
        var nodes = cache.Values.SelectMany(v => SubscriptionFetcher.ParseContent(v).Nodes);
        var node = nodes.First(n => n.Name.Contains(nodeName, StringComparison.Ordinal) && n.Protocol == Neo.Core.Models.NodeProtocol.VLESS);
        LocalPorts.TryApply(43208, 43209);
        var config = Path.Combine(output, "isolated-download-config.json");
        await File.WriteAllTextAsync(config, XrayConfigBuilder.Build(node, LocalPorts.Socks, LocalPorts.Http,
            "global", false, false, false, false, null, "warning", "223.5.5.5", 43210));
        var process = Process.Start(new ProcessStartInfo(Path.Combine(NeoPaths.RuntimeDir, "xray.exe"))
        {
            ArgumentList = { "run", "-c", config }, WorkingDirectory = NeoPaths.RuntimeDir,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
        })!;
        KillOnCloseJob.Attach(process, out _);
        _ = process.StandardOutput.ReadToEndAsync(); _ = process.StandardError.ReadToEndAsync();
        var tunnel = new IsolatedTunnel(process);
        for (var i = 0; i < 40; i++)
        {
            if (process.HasExited) { tunnel.Dispose(); throw new IOException("Isolated test core exited"); }
            try { using var client = new System.Net.Sockets.TcpClient(); await client.ConnectAsync("127.0.0.1", LocalPorts.Socks); Console.WriteLine("Isolated download tunnel ready; system proxy unchanged"); return tunnel; }
            catch (System.Net.Sockets.SocketException) { await Task.Delay(100); }
        }
        tunnel.Dispose(); throw new TimeoutException("Isolated test core did not become ready");
    }
    public void Dispose()
    { try { if (!_process.HasExited) { _process.Kill(true); _process.WaitForExit(3000); } } finally { _process.Dispose(); } }
}
