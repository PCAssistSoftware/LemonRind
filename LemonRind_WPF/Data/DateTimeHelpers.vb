Imports System.Globalization

Namespace Data

    ''' <summary>
    ''' Every timestamp in this app is written via `DateTime.UtcNow.ToString("o")`
    ''' and needs to come back as real UTC (Kind = Utc) - NOT silently
    ''' converted to local time, which is exactly what plain `DateTime.Parse`
    ''' does for a "Z"-suffixed ISO string unless told otherwise
    ''' (`DateTimeStyles.RoundtripKind` is required to actually preserve it).
    ''' Without this, a value compared against a fresh `DateTime.UtcNow`
    ''' (e.g. the Scheduler deciding what's due) would be silently shifted by
    ''' the machine's local UTC offset, since `DateTime.Parse` on its own
    ''' would misinterpret the value as local time despite it genuinely being
    ''' UTC. Used by every repository in the app (Sessions, Memories,
    ''' Knowledge Bases, MCP Servers, Scheduler) so there's one shared,
    ''' correct parse rather than the same landmine repeated at every call
    ''' site.
    ''' </summary>
    Public Module DateTimeHelpers

        Public Function ParseUtc(text As String) As DateTime
            Return DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
        End Function

    End Module

End Namespace
