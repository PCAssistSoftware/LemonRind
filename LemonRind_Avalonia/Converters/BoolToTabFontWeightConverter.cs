using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace LemonRindAvalonia.Converters;

/// <summary>Right panel's Stats/Modules tab buttons - the active one renders bold, no other selected-state chrome for this first pass.</summary>
public class BoolToTabFontWeightConverter : IValueConverter
{
    public static readonly BoolToTabFontWeightConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? FontWeight.Bold : FontWeight.Normal;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
