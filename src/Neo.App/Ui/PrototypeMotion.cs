using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace Neo.App.Ui;

/// <summary>Motion values transcribed from the current neo-all.html scripts.</summary>
internal static class PrototypeMotion
{
    internal static bool Enabled => new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
    internal static KeySpline EnterEase => new() { ControlPoint1 = new Point(.22, .7), ControlPoint2 = new Point(.3, 1) };
    internal static KeySpline GrowEase => new() { ControlPoint1 = new Point(.16, .84), ControlPoint2 = new Point(.24, 1) };
    internal static KeySpline CloseEase => new() { ControlPoint1 = new Point(.4, 0), ControlPoint2 = new Point(.2, 1) };

    internal static void Add(Storyboard board, DependencyObject target, string property,
        double from, double to, int duration, KeySpline? ease = null, double hold = 0, double finish = 1)
    {
        var animation = new DoubleAnimationUsingKeyFrames
            { EnableDependentAnimation = true, Duration = TimeSpan.FromMilliseconds(duration) };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = from });
        if (hold > 0) animation.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(duration * hold), Value = from });
        DoubleKeyFrame end = ease == null
            ? new LinearDoubleKeyFrame { Value = to }
            : new SplineDoubleKeyFrame { Value = to, KeySpline = new KeySpline
                { ControlPoint1 = ease.ControlPoint1, ControlPoint2 = ease.ControlPoint2 } };
        end.KeyTime = TimeSpan.FromMilliseconds(duration * finish);
        animation.KeyFrames.Add(end);
        if (finish < 1) animation.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(duration), Value = to });
        Storyboard.SetTarget(animation, target); Storyboard.SetTargetProperty(animation, property);
        board.Children.Add(animation);
    }
}
