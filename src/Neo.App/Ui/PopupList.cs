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

/// 带入场动效的下拉列表，替代 MenuFlyout。
/// 为什么不能用 MenuFlyout：
///   MenuFlyout 的出现动效由 WinUI 主题模板里的 MenuFlyoutPresenterStyle 决定，
///   应用侧能干预的只有 RequestedTheme（neo 目前只做了这一件，见 MainWindow.ApplyFlyoutTheme）。
///   观感就是用户说的「直接一帧出现」。
/// 为什么用 Composition 而不是 XAML Storyboard：
///   Composition 动画跑在合成器线程上，起止由合成器保证；而 Storyboard 走 UI 线程，
///   先置 opacity=0 再 Begin 之间可能插进一帧，且反复 Begin 会叠加多条永不回收的动画
///   （本项目已有前车之鉴，见 MainWindow 的 _brushAnims 注释）。
///   Composition 的 StartAnimation 对同名属性是覆盖语义，不存在堆积。
///   · 时长 220 ms：介于 WinUI 标准档 ControlFastAnimationDuration(167 ms) 与
///     ControlNormalAnimationDuration(250 ms) 之间 —— 弹层比整页切换更短、更「跟手」。
///   · 曲线 cubic-bezier(0,0,0,1) = Fluent「Fast Out, Slow In」，
///   · 位移 12 px + 缩放 0.97→1：Material 3 容器变换语义（容器从来源方向微微展开，
///     而不是凭空出现）。
///   · 列表项错峰 18 ms/项、封顶 7 项（约 126 ms）：60+ 个节点时逐项延迟不能线性放大，
///     否则最后一项要等 1 秒才出现。
///   初版继承 UserControl 并把卡片设为 Content。结果 UserControl 已把该元素认作自己的
///   内容树，再赋给 Popup.Child 时 WinUI 抛 ArgumentException「Value does not fall within
///   the expected range」（栈顶为 Popup.set_Child；隔离实例日志 19:00:46 抓到完整堆栈）。
///   改为普通类、由 Popup 独占持有卡片即根治。
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
        // 应用固定暗色主题，这里用不透明的固定底色，不跟随 GlassTuner 的缩放。
        var glass = new SolidColorBrush(Color.FromArgb(0xFF, 0x12, 0x16, 0x1D));
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
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = line,
            Background = glass,
            Padding = new Thickness(5),
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
            PlayEnter();
            PlayStagger();
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
        // 用不透明底色兜底：Flyout 是独立弹层窗口，如果这里留透明，
        // 窗口后面的页面内容会直接透上来（高透明档位尤其明显）。
        style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0xFF, 0x0F, 0x12, 0x18))));
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
        var label = new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

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
            Content = row,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10, 7, 10, 7),
            IsEnabled = enabled,
            MinHeight = 0,
        };

        if (onClick != null) btn.Click += (_, _) => onClick();
        btn.Click += (_, _) => Close();
        HookHover(btn);
        AddRow(btn);
        return btn;
    }

    /// <summary>不可点的说明行（对应原来 IsEnabled=false 的 MenuFlyoutItem）。</summary>
    public void AddDisabled(string text)
        => AddRow(new TextBlock
        {
            Text = text,
            FontSize = 12,
            Margin = new Thickness(11, 8, 11, 8),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.66,
        });

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

    /// <summary>与 MenuFlyoutItem 的默认悬停表现对齐（本类自带背景，不依赖外部模板）。</summary>
    private static void HookHover(Button b)
    {
        var brush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        b.Background = brush;
        b.PointerEntered += (_, _) =>
        {
            if (!b.IsEnabled) return;
            if (ThemeUtil.Get("NeoAccentSoft", Color.FromArgb(0x24, 0x5A, 0xA9, 0xFF)) is SolidColorBrush accent)
                brush.Color = accent.Color;
        };
        b.PointerExited += (_, _) => brush.Color = Microsoft.UI.Colors.Transparent;
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
        var below = Math.Max(0, root.Size.Height - (target.TransformToVisual(null).TransformPoint(new Point(0, 0)).Y + target.ActualHeight + 4));
        double avail = Math.Max(120, Math.Min(maxHeight, below - 12));
        _scroll.MaxHeight = avail;

        var placement = below >= 160
            ? FlyoutPlacementMode.BottomEdgeAlignedLeft
            : FlyoutPlacementMode.TopEdgeAlignedLeft;

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
                new Vector2(0f, 0f), new Vector2(0f, 1f));

            visual.Offset = new Vector3(0f, 12f, 0f);
            visual.Scale = new Vector3(0.97f, 0.97f, 1f);
            visual.Opacity = 0f;

            var dur = TimeSpan.FromMilliseconds(220);

            var offset = compositor.CreateVector3KeyFrameAnimation();
            offset.Duration = dur;
            offset.InsertKeyFrame(1f, Vector3.Zero, ease);

            var scale = compositor.CreateVector3KeyFrameAnimation();
            scale.Duration = dur;
            scale.InsertKeyFrame(1f, Vector3.One, ease);

            var opacity = compositor.CreateScalarKeyFrameAnimation();
            opacity.Duration = TimeSpan.FromMilliseconds(150);
            opacity.InsertKeyFrame(1f, 1f);

            visual.StartAnimation("Offset", offset);
            visual.StartAnimation("Scale", scale);
            visual.StartAnimation("Opacity", opacity);
        }
        catch { }
    }

    /// <summary>列表项逐项淡入。索引超过 7 后共用同一延迟 —— 长列表不能线性放大总时长。</summary>
    private void PlayStagger()
    {
        try
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                var v = ElementCompositionPreview.GetElementVisual(_rows[i]);
                v.Opacity = 0f;

                var op = v.Compositor.CreateScalarKeyFrameAnimation();
                op.Duration = TimeSpan.FromMilliseconds(140);
                op.DelayTime = TimeSpan.FromMilliseconds(18 * Math.Min(i, 7));
                op.InsertKeyFrame(1f, 1f);
                v.StartAnimation("Opacity", op);
            }
        }
        catch { }
    }
}