Imports System.Linq

''' <summary>
''' Read-only "what does this model actually support/how was it
''' launched" dialog, opened from a button next to the main model dropdown.
''' Rich runtime detail (device, the exact llama-server launch arguments -
''' temperature/top-k/top-p/repeat-penalty/etc) only exists for a model
''' that's actually loaded and running right now (GET /v1/health's
''' all_models_loaded array) - a model that's merely downloaded but not
''' loaded only has the static /v1/models catalog entry (labels, context
''' window, size, recipe), so this dialog shows a plain note instead of
''' guessing at runtime values it doesn't have.
''' </summary>
Class ModelDetailsDialog

    Private Sub New()
        InitializeComponent()
    End Sub

    Private Sub OnCloseClick(sender As Object, e As RoutedEventArgs)
        DialogResult = True
    End Sub

    Public Shared Shadows Sub Show(modelId As String, labels As IEnumerable(Of String), maxContextWindow As Integer,
                                    sizeGb As Double, recipe As String, isCurrentlyLoaded As Boolean,
                                    device As String, llamacppArgs As String,
                                    Optional owner As Window = Nothing)
        Dim dialog As New ModelDetailsDialog()
        dialog.ModelIdText.Text = modelId
        dialog.LabelsText.Text = If(labels IsNot Nothing AndAlso labels.Any(), String.Join(", ", labels), "(none reported)")
        dialog.ContextWindowText.Text = If(maxContextWindow > 0, $"{maxContextWindow:N0} tokens", "(unknown)")
        dialog.SizeText.Text = If(sizeGb > 0, $"{sizeGb:N1} GB", "(unknown)")
        dialog.BackendText.Text = If(String.IsNullOrEmpty(recipe), "(unknown)", recipe)

        If isCurrentlyLoaded AndAlso Not String.IsNullOrEmpty(llamacppArgs) Then
            dialog.RuntimeDetailPanel.Visibility = Visibility.Visible
            dialog.DeviceText.Text = If(String.IsNullOrEmpty(device), "(unknown)", device)
            dialog.LlamacppArgsText.Text = llamacppArgs
        Else
            dialog.NotLoadedNoteText.Visibility = Visibility.Visible
            dialog.NotLoadedNoteText.Text = If(isCurrentlyLoaded,
                "This model is loaded, but Lemonade didn't report its launch arguments (not every backend exposes them - e.g. image/embedding models).",
                "Load this model (select it above) to see its exact runtime arguments.")
        End If

        dialog.Owner = If(owner, Application.Current.Windows.OfType(Of Window)().FirstOrDefault(Function(w) w.IsActive))
        If dialog.Owner Is Nothing Then dialog.Owner = Application.Current.MainWindow

        dialog.ShowDialog()
    End Sub

End Class
