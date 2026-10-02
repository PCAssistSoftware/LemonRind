using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace LemonRindAvalonia.Converters;

/// <summary>
/// A local file path string → Bitmap, for the generated-image
/// bubble - Avalonia's Image.Source is an IImage, not a plain path, so
/// binding the path string directly wouldn't render anything.
/// </summary>
public class PathToBitmapConverter : IValueConverter
{
    public static readonly PathToBitmapConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            return new Bitmap(path);
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
