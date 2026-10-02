using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;

namespace LemonRindAvalonia.Converters;

/// <summary>
/// Two small bool-to-visual converters driving the chat bubble's alignment/
/// color by ChatMessageViewModel.IsUser - Avalonia's equivalent of the
/// VB.NET/WPF LemonRind app's own Converters\ folder (e.g.
/// BooleanToHealthBrushConverter.vb), just Avalonia.Data.Converters'
/// IValueConverter instead of WPF's System.Windows.Data one. Basic user/assistant
/// bubble styling: blue right-aligned user, gray left-aligned assistant.
/// </summary>
public class BoolToAlignmentConverter : IValueConverter
{
    public static readonly BoolToAlignmentConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class BoolToBubbleBrushConverter : IValueConverter
{
    public static readonly BoolToBubbleBrushConverter Instance = new();

    private static readonly IBrush UserBrush = new SolidColorBrush(Color.Parse("#007AFF"));
    private static readonly IBrush AssistantBrush = new SolidColorBrush(Color.Parse("#E5E5EA"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? UserBrush : AssistantBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class BoolToBubbleForegroundConverter : IValueConverter
{
    public static readonly BoolToBubbleForegroundConverter Instance = new();

    private static readonly IBrush UserForeground = Brushes.White;
    private static readonly IBrush AssistantForeground = Brushes.Black;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? UserForeground : AssistantForeground;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
