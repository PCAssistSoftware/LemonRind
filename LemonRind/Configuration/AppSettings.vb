Namespace Configuration

    ''' <summary>
    ''' Strongly-typed view of appsettings.json's "Lemonade" section.
    ''' Kept separate from LemonadeSettings/AppDataSettings below so each config
    ''' section can be bound and passed around independently, e.g. only the
    ''' Lemonade settings get handed to the chat client factory.
    ''' </summary>
    Public Class LemonadeSettings
        Public Property BaseUrl As String = "http://localhost:13305/v1/"
        Public Property ChatModel As String = ""
        Public Property EmbeddingModel As String = ""

        ''' <summary>
        ''' Optional - Lemonade doesn't currently enforce an API key (any
        ''' non-empty string works), but its docs recommend providing one
        ''' anyway. Every client that talks to Lemonade (chat/embedding/image
        ''' via the OpenAI SDK, LemonadeManagementClient's REST calls,
        ''' LemonadeLogClient's WebSocket) falls back to the harmless
        ''' "lemonade" placeholder string when this is blank. Same
        ''' restart-required scope as BaseUrl/ChatModel/EmbeddingModel above -
        ''' all four are baked into already-constructed client objects at DI
        ''' startup, so changing any of them only takes effect after a
        ''' restart.
        ''' </summary>
        Public Property ApiKey As String = ""
    End Class

    ''' <summary>
    ''' Text-to-image generation config, kept in its own section (not just
    ''' fields on LemonadeSettings) matching the dedicated-settings-section-
    ''' per-module pattern used by Knowledge Bases/MCP Servers/Scheduler.
    ''' Steps is deliberately NOT auto-switched by model name - a hardcoded
    ''' per-model lookup table would silently do the wrong thing for any
    ''' model not in the table, so this is one field the user updates by
    ''' hand when switching image models, same as ChatModel/EmbeddingModel.
    ''' Width/Height are never applied silently - ImageGenModule always asks
    ''' the user to confirm/override the size via ImageSizeDialog before
    ''' generating; these two fields are only that dialog's pre-filled
    ''' defaults.
    ''' </summary>
    Public Class ImageGenSettings
        Public Property ModelId As String = ""
        Public Property DefaultWidth As Integer = 512
        Public Property DefaultHeight As Integer = 512
        Public Property Steps As Integer = 4
        Public Property CfgScale As Double = 1.0
        Public Property Seed As Integer = -1
    End Class

    Public Class AppDataSettings
        ''' <summary>
        ''' Relative by default ("data") so the app is portable - copy the
        ''' whole publish folder anywhere and its database/memories move with
        ''' it, with no per-machine path to reconcile. Can also be set to an
        ''' absolute path (e.g. a shared network location) via Settings.
        ''' </summary>
        Public Property DataFolder As String = "data"

        ''' <summary>
        ''' DataFolder as stored may be relative (resolved against the app's
        ''' own folder, AppContext.BaseDirectory - not the process's current
        ''' working directory, which can differ depending on how the app was
        ''' launched) or an absolute/%ENVVAR%-style path, which is returned
        ''' as-is (after expansion) for e.g. a shared network location.
        ''' </summary>
        Public Function ResolvedDataFolder() As String
            Dim expanded = Environment.ExpandEnvironmentVariables(DataFolder)
            If IO.Path.IsPathRooted(expanded) Then
                Return expanded
            End If
            Return IO.Path.Combine(AppContext.BaseDirectory, expanded)
        End Function
    End Class

    ''' <summary>
    ''' Engine is a single swappable backend choice shared by both search_web
    ''' (WebSearchModule) and read_webpage (WebReaderModule), rather than two
    ''' independent settings - trading some mixing flexibility for one field
    ''' to reason about, and avoiding near-duplicate tools per provider that
    ''' would cost tokens every turn regardless of which is actually active.
    ''' The tool names the model sees never change; only which real backend
    ''' answers them does - same principle as the Lemonade URL being
    ''' swappable without the model needing to know or care. "SearXNG"
    ''' (search_web) + Direct fetch (read_webpage) is the default. Each
    ''' module maps this value to its own implementation and falls back to
    ''' its own default for a value it doesn't support (e.g. a search-only
    ''' engine falls back to Direct fetch for reading) - see
    ''' WebSearchModule.SearchAsync/WebReaderModule.ReadPageAsync.
    ''' </summary>
    Public Class WebSearchSettings
        Public Property SearXngBaseUrl As String = "http://localhost:8888/"
        Public Property Engine As String = "SearXNG"

        ''' <summary>
        ''' Shared by both capabilities when Engine="Jina" - Jina's Reader
        ''' works keyless too, just at a lower rate limit, but Search
        ''' requires a key (returns 401 without one).
        ''' </summary>
        Public Property JinaApiKey As String = ""

        ''' <summary>
        ''' Shared by both capabilities when Engine="Tavily" - unlike Jina,
        ''' Tavily requires a key for both Search and Extract; there's no
        ''' keyless tier.
        ''' </summary>
        Public Property TavilyApiKey As String = ""

        ''' <summary>
        ''' Shared by both capabilities when Engine="Firecrawl" - a key is
        ''' required for Scrape/Search/Crawl alike; there's no keyless tier.
        ''' </summary>
        Public Property FirecrawlApiKey As String = ""
    End Class

    ''' <summary>
    ''' A pure operational safety net, not an AI capability - zero tools (see
    ''' AutoBackupModule), but still implements IAssistantModule for the same
    ''' enable/disable + live-reload plumbing every other module gets. Off by
    ''' default via the shared Modules.Enabled dictionary, same as every
    ''' other module, since it has a real disk-usage cost and shouldn't
    ''' start writing zip files without the user opting in.
    ''' </summary>
    Public Class BackupSettings
        Public Property IntervalDays As Integer = 7

        ''' <summary>
        ''' Deliberately a sibling of AppDataSettings.DataFolder, never
        ''' inside it - backing up DataFolder while the backup output also
        ''' lives inside DataFolder would mean every new zip re-includes
        ''' every previous zip, compounding forever.
        ''' </summary>
        Public Property BackupFolder As String = "data_backups"

        ''' <summary>
        ''' An unbounded number of timestamped zips is a real, easy-to-hit
        ''' problem for anything left running for months - oldest backups
        ''' beyond this count are deleted right after a new one succeeds.
        ''' </summary>
        Public Property KeepCount As Integer = 5

        ''' <summary>Same relative/absolute resolution rule as AppDataSettings.ResolvedDataFolder - see its own comment.</summary>
        Public Function ResolvedBackupFolder() As String
            Dim expanded = Environment.ExpandEnvironmentVariables(BackupFolder)
            If IO.Path.IsPathRooted(expanded) Then
                Return expanded
            End If
            Return IO.Path.Combine(AppContext.BaseDirectory, expanded)
        End Function
    End Class

    Public Class FileSystemSettings
        ''' <summary>
        ''' Folders the file system module is allowed to touch - everything
        ''' else is refused before any read/write is attempted. Empty by
        ''' default, so the module has no effective access until a folder is
        ''' explicitly configured.
        ''' </summary>
        Public Property AllowedRoots As New List(Of String)()
    End Class

    ''' <summary>
    ''' Short-term memory / context compaction - the threshold at which
    ''' older messages get summarized once context usage crosses this
    ''' percentage of the model's real limit. Stored as a 0-100 percentage
    ''' (not a raw 0.0-1.0 fraction) since that's what actually goes in the
    ''' Settings screen's text field - friendlier to type/read than a
    ''' decimal.
    ''' </summary>
    Public Class MemorySettings
        Public Property CompactionTriggerPercent As Integer = 75
    End Class

    ''' <summary>
    ''' The base system prompt. A dedicated settings class (not folded into
    ''' Memory or Lemonade) since it doesn't cleanly belong to either - a
    ''' standalone piece of assistant behaviour, leaving room for related
    ''' settings later without overloading an unrelated group.
    ''' </summary>
    Public Class AssistantSettings
        Public Property SystemPrompt As String = "You are a helpful local AI assistant running on the user's own machine."
    End Class

    ''' <summary>
    ''' Lets the assistant build a standing sense of who it's talking to, not
    ''' just isolated recalled facts. Three pieces, each optional and
    ''' independently omitted from the prompt when unset (see
    ''' MainViewModel.BuildSystemPromptText/BuildWritingStyleText):
    ''' - Identity: who the assistant is (name/personality), separate from
    '''   AssistantSettings.SystemPrompt, which stays focused on operating
    '''   instructions.
    ''' - AboutUser: who the user is - included unconditionally on every
    '''   turn (unlike Memory's similarity-gated semantic recall), so it
    '''   can't fail to surface just because nothing recent triggered it.
    ''' - Tone/Verbosity/EmojiUsage/CustomInstructions: compact structured
    '''   writing-style controls.
    ''' Settings-authored only - not AI-self-editable via a tool call. A
    ''' future mode where the assistant updates its own Identity/writing
    ''' style via a tool call would slot in naturally as a new AIFunction on
    ''' a Persona-aware module calling the same setters below.
    ''' </summary>
    Public Class PersonaSettings
        Public Property Identity As String = ""
        Public Property AboutUser As String = ""
        Public Property Tone As String = "Default"
        Public Property Verbosity As String = "Default"
        Public Property EmojiUsage As String = "Default"
        Public Property CustomInstructions As String = ""
    End Class

    ''' <summary>
    ''' Which optional modules are enabled. Keyed by module name (e.g.
    ''' "WebSearch") rather than one bool property per module, so adding a
    ''' new module doesn't need a schema change here, just a new key. Every
    ''' module reads this dictionary live (not a value cached once at
    ''' construction), so toggling a module here takes effect immediately -
    ''' though actually starting/stopping its real work (MCP connections,
    ''' the Scheduler poll timer) still needs
    ''' ModuleRegistry.ReconcileEnabledModulesAsync to run, which
    ''' SettingsViewModel.Save() does automatically.
    ''' </summary>
    Public Class ModuleSettings
        Public Property Enabled As New Dictionary(Of String, Boolean)(StringComparer.OrdinalIgnoreCase)
    End Class

    ''' <summary>
    ''' Root settings object bound from appsettings.json. Registered as a
    ''' singleton in DI so any service can ask for exactly the section it needs.
    ''' </summary>
    Public Class AppSettings
        Public Property Lemonade As New LemonadeSettings()
        Public Property AppData As New AppDataSettings()
        Public Property WebSearch As New WebSearchSettings()
        Public Property FileSystem As New FileSystemSettings()
        Public Property Memory As New MemorySettings()
        Public Property Modules As New ModuleSettings()
        Public Property ImageGen As New ImageGenSettings()
        Public Property Assistant As New AssistantSettings()
        Public Property Persona As New PersonaSettings()
        Public Property Backup As New BackupSettings()

        ''' <summary>
        ''' Copies every field's value from Me into target's SAME nested
        ''' objects (target.Lemonade.BaseUrl = Me.Lemonade.BaseUrl, not
        ''' target.Lemonade = Me.Lemonade). Services that were handed a
        ''' reference to target (or one of its nested objects, e.g.
        ''' LemonadeSettings) at DI-construction time keep that same
        ''' reference forever; only a property-by-property copy into the
        ''' object they're already holding is visible to them - a wholesale
        ''' reassignment of the property here would just detach target's own
        ''' property from what those services still point at. Called from
        ''' SettingsViewModel.Save() to push its own edited, freshly-loaded
        ''' copy into the app's real, shared AppSettings singleton.
        ''' Some fields (Lemonade.BaseUrl/ChatModel/EmbeddingModel,
        ''' AppData.DataFolder) are copied here like everything else, but
        ''' don't take effect until a restart regardless - they're baked
        ''' into already-built singleton objects (the chat client, the
        ''' SQLite connection) at construction time, not read fresh per use
        ''' like everything else in this class. Update this method whenever
        ''' a new settings field is added, or it will silently stay
        ''' restart-only.
        ''' </summary>
        Public Sub CopyInto(target As AppSettings)
            target.Lemonade.BaseUrl = Lemonade.BaseUrl
            target.Lemonade.ChatModel = Lemonade.ChatModel
            target.Lemonade.EmbeddingModel = Lemonade.EmbeddingModel
            target.Lemonade.ApiKey = Lemonade.ApiKey

            target.AppData.DataFolder = AppData.DataFolder

            target.WebSearch.SearXngBaseUrl = WebSearch.SearXngBaseUrl
            target.WebSearch.Engine = WebSearch.Engine
            target.WebSearch.JinaApiKey = WebSearch.JinaApiKey
            target.WebSearch.TavilyApiKey = WebSearch.TavilyApiKey
            target.WebSearch.FirecrawlApiKey = WebSearch.FirecrawlApiKey

            target.FileSystem.AllowedRoots = FileSystem.AllowedRoots.ToList()

            target.Memory.CompactionTriggerPercent = Memory.CompactionTriggerPercent

            target.Modules.Enabled = New Dictionary(Of String, Boolean)(Modules.Enabled, StringComparer.OrdinalIgnoreCase)

            target.ImageGen.ModelId = ImageGen.ModelId
            target.ImageGen.DefaultWidth = ImageGen.DefaultWidth
            target.ImageGen.DefaultHeight = ImageGen.DefaultHeight
            target.ImageGen.Steps = ImageGen.Steps
            target.ImageGen.CfgScale = ImageGen.CfgScale
            target.ImageGen.Seed = ImageGen.Seed

            target.Assistant.SystemPrompt = Assistant.SystemPrompt

            target.Persona.Identity = Persona.Identity
            target.Persona.AboutUser = Persona.AboutUser
            target.Persona.Tone = Persona.Tone
            target.Persona.Verbosity = Persona.Verbosity
            target.Persona.EmojiUsage = Persona.EmojiUsage
            target.Persona.CustomInstructions = Persona.CustomInstructions

            target.Backup.IntervalDays = Backup.IntervalDays
            target.Backup.BackupFolder = Backup.BackupFolder
            target.Backup.KeepCount = Backup.KeepCount
        End Sub
    End Class

End Namespace
