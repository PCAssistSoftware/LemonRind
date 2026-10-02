using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.AI;
using OpenAIChatCompletionOptions = OpenAI.Chat.ChatCompletionOptions;
using OpenAIStreamingChatCompletionUpdate = OpenAI.Chat.StreamingChatCompletionUpdate;
using LemonRindAvalonia.Configuration;
using LemonRindAvalonia.Data;
using LemonRindAvalonia.Knowledge;
using LemonRindAvalonia.Memories;
using LemonRindAvalonia.Modules;
using LemonRindAvalonia.Scheduler;
using LemonRindAvalonia.Services;
using LemonRindAvalonia.Views;

namespace LemonRindAvalonia.ViewModels;

/// <summary>
/// Main window's view model. Ported from the VB.NET/WPF LemonRind app's
/// ViewModels\MainViewModel.vb. Covers the model selector, health, stats,
/// thinking panel, sessions sidebar, long-term memory and short-term
/// compaction.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly IChatClient _chatClient;
    private readonly LemonadeManagementClient _managementClient;
    private readonly ChatSessionRepository _sessionRepository;
    private readonly MemoryService _memoryService;
    private readonly ConversationCompactionService _compactionService;
    private readonly KnowledgeRepository _knowledgeRepository;
    private readonly KnowledgeService _knowledgeService;
    private readonly ModuleRegistry _moduleRegistry;
    private readonly SchedulerNotifier _schedulerNotifier;
    private readonly Func<SettingsWindow> _settingsWindowFactory;
    private readonly Func<LogViewerWindow> _logViewerWindowFactory;
    private readonly AppSettings _settings;
    private readonly ImageAttachmentService _imageAttachmentService;
    private readonly ImageGenerationService _imageGenerationService;
    private readonly DispatcherTimer _connectionRetryTimer;
    private readonly List<ChatMessage> _history = [];
    private readonly Dictionary<string, int> _modelContextWindows = new(StringComparer.OrdinalIgnoreCase);

    // Which downloaded models Lemonade's own /v1/models labels
    // "vision", populated alongside _modelContextWindows in InitializeAsync.
    // Kept separate rather than folded into that same dictionary since a
    // model can carry both a context window AND vision support at once.
    private readonly Dictionary<string, bool> _modelVisionSupport = new(StringComparer.OrdinalIgnoreCase);

    // The full /v1/models catalog entry per model id (labels,
    // size, recipe, static context window), for the model details dialog.
    // Kept as a third parallel dictionary rather than folding into the
    // other two - those are hot-path lookups (every SendAsync/dropdown
    // change), this one is read only when the details button is clicked.
    private readonly Dictionary<string, LemonadeModelInfo> _modelCatalog = new(StringComparer.OrdinalIgnoreCase);

    // Which capability category each downloaded model
    // belongs to, derived from Lemonade's own /v1/models "labels" field
    // ("chat"/"image"/"embeddings" are real label values it returns).
    // SendAsync checks this to route straight to image generation when an
    // image-labeled model is selected directly, rather than sending it a
    // normal chat request Lemonade would just reject with a confusing HTTP
    // 400 ("This model does not support chat completion").
    private readonly Dictionary<string, string> _modelCategories = new(StringComparer.OrdinalIgnoreCase);
    private const string ModelCategoryChat = "Chat";
    private const string ModelCategoryImage = "Image";
    private const string ModelCategoryEmbedding = "Embedding";
    private const string ModelCategoryOther = "Other";

    private static string DetermineModelCategory(IEnumerable<string> labels)
    {
        var labelSet = new HashSet<string>(labels, StringComparer.OrdinalIgnoreCase);
        if (labelSet.Contains("chat")) return ModelCategoryChat;
        if (labelSet.Contains("image")) return ModelCategoryImage;
        if (labelSet.Contains("embeddings")) return ModelCategoryEmbedding;
        return ModelCategoryOther;
    }

    private static int ModelCategorySortOrder(string category) => category switch
    {
        ModelCategoryChat => 0,
        ModelCategoryImage => 1,
        ModelCategoryEmbedding => 2,
        _ => 3,
    };

    private static string ModelCategoryHeaderText(string category) => category switch
    {
        ModelCategoryChat => "Chat models",
        ModelCategoryImage => "Image models",
        ModelCategoryEmbedding => "Embedding models",
        _ => "Other models",
    };

    private string _currentSessionId = "";
    private bool _isLoadingSession;

    // Backs the Stop button - set at the start of SendAsync, cleared in its
    // finally. Non-null exactly while a reply is streaming.
    private CancellationTokenSource? _generationCts;

    // Short-term memory / compaction - see CompactHistoryIfNeededAsync.
    // Deliberately in-memory only, not persisted (unlike MemoryService's
    // durable facts) - a fresh reload of a session doesn't need its
    // compaction summary restored, just the real transcript.
    private string _conversationSummaryText = "";
    private const int MessagesToKeepAfterCompaction = 6;

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];
    public ObservableCollection<string> AvailableModels { get; } = [];

    // A flat list mixing non-selectable
    // ModelSelectorRow headers ("Chat models", "Image models", ...) with
    // real model rows, rebuilt alongside AvailableModels in
    // RefreshAvailableModelsAsync. Avalonia has no built-in WPF-style
    // CollectionViewSource/PropertyGroupDescription grouping - this
    // "flatten with typed header rows" approach is the documented,
    // community-standard replacement, the same technique the
    // sidebar uses for folder headers (SidebarItems/
    // FolderHeaderSummary).
    public ObservableCollection<ModelSelectorRow> GroupedAvailableModels { get; } = [];

    public ObservableCollection<ChatSessionSummary> Sessions { get; } = [];

    // RAG. Index 0 is always a synthetic "(none)" placeholder
    // (Id=""), never a real row from KnowledgeRepository - KnowledgeService
    // already treats a null/empty knowledge base id as "none attached", so
    // this sentinel needs no special-casing anywhere else, it just falls
    // out of the existing IsNullOrEmpty check there.
    public ObservableCollection<KnowledgeBaseSummary> KnowledgeBases { get; } = [];

    [ObservableProperty]
    private bool _isKnowledgeModuleEnabled;

    [ObservableProperty]
    private string _inputText = "";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _isHealthy;

    // The detailed exception message behind "Lemonade
    // unreachable", shown as a ToolTip on the health indicator rather than
    // in the shared StatusText field, which doesn't have room for a long
    // connection-refused message without crowding out everything else in
    // the header. Null while healthy, so the tooltip itself doesn't show at
    // all (Avalonia suppresses a null ToolTip.Tip binding, same as WPF).
    [ObservableProperty]
    private string? _lastHealthCheckError;

    [ObservableProperty]
    private string _statsText = "";

    // Right panel's Stats tab - last-reply numbers (server-measured, per-
    // request, from GetStatsAsync) plus a running session total this app
    // accumulates itself turn-by-turn (never persisted - resets with
    // NewChat/LoadSession, same as the real WPF app's own ResetSessionStats).
    [ObservableProperty]
    private int _lastReplyInputTokens;

    [ObservableProperty]
    private int _lastReplyOutputTokens;

    [ObservableProperty]
    private double _lastReplyTokensPerSecond;

    [ObservableProperty]
    private double _lastReplyTimeToFirstToken;

    [ObservableProperty]
    private int _sessionTotalInputTokens;

    [ObservableProperty]
    private int _sessionTotalOutputTokens;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private int _contextUsageCurrent;

    [ObservableProperty]
    private int _contextUsageMax;

    [ObservableProperty]
    private string _contextUsageText = "";

    /// <summary>Total pixel width the context-breakdown popup's bar is drawn at - fixed rather than measured, so RefreshContextBreakdownAsync can precompute every segment's own width without a live layout pass.</summary>
    private const double ContextBreakdownBarWidth = 260;

    /// <summary>Drives the popup's IsOpen - toggled from MainWindow's PointerEntered/PointerExited handlers on the context-usage text, not a click, since this is a glanceable hover detail, not something to navigate into.</summary>
    [ObservableProperty]
    private bool _isContextBreakdownOpen;

    /// <summary>Null until RefreshContextBreakdownAsync first runs (on hover) - the popup's own XAML only reads this while IsContextBreakdownOpen is true, by which point it's always already set.</summary>
    [ObservableProperty]
    private ContextBreakdown? _contextBreakdownInfo;

    /// <summary>True while RefreshContextBreakdownAsync's real /v1/tokenize calls are in flight - guards against overlapping refreshes if the mouse re-enters quickly.</summary>
    [ObservableProperty]
    private bool _isContextBreakdownLoading;

    // How many times CompactHistoryIfNeededAsync has actually compacted
    // this session, for the breakdown popup's "compacted N times" badge -
    // same in-memory-only, per-session lifecycle as _conversationSummaryText,
    // reset alongside it.
    private int _compactionCount;

    // Caches PendingTokens' own live turn-context preview
    // (RefreshContextBreakdownAsync) keyed by the exact InputText (and
    // attached knowledge base) it was computed for - without this, every
    // single hover would re-run real memory/knowledge embedding searches
    // even when nothing had changed since the last hover.
    private string? _lastPendingPreviewInputText;
    private string? _lastPendingPreviewKnowledgeBaseId;
    private int _lastPendingPreviewTokens;

    // One-off file attachment staging. _attachedFileText holds the
    // full extracted text (set by AttachFile, consumed/cleared by the next
    // SendAsync); AttachedFileName is just the observable UI-facing chip
    // label shown above the input box while an attachment is queued.
    private string? _attachedFileText;

    [ObservableProperty]
    private string? _attachedFileName;

    // The staged, already-resized copy's path (set by AttachFile,
    // consumed/cleared by the next SendAsync) - an image and a document
    // never share the attachment slot, so this and AttachedFileName are
    // never both non-null. Observable (unlike _attachedFileText) because
    // it also drives the pre-send preview thumbnail and the vision-warning
    // check below.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AttachedImageVisionWarningText))]
    private string? _attachedImagePath;

    /// <summary>
    /// Blocks Send (via CanSend) and shows an inline explanation when an
    /// image is attached but the selected model isn't vision-labeled -
    /// mirrors how the (not-yet-ported) image-model dropdown routing in the
    /// real WPF app avoids a confusing HTTP 400 rather than letting
    /// Lemonade's own less-friendly error surface. Null (no warning, Send
    /// allowed) whenever no image is attached, regardless of model.
    /// </summary>
    public string? AttachedImageVisionWarningText =>
        AttachedImagePath is not null && !_modelVisionSupport.GetValueOrDefault(SelectedModel, false)
            ? "The selected model doesn't support image input - pick a vision-capable model or remove the attached image."
            : null;

    private string _selectedModel = "";
    public string SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (SetProperty(ref _selectedModel, value))
            {
                OnPropertyChanged(nameof(AttachedImageVisionWarningText));
                OnPropertyChanged(nameof(SelectedModelRow));
                SendCommand.NotifyCanExecuteChanged();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    SwitchModelFireAndForget(value);
                }
            }
        }
    }

    /// <summary>
    /// What the grouped model ComboBox actually binds SelectedItem to - a
    /// computed getter (looked up in GroupedAvailableModels by the real
    /// SelectedModel, kept in sync by the OnPropertyChanged calls in
    /// SelectedModel's own setter and after every GroupedAvailableModels
    /// rebuild) rather than a second, independently-settable field, so
    /// SelectedModel stays the single source of truth regardless of which
    /// code path changed it (this property's own setter, InitializeAsync's
    /// bypass-the-setter restore, RefreshAvailableModelsAsync's same
    /// bypass). Setting this to a header row is a deliberate no-op beyond
    /// re-raising PropertyChanged - that re-pulls the getter's still-
    /// correct value and pushes it back into the ComboBox, reverting its
    /// closed-box display to the real selected model rather than leaving a
    /// header's own text showing as if it were "selected".
    /// </summary>
    public ModelSelectorRow? SelectedModelRow
    {
        get => GroupedAvailableModels.FirstOrDefault(row => !row.IsHeader && row.ModelId == SelectedModel);
        set
        {
            if (value is not null && !value.IsHeader && value.ModelId is not null)
            {
                SelectedModel = value.ModelId;
            }
            OnPropertyChanged();
        }
    }

    private ChatSessionSummary? _selectedSession;
    public ChatSessionSummary? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (SetProperty(ref _selectedSession, value) && value is not null && !_isLoadingSession)
            {
                LoadSession(value.Id);
            }
        }
    }

    /// <summary>
    /// What the sidebar ListBox actually binds to: a flat mix of
    /// FolderHeaderSummary and ChatSessionSummary rows (a collapsed
    /// folder's sessions simply aren't in this list), rebuilt by
    /// RebuildSidebarItems whenever Sessions changes. Sessions itself stays
    /// the authoritative list every other method in this class reads/
    /// mutates, avoiding a type change rippling through DeleteSession/
    /// NewChat/SendAsync/etc.
    /// </summary>
    public ObservableCollection<object> SidebarItems { get; } = [];

    private object? _selectedSidebarItem;

    /// <summary>
    /// What the ListBox's own SelectedItem is really bound to (not
    /// SelectedSession directly) - SidebarItems can contain a
    /// FolderHeaderSummary, which SelectedSession's declared type
    /// (ChatSessionSummary) can't hold. Delegates into the existing,
    /// unchanged SelectedSession/LoadSession flow only when the newly
    /// selected row really is a session; clicking a folder header just
    /// leaves the previously selected session as-is.
    /// </summary>
    public object? SelectedSidebarItem
    {
        get => _selectedSidebarItem;
        set
        {
            if (SetProperty(ref _selectedSidebarItem, value) && value is ChatSessionSummary sessionValue)
            {
                SelectedSession = sessionValue;
            }
        }
    }

    private bool _isLoadingKnowledgeBaseSelection;
    private KnowledgeBaseSummary? _selectedKnowledgeBase;
    public KnowledgeBaseSummary? SelectedKnowledgeBase
    {
        get => _selectedKnowledgeBase;
        set
        {
            if (SetProperty(ref _selectedKnowledgeBase, value) && !_isLoadingKnowledgeBaseSelection)
            {
                _sessionRepository.SetAttachedKnowledgeBase(_currentSessionId, string.IsNullOrEmpty(value?.Id) ? null : value.Id);
            }
        }
    }

    private readonly AppSettingsStore _settingsStore;

    /// <summary>The saved widths of the two resizable side panes (0 = default), for the window to apply at startup.</summary>
    public (double Sidebar, double RightPanel) SavedPaneWidths => (_settings.Ui.SidebarWidth, _settings.Ui.RightPanelWidth);

    /// <summary>Remembers the pane widths after a splitter drag so they survive a restart (writes the settings file).</summary>
    public void SavePaneWidths(double sidebar, double rightPanel)
    {
        _settings.Ui.SidebarWidth = Math.Round(sidebar);
        _settings.Ui.RightPanelWidth = Math.Round(rightPanel);
        try { _settingsStore.Save(_settings); } catch { /* best-effort - a failed write just means the widths aren't remembered */ }
    }

    public MainViewModel(
        IChatClient chatClient,
        LemonadeManagementClient managementClient,
        ChatSessionRepository sessionRepository,
        MemoryService memoryService,
        ConversationCompactionService compactionService,
        KnowledgeRepository knowledgeRepository,
        KnowledgeService knowledgeService,
        ModuleRegistry moduleRegistry,
        SchedulerNotifier schedulerNotifier,
        Func<SettingsWindow> settingsWindowFactory,
        Func<LogViewerWindow> logViewerWindowFactory,
        AppSettings settings,
        ImageAttachmentService imageAttachmentService,
        ImageGenerationService imageGenerationService,
        AppSettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
        _chatClient = chatClient;
        _managementClient = managementClient;
        _sessionRepository = sessionRepository;
        _memoryService = memoryService;
        _compactionService = compactionService;
        _knowledgeRepository = knowledgeRepository;
        _knowledgeService = knowledgeService;
        _moduleRegistry = moduleRegistry;
        _schedulerNotifier = schedulerNotifier;
        _settingsWindowFactory = settingsWindowFactory;
        _logViewerWindowFactory = logViewerWindowFactory;
        _settings = settings;
        _imageAttachmentService = imageAttachmentService;
        _imageGenerationService = imageGenerationService;

        RefreshModuleStatusDisplay();

        // Raised from ScheduledJobRunner running on the poll Timer's own
        // thread pool thread, never the UI thread - Dispatcher.UIThread.Post
        // marshals both the sidebar refresh and (if it's the session
        // currently open) the visible transcript reload onto the UI thread,
        // the same reason WPF's own Dispatcher.Invoke was needed there.
        _schedulerNotifier.SessionUpdated += (_, sessionId) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                RefreshSessions();
                ReloadCurrentSessionIfMatching(sessionId);
            });

        // Keeps retrying in the background while Lemonade is
        // unreachable, so coming back doesn't require clicking Retry
        // manually. A no-op tick while already healthy costs nothing worth
        // guarding against, so this just runs for the app's whole lifetime
        // rather than being started/stopped around IsHealthy transitions.
        _connectionRetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _connectionRetryTimer.Tick += async (_, _) =>
        {
            if (!IsHealthy) await RefreshAvailableModelsAsync(CancellationToken.None);
        };
        _connectionRetryTimer.Start();
    }

    /// <summary>
    /// Opens a fresh SettingsWindow (Transient in DI, so each click gets its
    /// own instance/ViewModel/freshly-reloaded AppSettings copy - matches
    /// the real WPF app's own "Settings can be opened more than once per
    /// run" reasoning for using a factory instead of a shared singleton).
    /// Knowledge Bases are plain DB rows, not part of appsettings.json, so
    /// unlike every other Settings field they don't need a restart to take
    /// effect - refreshing the dropdown once the window closes is enough to
    /// pick up anything created/deleted there. Module toggles are
    /// live too (ModuleRegistry.ReconcileEnabledModulesAsync runs on every
    /// Settings save) - IsKnowledgeModuleEnabled was only ever computed
    /// once in InitializeAsync, so it needed the same "refresh when the
    /// window closes" treatment to actually reflect a toggle without a
    /// restart (otherwise the KB dropdown stayed
    /// visible after disabling the module until the next app launch).
    /// </summary>
    [RelayCommand]
    private void OpenSettings()
    {
        var window = _settingsWindowFactory();
        window.Closed += (_, _) =>
        {
            RefreshKnowledgeBases();
            IsKnowledgeModuleEnabled = _moduleRegistry.AllModules.FirstOrDefault(m => m.ConfigKey == "Knowledge")?.IsEnabled ?? false;
            RefreshModuleStatusDisplay();
        };
        window.Show();
    }

    /// <summary>Right panel's Modules tab - a plain snapshot list (IAssistantModule doesn't implement INotifyPropertyChanged, so this can't just bind live), refreshed at startup and whenever Settings closes.</summary>
    public ObservableCollection<ModuleStatusItem> ModuleStatusItems { get; } = [];

    private void RefreshModuleStatusDisplay()
    {
        ModuleStatusItems.Clear();
        foreach (var module in _moduleRegistry.AllModules)
        {
            ModuleStatusItems.Add(new ModuleStatusItem(module.Name, module.Description, module.IsEnabled));
        }
    }

    /// <summary>Right panel's Stats/Modules tab switch - CommandParameter is the tab name.</summary>
    [ObservableProperty]
    private string _rightPanelTab = "Stats";

    // Points at the current assistant bubble's own ToolCalls collection
    // (set in SendAsync right after that bubble is created) rather than a
    // second, independently-mutated list - same underlying ObservableCollection,
    // so the chip appears in both the bubble and the right panel from one
    // write. Session-only, same as ToolCalls itself - not persisted, reset
    // to empty on NewChat/LoadSession.
    [ObservableProperty]
    private ObservableCollection<ToolCallResult> _rightPanelToolCalls = [];

    [RelayCommand]
    private void SelectRightPanelTab(string tab) => RightPanelTab = tab;

    public bool IsStatsTabActive => RightPanelTab == "Stats";
    public bool IsModulesTabActive => RightPanelTab == "Modules";

    partial void OnRightPanelTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsStatsTabActive));
        OnPropertyChanged(nameof(IsModulesTabActive));
    }

    /// <summary>Opens a non-modal LogViewerWindow (Transient in DI) streaming Lemonade's own logs - meant to sit open alongside the main window, not block it.</summary>
    [RelayCommand]
    private void OpenLogViewer() => _logViewerWindowFactory().Show();

    /// <summary>Reloads KnowledgeBases from the database, preserving the current selection by Id if it still exists (falls back to the "(none)" placeholder otherwise).</summary>
    private void RefreshKnowledgeBases()
    {
        var previouslySelectedId = SelectedKnowledgeBase?.Id ?? "";

        KnowledgeBases.Clear();
        KnowledgeBases.Add(new KnowledgeBaseSummary { Id = "", Name = "(none)" });
        foreach (var kb in _knowledgeRepository.ListKnowledgeBases()) KnowledgeBases.Add(kb);

        _isLoadingKnowledgeBaseSelection = true;
        try { SelectedKnowledgeBase = KnowledgeBases.FirstOrDefault(kb => kb.Id == previouslySelectedId) ?? KnowledgeBases[0]; }
        finally { _isLoadingKnowledgeBaseSelection = false; }
    }

    /// <summary>
    /// Called once from MainWindow's Opened event. Refreshes the downloaded-
    /// models catalog/health (see RefreshAvailableModelsAsync), picks an
    /// initial selected model, and resumes the most recently-used chat.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _moduleRegistry.StartEnabledModulesAsync(cancellationToken);

        IsKnowledgeModuleEnabled = _moduleRegistry.AllModules.FirstOrDefault(m => m.ConfigKey == "Knowledge")?.IsEnabled ?? false;
        RefreshKnowledgeBases();

        await RefreshAvailableModelsAsync(cancellationToken);

        // First-run-only initial selection - RefreshAvailableModelsAsync
        // itself only ever preserves whatever's already selected, it never
        // picks one, so this still needs its own (best-effort, separate)
        // health check for /v1/health's currently-loaded model.
        try
        {
            var health = await _managementClient.GetHealthAsync(cancellationToken);
            var alreadyLoaded = health?.ModelLoaded;
            _selectedModel = !string.IsNullOrWhiteSpace(alreadyLoaded) && AvailableModels.Contains(alreadyLoaded)
                ? alreadyLoaded!
                : _settings.Lemonade.ChatModel;
        }
        catch
        {
            _selectedModel = _settings.Lemonade.ChatModel;
        }
        OnPropertyChanged(nameof(SelectedModel));
        OnPropertyChanged(nameof(SelectedModelRow));
        ContextUsageMax = _modelContextWindows.GetValueOrDefault(SelectedModel, 0);

        RefreshSessions();
        var mostRecent = Sessions.FirstOrDefault();
        if (mostRecent is not null)
        {
            _isLoadingSession = true;
            try { SelectedSession = mostRecent; }
            finally { _isLoadingSession = false; }
            LoadSession(mostRecent.Id);
        }
        else
        {
            NewChat();
        }
    }

    /// <summary>
    /// Refreshes the downloaded-models catalog and /v1/health - called at
    /// startup (via InitializeAsync) and again on retry (the Retry button,
    /// and the background 15s timer while unreachable). Preserves whatever
    /// model is currently selected (bypassing the normal setter, since
    /// nothing about the loaded model itself changed) rather than resetting
    /// it - InitializeAsync handles the one-time initial selection itself,
    /// separately, once this first call returns.
    /// </summary>
    public async Task RefreshAvailableModelsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var models = await _managementClient.ListDownloadedModelsAsync(cancellationToken);
            var previouslySelectedModel = SelectedModel;

            AvailableModels.Clear();
            _modelContextWindows.Clear();
            _modelVisionSupport.Clear();
            _modelCatalog.Clear();
            _modelCategories.Clear();
            // Chat models first, then Image, then Embedding, then anything
            // else - not just alphabetical, so a real chat model is always
            // the first, most-likely-intended thing the dropdown shows.
            foreach (var model in models.OrderBy(m => ModelCategorySortOrder(DetermineModelCategory(m.Labels))))
            {
                AvailableModels.Add(model.Id);
                _modelContextWindows[model.Id] = model.MaxContextWindow;
                _modelVisionSupport[model.Id] = model.Labels.Any(label => string.Equals(label, "vision", StringComparison.OrdinalIgnoreCase));
                _modelCatalog[model.Id] = model;
                _modelCategories[model.Id] = DetermineModelCategory(model.Labels);
            }

            // Grouped dropdown follow-up - one header row per category
            // actually present (never an empty "Embedding models" header if
            // nothing was downloaded in that category), each followed by
            // its own models in the same order AvailableModels was just
            // populated in.
            GroupedAvailableModels.Clear();
            string? currentCategory = null;
            foreach (var modelId in AvailableModels)
            {
                var category = _modelCategories[modelId];
                if (category != currentCategory)
                {
                    currentCategory = category;
                    GroupedAvailableModels.Add(new ModelSelectorRow { IsHeader = true, DisplayText = ModelCategoryHeaderText(category) });
                }
                GroupedAvailableModels.Add(new ModelSelectorRow { IsHeader = false, DisplayText = modelId, ModelId = modelId });
            }

            await _managementClient.GetHealthAsync(cancellationToken);

            // Reaching this line means both requests just succeeded, so
            // Lemonade is genuinely reachable - set back to True here, not
            // just False in the Catch below, so a brief outage doesn't leave
            // the health indicator permanently red once the server recovers.
            IsHealthy = true;
            LastHealthCheckError = null;

            if (AvailableModels.Contains(previouslySelectedModel))
            {
                SetProperty(ref _selectedModel, previouslySelectedModel, nameof(SelectedModel));
                SendCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(AttachedImageVisionWarningText));
            }
            OnPropertyChanged(nameof(SelectedModelRow));

            ContextUsageMax = _modelContextWindows.GetValueOrDefault(SelectedModel, 0);

            // Only refresh the displayed text if it was already showing a
            // real number (i.e. a turn has actually happened this chat) -
            // otherwise FormatContext
            // UsageText returns "0 / 131,072 tokens (0%)", not "", the
            // moment ContextUsageMax is non-zero, so clicking Retry (or the
            // background timer) on a brand-new chat with nothing sent yet
            // made a token count appear out of nowhere.
            if (!string.IsNullOrEmpty(ContextUsageText))
            {
                ContextUsageText = FormatContextUsageText();
            }
        }
        catch (Exception ex)
        {
            IsHealthy = false;
            LastHealthCheckError = $"Couldn't reach Lemonade: {ex.Message}";
        }
    }

    /// <summary>Clicking the health indicator while "Lemonade unreachable" is shown retries immediately, rather than waiting for the next background poll.</summary>
    [RelayCommand]
    private Task RetryConnection() => RefreshAvailableModelsAsync(CancellationToken.None);

    /// <summary>Stats tab button - shows the real, current _history[0] text, for visibility into what's actually being sent, with its token count (Lemonade's real tokenizer, chars/4 estimate if unreachable).</summary>
    [RelayCommand]
    private async Task OpenSystemPrompt()
    {
        var promptText = _history.Count > 0 ? _history[0].Text ?? "" : "";
        var tokenCount = await TryTokenizeAsync(promptText, CancellationToken.None);
        await SystemPromptDialog.ShowAsync(promptText,
            description: $"Exactly what's currently sitting at the front of the model's context for this chat - the same text that gets rebuilt each turn. (~{tokenCount:N0} tokens)");
    }

    /// <summary>
    /// Stats tab button - every enabled tool's real name/description/JSON
    /// schema, laid out readably (a "### name" header, description, indented
    /// schema JSON, "---" separators) like the WPF app, rather than one
    /// unbroken line. The token count is the SAME "tools" figure the context
    /// breakdown shows (computed from the compact wire-format text, plus the
    /// tool-use framework overhead estimate), not a count of this reformatted
    /// display text - formatting alone would read higher in an external tokenizer.
    /// </summary>
    [RelayCommand]
    private async Task OpenToolsSent()
    {
        var tools = _moduleRegistry.GetEnabledTools();
        if (tools.Count == 0)
        {
            await SystemPromptDialog.ShowAsync("(no modules with tools are currently enabled)", title: "Tools sent",
                description: "Every enabled tool's real name/description/JSON schema, exactly as sent to Lemonade alongside every request.");
            return;
        }

        var indented = new JsonSerializerOptions { WriteIndented = true };
        var sections = tools.Select(t =>
        {
            var schemaText = t is AIFunctionDeclaration declaration ? JsonSerializer.Serialize(declaration.JsonSchema, indented) : "(no schema)";
            return $"### {t.Name}{Environment.NewLine}{t.Description}{Environment.NewLine}{Environment.NewLine}{schemaText}";
        });
        var prettyToolsText = string.Join(Environment.NewLine + Environment.NewLine + "---" + Environment.NewLine + Environment.NewLine, sections);

        var toolsTokens = (await EstimateCommittedContextTokensAsync(CancellationToken.None)).Tools;
        await SystemPromptDialog.ShowAsync(prettyToolsText, title: "Tools sent",
            description: $"Every enabled tool's real name/description/JSON schema, exactly as sent to Lemonade alongside every request - this is what the context breakdown's \"tools\" figure is the token cost of (includes an estimated tool-use framework overhead on top of the schemas themselves). Reformatted here for readability, so pasting THIS text into an external tokenizer will read differently from the figure here. (~{toolsTokens:N0} tokens)");
    }

    public void RefreshSessions()
    {
        var results = _sessionRepository.SearchSessions(SearchText);
        var previouslySelectedId = _currentSessionId;

        Sessions.Clear();
        foreach (var session in results)
        {
            Sessions.Add(session);
        }

        var stillSelected = Sessions.FirstOrDefault(s => s.Id == previouslySelectedId);
        if (stillSelected is not null)
        {
            _isLoadingSession = true;
            try { SelectedSession = stillSelected; }
            finally { _isLoadingSession = false; }
        }

        RebuildSidebarItems();
    }

    /// <summary>
    /// Sessions itself (the authoritative, already-sorted list every other
    /// method in this class reads/mutates) is left untouched; this builds a
    /// separate, sidebar-display-only projection that interleaves a
    /// FolderHeaderSummary row before each folder's sessions, omitting a
    /// collapsed folder's sessions entirely. The "Unfiled" bucket gets a
    /// header too (for visual consistency with the grouping), but it's
    /// never collapsible - it has no real Folders row to persist a
    /// collapsed state against.
    /// </summary>
    private void RebuildSidebarItems()
    {
        var folders = _sessionRepository.ListFolders().ToDictionary(f => f.Id!);

        SidebarItems.Clear();
        var isFirstItem = true;
        string? currentFolderId = null;
        foreach (var session in Sessions)
        {
            if (isFirstItem || !string.Equals(session.FolderId, currentFolderId, StringComparison.Ordinal))
            {
                currentFolderId = session.FolderId;
                FolderHeaderSummary header;
                if (currentFolderId is null)
                {
                    header = new FolderHeaderSummary { FolderId = null, Name = "Unfiled", IsCollapsed = false, IsCollapsible = false };
                }
                else
                {
                    var isCollapsed = folders.GetValueOrDefault(currentFolderId)?.IsCollapsed ?? false;
                    header = new FolderHeaderSummary { FolderId = currentFolderId, Name = session.FolderName ?? "", IsCollapsed = isCollapsed };
                }
                header.ToggleExpandedCommand = new RelayCommand(() => ToggleFolderExpanded(header));
                SidebarItems.Add(header);
            }
            isFirstItem = false;

            var folderIsCollapsed = currentFolderId is not null && (folders.GetValueOrDefault(currentFolderId)?.IsCollapsed ?? false);
            if (!folderIsCollapsed) SidebarItems.Add(session);
        }

        // Keeps the ListBox's own highlighted row correct after a rebuild -
        // null (no visible selection) if the real selected session's
        // folder is currently collapsed, same "a collapsed group hides its
        // children, including a selected one" behavior a tree view has.
        _selectedSidebarItem = SidebarItems.OfType<ChatSessionSummary>().FirstOrDefault(s => s.Id == _currentSessionId);
        OnPropertyChanged(nameof(SelectedSidebarItem));
    }

    /// <summary>Click handler for a folder header row's own ToggleExpandedCommand - persists immediately, then a full RebuildSidebarItems (via RefreshSessions) picks up the new state.</summary>
    private void ToggleFolderExpanded(FolderHeaderSummary header)
    {
        if (!header.IsCollapsible || header.FolderId is null) return;

        _sessionRepository.SetFolderCollapsed(header.FolderId, !header.IsCollapsed);
        RefreshSessions();
    }

    /// <summary>MoveToFolderDialog handles both picking an existing folder and creating a new one; this just applies the result and refreshes (the sidebar is grouped by folder, so a move needs a full rebuild, not just an in-place property update).</summary>
    [RelayCommand]
    private async Task MoveToFolder(ChatSessionSummary? session)
    {
        if (session is null) return;

        var folders = _sessionRepository.ListFolders();
        var result = await MoveToFolderDialog.ShowAsync(folders, session.FolderId);
        if (!result.Confirmed) return;

        var folderId = result.FolderId;
        if (!string.IsNullOrWhiteSpace(result.NewFolderName))
        {
            folderId = _sessionRepository.CreateFolder(result.NewFolderName.Trim());
        }

        _sessionRepository.SetSessionFolder(session.Id, folderId);
        RefreshSessions();
    }

    /// <summary>TagManagerDialog writes each add/remove immediately, so this just refreshes afterward to pick up the new tag chips.</summary>
    [RelayCommand]
    private async Task ManageTags(ChatSessionSummary? session)
    {
        if (session is null) return;

        await TagManagerDialog.ShowAsync(session.Id, session.Tags, _sessionRepository);
        RefreshSessions();
    }

    // StatusText is otherwise persistent-until-overwritten
    // (connectivity warnings, "Loading {model}...") which is correct for
    // those, but a one-off confirmation like "Exported to ..." has no
    // natural next event to overwrite it, so it needs its own
    // flash-then-auto-clear timer.
    private DispatcherTimer? _statusTextClearTimer;

    private void ShowStatusTextTemporarily(string text)
    {
        StatusText = text;

        _statusTextClearTimer?.Stop();
        _statusTextClearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _statusTextClearTimer.Tick += (_, _) =>
        {
            StatusText = "";
            _statusTextClearTimer!.Stop();
        };
        _statusTextClearTimer.Start();
    }

    /// <summary>
    /// Reads straight from the DB (LoadMessages), not the live Messages
    /// collection, so this works for any session in the sidebar, not just
    /// whichever one happens to be currently open. Called from MainWindow's
    /// code-behind after its own Avalonia StorageProvider save-file dialog
    /// returns a destination path - file pickers are a View-side concern in
    /// Avalonia, same reasoning as AttachFile.
    /// </summary>
    public void ExportSessionToMarkdown(ChatSessionSummary session, string destinationPath)
    {
        try
        {
            var messages = _sessionRepository.LoadMessages(session.Id);
            var markdown = ChatMarkdownExporter.BuildMarkdown(session.Title, messages);
            File.WriteAllText(destinationPath, markdown);
            ShowStatusTextTemporarily($"Exported to {Path.GetFileName(destinationPath)}.");
        }
        catch (Exception ex)
        {
            ShowStatusTextTemporarily($"Couldn't export chat: {ex.Message}");
        }
    }

    partial void OnSearchTextChanged(string value) => RefreshSessions();

    [RelayCommand]
    private void NewChat()
    {
        _currentSessionId = _sessionRepository.CreateSession("New chat");
        _history.Clear();
        _conversationSummaryText = "";
        _compactionCount = 0;
        _lastPendingPreviewInputText = null;
        RefreshStableSystemPrompt();
        Messages.Clear();
        ContextUsageCurrent = 0;
        ContextUsageText = "";
        SessionTotalInputTokens = 0;
        SessionTotalOutputTokens = 0;
        RightPanelToolCalls = [];

        _isLoadingKnowledgeBaseSelection = true;
        try { SelectedKnowledgeBase = KnowledgeBases.Count > 0 ? KnowledgeBases[0] : null; }
        finally { _isLoadingKnowledgeBaseSelection = false; }

        RefreshSessions();
    }

    [RelayCommand]
    private void DeleteSession(ChatSessionSummary? session)
    {
        if (session is null) return;

        var wasCurrent = session.Id == _currentSessionId;
        _sessionRepository.DeleteSession(session.Id);
        RefreshSessions();

        if (wasCurrent)
        {
            var fallback = Sessions.FirstOrDefault();
            if (fallback is not null) LoadSession(fallback.Id);
            else NewChat();
        }
    }

    /// <summary>
    /// Switches a sidebar row into rename mode - moved here from a
    /// code-behind Click handler once the row's icon buttons (Rename/Move/
    /// Tags/Export/Delete) were replaced with a right-click context menu
    /// (direct feedback: five squeezed Unicode-emoji icons rendered as
    /// unrecognizable "tofu" boxes with no font that reliably shows them on
    /// both Windows and Linux - text menu items sidestep that risk
    /// entirely). No auto-focus-the-textbox behaviour like the old button
    /// click had (that relied on walking up from the clicked Button's own
    /// visual tree, which a MenuItem inside a separate popup doesn't have a
    /// clean equivalent for) - a minor, accepted UX trade-off, not a
    /// regression in anything essential.
    /// </summary>
    [RelayCommand]
    private void BeginRenameSession(ChatSessionSummary? session)
    {
        if (session is null) return;
        session.IsEditingTitle = true;
    }

    /// <summary>
    /// Persists a session's CURRENT Title (already updated in-memory by the
    /// sidebar's own two-way-bound TextBox) to the database - called from
    /// MainWindow's code-behind on that TextBox's LostFocus, not a
    /// [RelayCommand], since a tuple-parameter command has no clean XAML
    /// CommandParameter story for "which row, and its now-current text".
    /// </summary>
    public void CommitRename(ChatSessionSummary session)
    {
        var title = string.IsNullOrWhiteSpace(session.Title) ? "New chat" : session.Title.Trim();
        session.Title = title;
        _sessionRepository.RenameSession(session.Id, title);
    }

    /// <summary>
    /// Extracts a picked file's text immediately, not deferred to Send, so a
    /// corrupt/unsupported file fails right when picked, with the reason
    /// visible, rather than silently after a question about it has already
    /// been typed and sent. Called from MainWindow's code-behind after its
    /// own Avalonia StorageProvider file-picker dialog returns a path - file
    /// pickers are a View-side concern in Avalonia (no ViewModel-friendly
    /// equivalent of WPF's Microsoft.Win32.OpenFileDialog), so this method
    /// takes the already-picked path rather than being a parameterless
    /// [RelayCommand] that opens its own dialog.
    /// </summary>
    public void AttachFile(string filePath)
    {
        try
        {
            // One attachment slot holds an image OR a document,
            // never both; which path runs depends purely on the picked
            // file's extension. Downscaling happens right here, at attach
            // time (not deferred to Send), matching the document path's own
            // "fail fast, with the reason visible" reasoning - a corrupt/
            // unsupported image should fail before a question about it has
            // already been typed and sent, not after.
            if (_imageAttachmentService.IsSupported(filePath))
            {
                AttachedImagePath = _imageAttachmentService.SaveResizedCopy(filePath);
                _attachedFileText = null;
                AttachedFileName = null;
            }
            else
            {
                _attachedFileText = FileTextExtractor.ExtractText(filePath);
                AttachedFileName = Path.GetFileName(filePath);
                AttachedImagePath = null;
            }
        }
        catch (Exception ex)
        {
            _attachedFileText = null;
            AttachedFileName = null;
            AttachedImagePath = null;

            // Visible in the chat itself, not StatusText - the header
            // status slot is easy to miss, especially here where nothing
            // else about sending a message has happened yet to draw the
            // eye there. Not persisted - this is a pre-send UI failure, not
            // something either party actually said.
            var errorBubble = new ChatMessageViewModel(isUser: false, initialText: $"⚠️ Couldn't attach {Path.GetFileName(filePath)}: {ex.Message}");
            Messages.Add(errorBubble);
            SetDateDividerIfNeeded(errorBubble);
        }
    }

    [RelayCommand]
    private void RemoveAttachedFile()
    {
        _attachedFileText = null;
        AttachedFileName = null;
        AttachedImagePath = null;
    }

    /// <summary>Switches the chat window to an existing session's history. A no-op if it's already the open one.</summary>
    /// <summary>
    /// Reloads the given session's messages from the database if it's the
    /// one currently open - a no-op otherwise. Safe specifically because a
    /// scheduled job always writes into its own brand-new session, so
    /// sessionId matching _currentSessionId here only ever means "the user
    /// is watching this exact job run right now", never a stale/spurious
    /// reselect of an ordinary chat (the case LoadSession's own same-session
    /// guard exists to protect against).
    /// </summary>
    private void ReloadCurrentSessionIfMatching(string sessionId)
    {
        if (sessionId != _currentSessionId) return;
        LoadSession(sessionId, forceReload: true);
    }

    private void LoadSession(string sessionId, bool forceReload = false)
    {
        if (sessionId == _currentSessionId && !forceReload) return;

        _currentSessionId = sessionId;
        _history.Clear();
        _conversationSummaryText = "";
        _compactionCount = 0;
        _lastPendingPreviewInputText = null;
        RefreshStableSystemPrompt();
        Messages.Clear();
        ContextUsageCurrent = 0;
        ContextUsageText = "";
        SessionTotalInputTokens = 0;
        SessionTotalOutputTokens = 0;
        RightPanelToolCalls = [];

        var attachedKbId = _sessionRepository.GetAttachedKnowledgeBase(sessionId) ?? "";
        _isLoadingKnowledgeBaseSelection = true;
        try { SelectedKnowledgeBase = KnowledgeBases.FirstOrDefault(kb => kb.Id == attachedKbId) ?? (KnowledgeBases.Count > 0 ? KnowledgeBases[0] : null); }
        finally { _isLoadingKnowledgeBaseSelection = false; }

        foreach (var stored in _sessionRepository.LoadMessages(sessionId))
        {
            if (stored.Role == ChatRole.User.Value)
            {
                // Unlike the document-attachment marker (shown
                // as-is on reload, since there's no prettier equivalent),
                // an image marker is parsed back out: if the referenced
                // local file still exists, rebuild a real multi-content
                // message (so the newest one can still be resent, not just
                // remembered as text) and restore the bubble's thumbnail;
                // fall back to plain text if the file's gone.
                var (imagePath, remainingText) = ImageAttachmentService.TryParseAttachedImageMarker(stored.Content);
                if (imagePath is not null && File.Exists(imagePath))
                {
                    _history.Add(new ChatMessage(ChatRole.User, new List<AIContent> { new TextContent(stored.Content), _imageAttachmentService.BuildDataContent(imagePath) }));
                    var imageBubble = new ChatMessageViewModel(isUser: true, initialText: remainingText, createdAt: stored.CreatedAt.ToLocalTime()) { AttachedImagePath = imagePath };
                    Messages.Add(imageBubble);
                    SetDateDividerIfNeeded(imageBubble);
                }
                else
                {
                    _history.Add(new ChatMessage(ChatRole.User, stored.Content));
                    var userReloadBubble = new ChatMessageViewModel(isUser: true, initialText: stored.Content, createdAt: stored.CreatedAt.ToLocalTime());
                    Messages.Add(userReloadBubble);
                    SetDateDividerIfNeeded(userReloadBubble);
                }
            }
            else if (stored.Role == ChatRole.Assistant.Value)
            {
                _history.Add(new ChatMessage(ChatRole.Assistant, stored.Content));
                var bubble = new ChatMessageViewModel(isUser: false, initialText: stored.Content, createdAt: stored.CreatedAt.ToLocalTime())
                {
                    IsReasoningExpanded = _settings.Ui.ThinkingPanelOpenByDefault,
                };
                if (!string.IsNullOrEmpty(stored.ReasoningContent))
                {
                    bubble.ReasoningText = stored.ReasoningContent;
                }
                foreach (var toolCall in ToolCallResult.ParseFromStorage(stored.ToolCalls))
                {
                    bubble.ToolCalls.Add(toolCall);
                }
                bubble.GeneratedImagePath = TryExtractGeneratedImagePath(stored.Content);
                Messages.Add(bubble);
                SetDateDividerIfNeeded(bubble);

                // "Tool calls this turn" means the newly-opened session's
                // own last reply, not whatever chat was open before, so it is
                // restored here (not just left at the empty [] set above); that
                // works because real ToolCallResult data is persisted, not just
                // plain names.
                RightPanelToolCalls = bubble.ToolCalls;
            }
        }
    }

    /// <summary>
    /// "Today"/"Yesterday"/a full date, or null if the previous message was
    /// the same calendar day (no divider needed). Shared logic for both a
    /// live-session bubble (SendAsync) and a reloaded one (LoadSession).
    /// </summary>
    private static string? ComputeDateDividerText(DateTime? previousDate, DateTime currentCreatedAt)
    {
        var currentDate = currentCreatedAt.Date;
        if (previousDate.HasValue && previousDate.Value == currentDate) return null;

        if (currentDate == DateTime.Today) return "Today";
        if (currentDate == DateTime.Today.AddDays(-1)) return "Yesterday";
        return currentCreatedAt.ToString("dddd, d MMMM yyyy");
    }

    /// <summary>Call right after Messages.Add(bubble) - compares against whatever is now the second-to-last entry.</summary>
    private void SetDateDividerIfNeeded(ChatMessageViewModel bubble)
    {
        DateTime? previousDate = Messages.Count > 1 ? Messages[^2].CreatedAt.Date : null;
        bubble.DateDividerText = ComputeDateDividerText(previousDate, bubble.CreatedAt);
    }

    // Shown in the header while a model is loading (see MainWindow.axaml).
    // Lemonade reports no real load progress (POST /v1/load just blocks until
    // the model is ready), so this is an indeterminate bar plus an honest
    // elapsed-seconds counter - proof the app is working, not a prediction.
    [ObservableProperty]
    private bool _isModelLoading;

    [ObservableProperty]
    private string _modelLoadingText = "";

    private async void SwitchModelFireAndForget(string modelName)
    {
        IsBusy = true;
        StatusText = $"Loading {modelName}... this can take a while for a large model";
        SendCommand.NotifyCanExecuteChanged();

        var loadStopwatch = System.Diagnostics.Stopwatch.StartNew();
        string LoadingText() => $"Loading {modelName}... {(int)loadStopwatch.Elapsed.TotalSeconds}s";
        ModelLoadingText = LoadingText();
        IsModelLoading = true;
        var loadTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        loadTimer.Tick += (_, _) => ModelLoadingText = LoadingText();
        loadTimer.Start();
        try
        {
            await _managementClient.LoadModelAsync(modelName, CancellationToken.None);
            StatusText = "";
            ModelLoadingText = "";
            ContextUsageMax = _modelContextWindows.GetValueOrDefault(modelName, 0);
            ContextUsageText = FormatContextUsageText();
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load {modelName}: {ex.Message}";
            // StatusText isn't shown in the main window, so a failed load
            // would otherwise vanish silently - keep it visible in the same
            // header spot instead of just clearing the indicator.
            ModelLoadingText = StatusText;
        }
        finally
        {
            loadTimer.Stop();
            IsBusy = false;
            IsModelLoading = false;
            SendCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Static catalog detail (labels/size/recipe/context window) comes from
    /// the already-cached _modelCatalog; the richer runtime detail (device,
    /// exact llama-server launch arguments) is always queried fresh via
    /// /v1/health rather than cached alongside it - a low-frequency user
    /// click doesn't need that complexity, and a model can be loaded/
    /// unloaded between clicks.
    /// </summary>
    [RelayCommand]
    private async Task OpenModelDetailsAsync()
    {
        var modelId = SelectedModel;
        if (string.IsNullOrWhiteSpace(modelId)) return;

        var catalogEntry = _modelCatalog.GetValueOrDefault(modelId);

        var isCurrentlyLoaded = false;
        string? device = null;
        string? llamacppArgs = null;
        try
        {
            var health = await _managementClient.GetHealthAsync(CancellationToken.None);
            var loaded = health?.AllModelsLoaded.FirstOrDefault(m => string.Equals(m.ModelName, modelId, StringComparison.OrdinalIgnoreCase));
            if (loaded is not null)
            {
                isCurrentlyLoaded = true;
                device = loaded.Device;
                llamacppArgs = loaded.RecipeOptions?.LlamacppArgs;
            }
        }
        catch
        {
            // Best-effort - a failed health check just means the dialog
            // shows the not-loaded note instead of runtime detail, not a
            // blown-up dialog.
        }

        await ModelDetailsDialog.ShowAsync(
            modelId,
            catalogEntry?.Labels,
            catalogEntry?.MaxContextWindow ?? 0,
            catalogEntry?.SizeGb ?? 0,
            catalogEntry?.Recipe,
            isCurrentlyLoaded,
            device,
            llamacppArgs);
    }

    private bool CanSend() => !IsBusy && !string.IsNullOrWhiteSpace(InputText) && !string.IsNullOrWhiteSpace(SelectedModel) && AttachedImageVisionWarningText is null;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var userText = InputText.Trim();
        InputText = "";

        // Captured and cleared up front - a single-use attachment for this
        // one turn, not something that should silently carry over onto a
        // later message if it's still "queued" above the input box. An
        // image and a document never share the slot (see AttachFile), so
        // at most one of pendingFileText/pendingImagePath is non-null.
        var pendingFileName = AttachedFileName;
        var pendingFileText = _attachedFileText;
        var pendingImagePath = AttachedImagePath;
        AttachedFileName = null;
        _attachedFileText = null;
        AttachedImagePath = null;

        // What the MODEL sees (and what's persisted/sent to history)
        // includes the full extracted text; what the BUBBLE shows stays
        // just the typed message, with a small chip for the filename -
        // otherwise a multi-thousand-character PDF dump would flood the
        // visible transcript.
        var textForModel = userText;
        if (pendingFileText is not null)
        {
            textForModel = $"[Attached file: {pendingFileName}]{Environment.NewLine}{Environment.NewLine}{pendingFileText}{Environment.NewLine}{Environment.NewLine}{userText}";
        }
        else if (pendingImagePath is not null)
        {
            // Same marker convention as the document path, parsed back out
            // by ImageAttachmentService.TryParseAttachedImageMarker on
            // reload - kept as the message's own TextContent alongside the
            // real DataContent (not just userText) so that once an older
            // turn's image gets stripped (see StripOldImageContent below),
            // the remaining text still reads coherently instead of leaving
            // a bare, context-free question.
            textForModel = $"[Attached image: {pendingImagePath}]{Environment.NewLine}{Environment.NewLine}{userText}";
        }

        var isFirstMessage = Messages.Count == 0;

        var userBubble = new ChatMessageViewModel(isUser: true, initialText: userText) { AttachedFileName = pendingFileName, AttachedImagePath = pendingImagePath };
        Messages.Add(userBubble);
        SetDateDividerIfNeeded(userBubble);
        if (pendingImagePath is not null)
        {
            _history.Add(new ChatMessage(ChatRole.User, new List<AIContent> { new TextContent(textForModel), _imageAttachmentService.BuildDataContent(pendingImagePath) }));
        }
        else
        {
            _history.Add(new ChatMessage(ChatRole.User, textForModel));
        }
        _sessionRepository.SaveMessage(_currentSessionId, ChatRole.User.Value, textForModel, reasoningContent: null);

        if (isFirstMessage)
        {
            var title = userText.Length > 60 ? string.Concat(userText.AsSpan(0, 60), "...") : userText;
            _sessionRepository.RenameSession(_currentSessionId, title);
        }
        RefreshSessions();

        var assistantBubble = new ChatMessageViewModel(isUser: false, initialText: "") { IsStreaming = true, IsReasoningExpanded = _settings.Ui.ThinkingPanelOpenByDefault };
        Messages.Add(assistantBubble);
        SetDateDividerIfNeeded(assistantBubble);
        RightPanelToolCalls = assistantBubble.ToolCalls;

        // A real Stop button, threaded through GetStreamingResponseAsync, so a
        // runaway/stuck generation (e.g. Lemonade looping past 80,000+ tokens)
        // can always be cancelled.
        var cts = new CancellationTokenSource();
        _generationCts = cts;
        StopCommand.NotifyCanExecuteChanged();

        IsBusy = true;
        SendCommand.NotifyCanExecuteChanged();
        try
        {
            // Selecting an image-labeled model directly
            // skips the LLM entirely for this turn, matching how Lemonade's
            // own web UI behaves when an image model is active - avoids a
            // confusing HTTP 400 from picking an image model as the "chat"
            // model. Checked first, before any LLM-specific work (stable
            // prompt refresh, memory/knowledge retrieval), so none of that
            // gets wasted on a turn that was never going to use it. Still
            // returns through the same Finally below (IsBusy/cts cleanup) -
            // a return from inside this Try still runs it.
            if (_modelCategories.GetValueOrDefault(SelectedModel) == ModelCategoryImage)
            {
                await SendImageGenerationTurnAsync(userText, assistantBubble, cts.Token);
                return;
            }

            // _history[0] only ever holds the STABLE part of the system
            // prompt (RefreshStableSystemPrompt); this turn's relevant-
            // memories/knowledge-base search results are genuinely volatile
            // (different nearly every message) and go into
            // volatileContextText instead, folded into just this one
            // outgoing request by AppendVolatileContext - never _history,
            // never the database. See BuildStableSystemPromptText's own
            // comment for why this split matters (prompt caching).
            RefreshStableSystemPrompt();
            var relevantMemoriesText = await _memoryService.GetRelevantMemoriesTextAsync(userText, CancellationToken.None);

            // Skipped entirely when the module is off, not just discarded -
            // genuinely zero extra tokens/embedding calls spent, which is
            // the point of making Knowledge Bases a toggleable module.
            var knowledgeText = IsKnowledgeModuleEnabled
                ? await _knowledgeService.GetRelevantChunksTextAsync(SelectedKnowledgeBase?.Id, userText, CancellationToken.None)
                : "";

            // A local model has no other way to know the real current
            // date/time (its training data has a fixed cutoff) - matches
            // ScheduledJobRunner's own system prompt, which needed this fix
            // first after a job invented a plausible-but-wrong date for a
            // date-relative request. Volatile, not stable - the current
            // time changes on literally every turn, so it belongs here, not
            // in _history[0].
            var volatileContextSections = new List<string> { TimeAwareness.BuildTimeAwarenessText() };
            if (!string.IsNullOrEmpty(relevantMemoriesText)) volatileContextSections.Add(relevantMemoriesText);
            if (!string.IsNullOrEmpty(knowledgeText)) volatileContextSections.Add(knowledgeText);
            var volatileContextText = string.Join("\n\n", volatileContextSections);

            // A single stuck/looping completion otherwise has nothing
            // bounding it (Lemonade has been seen running a request past 80,000+
            // output tokens). ScheduledJobRunner's ChatOptions has the same cap.
            const int maxOutputTokensPerReply = 16384;
            var options = new ChatOptions
            {
                ModelId = SelectedModel,
                Tools = _moduleRegistry.GetEnabledTools(),
                MaxOutputTokens = maxOutputTokensPerReply,
                RawRepresentationFactory = BuildRawChatCompletionOptions,
            };
            var fullReply = new StringBuilder();
            var fullReasoning = new StringBuilder();
            var wasCancelled = false;

            try
            {
                // StripOldImageContent's result, not _history itself - the
                // real per-message DataContent stays in _history permanently
                // (LoadSession needs to be able to reconstruct it later),
                // only what's actually sent to the model drops every older
                // turn's image bytes. A token-saving
                // technique: an old image
                // costs real tokens for no benefit once it's not the current
                // turn. AppendVolatileContext folds this turn's time-
                // awareness/memories/knowledge text into just the outgoing
                // copy of the newest message - never _history itself.
                var outgoingMessages = AppendVolatileContext(StripOldImageContent(_history), volatileContextText);
                var hasStartedAnswering = false;
                await foreach (var update in _chatClient.GetStreamingResponseAsync(outgoingMessages, options, cts.Token))
                {
                    fullReply.Append(update.Text);
                    assistantBubble.Text = fullReply.ToString();

                    // Prompt-processing progress ("Processing
                    // prompt: 62% (ETA: 8s)"), read off llama.cpp's own
                    // prompt_progress streaming field - only meaningful
                    // before real generation starts (llama.cpp stops sending
                    // it the moment token generation begins), so this only
                    // actually shows anything for genuinely large prompts.
                    // See BuildRawChatCompletionOptions for the request-side
                    // half (return_progress). The deserializer stores an
                    // unrecognized top-level property as ONE opaque JSON
                    // blob at its own path ("$.prompt_progress") rather than
                    // breaking it into individually addressable sub-paths -
                    // querying a nested path like "$.prompt_progress.processed"
                    // directly would silently never match anything, so
                    // Contains/GetJson has to target the parent path only,
                    // then the blob gets parsed normally afterward.
                    if (!hasStartedAnswering && update.RawRepresentation is OpenAIStreamingChatCompletionUpdate nativeUpdate)
                    {
#pragma warning disable SCME0001
                        if (nativeUpdate.Patch.Contains("$.prompt_progress"u8))
                        {
                            var progressJson = nativeUpdate.Patch.GetJson("$.prompt_progress"u8);
#pragma warning restore SCME0001
                            using var progressDoc = JsonDocument.Parse(progressJson.ToMemory());
                            var progressRoot = progressDoc.RootElement;
                            var processed = progressRoot.GetProperty("processed").GetInt32();
                            var total = progressRoot.GetProperty("total").GetInt32();
                            var cache = progressRoot.GetProperty("cache").GetInt32();
                            var timeMs = progressRoot.GetProperty("time_ms").GetDouble();

                            // Cached (KV-cache-reused) prefix tokens are
                            // excluded from both processed and total, since
                            // those tokens never actually needed
                            // re-processing.
                            var actualProcessed = processed - cache;
                            var actualTotal = total - cache;
                            if (actualTotal > 0)
                            {
                                var percent = (int)Math.Round(100.0 * actualProcessed / actualTotal);
                                var etaText = "";
                                if (actualProcessed > 0 && timeMs >= 500)
                                {
                                    var etaSecs = (timeMs / 1000.0) * ((double)actualTotal / actualProcessed - 1.0);
                                    etaText = $" (ETA: {Math.Max(0, (int)Math.Round(etaSecs))}s)";
                                }
                                assistantBubble.PromptProgressText = $"Processing prompt: {percent}%{etaText}";
                            }
                        }
                    }

                    // The first real answer token marks the end of prompt
                    // processing (and "thinking", if the model reasons
                    // first) - matches the real WPF app's own
                    // hasStartedAnswering convention.
                    if (!hasStartedAnswering && !string.IsNullOrEmpty(update.Text))
                    {
                        hasStartedAnswering = true;
                        assistantBubble.PromptProgressText = null;
                    }

                    foreach (var content in update.Contents)
                    {
                        if (content is TextReasoningContent reasoning)
                        {
                            fullReasoning.Append(reasoning.Text);
                            assistantBubble.ReasoningText = fullReasoning.ToString();

                            // Reasoning tokens don't flip hasStartedAnswering
                            // (that's reserved for real answer text - see
                            // above), but they're still real generation, so
                            // prompt processing is just as over as if the
                            // answer itself had started.
                            assistantBubble.PromptProgressText = null;
                        }

                        // UseFunctionInvocation() (see LemonadeChatClientFactory)
                        // handles actually calling the tool automatically, but
                        // still surfaces a FunctionCallContent (the call) and a
                        // matching FunctionResultContent (the real outcome,
                        // correlated by CallId) in the stream - that's the hook
                        // used here, not a separate tracking mechanism.
                        // Succeeded starts null (see ToolCallResult's own
                        // comment) rather than defaulting to true, since an
                        // always-true checkmark before the real result exists
                        // would be misleading, not just wrong once a result
                        // eventually arrives.
                        else if (content is FunctionCallContent functionCall && !assistantBubble.ToolCalls.Any(t => t.CallId == functionCall.CallId))
                        {
                            assistantBubble.ToolCalls.Add(new ToolCallResult { Name = functionCall.Name, CallId = functionCall.CallId });
                        }
                        else if (content is FunctionResultContent functionResult)
                        {
                            var matchingCall = assistantBubble.ToolCalls.FirstOrDefault(t => t.CallId == functionResult.CallId);
                            if (matchingCall is not null)
                            {
                                matchingCall.Succeeded = functionResult.Exception is null;
                                matchingCall.ErrorMessage = functionResult.Exception?.Message;
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // Keeps whatever text/reasoning had already streamed in
                // rather than discarding it - a partial answer the user
                // watched arrive is still worth keeping.
                wasCancelled = true;
                if (fullReply.Length > 0) fullReply.Append(' ');
                fullReply.Append("[Stopped]");
                assistantBubble.Text = fullReply.ToString();
            }

            // Covers the edge case where the stream ends (cancelled, or a
            // genuine error) while still mid-prompt-processing - without
            // this, a stuck "Processing prompt: 43%" would linger on the
            // bubble forever, since the two clear points above only fire
            // once real generation actually starts.
            assistantBubble.PromptProgressText = null;

            var replyText = fullReply.ToString();
            assistantBubble.GeneratedImagePath = TryExtractGeneratedImagePath(replyText);
            _history.Add(new ChatMessage(ChatRole.Assistant, replyText));
            _sessionRepository.SaveMessage(_currentSessionId, ChatRole.Assistant.Value, replyText,
                reasoningContent: fullReasoning.Length > 0 ? fullReasoning.ToString() : null,
                toolCalls: assistantBubble.ToolCalls.Count > 0 ? ToolCallResult.SerializeForStorage(assistantBubble.ToolCalls) : null);
            RefreshSessions();

            // Auto-tagging - marks a deliberate, notable action worth being
            // able to find later (unlike e.g. web search, which would fire
            // in a large fraction of ordinary chats and make the tag
            // meaningless as a filter). AddTagToSession is idempotent
            // (INSERT OR IGNORE), so tagging again on a later turn that
            // happens to call the same tool is harmless, not a growing
            // duplicate. Each RefreshSessions() call is needed, not just
            // decorative - the sidebar's tag chip otherwise wouldn't show
            // until some unrelated later refresh (e.g. switching chats),
            // since a live ChatSessionSummary's own Tags collection is only
            // populated once, when it was first loaded, and adding a tag to
            // the DB doesn't retroactively update an object already sitting
            // in Sessions/SidebarItems.
            if (assistantBubble.ToolCalls.Any(t => t.Name == "generate_image"))
            {
                _sessionRepository.AddTagToSession(_currentSessionId, "image");
                RefreshSessions();
            }
            if (assistantBubble.ToolCalls.Any(t => t.Name.StartsWith("code_")))
            {
                _sessionRepository.AddTagToSession(_currentSessionId, "coder");
                RefreshSessions();
            }
            if (assistantBubble.ToolCalls.Any(t => _moduleRegistry.IsToolFromModule(t.Name, "Mcp")))
            {
                _sessionRepository.AddTagToSession(_currentSessionId, "mcp");
                RefreshSessions();
            }

            // Stats/fact-extraction/compaction are all skipped on a
            // cancelled reply, same as the original error-path exclusion -
            // there's nothing meaningful to measure or learn from a
            // deliberately-interrupted turn.
            if (!wasCancelled)
            {
                try
                {
                    var stats = await _managementClient.GetStatsAsync(CancellationToken.None);
                    if (stats is not null)
                    {
                        StatsText = $"Tokens in/out: {stats.PromptTokens}/{stats.OutputTokens} · {stats.TokensPerSecond:0.0} tok/s · TTFT {stats.TimeToFirstToken:0.00}s";
                        ContextUsageCurrent = stats.PromptTokens;
                        ContextUsageText = FormatContextUsageText();

                        // Right panel's Stats tab - "Last reply" (server-
                        // measured, per-request) plus a running "Session"
                        // total this app accumulates itself turn-by-turn
                        // (never persisted - resets with NewChat/LoadSession,
                        // same as the real WPF app's own ResetSessionStats).
                        LastReplyInputTokens = stats.InputTokens;
                        LastReplyOutputTokens = stats.OutputTokens;
                        LastReplyTokensPerSecond = stats.TokensPerSecond;
                        LastReplyTimeToFirstToken = stats.TimeToFirstToken;
                        SessionTotalInputTokens += stats.InputTokens;
                        SessionTotalOutputTokens += stats.OutputTokens;
                    }
                }
                catch
                {
                    // Best-effort - a failed stats fetch shouldn't blow up a reply the user already received.
                }

                // Fire-and-forget, not awaited - a second full model round-trip,
                // no reason to make the user wait for it before sending again.
                ExtractFactsFireAndForget(userText, replyText);

                // Awaited, not fire-and-forget - this mutates _history directly,
                // so it can't safely run in the background past IsBusy going
                // false.
                using var compactionTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await CompactHistoryIfNeededAsync(assistantBubble, compactionTimeoutCts.Token);
            }
        }
        catch (Exception ex)
        {
            assistantBubble.Text = $"⚠️ Something went wrong generating a reply: {ex.Message}";
            StatusText = "Error - see reply above";
        }
        finally
        {
            assistantBubble.IsStreaming = false;
            _generationCts = null;
            IsBusy = false;
            SendCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanStop() => _generationCts is not null;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _generationCts?.Cancel();

    /// <summary>
    /// The direct-image-model-selection path, sharing
    /// ImageGenerationService with the generate_image tool (ImageGenModule)
    /// rather than duplicating the request-building/response-parsing logic.
    /// Reuses the assistantBubble SendAsync already created (IsStreaming
    /// true at that point) instead of creating a second one - there's
    /// nothing to stream here, so IsStreaming flips false once this
    /// finishes, same as any other completed reply, letting Markdown.
    /// Avalonia render the image inline from the Markdown link in Text.
    /// </summary>
    private async Task SendImageGenerationTurnAsync(string prompt, ChatMessageViewModel assistantBubble, CancellationToken cancellationToken)
    {
        var sizeChoice = await ImageSizeDialog.ShowAsync(prompt, _settings.ImageGen.DefaultWidth, _settings.ImageGen.DefaultHeight);
        if (!sizeChoice.Confirmed)
        {
            assistantBubble.Text = "(Cancelled - no image was generated.)";
        }
        else
        {
            try
            {
                // SelectedModel, not a Settings-configured default - the
                // whole point of this path is generating with the model the
                // user actually picked in the dropdown.
                var imageUri = await _imageGenerationService.GenerateAsync(
                    prompt, SelectedModel, sizeChoice.Width, sizeChoice.Height,
                    _settings.ImageGen.Steps, _settings.ImageGen.CfgScale, _settings.ImageGen.Seed,
                    cancellationToken);

                var altText = prompt.Length > 80 ? prompt[..80] + "..." : prompt;
                assistantBubble.Text = $"![{altText}]({imageUri})";
                assistantBubble.GeneratedImagePath = new Uri(imageUri).LocalPath;

                // Auto-tagging - see SendAsync's matching hook for the
                // tool-based path. RefreshSessions() right after, for the
                // same reason that one needs it too (a live ChatSessionSummary's
                // own Tags collection is only populated once, when first
                // loaded).
                _sessionRepository.AddTagToSession(_currentSessionId, "image");
                RefreshSessions();
            }
            catch (OperationCanceledException)
            {
                assistantBubble.Text = "[Stopped]";
            }
            catch (Exception ex)
            {
                assistantBubble.Text = $"⚠️ Couldn't generate that image: {ex.Message}";
            }
        }

        // IsStreaming flips false in SendAsync's own shared Finally, not
        // here - this method returns straight back into that Try/Finally.
        _history.Add(new ChatMessage(ChatRole.Assistant, assistantBubble.Text));
        _sessionRepository.SaveMessage(_currentSessionId, ChatRole.Assistant.Value, assistantBubble.Text, reasoningContent: null);
    }

    /// <summary>
    /// Sets _history[0] to the current stable system prompt - safe to call
    /// whenever _history is empty (NewChat/LoadSession/right after
    /// CompactHistoryIfNeededAsync's Clear()) or already has a system
    /// message at index 0 (every other call site, i.e. SendAsync). Called at
    /// the start of every turn too (cheap - a local DB read of pinned
    /// facts, no network calls), so a newly-pinned fact shows up
    /// immediately rather than needing the app restarted - this doesn't
    /// hurt prompt caching, since the *content* only actually changes when
    /// pinned facts or the summary really do, not on every single message.
    /// </summary>
    private void RefreshStableSystemPrompt()
    {
        var systemMessage = new ChatMessage(ChatRole.System, BuildStableSystemPromptText());
        if (_history.Count == 0) _history.Add(systemMessage);
        else _history[0] = systemMessage;
    }

    /// <summary>
    /// Base system prompt + conversation summary + pinned facts only -
    /// deliberately NOT this turn's relevant-memories/knowledge-base search
    /// results (see AppendVolatileContext for where those go instead and
    /// why). Everything here is genuinely stable turn-to-turn (only changes
    /// when the summary updates via compaction, or the user pins/unpins a
    /// memory). llama.cpp's KV-cache reuse matches a common *prefix*, so a
    /// system message (position 0) that changed every turn would mean
    /// nothing could ever be cached, even though the actual conversation
    /// history itself was unchanged - hence keeping per-turn search results
    /// out of it entirely.
    /// </summary>
    private string BuildStableSystemPromptText()
    {
        var sections = new List<string>();

        // Persona: Identity/AboutUser come before the operating
        // instructions (SystemPrompt) - "who am I / who are you talking
        // to" reads naturally before "how should you behave". Both are
        // omitted entirely when unset, same as every other section here,
        // so an unconfigured Persona costs nothing in prompt size.
        var persona = _settings.Persona;
        if (!string.IsNullOrWhiteSpace(persona.Identity)) sections.Add(persona.Identity);
        if (!string.IsNullOrWhiteSpace(persona.AboutUser)) sections.Add($"About the person you're talking to:\n{persona.AboutUser}");

        sections.Add(_settings.Assistant.SystemPrompt);

        var writingStyleText = BuildWritingStyleText();
        if (!string.IsNullOrEmpty(writingStyleText)) sections.Add(writingStyleText);

        if (!string.IsNullOrEmpty(_conversationSummaryText))
        {
            sections.Add($"Summary of earlier parts of this conversation (older messages were trimmed to save context space):\n{_conversationSummaryText}");
        }

        var pinnedFactsText = _memoryService.GetPinnedFactsText();
        if (!string.IsNullOrEmpty(pinnedFactsText)) sections.Add(pinnedFactsText);

        return string.Join("\n\n", sections);
    }

    /// <summary>
    /// Compiles Persona's structured writing-style settings into compact
    /// bullet lines - cheap (a handful of tokens) but has an outsized
    /// effect on how the reply actually reads. Returns null (not an empty
    /// string) when every setting is still "Default" and no custom
    /// instructions are set, so BuildStableSystemPromptText's IsNullOrEmpty
    /// check correctly omits this section entirely for an unconfigured
    /// Persona.
    /// </summary>
    private string? BuildWritingStyleText()
    {
        var persona = _settings.Persona;
        var lines = new List<string>();

        if (persona.Tone != "Default") lines.Add($"Tone: {persona.Tone}");
        if (persona.Verbosity != "Default") lines.Add($"Verbosity: {persona.Verbosity}");
        if (persona.EmojiUsage != "Default") lines.Add($"Emoji use: {persona.EmojiUsage}");
        if (!string.IsNullOrWhiteSpace(persona.CustomInstructions)) lines.Add(persona.CustomInstructions);

        if (lines.Count == 0) return null;

        return "Writing style:\n- " + string.Join("\n- ", lines);
    }

    /// <summary>
    /// Sets return_progress on the outgoing request via
    /// ChatCompletionOptions.Patch - return_progress isn't part of the
    /// OpenAI schema at all, it's a llama.cpp/Lemonade-specific field;
    /// adding it turns on periodic prompt_progress chunks during the
    /// streaming response (see the streaming loop above for the response-
    /// side half). The SDK's own source (OpenAIChatClient.ToOpenAIOptions)
    /// uses this exact JsonPatch mechanism internally for ModelId, and
    /// merges Tools/ModelId/Temperature/etc onto whatever this factory
    /// returns afterward (via ??=), so a near-empty options object here
    /// loses nothing else. SCME0001 is JsonPatch's own experimental
    /// diagnostic ID, not a real risk - the SDK suppresses the exact same
    /// warning internally for the exact same feature.
    /// </summary>
    private static object BuildRawChatCompletionOptions(IChatClient client)
    {
        var native = new OpenAIChatCompletionOptions();
#pragma warning disable SCME0001
        native.Patch.Set("$.return_progress"u8, true);
#pragma warning restore SCME0001
        return native;
    }

    /// <summary>
    /// This turn's relevant-memories/knowledge-base search results get
    /// folded into a COPY of the newest message only, for this one outgoing
    /// request - never _history itself (which would resend stale search
    /// results on every future turn forever) and never the database
    /// (SaveMessage already only ever persisted textForModel, which never
    /// included this). Prepended before the message's own existing content
    /// (grounding context first, the actual question last), not appended as
    /// a separate trailing message - appending would need a turn-order
    /// spacer pass to avoid two same-role messages in a row; folding into
    /// the existing message's Contents sidesteps that problem entirely
    /// since no new message is added.
    /// </summary>
    private static List<ChatMessage> AppendVolatileContext(List<ChatMessage> messages, string volatileContextText)
    {
        if (string.IsNullOrEmpty(volatileContextText) || messages.Count == 0) return messages;

        var lastMessage = messages[^1];
        var augmentedContents = new List<AIContent> { new TextContent(volatileContextText) };
        augmentedContents.AddRange(lastMessage.Contents);

        var result = new List<ChatMessage>(messages);
        result[result.Count - 1] = new ChatMessage(lastMessage.Role, augmentedContents);
        return result;
    }

    private async void ExtractFactsFireAndForget(string userText, string assistantReply)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await _memoryService.ExtractAndSaveFactsAsync(userText, assistantReply, timeoutCts.Token);
    }

    /// <summary>
    /// Hand-rolled short-term memory / context compaction - once context
    /// usage crosses CompactionTriggerPercent of the model's real max
    /// context window, summarizes everything older than the most-recent
    /// MessagesToKeepAfterCompaction messages into a running summary and
    /// drops them from _history (the visible Messages/persisted transcript
    /// are untouched - only what's sent to the model shrinks).
    /// </summary>
    private async Task CompactHistoryIfNeededAsync(ChatMessageViewModel assistantBubble, CancellationToken cancellationToken)
    {
        if (ContextUsageMax <= 0) return;
        if ((double)ContextUsageCurrent / ContextUsageMax < _settings.Memory.CompactionTriggerPercent / 100.0) return;

        var compactibleCount = _history.Count - 1 - MessagesToKeepAfterCompaction;
        if (compactibleCount <= 0) return;

        // Shown BEFORE the summarization call below, which is itself a full
        // model round-trip and can take a while - without this, once
        // streaming has already finished, the user was left looking at
        // nothing with no sign anything was still happening. Removed again
        // in both the success and failure paths below, never left showing
        // once this method returns.
        const string compactingNote = "Compacting older messages to save context space...";
        assistantBubble.SystemNotes.Add(compactingNote);

        try
        {
            var currentTokens = ContextUsageCurrent;

            var itemsToSummarize = new List<ChatMessage>();
            if (!string.IsNullOrEmpty(_conversationSummaryText))
            {
                itemsToSummarize.Add(new ChatMessage(ChatRole.System, $"Summary of even earlier conversation: {_conversationSummaryText}"));
            }
            itemsToSummarize.AddRange(_history.Skip(1).Take(compactibleCount));

            _conversationSummaryText = await _compactionService.SummarizeAsync(itemsToSummarize, cancellationToken);

            var keptTail = _history.Skip(_history.Count - MessagesToKeepAfterCompaction).ToList();
            _history.Clear();
            // _conversationSummaryText was already updated above, so this
            // correctly includes the new summary.
            RefreshStableSystemPrompt();
            _history.AddRange(keptTail);

            // A rough ~4-chars-per-token estimate, marked as approximate -
            // ContextUsageCurrent otherwise stays at its last real
            // Lemonade-reported figure until the *next* turn's own stats
            // come back, which would read as "compaction did nothing" since
            // the number wouldn't move.
            var estimatedChars = _history.Sum(m => (m.Text ?? "").Length);
            var estimatedTokens = Math.Max(1, estimatedChars / 4);
            ContextUsageCurrent = estimatedTokens;
            ContextUsageText = FormatContextUsageText(isEstimate: true);

            // Visible in the chat itself (see SystemNotes) rather than the
            // header's StatusText - that slot is too narrow for a sentence
            // like this, and doing this with no visible indicator at all
            // would leave no way to tell it had happened.
            var tokensSaved = Math.Max(0, currentTokens - estimatedTokens);
            _compactionCount++;
            assistantBubble.SystemNotes.Remove(compactingNote);
            assistantBubble.SystemNotes.Add($"Compacted older messages to save context space · saved ~{tokensSaved:N0} tokens");
        }
        catch
        {
            // Best-effort - a failed compaction attempt just means the next
            // turn's request stays as large as it already was.
            assistantBubble.SystemNotes.Remove(compactingNote);
        }
    }

    private string FormatContextUsageText(bool isEstimate = false)
    {
        if (ContextUsageMax <= 0) return "";
        var percent = (int)Math.Round(100.0 * ContextUsageCurrent / ContextUsageMax);
        var prefix = isEstimate ? "~" : "";
        return $"{prefix}{ContextUsageCurrent:N0} / {ContextUsageMax:N0} tokens ({percent}%)";
    }

    /// <summary>Rough ~4-characters-per-token English approximation - only ever used as a stand-in until a real Lemonade-reported number is available.</summary>
    private static int EstimateTokenCount(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return Math.Max(1, text.Length / 4);
    }

    /// <summary>Real count via Lemonade's own tokenizer, falling back to the chars/4 guess if the call fails (older Lemonade version, transient network issue) - shared by every "view X (~N tokens)" caller rather than duplicating the same try/catch more than once.</summary>
    private async Task<int> TryTokenizeAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            return await _managementClient.TokenizeAsync(text, cancellationToken);
        }
        catch
        {
            return EstimateTokenCount(text);
        }
    }

    /// <summary>
    /// The literal text tokenized for the "tools" figure in the context
    /// breakdown popup - one shared builder so what's tokenized can never
    /// drift from what the tool list actually is. Builds the real OpenAI
    /// wire-format tools array (the wrapper syntax itself costs real
    /// tokens a bare name/description concatenation would miss), not just
    /// a schema dump - still can't capture whatever tool-use preamble the
    /// chat template might inject once when tools are present (no API
    /// exists to render that without a real generation), so
    /// ToolUsePreambleOverheadTokens below adds a separately-measured
    /// estimate for that on top.
    /// </summary>
    private string BuildRawToolsText()
    {
        var wireTools = _moduleRegistry.GetEnabledTools().Select(t =>
        {
            var declaration = t as AIFunctionDeclaration;
            object parameters = declaration is not null ? declaration.JsonSchema : JsonDocument.Parse("{}").RootElement;
            return new { type = "function", function = new { name = t.Name, description = t.Description, parameters } };
        }).ToList();
        return JsonSerializer.Serialize(wireTools);
    }

    /// <summary>
    /// A roughly fixed per-request token cost Qwen's chat template adds
    /// once, server-side, whenever tools are present in a request -
    /// measured (tools-enabled vs. tools-disabled as a direct control, three
    /// consecutive turns of a fresh conversation) rather than guessed.
    /// Worth recalibrating if this app's typical active tool count changes
    /// drastically from what this was measured against.
    /// </summary>
    private const int ToolUsePreambleOverheadTokens = 2200;

    /// <summary>
    /// Raw counts (via Lemonade's own real tokenizer where possible) for
    /// what's already committed to context right now: the stable system
    /// prompt, every enabled tool's schema, and _history. Falls back to a
    /// chars/4 approximation if the tokenize call fails.
    /// </summary>
    private async Task<(int SystemPrompt, int Tools, int History, bool UsedRealTokenizer)> EstimateCommittedContextTokensAsync(CancellationToken cancellationToken)
    {
        var systemPromptText = _history.Count > 0 ? _history[0].Text ?? "" : "";

        var toolsText = BuildRawToolsText();
        var toolUseOverhead = _moduleRegistry.GetEnabledTools().Count > 0 ? ToolUsePreambleOverheadTokens : 0;

        var historyTextBuilder = new StringBuilder();
        for (var i = 1; i < _history.Count; i++)
        {
            historyTextBuilder.AppendLine(ExtractMessageText(_history[i]));
        }
        var historyText = historyTextBuilder.ToString();

        try
        {
            var systemPromptTokens = await _managementClient.TokenizeAsync(systemPromptText, cancellationToken);
            var toolsTokens = await _managementClient.TokenizeAsync(toolsText, cancellationToken);
            var historyTokens = await _managementClient.TokenizeAsync(historyText, cancellationToken);
            return (systemPromptTokens, toolsTokens + toolUseOverhead, historyTokens, true);
        }
        catch
        {
            const int charsPerToken = 4;
            return (systemPromptText.Length / charsPerToken, toolsText.Length / charsPerToken + toolUseOverhead, historyText.Length / charsPerToken, false);
        }
    }

    /// <summary>
    /// ChatMessage.Text only concatenates TextContent - any message with a
    /// real tool call in it (a FunctionCallContent for the call, a
    /// FunctionResultContent for its result) contributes its arguments/
    /// result content to the real prompt Lemonade tokenizes, but nothing to
    /// a .Text-only read, so this walks Contents directly instead.
    /// </summary>
    private static string ExtractMessageText(ChatMessage message)
    {
        var sb = new StringBuilder();
        foreach (var content in message.Contents)
        {
            if (content is TextContent textContent)
            {
                sb.Append(textContent.Text);
            }
            else if (content is FunctionCallContent functionCall)
            {
                sb.Append(functionCall.Name);
                if (functionCall.Arguments is not null)
                {
                    sb.Append(JsonSerializer.Serialize(functionCall.Arguments));
                }
            }
            else if (content is FunctionResultContent functionResult && functionResult.Result is not null)
            {
                sb.Append(JsonSerializer.Serialize(functionResult.Result));
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// The genuine volatile per-turn content (time-awareness + whatever
    /// memory/knowledge search surfaces) for the given draft text - shared
    /// by OpenTurnContextAsync (a full preview dialog) and
    /// RefreshContextBreakdownAsync's PendingTokens (just needs the token
    /// cost), so the exact same real search only needs writing once.
    /// Genuinely empty (not a placeholder note - that's each caller's own
    /// UI concern) when draftText is blank, since neither search has real
    /// text to search against.
    /// </summary>
    private async Task<string> BuildLiveTurnContextTextAsync(string draftText, CancellationToken cancellationToken)
    {
        var sections = new List<string> { TimeAwareness.BuildTimeAwarenessText() };

        if (!string.IsNullOrWhiteSpace(draftText))
        {
            var relevantMemoriesText = await _memoryService.GetRelevantMemoriesTextAsync(draftText, cancellationToken);
            if (!string.IsNullOrEmpty(relevantMemoriesText)) sections.Add(relevantMemoriesText);

            if (IsKnowledgeModuleEnabled)
            {
                var relevantKnowledgeText = await _knowledgeService.GetRelevantChunksTextAsync(SelectedKnowledgeBase?.Id, draftText, cancellationToken);
                if (!string.IsNullOrEmpty(relevantKnowledgeText)) sections.Add(relevantKnowledgeText);
            }
        }

        return string.Join("\n\n", sections);
    }

    /// <summary>
    /// Shows the VOLATILE per-turn context - time-awareness plus whatever
    /// memory/knowledge search would surface for the text currently
    /// sitting in the message box, via BuildLiveTurnContextTextAsync - a
    /// genuine preview, not a guess. Memory/knowledge both need real text
    /// to search against, so with an empty message box only the time line
    /// is real content, with an explanation appended for display rather
    /// than silently omitting the other sections.
    /// </summary>
    [RelayCommand]
    private async Task OpenTurnContextAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var turnContextText = await BuildLiveTurnContextTextAsync(InputText, cts.Token);
        var tokenCount = await TryTokenizeAsync(turnContextText, cts.Token);

        var displayText = turnContextText;
        if (string.IsNullOrWhiteSpace(InputText))
        {
            displayText += "\n\n(Type something in the message box first to preview what relevant memories/knowledge would be included for it - both need real text to search against.)";
        }

        await SystemPromptDialog.ShowAsync(displayText, title: "Turn context (preview)",
            description: $"What gets silently prepended to your NEXT message when you hit Send - never shown in the chat, never in View system prompt. Recomputed fresh every turn, so this is a live preview based on the message box's current text, not a historical record. (~{tokenCount:N0} tokens)");
    }

    /// <summary>
    /// Computes a fresh ContextBreakdown snapshot right now - called from
    /// MainWindow's context-usage-text hover handler. Async since
    /// EstimateCommittedContextTokensAsync calls Lemonade's real POST
    /// /v1/tokenize rather than guessing - IsContextBreakdownLoading guards
    /// against overlapping calls if the mouse re-enters before a previous
    /// refresh finished. The composition (system prompt/tools/history)
    /// reflects what's genuinely already in context; PendingTokens is a
    /// live preview of what sending right now would ADD - the live turn-
    /// context/message-box cost is cached and only recomputed when
    /// InputText (or the attached knowledge base) actually changes, not on
    /// every hover, since it involves real memory/knowledge embedding
    /// searches that are wasted work when nothing's changed since the last
    /// one.
    /// </summary>
    public async Task RefreshContextBreakdownAsync()
    {
        if (IsContextBreakdownLoading) return;
        IsContextBreakdownLoading = true;
        try
        {
            var committed = await EstimateCommittedContextTokensAsync(CancellationToken.None);
            var systemPromptTokens = committed.SystemPrompt;
            var toolsTokens = committed.Tools;
            var historyTokens = committed.History;
            var toolsActiveCount = _moduleRegistry.GetEnabledTools().Count;

            var percentFull = ContextUsageMax > 0 ? (int)Math.Round(100.0 * ContextUsageCurrent / ContextUsageMax) : 0;

            // Widths sized against ContextUsageMax directly (the real
            // window size), NOT rescaled to force these three parts to sum
            // exactly to ContextUsageCurrent - deliberately: even with the
            // real tokenizer, this can't see chat-template rendering
            // overhead (per-message role markers, the tool-calling JSON
            // wrapper) that Lemonade's own real total does include, so a
            // small honest gap between "sum of parts" and "the one true
            // total" is expected and left as unlabeled free space rather
            // than force-summed with another estimated number.
            var widthPerToken = ContextUsageMax > 0 ? ContextBreakdownBarWidth / ContextUsageMax : 0;
            var systemPromptWidth = Math.Min(systemPromptTokens * widthPerToken, ContextBreakdownBarWidth);
            var toolsWidth = Math.Min(toolsTokens * widthPerToken, ContextBreakdownBarWidth - systemPromptWidth);
            var historyWidth = Math.Min(historyTokens * widthPerToken, ContextBreakdownBarWidth - systemPromptWidth - toolsWidth);
            var freeWidth = Math.Max(ContextBreakdownBarWidth - systemPromptWidth - toolsWidth - historyWidth, 0);

            // PendingTokens: the real, tokenized cost of what would
            // actually be added on top of the total above if Send were hit
            // right now - the live turn-context preview (time-awareness/
            // memory/knowledge for the message box's CURRENT text, the
            // exact same computation OpenTurnContextAsync's own dialog
            // uses, not stale leftover data from the last send) plus the
            // message box's own text. An addition on top of the one real
            // total, never a second competing total of its own. Cached
            // against the exact InputText/knowledge-base it was computed
            // for - re-running the real memory/knowledge embedding searches
            // on every hover regardless of whether anything changed is
            // real, wasted load on Lemonade.
            int pendingTokens;
            var selectedKbId = SelectedKnowledgeBase?.Id;
            if (InputText == _lastPendingPreviewInputText && selectedKbId == _lastPendingPreviewKnowledgeBaseId)
            {
                pendingTokens = _lastPendingPreviewTokens;
            }
            else
            {
                var turnContextText = await BuildLiveTurnContextTextAsync(InputText, CancellationToken.None);
                var pendingText = (string.IsNullOrEmpty(turnContextText) ? "" : turnContextText + "\n") + InputText;
                pendingTokens = string.IsNullOrWhiteSpace(pendingText) ? 0 : await TryTokenizeAsync(pendingText, CancellationToken.None);
                _lastPendingPreviewInputText = InputText;
                _lastPendingPreviewKnowledgeBaseId = selectedKbId;
                _lastPendingPreviewTokens = pendingTokens;
            }

            ContextBreakdownInfo = new ContextBreakdown
            {
                ContextNowTokens = ContextUsageCurrent,
                PercentFull = percentFull,
                UsedRealTokenizer = committed.UsedRealTokenizer,
                SystemPromptTokens = systemPromptTokens,
                SystemPromptWidth = systemPromptWidth,
                ToolsActiveCount = toolsActiveCount,
                ToolsTokens = toolsTokens,
                ToolsWidth = toolsWidth,
                HistoryTokens = historyTokens,
                HistoryWidth = historyWidth,
                FreeWidth = freeWidth,
                CompactionThresholdLeft = ContextBreakdownBarWidth * (_settings.Memory.CompactionTriggerPercent / 100.0),
                CompactionCount = _compactionCount,
                HasCompactions = _compactionCount > 0,
                PendingTokens = pendingTokens,
                WouldExceedLimit = ContextUsageMax > 0 && ContextUsageCurrent + pendingTokens >= ContextUsageMax,
            };
        }
        finally
        {
            IsContextBreakdownLoading = false;
        }
    }

    // Save/copy-image follow-up - pulls a generated image's local path out
    // of the assistant's own reply text (ImageGenModule's tool result tells
    // the model to include real Markdown image syntax, ![alt](file:///...))
    // so the Save/Copy buttons have a real local file to work with -
    // Markdown.Avalonia's own inline rendering doesn't expose that path
    // back out, only the visual result.
    private static readonly Regex GeneratedImageLinkPattern = new(@"!\[[^\]]*\]\((file:///?[^)\s]+)\)", RegexOptions.Compiled);

    private static string? TryExtractGeneratedImagePath(string replyText)
    {
        var match = GeneratedImageLinkPattern.Match(replyText);
        if (!match.Success) return null;

        try
        {
            return new Uri(match.Groups[1].Value).LocalPath;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    partial void OnInputTextChanged(string value) => SendCommand.NotifyCanExecuteChanged();

    partial void OnAttachedImagePathChanged(string? value) => SendCommand.NotifyCanExecuteChanged();

    /// <summary>
    /// Non-mutating - returns a new list, leaving the real _history
    /// untouched. Finds the single most recent message carrying DataContent
    /// and drops the DataContent (keeping its TextContent marker) from every
    /// other message that has any, so an old image doesn't cost real tokens
    /// on every later turn for no benefit once it's not the current one.
    /// </summary>
    private static List<ChatMessage> StripOldImageContent(List<ChatMessage> history)
    {
        var lastImageIndex = -1;
        for (var i = 0; i < history.Count; i++)
        {
            if (history[i].Contents.Any(c => c is DataContent)) lastImageIndex = i;
        }

        if (lastImageIndex < 0) return history;

        var result = new List<ChatMessage>(history.Count);
        for (var i = 0; i < history.Count; i++)
        {
            if (i == lastImageIndex || !history[i].Contents.Any(c => c is DataContent))
            {
                result.Add(history[i]);
            }
            else
            {
                var textOnly = history[i].Contents.Where(c => c is not DataContent).ToList();
                result.Add(new ChatMessage(history[i].Role, textOnly));
            }
        }
        return result;
    }
}
