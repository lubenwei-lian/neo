using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Neo.App;

/// 「玻璃透明度」滑杆的实装。
///   NeoPanel（NeoCard/NeoGlassPanel 的实色底）与 NeoCoolSurface
///   （CoolGlassCard 的渐变底）—— 用户拖滑杆时主卡片纹丝不动，文案却写
///   「卡片背景模糊与透光强度」，名不副实。现在三类都调：
///     · NeoGlass*     纯色（按钮/轨道等小元素，沿用原公式）
///     · NeoPanel      纯色（卡片/面板底色，alpha 直接缩放）
///     · NeoCoolSurface 渐变（冷缘卡片底色，只缩 GradientStop 的 alpha）
/// 视觉一致性取舍：渐变底的基准 alpha 只有 ~0x26，若把左半程的放大系数
/// 一起用于它，会得到近乎实色的浅蓝面板（与中性玻璃观感冲突），故渐变只向「更透」
/// 方向生效（系数上限 1.0）；纯色面板 alpha=255，本来也无法更实。
/// 默认值 78 时系数正好 1.0 = 设计稿原样，启动观感不变。
public static class GlassTuner
{
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

    public static void Apply(int percent)
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
