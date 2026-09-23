Imports System.Collections.ObjectModel
Imports System.Threading
Imports System.Windows.Media
Imports LemonRind.Services

''' <summary>
''' Live-streams Lemonade's own logs via its documented WS /logs/stream API
''' (see LemonadeLogClient). Not modal (Show, not ShowDialog) - meant to
''' sit open alongside the main window while you keep chatting, the way a
''' real log tail would.
''' </summary>
Class LogViewerWindow

    ''' <summary>Capped, not unbounded - a long-running session could otherwise grow this without limit; 3000 is generous for "what's happening right now" without becoming a real memory concern.</summary>
    Private Const MaxDisplayedEntries As Integer = 3000

    Private ReadOnly _logClient As LemonadeLogClient
    Private ReadOnly _entries As New ObservableCollection(Of LemonadeLogEntry)()
    Private _streamCts As CancellationTokenSource

    ' Same "only auto-follow if the user hasn't deliberately scrolled away"
    ' pattern as MainWindow's message auto-scroll (see its own comment) -
    ' proven there, reused here rather than a cruder always-force-scroll.
    Private _autoScrollToBottom As Boolean = True
    Private _logScrollViewer As ScrollViewer

    Public Sub New(logClient As LemonadeLogClient)
        InitializeComponent()
        _logClient = logClient
        LogListView.ItemsSource = _entries

        AddHandler Loaded, AddressOf OnWindowLoaded
        AddHandler Closed, AddressOf OnWindowClosed
    End Sub

    Private Async Sub OnWindowLoaded(sender As Object, e As RoutedEventArgs)
        _logScrollViewer = FindVisualChild(Of ScrollViewer)(LogListView)
        If _logScrollViewer IsNot Nothing Then
            AddHandler _logScrollViewer.ScrollChanged, AddressOf OnLogScrollChanged
        End If

        _streamCts = New CancellationTokenSource()
        Try
            Await _logClient.StreamAsync(
                onSnapshot:=Sub(snapshotEntries) Dispatcher.Invoke(Sub() AddEntries(snapshotEntries)),
                onEntry:=Sub(entry) Dispatcher.Invoke(Sub() AddEntries({entry})),
                cancellationToken:=_streamCts.Token)
        Catch ex As OperationCanceledException
            ' Expected - the window was closed, see OnClosed.
        Catch ex As Exception
            StatusText.Text = $"Disconnected: {ex.Message}"
            Return
        End Try

        ' StreamAsync only returns without throwing if the server itself
        ' closed the connection (see LemonadeLogClient.ReceiveFullMessageAsync
        ' returning Nothing) - not expected in normal use, worth saying so
        ' rather than leaving "Connecting..." showing forever.
        If Not _streamCts.IsCancellationRequested Then
            StatusText.Text = "Disconnected - Lemonade closed the connection."
        End If
    End Sub

    Private Sub AddEntries(newEntries As IEnumerable(Of LemonadeLogEntry))
        For Each entry In newEntries
            _entries.Add(entry)
        Next
        While _entries.Count > MaxDisplayedEntries
            _entries.RemoveAt(0)
        End While

        StatusText.Text = $"Connected - streaming live ({_entries.Count} shown)"

        If _autoScrollToBottom AndAlso _entries.Count > 0 Then
            LogListView.ScrollIntoView(_entries(_entries.Count - 1))
        End If
    End Sub

    ''' <summary>Same logic as MainWindow.xaml.vb's OnMessagesScrollChanged - see its comment for why ExtentHeightChange distinguishes "user scrolled" from "content grew".</summary>
    Private Sub OnLogScrollChanged(sender As Object, e As ScrollChangedEventArgs)
        If e.ExtentHeightChange = 0 Then
            Const nearBottomTolerance = 4
            _autoScrollToBottom = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - nearBottomTolerance
        End If
    End Sub

    Private Sub OnWindowClosed(sender As Object, e As EventArgs)
        _streamCts?.Cancel()
    End Sub

    Private Shared Function FindVisualChild(Of T As DependencyObject)(root As DependencyObject) As T
        For i = 0 To VisualTreeHelper.GetChildrenCount(root) - 1
            Dim child = VisualTreeHelper.GetChild(root, i)
            If TypeOf child Is T Then Return CType(child, T)
            Dim descendant = FindVisualChild(Of T)(child)
            If descendant IsNot Nothing Then Return descendant
        Next
        Return Nothing
    End Function

End Class
