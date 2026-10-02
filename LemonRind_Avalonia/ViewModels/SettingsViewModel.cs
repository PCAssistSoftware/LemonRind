using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LemonRindAvalonia.Configuration;
using LemonRindAvalonia.Knowledge;
using LemonRindAvalonia.Mcp;
using LemonRindAvalonia.Memories;
using LemonRindAvalonia.Modules;
using LemonRindAvalonia.Scheduler;
using LemonRindAvalonia.Services;

namespace LemonRindAvalonia.ViewModels;

/// <summary>
/// Settings screen's view model. Works on its own freshly-loaded copy of
/// AppSettings (AppSettingsStore.LoadFresh()) so edits don't affect the
/// running app until Save. Save also pushes
/// every field into _liveSettings (the app's real, shared DI singleton -
/// see AppSettings.CopyInto's own comment for why a field-by-field copy,
/// not a wholesale reference swap, is what actually reaches already-
/// constructed services) and calls ModuleRegistry.ReconcileEnabledModulesAsync,
/// so most settings now take effect immediately with no restart. A small,
/// explicit set still needs one - see SaveAsync's own restart-required diff.
///
/// Ported in spirit (not line-by-line) from the VB.NET/WPF LemonRind app's
/// own SettingsViewModel.vb.
///
/// Left-nav section switching (Sections/SelectedSection, driving each
/// section panel's IsVisible via SectionVisibilityConverter) replaces the
/// original single-scrollable-page layout, matching the real WPF app's own
/// design once this screen had enough sections to need it.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettingsStore _store;
    private readonly AppSettings _settings;
    private readonly AppSettings _liveSettings;
    private readonly ModuleRegistry _moduleRegistry;
    private readonly KnowledgeRepository _knowledgeRepository;
    private readonly KnowledgeIngestionService _knowledgeIngestionService;
    private readonly McpServerRepository _mcpServerRepository;
    private readonly SchedulerRepository _schedulerRepository;
    private readonly MemoryService _memoryService;

    // The left-nav sections. New sections get added here and to
    // SettingsWindow.axaml together.
    public ObservableCollection<string> Sections { get; } =
    [
        "Lemonade", "Web search", "File system access", "Memory", "Assistant", "Persona", "Interface", "Storage",
        "Modules", "Knowledge Bases", "MCP Servers", "Scheduler", "Image generation", "Auto-backup",
    ];

    [ObservableProperty] private string _selectedSection = "Lemonade";

    [ObservableProperty] private string _lemonadeBaseUrl = "";
    [ObservableProperty] private string _lemonadeChatModel = "";
    [ObservableProperty] private string _lemonadeEmbeddingModel = "";
    [ObservableProperty] private string _lemonadeApiKey = "";

    [ObservableProperty] private string _searXngBaseUrl = "";

    // Alternate search/read engines.
    [ObservableProperty] private string _engine = "SearXNG";
    [ObservableProperty] private string _jinaApiKey = "";
    [ObservableProperty] private string _tavilyApiKey = "";
    [ObservableProperty] private string _firecrawlApiKey = "";
    public ObservableCollection<string> EngineOptions { get; } = ["SearXNG", "Jina", "Tavily", "Firecrawl"];

    [ObservableProperty] private string _allowedRootsText = "";

    [ObservableProperty] private double _compactionTriggerPercent = 75;

    // Memories review screen - what the assistant has learned about the
    // user over time (MemoryService.ExtractAndSaveFactsAsync, running
    // silently after every reply), split into the same two
    // sections the system prompt itself treats differently: pinned facts
    // (always injected verbatim) vs. searchable ones (only folded in when
    // relevant to the current turn). Plain DB rows, not appsettings.json
    // fields - edits/deletes take effect immediately, no Save/restart
    // needed for anything in this section, same as Knowledge Bases.
    public ObservableCollection<MemoryItemViewModel> PinnedMemories { get; } = [];
    public ObservableCollection<MemoryItemViewModel> SearchableMemories { get; } = [];

    // Avalonia's binding engine has no built-in int-to-bool conversion for
    // "PinnedMemories.Count" (the same gap MainViewModel
    // documents for ToolCalls.Count) - these are plain computed
    // bools, manually re-raised after the two places that ever mutate the
    // collections (RefreshMemories, DeleteMemory), rather than wiring up
    // CollectionChanged for collections only this class's own code touches.
    public bool HasPinnedMemories => PinnedMemories.Count > 0;
    public bool HasSearchableMemories => SearchableMemories.Count > 0;

    [ObservableProperty] private string _memoriesStatusText = "";

    [ObservableProperty] private string _systemPrompt = "";

    // Identity/AboutUser/writing-style, folded into the stable
    // system prompt (MainViewModel.BuildStableSystemPromptText) ahead of
    // the base SystemPrompt's own operating instructions - "who am I / who
    // are you talking to" reads naturally before "how should you behave".
    [ObservableProperty] private string _personaIdentity = "";
    [ObservableProperty] private string _personaAboutUser = "";
    [ObservableProperty] private string _personaTone = "Default";
    [ObservableProperty] private string _personaVerbosity = "Default";
    [ObservableProperty] private string _personaEmojiUsage = "Default";
    [ObservableProperty] private string _personaCustomInstructions = "";

    public ObservableCollection<string> PersonaToneOptions { get; } = ["Default", "Casual", "Formal", "Warm", "Concise", "Direct"];
    public ObservableCollection<string> PersonaVerbosityOptions { get; } = ["Default", "Concise", "Detailed"];
    public ObservableCollection<string> PersonaEmojiUsageOptions { get; } = ["Default", "None", "Sparing", "Frequent"];

    [ObservableProperty] private bool _thinkingPanelOpenByDefault;

    // Theme (System / Light / Dark): applied the moment it's picked so the choice is visible straight away, and
    // persisted with Save like every other field.
    public string[] ThemeOptions => LemonRindAvalonia.Services.ThemeService.Options;
    [ObservableProperty] private string _theme = "System";
    partial void OnThemeChanged(string value) => LemonRindAvalonia.Services.ThemeService.Apply(value);

    [ObservableProperty] private string _dataFolder = "";

    [ObservableProperty] private string _statusText = "";

    // Drives the "Restart now" button's own visibility - only true right
    // after a Save that actually changed one of the four restart-required
    // fields (most saves take effect live with no restart).
    [ObservableProperty] private bool _restartNeeded;

    // Reconciling modules (most notably a full Mcp disconnect+reconnect
    // cycle, which can take several seconds) can
    // make Save look like it did nothing at all with no feedback in
    // between - IsSaving disables the button and StatusText gets an
    // immediate "Saving..." before any of the slow work starts, not just
    // after it finishes.
    [ObservableProperty] private bool _isSaving;

    public ObservableCollection<ModuleToggleItem> Modules { get; } = [];

    // "This module is disabled" banners for the module-backed sections
    // below - matches the real WPF app's own convention of every module's
    // own Settings section carrying this banner (the section stays fully
    // usable either way, this is purely informational).
    public ModuleToggleItem? WebSearchModuleToggle { get; private set; }
    public ModuleToggleItem? FileSystemModuleToggle { get; private set; }
    public ModuleToggleItem? KnowledgeModuleToggle { get; private set; }
    public ModuleToggleItem? McpModuleToggle { get; private set; }
    public ModuleToggleItem? SchedulerModuleToggle { get; private set; }
    public ModuleToggleItem? ImageGenModuleToggle { get; private set; }
    public ModuleToggleItem? BackupModuleToggle { get; private set; }

    public ObservableCollection<KnowledgeBaseCardViewModel> KnowledgeBaseCards { get; } = [];

    [ObservableProperty] private string _newKnowledgeBaseName = "";
    [ObservableProperty] private string _newKnowledgeBaseDescription = "";

    public ObservableCollection<McpServerRowViewModel> McpServers { get; } = [];

    [ObservableProperty] private string _pendingMcpServerConfigJson = "";
    [ObservableProperty] private string _newMcpServerName = "";
    [ObservableProperty] private string _newMcpServerCommand = "";
    [ObservableProperty] private string _newMcpServerArguments = "";
    [ObservableProperty] private string _newMcpServerEnvironmentVariables = "";
    [ObservableProperty] private string _mcpStatusText = "";

    // "(use current model)" sentinel, the same convention the
    // WPF app uses, since a job's ModelId is optional (null/empty
    // means "whatever the client defaults to" - see ScheduledJobRunner).
    public const string UseCurrentModelSentinel = "(use current model)";

    public ObservableCollection<ScheduledJobRowViewModel> ScheduledJobs { get; } = [];
    public ObservableCollection<string> SchedulerAvailableModels { get; } = [UseCurrentModelSentinel];

    [ObservableProperty] private string _newJobName = "";
    [ObservableProperty] private string _newJobCronExpressionText = "0 19 * * 5";
    [ObservableProperty] private string _newJobPrompt = "";
    [ObservableProperty] private string _newJobModelId = UseCurrentModelSentinel;
    [ObservableProperty] private string _schedulerStatusText = "";

    // GUI cron builder - the same Daily/Weekly/Monthly picker as the WPF app
    // (see the Scheduler section's comment in SettingsWindow.axaml). The builder
    // controls below just compute and overwrite NewJobCronExpressionText
    // whenever one of them changes and JobFrequency isn't "Custom" - someone
    // who wants a schedule the simple picker can't express can still
    // type/edit raw cron syntax directly in that same field either way.
    public ObservableCollection<string> JobFrequencyOptions { get; } = ["Daily", "Weekly", "Monthly", "Custom (type your own)"];

    [ObservableProperty] private string _jobFrequency = "Daily";
    partial void OnJobFrequencyChanged(string value) => RebuildCronFromBuilder();

    public ObservableCollection<int> JobHourOptions { get; } = [.. Enumerable.Range(0, 24)];

    [ObservableProperty] private int _jobHour = 9;
    partial void OnJobHourChanged(int value) => RebuildCronFromBuilder();

    // 5-minute steps (00, 05, ..., 55) - covers virtually every real
    // schedule without needing a full 0-59 picker.
    public ObservableCollection<int> JobMinuteOptions { get; } = [.. Enumerable.Range(0, 12).Select(i => i * 5)];

    [ObservableProperty] private int _jobMinute;
    partial void OnJobMinuteChanged(int value) => RebuildCronFromBuilder();

    // Standard cron day-of-week numbering (0 = Sunday ... 6 = Saturday)
    // matches System.DayOfWeek's own values exactly, so this doubles as the
    // ComboBox's bound items with no translation needed.
    public ObservableCollection<DayOfWeek> JobDayOfWeekOptions { get; } =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    [ObservableProperty] private DayOfWeek _jobDayOfWeek = DayOfWeek.Monday;
    partial void OnJobDayOfWeekChanged(DayOfWeek value) => RebuildCronFromBuilder();

    public ObservableCollection<int> JobDayOfMonthOptions { get; } = [.. Enumerable.Range(1, 31)];

    [ObservableProperty] private int _jobDayOfMonth = 1;
    partial void OnJobDayOfMonthChanged(int value) => RebuildCronFromBuilder();

    /// <summary>Standard 5-field cron - minute hour day-of-month month day-of-week.</summary>
    private void RebuildCronFromBuilder()
    {
        NewJobCronExpressionText = JobFrequency switch
        {
            "Daily" => $"{JobMinute} {JobHour} * * *",
            "Weekly" => $"{JobMinute} {JobHour} * * {(int)JobDayOfWeek}",
            "Monthly" => $"{JobMinute} {JobHour} {JobDayOfMonth} * *",
            // "Custom (type your own)" - leave whatever's already there
            // alone, this is the one case where the raw field is the
            // user's own to edit freely.
            _ => NewJobCronExpressionText,
        };
    }

    [ObservableProperty] private string _imageGenModelId = "";
    [ObservableProperty] private double _imageGenDefaultWidth = 512;
    [ObservableProperty] private double _imageGenDefaultHeight = 512;
    [ObservableProperty] private double _imageGenSteps = 4;
    [ObservableProperty] private double _imageGenCfgScale = 1;
    [ObservableProperty] private double _imageGenSeed = -1;

    // Auto-backup. Kept separate from the data folder by design
    // (BackupFolder is never nested inside DataFolder - see
    // AutoBackupModule's own PathsOverlap defensive check).
    [ObservableProperty] private double _backupIntervalDays = 7;
    [ObservableProperty] private string _backupFolder = "";
    [ObservableProperty] private double _backupKeepCount = 5;

    public SettingsViewModel(
        AppSettingsStore store,
        AppSettings liveSettings,
        ModuleRegistry moduleRegistry,
        KnowledgeRepository knowledgeRepository,
        KnowledgeIngestionService knowledgeIngestionService,
        McpServerRepository mcpServerRepository,
        SchedulerRepository schedulerRepository,
        LemonadeManagementClient managementClient,
        MemoryService memoryService)
    {
        _store = store;
        _memoryService = memoryService;
        _liveSettings = liveSettings;
        _moduleRegistry = moduleRegistry;
        _knowledgeRepository = knowledgeRepository;
        _knowledgeIngestionService = knowledgeIngestionService;
        _mcpServerRepository = mcpServerRepository;
        _schedulerRepository = schedulerRepository;
        _settings = store.LoadFresh();

        LemonadeBaseUrl = _settings.Lemonade.BaseUrl;
        LemonadeChatModel = _settings.Lemonade.ChatModel;
        LemonadeEmbeddingModel = _settings.Lemonade.EmbeddingModel;
        LemonadeApiKey = _settings.Lemonade.ApiKey;

        SearXngBaseUrl = _settings.WebSearch.SearXngBaseUrl;
        Engine = _settings.WebSearch.Engine;
        JinaApiKey = _settings.WebSearch.JinaApiKey;
        TavilyApiKey = _settings.WebSearch.TavilyApiKey;
        FirecrawlApiKey = _settings.WebSearch.FirecrawlApiKey;

        AllowedRootsText = string.Join(Environment.NewLine, _settings.FileSystem.AllowedRoots);

        CompactionTriggerPercent = _settings.Memory.CompactionTriggerPercent;

        SystemPrompt = _settings.Assistant.SystemPrompt;

        PersonaIdentity = _settings.Persona.Identity;
        PersonaAboutUser = _settings.Persona.AboutUser;
        PersonaTone = _settings.Persona.Tone;
        PersonaVerbosity = _settings.Persona.Verbosity;
        PersonaEmojiUsage = _settings.Persona.EmojiUsage;
        PersonaCustomInstructions = _settings.Persona.CustomInstructions;

        ImageGenModelId = _settings.ImageGen.ModelId;
        ImageGenDefaultWidth = _settings.ImageGen.DefaultWidth;
        ImageGenDefaultHeight = _settings.ImageGen.DefaultHeight;
        ImageGenSteps = _settings.ImageGen.Steps;
        ImageGenCfgScale = _settings.ImageGen.CfgScale;
        ImageGenSeed = _settings.ImageGen.Seed;

        BackupIntervalDays = _settings.Backup.IntervalDays;
        BackupFolder = _settings.Backup.BackupFolder;
        BackupKeepCount = _settings.Backup.KeepCount;

        ThinkingPanelOpenByDefault = _settings.Ui.ThinkingPanelOpenByDefault;
        Theme = string.IsNullOrWhiteSpace(_settings.Ui.Theme) ? "System" : _settings.Ui.Theme;
        // Test hook: LEMONRIND_SECTION=Modules etc. preselects a section (used for screenshots).
        if (Environment.GetEnvironmentVariable("LEMONRIND_SECTION") is { } testSection && Sections.Contains(testSection)) SelectedSection = testSection;

        DataFolder = _settings.AppData.DataFolder;

        foreach (var module in moduleRegistry.AllModules)
        {
            Modules.Add(new ModuleToggleItem(module.Name, module.Description, module.ConfigKey, module.IsEnabled));
        }

        // Looked up by ConfigKey rather than duplicating a second enabled-
        // flag - each section's own "this module is disabled" banner binds
        // straight to the SAME live ModuleToggleItem the Modules checkbox
        // list uses, so toggling it there updates the banner immediately,
        // no separate refresh needed.
        WebSearchModuleToggle = Modules.FirstOrDefault(m => m.ConfigKey == "WebSearch");
        FileSystemModuleToggle = Modules.FirstOrDefault(m => m.ConfigKey == "FileSystem");
        KnowledgeModuleToggle = Modules.FirstOrDefault(m => m.ConfigKey == "Knowledge");
        McpModuleToggle = Modules.FirstOrDefault(m => m.ConfigKey == "Mcp");
        SchedulerModuleToggle = Modules.FirstOrDefault(m => m.ConfigKey == "Scheduler");
        ImageGenModuleToggle = Modules.FirstOrDefault(m => m.ConfigKey == "ImageGen");
        BackupModuleToggle = Modules.FirstOrDefault(m => m.ConfigKey == "Backup");

        RefreshKnowledgeBaseCards();
        RefreshMcpServers();
        RefreshScheduledJobs();
        RefreshMemories();
        RebuildCronFromBuilder(); // populates the default "Daily 09:00" cron text immediately, instead of starting on the old hardcoded placeholder
        LoadAvailableModelsFireAndForget(managementClient);
    }

    /// <summary>Loads every stored memory fresh from the database, split into the two sections the review screen shows.</summary>
    private void RefreshMemories()
    {
        PinnedMemories.Clear();
        SearchableMemories.Clear();

        foreach (var item in _memoryService.ListAllFacts())
        {
            var row = new MemoryItemViewModel { Id = item.Id, IsPinned = item.IsPinned, CreatedAt = item.CreatedAt, Content = item.Content };
            (item.IsPinned ? PinnedMemories : SearchableMemories).Add(row);
        }
        OnPropertyChanged(nameof(HasPinnedMemories));
        OnPropertyChanged(nameof(HasSearchableMemories));
    }

    [RelayCommand]
    private async Task SaveMemoryEdit(MemoryItemViewModel? row)
    {
        if (row is null || string.IsNullOrWhiteSpace(row.Content)) return;

        var item = new MemoryItem { Id = row.Id, Content = row.Content, IsPinned = row.IsPinned, CreatedAt = row.CreatedAt };
        await _memoryService.UpdateFactAsync(item, row.Content, CancellationToken.None);
        MemoriesStatusText = "Saved.";
    }

    [RelayCommand]
    private void DeleteMemory(MemoryItemViewModel? row)
    {
        if (row is null) return;

        _memoryService.DeleteFact(row.Id);
        PinnedMemories.Remove(row);
        SearchableMemories.Remove(row);
        OnPropertyChanged(nameof(HasPinnedMemories));
        OnPropertyChanged(nameof(HasSearchableMemories));
        MemoriesStatusText = "Deleted.";
    }

    /// <summary>
    /// Same downloaded-models call as MainViewModel.InitializeAsync, just
    /// for the Scheduler section's model picker - fire-and-forget since the
    /// constructor can't be async, and the picker is fine appearing a
    /// moment after the window itself (the sentinel default already works
    /// while this is still in flight). CancellationToken.None is fine here -
    /// a single short-lived GET tied to the Settings window's own lifetime,
    /// not a model generation that could run away.
    /// </summary>
    private async void LoadAvailableModelsFireAndForget(LemonadeManagementClient managementClient)
    {
        try
        {
            var models = await managementClient.ListDownloadedModelsAsync(CancellationToken.None);
            foreach (var model in models) SchedulerAvailableModels.Add(model.Id);
        }
        catch
        {
            // Best-effort - Lemonade might not be reachable when Settings is
            // opened; the sentinel default still works fine either way.
        }
    }

    /// <summary>
    /// Knowledge Bases are plain DB rows, not part of appsettings.json, so
    /// creating/deleting one takes effect immediately - no restart, unlike
    /// every field above this point.
    /// </summary>
    [RelayCommand]
    private void CreateKnowledgeBase()
    {
        if (string.IsNullOrWhiteSpace(NewKnowledgeBaseName)) return;
        _knowledgeRepository.CreateKnowledgeBase(NewKnowledgeBaseName.Trim(), NewKnowledgeBaseDescription.Trim());
        NewKnowledgeBaseName = "";
        NewKnowledgeBaseDescription = "";
        RefreshKnowledgeBaseCards();
    }

    private void RefreshKnowledgeBaseCards()
    {
        KnowledgeBaseCards.Clear();
        foreach (var kb in _knowledgeRepository.ListKnowledgeBases())
        {
            KnowledgeBaseCards.Add(new KnowledgeBaseCardViewModel(kb, _knowledgeRepository, _knowledgeIngestionService, RefreshKnowledgeBaseCards));
        }
    }

    private void RefreshMcpServers()
    {
        McpServers.Clear();
        foreach (var config in _mcpServerRepository.ListServers())
        {
            McpServers.Add(ToRowViewModel(config));
        }
    }

    private McpServerRowViewModel ToRowViewModel(McpServerConfig config)
    {
        var row = new McpServerRowViewModel(
            config.Id,
            config.Name,
            config.Command,
            string.Join(" ", config.Arguments),
            string.Join(", ", config.EnvironmentVariables.Keys),
            config.IsEnabled);
        row.PersistIsEnabled = isEnabled => _ = ToggleMcpServerAsync(row, isEnabled);
        return row;
    }

    /// <summary>
    /// Matches the standard `{"mcpServers": {"name": {"command", "args",
    /// "env"}}}` config shape most MCP servers' own docs give a
    /// ready-to-paste snippet of - the real, primary way a server gets
    /// added here, not just an alternative to the manual fields below.
    /// Like Knowledge Bases, these are plain DB rows so the write itself is
    /// immediate; connecting to the server is McpModule's job (see its comment).
    /// </summary>
    [RelayCommand]
    private async Task ImportMcpServerConfigJson()
    {
        if (string.IsNullOrWhiteSpace(PendingMcpServerConfigJson)) return;

        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var parsed = JsonSerializer.Deserialize<McpConfigFileJson>(PendingMcpServerConfigJson, options);
            if (parsed?.mcpServers is null || parsed.mcpServers.Count == 0)
            {
                McpStatusText = "No servers found - expected a top-level \"mcpServers\" object.";
                return;
            }

            var importedNames = new List<string>();
            foreach (var (serverName, serverJson) in parsed.mcpServers)
            {
                if (string.IsNullOrWhiteSpace(serverJson.command)) continue;

                _mcpServerRepository.CreateServer(serverName, serverJson.command, serverJson.args ?? [], serverJson.env ?? []);
                importedNames.Add(serverName);
            }

            PendingMcpServerConfigJson = "";
            RefreshMcpServers();
            if (importedNames.Count > 0)
            {
                McpStatusText = "Connecting...";
                await _moduleRegistry.ReconcileEnabledModulesAsync(CancellationToken.None);
                McpStatusText = $"Imported and connected: {string.Join(", ", importedNames)}.";
            }
            else
            {
                McpStatusText = "Nothing valid to import - each server needs at least a \"command\".";
            }
        }
        catch (Exception ex)
        {
            McpStatusText = $"Couldn't parse that JSON: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CreateMcpServer()
    {
        if (string.IsNullOrWhiteSpace(NewMcpServerName) || string.IsNullOrWhiteSpace(NewMcpServerCommand)) return;

        var arguments = NewMcpServerArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        var environmentVariables = new Dictionary<string, string>();
        foreach (var line in NewMcpServerEnvironmentVariables.Split([Environment.NewLine, "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('=', 2);
            if (parts.Length == 2) environmentVariables[parts[0].Trim()] = parts[1].Trim();
        }

        _mcpServerRepository.CreateServer(NewMcpServerName.Trim(), NewMcpServerCommand.Trim(), arguments, environmentVariables);

        NewMcpServerName = "";
        NewMcpServerCommand = "";
        NewMcpServerArguments = "";
        NewMcpServerEnvironmentVariables = "";

        RefreshMcpServers();
        McpStatusText = "Connecting...";
        await _moduleRegistry.ReconcileEnabledModulesAsync(CancellationToken.None);
        McpStatusText = "Added and connected.";
    }

    [RelayCommand]
    private async Task DeleteMcpServer(McpServerRowViewModel? row)
    {
        if (row is null) return;
        _mcpServerRepository.DeleteServer(row.Id);
        RefreshMcpServers();
        McpStatusText = "Reconnecting...";
        await _moduleRegistry.ReconcileEnabledModulesAsync(CancellationToken.None);
        McpStatusText = "Deleted.";
    }

    private async Task ToggleMcpServerAsync(McpServerRowViewModel row, bool isEnabled)
    {
        _mcpServerRepository.SetEnabled(row.Id, isEnabled);
        McpStatusText = isEnabled ? $"Connecting {row.Name}..." : $"Disconnecting {row.Name}...";
        await _moduleRegistry.ReconcileEnabledModulesAsync(CancellationToken.None);
        McpStatusText = isEnabled ? $"{row.Name} connected." : $"{row.Name} disconnected.";
    }

    /// <summary>Matches the standard `{"mcpServers": {"name": {"command", "args", "env"}}}` shape most MCP servers' own docs give a ready-to-paste snippet of.</summary>
    private class McpConfigFileJson
    {
        public Dictionary<string, McpServerConfigJson>? mcpServers { get; set; }
    }

    private class McpServerConfigJson
    {
        public string command { get; set; } = "";
        public List<string>? args { get; set; }
        public Dictionary<string, string>? env { get; set; }
    }

    private void RefreshScheduledJobs()
    {
        ScheduledJobs.Clear();
        foreach (var job in _schedulerRepository.ListJobs())
        {
            ScheduledJobs.Add(ToScheduledJobRowViewModel(job));
        }
    }

    private ScheduledJobRowViewModel ToScheduledJobRowViewModel(ScheduledJob job)
    {
        var row = new ScheduledJobRowViewModel(
            job.Id,
            job.Name,
            job.CronExpression,
            job.Prompt,
            string.IsNullOrEmpty(job.ModelId) ? UseCurrentModelSentinel : job.ModelId,
            job.LastRunAt.HasValue ? $"{job.LastRunAt.Value.ToLocalTime():dd/MM/yyyy HH:mm}" : "Never run",
            job.NextRunAt.HasValue ? $"{job.NextRunAt.Value.ToLocalTime():dd/MM/yyyy HH:mm}" : "Never (invalid schedule)",
            job.IsEnabled);
        row.PersistIsEnabled = isEnabled => _schedulerRepository.SetEnabled(row.Id, isEnabled);
        return row;
    }

    /// <summary>
    /// Same cron-parsing/next-occurrence logic as SchedulerModule.ScheduleJob
    /// (the chat-tool path) - both need to validate and compute the same
    /// way, this is just the GUI entry point onto the same
    /// SchedulerRepository, added specifically so creating/testing a job
    /// doesn't depend on the model actually choosing to call the tool
    /// (the model sometimes narrates "Done!" without calling schedule_job
    /// at all).
    /// </summary>
    [RelayCommand]
    private void CreateScheduledJob()
    {
        if (string.IsNullOrWhiteSpace(NewJobName) || string.IsNullOrWhiteSpace(NewJobCronExpressionText) || string.IsNullOrWhiteSpace(NewJobPrompt)) return;

        try
        {
            // TimeZoneInfo.Local - cron fields are interpreted on the local
            // clock, not UTC (see SchedulerModule.ScheduleJob's own comment
            // for why that distinction matters).
            var parsed = Cronos.CronExpression.Parse(NewJobCronExpressionText);
            var nextRunAt = parsed.GetNextOccurrence(DateTime.UtcNow, TimeZoneInfo.Local, inclusive: false);

            var modelId = NewJobModelId == UseCurrentModelSentinel ? null : NewJobModelId;
            var id = _schedulerRepository.CreateJob(NewJobName, NewJobCronExpressionText, NewJobPrompt, nextRunAt, modelId);
            ScheduledJobs.Add(ToScheduledJobRowViewModel(_schedulerRepository.ListJobs().First(j => j.Id == id)));

            NewJobName = "";
            NewJobPrompt = "";
            NewJobModelId = UseCurrentModelSentinel;
            RebuildCronFromBuilder(); // re-syncs the cron text field with whatever the builder dropdowns still show, rather than leaving it blank while they still say e.g. "Daily 09:00"
            SchedulerStatusText = nextRunAt.HasValue
                ? $"Added - next run at {nextRunAt.Value.ToLocalTime():dd/MM/yyyy HH:mm}."
                : "Added, but that cron expression has no future occurrence - it will never actually run. Double-check it.";
        }
        catch (Exception ex)
        {
            SchedulerStatusText = $"Invalid cron expression: {ex.Message}";
        }
    }

    [RelayCommand]
    private void DeleteScheduledJob(ScheduledJobRowViewModel? row)
    {
        if (row is null) return;
        _schedulerRepository.DeleteJob(row.Id);
        ScheduledJobs.Remove(row);
    }

    /// <summary>Persists an edited job's Name/CronExpressionText/Prompt/ModelId (the row's own TextBoxes are already TwoWay-bound, this just needs to recompute NextRunAt and write it) - see CreateScheduledJob's comment on TimeZoneInfo.Local for why the cron re-parse matters, not just a formality.</summary>
    [RelayCommand]
    private void SaveScheduledJobEdit(ScheduledJobRowViewModel? row)
    {
        if (row is null || string.IsNullOrWhiteSpace(row.Name) || string.IsNullOrWhiteSpace(row.CronExpressionText) || string.IsNullOrWhiteSpace(row.Prompt)) return;

        try
        {
            var parsed = Cronos.CronExpression.Parse(row.CronExpressionText);
            var nextRunAt = parsed.GetNextOccurrence(DateTime.UtcNow, TimeZoneInfo.Local, inclusive: false);

            var modelId = row.ModelId == UseCurrentModelSentinel ? null : row.ModelId;
            _schedulerRepository.UpdateJob(row.Id, row.Name, row.CronExpressionText, row.Prompt, nextRunAt, modelId);
            // Simplest way to refresh NextRunDisplay/LastRunDisplay - a Save
            // is infrequent enough that reloading the whole list fresh from
            // the database isn't worth a more surgical single-row update.
            RefreshScheduledJobs();
            SchedulerStatusText = "Saved.";
        }
        catch (Exception ex)
        {
            SchedulerStatusText = $"Invalid cron expression: {ex.Message}";
        }
    }

    /// <summary>
    /// Diffed against the live singleton's CURRENT values (captured before
    /// CopyInto overwrites them) in SaveAsync - these four are baked into
    /// already-constructed singleton objects at DI startup (the Lemonade
    /// chat/embedding clients, the SQLite connection) and rebuilding those
    /// in place was judged a meaningfully bigger, riskier change than it is
    /// worth. Everything else in this screen goes live with no restart.
    /// </summary>
    private (string BaseUrl, string ChatModel, string EmbeddingModel, string DataFolder) CaptureRestartSensitiveValues()
        => (_liveSettings.Lemonade.BaseUrl, _liveSettings.Lemonade.ChatModel, _liveSettings.Lemonade.EmbeddingModel, _liveSettings.AppData.DataFolder);

    /// <summary>
    /// A base URL without a trailing slash silently breaks every relative
    /// request built against it - .NET's own URI combination rules treat a
    /// base ending without "/" as having its LAST PATH SEGMENT replaced by
    /// a relative reference, not appended to (e.g. "http://host:1/v1" +
    /// "models" → "http://host:1/models", dropping "/v1" entirely), rather
    /// than "http://host:1/v1/" + "models" → "http://host:1/v1/models" as
    /// intended. This is an easy mistake to make when hand-editing the field
    /// (it made the Settings screen unreachable right after a base URL edit)
    /// - normalizing on save
    /// means it's fixed here once instead of every client that builds a
    /// relative request against it needing its own defensive check.
    /// </summary>
    private static string NormalizeBaseUrl(string baseUrl)
        => string.IsNullOrEmpty(baseUrl) || baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        StatusText = "Saving...";
        var before = CaptureRestartSensitiveValues();

        _settings.Lemonade.BaseUrl = NormalizeBaseUrl(LemonadeBaseUrl.Trim());
        // Reflects the normalized value back into the textbox, so a
        // re-save is idempotent and the user can see what actually got saved.
        LemonadeBaseUrl = _settings.Lemonade.BaseUrl;
        _settings.Lemonade.ChatModel = LemonadeChatModel.Trim();
        _settings.Lemonade.EmbeddingModel = LemonadeEmbeddingModel.Trim();
        _settings.Lemonade.ApiKey = LemonadeApiKey.Trim();

        _settings.WebSearch.SearXngBaseUrl = SearXngBaseUrl.Trim();
        _settings.WebSearch.Engine = Engine;
        _settings.WebSearch.JinaApiKey = JinaApiKey.Trim();
        _settings.WebSearch.TavilyApiKey = TavilyApiKey.Trim();
        _settings.WebSearch.FirecrawlApiKey = FirecrawlApiKey.Trim();

        _settings.FileSystem.AllowedRoots = AllowedRootsText
            .Split([Environment.NewLine, "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        _settings.Memory.CompactionTriggerPercent = (int)CompactionTriggerPercent;

        _settings.Assistant.SystemPrompt = string.IsNullOrWhiteSpace(SystemPrompt)
            ? new AssistantSettings().SystemPrompt
            : SystemPrompt.Trim();

        _settings.Persona.Identity = PersonaIdentity.Trim();
        _settings.Persona.AboutUser = PersonaAboutUser.Trim();
        _settings.Persona.Tone = string.IsNullOrWhiteSpace(PersonaTone) ? "Default" : PersonaTone;
        _settings.Persona.Verbosity = string.IsNullOrWhiteSpace(PersonaVerbosity) ? "Default" : PersonaVerbosity;
        _settings.Persona.EmojiUsage = string.IsNullOrWhiteSpace(PersonaEmojiUsage) ? "Default" : PersonaEmojiUsage;
        _settings.Persona.CustomInstructions = PersonaCustomInstructions.Trim();

        foreach (var module in Modules)
        {
            _settings.Modules.Enabled[module.ConfigKey] = module.IsEnabled;
        }

        _settings.ImageGen.ModelId = ImageGenModelId.Trim();
        _settings.ImageGen.DefaultWidth = (int)ImageGenDefaultWidth;
        _settings.ImageGen.DefaultHeight = (int)ImageGenDefaultHeight;
        _settings.ImageGen.Steps = (int)ImageGenSteps;
        _settings.ImageGen.CfgScale = ImageGenCfgScale;
        _settings.ImageGen.Seed = (int)ImageGenSeed;

        _settings.Backup.IntervalDays = (int)BackupIntervalDays;
        _settings.Backup.BackupFolder = BackupFolder.Trim();
        _settings.Backup.KeepCount = (int)BackupKeepCount;

        _settings.Ui.ThinkingPanelOpenByDefault = ThinkingPanelOpenByDefault;
        _settings.Ui.Theme = Theme;

        _settings.AppData.DataFolder = string.IsNullOrWhiteSpace(DataFolder) ? new AppDataSettings().DataFolder : DataFolder.Trim();

        _store.Save(_settings);

        // [RelayCommand]'s generated AsyncRelayCommand doesn't surface an
        // exception thrown after this point back to the UI on its own (it
        // ends up on the command's own unobserved ExecutionTask, not
        // rethrown to whatever called Execute) - wrapping the rest in a
        // real try/catch means a real failure here shows up as a status
        // message instead of silently leaving RestartNeeded/StatusText
        // never updated, which otherwise looks identical to "nothing
        // needed to happen."
        try
        {
            // Pushes every field into the SAME nested objects every already-
            // constructed service captured a reference to at DI startup - see
            // AppSettings.CopyInto's own comment for why this has to be a
            // field-by-field copy, not target = source.
            _settings.CopyInto(_liveSettings);

            // Starts/stops modules whose enabled state actually changed, and
            // always reconnects Mcp specifically (its server list can change
            // via this same Settings screen without the module's own toggle
            // moving at all).
            await _moduleRegistry.ReconcileEnabledModulesAsync(CancellationToken.None);

            var after = CaptureRestartSensitiveValues();
            var restartRequired = before != after;
            RestartNeeded = restartRequired;

            StatusText = restartRequired
                ? "Saved - restart the app for the Lemonade connection/data folder changes to take effect."
                : "Settings saved and applied - no restart needed.";
        }
        catch (Exception ex)
        {
            StatusText = $"Saved to disk, but applying it live failed: {ex.Message}";
            RestartNeeded = true;
        }
        finally
        {
            IsSaving = false;
        }
    }
}
