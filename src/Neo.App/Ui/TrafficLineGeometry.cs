using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Neo.App.Ui;

/// <summary>Native, bounded, shape-preserving curves over real traffic samples.</summary>
internal static class TrafficLineGeometry
{
    internal static PathGeometry Build(IReadOnlyList<double> values, int start, double width, double height, double ceiling, bool fill)
    {
        var geometry = new PathGeometry();
        var count = values.Count - start;
        if (count < 2 || width <= 0 || height <= 0) return geometry;
        var top = height * .06;
        var bottom = Math.Max(top, height);
        var step = width / Math.Max(1, count - 1);
        var firstX = width - step * (count - 1);
        double Y(int i) => bottom - Math.Clamp(values[start + i] / ceiling, 0, 1) * (bottom - top);
        var figure = new PathFigure { StartPoint = new Point(firstX, Y(0)), IsClosed = fill, IsFilled = fill };
        for (var i = 0; i < count - 1; i++)
        {
            var y0 = Y(i); var y1 = Y(i + 1); var delta = y1 - y0;
            // Harmonic mean slopes flatten extrema and cannot overshoot a sample interval.
            var before = i == 0 ? y0 - delta : Y(i - 1);
            var after = i + 2 == count ? y1 + delta : Y(i + 2);
            var control = TrafficCurveMath.Controls(before, y0, y1, after);
            figure.Segments.Add(new BezierSegment
            {
                Point1 = new Point(firstX + step * (i + 1d / 3), control.First),
                Point2 = new Point(firstX + step * (i + 2d / 3), control.Second),
                Point3 = new Point(firstX + step * (i + 1), y1)
            });
        }
        if (fill)
        {
            figure.Segments.Add(new LineSegment { Point = new Point(width, bottom) });
            figure.Segments.Add(new LineSegment { Point = new Point(firstX, bottom) });
        }
        geometry.Figures.Add(figure);
        return geometry;
    }
}
