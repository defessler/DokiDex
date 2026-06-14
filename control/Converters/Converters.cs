using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace DokiCode.Control.Converters;

internal static class Ink
{
    public static SolidColorBrush Make(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        b.Freeze();
        return b;
    }
    public static readonly SolidColorBrush Teal = Make("#4ec9b0");
    public static readonly SolidColorBrush Amber = Make("#d7ba7d");
    public static readonly SolidColorBrush AmberDeep = Make("#cc8400");
    public static readonly SolidColorBrush Blue = Make("#569cd6");
    public static readonly SolidColorBrush Orange = Make("#e0913a");
    public static readonly SolidColorBrush Danger = Make("#f14c4c");
    public static readonly SolidColorBrush Grey = Make("#808080");
    public static readonly SolidColorBrush Dim = Make("#858585");
    public static readonly SolidColorBrush Text = Make("#d4d4d4");
    public static readonly SolidColorBrush Accent = Make("#0e639c");
    public static readonly SolidColorBrush Info = Make("#9cdcfe");
    public static readonly SolidColorBrush Surface2 = Make("#2d2d30");
}

// service StateKind -> status-dot brush
public sealed class StateKindToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => (value as string) switch
    {
        "healthy" => Ink.Teal,
        "starting" => Ink.Blue,
        "degraded" => Ink.Orange,
        "crashed" => Ink.Danger,
        "notinstalled" => Ink.Dim,
        _ => Ink.Grey,
    };
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

// log severity -> text brush
public sealed class SeverityToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => (value as string) switch
    {
        "error" => Ink.Danger,
        "warn" => Ink.Amber,
        "good" => Ink.Teal,
        _ => Ink.Text,
    };
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

// service group -> 3px accent brush
public sealed class GroupToAccentConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => (value as string) == "media" ? Ink.Amber : Ink.Teal;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

// ActiveMode (value) vs button's mode (parameter) -> accent when selected, else transparent
public sealed class ModeActiveBrushConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => string.Equals(value as string, p as string, StringComparison.OrdinalIgnoreCase)
            ? Ink.Accent : Ink.Surface2;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

// true -> 1.0, false -> recessed (0.5, or the double in parameter)
public sealed class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var recessed = 0.5;
        if (p is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) recessed = d;
        return (value is bool b && b) ? 1.0 : recessed;
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => (value is bool b && b) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

// bool (LowHeadroom/HotTemp) -> amber/danger else normal dim
public sealed class WarnFlagToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => (value is bool b && b) ? Ink.Amber : Ink.Dim;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}
