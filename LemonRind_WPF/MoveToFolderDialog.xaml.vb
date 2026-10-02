Imports System.Linq
Imports LemonRind.Data

''' <summary>
''' Assigns a session to a folder, or creates a new one on the fly (the
''' whole point of not having a separate "manage folders" screen -
''' creating one happens naturally the first time you move a session into
''' it). Same Dispatcher-free, ShowDialog-blocks-until-answered pattern as
''' ConfirmDialog/ImageSizeDialog, but this one is invoked directly from
''' MainViewModel (a right-click command), not from inside a tool call, so
''' there's no Dispatcher.Invoke needed here.
''' </summary>
Class MoveToFolderDialog

    Private _confirmed As Boolean = False

    Private Sub New()
        InitializeComponent()
    End Sub

    Private Sub OnMoveClick(sender As Object, e As RoutedEventArgs)
        _confirmed = True
        Close()
    End Sub

    Private Sub OnCancelClick(sender As Object, e As RoutedEventArgs)
        _confirmed = False
        Close()
    End Sub

    ''' <summary>
    ''' FolderId in the result is Nothing for "(Unfiled)" - same meaning as
    ''' ChatSessionSummary.FolderId itself. If the user typed a new folder
    ''' name, it's created here (via sessionRepository) before returning, so
    ''' the caller always just gets back a real, already-existing FolderId
    ''' (or Nothing) to assign - never has to know whether a new folder was
    ''' involved.
    ''' </summary>
    Public Shared Shadows Function Show(folders As List(Of FolderSummary), currentFolderId As String, sessionRepository As ChatSessionRepository) As (Confirmed As Boolean, FolderId As String)
        Dim dialog As New MoveToFolderDialog()

        Dim comboItems As New List(Of FolderSummary) From {New FolderSummary With {.Id = Nothing, .Name = "(Unfiled)"}}
        comboItems.AddRange(folders)
        dialog.FolderComboBox.ItemsSource = comboItems
        Dim currentItem = comboItems.FirstOrDefault(Function(f) f.Id = currentFolderId)
        dialog.FolderComboBox.SelectedItem = If(currentItem, comboItems(0))

        dialog.Owner = Application.Current.Windows.OfType(Of Window)().FirstOrDefault(Function(w) w.IsActive)
        If dialog.Owner Is Nothing Then dialog.Owner = Application.Current.MainWindow

        dialog.ShowDialog()

        If Not dialog._confirmed Then Return (False, Nothing)

        Dim newFolderName = dialog.NewFolderNameBox.Text.Trim()
        If Not String.IsNullOrEmpty(newFolderName) Then
            Dim newFolderId = sessionRepository.CreateFolder(newFolderName)
            Return (True, newFolderId)
        End If

        Dim selectedFolder = TryCast(dialog.FolderComboBox.SelectedItem, FolderSummary)
        Return (True, selectedFolder?.Id)
    End Function

End Class
