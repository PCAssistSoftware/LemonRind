using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.AI;
using LemonRindAvalonia.Configuration;
using LemonRindAvalonia.Views;

namespace LemonRindAvalonia.Modules.FileSystem;

/// <summary>
/// Read/write access to explicitly configured folders
/// (AppSettings.FileSystem.AllowedRoots), via PathSandbox. Reads are
/// unguarded (sandboxed, but no confirmation needed); writes go through
/// FileWriteApprovalDialog first.
///
/// Ported from the VB.NET/WPF LemonRind app's Modules\FileSystem\FileSystemModule.vb.
/// </summary>
public class FileSystemModule : IAssistantModule
{
    private readonly PathSandbox _sandbox;
    private readonly FileWriteApprovalStore _approvalStore;
    private readonly ModuleSettings _moduleSettings;

    public FileSystemModule(AppSettings settings, PathSandbox sandbox, FileWriteApprovalStore approvalStore)
    {
        _sandbox = sandbox;
        _approvalStore = approvalStore;
        _moduleSettings = settings.Modules;
    }

    public string Name => "File system";
    public string ConfigKey => "FileSystem";
    public string Description => "Reads and writes files in specific, pre-configured folders only.";

    public bool IsEnabled => _moduleSettings.Enabled.GetValueOrDefault(ConfigKey, false);

    public Task OnStartupAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task OnShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public IEnumerable<AITool> GetTools()
    {
        // The real folder path(s) are baked directly into every tool's
        // description - a static fact worth a few tokens on every request
        // rather than a separate "where's the workspace" tool call.
        var rootsList = string.Join(", ", _sandbox.AllowedRoots);

        return
        [
            AIFunctionFactory.Create(
                method: (string path) => ListDirectoryAsync(path),
                name: "list_directory",
                description: $"Lists files and folders at a path inside the allowed workspace folder(s): {rootsList}. Pass \"\" for the root."),
            AIFunctionFactory.Create(
                method: (string path) => ReadFileAsync(path),
                name: "read_file",
                description: $"Reads a text file's contents from inside the allowed workspace folder(s): {rootsList}."),
            AIFunctionFactory.Create(
                method: (string path, string content) => WriteFileAsync(path, content),
                name: "write_file",
                description: $"Writes (creating or overwriting) a text file inside the allowed workspace " +
                    $"folder(s): {rootsList}. The user is asked to approve every write before it happens."),
        ];
    }

    private Task<string> ListDirectoryAsync(string path)
    {
        try
        {
            var fullPath = _sandbox.ResolveSafePath(path);
            if (!Directory.Exists(fullPath))
            {
                return Task.FromResult($"'{path}' isn't a folder.");
            }

            var entries = new List<string>();
            entries.AddRange(Directory.GetDirectories(fullPath).Select(d => $"[dir]  {Path.GetFileName(d)}"));
            entries.AddRange(Directory.GetFiles(fullPath).Select(f => $"[file] {Path.GetFileName(f)}"));

            return Task.FromResult(entries.Count == 0 ? "(empty)" : string.Join(Environment.NewLine, entries));
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Couldn't list that folder: {ex.Message}");
        }
    }

    private async Task<string> ReadFileAsync(string path)
    {
        try
        {
            var fullPath = _sandbox.ResolveSafePath(path);
            if (!File.Exists(fullPath))
            {
                return $"'{path}' doesn't exist.";
            }
            return await File.ReadAllTextAsync(fullPath);
        }
        catch (Exception ex)
        {
            return $"Couldn't read that file: {ex.Message}";
        }
    }

    private async Task<string> WriteFileAsync(string path, string content)
    {
        string fullPath;
        try
        {
            fullPath = _sandbox.ResolveSafePath(path);
        }
        catch (Exception ex)
        {
            return $"Couldn't write that file: {ex.Message}";
        }

        var folder = Path.GetDirectoryName(fullPath)!;
        if (!_approvalStore.IsPreApproved(folder))
        {
            var approval = await ShowApprovalDialogAsync(fullPath, content);

            switch (approval)
            {
                case FileWriteApprovalDialog.ApprovalResult.Deny:
                    return "The user denied this write - the file was not changed.";
                case FileWriteApprovalDialog.ApprovalResult.AllowForSession:
                    _approvalStore.ApproveFolderForSession(folder);
                    break;
            }
        }

        try
        {
            await File.WriteAllTextAsync(fullPath, content);
            return $"Wrote {content.Length} characters to '{path}'.";
        }
        catch (Exception ex)
        {
            return $"Couldn't write that file: {ex.Message}";
        }
    }

    /// <summary>
    /// Avalonia's Window.ShowDialog&lt;TResult&gt; is itself async, so this
    /// can just be awaited directly - no Dispatcher-blocking workaround
    /// needed the way WPF's synchronous ShowDialog required.
    /// </summary>
    private static async Task<FileWriteApprovalDialog.ApprovalResult> ShowApprovalDialogAsync(string fullPath, string content)
    {
        var preview = content.Length > 2000 ? content[..2000] + "... [truncated]" : content;
        var dialog = new FileWriteApprovalDialog(fullPath, preview);

        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            return await dialog.ShowDialog<FileWriteApprovalDialog.ApprovalResult>(owner);
        }

        dialog.Show();
        return FileWriteApprovalDialog.ApprovalResult.Deny;
    }
}
