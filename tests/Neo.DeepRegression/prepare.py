"""Bind acceptance tests to current source; never load installed state or modify the live app."""
from pathlib import Path
import hashlib
import json

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
APP = ROOT / 'src/Neo.App/Services'

def method(source, signature):
    start = source.index(signature)
    if signature.endswith('DisconnectIfCurrent('):
        end = source.index(';', start) + 1
    elif signature.endswith('RunTunTeardownAsync('):
        end = source.index('\n    });', start) + len('\n    });')
    else:
        end = source.index('\n    }', start) + len('\n    }')
    return source[start:end]

source = (APP / 'ConnectionService.cs').read_text(encoding='utf-8-sig')
signatures = [
    '    public static Task<(bool Ok, string? Error)> ConnectAsync(',
    '    private static async Task<(bool Ok, string? Error)> ConnectSessionAsync(',
    '    private static Task ApplyTlsPolicyAsync(',
    '    public static Task<(bool Ok, string? Err)> SwitchNodeFastAsync(',
    '    private static async Task<(bool Ok, string? Err)> SwitchNodeFastCoreAsync(',
    '    public static void Disconnect(',
    '    internal static bool DisconnectIfCurrent(',
    '    private static bool ResetSession(',
    '    private static void ReleaseCore(',
    '    private static bool IsActiveCorePid(',
    '    private static void OnCoreExited(',
    '    private static void StartBackgroundTunTeardown(',
    '    private static Task RunTunTeardownAsync(',
    '    private static async Task TryAutoMeasureDelayAsync(',
    '    private static bool PortFree(',
    '    private static async Task<string> EnsureCompatibleConfigAsync(',
    '    private static void RecordPreflightFailure(',
    '    private static void LogAdaptNotes(',
    '    private static void RecordAdaptation(',
]
bodies = [method(source, s) for s in signatures]
bound_bodies = [body.replace(' ConnectAsync(', ' RunPortRecovery(', 1)
                if ' ConnectAsync(' in signature else body.replace(' PortFree(', ' RealPortFree(', 1)
                if ' PortFree(' in signature else body
                for signature, body in zip(signatures, bodies)]
listener_constant = next(line for line in source.splitlines() if 'private const string ListenerBindFailure =' in line)
(HERE / 'ConnectionUnderTest.g.cs').write_text('''#nullable enable
using System.Diagnostics;
using Neo.Core;
using Neo.Core.Core;
using Neo.Core.Models;
namespace Neo.App.Services;
public static partial class ConnectionService
{
''' + listener_constant + '\n' + '\n\n'.join(bound_bodies) + '\n}\n', encoding='utf-8')

proxy = (APP / 'SystemProxy.cs').read_text(encoding='utf-8-sig')
proxy = proxy.replace('using Microsoft.Win32;', '''using Microsoft.Win32;
using Registry = Neo.DeepAudit.MemoryRegistry;
using RegistryKey = Neo.DeepAudit.MemoryRegistryKey;''')
proxy = proxy.replace('public static class SystemProxy', 'public static class ProxyOwnershipUnderTest')
native_read = method(proxy, '    private static uint ReadNativeFlags(')
proxy = proxy.replace(native_read, '    private static uint ReadNativeFlags() { return ProxyNativeBoundary.Flags; }')
native_values = method(proxy, '    private static NativeProxyValues ReadNativeValues(')
proxy = proxy.replace(native_values, '''    private static NativeProxyValues ReadNativeValues()
    {
        if (ProxyNativeBoundary.RejectRead) throw new IOException("synthetic WinINet read failure");
        return new() { Flags = ProxyNativeBoundary.Flags, Server = ProxyNativeBoundary.Server,
            Bypass = ProxyNativeBoundary.Bypass, Pac = ProxyNativeBoundary.Pac };
    }''')
native_owner = method(proxy, '    private static bool IsLiveProxyOwner(')
proxy = proxy.replace(native_owner, '''    private static bool IsLiveProxyOwner(ProxyBackup backup)
    {
        return backup.OwnerPid > 0 && backup.OwnerPid != Environment.ProcessId && ProxyNativeBoundary.ForeignOwnerAlive;
    }''')
native_apply = method(proxy, '    private static void ApplyNativeProxy(')
proxy = proxy.replace(native_apply, '''    private static void ApplyNativeProxy(ProxyValues values, uint flags)
    {
        ProxyNativeBoundary.ApplyAttempts++;
        if (ProxyNativeBoundary.RejectApply || ProxyNativeBoundary.RejectApplyCount-- > 0) throw new IOException("synthetic WinINet apply failure");
        if (ProxyNativeBoundary.IgnoreApply) return;
        ProxyNativeBoundary.Flags = flags;
        ProxyNativeBoundary.Server = ProxyNativeBoundary.AlteredServer ?? values.Server;
        ProxyNativeBoundary.Bypass = values.Bypass;
        ProxyNativeBoundary.Pac = values.Pac;
        Registry.CurrentUser.Values["ProxyEnable"] = (flags & 2) != 0 ? 1 : 0;
        Registry.CurrentUser.Values["ProxyServer"] = values.Server ?? "";
        Registry.CurrentUser.Values["ProxyOverride"] = values.Bypass ?? "";
        Registry.CurrentUser.Values["AutoConfigURL"] = values.Pac ?? "";
        ProxyNativeBoundary.Applications++;
    }''')
(HERE / 'ProxyUnderTest.g.cs').write_text('#nullable enable\n' + proxy, encoding='utf-8')

throughput = method((APP / 'FailoverService.cs').read_text(encoding='utf-8-sig'),
    '    private static async Task<double> MeasureThroughputAsync(')
adapted = throughput.replace('https://speed.cloudflare.com/__down?bytes=131072',
    'http://throughput.audit.invalid/__down?bytes=131072')
(HERE / 'ThroughputUnderTest.g.cs').write_text('''#nullable enable
using System.Net;
using System.Net.Http;
using Neo.Core;
namespace Neo.App.Services;
public static class ThroughputUnderTest
{
    public static Task<double> Run(CancellationToken ct) => MeasureThroughputAsync(ct);
''' + adapted + '\n}\n', encoding='utf-8')

state = (APP / 'CoreState.cs').read_text(encoding='utf-8-sig')
state = state.replace('public static class CoreState', 'public static class OrderedCoreStateUnderTest')
state = state.replace(method(state, '    private static void Signal('), '    private static void Signal() { }')
(HERE / 'CoreStateUnderTest.g.cs').write_text('#nullable enable\n' + state, encoding='utf-8')

guard_source = (APP / 'GuardMode.cs').read_text(encoding='utf-8-sig')
guard_signatures = [
    '    private static void DoCleanup(',
    '    private static bool ShouldDeferCleanup(',
    '    private static void SyncCore(',
]
guard_bodies = [method(guard_source, signature) for signature in guard_signatures]
(HERE / 'GuardUnderTest.g.cs').write_text('''#nullable enable
using System.Diagnostics;
namespace Neo.App.Services;
public static partial class GuardUnderTest
{
''' + '\n\n'.join(guard_bodies) + '\n}\n', encoding='utf-8')

manifest = {
    'repository': str(ROOT),
    'connection_methods': [{'signature': s.strip(), 'sha256': hashlib.sha256(b.encode()).hexdigest()}
        for s, b in zip(signatures, bodies)],
    'guard_methods': [{'signature': s.strip(), 'sha256': hashlib.sha256(b.encode()).hexdigest()}
        for s, b in zip(guard_signatures, guard_bodies)],
    'files': {str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest()
        for p in list(APP.glob('*.cs')) + list((ROOT / 'src/Neo.Core').rglob('*.cs'))
        if not any(part in ('bin', 'obj') for part in p.relative_to(ROOT).parts)},
    'boundaries': 'Six full service files and current Core project; sixteen exact connection methods, including full ConnectSessionAsync. '
        'Process, relay, UI, network probes and stats counters are synthetic. Full SystemProxy source '
        'uses in-memory registry aliases and controlled native flag read/apply boundaries; real Notify is retained. Throughput changes only its target URL. '
        'TUN OS removal is a controlled barrier. No live registry, adapter or installed process changes.',
    'throughput_sha256': hashlib.sha256(throughput.encode()).hexdigest(),
}
(HERE / 'source-binding.json').write_text(json.dumps(manifest, indent=2, ensure_ascii=False), encoding='utf-8')

