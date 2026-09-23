Imports System.Threading
Imports Microsoft.Extensions.AI
Imports LemonRind.Data
Imports LemonRind.Memories
Imports LemonRind.Modules
Imports LemonRind.Modules.FileSystem

Namespace Scheduler

    ''' <summary>
    ''' Runs one scheduled job's prompt through the normal tool-enabled chat
    ''' pipeline, in a fresh, isolated session - not the currently-open chat
    ''' in the main window, so a job firing never disturbs whatever the user
    ''' is doing (see SchedulerModule for when this gets called). The new
    ''' session gets a "scheduled" tag so a job's output is identifiable in
    ''' the sidebar at a glance.
    ''' </summary>
    Public Class ScheduledJobRunner

        ' A fixed fallback prompt, independent of AssistantSettings.SystemPrompt -
        ' a scheduled job's system prompt doesn't currently track a
        ' user-edited System Prompt from Settings.
        Private Const SystemPrompt As String = "You are a helpful local AI assistant running on the user's own machine."

        Private ReadOnly _chatClient As IChatClient

        ' Func(Of ModuleRegistry), not ModuleRegistry directly -
        ' ModuleRegistry's own constructor needs every IAssistantModule
        ' (including SchedulerModule), SchedulerModule needs
        ' ScheduledJobRunner, and ScheduledJobRunner needs ModuleRegistry -
        ' a genuine cycle .NET's DI container refuses to resolve directly. A
        ' lazy factory breaks the cycle the same way MainViewModel uses
        ' Func(Of SettingsWindow) to avoid eagerly needing a Transient
        ' SettingsWindow - the real ModuleRegistry only needs to exist by
        ' the time GetEnabledTools() is actually CALLED (a job firing, well
        ' after the whole DI graph including ModuleRegistry itself has
        ' finished being built), not at ScheduledJobRunner's own construction.
        Private ReadOnly _moduleRegistry As Func(Of ModuleRegistry)

        Private ReadOnly _sessionRepository As ChatSessionRepository
        Private ReadOnly _memoryService As MemoryService
        Private ReadOnly _notifier As SchedulerNotifier
        Private ReadOnly _approvalStore As FileWriteApprovalStore

        Public Sub New(chatClient As IChatClient, moduleRegistry As Func(Of ModuleRegistry), sessionRepository As ChatSessionRepository, memoryService As MemoryService, notifier As SchedulerNotifier, approvalStore As FileWriteApprovalStore)
            _chatClient = chatClient
            _moduleRegistry = moduleRegistry
            _sessionRepository = sessionRepository
            _memoryService = memoryService
            _notifier = notifier
            _approvalStore = approvalStore
        End Sub

        Public Async Function RunJobAsync(job As ScheduledJob, cancellationToken As CancellationToken) As Task
            ' Plain job.Name, not a "SCHEDULED: " prefix - the "scheduled"
            ' tag (below) is what identifies a scheduled session in the
            ' sidebar, rather than baking it into the title text.
            Dim sessionId = _sessionRepository.CreateSession(job.Name)
            _sessionRepository.AddTagToSession(sessionId, "scheduled")

            ' Same pinned-facts + relevance-searched-against-the-prompt shape
            ' as a normal chat turn's system prompt (MainViewModel.
            ' BuildSystemPromptText) - a scheduled run benefits from the same
            ' "learned about the user" context a live chat would.
            Dim relevantMemoriesText = Await _memoryService.GetRelevantMemoriesTextAsync(job.Prompt, cancellationToken)
            Dim pinnedFactsText = _memoryService.GetPinnedFactsText()
            Dim systemSections As New List(Of String) From {SystemPrompt}
            If Not String.IsNullOrEmpty(pinnedFactsText) Then systemSections.Add(pinnedFactsText)
            If Not String.IsNullOrEmpty(relevantMemoriesText) Then systemSections.Add(relevantMemoriesText)

            Dim history As New List(Of ChatMessage) From {
                New ChatMessage(ChatRole.System, String.Join(Environment.NewLine & Environment.NewLine, systemSections)),
                New ChatMessage(ChatRole.User, job.Prompt)
            }

            Dim options As New ChatOptions With {
                .Tools = _moduleRegistry().GetEnabledTools().ToList()
            }
            ' Same override mechanism as a live chat turn's SelectedModel
            ' (MainViewModel.SendAsync) - Nothing/empty ModelId just means
            ' "whatever the client defaults to", so an unset job.ModelId is a
            ' no-op, not an error.
            If Not String.IsNullOrEmpty(job.ModelId) Then
                options.ModelId = job.ModelId
            End If

            ' Same retry-once-on-empty-reply safeguard as the live chat path
            ' (MainViewModel.SendAsync) - the same known Qwen flakiness
            ' (empty final answer after a tool call) applies here too, and a
            ' scheduled job silently producing nothing is worse than in a
            ' live chat, where the user could just ask again.
            '
            ' _approvalStore.IsRunningScheduledJob is set for exactly this
            ' call chain (GetResponseAsync runs the whole tool-invocation
            ' loop internally, including any write_file/Coder write) -
            ' nobody's present to click the file-write approval dialog for a
            ' job firing unattended, so without this it would just hang
            ' until JobRunTimeoutSeconds kills the job. AsyncLocal scoping
            ' (see FileWriteApprovalStore's
            ' own comment) means this never affects a concurrent live chat's
            ' own writes, which still get asked every time as normal.
            Dim replyText = ""
            _approvalStore.IsRunningScheduledJob = True
            Try
                Const maxAttempts = 2
                Dim attempt = 1
                While attempt <= maxAttempts
                    Dim response = Await _chatClient.GetResponseAsync(history, options, cancellationToken)
                    replyText = response.Text
                    If Not String.IsNullOrWhiteSpace(replyText) Then Exit While
                    attempt += 1
                End While
            Finally
                _approvalStore.IsRunningScheduledJob = False
            End Try

            If String.IsNullOrWhiteSpace(replyText) Then
                replyText = "(No response - the model's reasoning didn't lead to an answer.)"
            End If

            _sessionRepository.SaveMessage(sessionId, ChatRole.User.Value, job.Prompt, reasoningContent:="")
            _sessionRepository.SaveMessage(sessionId, ChatRole.Assistant.Value, replyText, reasoningContent:="")

            ' The new session now exists and has its messages - safe for the
            ' sidebar to pick it up. Raised here (not e.g. right after
            ' CreateSession) so RefreshSessions() sees a session that already
            ' has content, not an empty placeholder.
            _notifier.RaiseJobCompleted()

            ' Best-effort, same as a live turn's fact extraction - a
            ' scheduled run is a real exchange too, worth learning from.
            Try
                Using extractionTimeoutCts As New CancellationTokenSource(TimeSpan.FromSeconds(60))
                    Await _memoryService.ExtractAndSaveFactsAsync(job.Prompt, replyText, extractionTimeoutCts.Token)
                End Using
            Catch
            End Try
        End Function

    End Class

End Namespace
