using Microsoft.Extensions.AI;
using LemonRindBlazor.Configuration;
using LemonRindBlazor.Modules;

namespace LemonRindBlazor.Knowledge;

/// <summary>
/// A toggle for Knowledge Bases: disabling this means the "no knowledge
/// base is attached" boilerplate note never needs injecting into context at
/// all (see MainViewModel.SendAsync's IsKnowledgeModuleEnabled check), and
/// the knowledge-base dropdown hides from the main chat window entirely.
/// Contributes no tools - retrieval is folded directly into the prompt
/// (KnowledgeService), never a callable function.
///
/// Ported from the VB.NET/WPF LemonRind app's Knowledge\KnowledgeModule.vb.
/// </summary>
public class KnowledgeModule(AppSettings settings) : IAssistantModule
{
    private readonly ModuleSettings _moduleSettings = settings.Modules;

    public string Name => "Knowledge Bases";
    public string ConfigKey => "Knowledge";
    public string Description => "Lets a chat attach a knowledge base (files/folders/websites/pasted text you've ingested) so relevant content is folded into each reply.";

    public bool IsEnabled => _moduleSettings.Enabled.GetValueOrDefault(ConfigKey, false);

    public Task OnStartupAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task OnShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public IEnumerable<AITool> GetTools() => [];
}
