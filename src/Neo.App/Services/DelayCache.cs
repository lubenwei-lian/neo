using System.Collections.Generic;
using System.IO;
using Neo.Core;
using Neo.Core.Models;
using Neo.Core.Core;

namespace Neo.App.Services;

// 延迟数据只存活于本次进程会话：进程退出后自然清空，保证下次启动是干净的。
// 不再做 30 分钟过期，也不落盘；订阅导入/刷新/切换都不得清空。
//
// 缓存按「方案 + 证书策略」分键：
//   · tcp            —— TCP 握手结果，与真连互不覆盖；
//   · real|strict    —— 严格证书校验的真连结果；
//   · real|relax     —— 放宽证书校验后的真连结果（会带 note，例如「证书放宽」）。
// Apply 时优先严格结果，没有再退回放宽结果，并恢复对应的 note。
public static class DelayCache
{
    private static readonly object _lock = new();
    private static readonly Dictionary<string, (int Ms, string? Note, DateTime At)> _real = new();
    private static readonly Dictionary<string, int> _tcp = new();

    private static string FilePath => Path.Combine(NeoPaths.DataDir, "delays.json");

    private static string KeyOf(NodeItem n) => NodeIdentity.Fingerprint(n);

    static DelayCache()
    {
        // 旧版本曾把结果落盘；启动时清掉残留，避免被误当成跨重启缓存。
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }

    public static void Apply(NodeItem n)
    {
        lock (_lock)
        {
            var key = KeyOf(n);
            if (n.TcpDelay == 0 && _tcp.TryGetValue(key, out var tcp)) n.TcpDelay = tcp;
            if (n.RealDelay != 0) return;
            if (_real.TryGetValue(key + "|strict", out var strict))
            {
                n.RealDelay = strict.Ms;
                n.RealDelayNote = strict.Note;
            }
            else if (_real.TryGetValue(key + "|relax", out var relax))
            {
                n.RealDelay = relax.Ms;
                n.RealDelayNote = relax.Note;
            }
        }
    }

    public static void Set(NodeItem n, int ms, string? note = null)
    {
        lock (_lock)
        {
            var policy = note != null && note.Contains("证书放宽") ? "relax" : "strict";
            var key = KeyOf(n);
            _real[key + "|" + policy] = (ms, note, DateTime.UtcNow);
            // 放宽证书的结果不参与严格连接候选的性能排序。
            if (policy == "strict") NodeQualityHistory.Shared.RecordLatency(key, ms);
        }
    }

    public static bool IsFresh(NodeItem n, TimeSpan maximumAge)
    {
        lock (_lock)
            return _real.TryGetValue(KeyOf(n) + "|strict", out var sample)
                && sample.Ms > 0 && DateTime.UtcNow - sample.At <= maximumAge;
    }

    public static void SetTcp(NodeItem n, int ms)
    {
        lock (_lock) { _tcp[KeyOf(n)] = ms; }
    }
}
