using Neo.Core.Presentation;
using Neo.App.Services;

namespace Neo.RefreshChecks;

internal static class TrafficChecks
{
    internal static async Task Run(Func<string, Func<Task>, Task> test, Action<bool, string> check)
    {
        var start = new DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc);
        Task Sync(Action action) { action(); return Task.CompletedTask; }
        await test("traffic establishes a baseline without inventing a first rate", () => Sync(() => {
            var b = new TrafficSampleBuffer(); b.Record(1, 10, 50000, 90000, start);
            check(b.Capture(1, 10).Samples.Length == 0, "first totals became a speed spike");
            b.Record(1, 10, 50200, 90800, start.AddSeconds(2));
            var s = b.Capture(1, 10).Samples.Single();
            check(s.UpBps == 100 && s.DownBps == 400 && s.Id == 1, "wrong rates or sequence");
        }));
        await test("traffic continues for minutes without a renderer or UI reader", () => Sync(() => {
            var b = new TrafficSampleBuffer();
            for (var i = 0; i <= 240; i++) b.Record(1, 10, 1000L*i, 3000L*i, start.AddSeconds(i));
            var a = b.Capture(1, 10); var c = b.Capture(1, 10);
            check(a.Samples.Length is >= 64 and <= 71 && a.Samples[^1].Id == 240, "missing background history or unbounded memory");
            check(a.Samples.All(p => p.UpBps == 1000 && p.DownBps == 3000) && a.PeakBps == 3000, "reader lifecycle affected measured rates");
            a.Samples[0] = new(999, 0, 0, 0);
            check(c.Samples[0].Id != 999 && b.Capture(1, 10).Samples[0].Id != 999, "snapshot mutates host storage");
        }));
        await test("traffic never smooths a missing sampling interval into invented samples", () => Sync(() => {
            var b = new TrafficSampleBuffer(); b.Record(1, 10, 0, 0, start);
            b.Record(1, 10, 100, 200, start.AddSeconds(1));
            b.Record(1, 10, 5000, 9000, start.AddSeconds(21));
            check(b.Capture(1, 10).Samples.Length == 1, "long gap produced a fake average rate");
            b.Record(1, 10, 5100, 9200, start.AddSeconds(22));
            var s = b.Capture(1, 10).Samples;
            check(s.Length == 2 && s[1].SampledAt - s[0].SampledAt == 21000 && s[1].UpBps == 100, "gap lost or recovery baseline wrong");
        }));
        await test("traffic rejects duplicate time and handles counter resets without spikes", () => Sync(() => {
            var b = new TrafficSampleBuffer(); b.Record(1, 10, 1000, 2000, start);
            b.Record(1, 10, 9000, 9000, start); b.Record(1, 10, 1010, 2020, start.AddSeconds(1));
            check(b.Capture(1, 10).Samples.Single().UpBps == 10, "duplicate time changed baseline");
            b.Record(1, 10, 1, 2, start.AddSeconds(2)); b.Record(1, 10, 11, 22, start.AddSeconds(3));
            check(b.Capture(1, 10).Samples.Length == 2 && b.Capture(1, 10).PeakBps == 20, "reset invented a sample or peak");
        }));
        await test("traffic separates epochs and core PIDs and clears on stop", () => Sync(() => {
            var b = new TrafficSampleBuffer(); b.Record(1, 10, 0, 0, start); b.Record(1, 10, 10, 20, start.AddSeconds(1));
            check(b.Capture(2, 10).Samples.Length == 0 && b.Capture(1, 11).Samples.Length == 0, "stale session leaked");
            b.Record(2, 11, 100, 200, start.AddSeconds(2));
            check(b.Capture(2, 11).Samples.Length == 0 && b.Capture(2, 11).PeakBps == 0, "session inherited old counters");
            b.Record(2, 11, 110, 220, start.AddSeconds(3)); b.Clear();
            check(b.Capture(2, 11).Samples.Length == 0, "clear retained traffic");
        }));
        await test("traffic high frequency input has a hard capacity bound", () => Sync(() => {
            var b = new TrafficSampleBuffer();
            for (var i = 0; i < 2000; i++) b.Record(1, 10, i, i*2, start.AddMilliseconds(i*10));
            check(b.Capture(1, 10).Samples.Length == 256, "buffer grew without bound");
        }));
        await test("production sampler records history without any UI subscriber", async () => {
            StatsSampler.Stop(); ConnectionService.IsConnected = true; ConnectionService.CorePid = Environment.ProcessId;
            ConnectionService.SessionEpoch = 800;
            var n = 0; CoreStats.Read = () => { var i = Interlocked.Increment(ref n); return new(1000L*i, 2000L*i); };
            try {
                StatsSampler.Start(); await Task.Delay(2300);
                var a = StatsSampler.CaptureTraffic();
                check(a.Samples.Length >= 2 && a.Samples.All(p => p.DownBps > p.UpBps && p.UpBps > 0), "background sampler depends on UI");
                ConnectionService.SessionEpoch++;
                check(StatsSampler.CaptureTraffic().Samples.Length == 0, "new session sees old traffic");
            } finally { StatsSampler.Stop(); CoreStats.Read = () => new(1000, 2000); }
            check(StatsSampler.CaptureTraffic().Samples.Length == 0, "stop retained history");
        });
    }
}
