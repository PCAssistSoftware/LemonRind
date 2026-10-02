using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LemonRindBlazor.Modules;

/// <summary>
/// Central place that knows about every IAssistantModule in the app.
/// Registered as a singleton in DI; modules get added via constructor
/// injection (DI hands in "everything registered as IAssistantModule").
///
/// Ported from the VB.NET/WPF LemonRind app's Modules\ModuleRegistry.vb.
/// ReconcileEnabledModulesAsync applies live Settings toggles; IsToolFromModule
/// supports auto-tagging.
/// </summary>
public class ModuleRegistry(IEnumerable<IAssistantModule> modules, ILogger<ModuleRegistry> logger)
{
    private readonly IReadOnlyList<IAssistantModule> _modules = modules.ToList();

    // Tracks which modules currently have real startup work in effect -
    // shared between the initial app-launch StartEnabledModulesAsync and
    // every later ReconcileEnabledModulesAsync call, so a Settings save
    // knows what actually changed since the module last (re)started.
    private readonly HashSet<string> _startedModules = [];

    /// <summary>All registered modules, enabled or not - e.g. for a future Settings screen's toggle list.</summary>
    public IReadOnlyList<IAssistantModule> AllModules => _modules;

    /// <summary>Runs OnStartupAsync for every currently-enabled module, one at a time so one failure can't block the rest.</summary>
    public async Task StartEnabledModulesAsync(CancellationToken cancellationToken)
    {
        foreach (var module in _modules.Where(m => m.IsEnabled))
        {
            try
            {
                await module.OnStartupAsync(cancellationToken);
                _startedModules.Add(module.ConfigKey);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Module {ModuleName} failed to start", module.Name);
            }
        }
    }

    /// <summary>Runs OnShutdownAsync for every currently-enabled module.</summary>
    public async Task StopEnabledModulesAsync(CancellationToken cancellationToken)
    {
        foreach (var module in _modules.Where(m => m.IsEnabled))
        {
            try
            {
                await module.OnShutdownAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Module {ModuleName} failed to shut down", module.Name);
            }
        }
    }

    /// <summary>Every tool contributed by every currently-enabled module - what actually gets attached to a chat request.</summary>
    public List<AITool> GetEnabledTools()
    {
        return [.. _modules.Where(m => m.IsEnabled).SelectMany(m => m.GetTools())];
    }

    /// <summary>
    /// Called after every Settings save (not just when a toggle actually
    /// changed) so a module whose real startup work depends on config it
    /// can't see just from IsEnabled - most notably Mcp, whose server list
    /// lives in a separate DB table Settings writes to immediately,
    /// independent of the module's own on/off flag - still picks up
    /// changes without needing its own toggle flipped. Free for every
    /// module with no-op startup/shutdown (most of them); real work only
    /// for the ones actually still enabled with a live connection/timer to
    /// refresh.
    ///
    /// Ported in spirit from the VB.NET/WPF LemonRind app's own
    /// ModuleRegistry.ReconcileEnabledModulesAsync - narrowed the same way
    /// that app's own later fix did (only genuinely-changed or
    /// config-sensitive modules restart, not a blanket pass over every
    /// enabled module on every save).
    /// </summary>
    public async Task ReconcileEnabledModulesAsync(CancellationToken cancellationToken)
    {
        foreach (var module in _modules)
        {
            var wasStarted = _startedModules.Contains(module.ConfigKey);
            var isEnabled = module.IsEnabled;

            try
            {
                if (isEnabled && !wasStarted)
                {
                    await module.OnStartupAsync(cancellationToken);
                    _startedModules.Add(module.ConfigKey);
                }
                else if (!isEnabled && wasStarted)
                {
                    await module.OnShutdownAsync(cancellationToken);
                    _startedModules.Remove(module.ConfigKey);
                }
                else if (isEnabled && wasStarted && module.ConfigKey == "Mcp")
                {
                    // MCP servers are added/removed via Settings as plain,
                    // immediate DB writes - the module's own IsEnabled flag
                    // never changes when that happens, so this is the one
                    // case that needs a restart even with no state change,
                    // to actually reconnect and pick up the new server list.
                    await module.OnShutdownAsync(cancellationToken);
                    await module.OnStartupAsync(cancellationToken);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Module {ModuleName} failed to reconcile", module.Name);
            }
        }
    }

    /// <summary>
    /// Whether the given tool name is currently contributed by the
    /// specified enabled module - MainViewModel.SendAsync's auto-tagging
    /// needs this specifically for MCP, whose tool names come straight from
    /// whatever each connected server calls them, with no fixed naming
    /// convention to pattern-match against the way Coder's own consistent
    /// "code_" prefix allows.
    /// </summary>
    public bool IsToolFromModule(string toolName, string configKey)
    {
        var targetModule = _modules.FirstOrDefault(m => m.ConfigKey == configKey && m.IsEnabled);
        return targetModule is not null && targetModule.GetTools().Any(t => t.Name == toolName);
    }

    /// <summary>
    /// Whether the module with this ConfigKey is both registered
    /// and currently enabled. For a module that contributes no tools at all
    /// (Knowledge Bases) there's no other way to check this from outside -
    /// GetEnabledTools()/IsToolFromModule both only look at tool lists.
    /// </summary>
    public bool IsModuleEnabled(string configKey) => _modules.Any(m => m.ConfigKey == configKey && m.IsEnabled);
}
