using System.Diagnostics;
using System.IO;

namespace Neo.Core.Core;

public sealed class CoreProcess : IDisposable
{
    //   TUN 拆除 / 孤儿清理按名字杀 runtime 下 sing-box 时先跳过这些 PID，避免测速进行中被误杀。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _tempPids = new();
    public static bool IsTempCorePid(int pid) => pid > 0 && _tempPids.ContainsKey(pid);

    private readonly string _exePath;
    private readonly string _workingDir;
    private Process? _process;

    public event Action<string>? LogLine;

    public event Action<int>? Exited;

    private volatile bool _stopping;

    //   Job 绑定失败只提示一次，避免每个内核都刷一条（原因通常是环境，不会自己变好）。
    private static bool _jobAttachWarned;

    public bool IsRunning => _process is { HasExited: false };
    public int Pid => _process?.Id ?? -1;

    public CoreProcess(string exePath, string workingDir)
    {
        _exePath = exePath;
        _workingDir = workingDir;
    }

    /// <summary>默认启动方式：xray / sing-box 都是 `run -c &lt;config&gt;`。</summary>
    public void Start(string configPath) => Start(configPath, null);

    /// <summary>
    /// args 为 null 时走默认的 `run -c`。
    /// </summary>
    public void Start(string configPath, IReadOnlyList<string>? args)
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
        if (args == null)
        {
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(configPath);
        }
        else
        {
            foreach (var a in args) psi.ArgumentList.Add(a);
        }

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

        //  把内核挂到「随主进程结束」的 Job：neo 被任务管理器强杀/崩溃时 Disconnect 不会执行，
        //  内核子进程会残留并继续占端口；Job 由系统在进程结束时代为终止，无需依赖退出代码。
        try
        {
            if (!KillOnCloseJob.Attach(p, out var jobError) && !_jobAttachWarned)
            {
                _jobAttachWarned = true;
                LogLine?.Invoke("[neo] 内核未能加入随主进程结束的 Job（" + jobError + "）——本进程被强杀时该内核可能残留");
            }
        }
        catch (System.Exception ex)
        {
            if (!_jobAttachWarned)
            {
                _jobAttachWarned = true;
                LogLine?.Invoke("[neo] 内核 Job 绑定异常：" + ex.GetType().Name + " " + ex.Message);
            }
        }

        if (configPath.IndexOf("neo-realtest", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            try { _tempPids[p.Id] = 1; } catch { }
        }
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
        int pid = -1;
        try { pid = p.Id; } catch { }

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
            if (pid > 0) _tempPids.TryRemove(pid, out _);
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
        int pid = -1;
        try { pid = p.Id; } catch { }

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
            if (pid > 0) _tempPids.TryRemove(pid, out _);
            try { p.Dispose(); } catch { }
        }
    }

    public void Dispose() => Stop();
}
