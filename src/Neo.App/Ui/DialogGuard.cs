using Microsoft.UI.Xaml.Controls;
using Neo.App.Services;

namespace Neo.App.Ui;

/// 全局串行化 ContentDialog。WinUI 3 同一 XamlRoot 上**只允许一个 ContentDialog 打开**，
/// 并发打开第二个会抛 COMException，HRESULT 0x80000019（E_ASYNC_OPERATION_NOT_STARTED，
/// 中文系统消息为「某个异步操作没有正常启动。」）。
///   `未处理异常：COMException 某个异步操作没有正常启动。`
/// 随后进程以 0xc000027b fail-fast 终止（出错模块 Microsoft.UI.Xaml.dll）。
/// 该 HRESULT 正是「第二个对话框没能启动」的签名。触发条件在 neo 里是真实存在的：
///   - 30 分钟定时器 `MaybeAutoCheckAppUpdateAsync` 会主动 `PromptAutoUpdateAsync` 弹窗，
///     而它完全不关心用户此刻是否正开着别的对话框（添加订阅 / 切换节点确认 / 更新说明…）；
///   - 多个 `async void` 事件处理器（点按钮 → 拉订阅 → 失败弹窗）之间也没有互斥，
///     快速连点或「拉取失败弹窗」叠在「添加订阅对话框」上就会撞。
/// 这里用「单飞 + 排队」解决：同一时刻只有一个对话框真正打开，其余按调用顺序排队，
/// 前一个关闭后再打开下一个；调用方拿到的仍是自己那个对话框的真实结果。
public static class DialogGuard
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// 等待门的时间上限。**必须有上限**：串行化门是「持有者不释放、后来者永远进不来」的结构，
    /// 一旦某个对话框因异常情形再也不会关闭（XamlRoot 已脱离、窗口提前销毁、
    /// 或某条代码路径拿到门后卡住），无上限等待会让**全 app 的所有弹窗永久卡死** ——
    /// 用户看到的现象是「点什么按钮都没反应」，比偶尔弹窗冲突更糟。
    /// 超时后放行（宁愿冒一次并发冲突的风险，也不能让整个界面失去反馈能力），并留日志。
    private const int GateTimeoutMs = 8000;

    /// <summary>当前是否有对话框打开（供需要「静默失败」的调用方先行判断）。</summary>
    public static bool IsDialogOpen => Gate.CurrentCount == 0;

    /// 串行显示对话框。语义与 <see cref="ContentDialog.ShowAsync"/> 完全一致
    /// （返回用户点选的按钮结果），只是保证不会并发打开。
    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        // 退出中不要再弹窗：此时 XamlRoot 正在失效，弹窗只会换来 fail-fast。
        if (App.IsShuttingDown) return ContentDialogResult.None;

        var acquired = await WaitGateAsync().ConfigureAwait(true);
        try
        {
            if (App.IsShuttingDown) return ContentDialogResult.None;
            return await dialog.ShowAsync();
        }
        finally
        {
            // 只在真正持有时释放：Release 多调一次会抛 SemaphoreFullException，
            // 反而把「弹窗」变成新的崩溃源。
            if (acquired) { try { Gate.Release(); } catch { } }
        }
    }

    /// 尝试立即显示对话框；若已有对话框打开则不做任何事并返回 false。
    /// 供「后台自动弹窗」这类**可以放弃**的场景使用：与其排队等到用户处理完手头的事
    /// 再突然冒出来，不如安静跳过，让用户主动去更新页看。
    public static async Task<(bool Shown, ContentDialogResult Result)> TryShowAsync(ContentDialog dialog)
    {
        if (App.IsShuttingDown) return (false, ContentDialogResult.None);
        if (!await Gate.WaitAsync(0).ConfigureAwait(true)) return (false, ContentDialogResult.None);
        try
        {
            if (App.IsShuttingDown) return (false, ContentDialogResult.None);
            return (true, await dialog.ShowAsync());
        }
        finally
        {
            try { Gate.Release(); } catch { }
        }
    }

    /// <summary>带超时的取门。超时后仍然放行并留日志，避免全 app 弹窗永久卡死。</summary>
    private static async Task<bool> WaitGateAsync()
    {
        if (await Gate.WaitAsync(GateTimeoutMs).ConfigureAwait(true)) return true;
        try { LogBus.App("等待上一个对话框关闭超时，已强制放行本次弹窗（避免界面永久无响应）", "Warning"); } catch { }
        return false;
    }
}
