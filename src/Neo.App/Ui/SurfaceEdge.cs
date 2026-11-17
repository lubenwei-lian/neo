using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Neo.App.Ui;

/// <summary>Source inset highlight and cached two-layer shadow; no layout children or polling.</summary>
public sealed class SurfaceEdge : DependencyObject
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SurfaceEdge), new PropertyMetadata(false, Changed));
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);
    public static readonly DependencyProperty NodeSelectedProperty = DependencyProperty.RegisterAttached(
        "NodeSelected", typeof(bool), typeof(SurfaceEdge), new PropertyMetadata(false, NodeAppearanceChanged));
    public static bool GetNodeSelected(DependencyObject target) => (bool)target.GetValue(NodeSelectedProperty);
    public static void SetNodeSelected(DependencyObject target, bool value) => target.SetValue(NodeSelectedProperty, value);
    public static readonly DependencyProperty NodeHoveredProperty = DependencyProperty.RegisterAttached(
        "NodeHovered", typeof(bool), typeof(SurfaceEdge), new PropertyMetadata(false, NodeAppearanceChanged));
    public static bool GetNodeHovered(DependencyObject target) => (bool)target.GetValue(NodeHoveredProperty);
    public static void SetNodeHovered(DependencyObject target, bool value) => target.SetValue(NodeHoveredProperty, value);
    private static readonly ConditionalWeakTable<Border, Edge> Edges = new();
    private static LoadedImageSurface? _shadowSurface;
    private static LoadedImageSurface? _nodeShadowSurface;

    private static void NodeAppearanceChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is Border border && Edges.TryGetValue(border, out var edge)) edge.UpdateAppearance();
    }

    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not Border border) return;
        var edge = Edges.GetValue(border, b => new Edge(b));
        if ((bool)args.NewValue) edge.Start(); else edge.Stop();
    }

    private sealed class Edge
    {
        private readonly Border _border;
        private ShapeVisual? _visual;
        private ContainerVisual? _layer;
        private SpriteVisual? _shadow;
        private CompositionRoundedRectangleGeometry? _geometry;
        private CompositionColorGradientStop? _topInk;
        private long _radiusToken;
        internal Edge(Border border)
        {
            _border = border;
            border.Loaded += (_, _) => { if (GetEnabled(border)) Start(); };
            border.Unloaded += (_, _) => { Stop(); SetNodeHovered(border, false); };
            border.SizeChanged += (_, _) => Resize();
            border.ActualThemeChanged += (_, _) => UpdateAppearance();
        }
        internal void Start()
        {
            if (!_border.IsLoaded || _visual != null || _border.BorderThickness.Top == 0) return;
            var compositor = ElementCompositionPreview.GetElementVisual(_border).Compositor;
            _geometry = compositor.CreateRoundedRectangleGeometry();
            var ink = compositor.CreateLinearGradientBrush();
            ink.StartPoint = Vector2.Zero; ink.EndPoint = new Vector2(0, 1);
            _topInk = compositor.CreateColorGradientStop(0, Color.FromArgb(26, 255, 255, 255));
            ink.ColorStops.Add(_topInk);
            ink.ColorStops.Add(compositor.CreateColorGradientStop(.16f, Color.FromArgb(0, 255, 255, 255)));
            ink.ColorStops.Add(compositor.CreateColorGradientStop(1, Color.FromArgb(0, 255, 255, 255)));
            var outline = compositor.CreateSpriteShape(_geometry); outline.StrokeBrush = ink; outline.StrokeThickness = 1;
            _visual = compositor.CreateShapeVisual(); _visual.Shapes.Add(outline);
            _layer = compositor.CreateContainerVisual();
            // The transparent centre keeps the shadow outside the surface.
            // Nine-grid stretching preserves the original 26px corner and blur,
            // and reuses one 192px bitmap for every card instead of live blur.
            var node = Math.Abs(_border.CornerRadius.TopLeft - 22) < .1;
            if (node) _nodeShadowSurface ??= LoadedImageSurface.StartLoadFromUri(new Uri("ms-appx:///Assets/node-shadow.png"));
            else _shadowSurface ??= LoadedImageSurface.StartLoadFromUri(new Uri("ms-appx:///Assets/surface-shadow.png"));
            var source = compositor.CreateSurfaceBrush(node ? _nodeShadowSurface : _shadowSurface);
            source.Stretch = CompositionStretch.Fill;
            var shadowBrush = compositor.CreateNineGridBrush(); shadowBrush.Source = source;
            shadowBrush.LeftInset = shadowBrush.TopInset = shadowBrush.RightInset = shadowBrush.BottomInset = node ? 34 : 78;
            shadowBrush.IsCenterHollow = true;
            _shadow = compositor.CreateSpriteVisual(); _shadow.Brush = shadowBrush;
            _shadow.Offset = node ? new Vector3(-12,-12,0) : new Vector3(-52,-52,0);
            _layer.Children.InsertAtBottom(_shadow); _layer.Children.InsertAtTop(_visual);
            ElementCompositionPreview.SetElementChildVisual(_border, _layer);
            _radiusToken = _border.RegisterPropertyChangedCallback(Border.CornerRadiusProperty, (_, _) => Resize());
            Resize();
        }
        private void Resize()
        {
            if (_visual == null || _geometry == null) return;
            _visual.Size = new Vector2((float)_border.ActualWidth, (float)_border.ActualHeight);
            if (_layer != null) _layer.Size = _visual.Size;
            if (_shadow != null)
            {
                var node = Math.Abs(_border.CornerRadius.TopLeft - 22) < .1;
                _shadow.Size = _visual.Size + (node ? new Vector2(24,24) : new Vector2(104,104));
            }
            _geometry.Offset = new Vector2(.5f, .5f);
            _geometry.Size = new Vector2(Math.Max(0, (float)_border.ActualWidth - 1), Math.Max(0, (float)_border.ActualHeight - 1));
            _geometry.CornerRadius = new Vector2((float)Math.Max(0, _border.CornerRadius.TopLeft - .5));
            UpdateAppearance();
        }
        internal void UpdateAppearance()
        {
            if (_visual == null || _topInk == null || _shadow == null) return;
            var node = Math.Abs(_border.CornerRadius.TopLeft - 22) < .1;
            var selected = GetNodeSelected(_border);
            var hovered = GetNodeHovered(_border);
            _topInk.Color = node
                ? selected ? Color.FromArgb(41,158,205,255) : Color.FromArgb(hovered ? (byte)23 : (byte)11,255,255,255)
                : Color.FromArgb(26,255,255,255);
            _shadow.Opacity = node ? selected || hovered ? 1 : 0
                : Math.Abs(_border.CornerRadius.TopLeft - 26) < .1 && _visual.Size.X > 156 && _visual.Size.Y > 52 ? 1 : 0;
            if (_border.ActualTheme == ElementTheme.Light)
            {
                _topInk.Color = Color.FromArgb(node ? (byte)8 : (byte)12, 0, 0, 0);
                _shadow.Opacity *= .18f;
            }
        }
        internal void Stop()
        {
            if (_visual == null) return;
            _border.UnregisterPropertyChangedCallback(Border.CornerRadiusProperty, _radiusToken);
            ElementCompositionPreview.SetElementChildVisual(_border, null);
            _visual.Dispose(); _visual = null; _shadow?.Dispose(); _shadow = null;
            _layer?.Dispose(); _layer = null; _geometry?.Dispose(); _geometry = null;
            _topInk = null;
        }
    }
}
