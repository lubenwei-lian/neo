using Neo.App.Services;
using Neo.Core;
using System.Text.Json;

internal static class VersionChecks
{
    internal static async Task<int> Run(string output, string installedRuntime)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var checks = new List<object>();
        void Check(string name, bool passed)
        {
            checks.Add(new { name, passed });
            if (!passed) throw new Exception("FAIL: " + name);
            Console.WriteLine("PASS: " + name);
        }
        foreach (var (name, version, older) in new[] {
            ("xray", "26.11.9", "26.10.21"), ("sing-box", "1.14.4", "1.14.3"),
            ("mihomo", "1.19.34", "1.19.33"), ("naive", "154.0.8037.49-5", "154.0.8000.49") })
        {
            var same = UpdateChecker.DescribeKernelUpdate(version, version, true);
            Check(name + " same version has no installer", !same.CanInstall && same.Verdict.Contains("已是最新"));
            var newer = UpdateChecker.DescribeKernelUpdate(version, older, true);
            Check(name + " newer local version cannot be downgraded", !newer.CanInstall && newer.Verdict.Contains("本地版本比发布"));
            var upgrade = UpdateChecker.DescribeKernelUpdate(older, version, true);
            Check(name + " genuine upgrade offers installation", upgrade.CanInstall && upgrade.Verdict.Contains("发现新版本"));
            var missing = UpdateChecker.DescribeKernelUpdate(null, version, false);
            Check(name + " absent executable offers initial installation", missing.CanInstall && missing.Current == "未安装");
            foreach (var unknown in new string?[] { null, "", "—", "unknown" })
            {
                var invalid = UpdateChecker.DescribeKernelUpdate(unknown, version, true);
                Check(name + " installed but unreadable is not a new release: " + (unknown ?? "null"),
                    !invalid.CanInstall && invalid.Current == "版本未识别");
            }
        }
        Check("leading v and legacy suffix do not cause reinstalls",
            !UpdateChecker.DescribeKernelUpdate("v1.14.4-noGate", "1.14.4", true).CanInstall);
        Check("Naive executable without packaging revision is not reinstalled",
            !UpdateChecker.DescribeKernelUpdate("154.0.8037.49", "154.0.8037.49-5", true).CanInstall);

        // Real version processes only; no network or configuration changes.
        var runtimeField = typeof(NeoPaths).GetField("_runtimeDir", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        Environment.SetEnvironmentVariable("NEO_HOME", output);
        runtimeField.SetValue(null, installedRuntime);
        var versions = new Dictionary<string, string?>();
        foreach (var engine in KernelCatalog.All)
        {
            var version = await Task.Run(() => KernelCatalog.ReadVersion(engine.Engine, refresh:true));
            versions[engine.Display] = version;
            Check(engine.Display + " installed executable version is recognized", !string.IsNullOrEmpty(version));
            Check(engine.Display + " display and check use the same cached version", KernelCatalog.CachedVersion(engine.Engine) == version);
        }
        var isolated = Path.Combine(output, "runtime");
        Directory.CreateDirectory(isolated);
        runtimeField.SetValue(null, isolated);
        KernelCatalog.ResetVersion(CoreEngine.Xray);
        Check("missing probe returns unknown once", KernelCatalog.ReadVersion(CoreEngine.Xray) == null);
        File.Copy(Path.Combine(installedRuntime, "xray.exe"), Path.Combine(isolated, "xray.exe"), true);
        Check("periodic read does not loop failed process probes", KernelCatalog.ReadVersion(CoreEngine.Xray) == null);
        Check("manual retry recovers a previously failed probe", KernelCatalog.ReadVersion(CoreEngine.Xray, refresh:true) == versions["xray"]);
        KernelCatalog.ResetVersion(CoreEngine.Xray);
        var parallel = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => KernelCatalog.ReadVersion(CoreEngine.Xray))));
        Check("concurrent readers share a successful version", parallel.All(v => v == versions["xray"]));
        await File.WriteAllTextAsync(Path.Combine(output, "version-results.json"), JsonSerializer.Serialize(new { checks, versions }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} version and update decision checks");
        return 0;
    }
}
