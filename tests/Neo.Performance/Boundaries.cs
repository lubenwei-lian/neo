namespace Neo.App.Services
{
    public static class AppState { public static TestSettings State { get; } = new(); }
    public sealed class TestSettings { public int LogRetentionDays = 7; public string LogLevel = "warning"; }
}
namespace Microsoft.UI { }
namespace Windows.UI { public struct Color { public static Color FromArgb(byte a, byte r, byte g, byte b) => new(); } }
namespace Microsoft.UI.Xaml
{
    public class Application
    {
        public static Application Current { get; } = new();
        public Dictionary<string, object> Resources { get; } = new();
    }
}
namespace Microsoft.UI.Xaml.Media
{
    public class Brush { }
    public class SolidColorBrush(Windows.UI.Color color) : Brush { }
}
