using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Neo.App.Services;

namespace Neo.App.Ui;

/// <summary>
/// Keeps ContentDialog's modal focus, Escape handling and DialogGuard ownership,
/// while replacing its 1.05 zoom with the prototype's capsule/container motion.
/// </summary>
internal sealed class CapsuleDialog : ContentDialog
{
    internal FrameworkElement? Anchor { get; init; }
    private CapsuleSurfaceMotion? _motion;
    private bool _closing, _finished;

    internal CapsuleDialog()
    {
        DefaultStyleKey = typeof(ContentDialog);
        Style = (Style)Application.Current.Resources["NeoCapsuleDialog"];
        Opened += async (_, _) =>
        {
            if (Anchor == null || _motion == null) { _motion?.Cancel(); return; }
            try { await _motion.OpenAsync(Anchor); }
            catch (Exception ex) { _motion.Cancel(); LogBus.App("详情动效未能启动：" + ex.GetType().Name, "Warning"); }
        };
        Closing += async (_, e) =>
        {
            if (_finished || App.IsShuttingDown || !PrototypeMotion.Enabled || Anchor == null || _motion == null) return;
            if (_closing) { e.Cancel = true; return; }
            _closing = true;
            var deferral = e.GetDeferral();
            try { await _motion.CloseAsync(Anchor); }
            catch (Exception ex) { _motion.Cancel(); LogBus.App("详情收回动效中断：" + ex.GetType().Name, "Warning"); }
            finally { _finished = true; e.Cancel = false; deferral.Complete(); }
        };
        Closed += (_, _) => { _motion?.Dispose(); _motion = null; };
    }

    internal object? ReviewMotion => _motion?.Inspect();

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        // Clear only this template's built-in entrance/exit transitions. The
        // native states still provide visibility, modality and keyboard focus.
        if (GetTemplateChild("Container") is FrameworkElement container)
            foreach (var group in VisualStateManager.GetVisualStateGroups(container))
                if (group.Name == "DialogShowingStates") group.Transitions.Clear();
        _motion?.Dispose();
        if (GetTemplateChild("RestChrome") is Border surface && GetTemplateChild("Content") is FrameworkElement inner
            && GetTemplateChild("MorphLayer") is Canvas stage)
            _motion = new CapsuleSurfaceMotion(stage, surface, inner);
        if (GetTemplateChild("SmokeLayerBackground") is Microsoft.UI.Xaml.Shapes.Rectangle smoke)
        {
            smoke.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(148, 0, 0, 0));
            smoke.Tapped += (_, _) => Hide();
        }
        if (GetTemplateChild("LayoutRoot") is FrameworkElement layout)
            layout.SizeChanged += (_, e) => { if (e.PreviousSize.Width > 0 && e.PreviousSize != e.NewSize) _motion?.Cancel(); };
    }
}
