Imports System.Linq

''' <summary>
''' Read-only view of the real, current system message (_history(0) in
''' MainViewModel). Shows the exact live text, not a reconstruction - see
''' MainViewModel.OpenSystemPrompt. Optional title/description parameters
''' let MainViewModel.OpenTurnContext reuse the same generic read-only text
''' viewer for the volatile per-turn context, rather than a near-duplicate
''' dialog - same "generalize with an optional parameter" pattern already
''' used for FileWriteApprovalDialog's Coder reuse.
''' </summary>
Class SystemPromptDialog

    Private Sub New()
        InitializeComponent()
    End Sub

    Private Sub OnCloseClick(sender As Object, e As RoutedEventArgs)
        DialogResult = True
    End Sub

    Public Shared Shadows Sub Show(promptText As String, Optional owner As Window = Nothing,
                                    Optional title As String = "System prompt",
                                    Optional description As String = "Exactly what's currently sitting at the front of the model's context for this chat - the same text that gets rebuilt each turn.")
        Dim dialog As New SystemPromptDialog()
        dialog.Title = title
        dialog.DescriptionTextBlock.Text = description
        dialog.PromptTextBox.Text = promptText

        dialog.Owner = If(owner, Application.Current.Windows.OfType(Of Window)().FirstOrDefault(Function(w) w.IsActive))
        If dialog.Owner Is Nothing Then dialog.Owner = Application.Current.MainWindow

        dialog.ShowDialog()
    End Sub

End Class
