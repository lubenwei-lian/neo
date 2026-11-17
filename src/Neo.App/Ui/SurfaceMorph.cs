using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace Neo.App.Ui;

/// <summary>
/// The prototype's finite surface transition, animated on the compositor.
/// XAML keeps a full-size layout and glyph raster; it must never see the
/// 10px starting scale as a RenderTransform on the live settings tree.
/// </summary>
internal sealed class SurfaceMorph
{
    private readonly Border _surface;
    private readonly FrameworkElement _contents;
    private Visual? _visual, _inner;
    private CompositionScopedBatch? _batch;
    private Storyboard? _radiusBoard;
    private readonly List<(CompositionObject Target, string Property, CompositionAnimation Animation)> _animations = new();
    private TaskCompletionSource<bool>? _completion;
    private readonly Stopwatch _clock = new();
    private int _generation, _duration;
    private bool _closing, _dot;
    private double _hold;
    private Frame _from = Frame.Identity, _to = Frame.Identity, _held = Frame.Identity;

    private readonly record struct Frame(Vector3 Translation, Vector3 Scale, float Opacity, float Inner)
    {
        internal static Frame Identity => new(Vector3.Zero, Vector3.One, 1, 1);
    }

    internal bool Running => _batch != null;
    internal object Inspect()
    {
        var frame = Current();
        return new { running = Running, engine = "Composition", activeAnimations = _animations.Count, time = _clock.Elapsed.TotalMilliseconds,
            x = frame.Translation.X, y = frame.Translation.Y, sx = frame.Scale.X, sy = frame.Scale.Y,
            opacity = frame.Opacity, innerOpacity = frame.Inner, radius = _surface.CornerRadius.TopLeft };
    }

    internal SurfaceMorph(Border surface, FrameworkElement contents)
    {
        _surface = surface; _contents = contents;
    }

    private void EnsureVisuals()
    {
        if (_visual != null) return;
        _surface.ClearValue(UIElement.RenderTransformProperty);
        _surface.RenderTransformOrigin = new Point(0, 0);
        ElementCompositionPreview.SetIsTranslationEnabled(_surface, true);
        _visual = ElementCompositionPreview.GetElementVisual(_surface);
        _inner = ElementCompositionPreview.GetElementVisual(_contents);
        _visual.CenterPoint = Vector3.Zero;
    }

    private Frame Anchor(FrameworkElement anchor, bool dot)
    {
        var root = _surface.XamlRoot.Content;
        Point Position(UIElement element, Point point)
        {
            try { return element.TransformToVisual(root).TransformPoint(point); }
            catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException)
            { return element.TransformToVisual(null).TransformPoint(point); }
        }
        var a = Position(anchor, new Point());
        // ActualOffset belongs to layout, so this origin does not include an
        // interrupted compositor transform or depend on a text raster's scale.
        var parent = VisualTreeHelper.GetParent(_surface) as UIElement;
        var offset = _surface.ActualOffset;
        var b = parent != null ? Position(parent, new Point(offset.X, offset.Y)) : Position(_surface, new Point());
        var width = dot ? 10 : anchor.ActualWidth;
        var height = dot ? 10 : anchor.ActualHeight;
        return new Frame(new Vector3((float)(a.X - b.X + (dot ? (anchor.ActualWidth - 10) / 2 : 0)),
            (float)(a.Y - b.Y + (dot ? (anchor.ActualHeight - 10) / 2 : 0)), 0),
            new Vector3((float)(width / Math.Max(1, _surface.ActualWidth)),
                (float)(height / Math.Max(1, _surface.ActualHeight)), 1), dot ? 0 : 1, 0);
    }

    internal Task<bool> OpenAsync(FrameworkElement anchor, bool dot)
    {
        Cancel(); _surface.UpdateLayout();
        if (!PrototypeMotion.Enabled || anchor.ActualWidth <= 0 || _surface.ActualWidth <= 0)
            return Task.FromResult(true);
        EnsureVisuals();
        _surface.CornerRadius = new CornerRadius(dot ? 26 : 999);
        return Run(Anchor(anchor, dot), Frame.Identity, false, dot);
    }

    internal Task<bool> CloseAsync(FrameworkElement anchor, bool dot)
    {
        EnsureVisuals(); HoldCurrent();
        if (!PrototypeMotion.Enabled || anchor.ActualWidth <= 0 || _surface.ActualWidth <= 0)
        { Cancel(); return Task.FromResult(true); }
        var target = Anchor(anchor, dot) with { Opacity = dot ? .2f : .6f };
        return Run(_held, target, true, dot);
    }

    private Task<bool> Run(Frame from, Frame to, bool closing, bool dot)
    {
        _from = from; _to = to; _closing = closing; _dot = dot;
        _duration = closing ? dot ? 300 : 220 : dot ? 420 : 320;
        _hold = !closing && dot ? .14 : 0;
        var generation = ++_generation;
        var completion = _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var compositor = _visual!.Compositor;
        var ease = compositor.CreateCubicBezierEasingFunction(
            new Vector2(closing && !dot ? .4f : .16f, closing && !dot ? 0 : .84f),
            new Vector2(closing && !dot ? .2f : .24f, 1));
        var linear = compositor.CreateLinearEasingFunction();
        var batch = _batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        void Vector(CompositionObject target, string property, Vector3 first, Vector3 last)
        {
            var animation = compositor.CreateVector3KeyFrameAnimation();
            animation.Duration = TimeSpan.FromMilliseconds(_duration);
            animation.InsertKeyFrame(0, first, linear);
            if (_hold > 0) animation.InsertKeyFrame((float)_hold, first, linear);
            animation.InsertKeyFrame(1, last, ease);
            animation.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;
            _animations.Add((target, property, animation)); target.StartAnimation(property, animation);
        }
        void Opacity(Visual target, float first, float last, int duration, double hold, double finish, CompositionEasingFunction curve)
        {
            var animation = compositor.CreateScalarKeyFrameAnimation();
            animation.Duration = TimeSpan.FromMilliseconds(duration);
            animation.InsertKeyFrame(0, first, linear);
            if (hold > 0) animation.InsertKeyFrame((float)hold, first, linear);
            animation.InsertKeyFrame((float)finish, last, curve);
            if (finish < 1) animation.InsertKeyFrame(1, last, linear);
            animation.StopBehavior = AnimationStopBehavior.LeaveCurrentValue;
            _animations.Add((target, "Opacity", animation)); target.StartAnimation("Opacity", animation);
        }
        // All base XAML values stay at full size. These native animations are
        // committed together and do not drive layout on every animation frame.
        Vector(_visual, "Scale", from.Scale, to.Scale);
        Vector(_visual.Properties, "Translation", from.Translation, to.Translation);
        Opacity(_visual, from.Opacity, to.Opacity, _duration, _hold, 1, ease);
        Opacity(_inner!, from.Inner, to.Inner, closing ? dot ? 120 : 100 : _duration,
            closing ? 0 : dot ? .38 : .30, closing ? 1 : dot ? .74 : 1, linear);
        if (!dot) AnimateRadius(_surface.CornerRadius.TopLeft, closing ? 999 : 26, _duration, closing);
        batch.Completed += (_, _) =>
        {
            if (generation != _generation) return;
            _completion = null;
            if (closing) { _held = to; Stop(); Apply(to); } else Cancel();
            completion.TrySetResult(true);
        };
        _clock.Restart(); batch.End(); ease.Dispose(); linear.Dispose();
        _ = SettleOccludedAsync(generation, completion);
        return completion.Task;
    }

    private async Task SettleOccludedAsync(int generation, TaskCompletionSource<bool> completion)
    {
        await Task.Delay(_duration + 180);
        if (generation != _generation || _batch == null) return;
        _completion = null;
        if (_closing) { _held = _to; Stop(); Apply(_held); } else Cancel();
        completion.TrySetResult(true);
    }

    private Frame Current()
    {
        if (!Running) return _held;
        var elapsed = _clock.Elapsed.TotalMilliseconds;
        var progress = Math.Clamp((elapsed / _duration - _hold) / (1 - _hold), 0, 1);
        var eased = (float)Ease(progress, _closing && !_dot);
        var inner = _closing
            ? Math.Clamp(elapsed / (_dot ? 120 : 100), 0, 1)
            : Math.Clamp((elapsed / _duration - (_dot ? .38 : .30)) / (_dot ? .36 : .70), 0, 1);
        return new Frame(Vector3.Lerp(_from.Translation, _to.Translation, eased),
            Vector3.Lerp(_from.Scale, _to.Scale, eased), _from.Opacity + (_to.Opacity - _from.Opacity) * eased,
            _from.Inner + (_to.Inner - _from.Inner) * (float)inner);
    }

    private static double Ease(double x, bool close)
    {
        var lo = 0d; var hi = 1d;
        var x1 = close ? .4 : .16; var y1 = close ? 0 : .84; var x2 = close ? .2 : .24;
        static double Bezier(double t, double p1, double p2) => 3 * (1-t) * (1-t) * t * p1 + 3 * (1-t) * t * t * p2 + t*t*t;
        for (var i = 0; i < 20; i++) { var mid = (lo+hi)/2; if (Bezier(mid,x1,x2) < x) lo=mid; else hi=mid; }
        return x <= 0 ? 0 : x >= 1 ? 1 : Bezier((lo+hi)/2,y1,1);
    }

    private void AnimateRadius(double from, double to, int duration, bool closing)
    {
        var board = _radiusBoard = new Storyboard();
        var animation = new ObjectAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(duration) };
        var frames = (int)Math.Ceiling(duration / 16d);
        for (var i = 0; i <= frames; i++)
            animation.KeyFrames.Add(new DiscreteObjectKeyFrame { KeyTime = TimeSpan.FromMilliseconds(duration * (double)i / frames),
                Value = new CornerRadius(from + (to - from) * Ease((double)i / frames, closing)) });
        Storyboard.SetTarget(animation, _surface); Storyboard.SetTargetProperty(animation, "CornerRadius");
        board.Children.Add(animation); board.Begin();
    }

    private void Apply(Frame frame)
    {
        if (_visual == null || _inner == null) return;
        _visual.Scale = frame.Scale; _visual.Properties.InsertVector3("Translation", frame.Translation);
        _visual.Opacity = frame.Opacity; _inner.Opacity = frame.Inner;
    }

    private void Stop()
    {
        ++_generation; _clock.Stop();
        var batch = _batch; _batch = null;
        foreach (var (target, property, animation) in _animations) { target.StopAnimation(property); animation.Dispose(); }
        _animations.Clear(); batch?.Dispose();
        var radius = _surface.CornerRadius; _radiusBoard?.Stop(); _radiusBoard = null; _surface.CornerRadius = radius;
        _completion?.TrySetResult(false); _completion = null;
    }

    private void HoldCurrent()
    {
        _held = Current(); Stop(); Apply(_held);
    }

    internal void Cancel()
    {
        Stop(); _held = Frame.Identity; Apply(_held);
        _surface.Opacity = _contents.Opacity = 1; _surface.CornerRadius = new CornerRadius(26);
    }
}
