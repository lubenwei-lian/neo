using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Neo.App;

public static class ThemeUtil
{
    public static Brush Get(string key, Color fallback)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush)
            return brush;
        return new SolidColorBrush(fallback);
    }

    public static readonly Color LightAccent = Color.FromArgb(255, 0x25, 0x63, 0xEB);
    public static readonly Color DarkAccent = Color.FromArgb(255, 0x4C, 0xC2, 0xFF);
    public static readonly Color LightText1 = Color.FromArgb(255, 0x1A, 0x1D, 0x21);
    public static readonly Color LightText2 = Color.FromArgb(255, 0x6B, 0x72, 0x80);
    public static readonly Color DarkText1 = Color.FromArgb(255, 0xF2, 0xF3, 0xF5);
    public static readonly Color DarkText2 = Color.FromArgb(255, 0x9A, 0xA1, 0xAC);

    public static bool IsDark(FrameworkElement el) => el.ActualTheme == ElementTheme.Dark;

    public static Brush Accent(FrameworkElement el) => Get("NeoAccent", IsDark(el) ? DarkAccent : LightAccent);
    public static Brush Text2(FrameworkElement el) => Get("NeoText2", IsDark(el) ? DarkText2 : LightText2);
}
