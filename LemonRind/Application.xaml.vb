Imports System.Threading
Imports Microsoft.Extensions.AI
Imports Microsoft.Extensions.DependencyInjection
Imports Microsoft.Extensions.Hosting
Imports LemonRind.Configuration
Imports LemonRind.Data
Imports LemonRind.Knowledge
Imports LemonRind.Mcp
Imports LemonRind.Memories
Imports LemonRind.Modules
Imports LemonRind.Modules.FileSystem
Imports LemonRind.Modules.Firecrawl
Imports LemonRind.Modules.Jina
Imports LemonRind.Modules.Tavily
Imports LemonRind.Modules.WebReader
Imports LemonRind.Modules.WebSearch
Imports LemonRind.Scheduler
Imports LemonRind.Services
Imports LemonRind.ViewModels

''' <summary>
''' Composition root. Everything the app needs gets registered here, once,
''' via the .NET Generic Host. Using AddSingleton(Of T) directly is fine
''' here because each service type is registered exactly once - registering
''' *multiple* named instances of the same type (e.g. more than one AIAgent)
''' would need a keyed registration instead, since plain AddSingleton
''' silently overwrites earlier registrations of the same type with no
''' error.
''' </summary>
Class Application

    Private _host As IHost

    Protected Overrides Async Sub OnStartup(e As StartupEventArgs)
        MyBase.OnStartup(e)

        ' Logs any unhandled UI-thread exception (message + full stack trace)
        ' to a file next to the exe before WPF's default unhandled-exception
        ' behavior proceeds - the only way to see what failed after the
        ' process closes, short of attaching a debugger.
        AddHandler DispatcherUnhandledException, AddressOf OnDispatcherUnhandledException

        Dim builder = Host.CreateApplicationBuilder()

        ' Read through AppSettingsStore - the same reader/writer Settings
        ' itself uses - so there's exactly one code path that knows where
        ' the real settings file lives, and one that creates it fresh on
        ' first run. Constructed directly here (not resolved via DI) since
        ' the container isn't built yet at this point; the same instance is
        ' registered into DI below instead of letting AddSingleton(Of
        ' AppSettingsStore)() build a second, redundant one.
        Dim settingsStore As New AppSettingsStore()
        Dim settings = settingsStore.LoadFresh()
        builder.Services.AddSingleton(settingsStore)
        builder.Services.AddSingleton(settings)

        builder.Services.AddSingleton(Of AppDatabase)()
        builder.Services.AddSingleton(Of ChatSessionRepository)()
        builder.Services.AddSingleton(Of LemonadeChatClientFactory)()
        builder.Services.AddSingleton(Of IChatClient)(
            Function(sp) sp.GetRequiredService(Of LemonadeChatClientFactory)().CreateChatClient())
        builder.Services.AddSingleton(Of LemonadeManagementClient)()

        ' LemonadeLogClient is stateless between calls (each StreamAsync
        ' opens its own WebSocket), safe to share as a singleton;
        ' LogViewerWindow is Transient + a Func factory, same reasoning as
        ' SettingsWindow below (a window that can be opened more than once
        ' per run, and MainViewModel shouldn't be handed the whole
        ' IServiceProvider just to create one).
        builder.Services.AddSingleton(Of LemonadeLogClient)()
        builder.Services.AddTransient(Of LogViewerWindow)()
        builder.Services.AddSingleton(Of Func(Of LogViewerWindow))(
            Function(sp) Function() sp.GetRequiredService(Of LogViewerWindow)())

        ' Each module registers as IAssistantModule (not just its own concrete
        ' type) - DI collects every such registration into the
        ' IEnumerable(Of IAssistantModule) that ModuleRegistry's constructor
        ' asks for, so adding a new module later is just one more line here,
        ' nothing else needs to change.
        ' Shared by both WebSearchModule and WebReaderModule - the swappable
        ' "Engine" backend (see WebSearchSettings.Engine's own comment) -
        ' registered once here since both modules' Settings-driven engine
        ' paths need them.
        builder.Services.AddSingleton(Of JinaClient)()
        builder.Services.AddSingleton(Of TavilyClient)()
        builder.Services.AddSingleton(Of FirecrawlService)()

        builder.Services.AddSingleton(Of SearXngClient)()
        builder.Services.AddSingleton(Of IAssistantModule, WebSearchModule)()

        builder.Services.AddSingleton(Of SsrfSafeHttpFetcher)()
        builder.Services.AddSingleton(Of WebReaderClient)()
        builder.Services.AddSingleton(Of IAssistantModule, WebReaderModule)()

        builder.Services.AddSingleton(Of PathSandbox)()
        builder.Services.AddSingleton(Of FileWriteApprovalStore)()
        builder.Services.AddSingleton(Of IAssistantModule, FileSystemModule)()

        ' The Coder module deliberately reuses PathSandbox/
        ' FileWriteApprovalStore above rather than its own separate sandbox
        ' config, so allowed folders are configured once, not twice.
        builder.Services.AddSingleton(Of IAssistantModule, Coder.CoderModule)()

        ' McpServerRepository is also registered on its own (not just via
        ' the module) since the Settings screen's MCP Servers section
        ' manages server configs directly, independent of whether the
        ' module itself is enabled.
        builder.Services.AddSingleton(Of McpServerRepository)()
        builder.Services.AddSingleton(Of IAssistantModule, McpModule)()

        ' ImageGenerationService is the shared "call Lemonade, save the
        ' file" logic used both by ImageGenModule's generate_image tool (a
        ' real chat model asking for an image mid-conversation) and
        ' MainViewModel's direct path (an image-labeled model picked
        ' straight in the main dropdown, skipping the LLM entirely). Neither
        ' ImageClientFactory nor ImageGenerationService does any work in its
        ' constructor (just stores settings references), so injecting them
        ' directly is safe; the real Lemonade image client (and its
        ' ImageModel validation) is only built lazily the first time a
        ' generation is actually attempted, since every IAssistantModule
        ' gets constructed eagerly by ModuleRegistry regardless of
        ' IsEnabled - anything built eagerly here would run for every user
        ' on every launch, not just someone who enabled this module.
        builder.Services.AddSingleton(Of ImageClientFactory)()
        builder.Services.AddSingleton(Of ImageGenerationService)()
        builder.Services.AddSingleton(Of IAssistantModule, ImageGen.ImageGenModule)()

        ' ImageAttachmentService handles a user-attached image, not a
        ' generated one, but reuses the same "Workspace" folder convention
        ' and needs no lazy-factory trick like ImageClientFactory above,
        ' since it does no Lemonade/network setup in its constructor at all.
        builder.Services.AddSingleton(Of ImageAttachmentService)()

        builder.Services.AddSingleton(Of ModuleRegistry)()
        ' Lazy factory, not ModuleRegistry directly - ScheduledJobRunner
        ' needs to resolve it without creating a circular DI dependency,
        ' same pattern as Func(Of SettingsWindow) below for the same
        ' underlying reason.
        builder.Services.AddSingleton(Of Func(Of ModuleRegistry))(
            Function(sp) Function() sp.GetRequiredService(Of ModuleRegistry)())

        ' EmbeddingClientFactory/VectorMath (a plain Module, not registered)
        ' are shared infrastructure reused by Knowledge Bases too, not
        ' memory-specific.
        builder.Services.AddSingleton(Of EmbeddingClientFactory)()
        builder.Services.AddSingleton(Of MemoryRepository)()
        builder.Services.AddSingleton(Of MemoryService)()

        ' Short-term memory / context compaction.
        builder.Services.AddSingleton(Of ConversationCompactionService)()

        ' Knowledge bases reuse EmbeddingClientFactory (registered above for
        ' Memory) and WebReaderClient (already registered above) rather than
        ' building separate embedding/web-fetch code.
        builder.Services.AddSingleton(Of KnowledgeRepository)()
        builder.Services.AddSingleton(Of KnowledgeIngestionService)()
        builder.Services.AddSingleton(Of KnowledgeService)()
        ' KnowledgeModule is a toggle for existing functionality, not new
        ' functionality of its own; registered as IAssistantModule too so
        ' it gets a real row in Settings' Modules list for free.
        builder.Services.AddSingleton(Of IAssistantModule, Knowledge.KnowledgeModule)()

        ' SchedulerRepository is also registered on its own, same reasoning
        ' as McpServerRepository above (the Settings screen's Scheduler
        ' section manages jobs directly, independent of whether the module
        ' itself is enabled).
        builder.Services.AddSingleton(Of SchedulerRepository)()
        builder.Services.AddSingleton(Of SchedulerNotifier)()
        builder.Services.AddSingleton(Of ScheduledJobRunner)()
        builder.Services.AddSingleton(Of IAssistantModule, SchedulerModule)()

        ' AutoBackupModule is a pure background operational safety net,
        ' zero AI tools, registered as IAssistantModule purely for the free
        ' enable/disable + live-reload plumbing (see its own class summary).
        builder.Services.AddSingleton(Of IAssistantModule, Backup.AutoBackupModule)()

        ' Settings is a dialog the user can open more than once per run, so
        ' it's registered Transient (a fresh instance each time) rather than
        ' Singleton like everything else here - and MainViewModel gets a
        ' factory delegate to create one on demand instead of the whole
        ' IServiceProvider, which would be the DI "Service Locator"
        ' anti-pattern (hiding a class's real dependencies behind a generic
        ' "give me anything" object). AppSettingsStore itself is already
        ' registered above (the same instance used to load settings at
        ' startup), not re-added here.
        builder.Services.AddTransient(Of SettingsViewModel)()
        builder.Services.AddTransient(Of SettingsWindow)()
        builder.Services.AddSingleton(Of Func(Of SettingsWindow))(
            Function(sp) Function() sp.GetRequiredService(Of SettingsWindow)())

        builder.Services.AddSingleton(Of MainViewModel)()
        builder.Services.AddSingleton(Of MainWindow)()

        _host = builder.Build()

        Dim database = _host.Services.GetRequiredService(Of AppDatabase)()
        database.EnsureCreated()

        Dim moduleRegistry = _host.Services.GetRequiredService(Of ModuleRegistry)()
        Await moduleRegistry.StartEnabledModulesAsync(CancellationToken.None)

        Dim mainWindow = _host.Services.GetRequiredService(Of MainWindow)()
        mainWindow.Show()
    End Sub

    Protected Overrides Async Sub OnExit(e As ExitEventArgs)
        If _host IsNot Nothing Then
            Dim moduleRegistry = _host.Services.GetRequiredService(Of ModuleRegistry)()
            Await moduleRegistry.StopEnabledModulesAsync(CancellationToken.None)
            _host.Dispose()
        End If

        MyBase.OnExit(e)
    End Sub

    Private Sub OnDispatcherUnhandledException(sender As Object, e As Threading.DispatcherUnhandledExceptionEventArgs)
        Dim logPath = IO.Path.Combine(AppContext.BaseDirectory, "crash.log")
        IO.File.AppendAllText(logPath, $"{DateTime.Now:O}{Environment.NewLine}{e.Exception}{Environment.NewLine}{Environment.NewLine}")
    End Sub

End Class
