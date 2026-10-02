using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace LemonRindAvalonia.Converters;

/// <summary>
/// The log console is always dark (like the Blazor port), so these colours are fixed light-on-dark rather than theme resources.
/// Severity-driven row styling for the log viewer, applied directly to
/// each row's Foreground/Background/FontWeight via these converters bound
/// in the DataTemplate - Avalonia's per-item container styling doesn't
/// give a clean data-trigger-on-bound-value story the way WPF's
/// ItemContainerStyle DataTrigger does, so this is the simpler equivalent.
/// </summary>
public class SeverityToForegroundConverter : IValueConverter
{
    public static readonly SeverityToForegroundConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value as string) switch
        {
            "Warn" => new SolidColorBrush(Color.Parse("#E5B546")),
            "Error" => new SolidColorBrush(Color.Parse("#FF6B60")),
            "Fatal" => Brushes.White,
            _ => new SolidColorBrush(Color.Parse("#D7DAE0")),
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class SeverityToBackgroundConverter : IValueConverter
{
    public static readonly SeverityToBackgroundConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value as string == "Fatal" ? new SolidColorBrush(Color.Parse("#FF3B30")) : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class SeverityToFontWeightConverter : IValueConverter
{
    public static readonly SeverityToFontWeightConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value as string) is "Error" or "Fatal" ? FontWeight.Bold : FontWeight.Normal;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
