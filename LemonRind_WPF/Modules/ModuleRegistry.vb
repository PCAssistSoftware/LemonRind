Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Extensions.AI
Imports Microsoft.Extensions.Logging

Namespace Modules

    ''' <summary>
    ''' Central place that knows about every IAssistantModule in the app.
    ''' Registered as a singleton in DI; modules get added to it via
    ''' constructor injection (DI hands in "everything registered as
    ''' IAssistantModule", see Application.xaml.vb). Adding a new module is
    ''' just one more DI registration - this class and anything that depends
    ''' on it never needs to change.
    ''' </summary>
    Public Class ModuleRegistry

        Private ReadOnly _modules As IReadOnlyList(Of IAssistantModule)
        Private ReadOnly _logger As ILogger(Of ModuleRegistry)

        ''' <summary>
        ''' Which modules currently have real startup work in effect (a poll
        ''' timer running, MCP servers connected, ...) - reference-equality
        ''' HashSet, fine since every IAssistantModule is a DI singleton.
        ''' Tracked separately from IsEnabled (which reads live off
        ''' Settings) because "the setting says enabled" and "OnStartupAsync
        ''' has actually run" can genuinely disagree mid-session -
        ''' ReconcileEnabledModulesAsync
        ''' is what brings them back in sync without a restart.
        ''' </summary>
        Private ReadOnly _startedModules As New HashSet(Of IAssistantModule)()

        Public Sub New(modules As IEnumerable(Of IAssistantModule), logger As ILogger(Of ModuleRegistry))
            _modules = modules.ToList()
            _logger = logger
        End Sub

        ''' <summary>All registered modules, enabled or not - e.g. for the Settings screen's toggle list.</summary>
        Public ReadOnly Property AllModules As IReadOnlyList(Of IAssistantModule)
            Get
                Return _modules
            End Get
        End Property

        ''' <summary>
        ''' Starts every enabled module. Called once during app startup.
        ''' A module that throws during startup is logged and skipped rather
        ''' than crashing the whole app - one broken module (e.g. an MCP
        ''' server that's unreachable) shouldn't take the assistant down.
        ''' </summary>
        Public Async Function StartEnabledModulesAsync(cancellationToken As CancellationToken) As Task
            ' "module" is a reserved word in VB (declares Module blocks), so
            ' the loop variable is named assistantModule instead.
            For Each assistantModule In _modules.Where(Function(m) m.IsEnabled)
                Try
                    Await assistantModule.OnStartupAsync(cancellationToken)
                    _startedModules.Add(assistantModule)
                Catch ex As Exception
                    _logger.LogError(ex, "Module {ModuleName} failed to start", assistantModule.Name)
                End Try
            Next
        End Function

        Public Async Function StopEnabledModulesAsync(cancellationToken As CancellationToken) As Task
            For Each assistantModule In _modules.Where(Function(m) m.IsEnabled)
                Try
                    Await assistantModule.OnShutdownAsync(cancellationToken)
                Catch ex As Exception
                    _logger.LogError(ex, "Module {ModuleName} failed to shut down cleanly", assistantModule.Name)
                End Try
            Next
        End Function

        ''' <summary>
        ''' Brings actually-running modules back in sync with the current
        ''' Modules.Enabled setting, without an app restart - called from
        ''' SettingsViewModel.SaveAsync. Only handles actual enable/disable
        ''' transitions (a module newly enabled gets OnStartupAsync, one
        ''' newly disabled gets OnShutdownAsync) - a module that was already
        ''' running and stays enabled is deliberately left alone here, since
        ''' force-restarting every still-enabled module on every save would
        ''' make every Settings save noticeably slow (a full MCP
        ''' disconnect/reconnect cycle - spawning processes, handshaking -
        ''' even when only an unrelated field like CFG scale changed). The
        ''' one module that genuinely needs a targeted restart when its OWN
        ''' config changes (Mcp - its server list is only read once, at
        ''' OnStartupAsync) gets that via RestartModuleAsync below, called
        ''' right where its config actually changes, not bundled into every
        ''' save. Scheduler doesn't need this at all despite also having
        ''' real startup work - its poll tick already reads jobs fresh from
        ''' the database every time, so a job being added/edited needs no
        ''' restart to take effect.
        ''' </summary>
        Public Async Function ReconcileEnabledModulesAsync(cancellationToken As CancellationToken) As Task
            For Each assistantModule In _modules
                Dim isEnabledNow = assistantModule.IsEnabled
                Dim wasStarted = _startedModules.Contains(assistantModule)

                Try
                    If isEnabledNow AndAlso Not wasStarted Then
                        Await assistantModule.OnStartupAsync(cancellationToken)
                        _startedModules.Add(assistantModule)
                    ElseIf Not isEnabledNow AndAlso wasStarted Then
                        Await assistantModule.OnShutdownAsync(cancellationToken)
                        _startedModules.Remove(assistantModule)
                    End If
                Catch ex As Exception
                    _logger.LogError(ex, "Module {ModuleName} failed to reconcile its enabled state", assistantModule.Name)
                End Try
            Next
        End Function

        ''' <summary>
        ''' Restarts one specific already-running module (OnShutdownAsync
        ''' then OnStartupAsync) - for a module whose own configuration
        ''' changed in a way it only reads once at startup (Mcp's server
        ''' list). A no-op if the module isn't currently enabled/started, so
        ''' it's always safe to call after any change to that module's
        ''' config regardless of whether the module happens to be on.
        ''' </summary>
        Public Async Function RestartModuleAsync(configKey As String, cancellationToken As CancellationToken) As Task
            Dim assistantModule = _modules.FirstOrDefault(Function(m) String.Equals(m.ConfigKey, configKey, StringComparison.OrdinalIgnoreCase))
            If assistantModule Is Nothing OrElse Not _startedModules.Contains(assistantModule) Then Return

            Try
                Await assistantModule.OnShutdownAsync(cancellationToken)
                Await assistantModule.OnStartupAsync(cancellationToken)
            Catch ex As Exception
                _logger.LogError(ex, "Module {ModuleName} failed to restart", assistantModule.Name)
            End Try
        End Function

        ''' <summary>
        ''' Every tool from every enabled module, flattened into one list -
        ''' this is what gets handed to the chat client as its tool set.
        ''' </summary>
        Public Function GetEnabledTools() As IReadOnlyList(Of AITool)
            Return _modules.Where(Function(m) m.IsEnabled).SelectMany(Function(m) m.GetTools()).ToList()
        End Function

        ''' <summary>
        ''' Whether the given tool name is currently contributed by the
        ''' specified enabled module - MainViewModel.SendAsync's
        ''' auto-tagging needs this specifically for MCP, whose tool names
        ''' come straight from whatever each connected server calls them,
        ''' with no fixed naming convention to pattern-match against the
        ''' way Coder's own consistent "code_" prefix allows.
        ''' </summary>
        Public Function IsToolFromModule(toolName As String, configKey As String) As Boolean
            Dim targetModule = _modules.FirstOrDefault(Function(m) m.ConfigKey = configKey AndAlso m.IsEnabled)
            Return targetModule IsNot Nothing AndAlso targetModule.GetTools().Any(Function(t) t.Name = toolName)
        End Function

    End Class

End Namespace
