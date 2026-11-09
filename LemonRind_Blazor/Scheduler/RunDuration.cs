namespace LemonRindBlazor.Scheduler;

/// <summary>Writes how long a scheduled run took the way a person would say it.</summary>
public static class RunDuration
{
    /// <summary>"45 s", "12 min 40 s", "2 h 5 min".</summary>
    public static string Format(double seconds)
    {
        var whole = (long)Math.Max(0, Math.Round(seconds));
        var hours = whole / 3600;
        var minutes = whole % 3600 / 60;
        var secs = whole % 60;
        if (hours > 0) return $"{hours} h {minutes} min";
        if (minutes > 0) return $"{minutes} min {secs} s";
        return $"{secs} s";
    }
}
