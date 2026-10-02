using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using LemonRindAvalonia.Backup;
using LemonRindAvalonia.Configuration;
using LemonRindAvalonia.Data;
using LemonRindAvalonia.Knowledge;
using LemonRindAvalonia.Mcp;
using LemonRindAvalonia.Memories;
using LemonRindAvalonia.Modules.Coder;
using LemonRindAvalonia.Modules.Firecrawl;
using LemonRindAvalonia.Modules.ImageGen;
using LemonRindAvalonia.Modules.Jina;
using LemonRindAvalonia.Modules.Tavily;
using LemonRindAvalonia.Scheduler;
using LemonRindAvalonia.Modules;
using LemonRindAvalonia.Modules.FileSystem;
using LemonRindAvalonia.Modules.WebReader;
using LemonRindAvalonia.Modules.WebSearch;
using LemonRindAvalonia.Services;
using LemonRindAvalonia.ViewModels;
using LemonRindAvalonia.Views;

namespace LemonRindAvalonia;

/// <summary>
/// Composition root. Ported from the VB.NET/WPF LemonRind app's
/// Application.xaml.vb, deliberately only the Stage-1-equivalent DI graph
/// (AppSettingsStore/AppSettings, AppDatabase, LemonadeChatClientFactory/
/// IChatClient, MainViewModel/MainWindow) - every module (WebSearch,
/// FileSystem, Mcp, Scheduler, ...) and the empty ModuleRegistry.AllModules
/// list they'd populate get added here stage by stage, matching that app's
/// own build order, not registered all at once now.
///
/// Same .NET Generic Host used for the DI container (Host.CreateApplication
/// Builder()) - this part isn't WPF-specific at all, it's a plain .NET
/// pattern that works identically under Avalonia's own app lifecycle.
/// </summary>
public partial class App : Application
{
    private IHost? _host;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        var builder = Host.CreateApplicationBuilder();

        // Read through AppSettingsStore - the same reader/writer a future
        // Settings screen will use - so there's exactly one code path that
        // knows where the real settings file lives.
        var settingsStore = new AppSettingsStore();
        var settings = settingsStore.LoadFresh();
        LemonRindAvalonia.Services.ThemeService.Apply(settings.Ui.Theme);
        builder.Services.AddSingleton(settingsStore);
        builder.Services.AddSingleton(settings);

        builder.Services.AddSingleton<AppDatabase>();
        builder.Services.AddSingleton<LemonadeChatClientFactory>();
        builder.Services.AddSingleton<IChatClient>(sp =>
            sp.GetRequiredService<LemonadeChatClientFactory>().CreateChatClient());
        builder.Services.AddSingleton<LemonadeManagementClient>();
        builder.Services.AddSingleton<LemonadeLogClient>();
        builder.Services.AddSingleton<ChatSessionRepository>();

        // Image attachment (downscale/re-encode via Avalonia's
        // own Bitmap, saved under the same Workspace folder convention as
        // ImageGenerationService's own GeneratedImages).
        builder.Services.AddSingleton<ImageAttachmentService>();

        // EmbeddingClientFactory/VectorMath are shared infrastructure - used
        // by long-term memory and Knowledge Bases.
        builder.Services.AddSingleton<EmbeddingClientFactory>();
        builder.Services.AddSingleton<MemoryRepository>();
        builder.Services.AddSingleton<MemoryService>();
        builder.Services.AddSingleton<ConversationCompactionService>();

        // Web search/reader/file system tools, the first real
        // IAssistantModules. SearXngClient/WebReaderClient/PathSandbox/
        // FileWriteApprovalStore are plain singletons; each module wraps
        // one (or, for FileSystemModule, two) of them into a tool set.
        builder.Services.AddSingleton<SearXngClient>();
        builder.Services.AddSingleton<SsrfSafeHttpFetcher>();
        builder.Services.AddSingleton<WebReaderClient>();
        builder.Services.AddSingleton<PathSandbox>();
        builder.Services.AddSingleton<FileWriteApprovalStore>();

        // Jina/Tavily/Firecrawl, the alternate search/read
        // engines search_web/read_webpage can be pointed at via
        // WebSearchSettings.Engine, plus Firecrawl's own additive
        // crawl_website tool.
        builder.Services.AddSingleton<JinaClient>();
        builder.Services.AddSingleton<TavilyClient>();
        builder.Services.AddSingleton<FirecrawlService>();

        builder.Services.AddSingleton<IAssistantModule, WebSearchModule>();
        builder.Services.AddSingleton<IAssistantModule, WebReaderModule>();
        builder.Services.AddSingleton<IAssistantModule, FileSystemModule>();

        // RAG knowledge bases (files/folders/websites/pasted text,
        // chunked/embedded/stored, one attached per chat at a time).
        builder.Services.AddSingleton<KnowledgeRepository>();
        builder.Services.AddSingleton<KnowledgeIngestionService>();
        builder.Services.AddSingleton<KnowledgeService>();
        builder.Services.AddSingleton<IAssistantModule, KnowledgeModule>();

        // MCP client support (stdio-connected external servers,
        // e.g. Postmark for email). McpServerConfig rows are plain DB data,
        // independent of appsettings.json - see SettingsViewModel's own MCP
        // methods.
        builder.Services.AddSingleton<McpServerRepository>();
        builder.Services.AddSingleton<IAssistantModule, McpModule>();

        // Scheduler. Func<ModuleRegistry>, not ModuleRegistry
        // directly - ModuleRegistry's own constructor needs every
        // IAssistantModule (including SchedulerModule), SchedulerModule
        // needs ScheduledJobRunner, and ScheduledJobRunner needs
        // ModuleRegistry - a genuine cycle the DI container refuses to
        // resolve directly. A lazy factory breaks it the same way
        // Func<SettingsWindow> does above - the real ModuleRegistry only
        // needs to exist by the time GetEnabledTools() is actually called
        // (a job firing), not at ScheduledJobRunner's own construction.
        builder.Services.AddSingleton<Func<ModuleRegistry>>(sp => () => sp.GetRequiredService<ModuleRegistry>());
        builder.Services.AddSingleton<SchedulerRepository>();
        builder.Services.AddSingleton<SchedulerNotifier>();
        builder.Services.AddSingleton<ScheduledJobRunner>();
        builder.Services.AddSingleton<IAssistantModule, SchedulerModule>();

        // Coder module. Reuses the PathSandbox/
        // FileWriteApprovalStore singletons - same trust boundary as
        // File system access, not a second folder list to configure.
        builder.Services.AddSingleton<IAssistantModule, CoderModule>();

        // Image generation.
        builder.Services.AddSingleton<ImageClientFactory>();
        builder.Services.AddSingleton<ImageGenerationService>();
        builder.Services.AddSingleton<IAssistantModule, ImageGenModule>();

        // Auto-backup. Pure background infrastructure (zero AI
        // tools), but implemented as an IAssistantModule anyway for the
        // free enable/disable + live-reload plumbing every other module
        // already has.
        builder.Services.AddSingleton<IAssistantModule, AutoBackupModule>();

        builder.Services.AddSingleton<ModuleRegistry>();

        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainWindow>();

        // Transient, not singleton - Settings can be opened more than once
        // per run, and each opening should reload the real current
        // appsettings.json fresh (AppSettingsStore.LoadFresh(), inside
        // SettingsViewModel's constructor), not reuse a stale first-open
        // snapshot. The Func<SettingsWindow> factory is what MainViewModel
        // actually depends on (see its own OpenSettingsCommand) - a plain
        // constructor-injected SettingsWindow would only ever get one,
        // already-built instance.
        builder.Services.AddTransient<SettingsViewModel>();
        builder.Services.AddTransient<SettingsWindow>();
        builder.Services.AddSingleton<Func<SettingsWindow>>(sp => () =>
        {
            var window = sp.GetRequiredService<SettingsWindow>();
            window.DataContext = sp.GetRequiredService<SettingsViewModel>();
            return window;
        });

        // Not modal, so more than one can't accidentally open on
        // top of each other the way a Transient without a factory would
        // risk; Func<LogViewerWindow> matches the same "each open gets a
        // fresh instance" reasoning as Settings above.
        builder.Services.AddTransient<LogViewerWindow>();
        builder.Services.AddSingleton<Func<LogViewerWindow>>(sp => () => sp.GetRequiredService<LogViewerWindow>());

        _host = builder.Build();

        var database = _host.Services.GetRequiredService<AppDatabase>();
        database.EnsureCreated();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.DataContext = _host.Services.GetRequiredService<MainViewModel>();
            desktop.MainWindow = mainWindow;
            desktop.Exit += (_, _) => _host?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Logs any unhandled exception (message + full stack trace) to a file
    /// next to the exe before the process goes down - the only way to see
    /// what failed after it closes, short of attaching a debugger. Same
    /// intent as the VB.NET app's OnDispatcherUnhandledException, using
    /// AppDomain.UnhandledException instead of WPF's own DispatcherUnhandled
    /// Exception event, which Avalonia doesn't have an equivalent of.
    /// </summary>
    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var logPath = Path.Combine(AppContext.BaseDirectory, "crash.log");
        File.AppendAllText(logPath, $"{DateTime.Now:O}{Environment.NewLine}{e.ExceptionObject}{Environment.NewLine}{Environment.NewLine}");
    }
}
