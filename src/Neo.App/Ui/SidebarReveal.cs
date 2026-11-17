using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Neo.App.Ui;

/// <summary>
/// Reproduces the source's 220ms height reveal without remeasuring buttons on
/// every frame. Reserve the final layout once, then translate its contents and
/// the preceding usage card together behind an animated clip.
/// </summary>
internal sealed class SidebarReveal
{
    private readonly FrameworkElement _viewport, _content, _preceding;
    private Visual? _visual, _contentVisual, _precedingVisual;
    private InsetClip? _clip;
    private CompositionScopedBatch? _batch;
    private readonly List<(CompositionObject Target, string Property, CompositionAnimation Animation)> _animations = new();
    private readonly Stopwatch _clock = new();
    private double _extent, _from, _to, _exposed;
    private float _fromOpacity, _toOpacity;
    private int _generation;

    internal bool Running => _batch != null;
    internal double ExposedHeight => Running ? _from + (_to - _from) * Progress() : _exposed;
    internal object Inspect() => new { engine = "Composition/clip+translation", running = Running,
        layoutHeight = _viewport.Height, exposedHeight = ExposedHeight, extent = _extent,
        activeAnimations = _animations.Count };

    internal SidebarReveal(FrameworkElement viewport, FrameworkElement content, FrameworkElement preceding)
    { _viewport = viewport; _content = content; _preceding = preceding; }

    internal void Set(bool expanded, double extent, bool animate, Action completed)
    {
        var from = ExposedHeight;
        var opacity = Running ? _fromOpacity + (_toOpacity - _fromOpacity) * (float)Progress() : _exposed > 0 ? 1f : 0f;
        Stop();
        _extent = extent;
        _from = Math.Clamp(from, 0, extent); _to = expanded ? extent : 0;
        _fromOpacity = opacity; _toOpacity = expanded ? 1 : 0;
        var generation = ++_generation;
        _viewport.Visibility = Visibility.Visible;
        _viewport.Height = extent;
        _viewport.UpdateLayout();
        EnsureVisuals();

        void Complete()
        {
            if (generation != _generation) return;
            Stop(); _exposed = expanded ? extent : 0;
            _viewport.Height = _exposed;
            _viewport.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            _clip!.TopInset = 0;
            _visual!.Opacity = 1;
            _contentVisual!.Properties.InsertVector3("Translation", Vector3.Zero);
            _precedingVisual!.Properties.InsertVector3("Translation", Vector3.Zero);
            completed();
        }
        if (!animate || !PrototypeMotion.Enabled || Math.Abs(_to - _from) < .5)
        { Complete(); return; }

        Apply(_from, opacity);
        var compositor = _visual!.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(.16f, .84f), new Vector2(.24f, 1));
        var batch = _batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        void Scalar(CompositionObject target, string property, float first, float last)
        {
            var animation = compositor.CreateScalarKeyFrameAnimation();
            animation.Duration = TimeSpan.FromMilliseconds(220);
            animation.InsertKeyFrame(0, first); animation.InsertKeyFrame(1, last, easing);
            _animations.Add((target, property, animation)); target.StartAnimation(property, animation);
        }
        void Translate(Visual visual)
        {
            var animation = compositor.CreateVector3KeyFrameAnimation();
            animation.Duration = TimeSpan.FromMilliseconds(220);
            animation.InsertKeyFrame(0, new Vector3(0, (float)(extent - _from), 0));
            animation.InsertKeyFrame(1, new Vector3(0, (float)(extent - _to), 0), easing);
            _animations.Add((visual.Properties, "Translation", animation)); visual.Properties.StartAnimation("Translation", animation);
        }
        Scalar(_clip!, "TopInset", (float)(extent - _from), (float)(extent - _to));
        Scalar(_visual, "Opacity", opacity, _toOpacity);
        Translate(_contentVisual!); Translate(_precedingVisual!);
        batch.Completed += (_, _) => Complete();
        _clock.Restart(); batch.End(); easing.Dispose();
        _ = SettleAsync(generation, Complete);
    }

    private void EnsureVisuals()
    {
        if (_visual != null) return;
        ElementCompositionPreview.SetIsTranslationEnabled(_content, true);
        ElementCompositionPreview.SetIsTranslationEnabled(_preceding, true);
        _visual = ElementCompositionPreview.GetElementVisual(_viewport);
        _contentVisual = ElementCompositionPreview.GetElementVisual(_content);
        _precedingVisual = ElementCompositionPreview.GetElementVisual(_preceding);
        _clip = _visual.Compositor.CreateInsetClip();
        _visual.Clip = _clip;
    }

    private void Apply(double exposed, float opacity)
    {
        _clip!.TopInset = (float)(_extent - exposed);
        _visual!.Opacity = opacity;
        var shift = new Vector3(0, (float)(_extent - exposed), 0);
        _contentVisual!.Properties.InsertVector3("Translation", shift);
        _precedingVisual!.Properties.InsertVector3("Translation", shift);
    }

    private double Progress()
    {
        var x = Math.Clamp(_clock.Elapsed.TotalMilliseconds / 220, 0, 1);
        var lo = 0d; var hi = 1d;
        static double B(double t, double a, double b) => 3 * (1-t) * (1-t) * t * a + 3 * (1-t) * t * t * b + t*t*t;
        for (var i = 0; i < 20; i++) { var t = (lo + hi) / 2; if (B(t, .16, .24) < x) lo = t; else hi = t; }
        return x <= 0 ? 0 : x >= 1 ? 1 : B((lo + hi) / 2, .84, 1);
    }

    private async Task SettleAsync(int generation, Action complete)
    {
        await Task.Delay(400);
        if (!Neo.App.App.IsShuttingDown && generation == _generation && Running) complete();
    }

    private void Stop()
    {
        ++_generation; _clock.Stop();
        var batch = _batch; _batch = null;
        foreach (var (target, property, animation) in _animations) { target.StopAnimation(property); animation.Dispose(); }
        _animations.Clear(); batch?.Dispose();
    }
}
