using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace AutoCode.Desktop.Converters;

/// <summary>bool -> Visibility. ConverterParameter "invert" flips the mapping.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Visible;
}

/// <summary>null/empty -> Collapsed, otherwise Visible. "invert" flips.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var has = value is not null && value is not string s ? true : !string.IsNullOrEmpty(value as string);
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase))
        {
            has = !has;
        }

        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Resolves a status string ("done"/"running"/"error") to a foreground brush resource.</summary>
public sealed class ToolStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = (value as string)?.ToLowerInvariant() switch
        {
            "done" => parameter as string == "bg" ? "GreenBgBrush" : "GreenBrush",
            "error" => parameter as string == "bg" ? "RedBgBrush" : "RedBrush",
            _ => parameter as string == "bg" ? "AccentSoftBrush" : "AccentBrush",
        };

        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Resolves a status string to a status-pill geometry (check / spinner / x).</summary>
public sealed class ToolStatusToGeometryConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = (value as string)?.ToLowerInvariant() switch
        {
            "done" => "IconCheck",
            "error" => "IconX",
            _ => "IconSpinner",
        };

        return Application.Current?.TryFindResource(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Milliseconds -> "0.1s" / "11.4s" / "320ms".</summary>
public sealed class DurationMsToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var ms = value switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            _ => 0L,
        };

        if (ms <= 0)
        {
            return string.Empty;
        }

        return $"{ms / 1000.0:0.0}s";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>DateTimeOffset -> short relative time ("3h", "2w", "1mo").</summary>
public sealed class RelativeTimeConverter : IValueConverter
{
    public static string Format(DateTimeOffset then, DateTimeOffset now)
    {
        var span = now - then;
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        if (span.TotalMinutes < 1) return "now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays}d";
        if (span.TotalDays < 30) return $"{(int)(span.TotalDays / 7)}w";
        if (span.TotalDays < 365) return $"{(int)(span.TotalDays / 30)}mo";
        return $"{(int)(span.TotalDays / 365)}y";
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is DateTimeOffset dto ? Format(dto, DateTimeOffset.Now) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Returns true when the bound value equals the ConverterParameter (string compare).</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? parameter! : Binding.DoNothing;
}
