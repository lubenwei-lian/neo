using System.IO;
using System.Text.Json;
using Neo.Core;
using Neo.Core.Models;

namespace Neo.App.Services;

/// 节点「真实出口」检测结果的持久缓存（国家 + IP）。
/// 目的：
///   · 「真实 IP 显示」打开后不必每次启动都重新测 60+ 个节点（每个节点一次 Cloudflare trace）；
///   · 12 小时内复用结果，既省请求也省启动时间。
/// 只缓存**成功**结果（空国家不写），过期后自动重测。
public static class LocationCache
{
    private sealed class Entry
    {
        public string Country { get; set; } = "";
        public string Ip { get; set; } = "";
        public long AtUnix { get; set; }
    }

    private static readonly TimeSpan Ttl = TimeSpan.FromHours(12);
    private static readonly object Lock = new();
    private static Dictionary<string, Entry>? _map;

    private static string FilePath => Path.Combine(NeoPaths.DataDir, "location-cache.json");

    private static void EnsureLoaded()
    {
        if (_map != null) return;
        lock (Lock)
        {
            if (_map != null) return;
            _map = new Dictionary<string, Entry>();
            try
            {
                if (!File.Exists(FilePath)) return;
                var data = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath));
                if (data == null) return;
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                foreach (var kv in data)
                {
                    if (string.IsNullOrEmpty(kv.Value?.Country)) continue;
                    if (now - kv.Value.AtUnix > (long)Ttl.TotalSeconds) continue;
                    _map[kv.Key] = kv.Value;
                }
            }
            catch { }
        }
    }

    /// 有新鲜结果则返回 true（国家可能为空但 IP 非空的情况不缓存，见 Set）。
    public static bool TryGet(NodeItem n, out string country, out string ip)
    {
        country = ""; ip = "";
        EnsureLoaded();
        var key = AppState.NodeKey(n);
        lock (Lock)
        {
            if (!_map!.TryGetValue(key, out var e)) return false;
            country = e.Country;
            ip = e.Ip;
            return true;
        }
    }

    /// 把新鲜结果写回节点对象（订阅刷新重建节点时调用）。
    public static void Apply(NodeItem n)
    {
        if (!string.IsNullOrEmpty(n.ExitCountry)) return;
        if (TryGet(n, out var country, out var ip))
        {
            n.ExitCountry = country;
            n.ExitIp = ip;
        }
    }

    public static void Set(NodeItem n, string country, string ip)
    {
        if (string.IsNullOrEmpty(country)) return;   // 没测到不缓存，下次自然会重测
        EnsureLoaded();
        var key = AppState.NodeKey(n);
        lock (Lock)
        {
            _map![key] = new Entry
            {
                Country = country,
                Ip = ip,
                AtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
        }
        Save();
    }

    private static void Save()
    {
        try
        {
            lock (Lock)
            {
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_map));
                File.Move(tmp, FilePath, overwrite: true);
            }
        }
        catch { }
    }
}
