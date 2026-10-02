Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Extensions.AI

Namespace Modules

    ''' <summary>
    ''' Contract every feature (web search, MCP, RAG, scheduler, etc.) implements.
    ''' This is the mechanism behind "features enable/disable independently":
    ''' a disabled module simply never gets asked for its tools and never runs
    ''' its background work, so the rest of the app doesn't need to know it
    ''' exists.
    ''' </summary>
    Public Interface IAssistantModule

        ''' <summary>Display name shown in the Settings screen's module list (e.g. "Web search").</summary>
        ReadOnly Property Name As String

        ''' <summary>
        ''' Stable key used in appsettings.json's Modules.Enabled dictionary
        ''' (e.g. "WebSearch") - kept separate from the display Name so the
        ''' Settings screen can toggle a module's config without hardcoding a
        ''' name-to-key mapping anywhere outside the module itself.
        ''' </summary>
        ReadOnly Property ConfigKey As String

        ''' <summary>One-line description shown under the name in Settings.</summary>
        ReadOnly Property Description As String

        ''' <summary>
        ''' Whether this module is currently active. Backed by user config
        ''' (a toggle in Settings), not hardcoded - checked by the registry
        ''' before calling any other member.
        ''' </summary>
        ReadOnly Property IsEnabled As Boolean

        ''' <summary>
        ''' Runs once at app startup, only if IsEnabled is true. Use this for
        ''' anything a module needs to set up before its tools can be used
        ''' (e.g. connecting to an MCP server, opening a DB connection).
        ''' </summary>
        Function OnStartupAsync(cancellationToken As CancellationToken) As Task

        ''' <summary>Runs once at app shutdown, only if the module was started.</summary>
        Function OnShutdownAsync(cancellationToken As CancellationToken) As Task

        ''' <summary>
        ''' The tools this module contributes to the agent's tool set. Only
        ''' called for enabled modules - a disabled module's tools are never
        ''' seen by the model at all, which is the actual enable/disable
        ''' mechanism, not just a UI toggle that does nothing.
        ''' </summary>
        Function GetTools() As IEnumerable(Of AITool)

    End Interface

End Namespace
