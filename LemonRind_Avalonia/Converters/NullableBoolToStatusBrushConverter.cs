using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace LemonRindAvalonia.Converters;

/// <summary>
/// A tool call's real outcome as a colored dot: gray while
/// pending (Succeeded is still null - the call was made but no result has
/// arrived yet), green on success, red on failure. A plain Ellipse rather
/// than a ✓/✗ glyph deliberately - this app's default font has no reliable
/// cross-platform Unicode-symbol coverage (the sidebar's own
/// icon buttons rendered as unrecognizable "tofu" boxes for exactly this
/// reason), so a vector shape sidesteps that risk entirely, reusing the same
/// colored-dot convention already proven on the Modules tab and the
/// Lemonade health indicator.
/// </summary>
public class NullableBoolToStatusBrushConverter : IValueConverter
{
    public static readonly NullableBoolToStatusBrushConverter Instance = new();

    private static readonly IBrush PendingBrush = new SolidColorBrush(Color.Parse("#C7C7CC"));
    private static readonly IBrush SucceededBrush = new SolidColorBrush(Color.Parse("#34C759"));
    private static readonly IBrush FailedBrush = new SolidColorBrush(Color.Parse("#FF3B30"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            true => SucceededBrush,
            false => FailedBrush,
            _ => PendingBrush,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
