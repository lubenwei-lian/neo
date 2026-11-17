using System.Diagnostics;
using System.Text.Json;
using Neo.App.Services;
using Neo.Core.Core;
using Neo.RefreshChecks;
using Counter = Neo.Core.Core.XrayStatsClient.Counter;

if (args.Length != 1) throw new ArgumentException("Usage: Neo.RefreshChecks <isolated-output-directory>");
var output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
var results = new List<object>(); var failures = 0;
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
async Task Test(string name, Func<Task> action)
{
    try { await action().WaitAsync(TimeSpan.FromSeconds(12)); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; results.Add(new { name, passed = false, error = ex.ToString() }); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
Task Sync(Action action) { action(); return Task.CompletedTask; }
async Task Until(Func<bool> ready)
{
    var timer = Stopwatch.StartNew();
    while (!ready()) { if (timer.Elapsed > TimeSpan.FromSeconds(4)) throw new TimeoutException(); await Task.Delay(15); }
}
LogEntry Line(int id) => new() { Text = id.ToString() };
await TrafficChecks.Run(Test, Check);

await Test("raster unchanged layout retains its buffer without repeated UI writes", () => Sync(() => {
    var chart = new Chart(); chart.Layout(120); chart.Layout(60); var buffer = chart.First; UiWrites.Count = 0;
    for (var i = 0; i < 300; i++) chart.Layout(60);
    Check(UiWrites.Count == 0 && ReferenceEquals(buffer, chart.First), "steady geometry allocated or repeated setters");
}));
await Test("raster narrows its actual allocation instead of retaining largest XAML pool", () => Sync(() => {
    var chart = new Chart(); chart.Layout(120); var wide = chart.Count; chart.Layout(40);
    Check(chart.Count < wide / 2 && chart.Raster.Width * chart.Raster.Height * 4 == chart.Count, "raster retained hidden cells or wrong stride");
}));
await Test("raster height changes invalidate once and physical pixels follow display scale", () => Sync(() => {
    var chart = new Chart(); chart.Layout(60); UiWrites.Count = 0; chart.Layout(60, 300);
    Check(UiWrites.Count == 1, "height ignored"); chart.Layout(60, 300); Check(UiWrites.Count == 1, "height did not settle");
    chart.Raster.Configure(60, 300, 1.5); Check(chart.Raster.Height == 450 && chart.Raster.Width == 1005, "DPI geometry wrong");
}));
uint Pixel(Neo.Core.Presentation.TrafficChartRaster raster, int x, int y) {
    var i = (y * raster.Width + x) * 4; var p = raster.Pixels;
    return (uint)(p[i] | p[i + 1] << 8 | p[i + 2] << 16 | p[i + 3] << 24);
}
var dnColors = Enumerable.Range(0, 9).Select(x => 0xff000010u + (uint)x).ToArray();
var upColors = Enumerable.Range(0, 9).Select(x => 0xff001000u + (uint)x).ToArray();
await Test("raster preserves eight fixed rows, separate bands, empty cells and transparent gaps", () => Sync(() => {
    var raster = new Neo.Core.Presentation.TrafficChartRaster(); raster.Configure(8, 250, 1);
    raster.SetColumn(0, 1, 8, dnColors, upColors);
    Check(Pixel(raster, 14, 110) == dnColors[1] && Pixel(raster, 14, 33) == dnColors[0]
        && Pixel(raster, 14, 139) == upColors[8] && Pixel(raster, 14, 216) == upColors[0]
        && Pixel(raster, 20, 110) == 0 && Pixel(raster, 10, 106) == 0, "palette, band, level, gap or rounded corner changed");
    raster.SetColumn(0, 8, 1, dnColors, upColors);
    Check(Pixel(raster, 14, 110) == dnColors[0] && Pixel(raster, 14, 33) == dnColors[8]
        && Pixel(raster, 14, 139) == upColors[0], "old lit pixels were not cleared");
}));
await Test("identical raster samples do no drawing or managed allocation", () => Sync(() => {
    var raster = new Neo.Core.Presentation.TrafficChartRaster(); raster.Configure(120, 250, 1);
    for (var c = 0; c < 120; c++) raster.SetColumn(c, 4, 2, dnColors, upColors);
    raster.MarkUploaded(); var bytes = GC.GetAllocatedBytesForCurrentThread();
    for (var n = 0; n < 100; n++) for (var c = 0; c < 120; c++) raster.SetColumn(c, 4, 2, dnColors, upColors);
    Check(!raster.Dirty && GC.GetAllocatedBytesForCurrentThread() - bytes < 1024, "unchanged samples rendered or allocated");
}));
await Test("released background raster holds no surface and can rebuild cleanly", () => Sync(() => {
    var raster = new Neo.Core.Presentation.TrafficChartRaster(); raster.Configure(120, 250, 1); raster.SetColumn(0, 1, 8, dnColors, upColors);
    raster.Release(); Check(raster.Pixels.Length == 0 && raster.Columns == 0 && raster.Width == 0 && !raster.Dirty, "released surface retained");
    raster.Configure(8, 250, 1); raster.SetColumn(0, 0, 0, dnColors, upColors);
    Check(Pixel(raster, 14, 110) == dnColors[0] && Pixel(raster, 14, 139) == upColors[0], "recreated surface has missing/old cells");
}));
await Test("invalid or oversized raster geometry is rejected before allocation", () => Sync(() => {
    var raster = new Neo.Core.Presentation.TrafficChartRaster();
    foreach (var value in new[] { (0, 250d, 1d), (8, double.NaN, 1d), (8, 250d, 0d), (2048, 4096d, 4d) }) {
        try { raster.Configure(value.Item1, value.Item2, value.Item3); throw new Exception("invalid dimensions accepted"); }
        catch (ArgumentOutOfRangeException) { }
    }
    Check(raster.Pixels.Length == 0, "invalid geometry allocated a surface");
}));
await Test("10000 log arrivals enqueue only one UI wakeup", () => Sync(() => {
    var view = new LogView(); for (var i = 0; i < 10000; i++) view.Post(Line(i));
    Check(view.DispatcherQueue.Posts == 1 && view.PendingCount == 4000, "dispatch or buffer was unbounded");
    view.DispatcherQueue.Pump(); view.Tick();
    Check(view.LogList.Items.Count == 2000 && ((Neo.App.Ui.LogDisplayRow)view.LogList.Items[0]).HeadText == "8000"
        && ((Neo.App.Ui.LogDisplayRow)view.LogList.Items[^1]).HeadText == "9999", "latest display rows lost ordering"); view.Release();
}));
await Test("concurrent log producers share the same pending UI wakeup", async () => {
    var view = new LogView(); await Task.WhenAll(Enumerable.Range(0, 8).Select(p => Task.Run(() => {
        for (var i = 0; i < 1000; i++) view.Post(Line(p * 1000 + i));
    })));
    Check(view.DispatcherQueue.Posts == 1 && view.PendingCount == 4000, "concurrent producers duplicated dispatch");
    view.DispatcherQueue.Pump(); view.Tick(); Check(view.LogList.Items.Count == 2000, "display limit failed"); view.Release();
});
await Test("log idle timer stops and the next burst wakes it", () => Sync(() => {
    var view = new LogView(); view.Post(Line(1)); view.DispatcherQueue.Pump(); view.Tick();
    view.Post(Line(2)); Check(view.DispatcherQueue.Posts == 1, "running timer received another wakeup");
    view.Tick(); view.Tick(); Check(!view.TimerRunning, "empty timer did not stop");
    view.Post(Line(3)); Check(view.DispatcherQueue.Posts == 2, "next burst could not wake timer");
    view.DispatcherQueue.Pump(); view.Tick(); Check(view.LogList.Items.Count == 3, "burst lost"); view.Release();
}));
await Test("log arrivals during a flush remain pending for the next tick", () => Sync(() => {
    var view = new LogView(); view.Post(Line(1)); view.DispatcherQueue.Pump();
    view.LogList.OnScroll = () => { view.LogList.OnScroll = null; view.Post(Line(2)); };
    view.Tick(); Check(view.PendingCount == 1 && view.DispatcherQueue.Posts == 1, "flush arrival was lost or rescheduled");
    view.Tick(); Check(view.LogList.Items.Count == 2, "second tick lost arrival"); view.Release();
}));
await Test("clearing logs also clears a queued display batch", () => Sync(() => {
    var view = new LogView(); view.Post(Line(1)); view.ClearView(); view.DispatcherQueue.Pump(); view.Tick();
    Check(view.PendingCount == 0 && view.LogList.Items.Count == 0, "cleared logs reappeared"); view.Release();
}));
await Test("released log page cannot be restarted by its queued callback", () => Sync(() => {
    var view = new LogView(); view.Post(Line(1)); view.Release(); view.DispatcherQueue.Pump(); view.Post(Line(2));
    Check(!view.TimerRunning && view.PendingCount == 0 && view.DispatcherQueue.Posts == 1, "released page resurrected");
}));
await Test("a rejected log wakeup can be retried by the next arrival", () => Sync(() => {
    var view = new LogView(); view.DispatcherQueue.Reject = true; view.Post(Line(1));
    view.DispatcherQueue.Reject = false; view.Post(Line(2)); view.DispatcherQueue.Pump(); view.Tick();
    Check(view.LogList.Items.Count == 2, "failed dispatch left wakeup latched"); view.Release();
}));
await Test("shutdown prevents queued log UI work", () => Sync(() => {
    var view = new LogView(); view.Post(Line(1)); Neo.App.App.IsShuttingDown = true;
    view.DispatcherQueue.Pump(); view.Tick(); Check(!view.TimerRunning && view.PendingCount == 0, "shutdown restarted UI");
    view.Release(); Neo.App.App.IsShuttingDown = false;
}));

await Test("memory starts empty and is supplied by a background sample", async () => {
    StatsSampler.Stop(); ConnectionService.IsConnected = true; ConnectionService.CorePid = Environment.ProcessId;
    ConnectionService.SessionEpoch = 1; Check(ConnectionService.CoreMemoryMb == null, "memory sampled from UI getter");
    StatsSampler.Start(); await Until(() => ConnectionService.CoreMemoryMb is > 0);
});
await Test("cached memory reads do not allocate process objects", () => Sync(() => {
    _ = ConnectionService.CoreMemoryMb; var start = GC.GetAllocatedBytesForCurrentThread();
    for (var i = 0; i < 10000; i++) _ = ConnectionService.CoreMemoryMb;
    Check(GC.GetAllocatedBytesForCurrentThread() - start < 1024, "getter still allocates per read");
}));
await Test("a new session immediately hides memory from the old session", async () => {
    ConnectionService.SessionEpoch++; Check(ConnectionService.CoreMemoryMb == null, "old epoch memory leaked");
    await Until(() => ConnectionService.CoreMemoryMb is > 0);
});
await Test("a changed PID immediately hides the old process memory", async () => {
    ConnectionService.CorePid = -1; Check(ConnectionService.CoreMemoryMb == null, "old PID memory leaked");
    await Task.Delay(1100); Check(ConnectionService.CoreMemoryMb == null, "invalid PID gained a memory sample");
    ConnectionService.CorePid = Environment.ProcessId; await Until(() => ConnectionService.CoreMemoryMb is > 0);
});
await Test("disconnect hides memory immediately and stop clears the cache", () => Sync(() => {
    ConnectionService.IsConnected = false; Check(ConnectionService.CoreMemoryMb == null, "disconnected memory retained");
    StatsSampler.Stop(); ConnectionService.IsConnected = true; Check(ConnectionService.CoreMemoryMb == null, "stopped sample retained");
}));
await Test("a sample overlapping a new session cannot publish the previous snapshot", async () => {
    using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); var reads = 0;
    CoreStats.Read = () => {
        if (Interlocked.Increment(ref reads) == 1) { entered.Set(); release.Wait(TimeSpan.FromSeconds(4)); return new(10, 20); }
        return new(3000, 4000);
    };
    try {
        ConnectionService.SessionEpoch++; StatsSampler.Start(); await Until(() => entered.IsSet);
        ConnectionService.SessionEpoch++; Check(ConnectionService.CoreMemoryMb == null, "in-flight memory exposed"); release.Set();
        await Until(() => StatsSampler.Latest is { Uplink: 3000, Downlink: 4000 });
        Check(ConnectionService.CoreMemoryMb is > 0, "new session did not obtain its own sample");
    } finally { release.Set(); StatsSampler.Stop(); CoreStats.Read = () => new(1000, 2000); }
});
await Test("stop and restart reject memory from an old blocked sampling loop", async () => {
    using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); var reads = 0;
    CoreStats.Read = () => {
        if (Interlocked.Increment(ref reads) == 1) { entered.Set(); release.Wait(TimeSpan.FromSeconds(4)); return new(10, 20); }
        return new(5000, 6000);
    };
    try {
        StatsSampler.Start(); await Until(() => entered.IsSet); StatsSampler.Stop();
        ConnectionService.SessionEpoch++; StatsSampler.Start();
        await Until(() => StatsSampler.Latest is { Uplink: 5000, Downlink: 6000 });
        release.Set(); await Task.Delay(100);
        Check(StatsSampler.Latest is { Uplink: 5000, Downlink: 6000 } && ConnectionService.CoreMemoryMb is > 0,
            "old loop overwrote the restarted sample");
    } finally { release.Set(); StatsSampler.Stop(); CoreStats.Read = () => new(1000, 2000); }
});

await Test("typed Xray counters exclude direct, block, API and chain hops", () => Sync(() => {
    XrayStatsClient.Counters = new Counter[] {
        new("outbound>>>proxy>>>traffic>>>uplink", 100), new("outbound>>>chain0>>>traffic>>>downlink", 200),
        new("outbound>>>direct>>>traffic>>>uplink", 9999), new("outbound>>>block>>>traffic>>>downlink", 9999),
        new("outbound>>>api>>>traffic>>>uplink", 9999), new("outbound>>>hop0>>>traffic>>>uplink", 9999),
        new("inbound>>>socks-in>>>traffic>>>uplink", 100), new("inbound>>>http-in>>>traffic>>>downlink", 200) };
    Check(XrayStats.Query() is { Uplink: 100, Downlink: 200 }, "traffic was omitted or double-counted");
}));
await Test("Xray retains inbound fallback and ignores other inbound tags", () => Sync(() => {
    XrayStatsClient.Counters = new Counter[] { new("inbound>>>socks-in>>>traffic>>>uplink", 42),
        new("inbound>>>http-in>>>traffic>>>downlink", 84), new("inbound>>>api>>>traffic>>>uplink", 9999) };
    Check(XrayStats.Query() is { Uplink: 42, Downlink: 84 }, "inbound fallback changed");
}));
await Test("zero proxy counters remain valid and sampling never resets counters", () => Sync(() => {
    XrayStatsClient.Counters = new Counter[] { new("outbound>>>proxy>>>traffic>>>uplink", 0), new("outbound>>>proxy>>>traffic>>>downlink", 0) };
    Check(XrayStats.Query() is { Uplink: 0, Downlink: 0 } && XrayStats.IsResponsive(), "idle API treated as dead");
    Check(XrayStatsClient.Counters.Count == 2, "counter input mutated");
}));
await Test("unknown Xray traffic shape stays null while API remains responsive", () => Sync(() => {
    XrayStatsClient.Counters = new Counter[] { new("unrecognized", 100), new("outbound>>>proxy>>>traffic>>>other", 100) };
    Check(XrayStats.Query() == null && XrayStats.IsResponsive(), "shape error disguised as zero or dead API");
}));

var measurements = new List<object>();
object Measure(string name, Action action, int count)
{
    for (var i = 0; i < Math.Min(count, 32); i++) action();
    var allocated = GC.GetAllocatedBytesForCurrentThread(); var timer = Stopwatch.StartNew();
    for (var i = 0; i < count; i++) action();
    timer.Stop(); return new { name, iterations = count, milliseconds = timer.Elapsed.TotalMilliseconds,
        allocated_bytes = GC.GetAllocatedBytesForCurrentThread() - allocated };
}
if (SourceBinding.HasBaseline)
{
    XrayStatsClient.Counters = Enumerable.Range(0, 32).SelectMany(i => new[] {
        new Counter($"outbound>>>chain{i}>>>traffic>>>uplink", i * 100),
        new Counter($"outbound>>>chain{i}>>>traffic>>>downlink", i * 200) }).ToArray();
    ConnectionService.IsConnected = true; ConnectionService.CorePid = Environment.ProcessId;
    StatsSampler.Start(); await Until(() => ConnectionService.CoreMemoryMb is > 0);
    for (var round = 0; round < 3; round++)
    {
        var oldChart = new BaselineChart(); var chart = new Chart();
        oldChart.Layout(120); oldChart.Layout(60); chart.Layout(120); chart.Layout(60);
        UiWrites.Count = 0; for (var i = 0; i < 300; i++) oldChart.Layout(60); var beforeWrites = UiWrites.Count;
        UiWrites.Count = 0; for (var i = 0; i < 300; i++) chart.Layout(60);
        measurements.Add(new { name = "chart_repeated_layout_setters", round, before = beforeWrites, after = UiWrites.Count, refreshes = 300 });
        Action beforeStats = () => { _ = BaselineXrayStats.Query(); }; Action afterStats = () => { _ = XrayStats.Query(); };
        Action beforeMemory = () => { _ = BaselineConnectionService.CoreMemoryMb; }; Action afterMemory = () => { _ = ConnectionService.CoreMemoryMb; };
        if (round % 2 == 0) {
            measurements.Add(Measure("xray_before", beforeStats, 5000)); measurements.Add(Measure("xray_after", afterStats, 5000));
            measurements.Add(Measure("memory_before", beforeMemory, 1000)); measurements.Add(Measure("memory_after", afterMemory, 1000));
        } else {
            measurements.Add(Measure("xray_after", afterStats, 5000)); measurements.Add(Measure("xray_before", beforeStats, 5000));
            measurements.Add(Measure("memory_after", afterMemory, 1000)); measurements.Add(Measure("memory_before", beforeMemory, 1000));
        }
        var oldView = new BaselineLogView(); var view = new LogView();
        var entries = Enumerable.Range(0, 10000).Select(Line).ToArray();
        var oldAllocated = GC.GetAllocatedBytesForCurrentThread(); var oldTimer = Stopwatch.StartNew();
        foreach (var entry in entries) oldView.Post(entry);
        oldTimer.Stop(); oldAllocated = GC.GetAllocatedBytesForCurrentThread() - oldAllocated;
        var newAllocated = GC.GetAllocatedBytesForCurrentThread(); var newTimer = Stopwatch.StartNew();
        foreach (var entry in entries) view.Post(entry);
        newTimer.Stop(); newAllocated = GC.GetAllocatedBytesForCurrentThread() - newAllocated;
        measurements.Add(new { name = "log_dispatch", round, entries = entries.Length,
            before_posts = oldView.DispatcherQueue.Posts, after_posts = view.DispatcherQueue.Posts,
            before_allocated_bytes = oldAllocated, after_allocated_bytes = newAllocated,
            before_ms = oldTimer.Elapsed.TotalMilliseconds, after_ms = newTimer.Elapsed.TotalMilliseconds });
        oldView.Release(); view.Release();
    }
}
StatsSampler.Stop();
File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { passed = results.Count - failures, failed = failures, tests = results }, new JsonSerializerOptions { WriteIndented = true }));
File.WriteAllText(Path.Combine(output, "benchmarks.json"), JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true }));
File.Copy(Path.Combine(AppContext.BaseDirectory, "source-binding.json"), Path.Combine(output, "source-binding.json"), true);
Console.WriteLine($"Refresh checks: {results.Count - failures}/{results.Count}; {measurements.Count} measurement records");
return failures == 0 ? 0 : 1;
