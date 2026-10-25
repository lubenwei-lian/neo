using System.IO;
using System.Text;
using System.Text.Json;
using Neo.Core;

namespace Neo.App.Services;

/// <summary>一次订阅拉取的完整记录：开始时间、结果、HTTP 状态、节点数、错误与耗时。</summary>
public sealed class SubscriptionLogEntry
{
    public string Id { get; set; } = "";
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string Subscription { get; set; } = "";
    public string Host { get; set; } = "";
    public string Trigger { get; set; } = "";
    public string Result { get; set; } = "进行中";
    public string HttpStatus { get; set; } = "—";
    public int? NodeCount { get; set; }
    public string? Error { get; set; }
    public string? ParseError { get; set; }
    public string Placeholder { get; set; } = "未判定";
    public double? ElapsedMs { get; set; }

    public bool Running => EndTime == null;
}

/// <summary>
/// 订阅拉取链路日志：独立于运行日志，按天写入 data\logs\subscription-yyyyMMdd.log，
/// 每行一条 JSON（begin / end 两条记录按 id 合并），供「订阅日志」页筛选、复制、导出。
/// </summary>
public static class SubscriptionLogService
{
    private static readonly object Gate = new();
    private static readonly List<SubscriptionLogEntry> Entries = new();
    private static string _loadedDate = "";
    private const int MaxEntries = 2000;
    /// <summary>订阅日志保留天数：跟随「设置→通用→日志保留」（7/15/30），默认 7。</summary>
    private static int KeepFileDays
    {
        get
        {
            try
            {
                var d = AppState.State.LogRetentionDays;
                return d is 7 or 15 or 30 ? d : 7;
            }
            catch { return 7; }
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static event Action? Changed;

    public static string TodayFileName => $"subscription-{DateTime.Now:yyyyMMdd}.log";

    public static string FilePathFor(DateTime day)
        => Path.Combine(NeoPaths.LogDir, $"subscription-{day:yyyyMMdd}.log");

    public static IReadOnlyList<SubscriptionLogEntry> Snapshot()
    {
        lock (Gate) return Entries.Select(Clone).ToList();
    }

    /// <summary>由 URL 或本地文件路径取主机名（本地文件显示为「本地文件」）。</summary>
    public static string HostOf(string urlOrPath)
    {
        try
        {
            if (Uri.TryCreate(urlOrPath, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                return uri.Host;
        }
        catch { }
        return "本地文件";
    }

    public static string Begin(string subscription, string host, string trigger)
    {
        var entry = new SubscriptionLogEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            StartTime = DateTime.Now,
            Subscription = subscription,
            Host = host,
            Trigger = trigger,
            Result = "进行中",
        };
        lock (Gate)
        {
            Entries.Add(entry);
            TrimEntries();
            AppendLocked(new
            {
                ev = "b",
                id = entry.Id,
                at = entry.StartTime.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                sub = entry.Subscription,
                host = entry.Host,
                trigger = entry.Trigger,
            }, entry.StartTime);
        }
        RaiseChanged();
        return entry.Id;
    }

    public static void Finish(
        string id,
        string result,
        string httpStatus,
        int nodeCount,
        string? error,
        string? parseError,
        string placeholder,
        double elapsedMs)
    {
        SubscriptionLogEntry? entry;
        lock (Gate)
        {
            entry = Entries.FirstOrDefault(e => e.Id == id);
            if (entry == null) return;
            entry.EndTime = DateTime.Now;
            entry.Result = result;
            entry.HttpStatus = string.IsNullOrWhiteSpace(httpStatus) ? "—" : httpStatus;
            entry.NodeCount = nodeCount;
            entry.Error = error;
            entry.ParseError = parseError;
            entry.Placeholder = placeholder;
            entry.ElapsedMs = elapsedMs;
            AppendLocked(new
            {
                ev = "e",
                id = entry.Id,
                at = entry.EndTime.Value.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                sub = entry.Subscription,
                host = entry.Host,
                result = entry.Result,
                http = entry.HttpStatus,
                nodes = entry.NodeCount ?? 0,
                err = entry.Error,
                perr = entry.ParseError,
                ph = entry.Placeholder,
                ms = Math.Round(elapsedMs),
            }, entry.EndTime.Value);
        }
        RaiseChanged();
    }

    /// <summary>加载当天文件（进程内只加载一次/天），并清理过旧的分天文件。</summary>
    public static void EnsureTodayLoaded()
    {
        var today = DateTime.Now.ToString("yyyyMMdd");
        lock (Gate)
        {
            if (_loadedDate == today) return;
            _loadedDate = today;

            var path = FilePathFor(DateTime.Now);
            try
            {
                if (File.Exists(path))
                {
                    foreach (var line in File.ReadAllLines(path))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try { MergeLineLocked(line); } catch { }
                    }
                    Entries.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
                    TrimEntries();
                }
            }
            catch { }

            PruneOldFilesLocked();
        }
        RaiseChanged();
    }

    public static void Clear()
    {
        lock (Gate)
        {
            Entries.Clear();
            try
            {
                Directory.CreateDirectory(NeoPaths.LogDir);
                File.WriteAllText(FilePathFor(DateTime.Now), "");
            }
            catch { }
        }
        RaiseChanged();
    }

    public static string FormatText(IEnumerable<SubscriptionLogEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var e in entries.OrderByDescending(x => x.StartTime))
        {
            sb.Append(e.StartTime.ToString("yyyy-MM-dd HH:mm:ss"))
              .Append("  ").Append(e.Result.PadRight(3))
              .Append("  ").Append(e.Subscription)
              .Append("  ").Append(e.Host)
              .Append("  HTTP ").Append(e.HttpStatus)
              .Append("  ").Append(e.NodeCount is { } n ? n + " 节点" : "—")
              .Append("  ").Append(e.ElapsedMs is { } ms ? (ms / 1000.0).ToString("0.0") + "s" : "—")
              .Append("  占位=").Append(e.Placeholder)
              .Append("  触发=").Append(e.Trigger)
              .AppendLine();
            if (!string.IsNullOrWhiteSpace(e.Error))
                sb.Append("    错误：").AppendLine(e.Error);
            if (!string.IsNullOrWhiteSpace(e.ParseError))
                sb.Append("    解析失败：").AppendLine(e.ParseError);
        }
        return sb.ToString();
    }

    public static string FormatCsv(IEnumerable<SubscriptionLogEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("开始时间,结束时间,订阅,域名,触发方式,结果,HTTP状态,解析节点数,耗时毫秒,占位判定,错误详情,解析失败原因");
        foreach (var e in entries.OrderByDescending(x => x.StartTime))
        {
            sb.Append(Csv(e.StartTime.ToString("yyyy-MM-dd HH:mm:ss"))).Append(',')
              .Append(Csv(e.EndTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "")).Append(',')
              .Append(Csv(e.Subscription)).Append(',')
              .Append(Csv(e.Host)).Append(',')
              .Append(Csv(e.Trigger)).Append(',')
              .Append(Csv(e.Result)).Append(',')
              .Append(Csv(e.HttpStatus)).Append(',')
              .Append(e.NodeCount?.ToString() ?? "").Append(',')
              .Append(e.ElapsedMs is { } ms ? Math.Round(ms).ToString() : "").Append(',')
              .Append(Csv(e.Placeholder)).Append(',')
              .Append(Csv(e.Error ?? "")).Append(',')
              .Append(Csv(e.ParseError ?? ""))
              .AppendLine();
        }
        return sb.ToString();
    }

    private static string Csv(string v)
    {
        if (v.Length == 0) return "";
        return v.Contains(',') || v.Contains('"') || v.Contains('\n') || v.Contains('\r')
            ? "\"" + v.Replace("\"", "\"\"") + "\""
            : v;
    }

    private static void AppendLocked(object record, DateTime day)
    {
        try
        {
            Directory.CreateDirectory(NeoPaths.LogDir);
            var line = JsonSerializer.Serialize(record, JsonOpts);
            File.AppendAllText(FilePathFor(day), line + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
    }

    private static void MergeLineLocked(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (!root.TryGetProperty("ev", out var ev)) return;
        if (!root.TryGetProperty("id", out var idEl)) return;
        var id = idEl.GetString() ?? "";
        if (id.Length == 0) return;
        var kind = ev.GetString();

        if (kind == "b")
        {
            if (Entries.Any(e => e.Id == id)) return;
            Entries.Add(new SubscriptionLogEntry
            {
                Id = id,
                StartTime = ParseTime(root, "at"),
                Subscription = Str(root, "sub"),
                Host = Str(root, "host"),
                Trigger = Str(root, "trigger"),
                Result = "进行中",
            });
            return;
        }

        if (kind != "e") return;
        var target = Entries.FirstOrDefault(e => e.Id == id);
        if (target == null)
        {
            target = new SubscriptionLogEntry
            {
                Id = id,
                StartTime = ParseTime(root, "at"),
                Subscription = Str(root, "sub"),
                Host = Str(root, "host"),
            };
            Entries.Add(target);
        }
        target.EndTime = ParseTime(root, "at");
        target.Result = Str(root, "result", "失败");
        target.HttpStatus = Str(root, "http", "—");
        target.NodeCount = root.TryGetProperty("nodes", out var nodes) && nodes.TryGetInt32(out var n) ? n : null;
        target.Error = StrOrNull(root, "err");
        target.ParseError = StrOrNull(root, "perr");
        target.Placeholder = Str(root, "ph", "未判定");
        target.ElapsedMs = root.TryGetProperty("ms", out var ms) && ms.TryGetDouble(out var d) ? d : null;
    }

    private static DateTime ParseTime(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && DateTime.TryParse(v.GetString(), out var t) ? t : DateTime.Now;

    private static string Str(JsonElement root, string name, string fallback = "")
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? fallback) : fallback;

    private static string? StrOrNull(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static void TrimEntries()
    {
        if (Entries.Count <= MaxEntries) return;
        Entries.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
        Entries.RemoveRange(0, Entries.Count - MaxEntries);
    }

    private static void PruneOldFilesLocked()
    {
        try
        {
            var cutoff = DateTime.Today.AddDays(-KeepFileDays);
            foreach (var f in Directory.GetFiles(NeoPaths.LogDir, "subscription-*.log"))
            {
                try { if (File.GetLastWriteTime(f) < cutoff) File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    private static SubscriptionLogEntry Clone(SubscriptionLogEntry e) => new()
    {
        Id = e.Id,
        StartTime = e.StartTime,
        EndTime = e.EndTime,
        Subscription = e.Subscription,
        Host = e.Host,
        Trigger = e.Trigger,
        Result = e.Result,
        HttpStatus = e.HttpStatus,
        NodeCount = e.NodeCount,
        Error = e.Error,
        ParseError = e.ParseError,
        Placeholder = e.Placeholder,
        ElapsedMs = e.ElapsedMs,
    };

    private static void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch { }
    }
}
