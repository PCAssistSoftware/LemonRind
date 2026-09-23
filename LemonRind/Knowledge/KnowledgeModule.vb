Imports System.Threading
Imports Microsoft.Extensions.AI
Imports LemonRind.Configuration
Imports LemonRind.Modules

Namespace Knowledge

    ''' <summary>
    ''' A toggle for Knowledge Bases: disabling this means the "no knowledge
    ''' base is attached" boilerplate note never needs injecting into
    ''' context at all (see MainViewModel.SendAsync's
    ''' IsKnowledgeModuleEnabled check), and the knowledge-base dropdown
    ''' hides from the main chat window entirely (MainWindow.xaml).
    ''' Contributes no tools - retrieval is folded directly into the prompt
    ''' (KnowledgeService), never a callable function - so
    ''' OnStartupAsync/OnShutdownAsync/GetTools are all no-ops.
    ''' </summary>
    Public Class KnowledgeModule
        Implements IAssistantModule

        Private ReadOnly _moduleSettings As ModuleSettings

        Public Sub New(settings As AppSettings)
            _moduleSettings = settings.Modules
        End Sub

        Public ReadOnly Property Name As String Implements IAssistantModule.Name
            Get
                Return "Knowledge Bases"
            End Get
        End Property

        Public ReadOnly Property ConfigKey As String Implements IAssistantModule.ConfigKey
            Get
                Return "Knowledge"
            End Get
        End Property

        Public ReadOnly Property Description As String Implements IAssistantModule.Description
            Get
                Return "Lets a chat attach a knowledge base (files/folders/websites/pasted text you've ingested) so relevant content is folded into each reply. Off by default here just hides the attach-a-knowledge-base dropdown and its context-prompt note; existing knowledge bases and their content are untouched either way."
            End Get
        End Property

        ''' <summary>
        ''' Read live off the shared Modules.Enabled dictionary each time,
        ''' not cached at construction - same live-reload pattern every
        ''' other module already uses. Defaults to False, same as every
        ''' other module, so a fresh install starts with nothing enabled
        ''' until the user opts in.
        ''' </summary>
        Public ReadOnly Property IsEnabled As Boolean Implements IAssistantModule.IsEnabled
            Get
                Return _moduleSettings.Enabled.GetValueOrDefault(ConfigKey, False)
            End Get
        End Property

        Public Function OnStartupAsync(cancellationToken As CancellationToken) As Task Implements IAssistantModule.OnStartupAsync
            Return Task.CompletedTask
        End Function

        Public Function OnShutdownAsync(cancellationToken As CancellationToken) As Task Implements IAssistantModule.OnShutdownAsync
            Return Task.CompletedTask
        End Function

        Public Function GetTools() As IEnumerable(Of AITool) Implements IAssistantModule.GetTools
            Return Enumerable.Empty(Of AITool)()
        End Function

    End Class

End Namespace
