using System.IO;
using System.Text;
using System.Text.Json;
using Neo.Core;

namespace Neo.App.Services;

/// 事件自记录：把「连接/断开/内核退出/故障转移/自动修复/兜底/安全模式」等关键动作，
/// 以一行一条 JSON 写进 data\\incidents.jsonl，并在导出诊断时生成人话版问题报告。
/// 目的：出问题时用户不用自己翻技术日志 —— 软件自己把「发生了什么、做了什么」记下来。
public static class IncidentLog
{
    private const long MaxBytes = 256 * 1024;
    private const int KeepLines = 1200;
    private static readonly object Gate = new();

    private static string FilePath => Path.Combine(NeoPaths.DataDir, "incidents.jsonl");

    /// 不转义中文：incidents.jsonl 会被直接打包进诊断包，用户/客服打开要能看懂。
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Record(string kind, string summary, string? detail = null)
    {
        try
        {
            var obj = new
            {
                ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                kind,
                summary = Clamp(summary, 300),
                detail = Clamp(detail ?? "", 1200),
            };
            var line = JsonSerializer.Serialize(obj, JsonOpts);
            lock (Gate)
            {
                Directory.CreateDirectory(NeoPaths.DataDir);
                File.AppendAllText(FilePath, line + Environment.NewLine);
                TrimIfNeeded();
            }
        }
        catch { }
    }

    private static void TrimIfNeeded()
    {
        try
        {
            var fi = new FileInfo(FilePath);
            if (fi.Length <= MaxBytes) return;
            var lines = File.ReadAllLines(FilePath);
            File.WriteAllLines(FilePath, lines.Skip(Math.Max(0, lines.Length - KeepLines)));
        }
        catch { }
    }

    /// 最近 window 内的事件条数（用于界面/报告）。
    public static int CountRecent(TimeSpan window)
    {
        try { return ReadSince(DateTime.Now - window).Count; } catch { return 0; }
    }

    /// 生成人话版问题报告（导出诊断包用）。
    public static string BuildReport(int days = 3)
    {
        var sb = new StringBuilder();
        sb.AppendLine("neo 自动事件记录（最近 " + days + " 天）");
        sb.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine();

        List<Entry> list;
        try { list = ReadSince(DateTime.Now.AddDays(-days)); }
        catch (Exception ex) { return sb.AppendLine("读取事件记录失败：" + ex.Message).ToString(); }

        if (list.Count == 0)
        {
            sb.AppendLine("这段时间没有记录到异常事件（正常）。");
            return sb.ToString();
        }

        sb.AppendLine("事件总数：" + list.Count + "（按类型）");
        foreach (var g in list.GroupBy(e => KindLabel(e.Kind)).OrderByDescending(g => g.Count()))
            sb.AppendLine("  · " + g.Key + "：" + g.Count() + " 次");
        sb.AppendLine();

        var lastError = list.LastOrDefault(e => e.Kind is "error" or "kernel-exit" or "safe-mode");
        if (lastError != null)
            sb.AppendLine("最近一次需要关注的事件：" + lastError.ResolvedTime + " " + KindLabel(lastError.Kind) + " —— " + lastError.Summary);
        else
            sb.AppendLine("最近一次需要关注的事件：无");
        sb.AppendLine();

        sb.AppendLine("最近 40 条明细（时间 / 类型 / 说明）：");
        foreach (var e in list.Skip(Math.Max(0, list.Count - 40)))
        {
            sb.Append("  ").Append(e.ResolvedTime).Append("  [").Append(KindLabel(e.Kind)).Append("] ").Append(e.Summary);
            if (!string.IsNullOrWhiteSpace(e.Detail))
                sb.Append("  （").Append(e.Detail.Length <= 240 ? e.Detail : e.Detail[..240] + "…").Append("）");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static string KindLabel(string kind) => kind switch
    {
        "connect" => "连接",
        "disconnect" => "断开",
        "failover" => "故障转移",
        "kernel-exit" => "内核退出",
        "kernel-restart" => "内核重启",
        "auto-repair" => "自动修复",
        "fallback" => "兜底降级",
        "safe-mode" => "安全模式",
        "startup" => "启动",
        "error" => "错误",
        _ => kind,
    };

    private sealed class Entry
    {
        public string Time { get; init; } = "";
        public string Kind { get; init; } = "";
        public string Summary { get; init; } = "";
        public string Detail { get; init; } = "";
        public string ResolvedTime => Time;
    }

    private static List<Entry> ReadSince(DateTime since)
    {
        var result = new List<Entry>();
        if (!File.Exists(FilePath)) return result;

        var cutoff = since.ToString("yyyy-MM-dd HH:mm:ss");
        foreach (var line in File.ReadLines(FilePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var ts = root.TryGetProperty("ts", out var t) ? t.GetString() ?? "" : "";
                if (string.CompareOrdinal(ts, cutoff) < 0) continue;
                result.Add(new Entry
                {
                    Time = ts,
                    Kind = root.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "",
                    Summary = root.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "",
                    Detail = root.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : "",
                });
            }
            catch { }
        }
        return result;
    }

    private static string Clamp(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length <= max ? s : s[..max] + "…";
    }
}
