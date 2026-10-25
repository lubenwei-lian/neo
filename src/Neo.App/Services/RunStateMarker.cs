using System.IO;
using Neo.Core;

namespace Neo.App.Services;

/// <summary>
/// 运行标记：进程启动后写 data\running.marker，正常退出时删除。
/// 下次启动若发现标记还在，说明上次没走有序收尾（任务管理器强杀 / 崩溃 / 断电），
/// 据此在日志里明确告警，并配合窗口就绪后的残留清理
///（系统代理快照、孤儿内核、测速临时文件），让「上次异常退出」从只能靠猜变成有记录。
/// </summary>
public static class RunStateMarker
{
    private static string FilePath => Path.Combine(NeoPaths.DataDir, "running.marker");

    public static bool Exists
    {
        get
        {
            try { return File.Exists(FilePath); } catch { return false; }
        }
    }

    public static void MarkRunning()
    {
        try
        {
            Directory.CreateDirectory(NeoPaths.DataDir);
            File.WriteAllText(FilePath,
                "pid=" + Environment.ProcessId + Environment.NewLine +
                "started=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine);
        }
        catch { }
    }

    public static void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }
}
