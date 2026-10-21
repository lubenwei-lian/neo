using System.Collections.Generic;
using System.IO;
using Neo.Core;
using Neo.Core.Models;

namespace Neo.App.Services;

// 延迟数据只存活于本次进程会话：重启后不再显示上次的测速值。
// 旧版本曾把结果落盘到 delays.json（跨重启显示旧延迟），已废弃，进程启动时顺手清理残留文件。
public static class DelayCache
{
    private static readonly object _lock = new();
    private static readonly Dictionary<string, int> _map = new();

    private static string FilePath => Path.Combine(NeoPaths.DataDir, "delays.json");

    private static string KeyOf(NodeItem n) => $"{n.Protocol}|{n.Server}|{n.Port}|{n.Name}";

    static DelayCache()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }

    public static void Apply(NodeItem n)
    {
        if (n.RealDelay != 0) return;
        lock (_lock)
        {
            if (_map.TryGetValue(KeyOf(n), out var ms)) n.RealDelay = ms;
        }
    }

    public static void Set(NodeItem n, int ms)
    {
        lock (_lock) { _map[KeyOf(n)] = ms; }
    }
}
