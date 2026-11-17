using Neo.Core;
using Neo.Core.Core;

namespace Neo.App.Services;

public static class CoreStats
{
    public readonly record struct Snapshot(long Uplink, long Downlink);
    private sealed class Instance(CoreProcess core, string kind, int statsPort, int clashPort, int userPort)
    {
        public readonly object Gate = new();
        public readonly CoreProcess Core = core;
        public readonly string Kind = kind;
        public readonly int StatsPort = statsPort, ClashPort = clashPort, UserPort = userPort;
        public long Up, Down;
        public int Active;
        public bool Finished;
    }
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<CoreProcess, Instance> Instances = new();
    private static readonly object TotalsGate = new();
    private static long _up, _down;

    internal static void Register(CoreProcess core, string kind, int statsPort, int clashPort, int userPort)
        => Instances.TryAdd(core, new Instance(core, kind, statsPort, clashPort, userPort));

    private static bool Read(Instance instance)
    {
        if (!instance.Core.IsRunning) return false;
        Snapshot? snapshot;
        if (instance.Kind is "sing-box" or "mihomo" or "naive")
        {
            var s = SingBoxStats.Query(instance.ClashPort);
            snapshot = s is { } v ? new(v.Uplink, v.Downlink) : null;
            if (s is { } active) instance.Active = active.ActiveConnections;
        }
        else snapshot = XrayStats.Query(instance.StatsPort) is { } x ? new(x.Uplink, x.Downlink) : null;
        if (snapshot is not { } value) return false;
        // New instances start at zero. A failed read never advances their baseline.
        var up = value.Uplink >= instance.Up ? value.Uplink - instance.Up : value.Uplink;
        var down = value.Downlink >= instance.Down ? value.Downlink - instance.Down : value.Downlink;
        instance.Up = value.Uplink; instance.Down = value.Downlink;
        lock (TotalsGate) { _up += Math.Max(0, up); _down += Math.Max(0, down); }
        UsageTracker.AddDelta(Math.Max(0, up) + Math.Max(0, down));
        return true;
    }

    internal static void Finish(CoreProcess core)
    {
        if (!Instances.TryGetValue(core, out var instance)) return;
        lock (instance.Gate)
        {
            if (instance.Finished) return;
            try { Read(instance); }
            catch (Exception ex) { LogBus.App("Final traffic sample failed: " + ex.Message, "Warning"); }
            try
            {
                if (!core.StopAndVerify()) LogBus.App("Core did not exit within its shutdown budget", "Warning");
            }
            finally
            {
                instance.Finished = true;
                Instances.TryRemove(core, out _);
            }
        }
    }

    public static Snapshot? Query()
    {
        var any = false;
        foreach (var instance in Instances.Values)
        {
            lock (instance.Gate)
            {
                if (instance.Finished) continue;
                try { any |= Read(instance); } catch { }
            }
        }
        lock (TotalsGate) return any ? new Snapshot(_up, _down) : null;
    }

    public static int ActiveConnections()
    {
        // Xray relays share user ingress; count once rather than once per retiring instance.
        var clash = Instances.Values.Where(i => i.Kind is "sing-box" or "mihomo" or "naive").Sum(i => i.Active);
        return clash + Instances.Values.Where(i => i.Kind == "xray")
            .Select(i => i.UserPort).Distinct().Sum(TcpConnCounter.CountEstablished);
    }
}
