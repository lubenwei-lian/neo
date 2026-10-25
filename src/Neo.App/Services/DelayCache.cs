using System.Collections.Generic;
using System.IO;
using Neo.Core;
using Neo.Core.Models;

namespace Neo.App.Services;

// 延迟数据只存活于本次进程会话：进程退出后自然清空，保证下次启动是干净的。
// 不再做 30 分钟过期，也不落盘；订阅导入/刷新/切换都不得清空。
public static class DelayCache
{
    private static readonly object _lock = new();
    private static readonly Dictionary<string, (int RealMs, int TcpMs)> _map = new();

    private static string FilePath => Path.Combine(NeoPaths.DataDir, "delays.json");

    private static string KeyOf(NodeItem n) => $"{n.Protocol}|{n.Server}|{n.Port}|{n.Name}";

    static DelayCache()
    {
        // 旧版本曾把结果落盘；启动时清掉残留，避免被误当成跨重启缓存。
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }

    public static void Apply(NodeItem n)
    {
        lock (_lock)
        {
            if (!_map.TryGetValue(KeyOf(n), out var entry)) return;
            if (n.RealDelay == 0) n.RealDelay = entry.RealMs;
            if (n.TcpDelay == 0) n.TcpDelay = entry.TcpMs;
        }
    }

    public static void Set(NodeItem n, int ms)
    {
        lock (_lock)
        {
            _map.TryGetValue(KeyOf(n), out var entry);
            _map[KeyOf(n)] = (ms, entry.TcpMs);
        }
    }

    public static void SetTcp(NodeItem n, int ms)
    {
        lock (_lock)
        {
            _map.TryGetValue(KeyOf(n), out var entry);
            _map[KeyOf(n)] = (entry.RealMs, ms);
        }
    }
}
