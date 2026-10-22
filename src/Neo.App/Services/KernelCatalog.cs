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

    private static readonly Dictionary<CoreEngine, string?> VersionCache = new();

    public static CoreEngineSpec Get(CoreEngine e) => All.First(s => s.Engine == e);

    public static string ExePath(CoreEngine e) => Path.Combine(NeoPaths.RuntimeDir, Get(e).ExeName);

    public static bool IsInstalled(CoreEngine e)
    {
        try { return File.Exists(ExePath(e)); } catch { return false; }
    }

    /// <summary>
    /// 读内核版本（缓存）。xray/sing-box 的版本由 ConnectionService 统一探测并缓存，
    /// 这里只补 mihomo；三条路径的口径一致（取输出里第一个 semver）。
    /// </summary>
    public static string? ReadVersion(CoreEngine e)
    {
        if (VersionCache.TryGetValue(e, out var cached)) return cached;
        string? ver = null;
        try
        {
            var exe = ExePath(e);
            if (File.Exists(exe))
            {
                var args = e switch
                {
                    CoreEngine.Xray => "-version",
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
                    var so = p.StandardOutput.ReadToEnd();
                    var se = p.StandardError.ReadToEnd();
                    p.WaitForExit(4000);
                    var text = (so + "\n" + se).Trim();
                    var m = Regex.Match(text, @"v?(\d+\.\d+\.\d+(?:-[0-9A-Za-z.]+)?)");
                    if (m.Success) ver = m.Groups[1].Value;
                }
            }
        }
        catch { }
        VersionCache[e] = ver;
        return ver;
    }

    public static void ResetVersion(CoreEngine e) => VersionCache.Remove(e);

    /// <summary>设置页展示用：已安装 → 版本号；未安装 → 明确写「未安装」。</summary>
    public static string Describe(CoreEngine e)
    {
        if (!IsInstalled(e)) return "未安装";
        var v = ReadVersion(e);
        return string.IsNullOrEmpty(v) ? "已安装 · 版本未知" : v;
    }
}
