using Neo.App.Services;

namespace Neo.App.Services;

/// 把流量统计的取数搬离 UI 线程。
/// CoreStats.Query() 优先复用本机 gRPC 连接；旧内核/接口不兼容时回退 CLI 子进程。
/// 即使使用直读也保持后台采样，防止超时或系统 TCP 表枚举占用 UI 线程。
/// 原 CLI 路径是「启动 xray.exe 子进程 + 同步 ReadToEnd」的调用，
/// 1 秒 DispatcherTimer 在 UI 线程上直接调用 —— 等于每秒钟冻结 UI 线程 80 ms（8% 的帧时间），
/// 且瓶颈在进程创建与子进程调度，与节点快慢无关。连接数统计同样在 UI 线程内同步枚举 TCP 表
/// 这里用「后台固定节奏采样 + UI 线程只读缓存」替代「UI 线程按需取数」：
///   - UI 线程每秒只做一次字典/字段读取（纳秒级），不再有任何 IO 或进程操作；
///   - 取数节奏仍是 1 秒，界面刷新观感与原先一致；
///   - 采样异常绝不上抛，避免因为一次统计失败影响界面。
public static class StatsSampler
{
    private static readonly object Gate = new();
    private static CoreStats.Snapshot? _latest;
    private static int _activeConnections;
    private static int? _coreMemoryMb;
    private static long _sampledAtTicks;
    private static int _running;
    private static int _sampleSessionEpoch = -1;
    private static int _sampleCorePid;
    private static readonly Neo.Core.Presentation.TrafficSampleBuffer Traffic = new();

    internal static Neo.Core.Presentation.TrafficSampleBuffer.Snapshot CaptureTraffic()
    {
        lock (Gate) return Traffic.Capture(ConnectionService.IsConnected ? ConnectionService.SessionEpoch : -1,
            ConnectionService.CorePid);
    }

    /// 旧实现的 Stop 只是把 _running 置 0，而循环可能正卡在 Task.Delay 里；
    /// 紧接着的 Start 把 _running 置回 1 后，旧循环醒来会以为是「自己还在跑」，
    /// 于是同一个进程里出现两条采样循环（重复 Query 内核、重复累加流量）。
    /// 现在 Start 只给新循环发一个代号，循环每轮都校验代号，Stop 推进代号即可保证旧循环退出。
    private static int _generation;

    /// <summary>最近一次成功的采样值；未连接或尚无样本时为 null。</summary>
    public static CoreStats.Snapshot? Latest
    {
        get { lock (Gate) return _latest; }
    }

    /// <summary>最近一次采样到的活动连接数。</summary>
    public static int ActiveConnections
    {
        get { lock (Gate) return _activeConnections; }
    }

    /// <summary>当前会话的后台内存样本；UI 读取不枚举进程，切换后不返回旧内核数据。</summary>
    public static int? CoreMemoryMb
    {
        get
        {
            try
            {
                lock (Gate)
                    return Volatile.Read(ref _running) == 1 && ConnectionService.IsConnected
                        && _sampleSessionEpoch == ConnectionService.SessionEpoch
                        && _sampleCorePid == ConnectionService.CorePid ? _coreMemoryMb : null;
            }
            catch { return null; }
        }
    }

    private static int? ReadCoreMemoryMb(int pid)
    {
        if (pid <= 0) return null;
        long bytes = 0;
        var read = false;
        // The ingress bridge is part of the running core footprint too. Do not hide
        // its cost when TUN changes without changing the primary core PID.
        foreach (var ownedPid in ConnectionService.RunningCorePids())
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(ownedPid);
                bytes += process.WorkingSet64;
                read = true;
            }
            catch { }
        }
        return read ? (int)(bytes / 1024 / 1024) : null;
    }

    /// <summary>最近一次采样完成的时间（本地时间），供界面判断数据新鲜度。</summary>
    public static DateTime SampledAt
    {
        get { lock (Gate) return new DateTime(Interlocked.Read(ref _sampledAtTicks)); }
    }

    /// <summary>幂等：重复调用不会起第二个采样循环。</summary>
    public static void Start()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        var gen = Interlocked.Increment(ref _generation);
        _ = Task.Run(() => LoopAsync(gen));
    }

    public static void Stop()
    {
        Interlocked.Exchange(ref _running, 0);
        //   下一次 Start 重新 CAS 置 _running=1 并领新代号即可。
        Interlocked.Increment(ref _generation);
        lock (Gate)
        {
            _latest = null;
            _activeConnections = 0;
            _coreMemoryMb = null;
            _sampleSessionEpoch = -1;
            _sampleCorePid = 0;
            Traffic.Clear();
            Interlocked.Exchange(ref _sampledAtTicks, 0);
            UsageTracker.SessionBreak();
        }
    }

    private static DateTime _lastNullDiagAtUtc;

    /// <summary>
    /// 连续取数为空时每 30 秒记一条：界面会停在上一帧，
    /// 没有这条日志就无法区分「真的没流量」和「统计链路失败」。
    /// </summary>
    private static void DiagNull()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastNullDiagAtUtc).TotalSeconds < 30) return;
        _lastNullDiagAtUtc = now;
        try { LogBus.App("流量统计：本轮采样为空（界面保留上一帧，具体原因见「流量统计取数失败」日志）", "Warning"); } catch { }
    }

    private static async Task LoopAsync(int generation)
    {
        //   确保「Stop 后旧循环退出、Start 只起一个新循环」。
        while (Volatile.Read(ref _running) == 1 && Volatile.Read(ref _generation) == generation)
        {
            try
            {
                if (ConnectionService.IsConnected)
                {
                    var epoch = ConnectionService.SessionEpoch;
                    var pid = ConnectionService.CorePid;
                    // 这两项都在本后台线程上执行：进程调用 + TCP 表枚举都不再占用 UI 线程。
                    var snap = CoreStats.Query();
                    var active = CoreStats.ActiveConnections();
                    var memory = ReadCoreMemoryMb(pid);
                    lock (Gate)
                    {
                        // 查询是阻塞 I/O；期间 Stop/Start、断开或换内核后，旧样本必须丢弃。
                        if (Volatile.Read(ref _running) != 1 || Volatile.Read(ref _generation) != generation
                            || !ConnectionService.IsConnected || epoch != ConnectionService.SessionEpoch
                            || pid != ConnectionService.CorePid) continue;
                        if (_sampleSessionEpoch != epoch || _sampleCorePid != pid)
                        {
                            Traffic.Clear();
                            UsageTracker.SessionBreak();
                            _latest = null;
                            Interlocked.Exchange(ref _sampledAtTicks, 0);
                            _sampleSessionEpoch = epoch;
                            _sampleCorePid = pid;
                        }
                        _activeConnections = active;
                        _coreMemoryMb = memory;
                        if (snap is { } s)
                        {
                            _latest = s;
                            var sampledAt = DateTime.Now;
                            Interlocked.Exchange(ref _sampledAtTicks, sampledAt.Ticks);
                            Traffic.Record(epoch, pid, s.Uplink, s.Downlink, sampledAt);
                            // CoreStats settles per-instance deltas independently of UI/session generations.
                        }
                    }

                    //   每秒在 UI 线程调用 UsageTracker.Sample）。搬到后台采样循环后，
                    //   即使用户切到别的页面、页面计时器停掉，流量统计也照常累计。
                    if (snap == null) DiagNull();
                }
                else
                {
                    lock (Gate)
                    {
                        if (Volatile.Read(ref _generation) != generation) continue;
                        _latest = null;
                        _activeConnections = 0;
                        _coreMemoryMb = null;
                        _sampleSessionEpoch = -1;
                        _sampleCorePid = 0;
                        Traffic.Clear();
                        Interlocked.Exchange(ref _sampledAtTicks, 0);
                        UsageTracker.SessionBreak();
                    }
                    //   UsageTracker 内部对计数倒退也会重置基线，这里显式调用语义更清楚。
                }
            }
            catch
            {
                // 采样失败保留上一帧，界面不会闪成 0；下一轮自动重试。
            }

            try { await Task.Delay(1000).ConfigureAwait(false); }
            catch { return; }
        }
    }
}
