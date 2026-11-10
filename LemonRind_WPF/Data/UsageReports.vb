Imports System.Globalization

Namespace Data

    ''' <summary>One chat turn or scheduled job run: when it finished, in which chat, with which model, and the numbers it produced.</summary>
    Public NotInheritable Class UsageRequest

        Public ReadOnly Property At As DateTime ' UTC
        Public ReadOnly Property SessionId As String
        Public ReadOnly Property Title As String
        Public ReadOnly Property Model As String ' Nothing for replies saved before the model name was recorded
        Public ReadOnly Property InputTokens As Integer
        Public ReadOnly Property OutputTokens As Integer
        Public ReadOnly Property TokensPerSecond As Double
        Public ReadOnly Property TimeToFirstToken As Double
        Public ReadOnly Property Scheduled As Boolean ' the chat was made by a scheduled job

        Public Sub New(at As DateTime, sessionId As String, title As String, model As String, inputTokens As Integer, outputTokens As Integer, tokensPerSecond As Double, timeToFirstToken As Double, scheduled As Boolean)
            Me.At = at
            Me.SessionId = sessionId
            Me.Title = title
            Me.Model = model
            Me.InputTokens = inputTokens
            Me.OutputTokens = outputTokens
            Me.TokensPerSecond = tokensPerSecond
            Me.TimeToFirstToken = timeToFirstToken
            Me.Scheduled = scheduled
        End Sub

    End Class

    ''' <summary>One tool call and whether it failed.</summary>
    Public NotInheritable Class ToolUse

        Public ReadOnly Property At As DateTime ' UTC
        Public ReadOnly Property Name As String
        Public ReadOnly Property Failed As Boolean
        Public ReadOnly Property Model As String
        Public ReadOnly Property Scheduled As Boolean

        Public Sub New(at As DateTime, name As String, failed As Boolean, model As String, scheduled As Boolean)
            Me.At = at
            Me.Name = name
            Me.Failed = failed
            Me.Model = model
            Me.Scheduled = scheduled
        End Sub

    End Class

    Public NotInheritable Class UsageTotals

        Public ReadOnly Property Requests As Integer
        Public ReadOnly Property InputTokens As Long
        Public ReadOnly Property OutputTokens As Long
        Public ReadOnly Property TokensPerSecond As Double
        Public ReadOnly Property TimeToFirstToken As Double

        Public Sub New(requests As Integer, inputTokens As Long, outputTokens As Long, tokensPerSecond As Double, timeToFirstToken As Double)
            Me.Requests = requests
            Me.InputTokens = inputTokens
            Me.OutputTokens = outputTokens
            Me.TokensPerSecond = tokensPerSecond
            Me.TimeToFirstToken = timeToFirstToken
        End Sub

    End Class

    ''' <summary>One bar of the chart: a day, a week or a month.</summary>
    Public NotInheritable Class UsageBucket

        Public ReadOnly Property Start As DateOnly
        Public ReadOnly Property Label As String
        Public ReadOnly Property InputTokens As Long
        Public ReadOnly Property OutputTokens As Long
        Public ReadOnly Property TokensPerSecond As Double

        Public Sub New(start As DateOnly, label As String, inputTokens As Long, outputTokens As Long, tokensPerSecond As Double)
            Me.Start = start
            Me.Label = label
            Me.InputTokens = inputTokens
            Me.OutputTokens = outputTokens
            Me.TokensPerSecond = tokensPerSecond
        End Sub

    End Class

    Public NotInheritable Class UsageModelRow

        Public ReadOnly Property Model As String
        Public ReadOnly Property Totals As UsageTotals
        Public ReadOnly Property SlowestStart As Double

        Public Sub New(model As String, totals As UsageTotals, slowestStart As Double)
            Me.Model = model
            Me.Totals = totals
            Me.SlowestStart = slowestStart
        End Sub

    End Class

    Public NotInheritable Class UsageToolRow

        Public ReadOnly Property Name As String
        Public ReadOnly Property Calls As Integer
        Public ReadOnly Property Failed As Integer

        Public Sub New(name As String, calls As Integer, failed As Integer)
            Me.Name = name
            Me.Calls = calls
            Me.Failed = failed
        End Sub

        Public ReadOnly Property FailureRate As Double
            Get
                Return If(Calls = 0, 0.0, Failed / Calls)
            End Get
        End Property

    End Class

    Public NotInheritable Class UsageChatRow

        Public ReadOnly Property SessionId As String
        Public ReadOnly Property Title As String
        Public ReadOnly Property Tokens As Long

        Public Sub New(sessionId As String, title As String, tokens As Long)
            Me.SessionId = sessionId
            Me.Title = title
            Me.Tokens = tokens
        End Sub

    End Class

    Public NotInheritable Class UsageReport

        Public ReadOnly Property Totals As UsageTotals
        Public ReadOnly Property BucketKind As String ' "day", "week" or "month"
        Public ReadOnly Property Buckets As List(Of UsageBucket)
        Public ReadOnly Property Models As List(Of UsageModelRow)
        Public ReadOnly Property Tools As List(Of UsageToolRow)
        Public ReadOnly Property Chats As List(Of UsageChatRow)
        Public ReadOnly Property AvailableModels As List(Of String) ' for the model filter, whatever the filter is set to

        Public Sub New(totals As UsageTotals, bucketKind As String, buckets As List(Of UsageBucket), models As List(Of UsageModelRow), tools As List(Of UsageToolRow), chats As List(Of UsageChatRow), availableModels As List(Of String))
            Me.Totals = totals
            Me.BucketKind = bucketKind
            Me.Buckets = buckets
            Me.Models = models
            Me.Tools = tools
            Me.Chats = chats
            Me.AvailableModels = availableModels
        End Sub

    End Class

    ''' <summary>
    ''' Usage statistics: turning saved turn numbers into totals, a chart series and tables. Plain functions with no
    ''' database and no screen, so every rule can be tested with a handful of made-up rows. The same rules as the Python
    ''' edition's usage report.
    ''' </summary>
    Public NotInheritable Class UsageReports

        Private Sub New()
        End Sub

        Public Const UnknownModel As String = "Unknown (before tracking)"
        Public Const AllModels As String = "All models"
        Public Const TopChats As Integer = 5

        Private Const DayLimit As Integer = 45   ' a period up to this many days is charted day by day
        Private Const WeekLimit As Integer = 400 ' up to this many, week by week; longer periods are charted month by month

        Public Shared Function ModelName(model As String) As String
            Return If(String.IsNullOrWhiteSpace(model), UnknownModel, model)
        End Function

        ''' <summary>Adds up a group of requests. Speed is weighted by output tokens: a long answer counts for more than a short one.</summary>
        Public Shared Function TotalsOf(requests As IReadOnlyList(Of UsageRequest)) As UsageTotals
            If requests.Count = 0 Then Return New UsageTotals(0, 0, 0, 0, 0)

            Dim timed = requests.Where(Function(r) r.TokensPerSecond > 0 AndAlso r.OutputTokens > 0).ToList()
            Dim weight = timed.Sum(Function(r) CLng(r.OutputTokens))
            Dim speed = If(weight > 0, timed.Sum(Function(r) r.TokensPerSecond * r.OutputTokens) / weight, 0.0)
            Dim waits = requests.Where(Function(r) r.TimeToFirstToken > 0).Select(Function(r) r.TimeToFirstToken).ToList()
            Return New UsageTotals(
                requests.Count,
                requests.Sum(Function(r) CLng(r.InputTokens)),
                requests.Sum(Function(r) CLng(r.OutputTokens)),
                speed,
                If(waits.Count > 0, waits.Average(), 0.0))
        End Function

        ''' <summary>Charting by day, week or month, whichever keeps the chart readable for a span of this length.</summary>
        Public Shared Function BucketKindFor(first As DateOnly, last As DateOnly) As String
            Dim days = last.DayNumber - first.DayNumber + 1
            If days <= DayLimit Then Return "day"
            Return If(days <= WeekLimit, "week", "month")
        End Function

        Private Shared Function BucketStart(day As DateOnly, kind As String) As DateOnly
            Select Case kind
                Case "week"
                    Return day.AddDays(-((CInt(day.DayOfWeek) + 6) Mod 7)) ' the Monday of that week
                Case "month"
                    Return New DateOnly(day.Year, day.Month, 1)
                Case Else
                    Return day
            End Select
        End Function

        Private Shared Function NextBucket(start As DateOnly, kind As String) As DateOnly
            Select Case kind
                Case "week"
                    Return start.AddDays(7)
                Case "month"
                    Return start.AddMonths(1)
                Case Else
                    Return start.AddDays(1)
            End Select
        End Function

        Private Shared Function BucketLabel(start As DateOnly, kind As String) As String
            Dim culture = CultureInfo.InvariantCulture
            If kind = "month" Then Return start.ToString("MMM yyyy", culture)
            Return If(kind = "week", "w/c ", "") & start.ToString("d MMM", culture)
        End Function

        Private Shared Function LocalDay(utc As DateTime, tz As TimeZoneInfo) As DateOnly
            Return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz))
        End Function

        ''' <summary>
        ''' Everything the usage screen shows, for requests between since (Nothing = from the start) and until.
        ''' model limits the report to one model (UnknownModel picks the requests saved without one); Nothing means all of them.
        ''' includeScheduled False leaves out chats that scheduled jobs made. Days are the computer's own (tz), not UTC, so a
        ''' late-evening reply counts for the day you had it.
        ''' </summary>
        Public Shared Function Build(requests As IReadOnlyList(Of UsageRequest), toolUses As IReadOnlyList(Of ToolUse), since As DateTime?, until As DateTime, tz As TimeZoneInfo, Optional model As String = Nothing, Optional includeScheduled As Boolean = True) As UsageReport
            Dim inPeriod = Function(at As DateTime) (Not since.HasValue OrElse at >= since.Value) AndAlso at <= until

            ' the model filter is applied after, so the filter's own list can offer every model in this period
            Dim inScope = requests.Where(Function(r) inPeriod(r.At) AndAlso (includeScheduled OrElse Not r.Scheduled)).ToList()
            Dim available = inScope.Select(Function(r) ModelName(r.Model)).Distinct().OrderBy(Function(m) m, StringComparer.OrdinalIgnoreCase).ToList()
            Dim selected = inScope.Where(Function(r) model Is Nothing OrElse ModelName(r.Model) = model).ToList()

            Dim toolsSelected = toolUses.Where(Function(t) inPeriod(t.At) AndAlso (includeScheduled OrElse Not t.Scheduled) AndAlso (model Is Nothing OrElse ModelName(t.Model) = model)).ToList()

            Dim span = SpanOf(selected, since, until, tz)
            Dim kind = BucketKindFor(span.First, span.Last)
            Return New UsageReport(
                TotalsOf(selected),
                kind,
                BucketsOf(selected, span.First, span.Last, tz, kind),
                ModelRows(selected),
                ToolRows(toolsSelected),
                ChatRows(selected),
                available)
        End Function

        Private Shared Function SpanOf(selected As List(Of UsageRequest), since As DateTime?, until As DateTime, tz As TimeZoneInfo) As (First As DateOnly, Last As DateOnly)
            Dim last = LocalDay(until, tz)
            If since.HasValue Then Return (LocalDay(since.Value, tz), last)
            If selected.Count > 0 Then Return (LocalDay(selected.Min(Function(r) r.At), tz), last)
            Return (last, last)
        End Function

        Private Shared Function BucketsOf(selected As List(Of UsageRequest), first As DateOnly, last As DateOnly, tz As TimeZoneInfo, kind As String) As List(Of UsageBucket)
            Dim grouped = selected.GroupBy(Function(r) BucketStart(LocalDay(r.At, tz), kind)).ToDictionary(Function(g) g.Key, Function(g) g.ToList())

            Dim buckets As New List(Of UsageBucket)
            Dim start = BucketStart(first, kind)
            While start <= last ' every period appears, even one with no use, so the chart has no gaps
                Dim group As List(Of UsageRequest) = Nothing
                grouped.TryGetValue(start, group)
                Dim totals = TotalsOf(If(group, New List(Of UsageRequest)()))
                buckets.Add(New UsageBucket(start, BucketLabel(start, kind), totals.InputTokens, totals.OutputTokens, totals.TokensPerSecond))
                start = NextBucket(start, kind)
            End While
            Return buckets
        End Function

        Private Shared Function ModelRows(selected As List(Of UsageRequest)) As List(Of UsageModelRow)
            Return selected.GroupBy(Function(r) ModelName(r.Model)).
                Select(Function(g) New UsageModelRow(g.Key, TotalsOf(g.ToList()), g.Max(Function(r) r.TimeToFirstToken))).
                OrderByDescending(Function(r) r.Totals.Requests).ThenBy(Function(r) r.Model, StringComparer.OrdinalIgnoreCase).
                ToList()
        End Function

        Private Shared Function ToolRows(toolUses As List(Of ToolUse)) As List(Of UsageToolRow)
            Return toolUses.GroupBy(Function(t) t.Name).
                Select(Function(g) New UsageToolRow(g.Key, g.Count(), g.Count(Function(t) t.Failed))).
                OrderByDescending(Function(r) r.Calls).ThenBy(Function(r) r.Name, StringComparer.OrdinalIgnoreCase).
                ToList()
        End Function

        Private Shared Function ChatRows(selected As List(Of UsageRequest)) As List(Of UsageChatRow)
            Return selected.GroupBy(Function(r) r.SessionId).
                Select(Function(g) New UsageChatRow(g.Key, g.Last().Title, g.Sum(Function(r) CLng(r.InputTokens) + r.OutputTokens))).
                Where(Function(r) r.Tokens > 0).
                OrderByDescending(Function(r) r.Tokens).
                Take(TopChats).
                ToList()
        End Function

        ''' <summary>A short form of a count: 950, 1.2 k, 3.84 M.</summary>
        Public Shared Function Compact(number As Double) As String
            Dim value = Math.Abs(number)
            Dim culture = CultureInfo.CurrentCulture
            If value < 1000 Then Return number.ToString("N0", culture)
            If value < 1000000 Then Return (number / 1000).ToString("0.0", culture) & " k"
            Return (number / 1000000).ToString("0.00", culture) & " M"
        End Function

    End Class

End Namespace
