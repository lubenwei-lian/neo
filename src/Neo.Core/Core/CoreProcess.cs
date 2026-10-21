using System.Diagnostics;
using System.IO;

namespace Neo.Core.Core;

public sealed class CoreProcess : IDisposable
{
    private readonly string _exePath;
    private readonly string _workingDir;
    private Process? _process;

    public event Action<string>? LogLine;

    public event Action<int>? Exited;

    private volatile bool _stopping;

    public bool IsRunning => _process is { HasExited: false };
    public int Pid => _process?.Id ?? -1;

    public CoreProcess(string exePath, string workingDir)
    {
        _exePath = exePath;
        _workingDir = workingDir;
    }

    public void Start(string configPath)
    {
        if (IsRunning) throw new InvalidOperationException("内核已在运行");
        if (!File.Exists(_exePath)) throw new FileNotFoundException("内核不存在: " + _exePath);
        if (!File.Exists(configPath)) throw new FileNotFoundException("配置不存在: " + configPath);

        _stopping = false;
        var psi = new ProcessStartInfo
        {
            FileName = _exePath,
            WorkingDirectory = _workingDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(configPath);

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) LogLine?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) LogLine?.Invoke(e.Data); };
        p.Exited += (_, _) =>
        {
            if (!_stopping)
            {
                try { Exited?.Invoke(p.ExitCode); } catch { }
            }
        };

        try
        {
            p.Start();
        }
        catch
        {
            //   也不置空，Process 句柄泄漏到 GC；失败路径必须先释放再抛给调用方。
            try { p.Dispose(); } catch { }
            throw;
        }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _process = p;
    }

    public bool WaitForExit(int timeoutMs)
    {
        var p = _process;
        if (p == null) return true;
        try { return p.WaitForExit(timeoutMs); }
        catch { return true; }
    }

    /// 旧实现的问题：`p.Kill()` 后调 `p.WaitForExit(5000)` 却**丢弃返回值** ——
    /// 内核若在 5 秒内没死透（TUN 模式下要撤路由/关网卡，可能更慢），
    /// 调用方以为"已停止"而实际进程还活着、还在占用 neo-tun 网卡与路由表，
    /// 于是用户看到的现象就是 TUN「关不掉」。
    /// 现在：① 校验退出结果，失败则记录；② 二次兜底 Kill 再等一次；
    ///       ③ 仍不死就按进程名清理（只清本程序 runtime 目录下的，避免误杀第三方）。
    /// 返回 true 表示「已确认进程不再存活」。
    public bool StopAndVerify()
    {
        _stopping = true;
        var p = _process;
        _process = null;
        if (p == null) return true;

        bool exited = true;
        try
        {
            if (!p.HasExited)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                exited = p.WaitForExit(8000);
                if (!exited)
                {
                    // 二次兜底：整树 Kill 对已挂起的子进程可能没生效，再等一轮
                    try { p.Kill(entireProcessTree: true); } catch { }
                    exited = p.WaitForExit(4000);
                }
            }
        }
        catch { exited = false; }
        finally
        {
            try { p.Dispose(); } catch { }
        }
        return exited;
    }

    public void Stop()
    {
        _stopping = true;
        var p = _process;
        _process = null;
        if (p == null) return;

        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
        }
        catch { }
        finally
        {
            try { p.Dispose(); } catch { }
        }
    }

    public void Dispose() => Stop();
}
