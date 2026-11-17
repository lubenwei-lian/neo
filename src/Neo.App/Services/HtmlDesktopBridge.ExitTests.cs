using System.Text.Json;
using Neo.App.Ui;
using Neo.Core;
using Neo.Core.Core;
using Neo.Core.Models;

namespace Neo.App.Services;

internal sealed partial class HtmlDesktopBridge
{
    private bool _exitRegionsRunning;

    private async Task<object[]> TestExitRegionsAsync(JsonElement payload)
    {
        if (NativeUiReview.Enabled) throw new InvalidOperationException("隔离审核不探测真实节点");
        if (_exitRegionsRunning || _testRunning) throw new InvalidOperationException("已有节点测试正在进行");
        var keys = payload.GetProperty("keys").EnumerateArray().Select(k => k.GetString() ?? "").Distinct().ToArray();
        var nodes = keys.Select(FindNode).ToArray();
        _exitRegionsRunning = true;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        budget.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            // The batch tester owns a separate temporary core and independent ports.
            // It never changes ConnectionService's selected/committed session.
            var failures = new System.Collections.Concurrent.ConcurrentDictionary<NodeItem, string>();
            var observed = new System.Collections.Concurrent.ConcurrentDictionary<NodeItem, (string Country, string Ip)>();
            try
            {
                await RealDelayTester.DetectExitLocationsAsync(nodes, NeoPaths.RuntimeDir,
                    onResult: (node, country, ip) => observed[node] = (country, ip),
                    ct: budget.Token, onFailure: (node, detail) => failures[node] = detail);
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested && !_lifetime.IsCancellationRequested)
            {
                foreach (var node in nodes.Where(n => !observed.ContainsKey(n)))
                    failures.TryAdd(node, "本轮达到检测时限，可补测未确认节点");
                LogBus.App("出口地区检测达到时限，已保留完成的结果", "Warning");
            }
            return nodes.Select((n, index) =>
            {
                var supported = RealDelayTester.CanDetectExitLocation(n);
                var confirmed = observed.TryGetValue(n, out var result) && !string.IsNullOrEmpty(result.Country)
                    && System.Net.IPAddress.TryParse(result.Ip, out _);
                var expected = NodeGrouper.RegionOf(n.Name);
                var actual = confirmed ? result.Country.ToLowerInvariant() : "";
                return (object)new
                {
                    key = keys[index],
                    name = NodeDisplayName.WithoutFlag(n.Name), region = NodeGrouper.RegionDisplay(expected),
                    ip = confirmed ? result.Ip : "", country = confirmed ? NodeGrouper.RegionDisplay(actual) : "",
                    confirmed, matches = confirmed && string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase),
                    retryable = supported && !confirmed,
                    detail = confirmed ? "" : !supported ? "暂不支持此协议的批量出口检测"
                        : failures.TryGetValue(n, out var reason) ? reason : "出口检测未完成，请重试"
                };
            }).ToArray();
        }
        finally { _exitRegionsRunning = false; }
    }
}
