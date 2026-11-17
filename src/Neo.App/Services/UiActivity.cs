namespace Neo.App.Services;

/// <summary>Presentation visibility only; active proxy transport and health checks remain independent.</summary>
public static class UiActivity
{
    private static int _visible = 1;
    private static readonly object Gate = new();
    private static CancellationTokenSource? _visibleLifetime = new();
    public static bool IsVisible => Volatile.Read(ref _visible) != 0;
    public static void SetVisible(bool visible)
    {
        CancellationTokenSource? retired = null;
        lock (Gate)
        {
            if (visible == IsVisible) return;
            Volatile.Write(ref _visible, visible ? 1 : 0);
            if (visible) _visibleLifetime = new();
            else { retired = _visibleLifetime; _visibleLifetime = null; }
        }
        // Cancel outside the gate: socket callbacks must never run under our lock.
        try { retired?.Cancel(); } catch (AggregateException) { }
        finally { retired?.Dispose(); }
    }

    /// <summary>Only optional automatic probes use this lifetime; never the active proxy core.</summary>
    public static CancellationTokenSource CreateOptionalProbeCancellation(CancellationToken caller)
    {
        lock (Gate)
            return CancellationTokenSource.CreateLinkedTokenSource(caller,
                _visibleLifetime?.Token ?? new CancellationToken(canceled: true));
    }
}
