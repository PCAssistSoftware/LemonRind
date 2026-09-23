Imports System.Collections.ObjectModel
Imports System.Diagnostics
Imports System.Threading
Imports System.Windows
Imports System.Windows.Threading
Imports CommunityToolkit.Mvvm.ComponentModel
Imports CommunityToolkit.Mvvm.Input
Imports LemonRind.Configuration
Imports LemonRind.Knowledge
Imports LemonRind.Mcp
Imports LemonRind.Memories
Imports LemonRind.Modules
Imports LemonRind.Scheduler
Imports LemonRind.Services

Namespace ViewModels

    ''' <summary>
    ''' One module's row in the Settings screen's toggle list. Same
    ''' OnIsEnabledChanged callback pattern as McpServerViewModel/
    ''' ScheduledJobViewModel below - here it's not persisting anything
    ''' itself (that's still just the checkbox's own TwoWay binding into
    ''' Modules.Enabled, applied on Save), it's what lets SettingsViewModel
    ''' keep its per-module "is this module currently on" display
    ''' properties (IsImageGenModuleEnabled etc.) in sync live as the
    ''' checkbox is toggled, for the module-disabled banners on each
    ''' module's own Settings section.
    ''' </summary>
    Public Class ModuleToggleItem
        Public Property Name As String
        Public Property Description As String
        Public Property ConfigKey As String

        Private _isEnabled As Boolean
        Public Property IsEnabled As Boolean
            Get
                Return _isEnabled
            End Get
            Set(value As Boolean)
                If _isEnabled <> value Then
                    _isEnabled = value
                    OnIsEnabledChanged?.Invoke(value)
                End If
            End Set
        End Property

        Public Property OnIsEnabledChanged As Action(Of Boolean)
    End Class

    ''' <summary>
    ''' One stored memory's row in the Memories review screen. A separate
    ''' observable wrapper (not MemoryItem itself) because Content needs to
    ''' be two-way-bindable and edited in place before the user clicks Save -
    ''' MemoryItem is a plain data-access DTO, not meant to carry UI edit state.
    ''' </summary>
    Public Class MemoryItemViewModel
        Inherits ObservableObject

        Public Property Id As String
        Public Property IsPinned As Boolean
        Public Property CreatedAt As DateTime

        Private _content As String
        Public Property Content As String
            Get
                Return _content
            End Get
            Set(value As String)
                SetProperty(_content, value)
            End Set
        End Property
    End Class

    ''' <summary>One ingested source's row under a Knowledge Base card.</summary>
    Public Class KnowledgeSourceViewModel
        Public Property Id As String
        Public Property KnowledgeBaseId As String
        Public Property SourceType As String
        Public Property DisplayName As String
        Public Property Status As String
        Public Property ErrorMessage As String
        Public Property ChunkCount As Integer

        ''' <summary>Small icon per source type, shown in the sources list.</summary>
        Public ReadOnly Property Icon As String
            Get
                Select Case SourceType
                    Case "File" : Return ChrW(&HE7C3) ' Segoe MDL2 Assets - Page2
                    Case "Folder" : Return ChrW(&HE8B7) ' Folder
                    Case "Website" : Return ChrW(&HE774) ' Globe
                    Case Else : Return ChrW(&HE8A5) ' TextDocument (Text source)
                End Select
            End Get
        End Property
    End Class

    ''' <summary>
    ''' One Knowledge Base card in the Settings screen - holds its sources
    ''' plus the per-card "add a source" mini-form state (a website URL box,
    ''' a paste-text box+name), since each card has its own independent set
    ''' of these inputs.
    ''' </summary>
    Public Class KnowledgeBaseViewModel
        Inherits ObservableObject

        Public Property Id As String
        Public Property Name As String
        Public Property Description As String

        Public ReadOnly Property Sources As New ObservableCollection(Of KnowledgeSourceViewModel)()

        Private _isBusy As Boolean = False
        Public Property IsBusy As Boolean
            Get
                Return _isBusy
            End Get
            Set(value As Boolean)
                SetProperty(_isBusy, value)
            End Set
        End Property

        Private _statusText As String = ""
        Public Property StatusText As String
            Get
                Return _statusText
            End Get
            Set(value As String)
                SetProperty(_statusText, value)
            End Set
        End Property

        Private _pendingWebsiteUrl As String = ""
        Public Property PendingWebsiteUrl As String
            Get
                Return _pendingWebsiteUrl
            End Get
            Set(value As String)
                SetProperty(_pendingWebsiteUrl, value)
            End Set
        End Property

        Private _pendingTextName As String = ""
        Public Property PendingTextName As String
            Get
                Return _pendingTextName
            End Get
            Set(value As String)
                SetProperty(_pendingTextName, value)
            End Set
        End Property

        Private _pendingTextContent As String = ""
        Public Property PendingTextContent As String
            Get
                Return _pendingTextContent
            End Get
            Set(value As String)
                SetProperty(_pendingTextContent, value)
            End Set
        End Property
    End Class

    ''' <summary>One configured MCP server's row in the Settings screen.</summary>
    Public Class McpServerViewModel
        Inherits ObservableObject

        Public Property Id As String
        Public Property Name As String
        Public Property Command As String
        Public Property ArgumentsDisplay As String
        Public Property EnvironmentVariableNamesDisplay As String

        ''' <summary>
        ''' A plain TwoWay CheckBox binding only changes the in-memory
        ''' ViewModel property, nothing more - it would never actually
        ''' persist a toggle anywhere on its own.
        ''' OnIsEnabledChanged is set by SettingsViewModel when constructing
        ''' each row, so the property setter itself can trigger the real
        ''' repository write - kept here (not a separate Command + CheckBox
        ''' Click handler) since WPF's CheckBox has no single clean
        ''' "IsChecked changed" command hook alongside a plain TwoWay
        ''' binding, and this needs both the correct visual state and the
        ''' persistence to happen together.
        ''' </summary>
        Private _isEnabled As Boolean
        Public Property IsEnabled As Boolean
            Get
                Return _isEnabled
            End Get
            Set(value As Boolean)
                If SetProperty(_isEnabled, value) Then
                    OnIsEnabledChanged?.Invoke(value)
                End If
            End Set
        End Property

        Public Property OnIsEnabledChanged As Action(Of Boolean)
    End Class

    ''' <summary>One scheduled job's row in the Settings screen.</summary>
    Public Class ScheduledJobViewModel
        Inherits ObservableObject

        Public Property Id As String
        Public Property Name As String
        Public Property CronExpressionText As String
        Public Property Prompt As String
        Public Property LastRunDisplay As String
        Public Property NextRunDisplay As String

        ''' <summary>Empty/Nothing means "use whatever model is currently selected/default" - bound to a ComboBox alongside AvailableModels plus a leading "(use current model)" sentinel.</summary>
        Public Property ModelId As String

        ''' <summary>Same OnIsEnabledChanged pattern as McpServerViewModel - see its comment.</summary>
        Private _isEnabled As Boolean
        Public Property IsEnabled As Boolean
            Get
                Return _isEnabled
            End Get
            Set(value As Boolean)
                If SetProperty(_isEnabled, value) Then
                    OnIsEnabledChanged?.Invoke(value)
                End If
            End Set
        End Property

        Public Property OnIsEnabledChanged As Action(Of Boolean)
    End Class

    ''' <summary>
    ''' Drives the Settings window. Works on its own copy of AppSettings
    ''' (loaded fresh via AppSettingsStore) and only touches disk when Save
    ''' is clicked - same as before the Settings live-reload work. What's
    ''' new: Save also pushes every field into _liveSettings (the app's real,
    ''' shared DI singleton - see AppSettings.CopyInto's own comment for why
    ''' a field-by-field copy, not a wholesale reference swap, is what
    ''' actually reaches already-constructed services) and calls
    ''' ModuleRegistry.ReconcileEnabledModulesAsync, so most settings now
    ''' take effect immediately with no restart. A small, explicit set still
    ''' does need one - see RestartRequiredFieldsChanged.
    ''' </summary>
    Public Class SettingsViewModel
        Inherits ObservableObject

        Private ReadOnly _store As AppSettingsStore
        Private ReadOnly _settings As AppSettings
        Private ReadOnly _liveSettings As AppSettings
        Private ReadOnly _moduleRegistry As ModuleRegistry

        ''' <summary>Serialized _settings right after loading, and again after every successful save - what HasUnsavedChanges compares the current UI state against.</summary>
        Private _lastSavedJson As String
        Private ReadOnly _memoryService As MemoryService
        Private ReadOnly _knowledgeRepository As KnowledgeRepository
        Private ReadOnly _knowledgeIngestionService As KnowledgeIngestionService
        Private ReadOnly _mcpServerRepository As McpServerRepository
        Private ReadOnly _schedulerRepository As SchedulerRepository

        Private _dataFolder As String
        Public Property DataFolder As String
            Get
                Return _dataFolder
            End Get
            Set(value As String)
                If SetProperty(_dataFolder, value) Then
                    OnPropertyChanged(NameOf(ResolvedDataFolderPreview))
                End If
            End Set
        End Property

        ''' <summary>
        ''' Shows where DataFolder actually resolves to right now, as typed -
        ''' relative paths are easy to misjudge at a glance, and this is the
        ''' one setting where getting it wrong means "where did my chats go".
        ''' </summary>
        Public ReadOnly Property ResolvedDataFolderPreview As String
            Get
                Return New AppDataSettings With {.DataFolder = DataFolder}.ResolvedDataFolder()
            End Get
        End Property

        Private _lemonadeBaseUrl As String
        Public Property LemonadeBaseUrl As String
            Get
                Return _lemonadeBaseUrl
            End Get
            Set(value As String)
                SetProperty(_lemonadeBaseUrl, value)
            End Set
        End Property

        Private _lemonadeChatModel As String
        Public Property LemonadeChatModel As String
            Get
                Return _lemonadeChatModel
            End Get
            Set(value As String)
                SetProperty(_lemonadeChatModel, value)
            End Set
        End Property

        Private _lemonadeEmbeddingModel As String
        Public Property LemonadeEmbeddingModel As String
            Get
                Return _lemonadeEmbeddingModel
            End Get
            Set(value As String)
                SetProperty(_lemonadeEmbeddingModel, value)
            End Set
        End Property

        ''' <summary>Post-audit addition - optional, Lemonade doesn't enforce a key but its own docs recommend providing one. Same restart-required scope as BaseUrl/ChatModel/EmbeddingModel above.</summary>
        Private _lemonadeApiKey As String
        Public Property LemonadeApiKey As String
            Get
                Return _lemonadeApiKey
            End Get
            Set(value As String)
                SetProperty(_lemonadeApiKey, value)
            End Set
        End Property

        ''' <summary>
        ''' Six fields, one per ImageGenSettings property - plain strings/
        ''' numbers bound straight to TextBoxes (matching every other
        ''' Settings field's pattern in this file, e.g. LemonadeChatModel),
        ''' not a nested ViewModel - there's no list/collection here, just a
        ''' handful of scalar fields for one module.
        ''' </summary>
        Private _imageGenModelId As String
        Public Property ImageGenModelId As String
            Get
                Return _imageGenModelId
            End Get
            Set(value As String)
                SetProperty(_imageGenModelId, value)
            End Set
        End Property

        Private _imageGenDefaultWidth As Integer
        Public Property ImageGenDefaultWidth As Integer
            Get
                Return _imageGenDefaultWidth
            End Get
            Set(value As Integer)
                SetProperty(_imageGenDefaultWidth, value)
            End Set
        End Property

        Private _imageGenDefaultHeight As Integer
        Public Property ImageGenDefaultHeight As Integer
            Get
                Return _imageGenDefaultHeight
            End Get
            Set(value As Integer)
                SetProperty(_imageGenDefaultHeight, value)
            End Set
        End Property

        Private _imageGenSteps As Integer
        Public Property ImageGenSteps As Integer
            Get
                Return _imageGenSteps
            End Get
            Set(value As Integer)
                SetProperty(_imageGenSteps, value)
            End Set
        End Property

        Private _imageGenCfgScale As Double
        Public Property ImageGenCfgScale As Double
            Get
                Return _imageGenCfgScale
            End Get
            Set(value As Double)
                SetProperty(_imageGenCfgScale, value)
            End Set
        End Property

        Private _imageGenSeed As Integer
        Public Property ImageGenSeed As Integer
            Get
                Return _imageGenSeed
            End Get
            Set(value As Integer)
                SetProperty(_imageGenSeed, value)
            End Set
        End Property

        Private _searXngBaseUrl As String
        Public Property SearXngBaseUrl As String
            Get
                Return _searXngBaseUrl
            End Get
            Set(value As String)
                SetProperty(_searXngBaseUrl, value)
            End Set
        End Property

        ''' <summary>Post-SearXNG-saga addition - shared by search_web/read_webpage, see WebSearchSettings.Engine's own comment. "SearXNG" first so it stays the visible default.</summary>
        Public ReadOnly Property WebEngineOptions As New ObservableCollection(Of String) From {"SearXNG", "Jina", "Tavily", "Firecrawl"}

        Private _webEngine As String
        Public Property WebEngine As String
            Get
                Return _webEngine
            End Get
            Set(value As String)
                SetProperty(_webEngine, value)
            End Set
        End Property

        Private _jinaApiKey As String
        Public Property JinaApiKey As String
            Get
                Return _jinaApiKey
            End Get
            Set(value As String)
                SetProperty(_jinaApiKey, value)
            End Set
        End Property

        Private _tavilyApiKey As String
        Public Property TavilyApiKey As String
            Get
                Return _tavilyApiKey
            End Get
            Set(value As String)
                SetProperty(_tavilyApiKey, value)
            End Set
        End Property

        Private _firecrawlApiKey As String
        Public Property FirecrawlApiKey As String
            Get
                Return _firecrawlApiKey
            End Get
            Set(value As String)
                SetProperty(_firecrawlApiKey, value)
            End Set
        End Property

        Public ReadOnly Property AllowedRoots As New ObservableCollection(Of String)()

        Private _newRootPath As String = ""
        Public Property NewRootPath As String
            Get
                Return _newRootPath
            End Get
            Set(value As String)
                SetProperty(_newRootPath, value)
            End Set
        End Property

        Public ReadOnly Property ModuleToggles As New ObservableCollection(Of ModuleToggleItem)()

        ''' <summary>
        ''' One per module that has its own dedicated Settings section
        ''' (Image generation, MCP Servers, Web search, File system access,
        ''' Scheduler - Coder/WebReader don't have a section of their own,
        ''' and Memory/Knowledge Bases aren't gated by a module toggle at
        ''' all) - drives that section's "this module is currently off"
        ''' banner. Kept as named properties rather than a generic
        ''' section-name-to-enabled lookup so each section's XAML can just
        ''' bind directly, no converter/MultiBinding needed. Seeded from
        ''' _settings on load and kept live via each ModuleToggleItem's
        ''' OnIsEnabledChanged (see UpdateModuleEnabledDisplayProperty).
        ''' </summary>
        Private _isImageGenModuleEnabled As Boolean
        Public Property IsImageGenModuleEnabled As Boolean
            Get
                Return _isImageGenModuleEnabled
            End Get
            Set(value As Boolean)
                SetProperty(_isImageGenModuleEnabled, value)
            End Set
        End Property

        Private _isMcpModuleEnabled As Boolean
        Public Property IsMcpModuleEnabled As Boolean
            Get
                Return _isMcpModuleEnabled
            End Get
            Set(value As Boolean)
                SetProperty(_isMcpModuleEnabled, value)
            End Set
        End Property

        Private _isWebSearchModuleEnabled As Boolean
        Public Property IsWebSearchModuleEnabled As Boolean
            Get
                Return _isWebSearchModuleEnabled
            End Get
            Set(value As Boolean)
                SetProperty(_isWebSearchModuleEnabled, value)
            End Set
        End Property

        Private _isFileSystemModuleEnabled As Boolean
        Public Property IsFileSystemModuleEnabled As Boolean
            Get
                Return _isFileSystemModuleEnabled
            End Get
            Set(value As Boolean)
                SetProperty(_isFileSystemModuleEnabled, value)
            End Set
        End Property

        Private _isSchedulerModuleEnabled As Boolean
        Public Property IsSchedulerModuleEnabled As Boolean
            Get
                Return _isSchedulerModuleEnabled
            End Get
            Set(value As Boolean)
                SetProperty(_isSchedulerModuleEnabled, value)
            End Set
        End Property

        ''' <summary>Knowledge Bases has its own entry in the module-toggle list, like every other module.</summary>
        Private _isKnowledgeModuleEnabled As Boolean
        Public Property IsKnowledgeModuleEnabled As Boolean
            Get
                Return _isKnowledgeModuleEnabled
            End Get
            Set(value As Boolean)
                SetProperty(_isKnowledgeModuleEnabled, value)
            End Set
        End Property

        ''' <summary>Post-audit addition (Auto-backup) - same "dedicated section needs a display property" pattern as the others above.</summary>
        Private _isBackupModuleEnabled As Boolean
        Public Property IsBackupModuleEnabled As Boolean
            Get
                Return _isBackupModuleEnabled
            End Get
            Set(value As Boolean)
                SetProperty(_isBackupModuleEnabled, value)
            End Set
        End Property

        ''' <summary>Maps a module's ConfigKey to its dedicated display property - Case Else is deliberate (Coder/WebReader have no Settings section of their own to update).</summary>
        Private Sub UpdateModuleEnabledDisplayProperty(configKey As String, isEnabled As Boolean)
            Select Case configKey
                Case "ImageGen" : IsImageGenModuleEnabled = isEnabled
                Case "Mcp" : IsMcpModuleEnabled = isEnabled
                Case "WebSearch" : IsWebSearchModuleEnabled = isEnabled
                Case "FileSystem" : IsFileSystemModuleEnabled = isEnabled
                Case "Scheduler" : IsSchedulerModuleEnabled = isEnabled
                Case "Knowledge" : IsKnowledgeModuleEnabled = isEnabled
                Case "Backup" : IsBackupModuleEnabled = isEnabled
            End Select
        End Sub

        Private _compactionTriggerPercent As Integer
        ''' <summary>
        ''' How full the model's context window gets (as a percentage of its
        ''' real max) before short-term compaction kicks in and summarizes
        ''' older messages - see MainViewModel.CompactHistoryIfNeededAsync.
        ''' Exposed here rather than a fixed constant since the right value
        ''' depends on the model's real context size and how verbose replies
        ''' tend to be, which varies enough to be worth tuning per setup.
        ''' </summary>
        Public Property CompactionTriggerPercent As Integer
            Get
                Return _compactionTriggerPercent
            End Get
            Set(value As Integer)
                SetProperty(_compactionTriggerPercent, value)
            End Set
        End Property

        ''' <summary>A normal live-reload Settings field like everything else here.</summary>
        Private _systemPrompt As String
        Public Property SystemPrompt As String
            Get
                Return _systemPrompt
            End Get
            Set(value As String)
                SetProperty(_systemPrompt, value)
            End Set
        End Property

        ''' <summary>
        ''' Persona fields - see AppSettings.PersonaSettings for the full
        ''' design rationale. Same live-reload pattern as SystemPrompt
        ''' above: plain SetProperty-backed fields, seeded from _settings in
        ''' the constructor, written back in ApplyUiValuesToSettings().
        ''' </summary>
        Private _personaIdentity As String
        Public Property PersonaIdentity As String
            Get
                Return _personaIdentity
            End Get
            Set(value As String)
                SetProperty(_personaIdentity, value)
            End Set
        End Property

        Private _personaAboutUser As String
        Public Property PersonaAboutUser As String
            Get
                Return _personaAboutUser
            End Get
            Set(value As String)
                SetProperty(_personaAboutUser, value)
            End Set
        End Property

        Private _personaTone As String
        Public Property PersonaTone As String
            Get
                Return _personaTone
            End Get
            Set(value As String)
                SetProperty(_personaTone, value)
            End Set
        End Property

        Private _personaVerbosity As String
        Public Property PersonaVerbosity As String
            Get
                Return _personaVerbosity
            End Get
            Set(value As String)
                SetProperty(_personaVerbosity, value)
            End Set
        End Property

        Private _personaEmojiUsage As String
        Public Property PersonaEmojiUsage As String
            Get
                Return _personaEmojiUsage
            End Get
            Set(value As String)
                SetProperty(_personaEmojiUsage, value)
            End Set
        End Property

        Private _personaCustomInstructions As String
        Public Property PersonaCustomInstructions As String
            Get
                Return _personaCustomInstructions
            End Get
            Set(value As String)
                SetProperty(_personaCustomInstructions, value)
            End Set
        End Property

        ''' <summary>Options for the Tone/Verbosity/Emoji-use dropdowns - "Default" always first so it's the natural do-nothing choice.</summary>
        Public ReadOnly Property PersonaToneOptions As New ObservableCollection(Of String) From {"Default", "Casual", "Formal", "Warm", "Concise", "Direct"}
        Public ReadOnly Property PersonaVerbosityOptions As New ObservableCollection(Of String) From {"Default", "Concise", "Detailed"}
        Public ReadOnly Property PersonaEmojiUsageOptions As New ObservableCollection(Of String) From {"Default", "None", "Sparing", "Frequent"}

        ''' <summary>
        ''' Post-audit "Auto-backup" feature - see AppSettings.BackupSettings
        ''' for the design rationale. Same live-reload pattern as everything
        ''' else in this class.
        ''' </summary>
        Private _backupIntervalDays As Integer
        Public Property BackupIntervalDays As Integer
            Get
                Return _backupIntervalDays
            End Get
            Set(value As Integer)
                SetProperty(_backupIntervalDays, value)
            End Set
        End Property

        Private _backupFolder As String
        Public Property BackupFolder As String
            Get
                Return _backupFolder
            End Get
            Set(value As String)
                SetProperty(_backupFolder, value)
            End Set
        End Property

        Private _backupKeepCount As Integer
        Public Property BackupKeepCount As Integer
            Get
                Return _backupKeepCount
            End Get
            Set(value As Integer)
                SetProperty(_backupKeepCount, value)
            End Set
        End Property

        Public ReadOnly Property PinnedMemories As New ObservableCollection(Of MemoryItemViewModel)()
        Public ReadOnly Property SearchableMemories As New ObservableCollection(Of MemoryItemViewModel)()

        Public ReadOnly Property KnowledgeBases As New ObservableCollection(Of KnowledgeBaseViewModel)()

        Private _newKnowledgeBaseName As String = ""
        Public Property NewKnowledgeBaseName As String
            Get
                Return _newKnowledgeBaseName
            End Get
            Set(value As String)
                SetProperty(_newKnowledgeBaseName, value)
            End Set
        End Property

        Private _newKnowledgeBaseDescription As String = ""
        Public Property NewKnowledgeBaseDescription As String
            Get
                Return _newKnowledgeBaseDescription
            End Get
            Set(value As String)
                SetProperty(_newKnowledgeBaseDescription, value)
            End Set
        End Property

        Public ReadOnly Property McpServers As New ObservableCollection(Of McpServerViewModel)()

        Private _pendingMcpServerConfigJson As String = ""
        ''' <summary>
        ''' The standard `{"mcpServers": {"name": {"command", "args", "env"}}}`
        ''' config shape most MCP servers' own docs give you a ready-to-paste
        ''' snippet in (e.g. Postmark's README) - the primary, real-world way
        ''' servers actually get added, not just a nice-to-have alternative
        ''' to the manual fields below.
        ''' </summary>
        Public Property PendingMcpServerConfigJson As String
            Get
                Return _pendingMcpServerConfigJson
            End Get
            Set(value As String)
                SetProperty(_pendingMcpServerConfigJson, value)
            End Set
        End Property

        Private _newMcpServerName As String = ""
        Public Property NewMcpServerName As String
            Get
                Return _newMcpServerName
            End Get
            Set(value As String)
                SetProperty(_newMcpServerName, value)
            End Set
        End Property

        Private _newMcpServerCommand As String = ""
        Public Property NewMcpServerCommand As String
            Get
                Return _newMcpServerCommand
            End Get
            Set(value As String)
                SetProperty(_newMcpServerCommand, value)
            End Set
        End Property

        Private _newMcpServerArguments As String = ""
        ''' <summary>Space-separated, split on whitespace when the server is created - e.g. "-y @activecampaign/postmark-mcp".</summary>
        Public Property NewMcpServerArguments As String
            Get
                Return _newMcpServerArguments
            End Get
            Set(value As String)
                SetProperty(_newMcpServerArguments, value)
            End Set
        End Property

        Private _newMcpServerEnvironmentVariables As String = ""
        ''' <summary>One KEY=VALUE pair per line.</summary>
        Public Property NewMcpServerEnvironmentVariables As String
            Get
                Return _newMcpServerEnvironmentVariables
            End Get
            Set(value As String)
                SetProperty(_newMcpServerEnvironmentVariables, value)
            End Set
        End Property

        Private _mcpStatusText As String = ""
        Public Property McpStatusText As String
            Get
                Return _mcpStatusText
            End Get
            Set(value As String)
                SetProperty(_mcpStatusText, value)
            End Set
        End Property

        Public ReadOnly Property ScheduledJobs As New ObservableCollection(Of ScheduledJobViewModel)()

        ''' <summary>Bound to both the "New scheduled job" and each row's edit ComboBox alongside the UseCurrentModelSentinel - loaded once, fire-and-forget, in the constructor (see LoadAvailableModelsAsync).</summary>
        Public ReadOnly Property AvailableModels As New ObservableCollection(Of String)()

        ''' <summary>Not a real model id - a job storing this (or Nothing/empty) just means "whatever model is currently selected/default when the job fires", the same as leaving it unset entirely.</summary>
        Public Const UseCurrentModelSentinel As String = "(use current model)"

        Private _newJobModelId As String = UseCurrentModelSentinel
        Public Property NewJobModelId As String
            Get
                Return _newJobModelId
            End Get
            Set(value As String)
                SetProperty(_newJobModelId, value)
            End Set
        End Property

        Private _newJobName As String = ""
        Public Property NewJobName As String
            Get
                Return _newJobName
            End Get
            Set(value As String)
                SetProperty(_newJobName, value)
            End Set
        End Property

        Private _newJobCronExpressionText As String = ""
        ''' <summary>
        ''' The single source of truth actually sent to SchedulerRepository -
        ''' the builder controls below (Frequency/Hour/Minute/day pickers)
        ''' just compute and overwrite this whenever one of them changes and
        ''' Frequency isn't "Custom", so someone who wants a schedule the
        ''' simple picker can't express can still type/edit raw cron syntax
        ''' directly here, same field either way.
        ''' </summary>
        Public Property NewJobCronExpressionText As String
            Get
                Return _newJobCronExpressionText
            End Get
            Set(value As String)
                SetProperty(_newJobCronExpressionText, value)
            End Set
        End Property

        Public ReadOnly Property JobFrequencyOptions As New ObservableCollection(Of String) From {"Daily", "Weekly", "Monthly", "Custom (type your own)"}

        Private _jobFrequency As String = "Daily"
        Public Property JobFrequency As String
            Get
                Return _jobFrequency
            End Get
            Set(value As String)
                If SetProperty(_jobFrequency, value) Then RebuildCronFromBuilder()
            End Set
        End Property

        Public ReadOnly Property JobHourOptions As New ObservableCollection(Of Integer)(Enumerable.Range(0, 24))

        Private _jobHour As Integer = 9
        Public Property JobHour As Integer
            Get
                Return _jobHour
            End Get
            Set(value As Integer)
                If SetProperty(_jobHour, value) Then RebuildCronFromBuilder()
            End Set
        End Property

        ''' <summary>5-minute steps (00, 05, ..., 55) - covers virtually every real schedule without needing a full 0-59 picker.</summary>
        Public ReadOnly Property JobMinuteOptions As New ObservableCollection(Of Integer)(Enumerable.Range(0, 12).Select(Function(i) i * 5))

        Private _jobMinute As Integer = 0
        Public Property JobMinute As Integer
            Get
                Return _jobMinute
            End Get
            Set(value As Integer)
                If SetProperty(_jobMinute, value) Then RebuildCronFromBuilder()
            End Set
        End Property

        ''' <summary>Standard cron day-of-week numbering (0 = Sunday ... 6 = Saturday) matches System.DayOfWeek's own values exactly, so this doubles as the ComboBox's bound items with no translation needed.</summary>
        Public ReadOnly Property JobDayOfWeekOptions As New ObservableCollection(Of DayOfWeek) From {
            DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
            DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
        }

        Private _jobDayOfWeek As DayOfWeek = DayOfWeek.Monday
        Public Property JobDayOfWeek As DayOfWeek
            Get
                Return _jobDayOfWeek
            End Get
            Set(value As DayOfWeek)
                If SetProperty(_jobDayOfWeek, value) Then RebuildCronFromBuilder()
            End Set
        End Property

        Public ReadOnly Property JobDayOfMonthOptions As New ObservableCollection(Of Integer)(Enumerable.Range(1, 31))

        Private _jobDayOfMonth As Integer = 1
        Public Property JobDayOfMonth As Integer
            Get
                Return _jobDayOfMonth
            End Get
            Set(value As Integer)
                If SetProperty(_jobDayOfMonth, value) Then RebuildCronFromBuilder()
            End Set
        End Property

        ''' <summary>Standard 5-field cron - minute hour day-of-month month day-of-week.</summary>
        Private Sub RebuildCronFromBuilder()
            Select Case JobFrequency
                Case "Daily"
                    NewJobCronExpressionText = $"{JobMinute} {JobHour} * * *"
                Case "Weekly"
                    NewJobCronExpressionText = $"{JobMinute} {JobHour} * * {CInt(JobDayOfWeek)}"
                Case "Monthly"
                    NewJobCronExpressionText = $"{JobMinute} {JobHour} {JobDayOfMonth} * *"
                Case Else
                    ' "Custom (type your own)" - leave whatever's already
                    ' there alone, this is the one case where the raw field
                    ' is the user's own to edit freely.
            End Select
        End Sub

        Private _newJobPrompt As String = ""
        Public Property NewJobPrompt As String
            Get
                Return _newJobPrompt
            End Get
            Set(value As String)
                SetProperty(_newJobPrompt, value)
            End Set
        End Property

        Private _schedulerStatusText As String = ""
        Public Property SchedulerStatusText As String
            Get
                Return _schedulerStatusText
            End Get
            Set(value As String)
                SetProperty(_schedulerStatusText, value)
            End Set
        End Property

        Private _memoriesStatusText As String = ""
        Public Property MemoriesStatusText As String
            Get
                Return _memoriesStatusText
            End Get
            Set(value As String)
                SetProperty(_memoriesStatusText, value)
            End Set
        End Property

        Private _statusText As String = ""
        Public Property StatusText As String
            Get
                Return _statusText
            End Get
            Set(value As String)
                SetProperty(_statusText, value)
            End Set
        End Property

        ''' <summary>
        ''' One-shot timer clearing StatusText a few seconds after it's set -
        ''' the message shows plainly, then disappears on its own, which is
        ''' a much stronger signal that a NEW save just happened than a
        ''' static line that never visibly changes; even two saves in a row
        ''' with the identical final message each get their own clear
        ''' "shown, then gone" cycle.
        ''' </summary>
        Private _statusTextClearTimer As DispatcherTimer

        Private Sub ShowStatusTextTemporarily(text As String)
            StatusText = text

            _statusTextClearTimer?.Stop()
            _statusTextClearTimer = New DispatcherTimer With {.Interval = TimeSpan.FromSeconds(3)}
            AddHandler _statusTextClearTimer.Tick,
                Sub(sender, e)
                    StatusText = ""
                    _statusTextClearTimer.Stop()
                End Sub
            _statusTextClearTimer.Start()
        End Sub

        Public ReadOnly Property BrowseForDataFolderCommand As IRelayCommand
        Public ReadOnly Property AddRootCommand As IRelayCommand
        Public ReadOnly Property BrowseForRootCommand As IRelayCommand
        Public ReadOnly Property RemoveRootCommand As IRelayCommand(Of String)
        Public ReadOnly Property SaveCommand As IAsyncRelayCommand
        Public ReadOnly Property SaveMemoryEditCommand As IAsyncRelayCommand(Of MemoryItemViewModel)
        Public ReadOnly Property DeleteMemoryCommand As IRelayCommand(Of MemoryItemViewModel)

        Public ReadOnly Property CreateKnowledgeBaseCommand As IRelayCommand
        Public ReadOnly Property DeleteKnowledgeBaseCommand As IRelayCommand(Of KnowledgeBaseViewModel)
        Public ReadOnly Property AddFileSourceCommand As IAsyncRelayCommand(Of KnowledgeBaseViewModel)
        Public ReadOnly Property AddFolderSourceCommand As IAsyncRelayCommand(Of KnowledgeBaseViewModel)
        Public ReadOnly Property AddWebsiteSourceCommand As IAsyncRelayCommand(Of KnowledgeBaseViewModel)
        Public ReadOnly Property AddTextSourceCommand As IAsyncRelayCommand(Of KnowledgeBaseViewModel)
        Public ReadOnly Property RemoveSourceCommand As IRelayCommand(Of KnowledgeSourceViewModel)

        Public ReadOnly Property ImportMcpServerConfigJsonCommand As IAsyncRelayCommand
        Public ReadOnly Property CreateMcpServerCommand As IAsyncRelayCommand
        Public ReadOnly Property DeleteMcpServerCommand As IAsyncRelayCommand(Of McpServerViewModel)

        Public ReadOnly Property CreateScheduledJobCommand As IRelayCommand
        Public ReadOnly Property DeleteScheduledJobCommand As IRelayCommand(Of ScheduledJobViewModel)
        Public ReadOnly Property SaveScheduledJobEditCommand As IRelayCommand(Of ScheduledJobViewModel)

        ''' <summary>
        ''' Left-nav sections - only sections with a real feature behind
        ''' them appear here, rather than showing empty placeholder panels
        ''' for a feature that doesn't exist yet.
        ''' </summary>
        ' Lemonade first - SelectedSection defaults to "Lemonade" below, so
        ' this keeps the highlighted nav item at the top of the list rather
        ' than looking like it skipped over Storage above it.
        Public ReadOnly Property Sections As New ObservableCollection(Of String) From {
            "Lemonade", "Assistant", "Persona", "Storage", "Web search", "File system access", "Modules", "Memories", "Knowledge Bases", "MCP Servers", "Scheduler", "Image generation", "Auto-backup"
        }

        Private _selectedSection As String = "Lemonade"
        Public Property SelectedSection As String
            Get
                Return _selectedSection
            End Get
            Set(value As String)
                SetProperty(_selectedSection, value)
            End Set
        End Property

        Private ReadOnly _managementClient As LemonadeManagementClient

        Public Sub New(store As AppSettingsStore, liveSettings As AppSettings, moduleRegistry As ModuleRegistry, memoryService As MemoryService, knowledgeRepository As KnowledgeRepository, knowledgeIngestionService As KnowledgeIngestionService, mcpServerRepository As McpServerRepository, schedulerRepository As SchedulerRepository, managementClient As LemonadeManagementClient)
            _store = store
            _settings = store.LoadFresh()
            _liveSettings = liveSettings
            _moduleRegistry = moduleRegistry
            _memoryService = memoryService
            _knowledgeRepository = knowledgeRepository
            _knowledgeIngestionService = knowledgeIngestionService
            _mcpServerRepository = mcpServerRepository
            _schedulerRepository = schedulerRepository
            _managementClient = managementClient

            _dataFolder = _settings.AppData.DataFolder
            _compactionTriggerPercent = _settings.Memory.CompactionTriggerPercent
            _systemPrompt = _settings.Assistant.SystemPrompt
            _personaIdentity = _settings.Persona.Identity
            _personaAboutUser = _settings.Persona.AboutUser
            _personaTone = _settings.Persona.Tone
            _personaVerbosity = _settings.Persona.Verbosity
            _personaEmojiUsage = _settings.Persona.EmojiUsage
            _personaCustomInstructions = _settings.Persona.CustomInstructions
            _backupIntervalDays = _settings.Backup.IntervalDays
            _backupFolder = _settings.Backup.BackupFolder
            _backupKeepCount = _settings.Backup.KeepCount
            _lemonadeBaseUrl = _settings.Lemonade.BaseUrl
            _lemonadeChatModel = _settings.Lemonade.ChatModel
            _lemonadeEmbeddingModel = _settings.Lemonade.EmbeddingModel
            _lemonadeApiKey = _settings.Lemonade.ApiKey
            _imageGenModelId = _settings.ImageGen.ModelId
            _imageGenDefaultWidth = _settings.ImageGen.DefaultWidth
            _imageGenDefaultHeight = _settings.ImageGen.DefaultHeight
            _imageGenSteps = _settings.ImageGen.Steps
            _imageGenCfgScale = _settings.ImageGen.CfgScale
            _imageGenSeed = _settings.ImageGen.Seed
            _searXngBaseUrl = _settings.WebSearch.SearXngBaseUrl
            _webEngine = _settings.WebSearch.Engine
            _jinaApiKey = _settings.WebSearch.JinaApiKey
            _tavilyApiKey = _settings.WebSearch.TavilyApiKey
            _firecrawlApiKey = _settings.WebSearch.FirecrawlApiKey

            For Each root In _settings.FileSystem.AllowedRoots
                AllowedRoots.Add(root)
            Next

            ' Seeds from the real module list (ModuleRegistry.AllModules) so
            ' this can never drift out of sync with which modules actually
            ' exist - a hardcoded list here would need updating by hand every
            ' time a module is added, this doesn't.
            For Each assistantModule In moduleRegistry.AllModules
                ' assistantModule.IsEnabled, not a second GetValueOrDefault
                ' against _settings.Modules.Enabled with its own hardcoded
                ' False fallback - a hardcoded False here would silently
                ' disagree with KnowledgeModule's own True default (it
                ' defaults on, unlike every other module, since disabling it
                ' would remove existing functionality), showing the
                ' checkbox unchecked while the module itself, and everywhere
                ' else in the app, correctly treats it as enabled. Each
                ' module's own IsEnabled is the one place its real default
                ' belongs.
                Dim isModuleEnabled = assistantModule.IsEnabled
                Dim toggleItem As New ModuleToggleItem With {
                    .Name = assistantModule.Name,
                    .Description = assistantModule.Description,
                    .ConfigKey = assistantModule.ConfigKey,
                    .IsEnabled = isModuleEnabled
                }
                ' OnIsEnabledChanged wired after the object initializer above
                ' (so the initial .IsEnabled assignment there doesn't fire it
                ' before there's anything listening) - UpdateModuleEnabledDisplayProperty
                ' below covers seeding the initial state instead.
                toggleItem.OnIsEnabledChanged = Sub(newValue) UpdateModuleEnabledDisplayProperty(assistantModule.ConfigKey, newValue)
                ModuleToggles.Add(toggleItem)
                UpdateModuleEnabledDisplayProperty(assistantModule.ConfigKey, isModuleEnabled)
            Next

            LoadMemories()
            LoadKnowledgeBases()
            LoadMcpServers()
            LoadScheduledJobs()
            RebuildCronFromBuilder() ' populates the default "Daily 09:00" cron text immediately, instead of starting empty
            AvailableModels.Add(UseCurrentModelSentinel)
            LoadAvailableModelsFireAndForget()

            BrowseForDataFolderCommand = New RelayCommand(AddressOf BrowseForDataFolder)
            AddRootCommand = New RelayCommand(AddressOf AddRoot)
            BrowseForRootCommand = New RelayCommand(AddressOf BrowseForRoot)
            RemoveRootCommand = New RelayCommand(Of String)(AddressOf RemoveRoot)
            SaveCommand = New AsyncRelayCommand(AddressOf SaveAsync)
            SaveMemoryEditCommand = New AsyncRelayCommand(Of MemoryItemViewModel)(AddressOf SaveMemoryEditAsync)
            DeleteMemoryCommand = New RelayCommand(Of MemoryItemViewModel)(AddressOf DeleteMemory)

            CreateKnowledgeBaseCommand = New RelayCommand(AddressOf CreateKnowledgeBase)
            DeleteKnowledgeBaseCommand = New RelayCommand(Of KnowledgeBaseViewModel)(AddressOf DeleteKnowledgeBase)
            AddFileSourceCommand = New AsyncRelayCommand(Of KnowledgeBaseViewModel)(AddressOf AddFileSourceAsync)
            AddFolderSourceCommand = New AsyncRelayCommand(Of KnowledgeBaseViewModel)(AddressOf AddFolderSourceAsync)
            AddWebsiteSourceCommand = New AsyncRelayCommand(Of KnowledgeBaseViewModel)(AddressOf AddWebsiteSourceAsync)
            AddTextSourceCommand = New AsyncRelayCommand(Of KnowledgeBaseViewModel)(AddressOf AddTextSourceAsync)
            RemoveSourceCommand = New RelayCommand(Of KnowledgeSourceViewModel)(AddressOf RemoveSource)

            ImportMcpServerConfigJsonCommand = New AsyncRelayCommand(AddressOf ImportMcpServerConfigJsonAsync)
            CreateMcpServerCommand = New AsyncRelayCommand(AddressOf CreateMcpServerAsync)
            DeleteMcpServerCommand = New AsyncRelayCommand(Of McpServerViewModel)(AddressOf DeleteMcpServerAsync)

            CreateScheduledJobCommand = New RelayCommand(AddressOf CreateScheduledJob)
            DeleteScheduledJobCommand = New RelayCommand(Of ScheduledJobViewModel)(AddressOf DeleteScheduledJob)
            SaveScheduledJobEditCommand = New RelayCommand(Of ScheduledJobViewModel)(AddressOf SaveScheduledJobEdit)

            ' Captured here (via ApplyUiValuesToSettings, same normalization
            ' HasUnsavedChanges/SaveAsync apply later), not right after
            ' LoadFresh() - a module's config key (e.g. "Knowledge") might
            ' not exist yet in an existing user's appsettings.json, but
            ' ApplyUiValuesToSettings always writes every known module's
            ' toggle into _settings.Modules.Enabled. Capturing the baseline
            ' before that normalization runs would mean the dictionary
            ' gains a key between the baseline snapshot and the comparison
            ' snapshot, showing "unsaved changes" on a window nothing was
            ' actually changed on. Calling the same normalization before
            ' both snapshots makes the comparison apples-to-apples
            ' regardless of what's already on disk.
            ApplyUiValuesToSettings()
            _lastSavedJson = Text.Json.JsonSerializer.Serialize(_settings)
        End Sub

        ''' <summary>Loads every stored memory fresh from the database, split into the two sections the review screen shows.</summary>
        Private Sub LoadMemories()
            PinnedMemories.Clear()
            SearchableMemories.Clear()

            For Each item In _memoryService.ListAllFacts()
                Dim row As New MemoryItemViewModel With {
                    .Id = item.Id,
                    .Content = item.Content,
                    .IsPinned = item.IsPinned,
                    .CreatedAt = item.CreatedAt
                }
                If item.IsPinned Then
                    PinnedMemories.Add(row)
                Else
                    SearchableMemories.Add(row)
                End If
            Next
        End Sub

        Private Async Function SaveMemoryEditAsync(row As MemoryItemViewModel) As Task
            If row Is Nothing OrElse String.IsNullOrWhiteSpace(row.Content) Then Return

            Dim item As New MemoryItem With {
                .Id = row.Id,
                .Content = row.Content,
                .IsPinned = row.IsPinned,
                .CreatedAt = row.CreatedAt
            }
            Await _memoryService.UpdateFactAsync(item, row.Content, CancellationToken.None)
            MemoriesStatusText = "Saved."
        End Function

        Private Sub DeleteMemory(row As MemoryItemViewModel)
            If row Is Nothing Then Return

            _memoryService.DeleteFact(row.Id)
            PinnedMemories.Remove(row)
            SearchableMemories.Remove(row)
            MemoriesStatusText = "Deleted."
        End Sub

        ''' <summary>Loads every knowledge base and its sources fresh from the database.</summary>
        Private Sub LoadKnowledgeBases()
            KnowledgeBases.Clear()
            For Each kb In _knowledgeRepository.ListKnowledgeBases()
                Dim kbViewModel As New KnowledgeBaseViewModel With {
                    .Id = kb.Id,
                    .Name = kb.Name,
                    .Description = kb.Description
                }
                RefreshSources(kbViewModel)
                KnowledgeBases.Add(kbViewModel)
            Next
        End Sub

        Private Sub RefreshSources(kbViewModel As KnowledgeBaseViewModel)
            kbViewModel.Sources.Clear()
            For Each source In _knowledgeRepository.ListSources(kbViewModel.Id)
                kbViewModel.Sources.Add(New KnowledgeSourceViewModel With {
                    .Id = source.Id,
                    .KnowledgeBaseId = source.KnowledgeBaseId,
                    .SourceType = source.SourceType,
                    .DisplayName = source.DisplayName,
                    .Status = source.Status,
                    .ErrorMessage = source.ErrorMessage,
                    .ChunkCount = source.ChunkCount
                })
            Next
        End Sub

        Private Sub CreateKnowledgeBase()
            If String.IsNullOrWhiteSpace(NewKnowledgeBaseName) Then Return

            Dim id = _knowledgeRepository.CreateKnowledgeBase(NewKnowledgeBaseName, NewKnowledgeBaseDescription)
            KnowledgeBases.Add(New KnowledgeBaseViewModel With {
                .Id = id,
                .Name = NewKnowledgeBaseName,
                .Description = NewKnowledgeBaseDescription
            })
            NewKnowledgeBaseName = ""
            NewKnowledgeBaseDescription = ""
        End Sub

        Private Sub DeleteKnowledgeBase(kbViewModel As KnowledgeBaseViewModel)
            If kbViewModel Is Nothing Then Return

            Dim confirmed = ConfirmDialog.Show(
                $"Delete the knowledge base ""{kbViewModel.Name}"" and everything in it? This can't be undone.",
                "Delete knowledge base",
                confirmText:="Delete",
                isDestructive:=True)
            If Not confirmed Then Return

            _knowledgeRepository.DeleteKnowledgeBase(kbViewModel.Id)
            KnowledgeBases.Remove(kbViewModel)
        End Sub

        ''' <summary>Multi-select - each chosen file becomes its own source, ingested one at a time so the sources list can show each one's progress/result individually.</summary>
        Private Async Function AddFileSourceAsync(kbViewModel As KnowledgeBaseViewModel) As Task
            If kbViewModel Is Nothing Then Return

            Dim dialog As New Microsoft.Win32.OpenFileDialog With {
                .Title = "Add files to this knowledge base",
                .Multiselect = True,
                .Filter = "Supported files (*.pdf;*.docx;*.xlsx;*.txt;*.md)|*.pdf;*.docx;*.xlsx;*.txt;*.md|All files (*.*)|*.*"
            }
            If dialog.ShowDialog() <> True Then Return

            kbViewModel.IsBusy = True
            kbViewModel.StatusText = "Ingesting..."
            Try
                For Each filePath In dialog.FileNames
                    Await _knowledgeIngestionService.AddFileSourceAsync(kbViewModel.Id, filePath, CancellationToken.None)
                Next
                RefreshSources(kbViewModel)
                kbViewModel.StatusText = ""
            Finally
                kbViewModel.IsBusy = False
            End Try
        End Function

        Private Async Function AddFolderSourceAsync(kbViewModel As KnowledgeBaseViewModel) As Task
            If kbViewModel Is Nothing Then Return

            Dim dialog As New Microsoft.Win32.OpenFolderDialog With {
                .Title = "Add a folder to this knowledge base"
            }
            If dialog.ShowDialog() <> True Then Return

            kbViewModel.IsBusy = True
            kbViewModel.StatusText = "Ingesting folder - this can take a while for a lot of files..."
            Try
                Await _knowledgeIngestionService.AddFolderSourceAsync(kbViewModel.Id, dialog.FolderName, CancellationToken.None)
                RefreshSources(kbViewModel)
                kbViewModel.StatusText = ""
            Finally
                kbViewModel.IsBusy = False
            End Try
        End Function

        Private Async Function AddWebsiteSourceAsync(kbViewModel As KnowledgeBaseViewModel) As Task
            If kbViewModel Is Nothing OrElse String.IsNullOrWhiteSpace(kbViewModel.PendingWebsiteUrl) Then Return

            Dim url = kbViewModel.PendingWebsiteUrl.Trim()
            kbViewModel.IsBusy = True
            kbViewModel.StatusText = "Fetching and ingesting..."
            Try
                Await _knowledgeIngestionService.AddWebsiteSourceAsync(kbViewModel.Id, url, CancellationToken.None)
                RefreshSources(kbViewModel)
                kbViewModel.PendingWebsiteUrl = ""
                kbViewModel.StatusText = ""
            Finally
                kbViewModel.IsBusy = False
            End Try
        End Function

        Private Async Function AddTextSourceAsync(kbViewModel As KnowledgeBaseViewModel) As Task
            If kbViewModel Is Nothing OrElse String.IsNullOrWhiteSpace(kbViewModel.PendingTextContent) Then Return

            Dim displayName = If(String.IsNullOrWhiteSpace(kbViewModel.PendingTextName), "Pasted text", kbViewModel.PendingTextName.Trim())
            kbViewModel.IsBusy = True
            kbViewModel.StatusText = "Ingesting..."
            Try
                Await _knowledgeIngestionService.AddTextSourceAsync(kbViewModel.Id, kbViewModel.PendingTextContent, displayName, CancellationToken.None)
                RefreshSources(kbViewModel)
                kbViewModel.PendingTextName = ""
                kbViewModel.PendingTextContent = ""
                kbViewModel.StatusText = ""
            Finally
                kbViewModel.IsBusy = False
            End Try
        End Function

        Private Sub RemoveSource(sourceViewModel As KnowledgeSourceViewModel)
            If sourceViewModel Is Nothing Then Return

            _knowledgeRepository.DeleteSource(sourceViewModel.Id)
            Dim kbViewModel = KnowledgeBases.FirstOrDefault(Function(kb) kb.Id = sourceViewModel.KnowledgeBaseId)
            kbViewModel?.Sources.Remove(sourceViewModel)
        End Sub

        ''' <summary>Matches the standard `{"mcpServers": {"name": {"command", "args", "env"}}}` shape most MCP servers' own docs give a ready-to-paste snippet of - see PendingMcpServerConfigJson.</summary>
        Private Class McpConfigFileJson
            Public Property mcpServers As Dictionary(Of String, McpServerConfigJson)
        End Class

        Private Class McpServerConfigJson
            Public Property command As String
            Public Property args As List(Of String)
            Public Property env As Dictionary(Of String, String)
        End Class

        Private Sub LoadMcpServers()
            McpServers.Clear()
            For Each serverConfig In _mcpServerRepository.ListServers()
                McpServers.Add(ToMcpServerViewModel(serverConfig))
            Next
        End Sub

        Private Function ToMcpServerViewModel(serverConfig As McpServerConfig) As McpServerViewModel
            Dim row As New McpServerViewModel With {
                .Id = serverConfig.Id,
                .Name = serverConfig.Name,
                .Command = serverConfig.Command,
                .ArgumentsDisplay = String.Join(" ", serverConfig.Arguments),
                .EnvironmentVariableNamesDisplay = String.Join(", ", serverConfig.EnvironmentVariables.Keys),
                .IsEnabled = serverConfig.IsEnabled
            }
            row.OnIsEnabledChanged = Sub(isEnabled) _mcpServerRepository.SetEnabled(row.Id, isEnabled)
            Return row
        End Function

        ''' <summary>
        ''' Parses PendingMcpServerConfigJson and creates every server listed
        ''' under "mcpServers" - the real, primary way a server gets added
        ''' (see that property's comment), not just an alternative to the
        ''' manual fields below.
        ''' </summary>
        ''' <summary>
        ''' Awaits ModuleRegistry.RestartModuleAsync right after the server
        ''' list actually changes - a targeted MCP reconnect, not bundled
        ''' into the general Save button, which would make every save wait
        ''' on an MCP reconnect regardless of whether MCP config changed. A
        ''' no-op if MCP isn't currently enabled/running, so this is safe to
        ''' always call.
        ''' </summary>
        Private Async Function ImportMcpServerConfigJsonAsync() As Task
            If String.IsNullOrWhiteSpace(PendingMcpServerConfigJson) Then Return

            Try
                Dim options As New Text.Json.JsonSerializerOptions With {.PropertyNameCaseInsensitive = True}
                Dim parsed = Text.Json.JsonSerializer.Deserialize(Of McpConfigFileJson)(PendingMcpServerConfigJson, options)
                If parsed Is Nothing OrElse parsed.mcpServers Is Nothing OrElse parsed.mcpServers.Count = 0 Then
                    McpStatusText = "No servers found - expected a top-level ""mcpServers"" object."
                    Return
                End If

                Dim importedNames As New List(Of String)
                For Each entry In parsed.mcpServers
                    Dim serverJson = entry.Value
                    If String.IsNullOrWhiteSpace(serverJson.command) Then Continue For

                    Dim id = _mcpServerRepository.CreateServer(
                        entry.Key,
                        serverJson.command,
                        If(serverJson.args, New List(Of String)),
                        If(serverJson.env, New Dictionary(Of String, String)))
                    McpServers.Add(ToMcpServerViewModel(_mcpServerRepository.ListServers().First(Function(s) s.Id = id)))
                    importedNames.Add(entry.Key)
                Next

                PendingMcpServerConfigJson = ""
                If importedNames.Count > 0 Then
                    Await _moduleRegistry.RestartModuleAsync("Mcp", CancellationToken.None)
                    McpStatusText = $"Imported and connected: {String.Join(", ", importedNames)}."
                Else
                    McpStatusText = "Nothing valid to import - each server needs at least a ""command""."
                End If
            Catch ex As Exception
                McpStatusText = $"Couldn't parse that JSON: {ex.Message}"
            End Try
        End Function

        Private Async Function CreateMcpServerAsync() As Task
            If String.IsNullOrWhiteSpace(NewMcpServerName) OrElse String.IsNullOrWhiteSpace(NewMcpServerCommand) Then Return

            Dim arguments = NewMcpServerArguments.Split(" "c, StringSplitOptions.RemoveEmptyEntries).ToList()

            Dim environmentVariables As New Dictionary(Of String, String)
            For Each line In NewMcpServerEnvironmentVariables.Split({Environment.NewLine, vbLf}, StringSplitOptions.RemoveEmptyEntries)
                Dim separatorIndex = line.IndexOf("="c)
                If separatorIndex > 0 Then
                    environmentVariables(line.Substring(0, separatorIndex).Trim()) = line.Substring(separatorIndex + 1).Trim()
                End If
            Next

            Dim id = _mcpServerRepository.CreateServer(NewMcpServerName, NewMcpServerCommand, arguments, environmentVariables)
            McpServers.Add(ToMcpServerViewModel(_mcpServerRepository.ListServers().First(Function(s) s.Id = id)))

            NewMcpServerName = ""
            NewMcpServerCommand = ""
            NewMcpServerArguments = ""
            NewMcpServerEnvironmentVariables = ""

            Await _moduleRegistry.RestartModuleAsync("Mcp", CancellationToken.None)
            McpStatusText = "Added and connected."
        End Function

        Private Async Function DeleteMcpServerAsync(serverViewModel As McpServerViewModel) As Task
            If serverViewModel Is Nothing Then Return

            _mcpServerRepository.DeleteServer(serverViewModel.Id)
            McpServers.Remove(serverViewModel)

            Await _moduleRegistry.RestartModuleAsync("Mcp", CancellationToken.None)
        End Function

        Private Sub LoadScheduledJobs()
            ScheduledJobs.Clear()
            For Each job In _schedulerRepository.ListJobs()
                ScheduledJobs.Add(ToScheduledJobViewModel(job))
            Next
        End Sub

        Private Function ToScheduledJobViewModel(job As ScheduledJob) As ScheduledJobViewModel
            Dim row As New ScheduledJobViewModel With {
                .Id = job.Id,
                .Name = job.Name,
                .CronExpressionText = job.CronExpression,
                .Prompt = job.Prompt,
                .LastRunDisplay = If(job.LastRunAt.HasValue, $"{job.LastRunAt.Value.ToLocalTime():dd/MM/yyyy HH:mm}", "Never run"),
                .NextRunDisplay = If(job.NextRunAt.HasValue, $"{job.NextRunAt.Value.ToLocalTime():dd/MM/yyyy HH:mm}", "Never (invalid schedule)"),
                .IsEnabled = job.IsEnabled,
                .ModelId = If(String.IsNullOrEmpty(job.ModelId), UseCurrentModelSentinel, job.ModelId)
            }
            row.OnIsEnabledChanged = Sub(isEnabled) _schedulerRepository.SetEnabled(row.Id, isEnabled)
            Return row
        End Function

        ''' <summary>
        ''' Same downloaded-models call as MainViewModel.InitializeAsync, just
        ''' for the Scheduler section's model pickers - fire-and-forget since
        ''' the Settings window's constructor can't be async, and the
        ''' pickers are fine appearing a moment after the window itself
        ''' (same "(use current model)" sentinel is already there as the
        ''' default, so nothing is broken while this is still in flight).
        ''' CancellationToken.None here is fine (not the unbounded-background-
        ''' call bug documented elsewhere) - this is a single short-lived GET
        ''' tied to the Settings window's own lifetime, not a model generation
        ''' that could run away.
        ''' </summary>
        Private Async Sub LoadAvailableModelsFireAndForget()
            Try
                Dim models = Await _managementClient.ListDownloadedModelsAsync(CancellationToken.None)
                For Each model In models
                    AvailableModels.Add(model.Id)
                Next
            Catch
                ' Best-effort - Lemonade might not be reachable when Settings
                ' is opened; the sentinel default still works fine either way.
            End Try
        End Sub

        ''' <summary>Same cron-parsing/next-occurrence logic as SchedulerModule.ScheduleJob (the chat-tool path) - both need to validate and compute the same way, this is just the GUI entry point onto the same SchedulerRepository.</summary>
        Private Sub CreateScheduledJob()
            If String.IsNullOrWhiteSpace(NewJobName) OrElse String.IsNullOrWhiteSpace(NewJobCronExpressionText) OrElse String.IsNullOrWhiteSpace(NewJobPrompt) Then Return

            Try
                ' TimeZoneInfo.Local - cron fields are interpreted on the
                ' local clock, not UTC (see SchedulerModule.ScheduleJob's
                ' comment for why that distinction matters).
                Dim parsed = Cronos.CronExpression.Parse(NewJobCronExpressionText)
                Dim nextRunAt = parsed.GetNextOccurrence(DateTime.UtcNow, TimeZoneInfo.Local, inclusive:=False)

                Dim modelId = If(NewJobModelId = UseCurrentModelSentinel, Nothing, NewJobModelId)
                Dim id = _schedulerRepository.CreateJob(NewJobName, NewJobCronExpressionText, NewJobPrompt, nextRunAt, modelId)
                ScheduledJobs.Add(ToScheduledJobViewModel(_schedulerRepository.ListJobs().First(Function(j) j.Id = id)))

                NewJobName = ""
                NewJobPrompt = ""
                NewJobModelId = UseCurrentModelSentinel
                RebuildCronFromBuilder() ' re-syncs the cron text field with whatever the builder dropdowns still show, rather than leaving it blank while they still say e.g. "Daily 09:00"
                SchedulerStatusText = If(nextRunAt.HasValue,
                    $"Added - next run at {nextRunAt.Value.ToLocalTime():dd/MM/yyyy HH:mm}.",
                    "Added, but that cron expression has no future occurrence - it will never actually run. Double-check it.")
            Catch ex As Exception
                SchedulerStatusText = $"Invalid cron expression: {ex.Message}"
            End Try
        End Sub

        Private Sub DeleteScheduledJob(jobViewModel As ScheduledJobViewModel)
            If jobViewModel Is Nothing Then Return

            _schedulerRepository.DeleteJob(jobViewModel.Id)
            ScheduledJobs.Remove(jobViewModel)
        End Sub

        ''' <summary>Persists an edited job's Name/CronExpressionText/Prompt (the row's own TextBoxes are already TwoWay-bound, this just needs to recompute NextRunAt and write it) - see CreateScheduledJob's comment on TimeZoneInfo.Local for why the cron re-parse matters, not just a formality.</summary>
        Private Sub SaveScheduledJobEdit(jobViewModel As ScheduledJobViewModel)
            If jobViewModel Is Nothing OrElse String.IsNullOrWhiteSpace(jobViewModel.Name) OrElse
               String.IsNullOrWhiteSpace(jobViewModel.CronExpressionText) OrElse String.IsNullOrWhiteSpace(jobViewModel.Prompt) Then Return

            Try
                Dim parsed = Cronos.CronExpression.Parse(jobViewModel.CronExpressionText)
                Dim nextRunAt = parsed.GetNextOccurrence(DateTime.UtcNow, TimeZoneInfo.Local, inclusive:=False)

                Dim modelId = If(jobViewModel.ModelId = UseCurrentModelSentinel, Nothing, jobViewModel.ModelId)
                _schedulerRepository.UpdateJob(jobViewModel.Id, jobViewModel.Name, jobViewModel.CronExpressionText, jobViewModel.Prompt, nextRunAt, modelId)
                ' Simplest way to refresh NextRunDisplay/LastRunDisplay (plain
                ' properties, not SetProperty-backed - a Save is infrequent
                ' enough that reloading the whole list fresh from the
                ' database isn't worth a more surgical single-row update).
                LoadScheduledJobs()
                SchedulerStatusText = "Saved."
            Catch ex As Exception
                SchedulerStatusText = $"Invalid cron expression: {ex.Message}"
            End Try
        End Sub

        ''' <summary>
        ''' Browsing picks an absolute path, same as BrowseForRoot - typing a
        ''' relative one (the portable default, "data") is still supported,
        ''' just not offered as a browse option since there's no folder
        ''' picker UI for "relative to wherever this app happens to be".
        ''' </summary>
        Private Sub BrowseForDataFolder()
            Dim dialog As New Microsoft.Win32.OpenFolderDialog With {
                .Title = "Choose a folder to store the app's database and memories in"
            }
            If dialog.ShowDialog() = True Then
                DataFolder = dialog.FolderName
            End If
        End Sub

        Private Sub AddRoot()
            If Not String.IsNullOrWhiteSpace(NewRootPath) AndAlso Not AllowedRoots.Contains(NewRootPath) Then
                AllowedRoots.Add(NewRootPath)
                NewRootPath = ""
            End If
        End Sub

        Private Sub BrowseForRoot()
            Dim dialog As New Microsoft.Win32.OpenFolderDialog With {
                .Title = "Choose a folder to allow file system access to"
            }
            If dialog.ShowDialog() = True AndAlso Not AllowedRoots.Contains(dialog.FolderName) Then
                AllowedRoots.Add(dialog.FolderName)
            End If
        End Sub

        Private Sub RemoveRoot(root As String)
            AllowedRoots.Remove(root)
        End Sub

        ''' <summary>
        ''' Async now (was a plain Sub) so it can await
        ''' ModuleRegistry.ReconcileEnabledModulesAsync - the core of the
        ''' Settings live-reload work. Most fields take effect immediately;
        ''' Lemonade.BaseUrl/ChatModel/EmbeddingModel and AppData.DataFolder
        ''' are the deliberate exception (baked into already-built singleton
        ''' objects at construction - the chat client, the SQLite
        ''' connection - not read fresh per use the way everything else is),
        ''' so only those still prompt for a restart, and only when one of
        ''' them actually changed.
        ''' </summary>
        ''' <summary>
        ''' The one place UI-bound properties get copied into _settings -
        ''' shared by SaveAsync and HasUnsavedChanges (the Closing-warning
        ''' dirty check), so there's exactly one spot to update when a new
        ''' Settings field is added, not two that could drift out of sync.
        ''' </summary>
        Private Sub ApplyUiValuesToSettings()
            _settings.AppData.DataFolder = DataFolder

            ' The Slider's own Minimum/Maximum (10-95) already keep this in
            ' range from the UI side - this clamp is just a defensive
            ' fallback (e.g. a hand-edited appsettings.json), not something
            ' normal use through Settings can actually trigger anymore. A
            ' A text box + save-time-only clamp would be confusing (accepts
            ' anything while typing, silently corrects on save) - the Slider
            ' avoids that entirely.
            _settings.Memory.CompactionTriggerPercent = Math.Clamp(CompactionTriggerPercent, 10, 95)
            ' A blank prompt would leave the model with no instructions at
            ' all every turn - falls back to the built-in default rather
            ' than silently persisting an empty string.
            _settings.Assistant.SystemPrompt = If(String.IsNullOrWhiteSpace(SystemPrompt), New AssistantSettings().SystemPrompt, SystemPrompt)

            ' Persona fields are all genuinely optional (empty is a valid,
            ' meaningful "not set" state, unlike SystemPrompt above), so no
            ' fallback-to-default is needed here.
            _settings.Persona.Identity = PersonaIdentity
            _settings.Persona.AboutUser = PersonaAboutUser
            _settings.Persona.Tone = If(String.IsNullOrWhiteSpace(PersonaTone), "Default", PersonaTone)
            _settings.Persona.Verbosity = If(String.IsNullOrWhiteSpace(PersonaVerbosity), "Default", PersonaVerbosity)
            _settings.Persona.EmojiUsage = If(String.IsNullOrWhiteSpace(PersonaEmojiUsage), "Default", PersonaEmojiUsage)
            _settings.Persona.CustomInstructions = PersonaCustomInstructions

            _settings.Backup.IntervalDays = If(BackupIntervalDays > 0, BackupIntervalDays, New BackupSettings().IntervalDays)
            _settings.Backup.BackupFolder = If(String.IsNullOrWhiteSpace(BackupFolder), New BackupSettings().BackupFolder, BackupFolder)
            _settings.Backup.KeepCount = If(BackupKeepCount > 0, BackupKeepCount, New BackupSettings().KeepCount)

            _settings.Lemonade.BaseUrl = LemonadeBaseUrl
            _settings.Lemonade.ChatModel = LemonadeChatModel
            _settings.Lemonade.EmbeddingModel = LemonadeEmbeddingModel
            _settings.Lemonade.ApiKey = LemonadeApiKey
            _settings.ImageGen.ModelId = ImageGenModelId
            _settings.ImageGen.DefaultWidth = ImageGenDefaultWidth
            _settings.ImageGen.DefaultHeight = ImageGenDefaultHeight
            _settings.ImageGen.Steps = ImageGenSteps
            _settings.ImageGen.CfgScale = ImageGenCfgScale
            _settings.ImageGen.Seed = ImageGenSeed
            _settings.WebSearch.SearXngBaseUrl = SearXngBaseUrl
            _settings.WebSearch.Engine = If(String.IsNullOrWhiteSpace(WebEngine), "SearXNG", WebEngine)
            _settings.WebSearch.JinaApiKey = JinaApiKey
            _settings.WebSearch.TavilyApiKey = TavilyApiKey
            _settings.WebSearch.FirecrawlApiKey = FirecrawlApiKey
            _settings.FileSystem.AllowedRoots = AllowedRoots.ToList()

            For Each toggle In ModuleToggles
                _settings.Modules.Enabled(toggle.ConfigKey) = toggle.IsEnabled
            Next
        End Sub

        ''' <summary>
        ''' Checked from SettingsWindow's Closing event - warns before
        ''' silently discarding an edited-but-not-saved field if the window
        ''' is closed via its native X rather than clicking Save. Applies
        ''' current UI values into _settings (the
        ''' same thing SaveAsync itself does - harmless here since nothing
        ''' downstream of _settings is touched unless Save is actually
        ''' clicked) and compares its serialized JSON against
        ''' _lastSavedJson, captured right after loading and again after
        ''' every successful save.
        ''' </summary>
        Public Function HasUnsavedChanges() As Boolean
            ApplyUiValuesToSettings()
            Return Text.Json.JsonSerializer.Serialize(_settings) <> _lastSavedJson
        End Function

        Private Async Function SaveAsync() As Task
            ApplyUiValuesToSettings()

            _store.Save(_settings)
            _lastSavedJson = Text.Json.JsonSerializer.Serialize(_settings)

            ' Compared against _liveSettings BEFORE CopyInto below overwrites
            ' it - see this method's own comment for why these four fields
            ' specifically still need a real restart.
            Dim restartRequired =
                _settings.Lemonade.BaseUrl <> _liveSettings.Lemonade.BaseUrl OrElse
                _settings.Lemonade.ChatModel <> _liveSettings.Lemonade.ChatModel OrElse
                _settings.Lemonade.EmbeddingModel <> _liveSettings.Lemonade.EmbeddingModel OrElse
                _settings.AppData.DataFolder <> _liveSettings.AppData.DataFolder

            _settings.CopyInto(_liveSettings)
            Await _moduleRegistry.ReconcileEnabledModulesAsync(CancellationToken.None)

            If restartRequired Then
                Dim confirmed = ConfirmDialog.Show(
                    "The Lemonade base URL/chat/embedding model or data folder changed - those only take effect after restarting the app. Everything else is already applied. Restart now?",
                    "Restart needed",
                    confirmText:="Restart now",
                    cancelText:="Later")

                If confirmed Then
                    Process.Start(New ProcessStartInfo(Environment.ProcessPath) With {.UseShellExecute = True})
                    Application.Current.Shutdown()
                Else
                    ShowStatusTextTemporarily("Saved - restart the app when you're ready for the Lemonade/data-folder changes to take effect. Everything else is already applied.")
                End If
            Else
                ShowStatusTextTemporarily("Settings saved and applied - no restart needed.")
            End If
        End Function

    End Class

End Namespace
