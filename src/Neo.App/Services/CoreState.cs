using System.IO;
using System.Text.Json;
using Neo.Core;

namespace Neo.App.Services;

/// <summary>
/// 内核运行状态快照 + 命名事件。
///
/// 为什么需要它：看门狗是独立进程，无法读主程序内存。主程序在每次内核状态变化时
/// 原子写 data\core-state.json 并 Set 一个命名事件，看门狗据此「事件驱动」地判断
/// 当前内核是「正在切换/故障转移」还是「真的死了」，不需要固定轮询间隔。
/// </summary>
public static class CoreState
{
    public const string EventName = "neo-core-state-4f8a2c";

    public sealed class Snapshot
    {
        public int Version { get; set; } = 1;
        public int NeoPid { get; set; }
        public string State { get; set; } = "idle";
        public int CorePid { get; set; }
        public string CoreKind { get; set; } = "";
        public string UpdatedUtc { get; set; } = "";
        public string? Reason { get; set; }
    }

    private static readonly object Gate = new();
    private static EventWaitHandle? _event;

    public static string FilePath => Path.Combine(NeoPaths.DataDir, "core-state.json");

    /// <summary>写状态并唤醒看门狗。失败不影响主流程（看门狗读不到状态只会更保守）。</summary>
    public static void Report(string state, int corePid = 0, string coreKind = "", string? reason = null)
    {
        try
        {
            var snap = new Snapshot
            {
                NeoPid = Environment.ProcessId,
                State = state,
                CorePid = corePid > 0 ? corePid : 0,
                CoreKind = coreKind ?? "",
                UpdatedUtc = DateTime.UtcNow.ToString("o"),
                Reason = reason,
            };
            var json = JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true });
            lock (Gate)
            {
                Directory.CreateDirectory(NeoPaths.DataDir);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, FilePath, true);
            }
            Signal();
        }
        catch { }
    }

    private static void Signal()
    {
        try
        {
            _event ??= new EventWaitHandle(false, EventResetMode.ManualReset, EventName);
            _event.Set();
        }
        catch { }
    }

    public static Snapshot? Read()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path));
        }
        catch { return null; }
    }

    public static void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }
}
