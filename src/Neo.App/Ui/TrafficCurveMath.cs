namespace Neo.App.Ui;

internal static class TrafficCurveMath
{
    /// <summary>Harmonic mean tangents preserve extrema and monotonic intervals.</summary>
    internal static (double First, double Second) Controls(double before, double from, double to, double after)
    {
        static double Slope(double a, double b) => a * b <= 0 ? 0 : 2 * a * b / (a + b);
        var delta = to - from;
        return (from + Slope(from - before, delta) / 3, to - Slope(delta, after - to) / 3);
    }
}
