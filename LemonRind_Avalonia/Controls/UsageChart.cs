using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using LemonRindAvalonia.Data;

namespace LemonRindAvalonia.Controls;

/// <summary>
/// The usage screen's chart, drawn directly (no chart package): stacked bars of tokens in and out per day, week or month,
/// or - with ShowSpeed - a line of tokens per second. A period with no replies has a gap in the bars and is bridged by
/// the speed line. Colours follow the theme through the same resources as the rest of the window.
/// </summary>
public class UsageChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<UsageBucket>?> BucketsProperty =
        AvaloniaProperty.Register<UsageChart, IReadOnlyList<UsageBucket>?>(nameof(Buckets));

    public static readonly StyledProperty<bool> ShowSpeedProperty =
        AvaloniaProperty.Register<UsageChart, bool>(nameof(ShowSpeed));

    public IReadOnlyList<UsageBucket>? Buckets
    {
        get => GetValue(BucketsProperty);
        set => SetValue(BucketsProperty, value);
    }

    /// <summary>False: stacked token bars. True: the speed line.</summary>
    public bool ShowSpeed
    {
        get => GetValue(ShowSpeedProperty);
        set => SetValue(ShowSpeedProperty, value);
    }

    private static readonly IBrush InBrush = new SolidColorBrush(Color.Parse("#1976D2"));
    private static readonly IBrush OutBrush = new SolidColorBrush(Color.Parse("#26A69A"));
    private static readonly IBrush SpeedBrush = new SolidColorBrush(Color.Parse("#F4A300"));

    static UsageChart()
    {
        AffectsRender<UsageChart>(BucketsProperty, ShowSpeedProperty);
    }

    private IBrush Themed(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var found) && found is IBrush brush ? brush : fallback;

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var buckets = Buckets;
        if (buckets is null || buckets.Count == 0 || Bounds.Width < 120 || Bounds.Height < 60) return;

        const double left = 56, right = 12, top = 10, bottom = 24;
        var plotWidth = Bounds.Width - left - right;
        var plotHeight = Bounds.Height - top - bottom;
        var muted = Themed("TextMuted", Brushes.Gray);
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(70, 127, 127, 127)), 1);

        var max = NiceMax(ShowSpeed ? buckets.Max(b => b.TokensPerSecond) : buckets.Max(b => (double)(b.InputTokens + b.OutputTokens)));

        foreach (var tick in new[] { 0.0, 0.5, 1.0 })
        {
            var y = top + plotHeight * (1 - tick);
            context.DrawLine(gridPen, new Point(left, y), new Point(left + plotWidth, y));
            var label = ShowSpeed ? (max * tick).ToString("0", CultureInfo.CurrentCulture) : UsageReports.Compact(max * tick);
            DrawText(context, label, left - 6, y - 7, muted, rightAligned: true);
        }

        var slot = plotWidth / buckets.Count;
        var labelStep = Math.Max(1, (int)Math.Ceiling(buckets.Count / 8.0));
        var points = new List<Point>();

        for (var i = 0; i < buckets.Count; i++)
        {
            var bucket = buckets[i];
            var centre = left + slot * i + slot / 2;

            if (ShowSpeed)
            {
                if (bucket.TokensPerSecond > 0) points.Add(new Point(centre, top + plotHeight * (1 - bucket.TokensPerSecond / max)));
            }
            else
            {
                var barWidth = Math.Max(2, slot * 0.7);
                var x = centre - barWidth / 2;
                var inHeight = plotHeight * bucket.InputTokens / max;
                var outHeight = plotHeight * bucket.OutputTokens / max;
                if (inHeight > 0) context.DrawRectangle(InBrush, null, new Rect(x, top + plotHeight - inHeight, barWidth, inHeight));
                if (outHeight > 0) context.DrawRectangle(OutBrush, null, new Rect(x, top + plotHeight - inHeight - outHeight, barWidth, outHeight));
            }

            if (i % labelStep == 0) DrawText(context, bucket.Label, centre, top + plotHeight + 5, muted, centred: true);
        }

        if (ShowSpeed)
        {
            var linePen = new Pen(SpeedBrush, 2);
            for (var i = 1; i < points.Count; i++) context.DrawLine(linePen, points[i - 1], points[i]);
            foreach (var point in points) context.DrawEllipse(SpeedBrush, null, point, 3.5, 3.5);
        }
    }

    private static void DrawText(DrawingContext context, string text, double x, double y, IBrush brush, bool rightAligned = false, bool centred = false)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11, brush);
        var origin = rightAligned ? x - formatted.Width : centred ? x - formatted.Width / 2 : x;
        context.DrawText(formatted, new Point(origin, y));
    }

    /// <summary>A round number at or above the largest value, so the chart's top line reads sensibly.</summary>
    private static double NiceMax(double value)
    {
        if (value <= 0) return 1;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (var step in new[] { 1.0, 2.0, 2.5, 5.0, 10.0 })
        {
            if (value <= step * magnitude) return step * magnitude;
        }
        return 10 * magnitude;
    }
}
