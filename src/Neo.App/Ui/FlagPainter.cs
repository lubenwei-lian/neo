using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Neo.App.Ui;

public static class FlagPainter
{
    public static void Draw(Grid target, string flag)
    {
        target.Children.Clear();
        target.Background = null;

        switch (flag)
        {
            case "jp":
                target.Background = Solid(Colors.White);
                target.Children.Add(Circle(C(0xBC, 0x00, 0x2D), 8));
                break;

            case "hk":
                target.Background = Solid(C(0xDE, 0x29, 0x10));
                target.Children.Add(Circle(Colors.White, 7));
                break;

            case "sg":
                target.Background = VGrad((0, C(0xED, 0x29, 0x39)), (0.5, C(0xED, 0x29, 0x39)),
                                          (0.5, C(0xF5, 0xF5, 0xF5)), (1, C(0xF5, 0xF5, 0xF5)));
                break;

            case "kr":
                target.Background = Solid(C(0xF5, 0xF5, 0xF5));
                target.Children.Add(CircleBrush(VGrad((0, C(0xCD, 0x2E, 0x3A)), (0.5, C(0xCD, 0x2E, 0x3A)),
                                                      (0.5, C(0x00, 0x47, 0xA0)), (1, C(0x00, 0x47, 0xA0))), 8));
                break;

            case "de":
                target.Background = VGrad((0, C(0x11, 0x11, 0x11)), (0.333, C(0x11, 0x11, 0x11)),
                                          (0.333, C(0xDD, 0x00, 0x00)), (0.666, C(0xDD, 0x00, 0x00)),
                                          (0.666, C(0xFF, 0xCE, 0x00)), (1, C(0xFF, 0xCE, 0x00)));
                break;

            case "nl":
                target.Background = VGrad((0, C(0xAE, 0x1C, 0x28)), (0.333, C(0xAE, 0x1C, 0x28)),
                                          (0.333, C(0xF5, 0xF5, 0xF5)), (0.666, C(0xF5, 0xF5, 0xF5)),
                                          (0.666, C(0x21, 0x46, 0x8B)), (1, C(0x21, 0x46, 0x8B)));
                break;

            case "us":
                target.Background = VGrad((0, C(0xB2, 0x22, 0x34)), (0.167, C(0xB2, 0x22, 0x34)),
                                          (0.167, C(0xF5, 0xF5, 0xF5)), (0.333, C(0xF5, 0xF5, 0xF5)),
                                          (0.333, C(0xB2, 0x22, 0x34)), (0.5, C(0xB2, 0x22, 0x34)),
                                          (0.5, C(0xF5, 0xF5, 0xF5)), (0.667, C(0xF5, 0xF5, 0xF5)),
                                          (0.667, C(0xB2, 0x22, 0x34)), (0.833, C(0xB2, 0x22, 0x34)),
                                          (0.833, C(0xF5, 0xF5, 0xF5)), (1, C(0xF5, 0xF5, 0xF5)));
                target.Children.Add(new Border
                {
                    Width = 9, Height = 7.5, Background = Solid(C(0x3C, 0x3B, 0x6E)),
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                });
                break;

            case "gb":
                target.Background = Solid(C(0x01, 0x21, 0x69));
                target.Children.Add(new Border
                {
                    Background = Solid(Colors.White), Height = 2.6,
                    HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center,
                });
                target.Children.Add(new Border
                {
                    Background = Solid(Colors.White), Width = 2.6,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch,
                });
                break;

            case "au":
                target.Background = Solid(C(0x00, 0x24, 0x7D));
                target.Children.Add(new Border
                {
                    Width = 9, Height = 7.5, Background = Solid(C(0xF5, 0xF5, 0xF5)),
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    Child = new Border
                    {
                        Width = 1.6, Background = Solid(C(0xCF, 0x14, 0x2B)),
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch,
                    },
                });
                break;

            case "tw":
                target.Background = Solid(C(0xFE, 0x00, 0x00));
                target.Children.Add(new Border
                {
                    Width = 11, Height = 11, Background = Solid(C(0x00, 0x00, 0x95)),
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    Child = new Ellipse
                    {
                        Width = 5.5, Height = 5.5, Fill = Solid(Colors.White),
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    },
                });
                break;

            case "other":
                target.Background = ThemeUtil.Get("NeoChipBg", Color.FromArgb(0x0C, 0x1A, 0x1D, 0x21));
                break;
        }
    }

    private static Color C(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);

    private static SolidColorBrush Solid(Color c) => new(c);

    private static Ellipse Circle(Color c, double size) => new()
    {
        Width = size, Height = size, Fill = Solid(c),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };

    private static Ellipse CircleBrush(Brush b, double size) => new()
    {
        Width = size, Height = size, Fill = b,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };

    private static LinearGradientBrush VGrad(params (double Offset, Color Color)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        foreach (var (offset, color) in stops)
            brush.GradientStops.Add(new GradientStop { Offset = offset, Color = color });
        return brush;
    }
}
