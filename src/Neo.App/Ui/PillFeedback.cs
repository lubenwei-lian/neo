using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Neo.App.Ui;

/// <summary>The source pill's single .94 → 1 / 260ms spring, without a timer.</summary>
internal sealed class PillFeedback
{
    private static readonly ConditionalWeakTable<FrameworkElement, PillFeedback> Instances = new();
    private readonly FrameworkElement _element;
    private Visual? _visual;
    private CompositionScopedBatch? _batch;
    private CompositionAnimation? _animation;
    private int _generation;

    private PillFeedback(FrameworkElement element)
    {
        _element = element;
        element.Unloaded += (_, _) => Stop();
    }
    internal static void Play(FrameworkElement element)
    {
        if (PrototypeMotion.Enabled) Instances.GetValue(element, static e => new PillFeedback(e)).Start();
    }
    private void Start()
    {
        Stop();
        _visual ??= ElementCompositionPreview.GetElementVisual(_element);
        _visual.CenterPoint = new Vector3((float)_element.ActualWidth / 2, (float)_element.ActualHeight / 2, 0);
        var compositor = _visual.Compositor;
        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(.34f, 1.4f), new Vector2(.64f, 1));
        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(260);
        animation.InsertKeyFrame(0, new Vector3(.94f, .94f, 1));
        animation.InsertKeyFrame(1, Vector3.One, ease);
        var generation = ++_generation;
        var batch = _batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _animation = animation;
        batch.Completed += (_, _) => { if (generation == _generation) Stop(); };
        _visual.StartAnimation("Scale", animation); batch.End(); ease.Dispose();
    }
    private void Stop()
    {
        ++_generation;
        _visual?.StopAnimation("Scale");
        if (_visual != null) _visual.Scale = Vector3.One;
        var batch = _batch; _batch = null; batch?.Dispose();
        _animation?.Dispose(); _animation = null;
    }
}
