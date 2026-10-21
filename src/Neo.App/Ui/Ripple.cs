using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Neo.App.Ui;

public static class Ripple
{
    public static void Attach(FrameworkElement target, Canvas? layer)
    {
        if (layer == null) return;
        target.Tapped += (_, e) =>
        {
            try
            {
                var pos = e.GetPosition(target);
                var root = target.TransformToVisual(layer).TransformPoint(pos);
                var d = Math.Max(target.ActualWidth, target.ActualHeight) * 1.6;
                if (d <= 0) return;

                var el = new Ellipse
                {
                    Width = d,
                    Height = d,
                    Fill = new SolidColorBrush(Color.FromArgb(0x40, 0x25, 0x63, 0xEB)),
                    Opacity = 0.75,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new ScaleTransform { ScaleX = 0.12, ScaleY = 0.12 },
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(el, root.X - d / 2);
                Canvas.SetTop(el, root.Y - d / 2);
                layer.Children.Add(el);

                var sb = new Storyboard();
                var sx = new DoubleAnimation
                {
                    To = 1,
                    Duration = TimeSpan.FromMilliseconds(380),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                };
                Storyboard.SetTarget(sx, el.RenderTransform);
                Storyboard.SetTargetProperty(sx, "ScaleX");
                var sy = new DoubleAnimation
                {
                    To = 1,
                    Duration = TimeSpan.FromMilliseconds(380),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                };
                Storyboard.SetTarget(sy, el.RenderTransform);
                Storyboard.SetTargetProperty(sy, "ScaleY");
                var fo = new DoubleAnimation
                {
                    To = 0,
                    Duration = TimeSpan.FromMilliseconds(430),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                };
                Storyboard.SetTarget(fo, el);
                Storyboard.SetTargetProperty(fo, "Opacity");
                sb.Children.Add(sx);
                sb.Children.Add(sy);
                sb.Children.Add(fo);
                sb.Completed += (_, _) => { try { layer.Children.Remove(el); } catch { } };
                sb.Begin();
            }
            catch { }
        };
    }
}
