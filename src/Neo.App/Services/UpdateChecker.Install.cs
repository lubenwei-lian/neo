using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Neo.Core;
using Neo.Core.Core;

namespace Neo.App.Services;

public static partial class UpdateChecker
{
    private static readonly SemaphoreSlim InstallGate = new(1, 1);

    private static async Task<(bool Ok, string Message)> InstallReleaseAsync(CoreEngine engine,
        string version, string tag, string url, string asset, string? sha, long expectedSize,
        Action<string> progress, Action<DownloadProgress>? onProgress, CancellationToken ct)
    {
        if (!await InstallGate.WaitAsync(0, ct).ConfigureAwait(false)) return (false, "已有内核更新正在进行，请稍候。");
        string? extract = null;
        FileStream? processLock = null;
        try
        {
            var spec = KernelCatalog.Get(engine);
            var epoch = ConnectionService.SessionEpoch;
            // Different versions/runtimes must never share a resumable partial file.
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                NeoPaths.RuntimeDir.ToLowerInvariant() + "\n" + url + "\n" + sha))).ToLowerInvariant();
            var workspace = Path.Combine(Path.GetTempPath(), "neo-kernel-updates", identity);
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(NeoPaths.RuntimeDir);
            try { processLock = new FileStream(Path.Combine(NeoPaths.RuntimeDir, ".kernel-update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (UnauthorizedAccessException) { return (false, "内核目录不可写，请以管理员身份重新运行 Neo 后更新。"); }
            catch (IOException) { return (false, "另一个进程正在更新内核，请稍后重试。"); }
            var zip = Path.Combine(workspace, "release.zip");
            sha ??= KernelHashCache.TryGet(spec.Owner, spec.Repo, tag, asset);
            if (engine == CoreEngine.Xray && string.IsNullOrEmpty(sha))
            {
                var text = await FetchReleaseTextAsync(spec.Owner, spec.Repo, tag, asset + ".dgst", ct).ConfigureAwait(false);
                if (KernelInstall.TryParseSha256(text, out var digest)) sha = digest;
            }
            if (string.IsNullOrWhiteSpace(sha)) return (false, "暂时无法取得官方 SHA-256，已保留原内核；请重新检查更新后再试。");
            var downloaded = await FetchKernelZipAsync(url, zip, asset, sha, progress, onProgress, ct,
                spec.Owner, spec.Repo, tag, asset).ConfigureAwait(false);
            if (!downloaded.Ok) return downloaded;
            if (expectedSize > 0 && new FileInfo(zip).Length != expectedSize)
            { File.Delete(zip); return (false, "更新包大小与官方记录不符，已中止安装。"); }
            ct.ThrowIfCancellationRequested();
            progress("解压并校验内核版本…");
            extract = Path.Combine(workspace, "extract-" + Guid.NewGuid().ToString("N"));
            ZipFile.ExtractToDirectory(zip, extract);
            var executables = Directory.GetFiles(extract, "*.exe", SearchOption.AllDirectories)
                .Where(f => engine == CoreEngine.Mihomo
                    ? Path.GetFileName(f).StartsWith("mihomo", StringComparison.OrdinalIgnoreCase)
                    : Path.GetFileName(f).Equals(spec.ExeName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (executables.Length != 1) return (false, "更新包没有唯一的 " + spec.ExeName + "，已保留原内核。");
            var executable = executables[0];
            if (!CoreVersionMatches(version, ProbeCoreVersion(engine, executable)))
                return (false, "更新包版本与官方发布不符，已保留原内核。");
            var files = new Dictionary<string, string> { [spec.ExeName] = executable };
            if (engine == CoreEngine.SingBox)
            {
                var library = Path.Combine(Path.GetDirectoryName(executable)!, "libcronet.dll");
                if (File.Exists(library)) files["libcronet.dll"] = library;
            }
            if (engine is CoreEngine.Xray or CoreEngine.SingBox)
            {
                progress("用当前配置预检新内核…");
                var check = await PreflightWithCurrentConfigAsync(executable,
                    engine == CoreEngine.Xray ? KernelKind.Xray : KernelKind.SingBox, ct).ConfigureAwait(false);
                if (!check.Ok) return check;
            }
            ct.ThrowIfCancellationRequested();
            progress("替换内核文件…");
            var result = await ConnectionService.ReplaceKernelFilesAsync(epoch, () =>
            {
                KernelInstall.ReplaceBundle(files, () =>
                {
                    if (!CoreVersionMatches(version, ProbeCoreVersion(engine, KernelCatalog.ExePath(engine))))
                        throw new IOException("安装后版本校验失败");
                });
                KernelCatalog.ResetVersion(engine);
                if (engine == CoreEngine.Xray) ConnectionService.ResetXrayVersion();
                if (engine == CoreEngine.SingBox) ConnectionService.ResetSingBoxVersion();
                return (true, $"{spec.Display} 已更新到 {version}。");
            }, ct).ConfigureAwait(false);
            if (result.Ok) File.Delete(zip);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return (false, "安装失败，现有内核已保留或回滚：" + ex.Message); }
        finally
        {
            if (extract != null) { try { Directory.Delete(extract, true); } catch { } }
            processLock?.Dispose();
            InstallGate.Release();
        }
    }

    internal static bool CoreVersionMatches(string expected, string output)
    {
        // Naive emits Chromium's four-component version, without the package's
        // -N rebuild suffix. Compare all emitted components, never a substring.
        static string Parse(string value)
        {
            var match = Regex.Match(value ?? "", @"(?<![\d.])v?(\d+(?:\.\d+){2,3})(?![\d.])");
            return match.Success ? match.Groups[1].Value : "";
        }
        var wanted = Parse(expected);
        return wanted.Length > 0 && wanted == Parse(output);
    }

    private static string ProbeCoreVersion(CoreEngine engine, string executable)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = engine switch { CoreEngine.Naive => "--version", CoreEngine.Mihomo => "-v", _ => "version" },
                WorkingDirectory = Path.GetDirectoryName(executable), UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            });
            if (process == null) return "";
            KillOnCloseJob.Attach(process, out _);
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!Task.WhenAll(stdout, stderr).Wait(TimeSpan.FromSeconds(5)) || !process.WaitForExit(500)) return "";
                return process.ExitCode == 0 ? stdout.Result + "\n" + stderr.Result : "";
            }
            finally
            {
                if (!process.HasExited) { try { process.Kill(true); process.WaitForExit(2000); } catch { } }
            }
        }
        catch { return ""; }
    }
}
