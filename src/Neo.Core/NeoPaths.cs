using System.IO;

namespace Neo.Core;

public static class NeoPaths
{
    public static string RootDir => _rootDir ??= FindRootDir();

    private static string? _rootDir;

    public static string RuntimeDir => _runtimeDir ??= FindRuntimeDir();

    private static string? _runtimeDir;

    public static string LauncherPath
    {
        get
        {
            try
            {
                var candidate = Path.Combine(RootDir, "neo.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
            return Environment.ProcessPath ?? "";
        }
    }

    public static string DataDir
    {
        get
        {
            if (_dataDir == null)
            {
                var portable = Path.Combine(RootDir, "data");
                try
                {
                    Directory.CreateDirectory(portable);
                    var probe = Path.Combine(portable, ".writetest");
                    File.WriteAllText(probe, "ok");
                    File.Delete(probe);
                    _dataDir = portable;
                }
                catch
                {
                    var fallback = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "neo");
                    Directory.CreateDirectory(fallback);
                    _dataDir = fallback;
                }
            }
            return _dataDir;
        }
    }

    private static string? _dataDir;

    /// <summary>自定义日志目录（空 = 默认 DataDir\logs）。启动时由设置写入。</summary>
    private static string? _logDirOverride;

    public static void SetLogDirOverride(string? dir)
    {
        _logDirOverride = string.IsNullOrWhiteSpace(dir) ? null : dir.Trim();
    }

    public static string LogDir
    {
        get
        {
            var dir = Path.Combine(DataDir, "logs");
            if (_logDirOverride is { Length: > 0 } custom)
            {
                try
                {
                    Directory.CreateDirectory(custom);
                    dir = custom;
                }
                catch { /* 自定义目录不可用 → 回退默认，不能让日志写失败 */ }
            }
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string AppBaseDir()
    {
        var baseDir = AppContext.BaseDirectory;
        try
        {
            if (baseDir.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)
                && Environment.ProcessPath is { Length: > 0 } exe
                && Path.GetDirectoryName(exe) is { Length: > 0 } exeDir)
                return exeDir;
        }
        catch { }
        return baseDir;
    }

    private static string FindRootDir()
    {
        try
        {
            var home = Environment.GetEnvironmentVariable("NEO_HOME");
            if (!string.IsNullOrWhiteSpace(home) && Directory.Exists(home))
                return Path.GetFullPath(home);
        }
        catch { }
        var baseDir = AppBaseDir().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        try
        {
            var name = Path.GetFileName(baseDir);
            var parent = Path.GetDirectoryName(baseDir);
            if (string.Equals(name, "app", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(parent))
                return parent;
        }
        catch { }
        return baseDir;
    }

    private static string FindRuntimeDir()
    {
        var dir = new DirectoryInfo(AppBaseDir());
        for (int i = 0; i < 8 && dir != null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "runtime");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "xray.exe")))
                return candidate;
            dir = dir.Parent;
        }
        return Path.Combine(AppBaseDir(), "runtime");
    }
}
