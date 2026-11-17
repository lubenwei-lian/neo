using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace Neo.App.Ui;

/// <summary>Only the capsule chrome changes size. The full-size text tree never scales or reflows per frame.</summary>
internal sealed class CapsuleSurfaceMotion(Canvas stage, Border chrome, FrameworkElement contents) : IDisposable
{
    private ShapeVisual? _layer;
    private CompositionRoundedRectangleGeometry? _geometry;
    private CompositionRoundedRectangleGeometry? _contentGeometry;
    private CompositionGeometricClip? _contentClip;
    private CompositionSpriteShape? _shape;
    private CompositionColorBrush? _fill, _stroke;
    private Visual? _content;
    private CompositionScopedBatch? _batch;
    private readonly List<(CompositionObject Target, string Property, CompositionAnimation Animation)> _animations = [];
    private readonly Stopwatch _clock = new();
    private TaskCompletionSource<bool>? _completion;
    private int _generation, _duration;
    private bool _closing;
    private Vector2 _contentOrigin;
    private Frame _from, _to, _held;
    private readonly record struct Frame(Vector2 Position, Vector2 Size, Vector2 Radius, float Opacity, float Content);

    internal object Inspect()
    {
        var current = Current();
        return new { running = _batch != null, engine = "CompositionGeometry", activeAnimations = _animations.Count,
            x = current.Position.X, y = current.Position.Y, width = current.Size.X, height = current.Size.Y,
            radius = current.Radius.X, opacity = current.Opacity, innerOpacity = current.Content,
            contentScale = 1, time = _clock.Elapsed.TotalMilliseconds };
    }
    private void Ensure()
    {
        if (_layer != null) return;
        var compositor = ElementCompositionPreview.GetElementVisual(stage).Compositor;
        _geometry = compositor.CreateRoundedRectangleGeometry();
        _fill = compositor.CreateColorBrush((chrome.Background as SolidColorBrush)?.Color ?? Color.FromArgb(255, 23, 23, 28));
        _stroke = compositor.CreateColorBrush((chrome.BorderBrush as SolidColorBrush)?.Color ?? Color.FromArgb(255, 43, 43, 50));
        _shape = compositor.CreateSpriteShape(_geometry); _shape.FillBrush = _fill; _shape.StrokeBrush = _stroke; _shape.StrokeThickness = 1;
        _layer = compositor.CreateShapeVisual(); _layer.Size = new((float)stage.ActualWidth, (float)stage.ActualHeight);
        _layer.Shapes.Add(_shape); ElementCompositionPreview.SetElementChildVisual(stage, _layer);
        _content = ElementCompositionPreview.GetElementVisual(contents);
        _contentGeometry = compositor.CreateRoundedRectangleGeometry();
        _contentClip = compositor.CreateGeometricClip(_contentGeometry);
    }
    private Frame Bounds(FrameworkElement element, bool capsule)
    {
        Point point;
        try { point = element.TransformToVisual(stage).TransformPoint(new Point()); }
        catch (ArgumentException)
        {
            var origin = element.TransformToVisual(null).TransformPoint(new Point());
            var stageOrigin = stage.TransformToVisual(null).TransformPoint(new Point());
            point = new(origin.X - stageOrigin.X, origin.Y - stageOrigin.Y);
        }
        var size = new Vector2((float)element.ActualWidth, (float)element.ActualHeight);
        var radius = capsule ? Math.Min(size.X, size.Y) * .5f : 26;
        return new(new((float)point.X, (float)point.Y), size, new(radius), 1, capsule ? 0 : 1);
    }
    internal Task<bool> OpenAsync(FrameworkElement anchor)
    {
        Cancel(); Ensure(); chrome.UpdateLayout();
        if (!PrototypeMotion.Enabled || anchor.ActualWidth <= 0 || chrome.ActualWidth <= 0) return Task.FromResult(true);
        _layer!.Opacity = 1; chrome.Opacity = 0;
        return Run(Bounds(anchor, true), Bounds(chrome, false), false, (anchor as Control)?.Background as SolidColorBrush);
    }
    internal Task<bool> CloseAsync(FrameworkElement anchor)
    {
        Ensure(); _held = _batch != null ? Current() : Bounds(chrome, false); Stop(); Apply(_held);
        if (!PrototypeMotion.Enabled || anchor.ActualWidth <= 0) { Cancel(); return Task.FromResult(true); }
        _layer!.Opacity = 1; chrome.Opacity = 0;
        return Run(_held, Bounds(anchor, true) with { Opacity = .6f }, true, null);
    }
    private Task<bool> Run(Frame from, Frame to, bool closing, SolidColorBrush? anchorFill)
    {
        _from = from; _to = to; _closing = closing; _duration = closing ? 220 : 320;
        _contentOrigin = Bounds(contents, false).Position;
        _content!.Clip = _contentClip;
        var generation = ++_generation;
        var completion = _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var compositor = _layer!.Compositor;
        var ease = compositor.CreateCubicBezierEasingFunction(closing ? new(.4f, 0) : new(.16f, .84f), closing ? new(.2f, 1) : new(.24f, 1));
        var linear = compositor.CreateLinearEasingFunction();
        Apply(from); contents.IsHitTestVisible = false;
        var batch = _batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        void Vector(CompositionObject target, string name, Vector2 first, Vector2 last)
        {
            var animation = compositor.CreateVector2KeyFrameAnimation(); animation.Duration = TimeSpan.FromMilliseconds(_duration);
            animation.InsertKeyFrame(0, first); animation.InsertKeyFrame(1, last, ease);
            _animations.Add((target, name, animation)); target.StartAnimation(name, animation);
        }
        void Alpha(Visual target, float first, float last, int duration, float hold)
        {
            var animation = compositor.CreateScalarKeyFrameAnimation(); animation.Duration = TimeSpan.FromMilliseconds(duration);
            animation.InsertKeyFrame(0, first); if (hold > 0) animation.InsertKeyFrame(hold, first, linear);
            animation.InsertKeyFrame(1, last, linear);
            _animations.Add((target, "Opacity", animation)); target.StartAnimation("Opacity", animation);
        }
        Vector(_shape!, "Offset", from.Position, to.Position);
        Vector(_geometry!, "Size", from.Size, to.Size); Vector(_geometry!, "CornerRadius", from.Radius, to.Radius);
        Vector(_contentGeometry!, "Offset", from.Position - _contentOrigin, to.Position - _contentOrigin);
        Vector(_contentGeometry!, "Size", from.Size, to.Size);
        Vector(_contentGeometry!, "CornerRadius", from.Radius, to.Radius);
        Alpha(_layer, from.Opacity, to.Opacity, _duration, 0);
        Alpha(_content!, from.Content, to.Content, closing ? 100 : 320, closing ? 0 : .3f);
        if (anchorFill is { Color.A: > 0 })
        {
            var animation = compositor.CreateColorKeyFrameAnimation(); animation.Duration = TimeSpan.FromMilliseconds(_duration);
            animation.InsertKeyFrame(0, anchorFill.Color); animation.InsertKeyFrame(1, _fill!.Color, ease);
            _animations.Add((_fill!, "Color", animation)); _fill!.StartAnimation("Color", animation);
        }
        batch.Completed += (_, _) => Finish(generation, completion);
        _clock.Restart(); batch.End(); ease.Dispose(); linear.Dispose();
        _ = SettleAsync(generation, completion);
        return completion.Task;
    }
    private async Task SettleAsync(int generation, TaskCompletionSource<bool> completion)
    { await Task.Delay(_duration + 180); Finish(generation, completion); }
    private void Finish(int generation, TaskCompletionSource<bool> completion)
    {
        if (generation != _generation || _batch == null) return;
        _completion = null;
        var closing = _closing; var final = _to;
        Stop(); Apply(final); contents.IsHitTestVisible = !closing;
        if (!closing) { _layer!.Opacity = 0; chrome.Opacity = 1; _content!.Clip = null; }
        completion.TrySetResult(true);
    }
    private Frame Current()
    {
        if (_batch == null) return _held;
        var progress = Math.Clamp(_clock.Elapsed.TotalMilliseconds / _duration, 0, 1);
        var eased = (float)Ease(progress, _closing);
        var inner = _closing ? Math.Clamp(_clock.Elapsed.TotalMilliseconds / 100, 0, 1) : Math.Clamp((progress - .3) / .7, 0, 1);
        return new(Vector2.Lerp(_from.Position, _to.Position, eased), Vector2.Lerp(_from.Size, _to.Size, eased),
            Vector2.Lerp(_from.Radius, _to.Radius, eased), _from.Opacity + (_to.Opacity - _from.Opacity) * (float)progress,
            _from.Content + (_to.Content - _from.Content) * (float)inner);
    }
    private static double Ease(double x, bool close)
    {
        static double B(double t, double a, double b) => 3 * (1-t) * (1-t) * t * a + 3 * (1-t) * t * t * b + t*t*t;
        var lo = 0d; var hi = 1d;
        for (var i = 0; i < 20; i++) { var t = (lo+hi)/2; if (B(t, close ? .4 : .16, close ? .2 : .24) < x) lo=t; else hi=t; }
        return x <= 0 ? 0 : x >= 1 ? 1 : B((lo+hi)/2, close ? 0 : .84, 1);
    }
    private void Apply(Frame frame)
    {
        _held = frame;
        if (_layer == null) return;
        _shape!.Offset = frame.Position; _geometry!.Size = frame.Size; _geometry.CornerRadius = frame.Radius;
        _contentGeometry!.Offset = frame.Position - _contentOrigin; _contentGeometry.Size = frame.Size; _contentGeometry.CornerRadius = frame.Radius;
        _layer.Opacity = frame.Opacity; _content!.Opacity = frame.Content;
    }
    private void Stop()
    {
        ++_generation; _clock.Stop();
        var batch = _batch; _batch = null;
        foreach (var (target, name, animation) in _animations) { target.StopAnimation(name); animation.Dispose(); }
        _animations.Clear(); batch?.Dispose(); _completion?.TrySetResult(false); _completion = null;
    }
    internal void Cancel()
    {
        Stop(); if (_layer != null) _layer.Opacity = 0;
        if (_content != null) { _content.Opacity = 1; _content.Clip = null; }
        chrome.Opacity = contents.Opacity = 1; contents.IsHitTestVisible = true;
    }
    public void Dispose()
    {
        Cancel(); ElementCompositionPreview.SetElementChildVisual(stage, null);
        _shape?.Dispose(); _geometry?.Dispose(); _fill?.Dispose(); _stroke?.Dispose(); _layer?.Dispose();
        _contentClip?.Dispose(); _contentGeometry?.Dispose(); _contentClip = null; _contentGeometry = null;
        _shape = null; _geometry = null; _fill = _stroke = null; _layer = null; _content = null;
    }
}
