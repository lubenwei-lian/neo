using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Neo.App.Ui;

/// 冷缘玻璃卡片。
/// 采用 ContentControl + 自定义模板，模板内全部用 Border 绘制：
///   · 底座 Border 自带 CornerRadius，BorderBrush 沿圆角走 ⇒ 描边天然是圆的
///   · 顶刻线 / 底内光是只画单边的 Border，圆角与底座一致 ⇒ 不会盖住圆角
///   · 不使用任何 Rectangle + 手工 Margin 避让，也不需要 Composition 裁剪
/// 这样每张卡只依赖自己的 CornerRadius，改一处不会影响其他卡片。
/// 重要：模板里 CornerRadius 必须用
///   {Binding CornerRadius, RelativeSource={RelativeSource TemplatedParent}}
/// 而不是 {TemplateBinding CornerRadius} ——
/// 后者对**自定义**依赖属性会静默失效（圆角恒为 0，卡片变直角），
public sealed class CoolGlassCard : ContentControl
{
    /// <summary>卡片圆角。默认 26（设计稿 NeoPanelRadius）。每张卡可独立覆盖。</summary>
    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(CoolGlassCard),
            new PropertyMetadata(new CornerRadius(26)));

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public CoolGlassCard()
    {
        DefaultStyleKey = typeof(CoolGlassCard);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;

        // 1px 高的装饰边框必须落在整像素上，否则滚动时会跨两个物理像素、
        // 亮度各半，表现为「顶部亮条忽亮忽暗地抖」。
        UseLayoutRounding = true;
    }
}
