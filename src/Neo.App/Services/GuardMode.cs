using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using Neo.Core;

namespace Neo.App.Services;

/// <summary>
/// 事件驱动看门狗（无界面）。由 neo 启动时以 `neo.exe --guard &lt;neoPid&gt;` 拉起，
/// 不加入内核的 Job 对象，因此 neo 被强杀/崩溃后它仍活着，负责：
///   1) 等「neo 进程退出 / 内核进程退出 / 状态变化事件」——不轮询；
///   2) 内核退出时读 core-state.json 判断是正常切换/故障转移/重启，还是真死；
///   3) 确认死亡：写日志 → 查残留（只清 neo runtime 下的内核）→ 还原系统代理 →
///      TUN 残留清理（无权限则通知 + 5 分钟等待 + 待清理标记）→ 复查 → 无残留静默退出。
/// </summary>
public static class GuardMode
{
    private const uint Infinite = 0xFFFFFFFF;
    private static Process? _neo;
    private static Process? _core;
    private static int _neoPid;
    private static int _corePid;
    private static EventWaitHandle? _stateEvent;
    private static readonly object LogGate = new();
    private static GuardConfig _cfg = new();

    private sealed class GuardConfig
    {
        public int Socks = LocalPorts.DefaultSocks;
        public int Http = LocalPorts.DefaultHttp;
        public string LogDir = "";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForMultipleObjects(uint count, IntPtr[] handles, bool waitAll, uint timeout);

    /// <summary>看门狗主入口：阻塞直到 neo 退出并完成清理（探针模式由命令行进入）。</summary>
    public static int Run(int neoPid)
    {
        _neoPid = neoPid;
        _cfg = LoadConfig();
        try { _neo = Process.GetProcessById(neoPid); } catch { _neo = null; }
        Log("看门狗启动 · neo PID=" + neoPid + " · 端口 " + _cfg.Socks + "/" + _cfg.Http + " · 日志目录 " + LogDir());
        _stateEvent = OpenStateEvent();

        while (true)
        {
            var snap = CoreState.Read();
            SyncCore(snap);

            var handles = new List<IntPtr>();
            if (_neo != null) handles.Add(_neo.Handle);
            var coreIdx = -1;
            if (_core != null)
            {
                coreIdx = handles.Count;
                handles.Add(_core.Handle);
            }
            var evIdx = handles.Count;
            handles.Add(_stateEvent.SafeWaitHandle.DangerousGetHandle());

            uint rc;
            try { rc = WaitForMultipleObjects((uint)handles.Count, handles.ToArray(), false, Infinite); }
            catch { rc = 0xFFFFFFFF; }
            if (rc == 0xFFFFFFFF) { Thread.Sleep(500); continue; }

            var idx = (int)rc;
            if (_neo != null && idx == 0)
            {
                HandleNeoGone();
                return 0;
            }
            if (coreIdx >= 0 && idx == coreIdx)
            {
                HandleCoreExit();
                continue;
            }
            if (idx == evIdx)
            {
                try { _stateEvent.Reset(); } catch { }
            }
        }
    }

    /// <summary>--cleanup-tun：管理员身份运行，清 TUN 残留后退出。返回进程退出码。</summary>
    public static int RunCleanupTun()
    {
        try
        {
            Log("收到管理员清理请求（--cleanup-tun）");
            var had = ConnectionService.HasTunResidueForGuard();
            if (!had) { Log("未检测到 neo-tun 残留，无需清理"); GuardPending.Clear(); return 0; }
            var ok = ConnectionService.CleanupTunResidueForGuard();
            Log(ok ? "TUN 残留清理成功" : "TUN 残留清理失败（需要管理员权限或仍有进程占用）");
            if (ok) GuardPending.Clear();
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log("TUN 清理异常：" + ex.GetType().Name + " " + ex.Message);
            return 2;
        }
    }

    // ---------------- 事件处理 ----------------

    private static void HandleNeoGone()
    {
        var snap = CoreState.Read();
        Log("neo 进程已退出 · state=" + (snap?.State ?? "?") + " · corePid=" + (snap?.CorePid ?? 0));
        DoCleanup(snap?.CorePid ?? 0, "neo 进程退出");
        Log("清理完成，看门狗退出");
    }

    private static void HandleCoreExit()
    {
        var pid = _corePid;
        try { _core?.Dispose(); } catch { }
        _core = null;

        var snap = CoreState.Read();
        Log("内核进程退出（PID " + pid + "）· state=" + (snap?.State ?? "?"));

        if (_neo == null || _neo.HasExited)
        {
            DoCleanup(pid, "内核退出且 neo 已退出");
            Log("清理完成，看门狗退出");
            Environment.Exit(0);
        }

        // 竞态窗口：neo 的 Process.Exited 回调与本方法并发，状态文件可能还没更新。
        // 只等「状态变化事件」，不是固定检测周期。
        var changed = WaitStateEvent(1500);
        var s2 = CoreState.Read();
        if (s2 != null && (IsTransitionState(s2.State) || (s2.CorePid > 0 && s2.CorePid != pid)))
        {
            Log("判定为正常切换/故障转移/重启（state=" + s2.State + "），不介入");
            return;
        }
        if (changed && s2 != null && s2.State == "running" && s2.CorePid == pid)
        {
            // 状态没变但事件到了：继续往下做功能复核
        }

        var listening = IsListening(_cfg.Socks) || IsListening(_cfg.Http);
        var usable = listening && ProbeProxy();
        Log("内核退出但 neo 仍在运行 · 端口监听=" + listening + " · 功能探测=" + usable);
        if (!usable)
        {
            Log("确认代理不可用（neo 未自行恢复）—— 仅记录，等待程序自身恢复或退出；不主动断开会话");
        }
    }

    // ---------------- 清理 ----------------

    private static void DoCleanup(int corePid, string trigger)
    {
        var listening = IsListening(_cfg.Socks) || IsListening(_cfg.Http);
        var usable = listening && ProbeProxy();
        Log("触发清理：" + trigger + " · 端口监听=" + listening + " · 功能探测=" + usable);

        if (corePid > 0)
        {
            try
            {
                var p = Process.GetProcessById(corePid);
                Log("残留内核进程仍在运行（PID " + corePid + "），尝试清理");
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
                Log("已结束残留内核 PID " + corePid);
            }
            catch { }
        }

        try
        {
            Log("清理残留内核进程（runtime 目录保护）");
            ConnectionService.CleanupOrphanCores();
        }
        catch (Exception ex) { Log("清理残留内核异常：" + ex.GetType().Name + " " + ex.Message); }

        try
        {
            var disabled = SystemProxy.CleanupOrphaned(_cfg.Socks, _cfg.Http, _cfg.Socks + 100, _cfg.Http + 100, 10808, 10809);
            if (disabled) Log("已关闭/还原残留系统代理");
            if (SystemProxy.CleanupStaleSnapshot()) Log("已恢复残留的系统代理快照");
        }
        catch (Exception ex) { Log("处理系统代理异常：" + ex.GetType().Name + " " + ex.Message); }

        try { HandleTunCleanup(); }
        catch (Exception ex) { Log("TUN 清理异常：" + ex.GetType().Name + " " + ex.Message); }

        var left = CountLeftovers();
        Log(left == 0 ? "复查：无残留进程" : "复查：仍有 " + left + " 个残留内核进程（已记录）");
    }

    private static void HandleTunCleanup()
    {
        if (!ConnectionService.HasActiveTunResidueForGuard())
        {
            if (ConnectionService.HasTunResidueForGuard())
                Log("检测到未启用的 neo-tun 残留网卡（无路由，不影响上网）—— 跳过自动清理");
            GuardPending.Clear();
            return;
        }

        if (ConnectionService.IsElevated)
        {
            var ok = ConnectionService.CleanupTunResidueForGuard();
            Log("TUN 残留清理（管理员）= " + (ok ? "成功" : "失败"));
            if (ok)
            {
                GuardPending.Clear();
            }
            else
            {
                // 已是管理员仍清不掉（网卡被占用/驱动拒绝）→ 留标记，下次启动提醒用户重试。
                GuardPending.Write("看门狗以管理员身份清理 TUN 残留失败");
                Log("已写待清理标记（下次启动提醒）");
            }
            return;
        }

        GuardPending.Write("看门狗检测到 TUN 残留但无管理员权限");
        Log("检测到 TUN 残留但当前无管理员权限 —— 发通知请求授权（5 分钟内有效）");
        var clicked = GuardNotify.RequestAdmin(5 * 60 * 1000);
        if (!clicked)
        {
            Log("5 分钟内未获得授权（或通知不可用）—— 已写待清理标记，下次启动提醒并在 设置→通用 提供清理开关");
            return;
        }
        var code = GuardPending.LaunchElevatedCleanup();
        Log("提权清理返回退出码 " + code);
        if (code == 0) GuardPending.Clear();
    }

    private static int CountLeftovers()
    {
        var n = 0;
        foreach (var name in new[] { "xray", "sing-box", "naive" })
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        var path = p.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(path) && IsUnderRuntime(path)) n++;
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch { }
        }
        return n;
    }

    private static bool IsUnderRuntime(string path)
    {
        try
        {
            var rt = NeoPaths.RuntimeDir.TrimEnd('\\') + Path.DirectorySeparatorChar;
            return path.StartsWith(rt, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ---------------- 状态/进程/探测 ----------------

    private static void SyncCore(CoreState.Snapshot? snap)
    {
        if (snap == null) return;
        if (!IsCoreWatchedState(snap.State) || snap.CorePid <= 0)
        {
            if (_core != null) { try { _core.Dispose(); } catch { } _core = null; }
            _corePid = 0;
            return;
        }
        if (_core != null && !_core.HasExited && _core.Id == snap.CorePid) return;
        try { _core?.Dispose(); } catch { }
        _core = null;
        try
        {
            var p = Process.GetProcessById(snap.CorePid);
            if (p.HasExited) { try { p.Dispose(); } catch { } return; }
            _core = p;
            _corePid = snap.CorePid;
        }
        catch { _corePid = snap.CorePid; }
    }

    private static bool IsCoreWatchedState(string? state) =>
        state is "running" or "starting" or "restarting" or "switching" or "failover";

    private static bool IsTransitionState(string? state) =>
        state is "switching" or "failover" or "stopping" or "starting" or "restarting" or "idle";

    private static bool WaitStateEvent(int timeoutMs)
    {
        try { return _stateEvent?.WaitOne(timeoutMs) ?? false; }
        catch { return false; }
        finally { try { _stateEvent?.Reset(); } catch { } }
    }

    private static EventWaitHandle OpenStateEvent()
    {
        try
        {
            return EventWaitHandle.OpenExisting(CoreState.EventName);
        }
        catch { }
        try { return new EventWaitHandle(false, EventResetMode.ManualReset, CoreState.EventName); }
        catch { return new EventWaitHandle(false, EventResetMode.ManualReset); }
    }

    private static bool IsListening(int port)
    {
        try
        {
            using var c = new System.Net.Sockets.TcpClient();
            using var cts = new CancellationTokenSource(300);
            c.ConnectAsync("127.0.0.1", port, cts.Token).GetAwaiter().GetResult();
            return true;
        }
        catch { return false; }
    }

    /// <summary>功能探测：通过用户端口做一次 204 请求，确认“端口在”不等于“代理能用”。</summary>
    private static bool ProbeProxy()
    {
        try
        {
            using var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy(new Uri("socks5://127.0.0.1:" + _cfg.Socks)),
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromSeconds(3),
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            using var resp = http.GetAsync("http://cp.cloudflare.com/generate_204").GetAwaiter().GetResult();
            return (int)resp.StatusCode is 200 or 204;
        }
        catch { return false; }
    }

    // ---------------- 配置与日志 ----------------

    private static GuardConfig LoadConfig()
    {
        var cfg = new GuardConfig();
        try
        {
            var path = Path.Combine(NeoPaths.DataDir, "state.json");
            if (!File.Exists(path)) return cfg;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("SocksPort", out var s) && s.TryGetInt32(out var sv) && sv > 0) cfg.Socks = sv;
            if (doc.RootElement.TryGetProperty("HttpPort", out var h) && h.TryGetInt32(out var hv) && hv > 0) cfg.Http = hv;
            if (doc.RootElement.TryGetProperty("LogDirectory", out var d) && d.ValueKind == JsonValueKind.String)
                cfg.LogDir = d.GetString() ?? "";
        }
        catch { }
        return cfg;
    }

    private static string LogDir()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_cfg.LogDir))
            {
                Directory.CreateDirectory(_cfg.LogDir);
                return _cfg.LogDir;
            }
        }
        catch { }
        return NeoPaths.LogDir;
    }

    private static void Log(string message)
    {
        try
        {
            lock (LogGate)
            {
                var dir = LogDir();
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, "guard-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "-GUARD " + message + Environment.NewLine);
            }
        }
        catch { }
    }
}

/// <summary>
/// 管理员授权通知（Windows 通知）。不可用时返回 false，调用方退回「写日志 + 下次启动提醒」。
/// 5 分钟内用户点了通知才算确认；不弹模态框，避免打扰游戏。
/// </summary>
internal static class GuardNotify
{
    public static bool RequestAdmin(int timeoutMs)
    {
        try
        {
            var mgr = Microsoft.Windows.AppNotifications.AppNotificationManager.Default;
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            mgr.NotificationInvoked += (_, _) => tcs.TrySetResult(true);
            mgr.Register();
            var note = new Microsoft.Windows.AppNotifications.Builder.AppNotificationBuilder()
                .AddText("neo 需要管理员权限")
                .AddText("检测到 TUN 残留，点击本通知授权清理（5 分钟内有效，超时下次启动再处理）")
                .BuildNotification();
            mgr.Show(note);
            return tcs.Task.Wait(timeoutMs);
        }
        catch
        {
            return false;
        }
    }
}
