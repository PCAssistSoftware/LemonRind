using System.Globalization;

namespace LemonRindAvalonia.Data;

/// <summary>One model request (or one scheduled job run, or one chat turn): when it finished, in which chat, with which model, and the numbers it produced.</summary>
public sealed record UsageRequest(
    DateTime At,                 // UTC
    string SessionId,
    string Title,
    string? Model,               // null for replies saved before the model name was recorded
    int InputTokens,
    int OutputTokens,
    double TokensPerSecond,
    double TimeToFirstToken,
    bool Scheduled);             // the chat was made by a scheduled job

/// <summary>One tool call and whether it failed.</summary>
public sealed record ToolUse(DateTime At, string Name, bool Failed, string? Model, bool Scheduled);

public sealed record UsageTotals(int Requests, long InputTokens, long OutputTokens, double TokensPerSecond, double TimeToFirstToken);

/// <summary>One bar of the chart: a day, a week or a month.</summary>
public sealed record UsageBucket(DateOnly Start, string Label, long InputTokens, long OutputTokens, double TokensPerSecond);

public sealed record UsageModelRow(string Model, UsageTotals Totals, double SlowestStart);

public sealed record UsageToolRow(string Name, int Calls, int Failed)
{
    public double FailureRate => Calls == 0 ? 0 : (double)Failed / Calls;
}

public sealed record UsageChatRow(string SessionId, string Title, long Tokens);

public sealed record UsageReport(
    UsageTotals Totals,
    string BucketKind,           // "day", "week" or "month"
    List<UsageBucket> Buckets,
    List<UsageModelRow> Models,
    List<UsageToolRow> Tools,
    List<UsageChatRow> Chats,
    List<string> AvailableModels); // for the model filter, whatever the filter is set to

/// <summary>
/// Usage statistics: turning saved turn numbers into totals, a chart series and tables. Plain functions with no
/// database and no screen, so every rule can be tested with a handful of made-up rows. The same rules as the Python
/// edition's usage report.
/// </summary>
public static class UsageReports
{
    public const string UnknownModel = "Unknown (before tracking)";
    public const string AllModels = "All models";
    public const int TopChats = 5;

    private const int DayLimit = 45;   // a period up to this many days is charted day by day
    private const int WeekLimit = 400; // up to this many, week by week; longer periods are charted month by month

    public static string ModelName(string? model) => string.IsNullOrWhiteSpace(model) ? UnknownModel : model;

    /// <summary>Adds up a group of requests. Speed is weighted by output tokens: a long answer counts for more than a short one.</summary>
    public static UsageTotals TotalsOf(IReadOnlyList<UsageRequest> requests)
    {
        if (requests.Count == 0) return new UsageTotals(0, 0, 0, 0, 0);

        var timed = requests.Where(r => r.TokensPerSecond > 0 && r.OutputTokens > 0).ToList();
        var weight = timed.Sum(r => (long)r.OutputTokens);
        var speed = weight > 0 ? timed.Sum(r => r.TokensPerSecond * r.OutputTokens) / weight : 0;
        var waits = requests.Where(r => r.TimeToFirstToken > 0).Select(r => r.TimeToFirstToken).ToList();
        return new UsageTotals(
            requests.Count,
            requests.Sum(r => (long)r.InputTokens),
            requests.Sum(r => (long)r.OutputTokens),
            speed,
            waits.Count > 0 ? waits.Average() : 0);
    }

    /// <summary>Charting by day, week or month, whichever keeps the chart readable for a span of this length.</summary>
    public static string BucketKindFor(DateOnly first, DateOnly last)
    {
        var days = last.DayNumber - first.DayNumber + 1;
        if (days <= DayLimit) return "day";
        return days <= WeekLimit ? "week" : "month";
    }

    private static DateOnly BucketStart(DateOnly day, string kind) => kind switch
    {
        "week" => day.AddDays(-(((int)day.DayOfWeek + 6) % 7)), // the Monday of that week
        "month" => new DateOnly(day.Year, day.Month, 1),
        _ => day,
    };

    private static DateOnly NextBucket(DateOnly start, string kind) => kind switch
    {
        "week" => start.AddDays(7),
        "month" => start.AddMonths(1),
        _ => start.AddDays(1),
    };

    private static string Label(DateOnly start, string kind)
    {
        var culture = CultureInfo.InvariantCulture;
        if (kind == "month") return start.ToString("MMM yyyy", culture);
        return (kind == "week" ? "w/c " : "") + start.ToString("d MMM", culture);
    }

    private static DateOnly LocalDay(DateTime utc, TimeZoneInfo tz) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz));

    /// <summary>
    /// Everything the usage screen shows, for requests between since (null = from the start) and until.
    /// model limits the report to one model (UnknownModel picks the requests saved without one); null means all of them.
    /// includeScheduled false leaves out chats that scheduled jobs made. Days are the computer's own (tz), not UTC, so a
    /// late-evening reply counts for the day you had it.
    /// </summary>
    public static UsageReport Build(
        IReadOnlyList<UsageRequest> requests,
        IReadOnlyList<ToolUse> toolUses,
        DateTime? since,
        DateTime until,
        TimeZoneInfo tz,
        string? model = null,
        bool includeScheduled = true)
    {
        bool InPeriod(DateTime at) => (since is null || at >= since) && at <= until;

        // the model filter is applied after, so the filter's own list can offer every model in this period
        var inScope = requests.Where(r => InPeriod(r.At) && (includeScheduled || !r.Scheduled)).ToList();
        var available = inScope.Select(r => ModelName(r.Model)).Distinct().OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
        var selected = inScope.Where(r => model is null || ModelName(r.Model) == model).ToList();

        var toolsSelected = toolUses
            .Where(t => InPeriod(t.At) && (includeScheduled || !t.Scheduled) && (model is null || ModelName(t.Model) == model))
            .ToList();

        var (first, last) = Span(selected, since, until, tz);
        var kind = BucketKindFor(first, last);
        return new UsageReport(
            TotalsOf(selected),
            kind,
            Buckets(selected, first, last, tz, kind),
            ModelRows(selected),
            ToolRows(toolsSelected),
            ChatRows(selected),
            available);
    }

    private static (DateOnly First, DateOnly Last) Span(List<UsageRequest> selected, DateTime? since, DateTime until, TimeZoneInfo tz)
    {
        var last = LocalDay(until, tz);
        if (since is not null) return (LocalDay(since.Value, tz), last);
        if (selected.Count > 0) return (LocalDay(selected.Min(r => r.At), tz), last);
        return (last, last);
    }

    private static List<UsageBucket> Buckets(List<UsageRequest> selected, DateOnly first, DateOnly last, TimeZoneInfo tz, string kind)
    {
        var grouped = selected.GroupBy(r => BucketStart(LocalDay(r.At, tz), kind)).ToDictionary(g => g.Key, g => g.ToList());

        var buckets = new List<UsageBucket>();
        for (var start = BucketStart(first, kind); start <= last; start = NextBucket(start, kind)) // every period appears, even one with no use, so the chart has no gaps
        {
            var totals = TotalsOf(grouped.TryGetValue(start, out var group) ? group : []);
            buckets.Add(new UsageBucket(start, Label(start, kind), totals.InputTokens, totals.OutputTokens, totals.TokensPerSecond));
        }
        return buckets;
    }

    private static List<UsageModelRow> ModelRows(List<UsageRequest> selected) =>
        selected.GroupBy(r => ModelName(r.Model))
            .Select(g => new UsageModelRow(g.Key, TotalsOf(g.ToList()), g.Max(r => r.TimeToFirstToken)))
            .OrderByDescending(r => r.Totals.Requests).ThenBy(r => r.Model, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<UsageToolRow> ToolRows(List<ToolUse> toolUses) =>
        toolUses.GroupBy(t => t.Name)
            .Select(g => new UsageToolRow(g.Key, g.Count(), g.Count(t => t.Failed)))
            .OrderByDescending(r => r.Calls).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<UsageChatRow> ChatRows(List<UsageRequest> selected) =>
        selected.GroupBy(r => r.SessionId)
            .Select(g => new UsageChatRow(g.Key, g.Last().Title, g.Sum(r => (long)r.InputTokens + r.OutputTokens)))
            .Where(r => r.Tokens > 0)
            .OrderByDescending(r => r.Tokens)
            .Take(TopChats)
            .ToList();

    /// <summary>A short form of a count: 950, 1.2 k, 3.84 M.</summary>
    public static string Compact(double number)
    {
        var value = Math.Abs(number);
        var culture = CultureInfo.CurrentCulture;
        if (value < 1000) return number.ToString("N0", culture);
        if (value < 1_000_000) return (number / 1000).ToString("0.0", culture) + " k";
        return (number / 1_000_000).ToString("0.00", culture) + " M";
    }
}
