using System.Diagnostics;
using System.Net.Http;

namespace Neo.Core.Core;

/// <summary>A bounded HTTP probe; warmed latency and availability keep separate contracts.</summary>
internal static class DelayHttpProbe
{
    internal static async Task<int> MeasureAsync(HttpClient http, IReadOnlyList<string> targets,
        int attempts, int timeoutMs, CancellationToken ct, Func<bool>? certificateRejected = null,
        bool acceptRedirects = false, bool warmup = false, int stopAtMs = 1000)
    {
        ct.ThrowIfCancellationRequested();
        if (timeoutMs <= 0 || targets.Count == 0) return -1;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(timeoutMs);
        var primaryFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<int> ProbeTargetAsync(int index)
        {
            var best = -1;
            try
            {
                if (index > 0)
                {
                    // Start the alternate early after a hard failure, or halfway
                    // through a slow request. Do not cancel a viable slow primary.
                    try { await primaryFailed.Task.WaitAsync(TimeSpan.FromMilliseconds(
                        Math.Max(1, (long)timeoutMs * index / targets.Count)), budget.Token).ConfigureAwait(false); }
                    catch (TimeoutException) { }
                }
                // Displayed node latency retains the established warmed measurement:
                // DNS, proxy/TLS handshakes and cold connection setup are not latency samples.
                // The warmup still consumes the same total budget as the measurements.
                if (warmup)
                {
                    try
                    {
                        using var warm = await http.GetAsync(targets[index],
                            HttpCompletionOption.ResponseHeadersRead, budget.Token).ConfigureAwait(false);
                    }
                    catch (HttpRequestException)
                    {
                        if (certificateRejected?.Invoke() == true) { budget.Cancel(); return -1; }
                    }
                    if (certificateRejected?.Invoke() == true) { budget.Cancel(); return -1; }
                }
                for (var attempt = 0; attempt < Math.Clamp(attempts, 1, 3); attempt++)
                {
                    budget.Token.ThrowIfCancellationRequested();
                    try
                    {
                        var sample = Stopwatch.StartNew();
                        using var response = await http.GetAsync(targets[index],
                            HttpCompletionOption.ResponseHeadersRead, budget.Token).ConfigureAwait(false);
                        ct.ThrowIfCancellationRequested();
                        var code = (int)response.StatusCode;
                        if (acceptRedirects ? code is >= 200 and < 400 : code is 200 or 204)
                        {
                            var ms = Math.Max(1, (int)sample.ElapsedMilliseconds);
                            best = best < 0 ? ms : Math.Min(best, ms);
                            if (!warmup || best <= stopAtMs) return best;
                        }
                    }
                    catch (HttpRequestException)
                    {
                        if (certificateRejected?.Invoke() == true) { budget.Cancel(); break; }
                    }
                    if (certificateRejected?.Invoke() == true) { budget.Cancel(); break; }
                }
                if (best > 0 && certificateRejected?.Invoke() != true) return best;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) when (budget.IsCancellationRequested) { }
            if (best > 0 && certificateRejected?.Invoke() != true) return best;
            if (index == 0) primaryFailed.TrySetResult();
            return -1;
        }

        var probes = Enumerable.Range(0, targets.Count).Select(ProbeTargetAsync).ToArray();
        var pending = probes.ToList();
        try
        {
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(completed);
                var ms = await completed.ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (certificateRejected?.Invoke() == true) break;
                if (ms > 0) return ms;
            }
            ct.ThrowIfCancellationRequested();
            return -1;
        }
        finally
        {
            // Release the losing request before the owner disposes its proxy/core.
            // Neither a successful return nor Stop may leave a hidden HTTP probe alive.
            budget.Cancel();
            try { await Task.WhenAll(probes).ConfigureAwait(false); }
            catch (OperationCanceledException) when (budget.IsCancellationRequested) { }
        }
    }
}
