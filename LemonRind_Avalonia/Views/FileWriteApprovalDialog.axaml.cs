using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LemonRindAvalonia.Views;

/// <summary>
/// Deny / Allow once / Allow for this session, with a persisted "always
/// allow this folder" setting left for a future Settings screen rather than
/// built here.
///
/// Ported from the VB.NET/WPF LemonRind app's
/// Modules\FileSystem\FileWriteApprovalDialog.xaml(.vb). Avalonia's
/// Window.ShowDialog&lt;TResult&gt; is itself async and returns the result
/// directly - simpler than WPF's own Dispatcher.Invoke-blocking approach,
/// since FileSystemModule's WriteFileAsync is already async all the way
/// through.
/// </summary>
public partial class FileWriteApprovalDialog : Window
{
    public enum ApprovalResult
    {
        Deny,
        AllowOnce,
        AllowForSession,
    }

    public FileWriteApprovalDialog()
    {
        InitializeComponent();
    }

    public FileWriteApprovalDialog(string filePath, string contentPreview, string actionLabel = "write a file") : this()
    {
        ActionLabelText.Text = $"The assistant wants to {actionLabel}:";
        FilePathText.Text = filePath;
        ContentPreviewText.Text = contentPreview;
    }

    private void OnDenyClick(object? sender, RoutedEventArgs e) => Close(ApprovalResult.Deny);
    private void OnAllowOnceClick(object? sender, RoutedEventArgs e) => Close(ApprovalResult.AllowOnce);
    private void OnAllowSessionClick(object? sender, RoutedEventArgs e) => Close(ApprovalResult.AllowForSession);
}
