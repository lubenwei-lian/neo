namespace Neo.Core.Presentation;

/// <summary>Bounded, real traffic samples owned by the host, independent of any renderer.</summary>
public sealed class TrafficSampleBuffer
{
    public sealed record Sample(long Id, long SampledAt, double UpBps, double DownBps);
    public sealed record Snapshot(int Session, Sample[] Samples, double PeakBps);
    private readonly List<Sample> _samples = new(80);
    private int _session = -1, _pid;
    private long _up, _down, _ticks, _id;
    private double _peak;

    // Caller serializes access together with the sampler's latest counters.
    public void Record(int session, int pid, long up, long down, DateTime at)
    {
        if (_session != session || _pid != pid) { Clear(); _session = session; _pid = pid; }
        if (at.Ticks <= _ticks) return;
        var seconds = (at.Ticks - _ticks) / (double)TimeSpan.TicksPerSecond;
        if (_ticks != 0 && seconds < 10 && up >= _up && down >= _down)
        {
            var sample = new Sample(++_id, new DateTimeOffset(at).ToUnixTimeMilliseconds(),
                (up - _up) / seconds, (down - _down) / seconds);
            _samples.Add(sample);
            _peak = Math.Max(_peak, Math.Max(sample.UpBps, sample.DownBps));
        }
        _up = up; _down = down; _ticks = at.Ticks;
        var edge = new DateTimeOffset(at).ToUnixTimeMilliseconds() - 68000;
        // Keep one off-screen point, with both a time and a hard capacity bound.
        while (_samples.Count > 2 && _samples[1].SampledAt < edge) _samples.RemoveAt(0);
        if (_samples.Count > 256) _samples.RemoveRange(0, _samples.Count - 256);
    }

    public Snapshot Capture(int session, int pid) => _session == session && _pid == pid
        ? new(session, _samples.ToArray(), _peak) : new(session, [], 0);

    public void Clear()
    {
        _samples.Clear(); _session = -1; _pid = 0;
        _up = _down = _ticks = _id = 0; _peak = 0;
    }
}
