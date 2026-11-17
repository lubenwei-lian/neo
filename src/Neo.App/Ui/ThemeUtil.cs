using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Neo.App;

public static class ThemeUtil
{
    public static Brush Get(string key, Color fallback)
    {
        var theme = (App.MainWindowInstance?.Content as FrameworkElement)?.ActualTheme
            ?? (Application.Current.RequestedTheme == ApplicationTheme.Dark ? ElementTheme.Dark : ElementTheme.Light);
        Brush? Find(ResourceDictionary dictionary)
        {
            if (dictionary.ThemeDictionaries.TryGetValue(theme.ToString(), out var themed) && themed is ResourceDictionary selected && Find(selected) is { } brush) return brush;
            if (dictionary.ThemeDictionaries.TryGetValue("Default", out var common) && common is ResourceDictionary defaults && Find(defaults) is { } defaultBrush) return defaultBrush;
            for (var i = dictionary.MergedDictionaries.Count - 1; i >= 0; i--)
                if (Find(dictionary.MergedDictionaries[i]) is { } merged) return merged;
            // A top-level TryGetValue also traverses merged theme dictionaries using
            // Application.RequestedTheme, which can differ from this window's theme.
            // Prefer the explicitly selected dictionary before that implicit fallback.
            if (dictionary.TryGetValue(key, out var value) && value is Brush direct) return direct;
            return null;
        }
        return Find(Application.Current.Resources) ?? new SolidColorBrush(fallback);
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
