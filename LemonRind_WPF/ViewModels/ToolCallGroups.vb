Imports System.Collections.ObjectModel
Imports System.Collections.Specialized
Imports System.ComponentModel
Imports System.Text.RegularExpressions

Namespace ViewModels

    ''' <summary>
    ''' One badge in the tool-call row: every call to one tool that ended the same way, with a count
    ''' ("web_search x 12"). Calls that succeeded and calls that failed stay as separate badges, so a
    ''' failure is never hidden in a total.
    ''' </summary>
    Public NotInheritable Class ToolCallGroup

        Public ReadOnly Property Name As String
        Public ReadOnly Property Succeeded As Boolean?
        Public ReadOnly Property Count As Integer

        ''' <summary>What you see on hover: short versions of the real errors for failed calls, else how the calls ended.</summary>
        Public ReadOnly Property Tooltip As String

        Public ReadOnly Property Label As String
            Get
                Return If(Count > 1, $"{Name} x {Count}", Name)
            End Get
        End Property

        Public Sub New(name As String, succeeded As Boolean?, count As Integer, tooltip As String)
            Me.Name = name
            Me.Succeeded = succeeded
            Me.Count = count
            Me.Tooltip = tooltip
        End Sub

    End Class

    ''' <summary>
    ''' The badges for a list of tool calls, kept in step with it: a long scheduled job or a chat that uses
    ''' tools a lot makes dozens of calls to the same few tools, and a row per call is unreadable. The groups
    ''' are rebuilt whenever a call is added or a call's outcome arrives (Succeeded / ErrorMessage change).
    ''' </summary>
    Public NotInheritable Class ToolCallGroups

        Public ReadOnly Property Groups As New ObservableCollection(Of ToolCallGroup)()

        Private _source As ObservableCollection(Of ToolCallResult)
        Private ReadOnly _hooked As New List(Of ToolCallResult)()

        Public Sub New(Optional source As ObservableCollection(Of ToolCallResult) = Nothing)
            Attach(source)
        End Sub

        ''' <summary>Follow a different list of calls.</summary>
        Public Sub Attach(source As ObservableCollection(Of ToolCallResult))
            If _source IsNot Nothing Then RemoveHandler _source.CollectionChanged, AddressOf OnSourceChanged
            For Each item In _hooked
                RemoveHandler item.PropertyChanged, AddressOf OnItemChanged
            Next
            _hooked.Clear()

            _source = source
            If _source IsNot Nothing Then AddHandler _source.CollectionChanged, AddressOf OnSourceChanged
            SyncHooks()
            Rebuild()
        End Sub

        Private Sub OnSourceChanged(sender As Object, e As NotifyCollectionChangedEventArgs)
            SyncHooks()
            Rebuild()
        End Sub

        Private Sub OnItemChanged(sender As Object, e As PropertyChangedEventArgs)
            Rebuild()
        End Sub

        Private Sub SyncHooks()
            Dim current As IEnumerable(Of ToolCallResult) = If(_source, New ObservableCollection(Of ToolCallResult)())
            For Each gone In _hooked.Where(Function(h) Not current.Contains(h)).ToList()
                RemoveHandler gone.PropertyChanged, AddressOf OnItemChanged
                _hooked.Remove(gone)
            Next
            For Each added In current.Where(Function(c) Not _hooked.Contains(c)).ToList()
                AddHandler added.PropertyChanged, AddressOf OnItemChanged
                _hooked.Add(added)
            Next
        End Sub

        Private Sub Rebuild()
            Groups.Clear()
            If _source Is Nothing Then Return
            For Each group In GroupCalls(_source)
                Groups.Add(group)
            Next
        End Sub

        ''' <summary>One group per (tool, outcome), the tools in the order they were first used.</summary>
        Public Shared Function GroupCalls(calls As IEnumerable(Of ToolCallResult)) As List(Of ToolCallGroup)
            Dim all = calls.ToList()
            Dim firstUse = all.Select(Function(c) c.Name).Distinct().Select(Function(name, index) New With {Key .Name = name, Key .Index = index}).ToDictionary(Function(x) x.Name, Function(x) x.Index)
            Return all.
                GroupBy(Function(c) New With {Key .Name = c.Name, Key .Succeeded = c.Succeeded}).
                OrderBy(Function(g) firstUse(g.Key.Name)).
                Select(Function(g) New ToolCallGroup(g.Key.Name, g.Key.Succeeded, g.Count(), BuildTooltip(g.Key.Succeeded, g.ToList()))).
                ToList()
        End Function

        Private Shared Function BuildTooltip(succeeded As Boolean?, calls As List(Of ToolCallResult)) As String
            Dim errors = calls.Select(Function(c) ShortError(c.ErrorMessage)).Where(Function(e) e.Length > 0).Distinct().ToList()
            If errors.Count > 0 Then
                Dim lines = errors.Take(3).ToList()
                If errors.Count > 3 Then lines.Add($"(and {errors.Count - 3} more kinds of error)")
                Return String.Join(vbLf, lines)
            End If
            Dim title = If(succeeded.HasValue, If(succeeded.Value, "Succeeded", "Failed"), "Pending")
            Return If(calls.Count > 1, $"{title} ({calls.Count} calls)", title)
        End Function

        ''' <summary>
        ''' An error boiled down for a tooltip: one line, web-address query strings removed (the long signed
        ''' part is never the useful part), cut to a readable length.
        ''' </summary>
        Public Shared Function ShortError(text As String) As String
            If String.IsNullOrWhiteSpace(text) Then Return ""
            Dim oneLine = String.Join(" ", text.Split(CType(Nothing, Char()), StringSplitOptions.RemoveEmptyEntries))
            Dim plain = Regex.Replace(oneLine, "\?[^\s'""]+", "?...")
            Return If(plain.Length > 140, plain.Substring(0, 137).TrimEnd() & "...", plain)
        End Function

    End Class

End Namespace
