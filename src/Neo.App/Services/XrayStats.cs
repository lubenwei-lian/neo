using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Neo.Core;

namespace Neo.App.Services;

public static class XrayStats
{
    public readonly record struct Snapshot(long Uplink, long Downlink);

    /// 这些出站/入站 tag 是 xray 内部用途（直连 / 拦截 / 统计 API），不是用户流量。
    private static readonly HashSet<string> ReservedOutTags =
        new(StringComparer.OrdinalIgnoreCase) { "direct", "block", "api" };

    /// 代理入站（用户流量的入口）。仅在上游出站命名无法识别时作为兜底口径。
    private static readonly HashSet<string> ProxyInTags =
        new(StringComparer.OrdinalIgnoreCase) { "socks-in", "http-in" };

    private static bool _warnedFallback;
    private static bool _warnedShapeMismatch;

    public static Snapshot? Query()
    {
        try
        {
            var exe = Path.Combine(NeoPaths.RuntimeDir, "xray.exe");
            if (!File.Exists(exe)) { Diag("xray.exe 不存在：" + exe); return null; }

            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                ArgumentList = { "api", "statsquery", $"--server=127.0.0.1:{Neo.Core.LocalPorts.StatsApi}" },
                RedirectStandardOutput = true,
                // 必须重定向 stderr 并异步读空：不重定向时子进程继承的是 GUI 进程（WinExe）的
                // 无效 stderr 句柄，内核一写告警就可能失败/阻塞；重定向了却不读，管道写满同样卡住。
                // 读到的 stderr 同时留作失败诊断（见 Diag）。
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p == null) { Diag("Process.Start 返回 null"); return null; }

            //   整个采样线程（以及调用它的后台循环）被永久挂住。
            //   改为异步读 + 3 秒总超时，超时即 Kill 并返回 null。
            var readTask = p.StandardOutput.ReadToEndAsync();
            // 与 stdout 并行把 stderr 读完丢弃：既防管道写满阻塞，也留作失败诊断。
            var errTask = p.StandardError.ReadToEndAsync();
            if (!readTask.Wait(TimeSpan.FromSeconds(3)))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                Diag("读 stdout 超时 3 秒（子进程可能卡住）");
                return null;
            }

            string txt;
            try { txt = readTask.Result; }
            catch
            {
                // 读取失败（进程被杀/管道断开）：同样收尾，避免留下僵尸进程。
                try { p.Kill(entireProcessTree: true); } catch { }
                Diag("读 stdout 失败（进程被杀/管道断开）");
                return null;
            }
            try { p.WaitForExit(2000); } catch { }

            if (string.IsNullOrWhiteSpace(txt))
            {
                var err = "";
                try { if (errTask.Wait(500)) err = errTask.Result; } catch { }
                Diag($"stdout 为空（exit={(p.HasExited ? p.ExitCode : -1)}，stderr={Clip(err)}）");
                return null;
            }

            using var doc = JsonDocument.Parse(txt);
            if (!doc.RootElement.TryGetProperty("stat", out var arr)) return null;

            long outUp = 0, outDown = 0;
            long inUp = 0, inDown = 0;
            var outMatched = 0;

            foreach (var item in arr.EnumerateArray())
            {
                if (!item.TryGetProperty("name", out var nameEl)) continue;
                var name = nameEl.GetString() ?? "";

                if (TryParse(name, "outbound>>>", out var outTag, out var outDir))
                {
                    if (ReservedOutTags.Contains(outTag)) continue;
                    // 固定出口：hop{i}（客户端→入口）与 chain{i}（入口→出口）承载同一份用户流量，
                    // 两者相加会把流量翻倍，故只计 chain 段（用户实际收发）。
                    if (outTag.StartsWith("hop", StringComparison.OrdinalIgnoreCase)) continue;
                    outMatched++;
                    var val = ReadValue(item);
                    if (outDir == "uplink") outUp += val; else outDown += val;
                    continue;
                }

                if (TryParse(name, "inbound>>>", out var inTag, out var inDir))
                {
                    if (!ProxyInTags.Contains(inTag)) continue;
                    var val = ReadValue(item);
                    if (inDir == "uplink") inUp += val; else inDown += val;
                }
            }

            if (outMatched > 0) return new Snapshot(outUp, outDown);

            // 出站 tag 一个都没认出来（未来改了命名 / 新增了非 proxy 前缀的出站）。
            // 退回「代理入站」口径：它是用户流量的入口，与订阅计费口径一致，
            if (inUp > 0 || inDown > 0)
            {
                if (!_warnedFallback)
                {
                    _warnedFallback = true;
                    LogBus.App("流量统计：未能识别代理出站，已自动切换为「代理入站」口径统计", "Warning");
                }
                return new Snapshot(inUp, inDown);
            }

            // 两条路都没数：返回 0 会伪装成「今天没流量」，让统计静默失效。
            // 宁可返回 null 让界面保留上一帧。
            if (!_warnedShapeMismatch)
            {
                _warnedShapeMismatch = true;
                LogBus.App("流量统计：内核统计数据中没有可识别的代理流量，统计已暂停（请反馈日志）", "Warning");
            }
            Diag($"无可用统计条目（条目数={arr.GetArrayLength()}，outMatched={outMatched}，inUp={inUp}，inDown={inDown}）");
            return null;
        }
        catch (Exception ex)
        {
            Diag($"[{ex.GetType().Name}] {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 取数失败诊断（限流 20 秒一条）。
    /// 为什么需要：取数失败时界面只「保留上一帧」，不写日志的话用户和排障者都看不到原因
    /// （2026-10-23 上传/下载停在旧值即因此静默失败被掩盖）。
    /// </summary>
    private static DateTime _lastDiagAtUtc;
    private static int _diagCount;

    private static void Diag(string msg)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastDiagAtUtc).TotalSeconds < 20) return;
        _lastDiagAtUtc = now;
        _diagCount++;
        try { LogBus.App($"流量统计取数失败（第 {_diagCount} 次）：{msg}", "Warning"); } catch { }
    }

    private static string Clip(string s)
        => string.IsNullOrEmpty(s)
            ? "<空>"
            : (s.Length <= 200
                ? s.Replace(Environment.NewLine, " ")
                : s[..200].Replace(Environment.NewLine, " ") + "…");

    private static long ReadValue(JsonElement item)
    {
        if (!item.TryGetProperty("value", out var v)) return 0;
        try
        {
            return v.ValueKind switch
            {
                JsonValueKind.Number => v.GetInt64(),
                JsonValueKind.String => long.TryParse(v.GetString(), out var n) ? n : 0,
                _ => 0,
            };
        }
        catch { return 0; }
    }

    /// 解析形如 `outbound>>>proxy>>>traffic>>>uplink` 的统计名。
    /// 统计口径按「排除 direct/block/api 后的全部出站」汇总，不依赖具体 tag 名：
    /// 当前出站 tag 是单节点的 `proxy` 与固定出口的 `hop*` / `chain*`，
    /// 都由这里按 `outbound>>>` + `>>>traffic>>>` 前缀解析。
    private static bool TryParse(string name, string prefix, out string tag, out string dir)
    {
        tag = "";
        dir = "";
        const string middle = ">>>traffic>>>";
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) return false;

        var rest = name.AsSpan(prefix.Length);
        var idx = rest.IndexOf(middle, StringComparison.Ordinal);
        if (idx <= 0) return false;

        tag = rest[..idx].ToString();
        dir = rest[(idx + middle.Length)..].ToString();
        return dir is "uplink" or "downlink";
    }

    public static string Human(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("0.##") + " MB";
        return (bytes / 1024.0 / 1024 / 1024).ToString("0.##") + " GB";
    }
}
