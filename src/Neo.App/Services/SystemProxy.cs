using System.ComponentModel;
using System.Diagnostics;
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
    private const int InternetOptionPerConnection = 75;
    private const int InternetOptionProxySettingsChanged = 95;
    private const uint ProxyTypeDirect = 1;
    private const uint ProxyTypeProxy = 2;
    private const uint ProxyTypeAutoUrl = 4;
    private const uint ProxyTypeAutoDetect = 8;

    [StructLayout(LayoutKind.Explicit)]
    private struct InternetOptionValue
    {
        [FieldOffset(0)] public uint Flags;
        [FieldOffset(0)] public IntPtr Text;
        [FieldOffset(0)] public long FileTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InternetConnectionOption
    {
        public uint Option;
        public InternetOptionValue Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InternetConnectionOptions
    {
        public int Size;
        public IntPtr Connection;
        public int Count;
        public int Error;
        public IntPtr Options;
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    private static extern bool SetConnectionOptions(IntPtr internet, int option, ref InternetConnectionOptions options, int size);

    [DllImport("wininet.dll", EntryPoint = "InternetQueryOptionW", SetLastError = true)]
    private static extern bool QueryConnectionOptions(IntPtr internet, int option, ref InternetConnectionOptions options, ref int size);

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

    /// <summary>The Windows connection configuration must agree with Neo's public endpoints.</summary>
    public static bool IsEffective
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RegPath);
                if (k == null) return false;
                var configured = ReadValues(k);
                if (configured.Enable != 1 || !SameList(configured.Server, LocalProxySettings.Server)
                    || !SameList(configured.Bypass, ProxyBypass) || !string.IsNullOrEmpty(configured.Pac)) return false;
                return SameNative(ReadNativeValues(), NativeFrom(configured, ProxyTypeDirect | ProxyTypeProxy));
            }
            catch { return false; }
        }
    }

    /// 文件放在 data 目录下，随 neo 的数据一起存在/迁移。
    private static readonly object ProxyGate = new();

    private sealed class ProxyValues
    {
        public int? Enable { get; set; }
        public string? Server { get; set; }
        public string? Bypass { get; set; }
        public string? Pac { get; set; }
    }

    private sealed class NativeProxyValues
    {
        public uint Flags { get; set; }
        public string? Server { get; set; }
        public string? Bypass { get; set; }
        public string? Pac { get; set; }
    }

    private sealed class ProxyBackup
    {
        public int? ProxyEnable { get; set; }
        public string? ProxyServer { get; set; }
        public string? ProxyOverride { get; set; }
        public string? AutoConfigURL { get; set; }
        public uint? ConnectionFlags { get; set; }
        public NativeProxyValues? NativeOriginal { get; set; }
        public List<NativeProxyValues> NativeOwnedStates { get; set; } = new();
        public long SavedAtUnix { get; set; }
        public int OwnerPid { get; set; }
        public long OwnerStartedUtcTicks { get; set; }
        public string? OwnerExecutable { get; set; }
        public List<ProxyValues> OwnedStates { get; set; } = new();
    }

    private static string BackupPath => Path.Combine(NeoPaths.DataDir, "proxy-backup.json");

    /// <summary>本进程是否真的把系统代理改成了 neo 的值（用于 Disable 的兜底判断）。</summary>

    /// <summary>
    /// 系统代理的绕过清单：本机/内网不走代理（比只依赖内核分流更早生效——拿到域名的应用
    /// 连 DNS 都不需要出系统代理）。与上游客户端的 &lt;local&gt; + 私网段写法一致。
    /// </summary>
    private const string ProxyBypass = @"<local>;localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;172.22.*;172.23.*;172.24.*;172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;192.168.*;100.*";

    private static ProxyValues ReadValues(RegistryKey k) => new()
    {
        Enable = k.GetValue("ProxyEnable") as int?,
        Server = k.GetValue("ProxyServer") as string,
        Bypass = k.GetValue("ProxyOverride") as string,
        Pac = k.GetValue("AutoConfigURL") as string,
    };

    private static bool Matches(ProxyValues a, ProxyValues b) => a.Enable.GetValueOrDefault() == b.Enable.GetValueOrDefault()
        && SameList(a.Server, b.Server) && SameList(a.Bypass, b.Bypass)
        && string.Equals(a.Pac ?? "", b.Pac ?? "", StringComparison.Ordinal);

    private static bool Owns(ProxyBackup? backup, ProxyValues actual)
        => backup != null && backup.OwnedStates.Any(v => Matches(v, actual));

    private static string NormalizeList(string? value) => string.Join(';',
        (value ?? "").Split(new[] { ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)).ToLowerInvariant();

    private static bool SameList(string? a, string? b) => NormalizeList(a) == NormalizeList(b);

    private static NativeProxyValues NativeFrom(ProxyValues values, uint flags) => new()
    {
        Flags = flags, Server = values.Server, Bypass = values.Bypass, Pac = values.Pac,
    };

    private static bool SameNative(NativeProxyValues a, NativeProxyValues b, bool allowEnableToggle = false)
        => ((a.Flags ^ b.Flags) & (allowEnableToggle ? 13u : 15u)) == 0
            && SameList(a.Server, b.Server) && SameList(a.Bypass, b.Bypass)
            && string.Equals(a.Pac ?? "", b.Pac ?? "", StringComparison.Ordinal);

    private static bool OwnsNative(ProxyBackup backup, NativeProxyValues actual)
    {
        if (backup.NativeOriginal != null && SameNative(backup.NativeOriginal, actual)) return true;
        if (backup.NativeOwnedStates.Any(v => SameNative(v, actual, allowEnableToggle: true))) return true;
        // Older leases have no native identity. Do not take over an active foreign proxy/PAC.
        return backup.NativeOriginal == null && backup.NativeOwnedStates.Count == 0
            && (actual.Flags & (ProxyTypeAutoUrl | ProxyTypeAutoDetect)) == 0
            && ((actual.Flags & ProxyTypeProxy) == 0
                || LocalProxySettings.IsOwnedServer(actual.Server, LocalPorts.Socks, LocalPorts.Http));
    }

    private static void WriteBackup(ProxyBackup backup)
    {
        Directory.CreateDirectory(NeoPaths.DataDir);
        var tmp = BackupPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(backup));
        File.Move(tmp, BackupPath, true);
    }

    private sealed class RegistryLease : IDisposable
    {
        private readonly Mutex _mutex = new(false, @"Local\neo-system-proxy-4f8a2c");
        public RegistryLease()
        {
            try
            {
                if (!_mutex.WaitOne(TimeSpan.FromSeconds(3))) throw new TimeoutException("Another proxy transaction is still running");
            }
            catch (AbandonedMutexException) { } // Previous owner crashed; recovery owns the mutex now.
            catch { _mutex.Dispose(); throw; }
        }
        public void Dispose() { try { _mutex.ReleaseMutex(); } finally { _mutex.Dispose(); } }
    }

    private static bool IsLiveProxyOwner(ProxyBackup backup)
    {
        if (backup.OwnerPid <= 0 || backup.OwnerPid == Environment.ProcessId) return false;
        try
        {
            using var process = Process.GetProcessById(backup.OwnerPid);
            return !process.HasExited
                && (backup.OwnerStartedUtcTicks == 0 || process.StartTime.ToUniversalTime().Ticks == backup.OwnerStartedUtcTicks)
                && (string.IsNullOrEmpty(backup.OwnerExecutable)
                    || string.Equals(process.MainModule?.FileName, backup.OwnerExecutable, StringComparison.OrdinalIgnoreCase));
        }
        catch (ArgumentException) { return false; }
        catch { return true; }
    }

    public static bool Enable(string server, bool repair = false)
    {
        lock (ProxyGate)
        {
            try
            {
                using var transaction = new RegistryLease();
                using var k = Registry.CurrentUser.CreateSubKey(RegPath);
                if (k == null) return false;
                var actual = ReadValues(k);
                var native = ReadNativeValues();
                ProxyBackup? backup = null;
                if (File.Exists(BackupPath))
                    try { backup = JsonSerializer.Deserialize<ProxyBackup>(File.ReadAllText(BackupPath)); } catch { }
                if (repair && backup != null && IsLiveProxyOwner(backup)) return false;
                var enableOnlyChange = backup != null && actual.Enable == 0 && backup.OwnedStates.Any(v =>
                    v.Enable == 1 && Matches(v, new ProxyValues { Enable = 1, Server = actual.Server, Bypass = actual.Bypass, Pac = actual.Pac }));
                var retainedRegistry = Owns(backup, actual) || (repair && enableOnlyChange);
                var retainedLease = retainedRegistry && backup != null && OwnsNative(backup, native);
                if (repair && retainedRegistry && !retainedLease) return false;
                if (repair && ((native.Flags & (ProxyTypeAutoUrl | ProxyTypeAutoDetect)) != 0
                    || ((native.Flags & ProxyTypeProxy) != 0
                        && !LocalProxySettings.IsOwnedServer(native.Server, LocalPorts.Socks, LocalPorts.Http)))) return false;
                if (repair && !retainedLease && (backup != null || !string.IsNullOrEmpty(actual.Pac)
                    || (!string.IsNullOrEmpty(actual.Server) && !LocalProxySettings.IsOwnedServer(actual.Server,
                        LocalPorts.Socks, LocalPorts.Http)))) return false;
                // A foreign takeover terminates the previous lease. A new explicit enable snapshots its owner.
                if (!retainedLease) backup = new ProxyBackup
                {
                    // The effective Windows view is authoritative if registry scalars were stale.
                    ProxyEnable = (native.Flags & ProxyTypeProxy) != 0 ? 1 : 0,
                    ProxyServer = SameList(native.Server, actual.Server) ? actual.Server : native.Server,
                    ProxyOverride = SameList(native.Bypass, actual.Bypass) ? actual.Bypass : native.Bypass,
                    AutoConfigURL = string.Equals(native.Pac ?? "", actual.Pac ?? "", StringComparison.Ordinal) ? actual.Pac : native.Pac,
                    ConnectionFlags = native.Flags,
                    NativeOriginal = native,
                    SavedAtUnix = DateTimeOffset.Now.ToUnixTimeSeconds(),
                };
                using var owner = Process.GetCurrentProcess();
                backup!.OwnerPid = Environment.ProcessId;
                backup.OwnerStartedUtcTicks = owner.StartTime.ToUniversalTime().Ticks;
                backup.OwnerExecutable = Environment.ProcessPath;
                backup.OwnedStates.Clear();
                if (retainedLease) backup.OwnedStates.Add(actual);
                var next = new ProxyValues { Enable = actual.Enable, Server = server, Bypass = actual.Bypass, Pac = actual.Pac };
                var expected = actual;
                void WriteOwned(Action write)
                {
                    if (!Matches(ReadValues(k), expected)) throw new InvalidOperationException("Proxy owner changed during takeover");
                    // Persist the intended state before every individual registry write, covering partial takeover/crash.
                    var written = new ProxyValues { Enable = next.Enable, Server = next.Server, Bypass = next.Bypass, Pac = next.Pac };
                    backup!.OwnedStates.Add(written);
                    WriteBackup(backup);
                    write();
                    expected = written;
                }
                WriteOwned(() => k.SetValue("ProxyServer", server, RegistryValueKind.String));
                next.Bypass = ProxyBypass;
                WriteOwned(() => k.SetValue("ProxyOverride", ProxyBypass, RegistryValueKind.String));
                next.Pac = null;
                WriteOwned(() => k.DeleteValue("AutoConfigURL", false));
                next.Enable = 1;
                WriteOwned(() => k.SetValue("ProxyEnable", 1, RegistryValueKind.DWord));
                backup.NativeOwnedStates.Clear();
                backup.NativeOwnedStates.Add(NativeFrom(next, ProxyTypeDirect | ProxyTypeProxy));
                WriteBackup(backup);
                Notify();
                return true;
            }
            catch (Exception ex)
            {
                LogBus.App("System proxy takeover failed: " + ex.Message, "Warning");
                return false;
            }
        }
    }

    public static bool Disable(bool force = false)
    {
        lock (ProxyGate)
        {
            try
            {
                using var transaction = new RegistryLease();
                if (File.Exists(BackupPath))
                {
                    if (!TryRestoreFromBackup(out var why))
                    {
                        LogBus.App("System proxy restore skipped: " + why, "Warning");
                        return false;
                    }
                    File.Delete(BackupPath);
                    return true;
                }
                // No lease: only an explicitly requested orphan cleanup may disable our exact endpoint.
                if (!force) return false;
                using var k = Registry.CurrentUser.CreateSubKey(RegPath);
                if (k == null) return false;
                var values = ReadValues(k);
                var knownPorts = new[] { LocalPorts.Socks, LocalPorts.Http, LocalPorts.CoreSocks, LocalPorts.CoreHttp,
                    LocalPorts.DefaultSocks, LocalPorts.DefaultHttp, LocalPorts.DefaultSocks + 100,
                    LocalPorts.DefaultHttp + 100, 10808, 10809 };
                var native = ReadNativeValues();
                var registryOwned = LocalProxySettings.IsOwnedServer(values.Server, knownPorts);
                var nativeOwned = LocalProxySettings.IsOwnedServer(native.Server, knownPorts);
                if (!string.IsNullOrEmpty(values.Pac) || !string.IsNullOrEmpty(native.Pac)
                    || (native.Flags & (ProxyTypeAutoUrl | ProxyTypeAutoDetect)) != 0
                    || (values.Enable == 1 && !registryOwned)
                    || ((native.Flags & ProxyTypeProxy) != 0 && !nativeOwned)
                    || (!registryOwned && !nativeOwned)) return false;
                k.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                Notify();
                return true;
            }
            catch (Exception ex)
            {
                LogBus.App("System proxy restore failed: " + ex.Message, "Warning");
                return false;
            }
        }
    }

    private static bool TryRestoreFromBackup(out string why)
    {
        lock (ProxyGate)
        {
            why = "";
            try
            {
                var backup = JsonSerializer.Deserialize<ProxyBackup>(File.ReadAllText(BackupPath));
                if (backup == null) { why = "empty lease"; return false; }
                if (IsLiveProxyOwner(backup)) { why = "proxy lease belongs to another live Neo instance"; return false; }
                using var k = Registry.CurrentUser.CreateSubKey(RegPath);
                if (k == null) { why = "registry unavailable"; return false; }
                var actual = ReadValues(k);
                if (!Owns(backup, actual))
                {
                    // A stale snapshot is not authority over the current owner's settings.
                    File.Delete(BackupPath);
                    why = "proxy ownership changed; foreign settings preserved";
                    return false;
                }
                if (!OwnsNative(backup, ReadNativeValues()))
                {
                    File.Delete(BackupPath);
                    why = "effective Windows proxy ownership changed; foreign connection settings preserved";
                    return false;
                }
                backup.OwnedStates.Clear();
                backup.OwnedStates.Add(actual);
                var expected = actual;
                void RestoreOwned(ProxyValues next, Action write)
                {
                    if (!Matches(ReadValues(k), expected)) throw new InvalidOperationException("Proxy owner changed during restore");
                    if (!OwnsNative(backup, ReadNativeValues())) throw new InvalidOperationException("Windows proxy owner changed during restore");
                    backup.OwnedStates.Add(next);
                    WriteBackup(backup);
                    write();
                    expected = next;
                }
                var restored = new ProxyValues { Enable = actual.Enable, Server = backup.ProxyServer, Bypass = actual.Bypass, Pac = actual.Pac };
                RestoreOwned(restored, () => SetOrDelete(k, "ProxyServer", backup.ProxyServer));
                restored = new ProxyValues { Enable = actual.Enable, Server = backup.ProxyServer, Bypass = backup.ProxyOverride, Pac = actual.Pac };
                RestoreOwned(restored, () => SetOrDelete(k, "ProxyOverride", backup.ProxyOverride));
                restored = new ProxyValues { Enable = actual.Enable, Server = backup.ProxyServer, Bypass = backup.ProxyOverride, Pac = backup.AutoConfigURL };
                RestoreOwned(restored, () => SetOrDelete(k, "AutoConfigURL", backup.AutoConfigURL));
                restored = new ProxyValues { Enable = backup.ProxyEnable, Server = backup.ProxyServer, Bypass = backup.ProxyOverride, Pac = backup.AutoConfigURL };
                RestoreOwned(restored, () =>
                {
                    if (backup.ProxyEnable.HasValue) k.SetValue("ProxyEnable", backup.ProxyEnable.Value, RegistryValueKind.DWord);
                    else k.DeleteValue("ProxyEnable", false);
                });
                // Retain the lease until Windows has accepted the restored connection settings.
                // Old backups have no flags: derive the manual/PAC mode from their saved values.
                var restoredFlags = backup.ConnectionFlags ?? (ProxyTypeDirect
                    | (restored.Enable == 1 ? ProxyTypeProxy : 0)
                    | (!string.IsNullOrEmpty(restored.Pac) ? ProxyTypeAutoUrl : 0));
                var nativeTarget = NativeFrom(restored, restoredFlags);
                if (!backup.NativeOwnedStates.Any(v => SameNative(v, nativeTarget)))
                    backup.NativeOwnedStates.Add(nativeTarget);
                if (backup.NativeOwnedStates.Count > 2)
                    backup.NativeOwnedStates.RemoveRange(0, backup.NativeOwnedStates.Count - 2);
                WriteBackup(backup);
                Notify(backup.ConnectionFlags);
                return true;
            }
            catch (Exception ex) { why = ex.GetType().Name + ": " + ex.Message; return false; }
        }
    }

    /// <summary>Retry a failed owned restoration before its proxy listeners are removed.</summary>
    public static async Task<bool> RestoreAfterDisconnectAsync()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (!File.Exists(BackupPath)) return true;
            if (Disable() || !File.Exists(BackupPath)) return true;
            if (attempt < 2) await Task.Delay(100 * (attempt + 1)).ConfigureAwait(false);
        }
        return false;
    }

    private static void SetOrDelete(RegistryKey k, string name, string? value)
    {
        if (value == null)
        {
            k.DeleteValue(name, false);
        }
        else
        {
            k.SetValue(name, value, RegistryValueKind.String);
        }
    }

    private static void Notify(uint? connectionFlags = null)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RegPath);
        if (k == null) throw new IOException("Internet settings registry unavailable");
        var expected = ReadValues(k);
        var flags = connectionFlags ?? (ProxyTypeDirect
            | (expected.Enable == 1 ? ProxyTypeProxy : 0)
            | (!string.IsNullOrEmpty(expected.Pac) ? ProxyTypeAutoUrl : 0));

        // Registry ProxyEnable alone can disagree with DefaultConnectionSettings: Edge/CEF
        // then keep using DIRECT while the app reports success. Publish through WinINet too.
        ApplyNativeProxy(expected, flags);
        if (!SameNative(ReadNativeValues(), NativeFrom(expected, flags)))
            throw new IOException("Effective Windows proxy endpoint, bypass, PAC or mode did not match requested settings");
        if (!Matches(ReadValues(k), expected))
            throw new InvalidOperationException("Proxy owner changed during Windows notification");
    }

    private static uint ReadNativeFlags()
    {
        var optionSize = Marshal.SizeOf<InternetConnectionOption>();
        var buffer = Marshal.AllocHGlobal(optionSize);
        try
        {
            Marshal.StructureToPtr(new InternetConnectionOption { Option = 1 }, buffer, false);
            var options = new InternetConnectionOptions
            {
                Size = Marshal.SizeOf<InternetConnectionOptions>(), Count = 1, Options = buffer,
            };
            var size = options.Size;
            if (!QueryConnectionOptions(IntPtr.Zero, InternetOptionPerConnection, ref options, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Read Windows proxy flags failed");
            return Marshal.PtrToStructure<InternetConnectionOption>(buffer).Value.Flags;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static NativeProxyValues ReadNativeValues()
    {
        var optionSize = Marshal.SizeOf<InternetConnectionOption>();
        var buffer = Marshal.AllocHGlobal(optionSize * 4);
        var queried = false;
        try
        {
            for (var i = 0; i < 4; i++)
                Marshal.StructureToPtr(new InternetConnectionOption { Option = (uint)i + 1 }, IntPtr.Add(buffer, i * optionSize), false);
            var options = new InternetConnectionOptions
            {
                Size = Marshal.SizeOf<InternetConnectionOptions>(), Count = 4, Options = buffer,
            };
            var size = options.Size;
            if (!QueryConnectionOptions(IntPtr.Zero, InternetOptionPerConnection, ref options, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Read effective Windows proxy failed");
            queried = true;
            string? Text(int index) => Marshal.PtrToStringUni(
                Marshal.PtrToStructure<InternetConnectionOption>(IntPtr.Add(buffer, index * optionSize)).Value.Text);
            return new()
            {
                Flags = Marshal.PtrToStructure<InternetConnectionOption>(buffer).Value.Flags,
                Server = Text(1), Bypass = Text(2), Pac = Text(3),
            };
        }
        finally
        {
            if (queried)
                for (var i = 1; i < 4; i++)
                {
                    var pointer = Marshal.PtrToStructure<InternetConnectionOption>(IntPtr.Add(buffer, i * optionSize)).Value.Text;
                    if (pointer != IntPtr.Zero) GlobalFree(pointer);
                }
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr memory);

    private static void ApplyNativeProxy(ProxyValues values, uint flags)
    {
        var strings = new List<IntPtr>();
        var buffer = IntPtr.Zero;
        try
        {
            var entries = new List<InternetConnectionOption>
            {
                new() { Option = 1, Value = new() { Flags = flags } },
            };
            void AddText(uint option, string? text)
            {
                // Empty strings clear dormant native PAC/server/bypass values too.
                var pointer = Marshal.StringToHGlobalUni(text ?? "");
                strings.Add(pointer);
                entries.Add(new() { Option = option, Value = new() { Text = pointer } });
            }
            AddText(2, values.Server);
            AddText(3, values.Bypass);
            AddText(4, values.Pac);
            var optionSize = Marshal.SizeOf<InternetConnectionOption>();
            buffer = Marshal.AllocHGlobal(optionSize * entries.Count);
            for (var i = 0; i < entries.Count; i++)
                Marshal.StructureToPtr(entries[i], IntPtr.Add(buffer, i * optionSize), false);
            var options = new InternetConnectionOptions
            {
                Size = Marshal.SizeOf<InternetConnectionOptions>(), Count = entries.Count, Options = buffer,
            };
            if (!SetConnectionOptions(IntPtr.Zero, InternetOptionPerConnection, ref options, options.Size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Apply Windows proxy settings failed");
            if (!InternetSetOption(IntPtr.Zero, InternetOptionProxySettingsChanged, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Notify Windows proxy settings failed");
            InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            foreach (var pointer in strings) Marshal.FreeHGlobal(pointer);
        }
    }

    /// 从本机 TCP 监听表确认端口归属。TUN/WFP 可以拒绝 IPv6 回环连接，即使端口仍在监听；
    /// 主动连接失败不能证明代理已退出。无法读取监听表时保守保留代理，避免清掉仍有所有者的设置。
    private static bool IsPortListening(int port)
    {
        try
        {
            foreach (var endpoint in System.Net.NetworkInformation.IPGlobalProperties
                .GetIPGlobalProperties().GetActiveTcpListeners())
            {
                if (endpoint.Port == port
                    && (System.Net.IPAddress.IsLoopback(endpoint.Address)
                        || endpoint.Address.Equals(System.Net.IPAddress.Any)
                        || endpoint.Address.Equals(System.Net.IPAddress.IPv6Any)))
                    return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            LogBus.App("Proxy listener ownership check failed; cleanup skipped: " + ex.Message, "Warning");
            return true;
        }
    }

    /// 代理串是否指向本机 neo 端口。ProxyServer 可能是 "127.0.0.1:10808"，
    /// 也可能是分协议形式（"http=127.0.0.1:10808;https=..."），两种都认。
    private static bool PointsToNeoPort(string? server, int port)
    {
        if (string.IsNullOrWhiteSpace(server)) return false;
        foreach (var part in server.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var s = part.Contains('=') ? part[(part.IndexOf('=') + 1)..] : part;
            s = s.Trim();
            if (s.Equals($"127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase) ||
                s.Equals($"localhost:{port}", StringComparison.OrdinalIgnoreCase) ||
                s.Equals($"[::1]:{port}", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// 清理「孤儿系统代理」：neo 被强杀或崩溃时 Disable() 不会执行，
    /// 注册表残留 ProxyEnable=1 指向本机端口，但已无进程监听 → 所有走系统代理的程序全部断网，
    /// 且报错五花八门，极易被误判成网络故障。
    /// 判据必须包含「该端口无人监听」这一条：若其它代理客户端正占用同端口，
    /// 那是它自己的合法设置，绝不能动。
    public static bool CleanupOrphaned(params int[] neoPorts)
    {
        lock (ProxyGate)
        {
            try
            {
                using var transaction = new RegistryLease();
                using var k = Registry.CurrentUser.OpenSubKey(RegPath);
                if (k == null) return false;
                var values = ReadValues(k);
                var native = ReadNativeValues();
                var registryActive = values.Enable == 1;
                var nativeActive = (native.Flags & ProxyTypeProxy) != 0;
                // A disabled registry flag does not prove Windows stopped using the dead proxy.
                // Validate BOTH configurations, and never overwrite an active foreign proxy/PAC.
                if (!registryActive && !nativeActive) return false;
                if (!string.IsNullOrEmpty(values.Pac) || !string.IsNullOrEmpty(native.Pac)
                    || (native.Flags & (ProxyTypeAutoUrl | ProxyTypeAutoDetect)) != 0
                    || (registryActive && !LocalProxySettings.IsOwnedServer(values.Server, neoPorts))
                    || (nativeActive && !LocalProxySettings.IsOwnedServer(native.Server, neoPorts))) return false;
                var matched = false;
                foreach (var port in neoPorts)
                {
                    if (!(registryActive && PointsToNeoPort(values.Server, port))
                        && !(nativeActive && PointsToNeoPort(native.Server, port))) continue;
                    matched = true;
                    // Every referenced endpoint must be dead, not just the first SOCKS/HTTP entry.
                    if (IsPortListening(port)) return false;
                }
                return matched && Disable(force: true);
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// 清理「异常退出留下的快照」：neo 被强杀时 Disable() 没执行，data\proxy-backup.json 会一直留着。
    /// 若此时系统代理已被关掉（ProxyEnable=0），这份快照既不会被 Disable 用掉，
    /// 也不会被 CleanupOrphaned 处理（后者要求代理是开着的）→ 它会一直躺着，
    /// 并在下次连接/断开时被当成「用户原设置」还原，埋下隐患。
    /// 判据：快照存在 且 当前系统代理未启用 → 还原快照并删除文件。
    /// 正在接管中（ProxyEnable=1）时不动，交给 CleanupOrphaned / 正常 Disable。
    /// </summary>
    public static bool CleanupStaleSnapshot()
    {
        lock (ProxyGate)
        {
            if (!File.Exists(BackupPath) || IsEnabled) return false;
            return Disable();
        }
    }
}
