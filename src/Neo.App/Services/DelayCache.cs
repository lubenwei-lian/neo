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
    // 2026-10-24 审查 P2-1：缓存加 TTL。之前只按「本次进程会话」存，但用户可能几小时前测的
    //   延迟一直显示成当前值（节点已变慢/换 IP 也不刷新）→ 30 分钟后不再回填，显示为「未测速」。
    private static readonly Dictionary<string, (int Ms, DateTime At)> _map = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

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
            if (_map.TryGetValue(KeyOf(n), out var e) && DateTime.Now - e.At <= Ttl) n.RealDelay = e.Ms;
        }
    }

    public static void Set(NodeItem n, int ms)
    {
        lock (_lock) { _map[KeyOf(n)] = (ms, DateTime.Now); }
    }
}
