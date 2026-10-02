using Microsoft.Extensions.AI;
using LemonRindBlazor.Backup;
using LemonRindBlazor.Components;
using LemonRindBlazor.Configuration;
using LemonRindBlazor.Data;
using LemonRindBlazor.Knowledge;
using LemonRindBlazor.Mcp;
using LemonRindBlazor.Memories;
using LemonRindBlazor.Modules;
using LemonRindBlazor.Modules.Coder;
using LemonRindBlazor.Modules.FileSystem;
using LemonRindBlazor.Modules.ImageGen;
using LemonRindBlazor.Modules.Firecrawl;
using LemonRindBlazor.Modules.Jina;
using LemonRindBlazor.Modules.Tavily;
using LemonRindBlazor.Modules.WebReader;
using LemonRindBlazor.Modules.WebSearch;
using LemonRindBlazor.Scheduler;
using LemonRindBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Composition root: every service is registered here (AppSettingsStore/
// AppSettings, AppDatabase, the Lemonade clients, the repositories, then each
// module). Uses builder.Services directly - Blazor's WebApplicationBuilder
// exposes the same IServiceCollection as the Avalonia edition's
// Host.CreateApplicationBuilder(), so the registrations match that app's
// composition root (App.axaml.cs).
var settingsStore = new AppSettingsStore();
var settings = settingsStore.LoadFresh();
builder.Services.AddSingleton(settingsStore);
builder.Services.AddSingleton(settings);

builder.Services.AddSingleton<AppDatabase>();
builder.Services.AddSingleton<LemonadeChatClientFactory>();
builder.Services.AddSingleton<IChatClient>(sp =>
    sp.GetRequiredService<LemonadeChatClientFactory>().CreateChatClient());
builder.Services.AddSingleton<LemonadeManagementClient>();
builder.Services.AddSingleton<LemonadeLogClient>();
builder.Services.AddSingleton<ChatSessionRepository>();

// Long-term memory (pinned + semantic) and short-term memory
// (compaction). EmbeddingClientFactory/VectorMath are shared infrastructure -
// used by long-term memory and Knowledge Bases.
builder.Services.AddSingleton<EmbeddingClientFactory>();
builder.Services.AddSingleton<MemoryRepository>();
builder.Services.AddSingleton<MemoryService>();
builder.Services.AddSingleton<ConversationCompactionService>();

// Web search/reader/file system tools. SearXngClient/WebReaderClient/PathSandbox/
// FileWriteApprovalStore are plain singletons; each module wraps one (or,
// for FileSystemModule, two) of them into a tool set. FileWriteApprovalGate
// is the Blazor replacement for the desktop editions' modal
// FileWriteApprovalDialog - see its own doc comment. Jina/Tavily/Firecrawl
// are registered too, since WebSearchModule/WebReaderModule dispatch to
// them when WebSearchSettings.Engine selects one.
builder.Services.AddSingleton<SearXngClient>();
builder.Services.AddSingleton<SsrfSafeHttpFetcher>();
builder.Services.AddSingleton<WebReaderClient>();
builder.Services.AddSingleton<PathSandbox>();
builder.Services.AddSingleton<FileWriteApprovalStore>();
builder.Services.AddSingleton<FileWriteApprovalGate>();
builder.Services.AddSingleton<JinaClient>();
builder.Services.AddSingleton<TavilyClient>();
builder.Services.AddSingleton<FirecrawlService>();

builder.Services.AddSingleton<IAssistantModule, WebSearchModule>();
builder.Services.AddSingleton<IAssistantModule, WebReaderModule>();
builder.Services.AddSingleton<IAssistantModule, FileSystemModule>();

// RAG (Knowledge Bases) + one-off file analysis.
// KnowledgeModule contributes no tools of its own; it gates the Knowledge
// Bases feature. FileTextExtractor is static, no DI registration needed.
builder.Services.AddSingleton<KnowledgeRepository>();
builder.Services.AddSingleton<KnowledgeIngestionService>();
builder.Services.AddSingleton<KnowledgeService>();
builder.Services.AddSingleton<IAssistantModule, KnowledgeModule>();

// MCP client support. McpServerRepository is a plain singleton;
// McpModule connects to every individually-enabled configured server over
// stdio at startup. ModuleRegistry.ReconcileEnabledModulesAsync
// special-cases "Mcp" to reconnect on every Settings save, so
// adding/enabling a server takes effect live, not after a restart.
builder.Services.AddSingleton<McpServerRepository>();
builder.Services.AddSingleton<IAssistantModule, McpModule>();

// Scheduler. Func<ModuleRegistry>, not ModuleRegistry directly -
// ModuleRegistry's own constructor needs every IAssistantModule (including
// SchedulerModule), SchedulerModule needs ScheduledJobRunner, and
// ScheduledJobRunner needs ModuleRegistry - a genuine cycle the DI container
// refuses to resolve directly. A lazy factory breaks it - the real
// ModuleRegistry only needs to exist by the time GetEnabledTools() is
// actually called (a job firing), not at ScheduledJobRunner's own
// construction.
builder.Services.AddSingleton<Func<ModuleRegistry>>(sp => () => sp.GetRequiredService<ModuleRegistry>());
builder.Services.AddSingleton<SchedulerRepository>();
builder.Services.AddSingleton<SchedulerNotifier>();
builder.Services.AddSingleton<ScheduledJobRunner>();
builder.Services.AddSingleton<IAssistantModule, SchedulerModule>();

// Coder module. Reuses the PathSandbox/FileWriteApprovalStore/
// FileWriteApprovalGate singletons (no separate sandbox config) - CodeSyntaxValidator/
// CodeOutlineGenerator are static, no DI registration needed.
builder.Services.AddSingleton<IAssistantModule, CoderModule>();

// Image generation. ImageSizeGate is the Blazor
// replacement for the desktop editions' modal ImageSizeDialog, same
// TaskCompletionSource-backed "ask and wait" shape as FileWriteApprovalGate.
builder.Services.AddSingleton<ImageClientFactory>();
builder.Services.AddSingleton<ImageGenerationService>();
builder.Services.AddSingleton<ImageSizeGate>();
builder.Services.AddSingleton<IAssistantModule, ImageGenModule>();

// Image attachment (vision input). ImageAttachmentService isn't
// an IAssistantModule (no tool to expose) - it's called directly from
// Home.razor's own attach flow, same as FileTextExtractor's static methods
// for document attachment.
builder.Services.AddSingleton<ImageAttachmentService>();

// Auto-backup. AutoBackupModule (periodic whole-data-folder zip,
// pruned to KeepCount) has zero AI tools of its own; it is pure background
// infrastructure, registered as an IAssistantModule so it starts with the
// app and appears in Settings' Modules list.
builder.Services.AddSingleton<IAssistantModule, AutoBackupModule>();

builder.Services.AddSingleton<ModuleRegistry>();

var app = builder.Build();

app.Services.GetRequiredService<AppDatabase>().EnsureCreated();

// Fire-and-forget, not awaited - module startup (most notably McpModule's
// stdio handshake with each configured server, which can legitimately take
// 10-90+ seconds, see its own InitializationTimeout comment) used to block
// Kestrel from accepting any connection at all until every module finished,
// so the page itself couldn't even start loading until MCP was done. Kestrel
// now starts immediately below; a chat request that arrives before module
// startup finishes just sees fewer tools until GetEnabledTools() picks up
// whatever's connected so far - the same graceful "partial capability while
// still starting" a user would expect, not a hard dependency.
_ = app.Services.GetRequiredService<ModuleRegistry>().StartEnabledModulesAsync(CancellationToken.None);

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();

// Serves generated images to the browser. ImageGenerationService
// saves to a real local path and returns a file:// URI (Home.razor extracts
// the local path back out of the model's own Markdown image link) - but a
// file:// URI isn't loadable by a page served over http://, so the same
// folder is also exposed over HTTP here, under its own dedicated prefix
// (not wwwroot - these are generated user data, not build-time static assets).
var generatedImagesPath = Path.Combine(settings.AppData.ResolvedDataFolder(), "Workspace", "GeneratedImages");
Directory.CreateDirectory(generatedImagesPath);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(generatedImagesPath),
    RequestPath = "/generated-images",
});

// Same reasoning as GeneratedImages above: a chat bubble's
// thumbnail needs an http:// URL, not the file:// path ImageAttachmentService
// deals in internally.
var attachedImagesPath = Path.Combine(settings.AppData.ResolvedDataFolder(), "Workspace", "AttachedImages");
Directory.CreateDirectory(attachedImagesPath);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(attachedImagesPath),
    RequestPath = "/attached-images",
});

// Export a session to Markdown. A plain minimal API endpoint, not
// a Razor page/component - there's no UI here, just a file response, and a
// browser's own native download handling (triggered by a plain <a download>
// link in Home.razor's sidebar) is a better fit for a web app than trying to
// replicate the desktop apps' own native "Save As" dialog. Reads straight
// from the DB (ChatSessionRepository), not the live in-memory _messages a
// particular open circuit holds - any sidebar session can be exported, not
// just whichever one happens to be currently open in that browser tab.
app.MapGet("/export/{sessionId}", (string sessionId, ChatSessionRepository repository) =>
{
    var title = repository.ListSessions().FirstOrDefault(s => s.Id == sessionId)?.Title ?? "Untitled";
    var markdown = ChatMarkdownExporter.BuildMarkdown(title, repository.LoadMessages(sessionId));
    var safeFileName = string.Concat(title.Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim();
    var fileName = (string.IsNullOrEmpty(safeFileName) ? "chat" : safeFileName) + ".md";
    return Results.File(System.Text.Encoding.UTF8.GetBytes(markdown), "text/markdown", fileName);
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
