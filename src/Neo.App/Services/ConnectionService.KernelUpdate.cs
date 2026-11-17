namespace Neo.App.Services;

public static partial class ConnectionService
{
    /// <summary>Keep connection/switch requests out of the file commit, wait for
    /// actual process teardown, and restore only the session this update stopped.</summary>
    internal static async Task<(bool Ok, string Message)> ReplaceKernelFilesAsync(
        int expectedEpoch, Func<(bool Ok, string Message)> replace, CancellationToken ct)
    {
        if (!await SessionGate.WaitAsync(0, ct).ConfigureAwait(false))
            return (false, "连接操作正在进行，请稍后更新内核。");
        Neo.Core.Models.NodeItem? restoreNode = null;
        int? restoreEpoch = null;
        var teardownHeld = false;
        var cancelled = false;
        (bool Ok, string Message) result = (false, "内核更新未完成。");
        try
        {
            ct.ThrowIfCancellationRequested();
            lock (SessionStateGate)
            {
                if (expectedEpoch != _sessionEpoch || App.IsShuttingDown)
                    return (false, "下载期间连接状态已改变，已保留当前会话；请重新更新。");
                if (IsConnected)
                {
                    restoreNode = ConnectedNode;
                    if (!ResetSession("安装新内核前断开", expectedEpoch, null, out var next))
                        return (false, "连接状态已改变，请重新更新。");
                    restoreEpoch = next;
                    expectedEpoch = next;
                }
            }
            await LastTunTeardown.ConfigureAwait(false);
            await TeardownGate.WaitAsync(ct).ConfigureAwait(false);
            teardownHeld = true;
            ct.ThrowIfCancellationRequested();
            if (expectedEpoch != SessionEpoch || App.IsShuttingDown)
                result = (false, "更新已停止：连接状态已改变。");
            else result = replace(); // Commit is intentionally non-cancellable; rollback must finish.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { cancelled = true; }
        catch (Exception ex) { result = (false, "安装失败，已尝试恢复原内核：" + ex.Message); }
        finally
        {
            if (teardownHeld) TeardownGate.Release();
            SessionGate.Release();
        }
        if (restoreNode != null && restoreEpoch == SessionEpoch && !App.IsShuttingDown
            && AppState.CurrentNode != null && SameNode(AppState.CurrentNode, restoreNode))
        {
            var restored = await ConnectAsync(restoreNode, expectedEpoch: restoreEpoch).ConfigureAwait(false);
            if (!restored.Ok) result = (false, result.Message + "；恢复原节点失败：" + restored.Error);
        }
        if (cancelled) throw new OperationCanceledException(ct);
        return result;
    }
}
