using System.IO;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Neo.Core;
using Windows.UI;

namespace Neo.App.Services;

public sealed class LogEntry
{
    public DateTime Time { get; init; }
    public string Level { get; init; } = "Info";
    public string Text { get; init; } = "";

    public string Display => $"{Time:yyyy-MM-dd HH:mm:ss.fff}-{TagOf(Level)} {Text}";

    public Brush LevelBrush => LogBus.BrushFor(Level);

    private static string TagOf(string level) => level switch
    {
        "Error" => "ERROR",
        "Warning" => "WARN",
        "Debug" => "DEBUG",
        _ => "INFO",
    };
}

public static class LogBus
{
    private const int MaxLines = 4000;
    // 2026-10-23 用户要求：日志只保留 7 天。
    private const int KeepDays = 7;

    /// 单文件过大时记事本/杀软读取会卡死，且写坏一次就丢掉整天日志。
    private const long MaxFileBytes = 32L * 1024 * 1024;

    private const long MaxDirBytes = 512L * 1024 * 1024;

    private static readonly object Gate = new();

    /// 与 <see cref="Gate"/>（内存列表）分开：文件 IO 慢，不能拖住 Snapshot() 读取。
    private static readonly object FileGate = new();
    private static readonly List<LogEntry> _lines = new();
    private static bool _cleaned;

    public static event Action<LogEntry>? Appended;

    public static IReadOnlyList<LogEntry> Snapshot()
    {
        lock (Gate) return _lines.ToArray();
    }

    public static void Clear()
    {
        lock (Gate) _lines.Clear();
    }

    /// 内核重复警告去重：xray 每次启动都会刷 20~30 条完全相同的
    /// "The feature WebSocket transport ... is deprecated"，
    /// 落盘后把真正有用的行淹没。这里把「去掉时间戳/请求 id 后相同」的行折叠：
    /// 只保留首条，之后在第 2/5/10/50/100/… 次时写一条「（已重复 N 次）」。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _coreSeen = new();
    private static readonly HashSet<int> _repeatMilestones = new() { 2, 5, 10, 50, 100, 500, 1000, 5000, 10000 };

    private static string CoreKey(string raw)
    {
        var t = System.Text.RegularExpressions.Regex.Replace(
            raw, @"^\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2}(\.\d+)?\s*", "");
        // 去掉 [123456789] 这类每次请求都不同的 id，否则同一句告警无法判重
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\[\d+\]", "[#]");
        return t.Trim();
    }

    public static void Core(string raw)
    {
        var s = raw.ToLowerInvariant();
        //   sing-box 崩溃时打的是 "FATAL[0000] ..."，会被判成 Info 并被 warning 级门控吞掉 ——
        //   现在 FATAL / PANIC 一律按 Error（Error 级别绕过节流，永远落盘），
        //   sing-box 的 ERRO[ 也归到 Error。
        var level = s.Contains("[error]") || s.Contains("fatal") || s.Contains("fata[") || s.Contains("panic") || s.Contains("erro[")
            ? "Error"
            : s.Contains("[warning]") || s.Contains("[warn]") || s.Contains("warn[") ? "Warning"
            : s.Contains("[debug]") || s.Contains("debu[") ? "Debug"
            : "Info";
        var key = CoreKey(raw);
        var count = _coreSeen.AddOrUpdate(key, 1, (_, v) => v + 1);
        if (_coreSeen.Count > 5000) _coreSeen.Clear();   // 兜底，避免极端情况下无限增长
        if (count > 1 && !_repeatMilestones.Contains(count)) return;
        Add(new LogEntry
        {
            Time = DateTime.Now,
            Level = level,
            Text = "[内核] " + raw + (count > 1 ? $"（同一告警已重复 {count} 次）" : ""),
        }, fromCore: true);
    }

    public static void App(string text, string level = "Info")
        => Add(new LogEntry { Time = DateTime.Now, Level = level, Text = text }, fromCore: false);

    public static void Exception(string context, Exception ex)
        => App($"{context}：{Describe(ex)}{Environment.NewLine}{ex.StackTrace}", "Error");

    /// 描述一个异常，**展开 AggregateException 的内层**。
    /// 为什么必须展开：`TaskScheduler.UnobservedTaskException` 传进来的永远是
    /// `AggregateException`，而它的 `Message` 只是一句固定模板文案
    ///（"A Task's exception(s) were not observed either by Waiting on the Task..."），
    /// **真正的原因藏在 `InnerExceptions` 里**。原实现只记 `ex.Message`，
    /// 于是日志里长期只有那句无用的模板 —— 那意味着一个**真实的 bug 被掩盖了**，
    /// 而不是「一个无害的既有问题」。这里把内层逐条摊开，让日志真正可用。
    private static string Describe(Exception ex)
    {
        if (ex is AggregateException agg)
        {
            var inner = agg.Flatten().InnerExceptions;
            if (inner.Count == 0) return $"{agg.GetType().Name}（无内层异常）";
            var parts = inner.Select((x, i) =>
            {
                var head = $"#{i + 1} [{x.GetType().Name}] {x.Message}";
                // 再往里还有 InnerException 的话，一并带上（常见于 HttpRequestException 包 IOException）
                return x.InnerException is { } deep
                    ? $"{head} ← [{deep.GetType().Name}] {deep.Message}"
                    : head;
            });
            return $"{agg.GetType().Name}（含 {inner.Count} 个内层异常）{Environment.NewLine}   "
                   + string.Join(Environment.NewLine + "   ", parts);
        }

        return ex.InnerException is { } ie
            ? $"{ex.GetType().Name} {ex.Message} ← [{ie.GetType().Name}] {ie.Message}"
            : $"{ex.GetType().Name} {ex.Message}";
    }

    private static int Rank(string level) => level switch
    {
        "Error" => 3,
        "Warning" => 2,
        "Debug" => 0,
        _ => 1,
    };

    private static bool Enabled(string level, bool fromCore)
    {
        // A方案：Error 级别始终记录，绕过级别门控；否则 LogLevel=none 时连崩溃/异常日志都会被吞。
        if (level == "Error") return true;

        // 其中 99.999% 是内核行）。neo 自身的日志量极小（约 10 行/天）却是排查问题的主线索，
        // 日志安静就再次无法区分「正常」和「根本没跑」——那正是之前故障转移 bug 藏了 9 天的原因。
        if (!fromCore) return true;

        string setting;
        try { setting = AppState.State.LogLevel ?? "warning"; }
        catch { setting = "warning"; }
        var min = setting switch
        {
            "none" => 99,
            "error" => 3,
            "warning" => 2,
            "info" => 1,
            _ => 0,
        };
        return Rank(level) >= min;
    }

    private static void Add(LogEntry e, bool fromCore)
    {
        if (!Enabled(e.Level, fromCore)) return;

        lock (Gate)
        {
            _lines.Add(e);
            if (_lines.Count > MaxLines)
                _lines.RemoveRange(0, _lines.Count - MaxLines);
        }

        //   在轮转/删除交界处会互相踩（IOException 被 catch 吞掉 → 丢日志行）。
        lock (FileGate)
        {
            try
            {
                if (!_cleaned)
                {
                    _cleaned = true;
                    CleanupOldLogs();
                }

                var file = Path.Combine(NeoPaths.LogDir, $"neo-{DateTime.Now:yyyyMMdd}.log");
                //   再继续写新文件。
                RotateIfTooLarge(file);
                var body = e.Display.Replace(Environment.NewLine, Environment.NewLine + "   ");
                File.AppendAllText(file, body + Environment.NewLine);
            }
            catch { }
        }

        Appended?.Invoke(e);
    }

    ///   · 删除超过 30 天的所有 neo-*.log（含轮转出来的 `.1.log` —— 旧实现只认
    ///     `neo-yyyyMMdd.log` 这一种名字，轮转文件永远不会被清）；
    ///   · 目录总量超过 512 MB 时按文件名（= 日期）从最旧开始删，直到降回阈值以下。
    /// 全部 try/catch：清理失败不能影响「记日志」本身。
    private static void CleanupOldLogs()
    {
        try
        {
            var dir = NeoPaths.LogDir;
            foreach (var f in Directory.GetFiles(dir, "neo-*.log"))
            {
                if (!TryParseLogDate(f, out var d)) continue;
                // 2026-10-24 审查 P2-14：当天文件永远不删（长跑进程跨过 7 天后，当前文件也会
                //   「超龄」；原实现靠 Windows 文件锁让它删除失败，属于侥幸而非设计）。
                if (d.Date >= DateTime.Today) continue;
                if ((DateTime.Now - d).TotalDays > KeepDays)
                    File.Delete(f);
            }
        }
        catch { }
        EnforceDirQuota();
    }

    /// <summary>从日志文件名抽日期：neo-20261021.log / neo-20261021.1.log 都认。</summary>
    private static bool TryParseLogDate(string file, out DateTime date)
    {
        date = default;
        try
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!name.StartsWith("neo-", StringComparison.OrdinalIgnoreCase)) return false;
            var rest = name[4..];
            if (rest.Length < 8) return false;
            return DateTime.TryParseExact(rest[..8], "yyyyMMdd", null,
                System.Globalization.DateTimeStyles.None, out date);
        }
        catch { return false; }
    }

    private static void RotateIfTooLarge(string file)
    {
        try
        {
            var fi = new FileInfo(file);
            if (!fi.Exists || fi.Length <= MaxFileBytes) return;
            var dir = Path.GetDirectoryName(file);
            if (string.IsNullOrEmpty(dir)) return;
            var rotated = Path.Combine(dir, Path.GetFileNameWithoutExtension(file) + ".1.log");
            if (File.Exists(rotated)) File.Delete(rotated);
            File.Move(file, rotated, true);
            // 每次轮转（约每 32 MB）顺带查一次目录总量：长跑进程不会重启，
            // 只在启动时清理的话 512 MB 上限永远轮不到。
            EnforceDirQuota();
        }
        catch { }
    }

    /// 排序用文件名（neo-yyyyMMdd[.1].log 天然按日期有序；同一天里 `.1` 比主文件旧）。
    private static void EnforceDirQuota()
    {
        try
        {
            var dir = NeoPaths.LogDir;
            var todayFile = $"neo-{DateTime.Now:yyyyMMdd}.log";
            var files = new List<(FileInfo Fi, long Size)>();
            long total = 0;
            foreach (var f in Directory.GetFiles(dir, "neo-*.log"))
            {
                try
                {
                    var fi = new FileInfo(f);
                    if (!fi.Exists) continue;
                    // 2026-10-24 审查 P2-14：当天的活动日志受保护，配额只从历史文件里回收空间。
                    if (string.Equals(fi.Name, todayFile, StringComparison.OrdinalIgnoreCase)) continue;
                    files.Add((fi, fi.Length));
                    total += fi.Length;
                }
                catch { }
            }
            if (total <= MaxDirBytes) return;

            foreach (var (fi, size) in files.OrderBy(x => x.Fi.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (total <= MaxDirBytes) break;
                try
                {
                    fi.Delete();
                    total -= size;
                }
                catch { }
            }
        }
        catch { }
    }

    public static Brush BrushFor(string level)
    {
        string key = level switch
        {
            "Error" => "NeoRed",
            "Warning" => "NeoYellow",
            "Debug" => "NeoText3",
            _ => "NeoText2",
        };
        try
        {
            if (Application.Current.Resources.TryGetValue(key, out var v) && v is Brush b)
                return b;
        }
        catch { }
        return level switch
        {
            "Error" => new SolidColorBrush(Color.FromArgb(255, 0xE5, 0x48, 0x4D)),
            "Warning" => new SolidColorBrush(Color.FromArgb(255, 0xD9, 0x77, 0x06)),
            "Debug" => new SolidColorBrush(Color.FromArgb(255, 0x8B, 0x93, 0xA0)),
            _ => new SolidColorBrush(Color.FromArgb(255, 0x6B, 0x72, 0x80)),
        };
    }
}
