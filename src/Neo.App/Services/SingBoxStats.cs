using Neo.Core;
using System.Net.Http;
using System.Text.Json;
using Neo.Core.Core;

namespace Neo.App.Services;

public static class SingBoxStats
{
    public readonly record struct Snapshot(long Uplink, long Downlink, int ActiveConnections);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };
    //   无法区分端口/鉴权/协议错误。这里 20 秒限流记 Warning（原因变化时立即记）。
    private static DateTime _lastWarnAt;
    private static string? _lastWarnReason;

    private static void WarnRateLimited(string reason)
    {
        try
        {
            var now = DateTime.Now;
            if ((now - _lastWarnAt).TotalSeconds < 20 && string.Equals(reason, _lastWarnReason, StringComparison.Ordinal)) return;
            _lastWarnAt = now;
            _lastWarnReason = reason;
            LogBus.App($"统计接口不可用（127.0.0.1:{LocalPorts.CoreClashApi}）：{reason}", "Warning");
        }
        catch { }
    }

    public static Snapshot? Query()
    {
        try
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Get,
                $"http://127.0.0.1:{LocalPorts.CoreClashApi}/connections");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + SingBoxConfigBuilder.ClashApiSecret);

            using var resp = Http.Send(req);
            if (!resp.IsSuccessStatusCode)
            {
                WarnRateLimited("HTTP " + (int)resp.StatusCode);
                return null;
            }

            using var stream = resp.Content.ReadAsStream();
            using var doc = JsonDocument.Parse(stream);
            var r = doc.RootElement;

            long up = r.TryGetProperty("uploadTotal", out var u) && u.ValueKind == JsonValueKind.Number
                ? u.GetInt64() : 0;
            long down = r.TryGetProperty("downloadTotal", out var d) && d.ValueKind == JsonValueKind.Number
                ? d.GetInt64() : 0;
            int active = r.TryGetProperty("connections", out var c) && c.ValueKind == JsonValueKind.Array
                ? c.GetArrayLength() : 0;

            return new Snapshot(up, down, active);
        }
        catch (Exception ex)
        {
            WarnRateLimited(ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }
}
