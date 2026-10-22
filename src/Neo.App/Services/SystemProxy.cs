using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using Neo.Core;

namespace Neo.App.Services;

public static class SystemProxy
{
    private const string RegPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RegPath);
                return k?.GetValue("ProxyEnable") is int v && v == 1;
            }
            catch { return false; }
        }
    }

    public static string? CurrentServer
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RegPath);
                return k?.GetValue("ProxyServer") as string;
            }
            catch { return null; }
        }
    }

    /// 文件放在 data 目录下，随 neo 的数据一起存在/迁移。
    private sealed class ProxyBackup
    {
        public int ProxyEnable { get; set; }
        public string? ProxyServer { get; set; }
        public string? ProxyOverride { get; set; }
        public string? AutoConfigURL { get; set; }
        public long SavedAtUnix { get; set; }
    }

    private static string BackupPath => Path.Combine(NeoPaths.DataDir, "proxy-backup.json");

    /// <summary>本进程是否真的把系统代理改成了 neo 的值（用于 Disable 的兜底判断）。</summary>
    private static bool _tookOver;

    public static bool Enable(string server)
    {
        try
        {
            //   此时再快照就会把 neo 自己的 127.0.0.1:10808 当成用户设置，断开时还原不回去。
            TrySnapshotOnce();
            using var k = Registry.CurrentUser.CreateSubKey(RegPath);
            if (k == null) return false;
            k.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            k.SetValue("ProxyServer", server, RegistryValueKind.String);
            _tookOver = true;
            Notify();
            return true;
        }
        catch (Exception ex)
        {
            LogBus.App($"开启系统代理失败：{ex.GetType().Name} {ex.Message}", "Warning");
            return false;
        }
    }

    /// 三条分支：
    ///   ① 有快照 → 还原 4 个值并删除快照（正常连接后的断开走这里）；
    ///   ② 无快照但本进程接管过（快照读写失败）→ 退回旧行为「能关就关」，并留 Warning；
    ///   ③ 无快照且本进程从未接管（典型：TUN 模式，Disconnect 也会无条件调本方法）
    ///      → **什么都不做**，避免误关用户自己设置的代理。
    /// <paramref name="force"/> = true 时跳过 ③：供 <see cref="CleanupOrphaned"/> 使用 ——
    /// 必须关掉，否则用户会断网（这条路径没有本进程的 _tookOver 记忆可用）。
    public static bool Disable(bool force = false)
    {
        try
        {
            if (File.Exists(BackupPath))
            {
                if (TryRestoreFromBackup(out var why))
                {
                    try { File.Delete(BackupPath); }
                    catch (Exception ex)
                    {
                        // 删不掉只是下次会重复还原，不能因此让「关闭」失败。
                        LogBus.App($"还原后删除系统代理快照失败（{BackupPath}）：{ex.GetType().Name} {ex.Message}", "Warning");
                    }
                    _tookOver = false;
                    Notify();
                    return true;
                }

                // 还原失败：保留快照供下次重试，但仍先按旧行为把代理关掉（否则指向已死的 neo 会断网）。
                LogBus.App($"系统代理还原失败（{why}）——已改为仅关闭代理开关；原设置快照保留在 {BackupPath}", "Warning");
            }
            else if (!_tookOver && !force)
            {
                return false;
            }

            using var k = Registry.CurrentUser.CreateSubKey(RegPath);
            if (k == null) return false;
            k.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            _tookOver = false;
            Notify();
            return true;
        }
        catch (Exception ex)
        {
            LogBus.App($"关闭系统代理失败：{ex.GetType().Name} {ex.Message}", "Warning");
            return false;
        }
    }

    /// 读写失败不阻断 Enable：仍按旧行为覆盖并关闭（能关就关），但必须留 Warning。
    private static void TrySnapshotOnce()
    {
        try
        {
            if (File.Exists(BackupPath)) return;
            using var k = Registry.CurrentUser.OpenSubKey(RegPath);
            var backup = new ProxyBackup
            {
                ProxyEnable = k?.GetValue("ProxyEnable") is int v ? v : 0,
                ProxyServer = k?.GetValue("ProxyServer") as string,
                ProxyOverride = k?.GetValue("ProxyOverride") as string,
                AutoConfigURL = k?.GetValue("AutoConfigURL") as string,
                SavedAtUnix = DateTimeOffset.Now.ToUnixTimeSeconds(),
            };
            Directory.CreateDirectory(NeoPaths.DataDir);
            var tmp = BackupPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, BackupPath, true);
        }
        catch (Exception ex)
        {
            LogBus.App($"系统代理快照写入失败（{BackupPath}）：{ex.GetType().Name} {ex.Message} —— 断开时无法还原原有代理设置", "Warning");
        }
    }

    private static bool TryRestoreFromBackup(out string why)
    {
        why = "";
        try
        {
            var backup = JsonSerializer.Deserialize<ProxyBackup>(File.ReadAllText(BackupPath));
            if (backup == null) { why = "快照内容为空"; return false; }
            using var k = Registry.CurrentUser.CreateSubKey(RegPath);
            if (k == null) { why = "无法打开注册表键"; return false; }
            k.SetValue("ProxyEnable", backup.ProxyEnable, RegistryValueKind.DWord);
            SetOrDelete(k, "ProxyServer", backup.ProxyServer);
            SetOrDelete(k, "ProxyOverride", backup.ProxyOverride);
            SetOrDelete(k, "AutoConfigURL", backup.AutoConfigURL);
            return true;
        }
        catch (Exception ex)
        {
            why = $"{ex.GetType().Name} {ex.Message}";
            return false;
        }
    }

    private static void SetOrDelete(RegistryKey k, string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            try { k.DeleteValue(name, false); } catch { }
        }
        else
        {
            k.SetValue(name, value, RegistryValueKind.String);
        }
    }

    private static void Notify()
    {
        try
        {
            InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
        }
        catch { }
    }

    /// 端口是否有人监听（连得上就说明有进程在听）。
    /// 这里**不能**用 `c.ConnectAsync(...).Wait(300)`：超时返回 false 之后，那个连接任务仍然
    /// 在后台跑，随后 `using` 释放 TcpClient / 请求中止时抛出 SocketException
    ///（"由于线程退出或应用程序请求，已中止 I/O 操作"）——该任务无人 await，于是变成
    /// **未观察的 Task 异常**，最后被 `TaskScheduler.UnobservedTaskException` 在终结器线程上
    /// 抛出来。这正是 neo 日志里长期存在的「后台任务未观察异常：AggregateException」
    /// 正确做法：用带超时的 CancellationToken 让连接**真正被取消并 await 到结果**，
    /// 异常就地吃掉，不留悬挂任务。
    private static bool IsPortListening(int port)
    {
        var client = new System.Net.Sockets.TcpClient();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            try
            {
                client.ConnectAsync("127.0.0.1", port, cts.Token).GetAwaiter().GetResult();
                return true;
            }
            catch
            {
                // 超时/拒绝连接都表示「没人监听」；这里就地吃掉，避免留下未观察的任务异常。
                return false;
            }
        }
        finally
        {
            try { client.Dispose(); } catch { }
        }
    }

    /// 代理串是否指向本机 neo 端口。ProxyServer 可能是 "127.0.0.1:10808"，
    /// 也可能是分协议形式（"http=127.0.0.1:10808;https=..."），两种都认。
    private static bool PointsToNeoPort(string server, int port)
    {
        foreach (var part in server.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var s = part.Contains('=') ? part[(part.IndexOf('=') + 1)..] : part;
            s = s.Trim();
            if (s.Equals($"127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase) ||
                s.Equals($"localhost:{port}", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// 清理「孤儿系统代理」：neo 被强杀或崩溃时 Disable() 不会执行，
    /// 注册表残留 ProxyEnable=1 指向本机端口，但已无进程监听 → 所有走系统代理的程序全部断网，
    /// 且报错五花八门，极易被误判成网络故障。
    /// 判据必须包含「该端口无人监听」这一条：若 v2rayN 等第三方代理正占用同端口，
    /// 那是它自己的合法设置，绝不能动。
    public static bool CleanupOrphaned(params int[] neoPorts)
    {
        try
        {
            if (!IsEnabled) return false;
            var server = CurrentServer;
            if (string.IsNullOrWhiteSpace(server)) return false;
            var matched = -1;
            foreach (var port in neoPorts)
            {
                if (PointsToNeoPort(server, port)) { matched = port; break; }
            }
            if (matched < 0) return false;
            if (IsPortListening(matched)) return false;
            return Disable(force: true);
        }
        catch { return false; }
    }
}
