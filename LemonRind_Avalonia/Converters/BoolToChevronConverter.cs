using System.Globalization;
using Avalonia.Data.Converters;

namespace LemonRindAvalonia.Converters;

/// <summary>
/// IsCollapsed bool → a plain Unicode triangle glyph for the sidebar's
/// folder header rows. Deliberately plain BMP Unicode (▼/▶), not Segoe MDL2
/// Assets' private-use-area icon glyphs (as the WPF edition
/// does) - that font is Windows-only, and this app also runs on Linux.
/// </summary>
public class BoolToChevronConverter : IValueConverter
{
    public static readonly BoolToChevronConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "▶" : "▼";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
