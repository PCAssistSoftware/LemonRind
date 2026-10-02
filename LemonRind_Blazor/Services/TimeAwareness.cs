namespace LemonRindBlazor.Services;

/// <summary>
/// Shared by MainViewModel's live turn context and ScheduledJobRunner's
/// system prompt - a local model has no other way to know the real current
/// date/time (its training data has a fixed cutoff). Omitting this from a scheduled job's system prompt
/// was found to leave the model inventing a plausible-sounding but wrong date for
/// anything date-relative ("this week", "later this month").
///
/// Ported directly from the VB.NET/WPF LemonRind app's Services\TimeAwareness.vb.
/// </summary>
public static class TimeAwareness
{
    public static string BuildTimeAwarenessText()
    {
        var now = DateTimeOffset.Now;
        return $"Current date/time: {now:dddd, d MMMM yyyy HH:mm} ({TimeZoneInfo.Local.DisplayName})";
    }
}
