using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace LemonRindAvalonia.Converters;

/// <summary>Grouped model dropdown - a header row reads as a label (gray), a model row as plain selectable text.</summary>
public class BoolToHeaderForegroundConverter : IValueConverter
{
    public static readonly BoolToHeaderForegroundConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Brushes.Gray : Brushes.Black;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Grouped model dropdown - a header row's text sits slightly smaller than a real model row's, matching the sidebar's own folder-header convention.</summary>
public class BoolToHeaderFontSizeConverter : IValueConverter
{
    public static readonly BoolToHeaderFontSizeConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? 11.0 : 13.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
