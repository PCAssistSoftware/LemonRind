using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace LemonRindAvalonia.Controls;

/// <summary>
/// A small vector icon (stroke style, 24x24 design grid, shapes from the Lucide icon set - ISC licence): drawn as geometry,
/// not as a font glyph or emoji, so it looks identical on every OS (emoji can render as blank "tofu" boxes on some Linux
/// setups). It takes its colour from the inherited text colour, so it follows the button/theme it sits in.
/// Usage: &lt;ctl:Icon Kind="plus" Width="16" Height="16" /&gt;.
/// </summary>
public class Icon : Control
{
    private static readonly Dictionary<string, string> Shapes = new()
    {
        ["plus"] = "M5 12h14 M12 5v14",
        ["check"] = "M20 6 9 17l-5-5",
        ["settings"] = "M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z M15 12 A3 3 0 1 1 9 12 A3 3 0 1 1 15 12 Z",
        ["more"] = "M13 12 A1 1 0 1 1 11 12 A1 1 0 1 1 13 12 Z M13 5 A1 1 0 1 1 11 5 A1 1 0 1 1 13 5 Z M13 19 A1 1 0 1 1 11 19 A1 1 0 1 1 13 19 Z",
        ["send"] = "M5 12 L12 5 L19 12 M12 19 V5",
        ["stop"] = "M7 6 H17 A1 1 0 0 1 18 7 V17 A1 1 0 0 1 17 18 H7 A1 1 0 0 1 6 17 V7 A1 1 0 0 1 7 6 Z",
        ["paperclip"] = "m21.44 11.05-9.19 9.19a6 6 0 0 1-8.49-8.49l8.57-8.57A4 4 0 1 1 18 8.84l-8.59 8.57a2 2 0 0 1-2.83-2.83l8.49-8.48",
        ["info"] = "M22 12 A10 10 0 1 1 2 12 A10 10 0 1 1 22 12 Z M12 16 V12 M12 8 H12.01",
        ["copy"] = "M10 8 H20 A2 2 0 0 1 22 10 V20 A2 2 0 0 1 20 22 H10 A2 2 0 0 1 8 20 V10 A2 2 0 0 1 10 8 Z M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2",
        ["download"] = "M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4 M7 10 L12 15 L17 10 M12 15 V3",
        ["pencil"] = "M21.174 6.812a1 1 0 0 0-3.986-3.987L3.842 16.174a2 2 0 0 0-.5.83l-1.321 4.352a.5.5 0 0 0 .623.622l4.353-1.32a2 2 0 0 0 .83-.497z M15 5 L19 9",
        ["folder"] = "M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z",
        ["tag"] = "M12.586 2.586A2 2 0 0 0 11.172 2H4a2 2 0 0 0-2 2v7.172a2 2 0 0 0 .586 1.414l8.704 8.704a2.426 2.426 0 0 0 3.42 0l6.58-6.58a2.426 2.426 0 0 0 0-3.42z M7.6 7.5 A0.1 0.1 0 1 1 7.4 7.5 A0.1 0.1 0 1 1 7.6 7.5 Z",
        ["trash"] = "M3 6h18 M19 6v14c0 1-1 2-2 2H7c-1 0-2-1-2-2V6 M8 6V4c0-1 1-2 2-2h4c1 0 2 1 2 2v2",
        ["chevron-right"] = "m9 18 6-6-6-6",
        ["chevron-down"] = "m6 9 6 6 6-6",
        ["x"] = "M18 6 6 18 M6 6 18 18",
        ["terminal"] = "M4 17 L10 11 L4 5 M12 19 H20",
        ["file-text"] = "M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7Z M14 2v4a2 2 0 0 0 2 2h4 M10 9H8 M16 13H8 M16 17H8",
        ["layers"] = "m12.83 2.18a2 2 0 0 0-1.66 0L2.6 6.08a1 1 0 0 0 0 1.83l8.58 3.91a2 2 0 0 0 1.66 0l8.58-3.9a1 1 0 0 0 0-1.83Z M22 17.65l-9.17 4.16a2 2 0 0 1-1.66 0L2 17.65 M22 12.65l-9.17 4.16a2 2 0 0 1-1.66 0L2 12.65",
        ["wrench"] = "M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76z",
        ["arrow-left"] = "m12 19-7-7 7-7 M19 12H5",
        ["bot"] = "M12 8V4H8 M6 8H18A2 2 0 0 1 20 10V18A2 2 0 0 1 18 20H6A2 2 0 0 1 4 18V10A2 2 0 0 1 6 8Z M2 14H4 M20 14H22 M15 13V15 M9 13V15",
    };

    private static readonly Dictionary<string, Geometry> Cache = new();

    public static readonly StyledProperty<string?> KindProperty =
        AvaloniaProperty.Register<Icon, string?>(nameof(Kind));

    public string? Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    static Icon()
    {
        AffectsRender<Icon>(KindProperty, TextElement.ForegroundProperty);
        AffectsMeasure<Icon>(KindProperty);
    }

    public Icon()
    {
        Width = 16;
        Height = 16;
        IsHitTestVisible = false;
    }

    public override void Render(DrawingContext context)
    {
        var kind = Kind;
        if (kind is null || !Shapes.TryGetValue(kind, out var data)) return;

        if (!Cache.TryGetValue(kind, out var geometry))
        {
            geometry = Geometry.Parse(data);
            Cache[kind] = geometry;
        }

        var brush = TextElement.GetForeground(this) ?? Brushes.Gray;
        var pen = new Pen(brush, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 24.0;
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            context.DrawGeometry(null, pen, geometry);
        }
    }
}
