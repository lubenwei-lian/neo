using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Neo.Core;

namespace Neo.App.Services;

/// <summary>
/// 「待清理的 TUN 残留」标记：看门狗无管理员权限时写入。
/// 下次启动读它 → 提醒用户 + 设置→通用 出现临时开关「清理内核残留」；清理成功后删除。
/// </summary>
public static class GuardPending
{
    public static string FilePath => Path.Combine(NeoPaths.DataDir, "guard-pending-tun.json");

    public static bool Exists
    {
        get
        {
            try { return File.Exists(FilePath); } catch { return false; }
        }
    }

    public static void Write(string reason)
    {
        try
        {
            Directory.CreateDirectory(NeoPaths.DataDir);
            var info = new { detectedUtc = DateTime.UtcNow.ToString("o"), reason };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }

    /// <summary>以管理员身份启动本程序执行 --cleanup-tun，等待并返回退出码（-1 = 启动失败/被取消）。</summary>
    public static int LaunchElevatedCleanup()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return -1;
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            };
            psi.ArgumentList.Add("--cleanup-tun");
            using var p = Process.Start(psi);
            if (p == null) return -1;
            p.WaitForExit(60000);
            return p.HasExited ? p.ExitCode : -1;
        }
        catch { return -1; }
    }
}
