using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.CompilerServices;

namespace Neo.App.Ui;

/// <summary>
/// Explicit font sizes scale from their original value, never from a previously
/// enlarged value. Only loaded elements are retained; no polling or tree scans.
/// </summary>
public sealed class AdaptiveText : DependencyObject
{
    public static readonly DependencyProperty BaseSizeProperty = DependencyProperty.RegisterAttached(
        "BaseSize", typeof(double), typeof(AdaptiveText), new PropertyMetadata(double.NaN, OnBaseSizeChanged));
    public static readonly DependencyProperty BaseHeightProperty = DependencyProperty.RegisterAttached(
        "BaseHeight", typeof(double), typeof(AdaptiveText), new PropertyMetadata(double.NaN, OnBaseSizeChanged));
    public static readonly DependencyProperty BaseWidthProperty = DependencyProperty.RegisterAttached(
        "BaseWidth", typeof(double), typeof(AdaptiveText), new PropertyMetadata(double.NaN, OnBaseSizeChanged));

    private sealed class Registration { internal bool Hooked; }
    private static readonly ConditionalWeakTable<FrameworkElement, Registration> Registrations = new();
    private static readonly HashSet<FrameworkElement> Active = new();
    internal static double Scale { get; private set; } = 1;

    public static double GetBaseSize(DependencyObject target) => (double)target.GetValue(BaseSizeProperty);
    public static void SetBaseSize(DependencyObject target, double value) => target.SetValue(BaseSizeProperty, value);
    public static double GetBaseHeight(DependencyObject target) => (double)target.GetValue(BaseHeightProperty);
    public static void SetBaseHeight(DependencyObject target, double value) => target.SetValue(BaseHeightProperty, value);
    public static double GetBaseWidth(DependencyObject target) => (double)target.GetValue(BaseWidthProperty);
    public static void SetBaseWidth(DependencyObject target, double value) => target.SetValue(BaseWidthProperty, value);

    internal static T WithSize<T>(T element, double size) where T : FrameworkElement
    { SetBaseSize(element, size); return element; }

    internal static void UpdateViewport(double width, double height)
    {
        var scale = ResponsiveViewport.TextScale(width, height);
        if (Math.Abs(Scale - scale) < .001) return;
        Scale = scale;
        foreach (var element in Active.ToArray()) Apply(element);
    }

    private static void OnBaseSizeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement element) return;
        var registration = Registrations.GetValue(element, static _ => new Registration());
        if (!registration.Hooked)
        {
            registration.Hooked = true;
            element.Loaded += OnLoaded;
            element.Unloaded += OnUnloaded;
        }
        if (element.IsLoaded) Active.Add(element);
        Apply(element);
    }

    private static void OnLoaded(object sender, RoutedEventArgs args)
    {
        var element = (FrameworkElement)sender;
        Active.Add(element);
        Apply(element);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs args) => Active.Remove((FrameworkElement)sender);

    private static void Apply(FrameworkElement element)
    {
        var baseline = GetBaseSize(element);
        var height = GetBaseHeight(element);
        var width = GetBaseWidth(element);
        if (double.IsFinite(height) && height > 0) element.Height = ResponsiveViewport.FontSize(height, Scale);
        if (double.IsFinite(width) && width > 0) element.Width = ResponsiveViewport.FontSize(width, Scale);
        if (!double.IsFinite(baseline) || baseline <= 0) return;
        var size = ResponsiveViewport.FontSize(baseline, Scale);
        switch (element)
        {
            case TextBlock text: text.FontSize = size; break;
            case Control control: control.FontSize = size; break;
            case FontIcon icon: icon.FontSize = size; break;
            case RichTextBlock rich: rich.FontSize = size; break;
            case ContentPresenter presenter: presenter.FontSize = size; break;
        }
    }
}
