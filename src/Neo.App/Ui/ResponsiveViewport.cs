namespace Neo.App.Ui;

/// <summary>Window dimensions are effective pixels: Windows DPI is already applied.</summary>
internal static class ResponsiveViewport
{
    internal static double TextScale(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            return 1;
        // A wide but short window must retain the compact layout. Large desktop
        // windows get readable text without multiplying the system DPI twice.
        var available = Math.Min(width / 1200d, height / 800d);
        return Math.Clamp(Math.Round(available * 40) / 40, 1, 1.6);
    }

    internal static double FontSize(double baseSize, double scale)
        => Math.Round(baseSize * scale * 4) / 4;
}
