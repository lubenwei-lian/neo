using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Neo.Core.Core;

/// <summary>
/// 给内核子进程挂一个「随主进程一起结束」的 Job 对象。
///
/// 背景：neo 被任务管理器强杀 / 崩溃时，进程内没有机会执行 Disconnect 收尾，
/// 于是 xray / sing-box 子进程会残留（继续占内核端口、TUN 路由），
/// 而用户端口中继已经随主进程消失 —— 表现为「端口占着但代理不通」。
/// 用 JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE：只要本进程句柄被系统回收（进程结束），
/// Windows 就会终止 Job 内的所有子进程，不依赖退出代码是否执行。
///
/// 失败降级：若本进程本身处在不允许 breakaway 的 Job 中（某些启动器/沙箱），
/// AssignProcessToJobObject 会失败；此时只记录一次日志，原有 Stop() 逻辑照常工作。
/// </summary>
public static class KillOnCloseJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private static readonly object _lock = new();
    private static IntPtr _job;
    private static bool _createFailed;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr job, int infoClass, IntPtr info, uint infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private static IntPtr EnsureJob()
    {
        lock (_lock)
        {
            if (_createFailed) return IntPtr.Zero;
            if (_job != IntPtr.Zero) return _job;

            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                _createFailed = true;
                return IntPtr.Zero;
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
            var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)length))
                {
                    try { CloseHandle(job); } catch { }
                    _createFailed = true;
                    return IntPtr.Zero;
                }
            }
            finally
            {
                try { Marshal.FreeHGlobal(buffer); } catch { }
            }

            _job = job;
            return _job;
        }
    }

    /// <summary>把已启动的子进程挂到 Job 上；失败返回 false 并给出原因（调用方只记录一次）。</summary>
    public static bool Attach(Process process, out string error)
    {
        error = "";
        try
        {
            var job = EnsureJob();
            if (job == IntPtr.Zero) { error = "创建 Job 对象失败"; return false; }
            if (process.Handle == IntPtr.Zero) { error = "子进程句柄无效"; return false; }
            if (!AssignProcessToJobObject(job, process.Handle))
            {
                error = "AssignProcessToJobObject 失败，Win32=" + Marshal.GetLastWin32Error();
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + " " + ex.Message;
            return false;
        }
    }
}
