Imports System.Linq

''' <summary>
''' Styled replacement for MessageBox.Show's plain native Yes/No dialog - a
''' real Win32 MessageBox has no WPF template to hook into at all (it's a
''' genuine OS common dialog, not stylable), so the only way to
''' match the rest of the app's rounded-button look is a real WPF Window
''' like this one. Synchronous, like MessageBox.Show, so existing call
''' sites barely change - Show() blocks until closed and returns a plain
''' Boolean (True = confirmed) instead of a MessageBoxResult.
''' </summary>
Class ConfirmDialog

    Private Sub New()
        InitializeComponent()
    End Sub

    Private Sub OnConfirmClick(sender As Object, e As RoutedEventArgs)
        DialogResult = True
    End Sub

    Private Sub OnCancelClick(sender As Object, e As RoutedEventArgs)
        DialogResult = False
    End Sub

    ''' <summary>
    ''' owner defaults to whichever window is currently active (not always
    ''' the main window) so this centers over and stays above whichever
    ''' window the user actually triggered it from - e.g. a delete
    ''' confirmation from within Settings should sit on top of the Settings
    ''' window, not appear behind it under the main window.
    ''' </summary>
    ' Shadows, not Overloads - deliberately hiding Window's own instance
    ' Show(), not overloading it (this is a Shared factory method with an
    ' unrelated signature/purpose - silences BC40003).
    Public Shared Shadows Function Show(message As String, title As String,
                                 Optional confirmText As String = "Yes",
                                 Optional cancelText As String = "Cancel",
                                 Optional isDestructive As Boolean = False,
                                 Optional owner As Window = Nothing) As Boolean
        Dim dialog As New ConfirmDialog()
        dialog.Title = title
        dialog.MessageTextBlock.Text = message
        dialog.ConfirmButtonElement.Content = confirmText
        dialog.CancelButtonElement.Content = cancelText
        dialog.ConfirmButtonElement.Style = CType(dialog.FindResource(
            If(isDestructive, "DialogDestructiveButtonStyle", "DialogPrimaryButtonStyle")), Style)

        dialog.Owner = If(owner, Application.Current.Windows.OfType(Of Window)().FirstOrDefault(Function(w) w.IsActive))
        If dialog.Owner Is Nothing Then dialog.Owner = Application.Current.MainWindow

        Return dialog.ShowDialog() = True
    End Function

End Class
