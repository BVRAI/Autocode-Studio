using System.Windows;
using System.Windows.Media.Animation;

namespace AutoCode.Desktop.Controls;

/// <summary>
/// Animates a <see cref="GridLength"/> (WPF has no built-in animation for it).
/// Used to slide the sidebar (262 &lt;-&gt; 0) and side panel (0 &lt;-&gt; 372) column widths.
/// Only meaningful for absolute (pixel) GridLengths.
/// </summary>
public sealed class GridLengthAnimation : AnimationTimeline
{
    public override Type TargetPropertyType => typeof(GridLength);

    protected override Freezable CreateInstanceCore() => new GridLengthAnimation();

    public static readonly DependencyProperty FromProperty =
        DependencyProperty.Register(nameof(From), typeof(double), typeof(GridLengthAnimation),
            new PropertyMetadata(0.0));

    public double From
    {
        get => (double)GetValue(FromProperty);
        set => SetValue(FromProperty, value);
    }

    public static readonly DependencyProperty ToProperty =
        DependencyProperty.Register(nameof(To), typeof(double), typeof(GridLengthAnimation),
            new PropertyMetadata(0.0));

    public double To
    {
        get => (double)GetValue(ToProperty);
        set => SetValue(ToProperty, value);
    }

    public IEasingFunction? EasingFunction
    {
        get => (IEasingFunction?)GetValue(EasingFunctionProperty);
        set => SetValue(EasingFunctionProperty, value);
    }

    public static readonly DependencyProperty EasingFunctionProperty =
        DependencyProperty.Register(nameof(EasingFunction), typeof(IEasingFunction), typeof(GridLengthAnimation),
            new PropertyMetadata(null));

    public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
    {
        if (animationClock.CurrentProgress is not double progress)
        {
            return new GridLength(From, GridUnitType.Pixel);
        }

        var eased = EasingFunction?.Ease(progress) ?? progress;
        var current = From + (To - From) * eased;
        return new GridLength(current, GridUnitType.Pixel);
    }
}
