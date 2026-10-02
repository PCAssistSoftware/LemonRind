namespace LemonRindAvalonia.Modules.FileSystem;

/// <summary>
/// Remembers which folders the user picked "Allow for this session" for, so
/// a write to the same folder later in the same app run skips the
/// confirmation dialog. In-memory only, per app run - not persisted.
///
/// Ported from the VB.NET/WPF LemonRind app's
/// Modules\FileSystem\FileWriteApprovalStore.vb. IsRunningScheduledJob is set while a
/// scheduled job runs; with nobody there to answer the dialog, such writes count
/// as pre-approved.
/// </summary>
public class FileWriteApprovalStore
{
    private readonly HashSet<string> _approvedFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly AsyncLocal<bool> _isRunningScheduledJob = new();

    public bool IsRunningScheduledJob
    {
        get => _isRunningScheduledJob.Value;
        set => _isRunningScheduledJob.Value = value;
    }

    public bool IsPreApproved(string folderPath) => IsRunningScheduledJob || _approvedFolders.Contains(folderPath);

    public void ApproveFolderForSession(string folderPath) => _approvedFolders.Add(folderPath);
}
