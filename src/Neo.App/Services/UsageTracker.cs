using System.IO;
using System.Text.Json;
using Neo.Core;

namespace Neo.App.Services;

/// 旧实现只有 Date / Today / Total 三个字段，跨天时 <c>Rollover()</c> 把 _today 直接
/// 清零，**旧那天的数值从未写入任何地方** —— 也就是说过去每天用了多少，程序根本没留。
/// 后果是「本周 / 本月用量」「用量趋势」这类功能**无论如何都做不出来**，不是显示问题。
/// 现在跨天时把旧那天的量归档进 Daily（键为日期），并保留 HISTORY_KEEP_DAYS 天。
///   你此前累计的 138.59 GB 只存在于 Total 这一个总数里，没有按天拆分，
///   所以本周 / 本月从本次改动上线那天才开始累积，第一天必然偏小。
///   这是数据本身的缺失，不是算错。
public static class UsageTracker
{
    private const int HistoryKeepDays = 60;

    private sealed class Data
    {
        public string Date { get; set; } = "";
        public long Today { get; set; }
        public long Total { get; set; }

        /// <summary>每日用量（键 = yyyy-MM-dd，值 = 字节）。旧版 usage.json 没有此字段，反序列化后为 null。</summary>
        public Dictionary<string, long> Daily { get; set; } = new();
    }

    private static readonly object Lock = new();
    private static string _date = "";
    private static long _today;
    private static long _total;
    private static readonly Dictionary<string, long> _daily = new();
    private static long _lastUp = -1;
    private static long _lastDown = -1;
    private static DateTime _lastSave = DateTime.MinValue;

    private static string FilePath => System.IO.Path.Combine(NeoPaths.DataDir, "usage.json");

    private static string LastGoodPath => FilePath + ".lastgood";

    public static long TodayBytes { get { lock (Lock) return _today; } }
    public static long TotalBytes { get { lock (Lock) return _total; } }

    /// <summary>最近 7 天（含今天）的用量。</summary>
    public static long WeekBytes { get { lock (Lock) return SumLastDays(7); } }

    /// <summary>最近 30 天（含今天）的用量。</summary>
    public static long MonthBytes { get { lock (Lock) return SumLastDays(30); } }

    /// 取最近 <paramref name="days"/> 天的每日用量，**按时间正序**返回（最后一项是今天）。
    /// 供趋势图使用：缺失的日期补 0，保证列数与天数严格一致（否则图会错位）。
    public static long[] DailySeries(int days)
    {
        lock (Lock)
        {
            var arr = new long[days];
            var today = DateTime.Now.Date;
            for (int i = 0; i < days; i++)
            {
                var d = today.AddDays(-(days - 1 - i));
                var key = d.ToString("yyyy-MM-dd");
                if (d == today) arr[i] = _today;
                else if (_daily.TryGetValue(key, out var v)) arr[i] = v;
            }
            return arr;
        }
    }

    private static long SumLastDays(int days)
    {
        long sum = 0;
        var today = DateTime.Now.Date;
        for (int i = 0; i < days; i++)
        {
            var d = today.AddDays(-i);
            if (d == today) sum += _today;
            else if (_daily.TryGetValue(d.ToString("yyyy-MM-dd"), out var v)) sum += v;
        }
        return sum;
    }

    public static void Load()
    {
        lock (Lock)
        {
            //   不能像旧实现那样 catch { } 静默清零（用户只会看到用量突然归零、无从追查）。
            var main = TryLoadFrom(FilePath);
            if (!main.Ok)
            {
                var backup = TryLoadFrom(LastGoodPath);
                if (backup.Ok)
                {
                    LogBus.App($"usage.json 读取失败（{(main.Exists ? main.Error : "文件不存在")}）——已从 usage.json.lastgood 备份恢复用量统计", "Error");
                }
                else if (main.Exists || backup.Exists)
                {
                    LogBus.App($"usage.json 读取失败（{(main.Exists ? main.Error : "文件不存在")}）；"
                             + $"usage.json.lastgood 也{(backup.Exists ? "失败（" + backup.Error + "）" : "不存在")}"
                             + "——本次用量统计从 0 开始（历史数据无法恢复）", "Error");
                }
            }
            Rollover();
        }
    }

    ///   Ok=读取成功；Exists=文件是否存在；Error=失败原因（成功或文件不存在时为 null）。
    private static (bool Ok, bool Exists, string? Error) TryLoadFrom(string path)
    {
        try
        {
            if (!File.Exists(path)) return (false, false, null);
            var d = JsonSerializer.Deserialize<Data>(File.ReadAllText(path));
            if (d == null) return (false, true, "反序列化结果为 null");
            _date = d.Date ?? "";
            _today = d.Today;
            _total = d.Total;
            _daily.Clear();
            if (d.Daily != null)
                foreach (var kv in d.Daily) _daily[kv.Key] = kv.Value;
            return (true, true, null);
        }
        catch (Exception ex)
        {
            return (false, true, $"{ex.GetType().Name} {ex.Message}");
        }
    }

    public static void Sample(long upTotal, long downTotal)
    {
        lock (Lock)
        {
            if (_lastUp < 0 || _lastDown < 0 || upTotal < _lastUp || downTotal < _lastDown)
            {
                _lastUp = upTotal;
                _lastDown = downTotal;
                return;
            }

            var delta = (upTotal - _lastUp) + (downTotal - _lastDown);
            _lastUp = upTotal;
            _lastDown = downTotal;
            if (delta <= 0) return;

            Rollover();
            _today += delta;
            _total += delta;
            if ((DateTime.Now - _lastSave).TotalSeconds >= 30) SaveLocked();
        }
    }

    public static void SessionBreak()
    {
        lock (Lock)
        {
            _lastUp = -1;
            _lastDown = -1;
        }
    }

    public static void Flush()
    {
        lock (Lock) SaveLocked();
    }

    /// 跨天处理。**关键改动：归档而不是丢弃。**
    /// 旧实现只做 <c>_date = today; _today = 0;</c> —— 前一天的数值就这样没了，
    /// 而且当时也不会立刻落盘，等于「跨天那一刻的数据只能靠运气」。
    /// 现在先把旧那天的量写进 _daily，再清零，并立即 SaveLocked 落盘。
    private static void Rollover()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (_date == today) return;

        // 归档旧日期（首次运行 _date 为空，此时不归档，避免写入一个空键）
        if (!string.IsNullOrEmpty(_date) && _today > 0)
        {
            // 同一日期可能已存在（多次跨天/重启），取较大值避免清零后又从 0 重记导致倒退
            _daily[_date] = _daily.TryGetValue(_date, out var prev) ? Math.Max(prev, _today) : _today;
        }

        _date = today;
        _today = 0;
        TrimHistory();
        SaveLocked();   // 立即落盘：跨天是低频事件，值得这一次同步写
    }

    /// <summary>裁掉过老的每日记录，避免文件无限增长。</summary>
    private static void TrimHistory()
    {
        try
        {
            if (_daily.Count <= HistoryKeepDays) return;
            var cutoff = DateTime.Now.Date.AddDays(-HistoryKeepDays).ToString("yyyy-MM-dd");
            var stale = _daily.Keys.Where(k => string.CompareOrdinal(k, cutoff) < 0).ToList();
            foreach (var k in stale) _daily.Remove(k);
        }
        catch { }
    }

    private static void SaveLocked()
    {
        try
        {
            _lastSave = DateTime.Now;
            var json = JsonSerializer.Serialize(
                new Data
                {
                    Date = _date,
                    Today = _today,
                    Total = _total,
                    Daily = new Dictionary<string, long>(_daily),
                },
                new JsonSerializerOptions { WriteIndented = true });

            //   原实现 File.WriteAllText 整文件覆盖：写一半被读、或进程在写一半退出，
            //   usage.json 就是半截 JSON → 下次启动解析失败 → 用量归零。
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, true);

            try { File.Copy(FilePath, LastGoodPath, true); } catch { }
        }
        catch (Exception ex)
        {
            LogBus.App($"usage.json 保存失败（{FilePath}）：{ex.GetType().Name} {ex.Message}", "Error");
        }
    }

    public static (string Value, string Unit) Split(long bytes)
    {
        if (bytes < 1024) return (bytes.ToString(), "B");
        if (bytes < 1024 * 1024) return ((bytes / 1024.0).ToString("0.#"), "KB");
        if (bytes < 1024L * 1024 * 1024) return ((bytes / 1024.0 / 1024).ToString("0.##"), "MB");
        return ((bytes / 1024.0 / 1024 / 1024).ToString("0.##"), "GB");
    }

    public static string Format(long bytes)
    {
        var (v, u) = Split(bytes);
        return v + " " + u;
    }
}
