using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Neo.App;

/// 界面观感的固定实装（主题设置已移除，不再提供滑杆）。
///   固定采用原「玻璃透明度」滑杆的 100% 档 —— 也就是用户最后看到的那套观感，
///   启动后不再随任何设置变化：
///     · NeoGlass*     纯色（按钮/轨道等小元素）
///     · NeoPanel      纯色（卡片/面板底色）
///     · NeoCoolSurface 渐变（冷缘卡片底色，只缩 GradientStop 的 alpha）
///   两类系数都是 alpha × m（上限 255）；渐变基准 alpha 只有 ~0x26，
///   若一并放大反而会变成近乎实色的浅蓝面板，故渐变只向「更透」方向生效（系数上限 1.0）。
/// 以后若要整体调「更实 / 更透」，只改 FixedPercent 一个数（0-100，越大越透）。
public static class GlassTuner
{
    /// 固定档位：原滑杆取值 0-100，数值越大越透。100 = 当前线上观感。
    public const int FixedPercent = 100;

    private static readonly string[] Keys =
    {
        "NeoGlass", "NeoGlassStrong", "NeoGlass2", "NeoGlass3", "NeoGlassBorder",
        "NeoPanel",
    };

    private static readonly string[] GradientKeys =
    {
        "NeoCoolSurface",
    };

    private static readonly List<(SolidColorBrush Brush, byte Alpha)> _orig = new();
    private static readonly List<(GradientStop Stop, byte Alpha)> _stops = new();
    private static bool _collected;

    /// 启动时调用一次即可（MainWindow 初始化时）。
    public static void ApplyFixed() => Apply(FixedPercent);

    private static void Apply(int percent)
    {
        Collect();
        double m = Math.Max(0.15, (100.0 - percent) / 22.0);

        // 纯色：alpha 直接乘系数（上限 255）。
        foreach (var (brush, alpha) in _orig)
        {
            var c = brush.Color;
            var a = (byte)Math.Round(Math.Min(255.0, alpha * m));
            brush.Color = Color.FromArgb(a, c.R, c.G, c.B);
        }

        // 渐变停靠点：只允许变透（系数上限 1.0，理由见类注释）。
        double mg = Math.Min(1.0, m);
        foreach (var (stop, alpha) in _stops)
        {
            var c = stop.Color;
            var a = (byte)Math.Round(Math.Min(255.0, alpha * mg));
            stop.Color = Color.FromArgb(a, c.R, c.G, c.B);
        }
    }

    private static void Collect()
    {
        if (_collected) return;
        _collected = true;
        try { Scan(Application.Current.Resources); } catch { }
    }

    private static void Scan(ResourceDictionary dict)
    {
        try
        {
            foreach (var key in dict.ThemeDictionaries.Keys)
            {
                if (dict.ThemeDictionaries[key] is not ResourceDictionary inner) continue;
                foreach (var k in Keys)
                {
                    if (inner.TryGetValue(k, out var v) && v is SolidColorBrush b &&
                        !_orig.Any(o => ReferenceEquals(o.Brush, b)))
                        _orig.Add((b, b.Color.A));
                }

                foreach (var k in GradientKeys)
                {
                    if (inner.TryGetValue(k, out var v) && v is LinearGradientBrush g)
                    {
                        foreach (var stop in g.GradientStops)
                        {
                            if (!_stops.Any(o => ReferenceEquals(o.Stop, stop)))
                                _stops.Add((stop, stop.Color.A));
                        }
                    }
                }
            }
        }
        catch { }

        foreach (var merged in dict.MergedDictionaries)
            Scan(merged);
    }
}
