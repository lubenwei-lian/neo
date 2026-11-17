using System.Diagnostics;
using System.Text.RegularExpressions;
using Neo.Core;

namespace Neo.App.Services;

/// <summary>neo 管理的内核种类。与「节点能力判定」无关：这里只负责文件/版本/更新。</summary>
public enum CoreEngine
{
    Xray,
    SingBox,
    Mihomo,
    Naive,
}

/// <summary>
/// 内核的静态描述（对照同类客户端的 CoreInfo 设计）：
/// 可执行文件名、上游仓库、版本探测方式、是否允许预发布。
/// 新增内核时只改这里，更新检查与设置页都从这里读，避免三处硬编码漂移。
/// </summary>
public sealed record CoreEngineSpec(
    CoreEngine Engine,
    string Display,
    string ExeName,
    string Owner,
    string Repo,
    bool AllowPreRelease);

public static class KernelCatalog
{
    public static readonly CoreEngineSpec[] All =
    [
        new(CoreEngine.Xray, "xray", "xray.exe", "XTLS", "Xray-core", true),
        new(CoreEngine.SingBox, "sing-box", "sing-box.exe", "SagerNet", "sing-box", false),
        new(CoreEngine.Mihomo, "mihomo", "mihomo.exe", "MetaCubeX", "mihomo", false),
        // naiveproxy：naive.exe 作为辅助进程（socks 入口），前置由 sing-box 承载
        new(CoreEngine.Naive, "naive", "naive.exe", "klzgrad", "naiveproxy", false),
    ];

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<CoreEngine, string?> VersionCache = new();
    private static readonly object[] VersionGates = All.Select(_ => new object()).ToArray();

    public static CoreEngineSpec Get(CoreEngine e) => All.First(s => s.Engine == e);

    public static string ExePath(CoreEngine e) => Path.Combine(NeoPaths.RuntimeDir, Get(e).ExeName);

    public static bool IsInstalled(CoreEngine e)
    {
        try { return File.Exists(ExePath(e)); } catch { return false; }
    }

    /// <summary>
    /// 所有界面和更新检查共用此版本缓存。主动检查可重新探测，避免一次启动失败永久显示未知。
    /// 每个内核串行探测；只从成功退出的版本输出识别，避免误取环境或错误信息中的数字。
    /// </summary>
    public static string? ReadVersion(CoreEngine e, bool refresh = false)
    {
        lock (VersionGates[(int)e]) return ReadVersionCore(e, refresh);
    }

    private static string? ReadVersionCore(CoreEngine e, bool refresh)
    {
        if (refresh) VersionCache.TryRemove(e, out _);
        if (VersionCache.TryGetValue(e, out var cached)) return cached;
        string? ver = null;
        try
        {
            var exe = ExePath(e);
            if (File.Exists(exe))
            {
                var args = e switch
                {
                    CoreEngine.Xray => "version",
                    CoreEngine.SingBox => "version",
                    CoreEngine.Naive => "--version",
                    _ => "-v",
                };
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = NeoPaths.RuntimeDir,
                };
                using var p = Process.Start(psi);
                if (p != null)
                {
                    Neo.Core.Core.KillOnCloseJob.Attach(p, out _);
                    try
                    {
                        // Drain both pipes concurrently; timeout must cover reads too, not just
                        // WaitForExit after synchronous ReadToEnd has already blocked forever.
                        var stdout = p.StandardOutput.ReadToEndAsync();
                        var stderr = p.StandardError.ReadToEndAsync();
                        if (Task.WhenAll(stdout, stderr).Wait(TimeSpan.FromSeconds(4)) && p.WaitForExit(1000) && p.ExitCode == 0)
                        {
                            var text = (stdout.Result + "\n" + stderr.Result).Trim();
                            var prefix = e switch
                            {
                                CoreEngine.Xray => @"Xray\s+",
                                CoreEngine.SingBox => @"sing-box\s+version\s+",
                                CoreEngine.Mihomo => @"Mihomo(?:\s+Meta)?\s+",
                                _ => @"(?:naive\s+)?",
                            };
                            var m = Regex.Match(text, @"^\s*" + prefix + @"v?(\d+(?:\.\d+){2,3}(?:-[0-9A-Za-z.]+)?)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                            if (m.Success) ver = m.Groups[1].Value;
                        }
                    }
                    finally
                    {
                        if (!p.HasExited) { try { p.Kill(entireProcessTree: true); p.WaitForExit(2000); } catch { } }
                    }
                }
            }
        }
        catch { }
        VersionCache[e] = ver;
        return ver;
    }

    public static void ResetVersion(CoreEngine e)
    {
        lock (VersionGates[(int)e]) VersionCache.TryRemove(e, out _);
    }
    public static string? CachedVersion(CoreEngine e) => VersionCache.TryGetValue(e, out var value) ? value : null;

    /// <summary>设置页展示用：已安装 → 版本号；未安装 → 明确写「未安装」。</summary>
    public static string Describe(CoreEngine e)
    {
        if (!IsInstalled(e)) return "未安装";
        var v = ReadVersion(e);
        return string.IsNullOrEmpty(v) ? "已安装 · 版本未知" : v;
    }
}
