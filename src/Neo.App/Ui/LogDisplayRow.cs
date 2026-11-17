using Microsoft.UI.Xaml.Media;
using Neo.App.Services;
using Windows.UI;

namespace Neo.App.Ui;

// Display projection only. Streaming, retention and export continue to use LogEntry.
public sealed class LogDisplayRow
{
    public string TimeText { get; }
    public string LevelText { get; }
    public string HeadText { get; }
    public Brush HeadBrush { get; }
    public Brush BadgeBackground { get; }

    public LogDisplayRow(LogEntry entry)
    {
        TimeText = entry.Time.ToString("HH:mm:ss");
        HeadText = entry.Text;
        LevelText = entry.Level switch { "Error" => "错误", "Warning" => "警告", "Debug" => "调试", _ => "信息" };
        var key = entry.Level switch { "Error" => "NeoRed", "Warning" => "NeoYellow", "Debug" => "NeoText2", _ => "NeoAccent" };
        HeadBrush = ThemeUtil.Get(key, Color.FromArgb(255, 90, 169, 255));
        var color = HeadBrush is SolidColorBrush solid ? solid.Color : Color.FromArgb(255, 90, 169, 255);
        BadgeBackground = new SolidColorBrush(Color.FromArgb(28, color.R, color.G, color.B));
    }
}
