using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using MdAvalonia = Markdown.Avalonia;

namespace LemonRindAvalonia.Converters;

/// <summary>
/// A chat bubble's finished (non-streaming) text - Markdown syntax rendered
/// as real controls, using Markdown.Avalonia.Tight's Markdown.Transform,
/// confirmed via reflection against the installed package to return a plain
/// Avalonia.Controls.Control tree that embeds directly in this app's own
/// per-message layout without a nested ScrollViewer (unlike that library's
/// own MarkdownScrollViewer control, which brings its own scrolling - not
/// wanted here, since every bubble already sits inside the message list's
/// single outer ScrollViewer).
///
/// A MultiBinding (Text + IsStreaming), not a plain single-value converter
/// bound to Text alone - a plain binding would still re-evaluate (and
/// re-parse the whole string into a fresh control tree) on every one of a
/// long reply's many streamed chunks even while this ContentControl sits
/// IsVisible="False", since Avalonia's binding engine doesn't skip
/// evaluation just because the target is hidden. Short-circuiting on
/// IsStreaming here is what actually avoids that cost, not the IsVisible
/// binding in the DataTemplate.
/// </summary>
public class MarkdownToControlConverter : IMultiValueConverter
{
    public static readonly MarkdownToControlConverter Instance = new();

    private readonly MdAvalonia.Markdown _engine = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = values.Count > 0 ? values[0] as string ?? "" : "";
        var isStreaming = values.Count > 1 && values[1] is true;
        if (isStreaming) return null;

        try
        {
            return _engine.Transform(text);
        }
        catch
        {
            // Malformed input shouldn't take the whole bubble down - falls
            // back to the same plain wrapped text the streaming TextBox
            // already shows.
            return new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        }
    }
}
