using System.Diagnostics;
using Neo.Core;
using Neo.Core.Core;

namespace Neo.App.Services;

public static partial class ConnectionService
{
    private static CoreProcess? _tunBridge;
    private static int _tunBridgeRestarts;

    internal static int[] RunningCorePids()
    {
        lock (SessionStateGate)
            return new[] { _xray, _retiringCore, _naive, _tunBridge }
                .Where(core => core is { IsRunning: true }).Select(core => core!.Pid)
                .Where(pid => pid > 0).Distinct().ToArray();
    }

    /// <summary>Change ingress without resetting the node session, relays, counters or timer.</summary>
    public static async Task<(bool Ok, string? Error)> SetTunEnabledAsync(bool enabled,
        CancellationToken ct = default)
    {
        var epoch = SessionEpoch;
        if (ct.IsCancellationRequested || App.IsShuttingDown) return (false, ConnectionCancelled);
        if (!await SessionGate.WaitAsync(0).ConfigureAwait(false))
            return (false, "已有连接操作正在进行，请稍候重试");
        var lifecycleHeld = false;
        var originalBridge = _tunBridge;
        var committed = false;
        try
        {
            if (epoch != SessionEpoch || ct.IsCancellationRequested || App.IsShuttingDown)
                return (false, ConnectionCancelled);
            if (enabled && !IsElevated) return (false, "启用 TUN 需要管理员权限");
            if (!IsConnected)
            {
                AppState.State.UseTun = enabled;
                committed = true;
                return (true, null);
            }
            await TeardownGate.WaitAsync(ct).ConfigureAwait(false);
            lifecycleHeld = true;
            if (epoch != SessionEpoch || App.IsShuttingDown || ct.IsCancellationRequested)
                return (false, ConnectionCancelled);
            if (enabled)
            {
                _tunBridgeRestarts = 0;
                var started = await StartTunBridgeAsync(epoch, ct).ConfigureAwait(false);
                if (!started.Ok) return started;
            }
            else await StopTunBridgeAsync().ConfigureAwait(false);
            lock (SessionStateGate)
            {
                if (epoch != _sessionEpoch || !IsConnected || App.IsShuttingDown
                    || enabled && ct.IsCancellationRequested)
                    return (false, ConnectionCancelled);
                AppState.State.UseTun = enabled;
                committed = true;
            }
            FailoverService.MarkTunNetworkEvent();
            CoreState.Report("running", CorePid, CoreKind, enabled ? "tun ingress enabled" : "tun ingress disabled", epoch);
            LogBus.App($"TUN 接管已{(enabled ? "开启" : "关闭")}；节点内核 PID {CorePid}、连接计时与本地代理连接保持不变");
            return (true, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (false, ConnectionCancelled);
        }
        catch (Exception ex)
        {
            LogBus.Exception("切换 TUN 接管失败（节点连接保留）", ex);
            return (false, "切换 TUN 接管失败，节点连接已保留：" + ex.Message);
        }
        finally
        {
            if (lifecycleHeld && enabled && !committed && originalBridge == null && _tunBridge != null)
                try { await StopTunBridgeAsync().ConfigureAwait(false); }
                catch (Exception ex) { LogBus.Exception("撤销未提交的 TUN 接管失败", ex); }
            if (lifecycleHeld) TeardownGate.Release();
            SessionGate.Release();
            NotifyStateChanged();
        }
    }

    // Caller holds SessionGate and TeardownGate. Session revocation can still happen at any await.
    private static async Task<(bool Ok, string? Error)> StartTunBridgeAsync(int epoch, CancellationToken ct)
    {
        if (_tunBridge is { IsRunning: true } && _tunSessionActive) return (true, null);
        if (_tunBridge != null) await StopTunBridgeAsync().ConfigureAwait(false);
        var transport = _xray;
        bool Current() => epoch == SessionEpoch && transport is { IsRunning: true }
            && ReferenceEquals(transport, _xray) && !ct.IsCancellationRequested && !App.IsShuttingDown;
        if (!Current()) return (false, ConnectionCancelled);
        var exe = Path.Combine(NeoPaths.RuntimeDir, "sing-box.exe");
        if (!File.Exists(exe)) return (false, "TUN 接管需要 sing-box 内核；原节点连接已保留");
        if (!_tunPreStartGuardDone)
        {
            _tunPreStartGuardDone = true;
            EnsureTunAdapterCleared();
        }
        if (HasActiveTunResidueForGuard())
            return (false, "旧 TUN 网卡或路由仍在生效，未修改原节点连接");
        var path = Path.Combine(NeoPaths.DataDir, $"neo-tun-{Environment.ProcessId}-{epoch}-{Guid.NewGuid():N}.json");
        CoreProcess? bridge = null;
        var committed = false;
        var tail = new System.Collections.Concurrent.ConcurrentQueue<string>();
        try
        {
            var state = AppState.State;
            var paths = new[] { "xray.exe", "sing-box.exe", "mihomo.exe", "naive.exe" }
                .Select(name => Path.Combine(NeoPaths.RuntimeDir, name)).ToArray();
            var json = SingBoxConfigBuilder.BuildTunBridge(LocalPorts.Socks, state.KernelLogLevel,
                state.DnsServer, paths, state.GameDirectEnabled, AppState.GameDirectProcessList());
            File.WriteAllText(path, json);
            var check = await Task.Run(() => KernelCompat.RunCheck(exe, KernelKind.SingBox, path, 8000), ct).ConfigureAwait(false);
            if (!check.Ok) return (false, "TUN 接管配置校验失败，原节点连接已保留：" + KernelCompat.FirstLines(check.Output, 3));
            if (!Current()) return (false, ConnectionCancelled);
            bridge = new CoreProcess(exe, NeoPaths.RuntimeDir);
            var owned = bridge;
            bridge.LogLine += line =>
            {
                tail.Enqueue(line);
                while (tail.Count > 12) tail.TryDequeue(out _);
                LogBus.Core("[TUN] " + line);
            };
            bridge.Exited += code => _ = RecoverTunBridgeAsync(owned, epoch, code);
            lock (SessionStateGate)
            {
                if (!Current()) return (false, ConnectionCancelled);
                CoreConfigs[bridge] = path;
                _tunBridge = bridge;
                bridge.Start(path);
            }
            var timer = Stopwatch.StartNew();
            while (Current() && bridge.IsRunning && timer.Elapsed < TimeSpan.FromSeconds(6))
            {
                if (IsTunAdapterActive())
                {
                    // The device can appear before sing-box finishes starting its packet stack.
                    await Task.Delay(120, ct).ConfigureAwait(false);
                    lock (SessionStateGate)
                    {
                        if (Current() && bridge.IsRunning && ReferenceEquals(_tunBridge, bridge) && IsTunAdapterActive())
                        {
                            _tunSessionActive = true;
                            committed = true;
                            LogBus.App($"TUN 接管层就绪（PID {bridge.Pid} · {timer.ElapsedMilliseconds} ms）；沿用节点内核 PID {transport!.Pid}", "Debug");
                            return (true, null);
                        }
                    }
                }
                await Task.Delay(60, ct).ConfigureAwait(false);
            }
            return (false, !Current() ? ConnectionCancelled
                : "TUN 接管层启动失败，原节点连接已保留：" + KernelCompat.FirstLines(string.Join(Environment.NewLine, tail), 4));
        }
        finally
        {
            if (!committed)
            {
                var adapter = bridge is { Pid: > 0 } ? CaptureTunAdapterId() : null;
                if (bridge != null)
                {
                    await Task.Run(() => bridge.StopAndVerify()).ConfigureAwait(false);
                    CoreConfigs.TryRemove(bridge, out _);
                    lock (SessionStateGate)
                        if (ReferenceEquals(_tunBridge, bridge)) { _tunBridge = null; _tunSessionActive = false; }
                }
                if (adapter != null && HasTunAdapter())
                    await Task.Run(() => TeardownTunNetwork(adapter)).ConfigureAwait(false);
                try { File.Delete(path); } catch { }
            }
        }
    }

    private static async Task StopTunBridgeAsync()
    {
        var bridge = _tunBridge;
        if (bridge == null) return;
        var adapter = CaptureTunAdapterId();
        if (!await Task.Run(() => bridge.StopAndVerify()).ConfigureAwait(false))
            throw new IOException("TUN 接管进程尚未退出，节点内核未受影响");
        if (adapter != null && HasTunAdapter())
            await Task.Run(() => TeardownTunNetwork(adapter)).ConfigureAwait(false);
        if (HasActiveTunResidueForGuard())
            throw new IOException("TUN 路由清理未完成，已保留重试状态");
        if (CoreConfigs.TryRemove(bridge, out var path))
            try { File.Delete(path); } catch { }
        lock (SessionStateGate)
            if (ReferenceEquals(_tunBridge, bridge)) { _tunBridge = null; _tunSessionActive = false; }
    }

    private static async Task RecoverTunBridgeAsync(CoreProcess bridge, int epoch, int code)
    {
        await SessionGate.WaitAsync().ConfigureAwait(false);
        var lifecycleHeld = false;
        try
        {
            if (epoch != SessionEpoch || !IsConnected || !ReferenceEquals(_tunBridge, bridge)
                || !_tunSessionActive || App.IsShuttingDown) return;
            await TeardownGate.WaitAsync().ConfigureAwait(false);
            lifecycleHeld = true;
            if (epoch != SessionEpoch || !IsConnected || !ReferenceEquals(_tunBridge, bridge)) return;
            LogBus.App($"TUN 接管层异常（exit {code}）；节点内核与连接保留", "Warning");
            await StopTunBridgeAsync().ConfigureAwait(false);
            if (epoch != SessionEpoch || App.IsShuttingDown) return;
            if (AppState.State.AutoRestartCore && AppState.State.UseTun && _tunBridgeRestarts++ == 0)
            {
                var restarted = await StartTunBridgeAsync(epoch, CancellationToken.None).ConfigureAwait(false);
                if (restarted.Ok) { LogBus.App("已单独恢复 TUN 接管层，节点没有重连"); return; }
                LogBus.App("TUN 接管层恢复失败：" + restarted.Error, "Warning");
            }
            lock (SessionStateGate)
            {
                if (epoch != _sessionEpoch || !IsConnected || App.IsShuttingDown) return;
                AppState.State.UseTun = false;
                AppState.Save();
            }
            AppState.NotifyUiSettingsChanged();
            NotifyUser("TUN 接管已停止，已保留系统代理和原节点连接");
        }
        catch (Exception ex) { LogBus.Exception("TUN 接管层恢复失败", ex); }
        finally
        {
            if (lifecycleHeld) TeardownGate.Release();
            SessionGate.Release();
            NotifyStateChanged();
        }
    }
}
