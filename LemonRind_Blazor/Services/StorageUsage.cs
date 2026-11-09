namespace LemonRindBlazor.Services;

/// <summary>
/// What is using the space in the data folder, for Settings > Storage: the database
/// (with its two side files), the generated and attached pictures, other workspace
/// files, the settings file, and anything else, adding up to the folder's total.
/// </summary>
public static class StorageUsage
{
    /// <summary>One line per part, then the total: "Database (chats, memories, knowledge bases, jobs): 2.1 MB".</summary>
    public static List<string> BuildLines(string dataFolder)
    {
        var lines = new List<string>();
        if (!Directory.Exists(dataFolder))
        {
            lines.Add("The data folder does not exist yet.");
            return lines;
        }

        var database = new[] { "lemonrind.db", "lemonrind.db-wal", "lemonrind.db-shm" }
            .Sum(name => FileSize(Path.Combine(dataFolder, name)));
        var settings = FileSize(Path.Combine(dataFolder, "appsettings.json"));
        var workspace = Path.Combine(dataFolder, "Workspace");
        var generated = FolderSize(Path.Combine(workspace, "GeneratedImages"));
        var attached = FolderSize(Path.Combine(workspace, "AttachedImages"));
        var workspaceFiles = Math.Max(0, FolderSize(workspace) - generated - attached);
        var total = FolderSize(dataFolder);
        var other = Math.Max(0, total - database - settings - generated - attached - workspaceFiles);

        // The database is always listed; the others only when there is something in them.
        lines.Add($"Database (chats, memories, knowledge bases, jobs): {FormatSize(database)}");
        if (generated > 0) lines.Add($"Generated pictures: {FormatSize(generated)}");
        if (attached > 0) lines.Add($"Attached pictures: {FormatSize(attached)}");
        if (workspaceFiles > 0) lines.Add($"Other workspace files: {FormatSize(workspaceFiles)}");
        if (settings > 0) lines.Add($"Settings file: {FormatSize(settings)}");
        if (other > 0) lines.Add($"Other: {FormatSize(other)}");
        lines.Add($"Total: {FormatSize(total)}");
        return lines;
    }

    /// <summary>
    /// A size the way the computer's own file manager shows it: Windows counts 1 KB as 1,024
    /// bytes, other systems as 1,000, so the numbers match what you see there.
    /// </summary>
    public static string FormatSize(long bytes)
    {
        var step = OperatingSystem.IsWindows() ? 1024.0 : 1000.0;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= step && unit < units.Length - 1)
        {
            value /= step;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    private static long FileSize(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    private static long FolderSize(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return 0;
            long sum = 0;
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                sum += FileSize(file);
            }
            return sum;
        }
        catch
        {
            return 0;
        }
    }
}
