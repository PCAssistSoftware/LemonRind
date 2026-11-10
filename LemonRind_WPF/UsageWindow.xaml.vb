Imports System.Windows.Media
Imports LemonRind.Data

''' <summary>
''' The usage screen: tokens and speed over time, per model and per tool, from the numbers saved with every reply. The
''' report itself is worked out by UsageReports (plain functions, no screen); this window only chooses the period, asks
''' for the report and draws it. Not modal - it can sit open next to the chat.
''' </summary>
Class UsageWindow

    Private Shared ReadOnly Periods As (Label As String, Days As Integer?)() = {("7 days", 7), ("30 days", 30), ("12 months", 365), ("All time", CType(Nothing, Integer?))}

    Private ReadOnly _repository As ChatSessionRepository
    Private _requests As New List(Of UsageRequest)()
    Private _toolUses As New List(Of ToolUse)()
    Private _building As Boolean ' True while the controls are being filled in, so that does not look like the person changing them

    ''' <summary>Called with a chat's id when "Busiest chats" is clicked; the main window opens it.</summary>
    Public Property OpenChat As Action(Of String)

    Public Sub New(repository As ChatSessionRepository)
        InitializeComponent()
        _repository = repository

        _building = True
        PeriodBox.ItemsSource = Periods.Select(Function(p) p.Label).ToList()
        PeriodBox.SelectedIndex = 1 ' 30 days
        _building = False

        _requests = _repository.UsageRequests()
        _toolUses = _repository.UsageToolUses()
        Rebuild()
    End Sub

    Private Sub OnFilterChanged(sender As Object, e As RoutedEventArgs)
        If Not _building AndAlso _repository IsNot Nothing Then Rebuild()
    End Sub

    Private Sub Rebuild()
        Dim days = Periods(Math.Max(0, PeriodBox.SelectedIndex)).Days
        Dim tz = TimeZoneInfo.Local
        Dim now = DateTime.UtcNow
        Dim since As DateTime? = Nothing
        If days.HasValue Then
            ' midnight, days calendar days back counting today as one
            Dim firstDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, tz)).AddDays(-(days.Value - 1))
            since = TimeZoneInfo.ConvertTimeToUtc(firstDay.ToDateTime(TimeOnly.MinValue), tz)
        End If

        Dim includeScheduled = ScheduledBox.IsChecked = True
        Dim chosen = TryCast(ModelBox.SelectedItem, String)
        Dim model As String = If(String.IsNullOrEmpty(chosen) OrElse chosen = UsageReports.AllModels, Nothing, chosen)

        Dim report = UsageReports.Build(_requests, _toolUses, since, now, tz, model, includeScheduled)
        If model IsNot Nothing AndAlso Not report.AvailableModels.Contains(model) Then
            model = Nothing ' the chosen model has no replies in this period: go back to all
            report = UsageReports.Build(_requests, _toolUses, since, now, tz, Nothing, includeScheduled)
        End If

        _building = True
        ModelBox.ItemsSource = New List(Of String)({UsageReports.AllModels}).Concat(report.AvailableModels).ToList()
        ModelBox.SelectedItem = If(model, UsageReports.AllModels)
        _building = False

        Draw(report)
    End Sub

    Private Sub Draw(report As UsageReport)
        Dim isEmpty = report.Totals.Requests = 0
        EmptyText.Visibility = If(isEmpty, Visibility.Visible, Visibility.Collapsed)
        ReportPanel.Visibility = If(isEmpty, Visibility.Collapsed, Visibility.Visible)
        If isEmpty Then Return

        Dim totals = report.Totals
        TilesPanel.Children.Clear()
        AddTile("Replies", totals.Requests.ToString("N0"))
        AddTile("Tokens in", UsageReports.Compact(totals.InputTokens))
        AddTile("Tokens out", UsageReports.Compact(totals.OutputTokens))
        AddTile("Average speed", If(totals.TokensPerSecond > 0, $"{totals.TokensPerSecond:0} tok/s", "-"))
        AddTile("Average first token", If(totals.TimeToFirstToken > 0, $"{totals.TimeToFirstToken:0.0} s", "-"))

        TokensTitle.Text = $"Tokens per {report.BucketKind}"
        TokenChart.Buckets = report.Buckets
        SpeedChart.Buckets = report.Buckets

        FillTable(ModelsHost,
            {"Model", "Replies", "Tokens in", "Tokens out", "Avg tok/s", "Avg first token", "Slowest start"},
            report.Models.Select(Function(r) New String() {
                r.Model,
                r.Totals.Requests.ToString("N0"),
                r.Totals.InputTokens.ToString("N0"),
                r.Totals.OutputTokens.ToString("N0"),
                If(r.Totals.TokensPerSecond > 0, r.Totals.TokensPerSecond.ToString("0"), "-"),
                If(r.Totals.TimeToFirstToken > 0, $"{r.Totals.TimeToFirstToken:0.0} s", "-"),
                If(r.SlowestStart > 0, $"{r.SlowestStart:0.0} s", "-")
            }).ToList())

        If report.Tools.Count = 0 Then
            ToolsHost.Children.Clear()
            ToolsHost.Children.Add(Note("No tool calls in this period."))
        Else
            FillTable(ToolsHost, {"Tool", "Calls", "Failed", "Failure rate"},
                report.Tools.Select(Function(t) New String() {t.Name, t.Calls.ToString("N0"), t.Failed.ToString("N0"), t.FailureRate.ToString("0%")}).ToList())
        End If

        Dim anyChats = report.Chats.Count > 0
        ChatsTitle.Visibility = If(anyChats, Visibility.Visible, Visibility.Collapsed)
        ChatsHost.Children.Clear()
        For Each chat In report.Chats
            Dim id = chat.SessionId
            Dim button As New Button With {
                .Content = $"{chat.Title}  ({UsageReports.Compact(chat.Tokens)} tokens)",
                .Background = Brushes.Transparent,
                .BorderThickness = New Thickness(0),
                .Padding = New Thickness(0, 3, 0, 3),
                .HorizontalAlignment = HorizontalAlignment.Left,
                .Cursor = Cursors.Hand
            }
            AddHandler button.Click, Sub(s, args)
                                         Close()
                                         If OpenChat IsNot Nothing Then OpenChat.Invoke(id)
                                     End Sub
            ChatsHost.Children.Add(button)
        Next

        NotesHost.Children.Clear()
        NotesHost.Children.Add(Note("Tokens in is what the model had to process for each request; the part of the conversation it had already stored is not counted again. A reply that uses tools takes several requests, and the numbers for one reply add them all up."))
        NotesHost.Children.Add(Note("Speed is averaged by how long each answer was, so a long answer counts for more than a short one. Speed and first-token time come from Lemonade after each request, so two things running at the same moment can be mixed up."))
        If report.AvailableModels.Contains(UsageReports.UnknownModel) Then
            NotesHost.Children.Add(Note($"The model's name and the numbers have been saved since this screen was added; earlier replies are listed as '{UsageReports.UnknownModel}' where they have numbers at all. Their tool calls are still counted in the tools table."))
        End If
    End Sub

    Private Sub AddTile(caption As String, value As String)
        Dim tile As New StackPanel With {.Margin = New Thickness(0, 0, 36, 8)}
        tile.Children.Add(New TextBlock With {.Text = value, .FontSize = 22, .FontWeight = FontWeights.Bold, .Foreground = New SolidColorBrush(Color.FromRgb(&H19, &H76, &HD2))})
        tile.Children.Add(Note(caption))
        TilesPanel.Children.Add(tile)
    End Sub

    Private Shared Function Note(text As String) As TextBlock
        Return New TextBlock With {.Text = text, .FontSize = 12, .TextWrapping = TextWrapping.Wrap, .Foreground = Brushes.Gray, .Margin = New Thickness(0, 0, 0, 4)}
    End Function

    ''' <summary>A plain table: a bold header row, then one row per line. The first column is wider and left-aligned; the rest are numbers.</summary>
    Private Shared Sub FillTable(host As StackPanel, headers As String(), rows As List(Of String()))
        host.Children.Clear()
        host.Children.Add(TableRow(headers, header:=True))
        For Each row In rows
            host.Children.Add(TableRow(row, header:=False))
        Next
    End Sub

    Private Shared Function TableRow(cells As String(), header As Boolean) As Grid
        Dim grid As New Grid With {.Margin = New Thickness(0, 2, 0, 2)}
        grid.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(3, GridUnitType.Star)})
        For i = 1 To cells.Length - 1
            grid.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
        Next

        For i = 0 To cells.Length - 1
            Dim text As New TextBlock With {
                .Text = cells(i),
                .FontSize = 12,
                .FontWeight = If(header, FontWeights.SemiBold, FontWeights.Normal),
                .HorizontalAlignment = If(i = 0, HorizontalAlignment.Left, HorizontalAlignment.Right),
                .TextTrimming = TextTrimming.CharacterEllipsis
            }
            Grid.SetColumn(text, i)
            grid.Children.Add(text)
        Next
        Return grid
    End Function

End Class
