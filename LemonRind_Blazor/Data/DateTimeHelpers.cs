using System.Globalization;

namespace LemonRindBlazor.Data;

/// <summary>
/// Every timestamp in this app is written via
/// DateTime.UtcNow.ToString("o") and needs to come back as real UTC
/// (Kind = Utc) - NOT silently converted to local time, which is what
/// plain DateTime.Parse does for a "Z"-suffixed ISO string unless told
/// otherwise (DateTimeStyles.RoundtripKind is required to actually
/// preserve it). Without this, a value compared against a fresh
/// DateTime.UtcNow could be silently shifted by the machine's local UTC
/// offset.
///
/// Ported directly from the VB.NET/WPF LemonRind app's
/// Data\DateTimeHelpers.vb - a real, hard-won lesson (a landmine
/// repeated across several repositories before being extracted here), not
/// something to quietly re-derive differently.
/// </summary>
public static class DateTimeHelpers
{
    public static DateTime ParseUtc(string text)
        => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
