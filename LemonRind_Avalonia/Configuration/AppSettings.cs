namespace LemonRindAvalonia.Configuration;

/// <summary>
/// Strongly-typed view of appsettings.json's "Lemonade" section. Kept
/// separate from AppDataSettings/etc below so each config section can be
/// bound and passed around independently, e.g. only the Lemonade settings
/// get handed to the chat client factory.
///
/// Ported from the VB.NET/WPF LemonRind app's Configuration\AppSettings.vb -
/// brought over in full now (all stages' fields), not stripped down to a
/// Stage-1-only subset, since this is a small, self-contained, non-UI file:
/// splitting it into artificial per-stage slices would mean re-editing it
/// repeatedly for no real benefit. Fields belonging to features not ported
/// yet (WebSearch, Persona, Backup, ...) simply sit unused until their own
/// stage's C# code exists.
/// </summary>
public class LemonadeSettings
{
    public string BaseUrl { get; set; } = "http://localhost:13305/v1/";
    public string ChatModel { get; set; } = "";
    public string EmbeddingModel { get; set; } = "";

    /// <summary>
    /// Optional - Lemonade doesn't currently enforce an API key (any
    /// non-empty string works), but its docs recommend providing one
    /// anyway. Every client that talks to Lemonade falls back to the
    /// harmless "lemonade" placeholder string when this is blank.
    /// </summary>
    public string ApiKey { get; set; } = "";
}

public class ImageGenSettings
{
    public string ModelId { get; set; } = "";
    public int DefaultWidth { get; set; } = 512;
    public int DefaultHeight { get; set; } = 512;
    public int Steps { get; set; } = 4;
    public double CfgScale { get; set; } = 1.0;
    public int Seed { get; set; } = -1;
}

public class AppDataSettings
{
    /// <summary>
    /// Relative by default ("data") so the app is portable - copy the whole
    /// publish folder anywhere and its database/memories move with it.
    /// </summary>
    public string DataFolder { get; set; } = "data";

    /// <summary>
    /// DataFolder as stored may be relative (resolved against the app's own
    /// folder, AppContext.BaseDirectory - not the process's current working
    /// directory) or an absolute/%ENVVAR%-style path, returned as-is (after
    /// expansion).
    /// </summary>
    public string ResolvedDataFolder()
    {
        var expanded = Environment.ExpandEnvironmentVariables(DataFolder);
        return Path.IsPathRooted(expanded)
            ? expanded
            : Path.Combine(AppContext.BaseDirectory, expanded);
    }
}

public class WebSearchSettings
{
    public string SearXngBaseUrl { get; set; } = "http://localhost:8888/";
    public string Engine { get; set; } = "SearXNG";
    public string JinaApiKey { get; set; } = "";
    public string TavilyApiKey { get; set; } = "";
    public string FirecrawlApiKey { get; set; } = "";
}

public class BackupSettings
{
    public int IntervalDays { get; set; } = 7;
    public string BackupFolder { get; set; } = "data_backups";
    public int KeepCount { get; set; } = 5;

    public string ResolvedBackupFolder()
    {
        var expanded = Environment.ExpandEnvironmentVariables(BackupFolder);
        return Path.IsPathRooted(expanded)
            ? expanded
            : Path.Combine(AppContext.BaseDirectory, expanded);
    }
}

public class FileSystemSettings
{
    public List<string> AllowedRoots { get; set; } = [];
}

public class MemorySettings
{
    public int CompactionTriggerPercent { get; set; } = 75;
}

public class AssistantSettings
{
    public string SystemPrompt { get; set; } = "You are a helpful local AI assistant running on the user's own machine.";
}

public class PersonaSettings
{
    public string Identity { get; set; } = "";
    public string AboutUser { get; set; } = "";
    public string Tone { get; set; } = "Default";
    public string Verbosity { get; set; } = "Default";
    public string EmojiUsage { get; set; } = "Default";
    public string CustomInstructions { get; set; } = "";
}

public class UiSettings
{
    public bool ThinkingPanelOpenByDefault { get; set; } = false;

    /// <summary>"System" (follow the OS), "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Widths of the resizable side panes (set by dragging the splitters); 0 = use the default.</summary>
    public double SidebarWidth { get; set; } = 0;
    public double RightPanelWidth { get; set; } = 0;
}

public class ModuleSettings
{
    public Dictionary<string, bool> Enabled { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Root settings object, read/written whole by AppSettingsStore. Registered
/// as a singleton in DI so any service can ask for exactly the section it
/// needs.
/// </summary>
public class AppSettings
{
    public LemonadeSettings Lemonade { get; set; } = new();
    public AppDataSettings AppData { get; set; } = new();
    public WebSearchSettings WebSearch { get; set; } = new();
    public FileSystemSettings FileSystem { get; set; } = new();
    public MemorySettings Memory { get; set; } = new();
    public ModuleSettings Modules { get; set; } = new();
    public ImageGenSettings ImageGen { get; set; } = new();
    public AssistantSettings Assistant { get; set; } = new();
    public PersonaSettings Persona { get; set; } = new();
    public BackupSettings Backup { get; set; } = new();
    public UiSettings Ui { get; set; } = new();

    /// <summary>
    /// Copies every field's value from this into target's SAME nested
    /// objects (target.Lemonade.BaseUrl = ..., never target.Lemonade = ...)
    /// - a wholesale reassignment would detach target's own property from
    /// whatever a service captured a reference to at construction time.
    /// Used by the Settings screen's live reload.
    /// </summary>
    public void CopyInto(AppSettings target)
    {
        target.Lemonade.BaseUrl = Lemonade.BaseUrl;
        target.Lemonade.ChatModel = Lemonade.ChatModel;
        target.Lemonade.EmbeddingModel = Lemonade.EmbeddingModel;
        target.Lemonade.ApiKey = Lemonade.ApiKey;

        target.AppData.DataFolder = AppData.DataFolder;

        target.WebSearch.SearXngBaseUrl = WebSearch.SearXngBaseUrl;
        target.WebSearch.Engine = WebSearch.Engine;
        target.WebSearch.JinaApiKey = WebSearch.JinaApiKey;
        target.WebSearch.TavilyApiKey = WebSearch.TavilyApiKey;
        target.WebSearch.FirecrawlApiKey = WebSearch.FirecrawlApiKey;

        target.FileSystem.AllowedRoots = [.. FileSystem.AllowedRoots];

        target.Memory.CompactionTriggerPercent = Memory.CompactionTriggerPercent;

        target.Modules.Enabled = new Dictionary<string, bool>(Modules.Enabled, StringComparer.OrdinalIgnoreCase);

        target.ImageGen.ModelId = ImageGen.ModelId;
        target.ImageGen.DefaultWidth = ImageGen.DefaultWidth;
        target.ImageGen.DefaultHeight = ImageGen.DefaultHeight;
        target.ImageGen.Steps = ImageGen.Steps;
        target.ImageGen.CfgScale = ImageGen.CfgScale;
        target.ImageGen.Seed = ImageGen.Seed;

        target.Assistant.SystemPrompt = Assistant.SystemPrompt;

        target.Persona.Identity = Persona.Identity;
        target.Persona.AboutUser = Persona.AboutUser;
        target.Persona.Tone = Persona.Tone;
        target.Persona.Verbosity = Persona.Verbosity;
        target.Persona.EmojiUsage = Persona.EmojiUsage;
        target.Persona.CustomInstructions = Persona.CustomInstructions;

        target.Backup.IntervalDays = Backup.IntervalDays;
        target.Backup.BackupFolder = Backup.BackupFolder;
        target.Backup.KeepCount = Backup.KeepCount;

        target.Ui.ThinkingPanelOpenByDefault = Ui.ThinkingPanelOpenByDefault;
        target.Ui.Theme = Ui.Theme;
        target.Ui.SidebarWidth = Ui.SidebarWidth;
        target.Ui.RightPanelWidth = Ui.RightPanelWidth;
    }
}
