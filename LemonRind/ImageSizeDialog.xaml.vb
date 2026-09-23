Imports System.Linq

''' <summary>
''' Confirms/overrides the width and height before generate_image actually
''' calls Lemonade - size is asked about every time, not silently
''' defaulted, unlike steps/cfg_scale/seed which are quiet Settings-level
''' defaults. Same Dispatcher.Invoke-from-a-tool-method,
''' ShowDialog-blocks-until-answered pattern as
''' FileWriteApprovalDialog/ConfirmDialog.
''' </summary>
Class ImageSizeDialog

    Public Property Confirmed As Boolean = False
    ' Shadows, not Overloads - deliberately hiding FrameworkElement's own
    ' Width/Height (layout size DPs), not overloading them; these two are
    ' plain properties holding the chosen image dimensions, unrelated to
    ' this Window's own on-screen size (silences BC40003).
    Public Shadows Property Width As Integer
    Public Shadows Property Height As Integer

    Private Sub New()
        InitializeComponent()
    End Sub

    Private Sub OnGenerateClick(sender As Object, e As RoutedEventArgs)
        Dim parsedWidth As Integer
        Dim parsedHeight As Integer
        If Not Integer.TryParse(WidthBox.Text, parsedWidth) OrElse parsedWidth <= 0 Then parsedWidth = 512
        If Not Integer.TryParse(HeightBox.Text, parsedHeight) OrElse parsedHeight <= 0 Then parsedHeight = 512

        Width = parsedWidth
        Height = parsedHeight
        Confirmed = True
        Close()
    End Sub

    Private Sub OnCancelClick(sender As Object, e As RoutedEventArgs)
        Confirmed = False
        Close()
    End Sub

    ' Shadows, not Overloads - deliberately hiding Window's own instance
    ' Show(), same as ConfirmDialog.Show (silences BC40003).
    Public Shared Shadows Function Show(prompt As String, defaultWidth As Integer, defaultHeight As Integer) As (Confirmed As Boolean, Width As Integer, Height As Integer)
        Dim dialog As New ImageSizeDialog()
        dialog.PromptPreviewText.Text = If(prompt.Length > 140, prompt.Substring(0, 140) & "...", prompt)
        dialog.WidthBox.Text = defaultWidth.ToString()
        dialog.HeightBox.Text = defaultHeight.ToString()

        dialog.Owner = Application.Current.Windows.OfType(Of Window)().FirstOrDefault(Function(w) w.IsActive)
        If dialog.Owner Is Nothing Then dialog.Owner = Application.Current.MainWindow

        dialog.ShowDialog()
        Return (dialog.Confirmed, dialog.Width, dialog.Height)
    End Function

End Class
