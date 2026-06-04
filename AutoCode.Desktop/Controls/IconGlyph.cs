using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AutoCode.Desktop.Controls;

/// <summary>
/// Renders a stroke (or filled) vector icon from a <see cref="Geometry"/> resource.
/// The geometry is authored in a 20x20 coordinate space and scaled uniformly by the
/// control's Width/Height (stroke included), matching the SVG source in icons.jsx.
/// Templated in Styles/IconGlyph.xaml.
/// </summary>
public class IconGlyph : Control
{
    static IconGlyph()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(IconGlyph),
            new FrameworkPropertyMetadata(typeof(IconGlyph)));
    }

    public static readonly DependencyProperty GeometryProperty =
        DependencyProperty.Register(nameof(Geometry), typeof(Geometry), typeof(IconGlyph),
            new FrameworkPropertyMetadata(null));

    public Geometry? Geometry
    {
        get => (Geometry?)GetValue(GeometryProperty);
        set => SetValue(GeometryProperty, value);
    }

    public static readonly DependencyProperty StrokeThicknessProperty =
        DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(IconGlyph),
            new FrameworkPropertyMetadata(1.7));

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public static readonly DependencyProperty FilledProperty =
        DependencyProperty.Register(nameof(Filled), typeof(bool), typeof(IconGlyph),
            new FrameworkPropertyMetadata(false));

    public bool Filled
    {
        get => (bool)GetValue(FilledProperty);
        set => SetValue(FilledProperty, value);
    }
}
