using System.Diagnostics;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using LemonRindBlazor.Configuration;
using LemonRindBlazor.Modules;
using Microsoft.Extensions.Logging;

namespace LemonRindBlazor.Mcp;

/// <summary>
/// MCP client support - connects to every enabled configured MCP server
/// (McpServerRepository) over stdio at startup and exposes their tools to
/// the model, alongside this app's own hand-built ones.
/// ModelContextProtocol.Client.McpClientTool already inherits
/// Microsoft.Extensions.AI.AIFunction directly, with its Name/Description/
/// JsonSchema all sourced from the MCP server's real tool schema and a
/// working InvokeCoreAsync - it's already a ready-to-use AITool, no wrapper
/// needed (confirmed via reflection against the actually-installed package
/// version by the real WPF app before this was ever built there).
///
/// Ported from the VB.NET/WPF LemonRind app's Mcp\McpModule.vb.
/// ModuleRegistry.ReconcileEnabledModulesAsync (targeted per-module restart
/// from Settings, without a full app relaunch) is used here so that
/// adding/enabling a server in Settings takes effect live: the module reconnects and picks up
/// the new server list without an app restart.
/// </summary>
public class McpModule(AppSettings settings, McpServerRepository repository, ILogger<McpModule> logger, ILoggerFactory loggerFactory) : IAssistantModule
{
    private readonly ModuleSettings _moduleSettings = settings.Modules;

    // One connected McpClient per successfully-started server - kept for
    // clean shutdown (each is IAsyncDisposable) and because its tools need
    // to stay reachable for the lifetime of the connection, not just at the
    // moment they were listed. _connectedProcessIds runs parallel to it -
    // see OnShutdownAsync's own comment for why this exists at all.
    private readonly List<McpClient> _connectedClients = [];
    private readonly List<int?> _connectedProcessIds = [];
    private readonly List<ProcessJob?> _connectedJobs = [];
    private readonly List<AITool> _tools = [];

    public string Name => "MCP servers";
    public string ConfigKey => "Mcp";
    public string Description => "Connects to external MCP servers (e.g. Postmark for email) and exposes their tools.";

    public bool IsEnabled => _moduleSettings.Enabled.GetValueOrDefault(ConfigKey, false);

    /// <summary>
    /// Connects to every individually-enabled configured server in turn.
    /// One server failing to start (bad command, missing npx package, wrong
    /// env vars, ...) doesn't stop the others from connecting or block the
    /// app's own startup - caught and skipped per-server, so one broken
    /// server degrades gracefully rather than taking the app down.
    /// </summary>
    public async Task OnStartupAsync(CancellationToken cancellationToken)
    {
        foreach (var serverConfig in repository.ListServers().Where(s => s.IsEnabled))
        {
            ProcessJob? job = null;
            try
            {
                var transportOptions = new StdioClientTransportOptions
                {
                    Name = serverConfig.Name,
                    Command = serverConfig.Command,
                    Arguments = serverConfig.Arguments,
                };
                transportOptions.EnvironmentVariables = new Dictionary<string, string?>(
                    serverConfig.EnvironmentVariables.Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value)));

                // Root cause of the ~70s MCP connect delay (found 2026-10-02): with
                // HTTPS scanning on (AVG here), TLS to the npm registry is re-signed
                // by the AV's own root CA, which node doesn't trust by default - npx
                // retries/backs off for ~70s before giving up on the registry and
                // using its cache. AV tools inject NODE_EXTRA_CA_CERTS into some
                // shells but not into processes launched from elsewhere (e.g. an IDE or
                // app launcher), which is why it was fast from a shell and slow
                // otherwise. Telling node to trust the Windows certificate store
                // (where the AV installs that root) fixes it without AV-specific code.
                if (!transportOptions.EnvironmentVariables.ContainsKey("NODE_USE_SYSTEM_CA")
                    && Environment.GetEnvironmentVariable("NODE_USE_SYSTEM_CA") is null)
                {
                    transportOptions.EnvironmentVariables["NODE_USE_SYSTEM_CA"] = "1";
                }

                // PidCapturingLoggerFactory wraps the real
                // one just to intercept the SDK's own "started server process
                // with PID {ProcessId}" log line via its structured state, not
                // string-parsing - see that class's own doc comment for why
                // this is needed at all. Must be passed to StdioClientTransport's
                // OWN constructor, not just McpClient.CreateAsync's: StdioClientTransport stores its own independent
                // ILoggerFactory? field (defaulting to null when omitted here,
                // which silently routes its internal logging - including this
                // exact PID line - to NullLogger.Instance regardless of whatever
                // factory CreateAsync itself is given separately).
                // The PID callback fires right after Process.Start(), before npx
                // has spawned anything - assigning it to a Job Object then means
                // every descendant joins the job too, so shutdown can kill the
                // whole tree even once the parent-PID chain is broken (see
                // ProcessJob / OnShutdownAsync).
                job = ProcessJob.Create();
                var capturingFactory = new PidCapturingLoggerFactory(loggerFactory, out var capturedProcessId, pid => job?.TryAssign(pid));
                var transport = new StdioClientTransport(transportOptions, capturingFactory);
                // The SDK's default initialize-handshake timeout is too short for a server
                // whose command needs a cold npx/npm resolve (or just runs on a loaded
                // machine) - e.g. @modelcontextprotocol/server-everything timed out under
                // the default timeout but connected fine once given longer.
                var clientOptions = new McpClientOptions { InitializationTimeout = TimeSpan.FromSeconds(90) };
                var client = await McpClient.CreateAsync(transport, clientOptions, capturingFactory, cancellationToken);
                _connectedClients.Add(client);
                _connectedProcessIds.Add(capturedProcessId());
                _connectedJobs.Add(job);

                var serverTools = await client.ListToolsAsync(cancellationToken: cancellationToken);
                _tools.AddRange(serverTools);
            }
            catch (Exception ex)
            {
                // A failed/timed-out connect can still leave a spawned tree
                // behind - killing the job cleans it up. (No-op once the job
                // was handed to _connectedJobs, which owns it from then on.)
                if (job is not null && !_connectedJobs.Contains(job))
                {
                    job.Terminate();
                    job.Dispose();
                }
                // Best-effort - see this method's summary. A server that
                // can't connect just contributes no tools this run.
                logger.LogWarning(ex, "MCP server '{ServerName}' failed to connect", serverConfig.Name);
            }
        }
    }

    /// <summary>
    /// Disposes every connected client, then - Windows only, best-effort -
    /// kills the real OS process tree the SDK spawned for it.
    ///
    /// A Settings save that reconnects Mcp was
    /// observed taking 70-90+ seconds, and Get-Process during a single
    /// save showed FIVE separate cmd.exe/node.exe process pairs spawned
    /// ~30-40s apart, with every completed one still running well after the
    /// save finished - a genuine leak (reproduced with a standalone test:
    /// `npx -y @modelcontextprotocol/server-everything` on Windows
    /// spawns TWO separate node.exe processes (npx's own bootstrap, which
    /// then spawns a second, separate node.exe to actually run the resolved
    /// package), nested under a cmd.exe wrapper the SDK itself adds
    /// (StdioClientTransport routes any non-cmd.exe Command through
    /// "cmd.exe /c" on Windows). The SDK's own DisposeAsync already calls
    /// Process.Kill(entireProcessTree: true) on that cmd.exe wrapper (see
    /// ModelContextProtocol.Core's ProcessHelper.KillTree) - but a direct,
    /// isolated test proved that call only reaches the first node.exe (npx's
    /// own bootstrap); the second, actual long-running server process
    /// survives as an orphan every time. A second isolated test proved
    /// `taskkill /F /T /PID` DOES correctly reach every process in the same
    /// tree (confirmed: 0 node.exe processes left afterward, vs 1 left by
    /// the SDK's own native kill) - taskkill's own tree-walk evidently
    /// handles this specific multi-hop npx spawn shape more reliably than
    /// .NET's native entireProcessTree implementation does on Windows.
    /// </summary>
    public async Task OnShutdownAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < _connectedClients.Count; i++)
        {
            // Primary cleanup: kill everything that ever joined the job. Done
            // BEFORE DisposeAsync so the SDK's own graceful-shutdown wait on an
            // already-dead server doesn't add its multi-second delay to every
            // Settings save. The taskkill below stays as a backstop for when
            // job assignment failed.
            _connectedJobs[i]?.Terminate();
            await _connectedClients[i].DisposeAsync();
            _connectedJobs[i]?.Dispose();

            var processId = _connectedProcessIds[i];
            if (processId is int pid && OperatingSystem.IsWindows())
            {
                try
                {
                    using var taskkill = Process.Start(new ProcessStartInfo
                    {
                        FileName = "taskkill",
                        ArgumentList = { "/F", "/T", "/PID", pid.ToString() },
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    });
                    if (taskkill is not null) await taskkill.WaitForExitAsync(cancellationToken);
                }
                catch
                {
                    // Best-effort - the SDK's own kill may have already
                    // succeeded (taskkill then just reports "not found",
                    // which is fine, not an error worth surfacing), and a
                    // leftover orphan process is a resource-usage nuisance,
                    // never something that should block a Settings save or
                    // app shutdown.
                }
            }
        }
        _connectedClients.Clear();
        _connectedProcessIds.Clear();
        _connectedJobs.Clear();
        _tools.Clear();
    }

    public IEnumerable<AITool> GetTools() => _tools;

    /// <summary>
    /// Wraps a real ILoggerFactory just to intercept ModelContextProtocol's
    /// own "{EndpointName} started server process with PID {ProcessId}."
    /// log line (Information level, logged right after Process.Start()
    /// succeeds in StdioClientTransport) and capture the PID from its
    /// structured log state - the SDK has no public API exposing the
    /// spawned process's ID otherwise. Matches on the log message's own
    /// EventId.Name ("LogTransportProcessStarted", a stable identifier the
    /// SDK's own LoggerMessage.Define generates) rather than parsing the
    /// formatted message text, so this survives wording changes across SDK
    /// versions. Every other log call is passed through to the real logger
    /// unchanged - this never suppresses or alters normal MCP logging.
    /// </summary>
    private sealed class PidCapturingLoggerFactory : ILoggerFactory
    {
        private readonly ILoggerFactory _inner;
        private int? _capturedProcessId;

        private readonly Action<int>? _onProcessStarted;

        public PidCapturingLoggerFactory(ILoggerFactory inner, out Func<int?> capturedProcessId, Action<int>? onProcessStarted = null)
        {
            _inner = inner;
            _onProcessStarted = onProcessStarted;
            capturedProcessId = () => _capturedProcessId;
        }

        public ILogger CreateLogger(string categoryName) => new PidCapturingLogger(_inner.CreateLogger(categoryName), pid =>
        {
            _capturedProcessId = pid;
            _onProcessStarted?.Invoke(pid);
        });

        public void AddProvider(ILoggerProvider provider) => _inner.AddProvider(provider);

        public void Dispose() { }
    }

    private sealed class PidCapturingLogger(ILogger inner, Action<int> onProcessIdCaptured) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

        // Deliberately always true, independent of the inner logger's own
        // configured level - the SDK's [LoggerMessage]-generated code checks
        // IsEnabled before calling Log at all, so returning the real,
        // possibly-filtered-out answer here would mean PID capture silently
        // stops working the moment Information-level logging for this
        // category is ever turned down. Log() below still only forwards to
        // the real sink when the real level would actually allow it,
        // so this never changes what ends up in /logs or anywhere else.
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (eventId.Name == "LogTransportProcessStarted" && state is IReadOnlyList<KeyValuePair<string, object>> pairs)
            {
                var processIdPair = pairs.FirstOrDefault(p => p.Key == "ProcessId");
                if (processIdPair.Value is int pid) onProcessIdCaptured(pid);
            }
            if (inner.IsEnabled(logLevel)) inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
