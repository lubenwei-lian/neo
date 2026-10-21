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
            if (!File.Exists(exe)) return null;

            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                ArgumentList = { "api", "statsquery", "--server=127.0.0.1:10085" },
                RedirectStandardOutput = true,
                // 刻意不重定向 stderr：重定向了却不读，子进程写满管道缓冲（约 4 KB）后会阻塞，
                // 而父进程正在等 stdout → 双方互等死锁。本调用只需要 stdout。
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p == null) return null;

            //   整个采样线程（以及调用它的后台循环）被永久挂住。
            //   改为异步读 + 3 秒总超时，超时即 Kill 并返回 null。
            var readTask = p.StandardOutput.ReadToEndAsync();
            if (!readTask.Wait(TimeSpan.FromSeconds(3)))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            string txt;
            try { txt = readTask.Result; }
            catch
            {
                // 读取失败（进程被杀/管道断开）：同样收尾，避免留下僵尸进程。
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            try { p.WaitForExit(2000); } catch { }

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

            // 两条路都没数：返回 0 会伪装成「今天没流量」，让统计静默失效
            //（这正是多节点加速上线后流量统计恒为 0 的形态）。宁可返回 null 让界面保留上一帧。
            if (!_warnedShapeMismatch)
            {
                _warnedShapeMismatch = true;
                LogBus.App("流量统计：内核统计数据中没有可识别的代理流量，统计已暂停（请反馈日志）", "Warning");
            }
            return null;
        }
        catch { return null; }
    }

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

    /// 解析形如 `outbound>>>proxy0>>>traffic>>>uplink` 的统计名。
    /// 单节点出站 tag 是 `proxy`，多节点加速下是 `proxy0`…`proxyN` ——
    /// 旧实现写死比较 `outbound>>>proxy>>>traffic>>>uplink`，在多节点下一条都匹配不到，
    /// 于是 up/down 恒为 0：图表全平、今日流量不再累加（用户报告的「流量统计不加载」）。
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
