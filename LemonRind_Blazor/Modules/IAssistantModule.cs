using Microsoft.Extensions.AI;

namespace LemonRindBlazor.Modules;

/// <summary>
/// Contract every feature (web search, MCP, RAG, scheduler, etc.) will
/// implement, once ported. This is the mechanism behind "features
/// enable/disable independently": a disabled module simply never gets asked
/// for its tools and never runs its background work.
///
/// Ported directly from the VB.NET/WPF LemonRind app's
/// Modules\IAssistantModule.vb - the interface every
/// capability module implements.
/// </summary>
public interface IAssistantModule
{
    /// <summary>Display name shown in the Settings screen's module list (e.g. "Web search").</summary>
    string Name { get; }

    /// <summary>Stable key used in appsettings.json's Modules.Enabled dictionary (e.g. "WebSearch").</summary>
    string ConfigKey { get; }

    /// <summary>One-line description shown under the name in Settings.</summary>
    string Description { get; }

    /// <summary>Whether this module is currently active, backed by user config.</summary>
    bool IsEnabled { get; }

    /// <summary>Runs once at app startup, only if IsEnabled is true.</summary>
    Task OnStartupAsync(CancellationToken cancellationToken);

    /// <summary>Runs once at app shutdown, only if the module was started.</summary>
    Task OnShutdownAsync(CancellationToken cancellationToken);

    /// <summary>The tools this module contributes to the agent's tool set. Only called for enabled modules.</summary>
    IEnumerable<AITool> GetTools();
}
