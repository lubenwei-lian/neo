using System.Text.Json;
using Neo.Core;

namespace Neo.App.Services;

/// <summary>
/// 内核更新包 SHA-256 的本地缓存（data\kernel-hashes.json）。
/// 为什么需要：
///   · GitHub 的官方哈希只暴露在 REST API 的 asset.digest 字段；匿名配额 60 次/小时，
///     被限流后就拿不到官方值（sing-box / mihomo 也没有 .sha256 资产可兜底）。
///   · 一旦某次成功拿到（API digest 或 xray 的 .dgst），就按「仓库@tag/资产名」存下来；
///     下次检查/安装（哪怕 API 被限流）仍能用**官方值**校验同一个包，而不是退化成「无哈希」。
/// 只缓存哈希值，不缓存下载内容；文件很小（每条 ~100B）。
/// </summary>
public static class KernelHashCache
{
    private static readonly object Gate = new();
    private static Dictionary<string, string>? _map;

    private static string FilePath => Path.Combine(NeoPaths.DataDir, "kernel-hashes.json");

    private static Dictionary<string, string> Load()
    {
        if (_map != null) return _map;
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                _map = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                       ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                _map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { _map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
        return _map;
    }

    public static string? TryGet(string owner, string repo, string tag, string asset)
    {
        try
        {
            lock (Gate)
            {
                return Load().TryGetValue(Key(owner, repo, tag, asset), out var sha) ? sha : null;
            }
        }
        catch { return null; }
    }

    public static void Put(string owner, string repo, string tag, string asset, string? sha)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sha) || sha.Length != 64) return;
            lock (Gate)
            {
                var map = Load();
                map[Key(owner, repo, tag, asset)] = sha.Trim().ToLowerInvariant();
                // 只保留最近 200 条（一条 ~100B，200 条 = 20KB），避免无限增长。
                if (map.Count > 200)
                {
                    foreach (var k in map.Keys.Take(map.Count - 200).ToList()) map.Remove(k);
                }
                File.WriteAllText(FilePath, JsonSerializer.Serialize(map));
            }
        }
        catch { }
    }

    private static string Key(string owner, string repo, string tag, string asset)
        => $"{owner}/{repo}@{tag}/{asset}".ToLowerInvariant();
}
