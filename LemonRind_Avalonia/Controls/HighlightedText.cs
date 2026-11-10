using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Styling;
using LemonRindAvalonia.Data;

namespace LemonRindAvalonia.Controls;

/// <summary>
/// A TextBlock that draws a list of pieces of text, the ones the chat search matched with a coloured background
/// (the "MatchBg" colour in App.axaml, which has a light and a dark value). A TextBlock's own Inlines cannot be bound
/// to, so this builds them from <see cref="Parts"/> whenever that changes.
/// Usage: &lt;ctl:HighlightedText Parts="{Binding TitleParts}" /&gt;.
/// </summary>
public class HighlightedText : TextBlock
{
    public static readonly StyledProperty<IReadOnlyList<HighlightPart>?> PartsProperty =
        AvaloniaProperty.Register<HighlightedText, IReadOnlyList<HighlightPart>?>(nameof(Parts));

    public IReadOnlyList<HighlightPart>? Parts
    {
        get => GetValue(PartsProperty);
        set => SetValue(PartsProperty, value);
    }

    // A TextBlock subclass takes the TextBlock styles (font, colour, trimming) only when it says it is one.
    protected override Type StyleKeyOverride => typeof(TextBlock);

    static HighlightedText()
    {
        PartsProperty.Changed.AddClassHandler<HighlightedText>((control, _) => control.Rebuild());
    }

    private void Rebuild()
    {
        Inlines?.Clear();
        Text = null;
        if (Parts is null) return;

        foreach (var part in Parts)
        {
            var run = new Run(part.Text);
            if (part.IsMatch) run.Bind(TextElement.BackgroundProperty, this.GetResourceObservable("MatchBg"));
            Inlines!.Add(run);
        }
    }
}
