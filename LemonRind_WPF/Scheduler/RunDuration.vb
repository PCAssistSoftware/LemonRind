Namespace Scheduler

    ''' <summary>Writes how long a scheduled run took the way a person would say it.</summary>
    Public Module RunDuration

        ''' <summary>"45 s", "12 min 40 s", "2 h 5 min".</summary>
        Public Function Format(seconds As Double) As String
            Dim whole = CLng(Math.Max(0, Math.Round(seconds)))
            Dim hours = whole \ 3600
            Dim minutes = (whole Mod 3600) \ 60
            Dim secs = whole Mod 60
            If hours > 0 Then Return $"{hours} h {minutes} min"
            If minutes > 0 Then Return $"{minutes} min {secs} s"
            Return $"{secs} s"
        End Function

    End Module

End Namespace
