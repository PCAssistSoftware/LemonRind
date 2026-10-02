Imports System.Collections.ObjectModel
Imports System.Linq
Imports LemonRind.Data

''' <summary>
''' Add/remove tags on one session. Unlike ConfirmDialog/ImageSizeDialog/
''' MoveToFolderDialog (stage changes, apply on confirm), this one writes to
''' the database immediately as you add/remove each tag, matching how the
''' Settings screen's MCP servers already work - "Done" is just a close
''' button, not a commit step.
''' </summary>
Class TagManagerDialog

    Private ReadOnly _sessionId As String
    Private ReadOnly _sessionRepository As ChatSessionRepository
    Private ReadOnly _tags As New ObservableCollection(Of String)()

    Private Sub New(sessionId As String, initialTags As IEnumerable(Of String), sessionRepository As ChatSessionRepository)
        InitializeComponent()
        _sessionId = sessionId
        _sessionRepository = sessionRepository
        ' Loop variable named tagName, not tag - "tag" collides with this
        ' Window's own inherited FrameworkElement.Tag property (VB is
        ' case-insensitive), producing a build error if named the same.
        For Each tagName In initialTags
            _tags.Add(tagName)
        Next
        TagsItemsControl.ItemsSource = _tags
    End Sub

    Private Sub OnAddTagClick(sender As Object, e As RoutedEventArgs)
        AddTag()
    End Sub

    Private Sub OnNewTagBoxKeyDown(sender As Object, e As KeyEventArgs)
        If e.Key = Key.Enter Then AddTag()
    End Sub

    Private Sub AddTag()
        Dim tagName = NewTagBox.Text.Trim()
        If String.IsNullOrEmpty(tagName) Then Return
        If _tags.Any(Function(t) String.Equals(t, tagName, StringComparison.OrdinalIgnoreCase)) Then
            NewTagBox.Text = ""
            Return
        End If

        _sessionRepository.AddTagToSession(_sessionId, tagName)
        _tags.Add(tagName)
        NewTagBox.Text = ""
        NewTagBox.Focus()
    End Sub

    Private Sub OnRemoveTagClick(sender As Object, e As RoutedEventArgs)
        Dim tagName = CStr(CType(sender, Button).Tag)
        _sessionRepository.RemoveTagFromSession(_sessionId, tagName)
        _tags.Remove(tagName)
    End Sub

    Private Sub OnDoneClick(sender As Object, e As RoutedEventArgs)
        Close()
    End Sub

    ''' <summary>Opens the dialog and blocks until closed - changes are already saved by then, so there's nothing for the caller to apply, just a RefreshSessions() to pick up the new tag chips (see MainViewModel's caller).</summary>
    Public Shared Shadows Sub Show(sessionId As String, currentTags As IEnumerable(Of String), sessionRepository As ChatSessionRepository)
        Dim dialog As New TagManagerDialog(sessionId, currentTags, sessionRepository)

        dialog.Owner = Application.Current.Windows.OfType(Of Window)().FirstOrDefault(Function(w) w.IsActive)
        If dialog.Owner Is Nothing Then dialog.Owner = Application.Current.MainWindow

        dialog.ShowDialog()
    End Sub

End Class
