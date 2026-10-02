using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace LemonRindAvalonia.Converters;

/// <summary>
/// Avalonia equivalent of the VB.NET/WPF LemonRind app's
/// Converters\BooleanToHealthBrushConverter.vb - green when Lemonade is
/// reachable, red when not.
/// </summary>
public class BoolToHealthBrushConverter : IValueConverter
{
    public static readonly BoolToHealthBrushConverter Instance = new();

    private static readonly IBrush HealthyBrush = new SolidColorBrush(Color.Parse("#34C759"));
    private static readonly IBrush UnhealthyBrush = new SolidColorBrush(Color.Parse("#FF3B30"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? HealthyBrush : UnhealthyBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// A real "Lemonade healthy"/"Lemonade unreachable" label next to the dot -
/// matches the real WPF app's own header convention, and fixes a genuine
/// usability gap the empty StatusText-bound tooltip had while healthy
/// (nothing to show, so hovering the dot showed a blank tooltip with no
/// indication anything was there to check).
/// </summary>
public class BoolToHealthTextConverter : IValueConverter
{
    public static readonly BoolToHealthTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "Lemonade healthy" : "Lemonade unreachable";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
