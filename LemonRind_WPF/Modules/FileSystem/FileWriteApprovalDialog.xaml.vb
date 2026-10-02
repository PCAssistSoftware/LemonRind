''' <summary>
''' Deliberately a plain top-level class, not wrapped in a Namespace block -
''' MainWindow.xaml.vb uses the same pattern. VB's x:Class in XAML doesn't
''' include the project's RootNamespace the way C#'s does, and mixing an
''' explicit Namespace block with x:Class resolution isn't worth the risk
''' of a build error for what's a single small dialog - not a design
''' statement about where dialogs generally belong.
'''
''' Deny / Allow once / Allow for this session, with a persisted "always
''' allow this folder" setting left for a future Settings screen rather
''' than built here.
'''
''' actionLabel generalizes this beyond just "write a file" - CoderModule
''' reuses this same dialog for edit/create/delete/move actions rather
''' than building a near-duplicate dialog for each, so the header text is
''' parameterized instead of hardcoded.
''' </summary>
Class FileWriteApprovalDialog

    Public Enum ApprovalResult
        Deny
        AllowOnce
        AllowForSession
    End Enum

    Public Property Result As ApprovalResult = ApprovalResult.Deny

    Public Sub New(filePath As String, contentPreview As String, Optional actionLabel As String = "write a file")
        InitializeComponent()
        ActionLabelText.Text = $"The assistant wants to {actionLabel}:"
        FilePathText.Text = filePath
        ContentPreviewText.Text = contentPreview
    End Sub

    Private Sub OnDenyClick(sender As Object, e As RoutedEventArgs)
        Result = ApprovalResult.Deny
        Close()
    End Sub

    Private Sub OnAllowOnceClick(sender As Object, e As RoutedEventArgs)
        Result = ApprovalResult.AllowOnce
        Close()
    End Sub

    Private Sub OnAllowSessionClick(sender As Object, e As RoutedEventArgs)
        Result = ApprovalResult.AllowForSession
        Close()
    End Sub

End Class
