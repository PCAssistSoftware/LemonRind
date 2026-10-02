using System.Text.Json;

namespace LemonRindAvalonia.Configuration;

/// <summary>
/// Reads/writes the app's real settings file. Ported directly from the
/// VB.NET/WPF LemonRind app's AppSettingsStore.vb - deliberately lives at
/// "data\appsettings.json" (not loose next to the .exe), so it travels with
/// the same portable data folder as the database/workspace. Not resolved
/// via AppData.DataFolder (itself a field inside this file) to avoid a
/// bootstrap chicken-and-egg problem.
/// </summary>
public class AppSettingsStore
{
    private readonly string _filePath;

    public AppSettingsStore()
    {
        _filePath = Path.Combine(AppContext.BaseDirectory, "data", "appsettings.json");
    }

    /// <summary>
    /// First run (or a fresh clone with no data folder yet): no file exists,
    /// so a brand-new AppSettings (its own property defaults are already
    /// generic and safe) is created, saved immediately so it exists on disk
    /// from here on, and returned.
    /// </summary>
    public AppSettings LoadFresh()
    {
        if (!File.Exists(_filePath))
        {
            var fresh = new AppSettings();
            Save(fresh);
            return fresh;
        }

        var json = File.ReadAllText(_filePath);
        var settings = JsonSerializer.Deserialize<AppSettings>(json);
        return settings ?? new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(settings, options);
        File.WriteAllText(_filePath, json);
    }
}
