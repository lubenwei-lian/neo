using System.Text.Json;
using Neo.Core;

namespace Neo.App.Services;

public static partial class KernelInstall
{
    private sealed record BundleFile(string Name, bool Existed);
    private sealed record BundleJournal(string Directory, BundleFile[] Files);
    private static readonly string[] BundleNames = ["xray.exe", "sing-box.exe", "mihomo.exe", "naive.exe", "libcronet.dll"];
    private static string JournalPath => Path.Combine(NeoPaths.RuntimeDir, "kernel-update.json");

    /// <summary>Replace one release as a unit, including its runtime DLL. The
    /// journal and original files survive process termination until verification.</summary>
    internal static void ReplaceBundle(IReadOnlyDictionary<string, string> sources, Action verify)
    {
        RecoverBundle();
        var directory = ".kernel-update-" + Guid.NewGuid().ToString("N");
        var root = Path.Combine(NeoPaths.RuntimeDir, directory);
        Directory.CreateDirectory(root);
        var journal = new BundleJournal(directory, sources.Keys.Select(name =>
        {
            if (!BundleNames.Contains(name, StringComparer.Ordinal)) throw new IOException("不支持的内核文件：" + name);
            return new BundleFile(name, File.Exists(Path.Combine(NeoPaths.RuntimeDir, name)));
        }).ToArray());
        var journalWritten = false;
        try
        {
            foreach (var file in journal.Files)
            {
                File.Copy(sources[file.Name], Path.Combine(root, file.Name + ".new"));
                if (file.Existed) File.Copy(Path.Combine(NeoPaths.RuntimeDir, file.Name), Path.Combine(root, file.Name + ".old"));
            }
            using (var output = new FileStream(JournalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(output, journal);
                output.Flush(true);
            }
            journalWritten = true;
            foreach (var file in journal.Files)
            {
                var target = Path.Combine(NeoPaths.RuntimeDir, file.Name);
                var staged = Path.Combine(root, file.Name + ".new");
                if (file.Existed) File.Replace(staged, target, null, true);
                else File.Move(staged, target);
            }
            verify();
            foreach (var file in journal.Files.Where(f => f.Existed))
            {
                try { File.Copy(Path.Combine(root, file.Name + ".old"), Path.Combine(NeoPaths.RuntimeDir, file.Name + ".bak-prev"), true); }
                catch (Exception ex) { LogBus.App("保留上版内核备份失败：" + ex.Message, "Warning"); }
            }
            File.Delete(JournalPath);
            journalWritten = false;
        }
        catch (Exception failure)
        {
            if (journalWritten)
            {
                try { RestoreBundle(journal); File.Delete(JournalPath); journalWritten = false; }
                catch (Exception rollback) { throw new IOException("内核更新失败，恢复未完成；已保留恢复记录。" + rollback.Message, failure); }
            }
            throw;
        }
        finally
        {
            if (!journalWritten) { try { Directory.Delete(root, true); } catch { } }
        }
    }

    private static void RestoreBundle(BundleJournal journal)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(journal.Directory, @"^\.kernel-update-[a-f0-9]{32}$")
            || journal.Files.Length == 0 || journal.Files.Any(f => !BundleNames.Contains(f.Name, StringComparer.Ordinal)))
            throw new IOException("内核恢复记录无效");
        var root = Path.Combine(NeoPaths.RuntimeDir, journal.Directory);
        // Validate every backup before restoring any target.
        foreach (var file in journal.Files.Where(f => f.Existed))
            if (!File.Exists(Path.Combine(root, file.Name + ".old"))) throw new IOException("缺少内核备份：" + file.Name);
        foreach (var file in journal.Files)
        {
            var target = Path.Combine(NeoPaths.RuntimeDir, file.Name);
            if (file.Existed)
            {
                var backup = Path.Combine(root, file.Name + ".old");
                if (File.Exists(target) && Hash(target) == Hash(backup)) continue; // May still be locked; no change to undo.
                ReplaceAtomically(backup, target, Path.Combine(root, file.Name + ".failed"));
            }
            else if (File.Exists(target)) File.Delete(target);
        }
        static string Hash(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
    }

    internal static void RecoverBundle()
    {
        if (!File.Exists(JournalPath)) return;
        var journal = JsonSerializer.Deserialize<BundleJournal>(File.ReadAllText(JournalPath))
            ?? throw new IOException("内核恢复记录为空");
        RestoreBundle(journal);
        File.Delete(JournalPath);
        try { Directory.Delete(Path.Combine(NeoPaths.RuntimeDir, journal.Directory), true); } catch { }
        LogBus.App("已恢复上次未完成的内核更新", "Warning");
    }
}
