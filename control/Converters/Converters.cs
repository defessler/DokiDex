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
    // Premium theme: cyan = the emitting "live" signal, gold = etched structure, functional
    // warm/red kept for genuine trouble (the dashboard's job). Matches Themes/Palette.xaml.
    public static readonly SolidColorBrush Teal = Make("#35e0f0");      // healthy / live = the emitting cyan
    public static readonly SolidColorBrush Amber = Make("#e8c77a");     // warn / structure = etched gold
    public static readonly SolidColorBrush AmberDeep = Make("#b8954e");
    public static readonly SolidColorBrush Blue = Make("#5aa9e6");      // starting
    public static readonly SolidColorBrush Orange = Make("#e0913a");    // degraded (warm signal)
    public static readonly SolidColorBrush Danger = Make("#ff6b6b");    // crashed (the one alarm)
    public static readonly SolidColorBrush Grey = Make("#5a6b78");      // offline / not-installed (cool, never alarmist)
    public static readonly SolidColorBrush Dim = Make("#7e8c99");
    public static readonly SolidColorBrush Text = Make("#e6eef6");
    public static readonly SolidColorBrush Accent = Make("#0e7490");    // deep cyan (the rare emphatic fill)
    public static readonly SolidColorBrush Info = Make("#afe3f2");
    public static readonly SolidColorBrush Surface2 = Make("#121826");
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

// service group / active GPU group -> accent brush. Three-way so an IDLE meter (group "none")
// does NOT fill with the reserved emitting cyan — idle gets a structural grey.
public sealed class GroupToAccentConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => (value as string) switch
    {
        "media" => Ink.Amber,   // MEDIA = etched gold
        "llm" => Ink.Teal,      // LLM live = the emitting cyan
        _ => Ink.Dim,           // none/idle = structural grey, never the "live" cyan
    };
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
