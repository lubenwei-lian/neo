using System.IO;
using System.Text.Json;
using Neo.Core;

namespace Neo.App.Services;

/// 启动看门狗：判断「上次启动有没有走到窗口显示」。
/// 为什么需要：曾出现 XAML 加载失败导致启动即崩（0xc000027b），用户看到的是「双击没反应」，
/// 日志里也未必留痕，只能靠猜。这里用 marker 文件把「启动开始」和「启动完成」分开记：
///   · 启动时若发现上次的 marker 还在 ⇒ 上次没走完，失败计数 +1；
///   · 连续失败达到阈值 ⇒ 进入安全模式（关 TUN / 多节点 / 固定出口），先保证能起来；
///   · 启动完成（窗口已显示）时 Clear()：删 marker、清零计数。
/// 安全模式只改这三项，其他设置原样保留，用户确认后可在设置里重新打开。
public static class StartupGuard
{
    private const int FailuresBeforeSafeMode = 2;

    private static string MarkerPath => Path.Combine(NeoPaths.DataDir, "startup.marker");
    private static string FailurePath => Path.Combine(NeoPaths.DataDir, "startup-failures.json");

    public sealed class Result
    {
        public bool PreviousLaunchFailed { get; init; }
        public int ConsecutiveFailures { get; init; }
        public bool SafeMode { get; init; }
        public string MarkerDetail { get; init; } = "";
    }

    /// 在加载配置之后、建窗口之前调用。
    public static Result Begin()
    {
        try
        {
            bool prevFailed = File.Exists(MarkerPath);
            var detail = "";
            if (prevFailed)
            {
                try { detail = File.ReadAllText(MarkerPath).Trim(); } catch { }
            }

            int failures = prevFailed ? LoadFailures() + 1 : 0;
            if (prevFailed) SaveFailures(failures);

            try
            {
                File.WriteAllText(MarkerPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " pid=" + Environment.ProcessId);
            }
            catch { }

            return new Result
            {
                PreviousLaunchFailed = prevFailed,
                ConsecutiveFailures = failures,
                SafeMode = failures >= FailuresBeforeSafeMode,
                MarkerDetail = detail,
            };
        }
        catch
        {
            return new Result();
        }
    }

    /// 启动成功（窗口已显示）时调用：清 marker 与失败计数。
    public static void Clear()
    {
        try { if (File.Exists(MarkerPath)) File.Delete(MarkerPath); } catch { }
        try { if (File.Exists(FailurePath)) File.Delete(FailurePath); } catch { }
    }

    private static int LoadFailures()
    {
        try
        {
            if (!File.Exists(FailurePath)) return 0;
            using var doc = JsonDocument.Parse(File.ReadAllText(FailurePath));
            return doc.RootElement.TryGetProperty("Failures", out var f) && f.TryGetInt32(out var n) ? n : 0;
        }
        catch { return 0; }
    }

    private static void SaveFailures(int n)
    {
        try { File.WriteAllText(FailurePath, "{\"Failures\":" + n + "}"); } catch { }
    }
}
