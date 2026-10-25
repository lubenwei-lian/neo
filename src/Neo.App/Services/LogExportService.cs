using System.IO;
using System.IO.Compression;
using System.Text;
using Neo.Core;

namespace Neo.App.Services;

/// <summary>
/// 把最近 N 天日志打成一个 zip：主日志 neo-*.log（内核输出也在这个文件里，前缀 [内核]）、
/// 看门狗 guard-*.log、订阅日志 subscription-*.log，外加一份 info.txt（版本/构建/系统/代理状态）。
/// 供「日志页 → 导出日志」使用；导出路径由用户在系统保存对话框里选。
/// </summary>
public static class LogExportService
{
    public static string BuildZip(string zipPath, int days = 3)
    {
        if (days < 1) days = 1;
        var tmp = Path.Combine(Path.GetTempPath(), "neo-logexport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var logDir = NeoPaths.LogDir;
            var cutoff = DateTime.Now.Date.AddDays(-(days - 1));
            var copied = 0;
            foreach (var pattern in new[] { "neo-*.log", "guard-*.log", "subscription-*.log" })
            {
                foreach (var f in Directory.GetFiles(logDir, pattern))
                {
                    try
                    {
                        if (File.GetLastWriteTime(f) < cutoff) continue;
                        File.Copy(f, Path.Combine(tmp, Path.GetFileName(f)), true);
                        copied++;
                    }
                    catch { }
                }
            }

            var info = new StringBuilder();
            info.AppendLine("neo 日志导出");
            info.AppendLine("导出时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            info.AppendLine("日志范围：最近 " + days + " 天（文件 " + copied + " 个）");
            info.AppendLine("日志目录：" + logDir);
            info.AppendLine("版本：" + (typeof(LogExportService).Assembly.GetName().Version?.ToString(3) ?? "?"));
            try { info.AppendLine("构建时间：" + File.GetLastWriteTime(typeof(LogExportService).Assembly.Location).ToString("yyyy-MM-dd HH:mm:ss")); } catch { }
            info.AppendLine("系统：" + Environment.OSVersion);
            try { info.AppendLine("系统代理：" + SystemProxy.IsEnabled); } catch { }
            try { info.AppendLine("TUN：" + AppState.State.UseTun); } catch { }
            File.WriteAllText(Path.Combine(tmp, "info.txt"), info.ToString(), new UTF8Encoding(false));

            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(tmp, zipPath);
            return zipPath;
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }
}
