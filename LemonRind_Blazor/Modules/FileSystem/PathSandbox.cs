using LemonRindBlazor.Configuration;

namespace LemonRindBlazor.Modules.FileSystem;

/// <summary>
/// Confines file access to the folders configured in
/// AppSettings.FileSystem.AllowedRoots. Every tool method in
/// FileSystemModule routes its path through ResolveSafePath before touching
/// disk - there's no other way into these folders from the model's side.
///
/// Ported from the VB.NET/WPF LemonRind app's Modules\FileSystem\PathSandbox.vb.
/// </summary>
public class PathSandbox(AppSettings settings)
{
    private readonly FileSystemSettings _fileSystemSettings = settings.FileSystem;

    /// <summary>
    /// A relative AllowedRoots entry (e.g. "data\Workspace") resolves
    /// against AppContext.BaseDirectory, not the process's current working
    /// directory. An absolute entry is returned unchanged either way.
    /// </summary>
    private static string ResolveRootPath(string entry)
    {
        var expanded = Environment.ExpandEnvironmentVariables(entry);
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, expanded)).TrimEnd(Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// Resolved and directories-created fresh every call, not cached at
    /// construction, so adding/removing an allowed folder in Settings takes
    /// effect on the very next tool call, no app restart needed.
    /// </summary>
    public IReadOnlyList<string> AllowedRoots
    {
        get
        {
            var resolvedRoots = _fileSystemSettings.AllowedRoots.Select(ResolveRootPath).ToList();
            foreach (var root in resolvedRoots) Directory.CreateDirectory(root);
            return resolvedRoots;
        }
    }

    /// <summary>
    /// Resolves a model-supplied path to a real full path, refusing it if it
    /// falls outside every configured allowed root. A relative path is
    /// resolved against the first configured root; an absolute path is
    /// accepted only if it's still inside one of the roots after
    /// normalization (blocks "..\..\" traversal tricks).
    /// </summary>
    public string ResolveSafePath(string requestedPath)
    {
        var currentAllowedRoots = AllowedRoots;
        if (currentAllowedRoots.Count == 0)
        {
            throw new InvalidOperationException("No folders are configured for file system access.");
        }

        var candidate = Path.IsPathRooted(requestedPath)
            ? Path.GetFullPath(requestedPath)
            : Path.GetFullPath(Path.Combine(currentAllowedRoots[0], requestedPath));

        var isInsideAnAllowedRoot = currentAllowedRoots.Any(root =>
            candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

        if (!isInsideAnAllowedRoot)
        {
            throw new InvalidOperationException($"'{requestedPath}' is outside the allowed folder(s).");
        }

        return candidate;
    }
}
