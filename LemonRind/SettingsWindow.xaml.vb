Imports System.ComponentModel
Imports LemonRind.ViewModels

''' <summary>Same thin-code-behind pattern as MainWindow - the ViewModel does everything.</summary>
Class SettingsWindow

    Private ReadOnly _viewModel As SettingsViewModel

    Public Sub New(viewModel As SettingsViewModel)
        InitializeComponent()
        _viewModel = viewModel
        DataContext = viewModel
        AddHandler Closing, AddressOf OnSettingsWindowClosing
    End Sub

    ''' <summary>
    ''' Named OnSettingsWindowClosing, not OnClosing - Window already has a
    ''' same-named overridable method (BC40005 shadowing). Warns before
    ''' silently discarding an edited-but-not-saved field if the window is
    ''' closed via its native X rather than clicking Save.
    ''' </summary>
    Private Sub OnSettingsWindowClosing(sender As Object, e As CancelEventArgs)
        If Not _viewModel.HasUnsavedChanges() Then Return

        Dim confirmed = ConfirmDialog.Show(
            "You have unsaved changes. Close without saving?",
            "Unsaved changes",
            confirmText:="Close without saving",
            cancelText:="Keep editing",
            isDestructive:=True)

        If Not confirmed Then e.Cancel = True
    End Sub

End Class
