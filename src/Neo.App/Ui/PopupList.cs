using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace Neo.App.Ui;

/// Native anchored menu. Appearance and single 130ms entrance follow styles.css
/// plus surface.css. Flyout owns positioning, light dismissal and focus.
public sealed class PopupList
{
    private readonly StackPanel _stack = new() { Spacing = 1 };
    private readonly Border _card;
    private readonly ScrollViewer _scroll;
    private readonly List<FrameworkElement> _rows = new();
    private readonly Flyout _flyout;

    /// <summary>弹层关闭后触发（含轻扫关闭）。</summary>
    public event EventHandler? Dismissed;

    public PopupList()
    {
        // 弹层盖在页面内容上，必须**完全不透**：否则玻璃透明度调高时，
        // 底下的节点列表/流量图会透上来，菜单看不清（用户实测反馈）。
        // Only the rounded inner card paints the surface. A second, opaque
        // presenter background would expose a square behind its corners.
        var glass = ThemeUtil.Get("NeoPopupSurface", Color.FromArgb(0xFF, 0x24, 0x24, 0x29));
        var line = ThemeUtil.Get("NeoGlassBorder", Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            Content = _stack,
        };

        _card = new Border
        {
            CornerRadius = new CornerRadius(20),
            BorderThickness = new Thickness(1),
            BorderBrush = line,
            Background = glass,
            Padding = new Thickness(6),
            Child = _scroll,
        };

        // 卡片只归 Flyout 所有。不要再把它塞进任何 UserControl/Grid 的内容树，
        // 否则复用同一元素会抛 E_INVALIDARG（见类注释）。
        //   裸 Popup 的 HorizontalOffset / VerticalOffset 在本机 WinUI 3（1.8.260803003）
        //   下**完全不生效**——无论 ShouldConstrainToRootBounds 取 true 还是 false，
        //     · 日志回读：写入=(290,427) 回读=(290,427)（值确实存住了）
        //     · 屏幕实拍：弹层左上角落在 (118,89)，即主窗口客户区原点；
        //       而目标行在屏幕 (418,461) 676x53，正确落点应是 (418,518)
        //     · 同一时刻量到的弹窗矩形也是 118,89
        //   而 MenuFlyout 原本定位是对的，说明「定位」这件事该交给平台。
        //   Flyout.ShowAt 走的正是那条通路；我们只关掉它自带的出现过渡
        //   （AreOpenCloseAnimationsEnabled=false），换用自己的 Composition 动画。
        _flyout = new Flyout
        {
            Content = _card,
            AreOpenCloseAnimationsEnabled = false,
        };
        _flyout.FlyoutPresenterStyle = BuildPresenterStyle();
        _flyout.Opened += (_, _) =>
        {
            if (PrototypeMotion.Enabled) PlayEnter(); else ResetRowAnimations();
            // Source menu has one 130ms entrance; no per-item delay.
        };
        _flyout.Closed += (_, _) =>
        {
            try { Dismissed?.Invoke(this, EventArgs.Empty); } catch { }
        };
    }

    /// 去掉 FlyoutPresenter 自带的底色 / 描边 / 内边距 —— 弹层外观完全由我们自己的卡片决定。
    /// MaxWidth 必须放开：默认值会把弹层宽度夹到 456 左右，而节点列表要和 676 宽的选择行同宽。
    private static Style BuildPresenterStyle()
    {
        var style = new Style(typeof(FlyoutPresenter));
        // The card stays opaque; the host must stay transparent at its corners.
        style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Microsoft.UI.Colors.Transparent)));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(20)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.MinWidthProperty, 0d));
        style.Setters.Add(new Setter(Control.MinHeightProperty, 0d));
        style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, double.PositiveInfinity));
        return style;
    }

    /// <summary>加入一个可点条目；返回 Button 便于调用方进一步定制。</summary>
    public Button AddItem(string text, string? glyph = null, Action? onClick = null, bool enabled = true)
    {
        var label = Neo.App.Ui.AdaptiveText.WithSize(new TextBlock
        {
            Text = text,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        }, 12.5);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (!string.IsNullOrEmpty(glyph))
        {
            row.Children.Add(new FontIcon
            {
                Glyph = glyph!,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        row.Children.Add(label);

        var btn = new Button
        {
            Style = Application.Current.Resources["NeoPillButton"] as Style,
            Content = row,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(10, 7, 10, 7),
            IsEnabled = enabled,
            MinHeight = 0,
        };
        PillShape.SetEnabled(btn, true);

        if (onClick != null) btn.Click += (_, _) => onClick();
        btn.Click += (_, _) => Close();
        AddRow(btn);
        return btn;
    }

    /// <summary>不可点的说明行（对应原来 IsEnabled=false 的 MenuFlyoutItem）。</summary>
    public void AddDisabled(string text)
        => AddRow(Neo.App.Ui.AdaptiveText.WithSize(new TextBlock
        {
            Text = text,
            Margin = new Thickness(11, 8, 11, 8),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.66,
        }, 12));

    /// <summary>分隔线（对应 MenuFlyoutSeparator）。</summary>
    public void AddSeparator()
        => AddRow(new Border
        {
            Height = 1,
            Margin = new Thickness(6, 4, 6, 4),
            Background = ThemeUtil.Get("NeoHairline", Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
        });

    private void AddRow(FrameworkElement el)
    {
        _rows.Add(el);
        _stack.Children.Add(el);
    }

    /// <summary>清空全部条目（供复用一个实例时重新填充）。</summary>
    public void ClearItems()
    {
        try
        {
            _rows.Clear();
            _stack.Children.Clear();
        }
        catch { }
    }

    public void Close()
    {
        try { _flyout.Hide(); } catch { }
    }

    public bool IsOpen
    {
        get { try { return _flyout.IsOpen; } catch { return false; } }
    }

    /// 在目标元素正下方弹出。
    /// width 通常取目标行的宽度（与 MenuFlyout 的观感一致），会被夹到窗口宽度内。
    public static void ShowBelow(PopupList list, FrameworkElement target, double width, double maxHeight = 420)
        => list.Show(target, width, maxHeight);

    private void Show(FrameworkElement target, double width, double maxHeight)
    {
        var root = target.XamlRoot;
        if (root == null) return;
        // A Flyout can be reused after a theme change and lives in its own
        // visual root. Resolve the opaque surface for the current anchor.
        _card.RequestedTheme = target.ActualTheme;
        _card.Background = ThemeUtil.Get("NeoPopupSurface", Color.FromArgb(0xFF, 0x24, 0x24, 0x29));
        _card.BorderBrush = ThemeUtil.Get("NeoHairline", Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));

        // 宽度：与目标行同宽（MenuFlyout 的观感），但不得超出窗口可用宽度。
        double want = Math.Max(200, width);
        double maxW = Math.Max(200, root.Size.Width - 24);
        _card.Width = Math.Min(want, maxW);

        ResetRowAnimations();

        // 定位由平台完成：BottomEdgeAlignedLeft = 与目标元素左边缘对齐、贴其下沿。
        // 这正是 MenuFlyout 原有的行为，也是上一版裸 Popup 做不到的那一步。
        //   MaxHeight=420 的滚动卡片贴在下沿时会**超出窗口底部**，被截断。
        //   这里按目标下方的剩余空间收紧 MaxHeight；空间实在太小时改用 TopEdgeAlignedLeft
        //   （向上弹），并额外抬一个上边距，避免卡片顶到窗口标题栏。
        //   下面这行是本次修复的关键：把「可见高度」直接设成滚动区高度。
        //   _scroll 自身高度靠内容撑开（最多 maxHeight），而 _card 又包着它，
        //   因此只要约束 _scroll 就不会溢出。
        var anchor = target.TransformToVisual(null).TransformPoint(new Point(0, 0));
        var below = Math.Max(0, root.Size.Height - (anchor.Y + target.ActualHeight + 4));
        var above = Math.Max(0, anchor.Y - 4);
        var openBelow = below >= 160 || below >= above;
        _scroll.MaxHeight = Math.Max(40, Math.Min(maxHeight, (openBelow ? below : above) - 26));
        // Near the right edge, align to the anchor's right instead of letting
        // the platform clamp the menu flush against the window boundary.
        var alignRight = anchor.X + _card.Width > root.Size.Width - 12;
        var placement = openBelow
            ? alignRight ? FlyoutPlacementMode.BottomEdgeAlignedRight : FlyoutPlacementMode.BottomEdgeAlignedLeft
            : alignRight ? FlyoutPlacementMode.TopEdgeAlignedRight : FlyoutPlacementMode.TopEdgeAlignedLeft;

        _flyout.ShowAt(target, new FlyoutShowOptions
        {
            Placement = placement,
            ShowMode = FlyoutShowMode.Standard,
        });
    }

    private void ResetRowAnimations()
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            try
            {
                var v = ElementCompositionPreview.GetElementVisual(_rows[i]);
                v.StopAnimation("Opacity");
                v.Opacity = 1f;
            }
            catch { }
        }
        try
        {
            var cv = ElementCompositionPreview.GetElementVisual(_card);
            cv.StopAnimation("Offset");
            cv.StopAnimation("Scale");
            cv.StopAnimation("Opacity");
            cv.Offset = Vector3.Zero;
            cv.Scale = Vector3.One;
            cv.Opacity = 1f;
        }
        catch { }
    }

    /// 卡片整体入场：向上滑入 12 px + 0.97→1 缩放 + 淡入。
    /// 三个属性由合成器统一驱动，不会出现中间空帧。
    /// 位移走 visual.Offset：此处卡片是 Flyout 的根内容，自身不参与父级布局重排，
    /// 不会被布局结果顶掉（与页面切换场景不同，那里必须用 Translation 通道）。
    private void PlayEnter()
    {
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(_card);
            var compositor = visual.Compositor;

            var ease = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0f, 0f), new Vector2(.58f, 1f));

            visual.Offset = new Vector3(0f, -4f, 0f);
            visual.Scale = new Vector3(.985f, .985f, 1f);
            visual.Opacity = 0f;

            var dur = TimeSpan.FromMilliseconds(130);

            var offset = compositor.CreateVector3KeyFrameAnimation();
            offset.Duration = dur;
            offset.InsertKeyFrame(1f, Vector3.Zero, ease);

            var scale = compositor.CreateVector3KeyFrameAnimation();
            scale.Duration = dur;
            scale.InsertKeyFrame(1f, Vector3.One, ease);

            var opacity = compositor.CreateScalarKeyFrameAnimation();
            opacity.Duration = TimeSpan.FromMilliseconds(130);
            opacity.InsertKeyFrame(1f, 1f);

            visual.StartAnimation("Offset", offset);
            visual.StartAnimation("Scale", scale);
            visual.StartAnimation("Opacity", opacity);
        }
        catch { }
    }

}
