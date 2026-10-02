namespace LemonRindBlazor.Modules.FileSystem;

public enum ApprovalResult
{
    Deny,
    AllowOnce,
    AllowForSession,
}

/// <summary>One pending approval request - carries its own TaskCompletionSource so whichever Razor component shows the approval UI can resolve it directly.</summary>
public class FileWriteApprovalRequest(string filePath, string actionLabel, string preview)
{
    public string FilePath { get; } = filePath;
    public string ActionLabel { get; } = actionLabel;
    public string Preview { get; } = preview;
    public TaskCompletionSource<ApprovalResult> Completion { get; } = new();
}

/// <summary>
/// Blazor Server's equivalent of the desktop apps' own modal
/// FileWriteApprovalDialog - there's no blocking Window.ShowDialog() here,
/// so a write-gated tool call instead raises an event and awaits a
/// TaskCompletionSource that whichever Razor component is actually showing
/// (subscribed via OnPendingRequestChanged) resolves once the user clicks a
/// button in its own on-page approval overlay. Singleton, matching
/// FileSystemModule/CoderModule's own singleton lifetime and this app's
/// real single-user-local usage - broadcasts to whichever one browser tab
/// is actually open, the same "not built for multi-tenant" scope this
/// whole project has had from the start.
/// </summary>
public class FileWriteApprovalGate
{
    /// <summary>Fires when a new approval request needs to be shown, and again with null once it's resolved - a subscribing component's own StateHasChanged should follow this directly.</summary>
    public event Action<FileWriteApprovalRequest?>? OnPendingRequestChanged;

    public Task<ApprovalResult> RequestApprovalAsync(string filePath, string preview, string actionLabel = "write a file")
    {
        var request = new FileWriteApprovalRequest(filePath, actionLabel, preview);
        OnPendingRequestChanged?.Invoke(request);

        // Clears the pending request the moment it's resolved, regardless
        // of which button was clicked, so the next call starts from a
        // clean slate.
        _ = request.Completion.Task.ContinueWith(_ => OnPendingRequestChanged?.Invoke(null), TaskScheduler.Default);

        return request.Completion.Task;
    }
}
