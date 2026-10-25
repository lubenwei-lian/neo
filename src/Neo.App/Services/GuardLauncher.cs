using System.Diagnostics;

namespace Neo.App.Services;

/// <summary>
/// 看门狗启动器：以同目录的 neo.exe 再起一个无界面进程（--guard &lt;neoPid&gt;）。
/// 它**不加入内核 Job**，所以 neo 被强杀/崩溃后仍活着，能完成残留清理。
/// </summary>
public static class GuardLauncher
{
    public static void Start()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            psi.ArgumentList.Add("--guard");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            try { LogBus.App("看门狗启动失败：" + ex.GetType().Name + " " + ex.Message, "Warning"); } catch { }
        }
    }
}
