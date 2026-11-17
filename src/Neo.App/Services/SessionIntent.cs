namespace Neo.App.Services;

/// <summary>A decision keeps its original authority; only its own transitions may advance it.</summary>
public sealed class SessionIntent
{
    private int _epoch;
    internal SessionIntent(int epoch) => _epoch = epoch;
    public int Epoch => Volatile.Read(ref _epoch);
    internal void Advance(int expected, int next) => Interlocked.CompareExchange(ref _epoch, next, expected);
}
