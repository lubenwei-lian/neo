using Neo.App.Ui;
using System.Reflection.Metadata;
var failures = new List<string>(); var checks = 0;
void Check(bool value, string label) { checks++; if (!value) failures.Add(label); Console.WriteLine($"{(value ? "PASS" : "FAIL")} {label}"); }
Check(ResponsiveViewport.TextScale(1101, 883) == 1, "normal window retains original typography");
Check(ResponsiveViewport.TextScale(900, 600) == 1 && ResponsiveViewport.TextScale(2400, 600) == 1, "compact and wide-short windows do not overflow from enlargement");
Check(ResponsiveViewport.TextScale(1920, 1040) == 1.3, "full HD desktop enlarges typography by thirty percent");
Check(ResponsiveViewport.TextScale(3840, 2160) == 1.6, "ultra large desktop caps enlargement");
Check(ResponsiveViewport.TextScale(double.NaN, 1080) == 1 && ResponsiveViewport.TextScale(1920, 0) == 1, "invalid and unloaded dimensions are safe");
Check(ResponsiveViewport.TextScale(1920, 1080) == ResponsiveViewport.TextScale(3840 / 2, 2160 / 2), "DPI uses effective pixels without double scaling");
var resizeSizes = new[] { (1101d, 883d), (1920d, 1040d), (2560d, 1400d), (900d, 600d), (1101d, 883d) };
var resized = resizeSizes.Select(s => ResponsiveViewport.FontSize(14, ResponsiveViewport.TextScale(s.Item1, s.Item2))).ToArray();
Check(resized[0] == resized[^1] && resized[1] > resized[0], "restoring window restores baseline without cumulative font growth");
Check(Enumerable.Range(600, 2400).Select(h => ResponsiveViewport.TextScale(h * 1.7, h)).Zip(Enumerable.Range(601, 2400).Select(h => ResponsiveViewport.TextScale(h * 1.7, h))).All(pair => pair.First <= pair.Second), "enlargement stays monotonic across resize range");
var good = Enumerable.Repeat("可达", 8).ToArray();
Check(ConnectivityDisplay.Summarize(good, true, false, true).Percentage == "100%", "completed valid round");
var failed = good.ToArray(); failed[0] = "失败";
Check(ConnectivityDisplay.Summarize(failed, true, false, true).Percentage == "87.5%", "failed site retains denominator");
failed[0] = "站点限制";
Check(ConnectivityDisplay.Summarize(failed, true, false, true).Percentage == "87.5%" && ConnectivityDisplay.Site(failed[0]) == "受限", "restricted site remains distinct");
foreach (var state in new[] { "未检测", "检测中", "已取消", "future unknown state" })
{
    var states = good.ToArray(); states[0] = state;
    Check(ConnectivityDisplay.Summarize(states, true, false, true).Percentage == "—", "no percentage for " + state);
}
Check(ConnectivityDisplay.Summarize(good, false, false, true).Percentage == "—", "stale session invalidated");
Check(ConnectivityDisplay.Summarize(good, true, true, true).Percentage == "—", "in-flight round invalidated");
Check(ConnectivityDisplay.Summarize(good, true, false, false).Percentage == "—", "no completion timestamp invalidated");
Check(ConnectivityDisplay.Summarize(Array.Empty<string>(), true, false, true).Percentage == "—", "empty target list invalidated");
Check(ConnectivityDisplay.Site(null) == "—" && ConnectivityDisplay.Site("失败") == "失败" && ConnectivityDisplay.Site("可达") == "通过", "individual sites have a state rather than a made-up percentage");
foreach (var state in new[] { "响应异常", "站点异常", "检测异常" })
{
    var states = good.ToArray(); states[0] = state;
    Check(ConnectivityDisplay.Summarize(states, true, false, true).Percentage == "87.5%" && !ConnectivityDisplay.Site(state).Contains('%'), "completed error counts against total: " + state);
}
Check(ConnectivityDisplay.Summarize(Enumerable.Repeat("失败", 8).ToArray(), true, false, true).Percentage == "0%", "all failures produce zero overall passes");

var random = new Random(117);
var noOvershoot = true; var endpoints = true;
for (var i = 0; i < 10000; i++)
{
    double Next() => random.NextDouble() * 100;
    var before = Next(); var from = Next(); var to = Next(); var after = Next();
    var c = TrafficCurveMath.Controls(before, from, to, after);
    double At(double t) => Math.Pow(1 - t, 3) * from + 3 * Math.Pow(1 - t, 2) * t * c.First + 3 * (1 - t) * t * t * c.Second + t * t * t * to;
    endpoints &= Math.Abs(At(0) - from) < 1e-9 && Math.Abs(At(1) - to) < 1e-9;
    for (var j = 1; j < 20; j++) { var y = At(j / 20d); noOvershoot &= y >= Math.Min(from, to) - 1e-9 && y <= Math.Max(from, to) + 1e-9; }
}
Check(noOvershoot, "10000 random curve intervals preserve sample bounds");
Check(endpoints, "curve passes through all sample endpoints");
var peak = TrafficCurveMath.Controls(0, 100, 100, 0);
Check(peak.First == 100 && peak.Second == 100, "flat peak remains flat");
var flat = TrafficCurveMath.Controls(0, 0, 0, 0);
Check(flat.First == 0 && flat.Second == 0, "zero traffic remains flat");
if (args.Length > 0)
{
    using var stream = File.OpenRead(args[0]);
    using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
    var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
    byte[]? body = null;
    foreach (var handle in metadata.TypeDefinitions)
    {
        var type = metadata.GetTypeDefinition(handle);
        if (metadata.GetString(type.Name) != "NativeUiReview") continue;
        foreach (var methodHandle in type.GetMethods())
        {
            var method = metadata.GetMethodDefinition(methodHandle);
            if (metadata.GetString(method.Name) == "get_Enabled") body = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
        }
    }
    Check(body is { Length: 2 } && body[0] == 0x16 && body[1] == 0x2a, "formal assembly cannot enable the offline review switch");
}
Console.WriteLine($"{checks - failures.Count}/{checks} passed");
return failures.Count == 0 ? 0 : 1;
