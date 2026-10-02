using System.Globalization;
using Avalonia.Data.Converters;

namespace LemonRindAvalonia.Converters;

/// <summary>
/// value-equals-parameter → bool, driving the Settings screen's left-nav
/// section switching (each section's panel binds its own IsVisible to
/// {Binding SelectedSection} with ConverterParameter="ThatSection'sName").
/// Ported in spirit from the VB.NET/WPF LemonRind app's own
/// SectionVisibilityConverter.vb.
/// </summary>
public class SectionVisibilityConverter : IValueConverter
{
    public static readonly SectionVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value as string, parameter as string, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
