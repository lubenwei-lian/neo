using Neo.App.Services;
using Neo.Core;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

internal static class UpdateChecks
{
    private static int _passed;
    private static void Check(string name, bool value)
    { if (!value) throw new Exception("FAIL: " + name); _passed++; Console.WriteLine("PASS: " + name); }
    internal static async Task<int> Run(string output)
    {
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("NEO_HOME", output);
        typeof(NeoPaths).GetField("_runtimeDir", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(null, Path.Combine(output, "runtime"));
        Check("runtime isolated from source and installed app", NeoPaths.RuntimeDir == Path.Combine(output, "runtime"));
        ConnectionService.IsConnected = true;
        LocalPorts.TryApply(32701, 32702);
        var bypassed = false;
        await using (var origin = new HttpFixture(_ => { bypassed = true; return ("200 OK", "", Encoding.ASCII.GetBytes("must not bypass")); }))
        {
            var failure = await KernelDownloader.DownloadAsync(origin.Url, Path.Combine(output, "no-bypass.zip"), null, default);
            Check("connected proxy failure never falls through to direct origin", !failure.Ok && !bypassed && failure.Message.Contains("未回退直连"));
        }
        var absoluteRequest = false;
        await using (var httpProxy = new HttpFixture(request => { absoluteRequest = request.StartsWith("GET http://update-fixture.invalid/"); return ("200 OK", "", Encoding.ASCII.GetBytes("proxied")); }))
        {
            LocalPorts.TryApply(32701, new Uri(httpProxy.Url).Port);
            var routed = Path.Combine(output, "http-proxy.zip");
            Check("SOCKS failure retries through explicit local HTTP proxy", (await KernelDownloader.DownloadAsync("http://update-fixture.invalid/release", routed, null, default)).Ok && absoluteRequest && File.ReadAllText(routed) == "proxied");
        }
        ConnectionService.IsConnected = false;
        LocalPorts.TryApply(32701, 32702); // A missing synthetic SOCKS boundary falls through to actual direct HTTP.
        var target = Path.Combine(output, "download.zip");
        byte[] payload = Encoding.ASCII.GetBytes("abcdefghijklmnop");
        await using var server = new HttpFixture(_ => ("200 OK", "", payload));
        Check("real HTTP download", (await KernelDownloader.DownloadAsync(server.Url, target, null, default)).Ok && File.ReadAllBytes(target).SequenceEqual(payload));
        File.WriteAllBytes(target + ".part", payload[..4]);
        var resumed = false;
        server.Response = request => { resumed = request.Contains("Range: bytes=4-", StringComparison.OrdinalIgnoreCase); return ("206 Partial Content", "Content-Range: bytes 4-15/16\r\n", payload[4..]); };
        Check("206 resume preserves exact bytes", (await KernelDownloader.DownloadAsync(server.Url, target, null, default)).Ok && resumed && File.ReadAllBytes(target).SequenceEqual(payload));
        File.WriteAllBytes(target + ".part", payload[..4]);
        server.Response = _ => ("200 OK", "", payload);
        Check("ignored range restarts cleanly", (await KernelDownloader.DownloadAsync(server.Url, target, null, default)).Ok && File.ReadAllBytes(target).SequenceEqual(payload));
        File.WriteAllBytes(target + ".part", payload);
        server.Response = _ => ("416 Range Not Satisfiable", "Content-Range: bytes */16\r\n", []);
        Check("416 exact completed partial", (await KernelDownloader.DownloadAsync(server.Url, target, null, default)).Ok && File.ReadAllBytes(target).SequenceEqual(payload));
        File.WriteAllBytes(target + ".part", new byte[20]);
        server.Response = _ => File.Exists(target + ".part") ? ("416 Range Not Satisfiable", "Content-Range: bytes */16\r\n", []) : ("200 OK", "", payload);
        Check("416 oversized partial discarded", (await KernelDownloader.DownloadAsync(server.Url, target, null, default)).Ok && File.ReadAllBytes(target).SequenceEqual(payload));
        File.WriteAllBytes(target + ".part", payload[..4]);
        server.Response = _ => File.Exists(target + ".part") ? ("206 Partial Content", "Content-Range: bytes 6-15/16\r\n", payload[6..]) : ("200 OK", "", payload);
        Check("wrong Content-Range cannot corrupt file", (await KernelDownloader.DownloadAsync(server.Url, target, null, default)).Ok && File.ReadAllBytes(target).SequenceEqual(payload));
        server.Response = _ => ("200 OK", "", payload);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var cancelled = false;
        try { await KernelDownloader.VerifySha256Async(target, null, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
        Check("hash cancellation remains cancellation", cancelled);
        Check("hash mismatch rejected", !(await KernelDownloader.VerifySha256Async(target, new string('0', 64), default)).Match);
        Check("full Naive build accepted", UpdateChecker.CoreVersionMatches("154.0.8037.49-5", "naive 154.0.8037.49"));
        Check("wrong Naive build rejected", !UpdateChecker.CoreVersionMatches("154.0.8037.49-5", "naive 154.0.8000.49"));
        Check("empty version rejected", !UpdateChecker.CoreVersionMatches("1.14.4", ""));
        Check("substring version rejected", !UpdateChecker.CoreVersionMatches("26.11.9", "Xray 26.11.90"));
        Check("legacy suffix comparison", UpdateChecker.CompareVersions("1.14.4", "1.14.3-noGate") > 0);
        var h1 = new string('a', 64); var h2 = new string('b', 64);
        var html = $"<li><a href=\"/o/r/releases/download/v1/a.zip\">a</a><span>sha256:{h1}</span></li><li><a href=\"/o/r/releases/download/v1/b.zip\">b</a><span>sha256:{h2}</span></li>";
        var parsed = GitHubChannel.ParseExpandedAssets(html, "o", "r", "v1");
        Check("official HTML digests bound to individual assets", parsed.Count == 2 && parsed[0].Sha256 == h1 && parsed[1].Sha256 == h2);
        html = $"<li><a href=\"/o/r/releases/download/v1/a.zip\">a</a></li><li><a href=\"/o/r/releases/download/v1/b.zip\">b</a><span>sha256:{h2}</span></li>";
        Check("missing digest never takes neighbour hash", GitHubChannel.ParseExpandedAssets(html, "o", "r", "v1")[0].Sha256 == null);
        Directory.CreateDirectory(NeoPaths.RuntimeDir);
        var exe = Path.Combine(NeoPaths.RuntimeDir, "sing-box.exe"); var dll = Path.Combine(NeoPaths.RuntimeDir, "libcronet.dll");
        var newExe = Path.Combine(output, "new-exe"); var newDll = Path.Combine(output, "new-dll");
        File.WriteAllText(exe, "old exe"); File.WriteAllText(dll, "old dll");
        File.WriteAllText(newExe, "new exe"); File.WriteAllText(newDll, "new dll");
        var files = new Dictionary<string, string> { ["sing-box.exe"] = newExe, ["libcronet.dll"] = newDll };
        try { KernelInstall.ReplaceBundle(files, () => throw new IOException("synthetic post-install rejection")); } catch (IOException) { }
        Check("verification failure rolls back exe and DLL together", File.ReadAllText(exe) == "old exe" && File.ReadAllText(dll) == "old dll");
        using (var locked = new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.Read))
        { try { KernelInstall.ReplaceBundle(files, () => { }); } catch (IOException) { } }
        Check("locked DLL rolls back earlier exe replacement", File.ReadAllText(exe) == "old exe" && File.ReadAllText(dll) == "old dll");
        KernelInstall.ReplaceBundle(files, () => { });
        Check("successful bundle installs both files", File.ReadAllText(exe) == "new exe" && File.ReadAllText(dll) == "new dll");
        Check("successful update retains previous release", File.ReadAllText(exe + ".bak-prev") == "old exe" && File.ReadAllText(dll + ".bak-prev") == "old dll");
        Check("transaction journal cleaned", !File.Exists(Path.Combine(NeoPaths.RuntimeDir, "kernel-update.json")) && !Directory.GetDirectories(NeoPaths.RuntimeDir, ".kernel-update-*").Any());
        var restoreDir = ".kernel-update-" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(Path.Combine(NeoPaths.RuntimeDir, restoreDir));
        File.WriteAllText(Path.Combine(NeoPaths.RuntimeDir, restoreDir, "sing-box.exe.old"), "crash backup");
        File.WriteAllText(Path.Combine(NeoPaths.RuntimeDir, "kernel-update.json"), System.Text.Json.JsonSerializer.Serialize(new { Directory = restoreDir, Files = new[] { new { Name = "sing-box.exe", Existed = true } } }));
        KernelInstall.RecoverIfNeeded();
        Check("interrupted update recovers on startup", File.ReadAllText(exe) == "crash backup");
        var virgin = Path.Combine(NeoPaths.RuntimeDir, "naive.exe");
        try { KernelInstall.ReplaceBundle(new Dictionary<string, string> { ["naive.exe"] = newExe }, () => throw new IOException("reject new installation")); } catch (IOException) { }
        Check("failed first install leaves no executable", !File.Exists(virgin));
        KernelHashCache.Put("o", "r", "bad", "a", new string('z', 64));
        Check("invalid hash never enters official cache", KernelHashCache.TryGet("o", "r", "bad", "a") == null);
        var initialExe = Path.Combine(NeoPaths.RuntimeDir, "xray.exe"); File.WriteAllText(initialExe, "original synthetic core");
        server.Response = _ => ("200 OK", "", payload);
        var badHash = await UpdateChecker.DownloadAndInstallAsync(new("26.11.9", "fixture-bad-hash", server.Url,
            payload.Length, false, new string('0', 64)), _ => { });
        Check("installer rejects corrupt checksum before touching core", !badHash.Ok && File.ReadAllText(initialExe) == "original synthetic core");
        var goodHash = Convert.ToHexString(SHA256.HashData(payload));
        var badZip = await UpdateChecker.DownloadAndInstallAsync(new("26.11.9", "fixture-bad-zip", server.Url,
            payload.Length, false, goodHash), _ => { });
        Check("installer rejects malformed archive before touching core", !badZip.Ok && File.ReadAllText(initialExe) == "original synthetic core");
        using var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, true))
        { using var entry = new StreamWriter(zip.CreateEntry("xray.exe").Open()); entry.Write("not an executable"); }
        var invalidExeZip = archive.ToArray(); server.Response = _ => ("200 OK", "", invalidExeZip);
        var invalidExe = await UpdateChecker.DownloadAndInstallAsync(new("26.11.9", "fixture-invalid-exe", server.Url,
            invalidExeZip.Length, false, Convert.ToHexString(SHA256.HashData(invalidExeZip))), _ => { });
        Check("installer rejects non-runnable version check", !invalidExe.Ok && File.ReadAllText(initialExe) == "original synthetic core");
        using var pauseBeforeCommit = new CancellationTokenSource();
        var cancelInstall = false;
        try { await UpdateChecker.DownloadAndInstallAsync(new("26.11.9", "fixture-cancel", server.Url,
            invalidExeZip.Length, false, Convert.ToHexString(SHA256.HashData(invalidExeZip))),
            stage => { if (stage.StartsWith("校验 SHA")) pauseBeforeCommit.Cancel(); }, pauseBeforeCommit.Token); }
        catch (OperationCanceledException) { cancelInstall = true; }
        Check("installer cancellation cannot replace original core", cancelInstall && File.ReadAllText(initialExe) == "original synthetic core");
        Check("failed packages clean extraction directories", !Directory.GetDirectories(Path.Combine(Path.GetTempPath(), "neo-kernel-updates"), "extract-*", SearchOption.AllDirectories).Any());
        Console.WriteLine($"PASSED {_passed} update checks"); return 0;
    }
}
