Imports System.Threading
Imports Microsoft.Extensions.AI
Imports ModelContextProtocol.Client
Imports LemonRind.Configuration
Imports LemonRind.Modules

Namespace Mcp

    ''' <summary>
    ''' MCP client support - connects to every enabled configured MCP server
    ''' (McpServerRepository) over stdio at startup and exposes their tools
    ''' to the model, alongside this app's own hand-built ones.
    ''' ModelContextProtocol.Client.McpClientTool already inherits
    ''' Microsoft.Extensions.AI.AIFunction directly, with its
    ''' Name/Description/JsonSchema all sourced from the MCP server's real
    ''' tool schema and a working InvokeCoreAsync - it's already a
    ''' ready-to-use AITool, no wrapper needed.
    ''' </summary>
    Public Class McpModule
        Implements IAssistantModule

        Private ReadOnly _repository As McpServerRepository
        Private ReadOnly _moduleSettings As ModuleSettings

        ' One connected McpClient per successfully-started server - kept for
        ' clean shutdown (each is IAsyncDisposable) and because its tools
        ' need to stay reachable for the lifetime of the connection, not just
        ' at the moment they were listed.
        Private ReadOnly _connectedClients As New List(Of McpClient)
        Private ReadOnly _tools As New List(Of AITool)

        Public Sub New(settings As AppSettings, repository As McpServerRepository)
            _repository = repository
            _moduleSettings = settings.Modules
        End Sub

        Public ReadOnly Property Name As String Implements IAssistantModule.Name
            Get
                Return "MCP servers"
            End Get
        End Property

        Public ReadOnly Property ConfigKey As String Implements IAssistantModule.ConfigKey
            Get
                Return "Mcp"
            End Get
        End Property

        Public ReadOnly Property Description As String Implements IAssistantModule.Description
            Get
                Return "Connects to external MCP servers (e.g. Postmark for email) and exposes their tools."
            End Get
        End Property

        ''' <summary>Read live off the shared Modules.Enabled dictionary each time, not cached at construction - toggling this module in Settings takes effect immediately, no restart needed. Actually (re)connecting to servers still needs OnStartupAsync to run again - see ModuleRegistry.ReconcileEnabledModulesAsync, called from SettingsViewModel.Save().</summary>
        Public ReadOnly Property IsEnabled As Boolean Implements IAssistantModule.IsEnabled
            Get
                Return _moduleSettings.Enabled.GetValueOrDefault(ConfigKey, False)
            End Get
        End Property

        ''' <summary>
        ''' Connects to every individually-enabled configured server in turn.
        ''' One server failing to start (bad command, missing npx package,
        ''' wrong env vars, ...) doesn't stop the others from connecting or
        ''' block the app's own startup - caught and skipped per-server,
        ''' so one broken server degrades gracefully rather than taking the
        ''' app down.
        ''' </summary>
        Public Async Function OnStartupAsync(cancellationToken As CancellationToken) As Task Implements IAssistantModule.OnStartupAsync
            For Each serverConfig In _repository.ListServers().Where(Function(s) s.IsEnabled)
                Try
                    ' Arguments/EnvironmentVariables are both Nothing by
                    ' default on a fresh StdioClientTransportOptions -
                    ' assigned directly here, not mutated in place.
                    Dim transportOptions As New StdioClientTransportOptions With {
                        .Name = serverConfig.Name,
                        .Command = serverConfig.Command,
                        .Arguments = serverConfig.Arguments,
                        .EnvironmentVariables = New Dictionary(Of String, String)(serverConfig.EnvironmentVariables)
                    }

                    Dim transport As New StdioClientTransport(transportOptions)
                    Dim client = Await McpClient.CreateAsync(transport, cancellationToken:=cancellationToken)
                    _connectedClients.Add(client)

                    Dim serverTools = Await client.ListToolsAsync(cancellationToken:=cancellationToken)
                    _tools.AddRange(serverTools)
                Catch
                    ' Best-effort - see this method's summary. A server that
                    ' can't connect just contributes no tools this run.
                End Try
            Next
        End Function

        ''' <summary>
        ''' Clears _connectedClients/_tools after disposing, not just
        ''' disposing - a shutdown can be followed by a fresh OnStartupAsync
        ''' in the same run (ModuleRegistry.ReconcileEnabledModulesAsync),
        ''' not only at real app exit. Without this, a restart cycle would
        ''' accumulate disposed (dead) clients and duplicate tool entries
        ''' from the old connection alongside the new one.
        ''' </summary>
        Public Async Function OnShutdownAsync(cancellationToken As CancellationToken) As Task Implements IAssistantModule.OnShutdownAsync
            For Each client In _connectedClients
                Await client.DisposeAsync()
            Next
            _connectedClients.Clear()
            _tools.Clear()
        End Function

        Public Function GetTools() As IEnumerable(Of AITool) Implements IAssistantModule.GetTools
            Return _tools
        End Function

    End Class

End Namespace
