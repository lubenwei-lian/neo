using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Neo.Core;

namespace Neo.App.Services;

/// <summary>
/// 配置备份/恢复（对标成熟客户端的「备份与恢复」）：
/// 导出把「用户可见的配置」打成一个 zip（state.json + 订阅缓存 + 自定义配置 + Clash 直通 YAML + 用量统计），
/// 导入则先给现有数据做一份回滚副本，再原样放回去；导入完成后需要重启应用生效。
/// 注意：**不包含**内核二进制、分流数据（geo*）、日志——这些体积大且可重新下载，放进去只会让备份变成几百 MB。
/// </summary>
public static class ConfigBackup
{
    private const string MetaName = "backup-meta.json";

    private static string DataDir => NeoPaths.DataDir;

    public static string DefaultFileName() => $"neo-config-{DateTime.Now:yyyyMMdd-HHmm}.zip";

    /// <summary>把当前用户配置打包；返回 (是否成功, 说明, 实际路径)。</summary>
    public static (bool Ok, string Message, string? Path) Export(string zipPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(zipPath)) return (false, "备份路径为空", null);
            var dir = Path.GetDirectoryName(zipPath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(zipPath)) File.Delete(zipPath);

            var meta = new
            {
                App = "neo",
                Version = CoreVersionText(),
                ExportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Format = 1,
            };
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                void AddFile(string src, string entry)
                {
                    if (!File.Exists(src)) return;
                    zip.CreateEntryFromFile(src, entry, CompressionLevel.Optimal);
                }
                AddFile(Path.Combine(DataDir, "state.json"), "state.json");
                AddFile(Path.Combine(DataDir, "subscription-cache.json"), "subscription-cache.json");
                AddFile(Path.Combine(DataDir, "usage.json"), "usage.json");
                foreach (var f in SafeFiles(Path.Combine(DataDir, "custom"), "custom-*.json"))
                    AddFile(f, "custom/" + Path.GetFileName(f));
                foreach (var f in SafeFiles(Path.Combine(DataDir, "clash"), "*.yaml"))
                    AddFile(f, "clash/" + Path.GetFileName(f));

                var metaEntry = zip.CreateEntry(MetaName, CompressionLevel.Optimal);
                using var w = new StreamWriter(metaEntry.Open());
                w.Write(JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
            }
            var size = new FileInfo(zipPath).Length;
            LogBus.App($"配置备份已导出：{zipPath}（{size / 1024.0:F1} KB）", "Info");
            return (true, $"已导出（{size / 1024.0:F1} KB）：{zipPath}", zipPath);
        }
        catch (Exception ex)
        {
            LogBus.App($"配置备份导出失败：{ex.GetType().Name} {ex.Message}", "Error");
            return (false, "导出失败：" + ex.Message, null);
        }
    }

    /// <summary>校验备份包是否可用（有 state.json 且是 JSON 对象）。</summary>
    public static (bool Ok, string Message) Validate(string zipPath)
    {
        try
        {
            if (!File.Exists(zipPath)) return (false, "文件不存在");
            using var zip = ZipFile.OpenRead(zipPath);
            var e = zip.GetEntry("state.json");
            if (e == null) return (false, "不是 neo 配置备份（缺少 state.json）");
            using var s = e.Open();
            using var doc = JsonDocument.Parse(s);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (false, "备份里的 state.json 不是 JSON 对象");
            return (true, "OK");
        }
        catch (Exception ex)
        {
            return (false, "备份文件损坏或不是 zip：" + ex.Message);
        }
    }

    /// <summary>
    /// 导入：先把当前数据整目录快照到 data\backup-preimport-时间戳，再按备份内容覆盖。
    /// 调用方必须**先断开代理**并**重启应用**（函数只落盘）。
    /// </summary>
    public static (bool Ok, string Message) Import(string zipPath)
    {
        string staging = Path.Combine(Path.GetTempPath(), "neo-import-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var v = Validate(zipPath);
            if (!v.Ok) return v;

            Directory.CreateDirectory(staging);
            ZipFile.ExtractToDirectory(zipPath, staging, true);

            // 1) 回滚副本（只复制用户配置，不复制日志/内核）
            var rollback = Path.Combine(DataDir, "backup-preimport-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(rollback);
            foreach (var name in new[] { "state.json", "subscription-cache.json", "usage.json" })
            {
                var src = Path.Combine(DataDir, name);
                if (File.Exists(src)) File.Copy(src, Path.Combine(rollback, name), true);
            }
            CopyDirIfExists(Path.Combine(DataDir, "custom"), Path.Combine(rollback, "custom"));
            CopyDirIfExists(Path.Combine(DataDir, "clash"), Path.Combine(rollback, "clash"));

            // 2) 覆盖配置
            foreach (var name in new[] { "state.json", "subscription-cache.json", "usage.json" })
            {
                var src = Path.Combine(staging, name);
                if (!File.Exists(src)) continue;
                var tmp = Path.Combine(DataDir, name + ".tmp");
                File.Copy(src, tmp, true);
                File.Move(tmp, Path.Combine(DataDir, name), true);
            }
            ReplaceDir(Path.Combine(staging, "custom"), Path.Combine(DataDir, "custom"), "custom-*.json");
            ReplaceDir(Path.Combine(staging, "clash"), Path.Combine(DataDir, "clash"), "*.yaml");

            LogBus.App($"配置已导入（{zipPath}）；回滚副本：{rollback}", "Info");
            return (true, $"导入成功。重启后生效。\n导入前的配置已备份到：{rollback}");
        }
        catch (Exception ex)
        {
            LogBus.App($"配置导入失败：{ex.GetType().Name} {ex.Message}", "Error");
            return (false, "导入失败：" + ex.Message);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
        }
    }

    private static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try
        {
            return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern).Where(f => !f.EndsWith("runtime-config.json", StringComparison.OrdinalIgnoreCase)) : Array.Empty<string>();
        }
        catch { return Array.Empty<string>(); }
    }

    private static void CopyDirIfExists(string src, string dst)
    {
        if (!Directory.Exists(src)) return;
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
    }

    private static void ReplaceDir(string srcDir, string dstDir, string pattern)
    {
        if (!Directory.Exists(dstDir)) Directory.CreateDirectory(dstDir);
        // 先删同类旧文件（配置类资产，删除安全；回滚副本已保留）
        foreach (var f in Directory.GetFiles(dstDir, pattern))
        {
            try { File.Delete(f); } catch { }
        }
        if (!Directory.Exists(srcDir)) return;
        foreach (var f in Directory.GetFiles(srcDir, pattern))
            File.Copy(f, Path.Combine(dstDir, Path.GetFileName(f)), true);
    }

    private static string CoreVersionText()
    {
        try { return System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0"; }
        catch { return "0.0.0"; }
    }
}
