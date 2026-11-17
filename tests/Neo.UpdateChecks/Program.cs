using Neo.App.Services;
using System.Text.Json;

var command = args.FirstOrDefault() ?? "discover";
if (command == "checks") return await UpdateChecks.Run(args[1]);
if (command == "version-checks") return await VersionChecks.Run(args[1], args[2]);
if (command == "download")
{
    AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
    { if (e.Exception is System.Net.Http.HttpRequestException or TimeoutException) Console.WriteLine("TRACE " + e.Exception.GetType().Name + " " + e.Exception.Message); };
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180));
    DateTime last = DateTime.MinValue;
    var result = await KernelDownloader.DownloadAsync(args[1], args[2], p => {
        if ((DateTime.UtcNow-last).TotalSeconds < 2) return;
        last=DateTime.UtcNow;Console.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " " + p.Text());
    }, deadline.Token);
    Console.WriteLine(result); return result.Ok ? 0 : 1;
}
if (command == "install")
{
    var output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
    Environment.SetEnvironmentVariable("NEO_HOME", output);
    typeof(Neo.Core.NeoPaths).GetField("_runtimeDir", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
        .SetValue(null, Path.Combine(output, "runtime"));
    using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(args[2]));
    var releases = doc.RootElement.EnumerateArray().ToArray();
    var report = new List<object>();
    using var tunnel = args.Contains("isolated") ? await IsolatedTunnel.Start(output) : null;
    ConnectionService.IsConnected = tunnel != null;
    foreach (var engine in Enum.GetValues<CoreEngine>())
    {
        if (args.Length > 3 && (int)engine < int.Parse(args[3])) continue;
        var release = releases[(int)engine];
        var start = DateTime.UtcNow;
        DateTime last = DateTime.MinValue;
        var bytes = new Action<DownloadProgress>(p => { if ((DateTime.UtcNow-last).TotalSeconds < 5) return;last=DateTime.UtcNow;Console.WriteLine(engine+" "+p.Text()); });
        var progress = new Action<string>(stage => {
            Console.WriteLine(stage);
            if (!stage.StartsWith("校验 SHA")) return;
            var identity=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                Neo.Core.NeoPaths.RuntimeDir.ToLowerInvariant()+"\n"+release.GetProperty("DownloadUrl").GetString()+"\n"+release.GetProperty("Sha256").GetString()))).ToLowerInvariant();
            var zip=Path.Combine(Path.GetTempPath(),"neo-kernel-updates",identity,"release.zip");
            var artifacts=Path.Combine(output,"official-packages");Directory.CreateDirectory(artifacts);
            if(File.Exists(zip))File.Copy(zip,Path.Combine(artifacts,engine+".zip"),true);
        });
        (bool Ok, string Message) result = engine switch
        {
            CoreEngine.Xray => await UpdateChecker.DownloadAndInstallAsync(release.Deserialize<XrayRelease>()!, progress, onProgress:bytes),
            CoreEngine.SingBox => await UpdateChecker.DownloadAndInstallSingBoxAsync(release.Deserialize<SingBoxRelease>()!, progress, onProgress:bytes),
            CoreEngine.Mihomo => await UpdateChecker.DownloadAndInstallMihomoAsync(release.Deserialize<MihomoRelease>()!, progress, onProgress:bytes),
            _ => await UpdateChecker.DownloadAndInstallNaiveAsync(release.Deserialize<UpdateChecker.NaiveRelease>()!, progress, onProgress:bytes),
        };
        Console.WriteLine(engine + " " + result);
        report.Add(new { engine = engine.ToString(), result.Ok, result.Message, elapsed = (DateTime.UtcNow - start).TotalSeconds, version = KernelCatalog.ReadVersion(engine) });
        await File.WriteAllTextAsync(Path.Combine(output, "update-results.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        if (!result.Ok) return 1;
    }
    return 0;
}
if (command == "discover")
{
    var releases = await Task.WhenAll(
        Task.Run(async () => (object?)await UpdateChecker.FetchLatestAsync()),
        Task.Run(async () => (object?)await UpdateChecker.FetchLatestSingBoxAsync()),
        Task.Run(async () => (object?)await UpdateChecker.FetchLatestMihomoAsync()),
        Task.Run(async () => (object?)await UpdateChecker.FetchLatestNaiveAsync()));
    var json = JsonSerializer.Serialize(releases, new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine(json);
    if (args.Length > 1) await File.WriteAllTextAsync(args[1], json);
    return releases.All(r => r != null) ? 0 : 1;
}
return 64;
