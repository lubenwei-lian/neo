namespace Neo.Core.Core;

/// <summary>进程内、有限且会过期的节点质量证据。键由调用方的配置身份提供，不记录凭据。</summary>
public sealed class NodeQualityHistory(TimeProvider? timeProvider = null)
{
    public static NodeQualityHistory Shared { get; } = new();
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(20);
    private const int MaxEntries = 2048;
    private sealed class Entry
    {
        public DateTimeOffset LastSeen;
        public Queue<(DateTimeOffset At, int Ms)> Latencies { get; } = new();
        public Queue<(DateTimeOffset At, bool Ok)> Health { get; } = new();
        public (DateTimeOffset At, double Bps)? Throughput;
    }

    public void RecordLatency(string key, int milliseconds)
    {
        // -2 是本机测速未运行，不得把它当成远端故障。
        if (milliseconds == 0 || milliseconds < -1) return;
        lock (_gate)
        {
            var entry = GetOrAdd(key); var now = _clock.GetUtcNow();
            entry.Health.Enqueue((now, milliseconds > 0)); Trim(entry.Health);
            if (milliseconds > 0) { entry.Latencies.Enqueue((now, milliseconds)); Trim(entry.Latencies); }
        }
    }

    public void RecordHealth(string key, bool healthy)
    {
        lock (_gate) { var entry = GetOrAdd(key); entry.Health.Enqueue((_clock.GetUtcNow(), healthy)); Trim(entry.Health); }
    }

    public void RecordThroughput(string key, double bytesPerSecond)
    {
        if (!double.IsFinite(bytesPerSecond) || bytesPerSecond < 0) return;
        lock (_gate) GetOrAdd(key).Throughput = (_clock.GetUtcNow(), bytesPerSecond);
    }

    public double Score(string key, int fallbackDelay)
    {
        lock (_gate)
        {
            var baseline = fallbackDelay > 0 ? fallbackDelay : fallbackDelay == 0 ? 100_000 : 200_000;
            if (!_entries.TryGetValue(key, out var entry)) return baseline;
            var cutoff = _clock.GetUtcNow() - Lifetime;
            if (entry.LastSeen < cutoff) { _entries.Remove(key); return baseline; }
            var latencies = entry.Latencies.Where(x => x.At >= cutoff).Select(x => (double)x.Ms).Order().ToArray();
            var center = latencies.Length == 0 ? baseline : latencies[latencies.Length / 2];
            var jitter = latencies.Length < 2 ? 0 : latencies.Average(x => Math.Abs(x - center));
            var health = entry.Health.Where(x => x.At >= cutoff).ToArray();
            var failurePenalty = 2000.0 * health.Count(x => !x.Ok) / (health.Length + 2);
            // 小样本下载只用来惩罚明显劣化，不以它推断节点峰值带宽或奖励极高读数。
            var throughputPenalty = entry.Throughput is { } t && t.At >= cutoff && t.Bps < 30 * 1024 ? 2000 : 0;
            return center + jitter + failurePenalty + throughputPenalty;
        }
    }

    public void Clear() { lock (_gate) _entries.Clear(); }
    public int Count { get { lock (_gate) return _entries.Count; } }
    private Entry GetOrAdd(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var now = _clock.GetUtcNow();
        if (!_entries.TryGetValue(key, out var entry))
        {
            if (_entries.Count >= MaxEntries)
                _entries.Remove(_entries.MinBy(x => x.Value.LastSeen).Key);
            _entries.Add(key, entry = new Entry());
        }
        entry.LastSeen = now; return entry;
    }
    private static void Trim<T>(Queue<T> values) { while (values.Count > 12) values.Dequeue(); }
}
