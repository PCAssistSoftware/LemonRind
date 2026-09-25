Namespace Services

    ''' <summary>
    ''' Shared by MainViewModel's live turn-context (BuildLiveTurnContextTextAsync)
    ''' and ScheduledJobRunner's system prompt - a local model has no other
    ''' way to know the real current date/time (its training data has a
    ''' fixed cutoff), and confirmed live that omitting this from a
    ''' scheduled job's system prompt (the one place it used to be missing)
    ''' left the model inventing a plausible-sounding but wrong date for
    ''' anything date-relative ("this week", "later this month") instead of
    ''' reasoning from the real one.
    ''' </summary>
    Public Class TimeAwareness

        Public Shared Function BuildTimeAwarenessText() As String
            Dim now = DateTimeOffset.Now
            Return $"Current date/time: {now:dddd, d MMMM yyyy HH:mm} ({TimeZoneInfo.Local.DisplayName})"
        End Function

    End Class

End Namespace
