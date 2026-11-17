using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Neo.App.Ui;

/// <summary>
/// The source switch-d.css: fixed 44 x 26 geometry, a clipped accent circle,
/// 320ms knob travel and 90ms press feedback. No layout animation or timer.
/// ToggleSwitch still owns input, accessibility, IsOn and the Toggled event.
/// </summary>
public sealed class SwitchMotion : DependencyObject
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SwitchMotion), new PropertyMetadata(false, OnEnabledChanged));
    private static readonly ConditionalWeakTable<ToggleSwitch, Motion> Instances = new();
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);
    internal static void PressForReview(ToggleSwitch toggle, bool pressed)
    {
        if (!NativeUiReview.Enabled) throw new InvalidOperationException();
        if (Instances.TryGetValue(toggle, out var motion)) motion.SetPressed(pressed);
    }

    private static void OnEnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not ToggleSwitch toggle) return;
        if ((bool)args.NewValue) Instances.GetValue(toggle, static t => new Motion(t)).Attach();
        else if (Instances.TryGetValue(toggle, out var motion)) motion.Detach();
    }

    private sealed class Motion(ToggleSwitch toggle)
    {
        private bool _attached, _pressed;
        private Visual? _root, _knob, _accent;
        private CompositionGeometricClip? _clip;
        private CompositionRoundedRectangleGeometry? _geometry;

        internal void Attach()
        {
            if (_attached) return;
            _attached = true;
            toggle.Loaded += Loaded;
            toggle.Unloaded += Unloaded;
            toggle.Toggled += Toggled;
            toggle.IsEnabledChanged += EnabledChanged;
            toggle.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Pressed), true);
            toggle.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Released), true);
            toggle.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(Released), true);
            toggle.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(Released), true);
            toggle.PointerExited += Released;
            toggle.KeyDown += KeyDown;
            toggle.KeyUp += KeyUp;
            toggle.LostFocus += LostFocus;
            if (toggle.IsLoaded) Initialize();
        }

        internal void Detach()
        {
            if (!_attached) return;
            _attached = false;
            toggle.Loaded -= Loaded;
            toggle.Unloaded -= Unloaded;
            toggle.Toggled -= Toggled;
            toggle.IsEnabledChanged -= EnabledChanged;
            toggle.RemoveHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Pressed));
            toggle.RemoveHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Released));
            toggle.RemoveHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(Released));
            toggle.RemoveHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(Released));
            toggle.PointerExited -= Released;
            toggle.KeyDown -= KeyDown;
            toggle.KeyUp -= KeyUp;
            toggle.LostFocus -= LostFocus;
            ReleaseVisuals();
        }

        private static FrameworkElement? Find(DependencyObject parent, string name)
        {
            if (parent is FrameworkElement element && element.Name == name) return element;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                if (Find(VisualTreeHelper.GetChild(parent, i), name) is { } found) return found;
            return null;
        }

        private void Loaded(object sender, RoutedEventArgs args) => Initialize();
        private void Unloaded(object sender, RoutedEventArgs args) => ReleaseVisuals();
        private void Initialize()
        {
            ReleaseVisuals();
            toggle.ApplyTemplate();
            if (Find(toggle, "SwitchRoot") is not { } root ||
                Find(toggle, "Knob") is not { } knob || Find(toggle, "AccentReveal") is not { } accent) return;
            _root = ElementCompositionPreview.GetElementVisual(root);
            _knob = ElementCompositionPreview.GetElementVisual(knob);
            _accent = ElementCompositionPreview.GetElementVisual(accent);
            ElementCompositionPreview.SetIsTranslationEnabled(knob, true);
            _root.CenterPoint = new Vector3(22, 13, 0);
            _knob.CenterPoint = _accent.CenterPoint = new Vector3(10, 10, 0);
            _geometry = _root.Compositor.CreateRoundedRectangleGeometry();
            _geometry.Size = new Vector2(44, 26);
            _geometry.CornerRadius = new Vector2(13, 13);
            _clip = _root.Compositor.CreateGeometricClip(_geometry);
            _root.Clip = _clip;
            Apply(false);
        }

        private void Toggled(object sender, RoutedEventArgs args) => Apply(true);
        private void EnabledChanged(object sender, DependencyPropertyChangedEventArgs args)
        {
            if (!toggle.IsEnabled) SetPressed(false);
        }
        private void Pressed(object sender, PointerRoutedEventArgs args)
        {
            if (toggle.IsEnabled && args.GetCurrentPoint(toggle).Properties.IsLeftButtonPressed) SetPressed(true);
        }
        private void Released(object sender, PointerRoutedEventArgs args) => SetPressed(false);
        private void LostFocus(object sender, RoutedEventArgs args) => SetPressed(false);
        private void KeyDown(object sender, KeyRoutedEventArgs args)
        {
            if (toggle.IsEnabled && args.Key is VirtualKey.Space or VirtualKey.Enter) SetPressed(true);
        }
        private void KeyUp(object sender, KeyRoutedEventArgs args)
        {
            if (args.Key is VirtualKey.Space or VirtualKey.Enter) SetPressed(false);
        }
        internal void SetPressed(bool pressed)
        {
            if (_pressed == pressed) return;
            _pressed = pressed;
            if (_root == null || _knob == null) return;
            Animate(_root, "Scale", pressed ? new Vector3(.955f, .955f, 1) : Vector3.One, 90, null, null, true);
            Animate(_knob, "Scale", pressed ? new Vector3(1.1f, .92f, 1) : Vector3.One, 320,
                new Vector2(.34f, 1.4f), new Vector2(.55f, 1), true);
        }
        private void Apply(bool animate)
        {
            if (_knob == null || _accent == null) return;
            Animate(_knob, "Translation", new Vector3(toggle.IsOn ? 18 : 0, 0, 0), 320,
                new Vector2(.34f, 1.4f), new Vector2(.55f, 1), animate);
            var scale = toggle.IsOn ? 3.6f : 0;
            Animate(_accent, "Scale", new Vector3(scale, scale, 1), 300,
                new Vector2(.22f, .9f), new Vector2(.24f, 1), animate);
        }
        private static void Animate(Visual visual, string property, Vector3 target, int milliseconds,
            Vector2? control1, Vector2? control2, bool animate)
        {
            if (!animate || !PrototypeMotion.Enabled)
            {
                visual.StopAnimation(property);
                if (property == "Translation") visual.Properties.InsertVector3(property, target);
                else visual.Scale = target;
                return;
            }
            using var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
            animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
            using CompositionEasingFunction ease = control1.HasValue && control2.HasValue
                ? visual.Compositor.CreateCubicBezierEasingFunction(control1.Value, control2.Value)
                : visual.Compositor.CreateLinearEasingFunction();
            animation.InsertKeyFrame(1, target, ease);
            // A final-only keyframe starts from the current presentation value,
            // including when the user reverses the switch during its travel.
            visual.StartAnimation(property, animation);
        }
        private void ReleaseVisuals()
        {
            _pressed = false;
            _root?.StopAnimation("Scale");
            _knob?.StopAnimation("Translation");
            _knob?.StopAnimation("Scale");
            _accent?.StopAnimation("Scale");
            if (_root != null) { _root.Scale = Vector3.One; _root.Clip = null; }
            if (_knob != null) _knob.Scale = Vector3.One;
            _clip?.Dispose(); _clip = null;
            _geometry?.Dispose(); _geometry = null;
            _root = _knob = _accent = null;
        }
    }
}
