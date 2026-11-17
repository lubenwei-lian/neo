using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Neo.App.Ui;

/// <summary>Keep circular end caps at the actual arranged height, including DPI
/// and adaptive sizing. A CSS-style 999px radius becomes an ellipse in XAML.</summary>
public sealed class PillShape : DependencyObject
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(PillShape), new PropertyMetadata(false, OnEnabledChanged));
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not FrameworkElement element) return;
        element.SizeChanged -= OnSizeChanged; element.Loaded -= OnLoaded;
        if (!(bool)args.NewValue) return;
        element.SizeChanged += OnSizeChanged; element.Loaded += OnLoaded;
        Apply(element);
    }
    private static void OnSizeChanged(object sender, SizeChangedEventArgs args) => Apply((FrameworkElement)sender);
    private static void OnLoaded(object sender, RoutedEventArgs args) => Apply((FrameworkElement)sender);
    private static void Apply(FrameworkElement element)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0) return;
        var radius = new CornerRadius(Math.Min(element.ActualWidth, element.ActualHeight) / 2);
        switch (element)
        {
            case Control control: if (control.CornerRadius != radius) control.CornerRadius = radius; break;
            case Border border: if (border.CornerRadius != radius) border.CornerRadius = radius; break;
            case Grid grid: if (grid.CornerRadius != radius) grid.CornerRadius = radius; break;
        }
    }
}
