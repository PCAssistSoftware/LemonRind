Imports System.Text.Json
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Microsoft.Extensions.AI
Imports LemonRind.Data
Imports LemonRind.Memories
Imports LemonRind.Modules
Imports LemonRind.Modules.FileSystem
Imports LemonRind.Services

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

            ' Saved and surfaced immediately, before the model is even
            ' called - not deferred to sit alongside the reply/error at the
            ' very end (the previous behaviour). Confirmed live this was a
            ' real, confusing gap: the sidebar entry itself could appear
            ' early (MainWindow's own Activated-triggered RefreshSessions
            ' can pick up the just-CreateSession'd, still-empty session
            ' independently of this class), but with no messages saved yet,
            ' opening it showed nothing at all - and once a reply/error
            ' finally did land, both the prompt and the reply bubbles carried
            ' the exact same saved timestamp, making a job that took several
            ' minutes look like it appeared instantly. A live chat's own
            ' user message appears the moment Send is pressed, not only once
            ' a reply comes back - this matches that.
            _sessionRepository.SaveMessage(sessionId, ChatRole.User.Value, job.Prompt, reasoningContent:="")
            _notifier.RaiseSessionUpdated(sessionId)

            ' A live chat turn shows real, continuous progress while it
            ' works (streamed reply text, tool-call chips, "Thinking for
            ' Ns") - this path has nothing equivalent to stream, since
            ' GetResponseAsync below runs the whole tool-invocation loop
            ' internally and only returns once it's completely done, which
            ' can genuinely take several minutes. Without any sign of life
            ' in between, watching this session (if the user happens to have
            ' it open) looks identical to a hang. One plain note, left in
            ' the transcript permanently rather than removed once the real
            ' reply lands - same idea as CompactHistoryIfNeededAsync's own
            ' "Compacted older messages..." note, a persisted record that
            ' something happened, not a spinner that needs cleaning up
            ' afterward.
            _sessionRepository.SaveMessage(sessionId, ChatRole.Assistant.Value, "🔄 Running in the background - this can take several minutes, especially with tool calls like web search.", reasoningContent:="")
            _notifier.RaiseSessionUpdated(sessionId)

            ' Same pinned-facts + relevance-searched-against-the-prompt shape
            ' as a normal chat turn's system prompt (MainViewModel.
            ' BuildSystemPromptText) - a scheduled run benefits from the same
            ' "learned about the user" context a live chat would.
            Dim relevantMemoriesText = Await _memoryService.GetRelevantMemoriesTextAsync(job.Prompt, cancellationToken)
            Dim pinnedFactsText = _memoryService.GetPinnedFactsText()
            ' TimeAwareness - confirmed live this was missing here (unlike
            ' every live chat turn, see MainViewModel.BuildLiveTurnContextTextAsync)
            ' and directly caused a real, wrong report: with no real date to
            ' anchor to, the model invented a plausible-sounding but
            ' incorrect one for anything date-relative ("this week", "later
            ' this month") instead of reasoning from today's actual date.
            Dim systemSections As New List(Of String) From {SystemPrompt, TimeAwareness.BuildTimeAwarenessText()}
            If Not String.IsNullOrEmpty(pinnedFactsText) Then systemSections.Add(pinnedFactsText)
            If Not String.IsNullOrEmpty(relevantMemoriesText) Then systemSections.Add(relevantMemoriesText)

            Dim history As New List(Of ChatMessage) From {
                New ChatMessage(ChatRole.System, String.Join(Environment.NewLine & Environment.NewLine, systemSections)),
                New ChatMessage(ChatRole.User, job.Prompt)
            }

            ' Without this, a single completion has nothing stopping it from
            ' running to Lemonade's own server-side default n_predict -
            ' confirmed live this was a real, serious gap (the same one
            ' already fixed for the live chat path, see MainViewModel.
            ' SendAsync's own MaxOutputTokens comment, just never carried
            ' over here): a single stuck/looping generation produced over
            ' 12,000 output tokens on its own, eating most of
            ' JobRunTimeoutSeconds and getting the whole job cancelled.
            ' 16384 matches SendAsync's own generous cap ceiling - plenty
            ' for a real report, but bounded so one runaway completion can't
            ' consume the entire job.
            Const MaxOutputTokensPerReply As Integer = 16384

            Dim options As New ChatOptions With {
                .Tools = _moduleRegistry().GetEnabledTools().ToList(),
                .MaxOutputTokens = MaxOutputTokensPerReply
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
            Dim jobFailed = False
            _approvalStore.IsRunningScheduledJob = True
            Try
                Try
                    Const maxAttempts = 2
                    Dim attempt = 1
                    While attempt <= maxAttempts
                        Dim response = Await _chatClient.GetResponseAsync(history, options, cancellationToken)
                        replyText = response.Text
                        If Not String.IsNullOrWhiteSpace(replyText) Then Exit While
                        attempt += 1
                    End While
                Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
                    ' Distinguished from a generic Exception below by the
                    ' When filter - cancellationToken here IS
                    ' SchedulerModule's own JobRunTimeoutSeconds-bound token,
                    ' so this only matches when THIS job's own overall time
                    ' limit was reached, not a per-request SDK timeout (a
                    ' separate, internal token - that case still throws a
                    ' ClientResultException/TimeoutException with
                    ' cancellationToken.IsCancellationRequested still False,
                    ' and falls through to the generic Catch's own timeout
                    ' detection instead). Confirmed live the generic Catch
                    ' below previously caught this too and showed the
                    ' unhelpful "Check Lemonade is running a real chat
                    ' model..." hint, which has nothing to do with a job
                    ' simply running out of time.
                    replyText = "⚠️ This scheduled job didn't finish before its own time limit and was cancelled." &
                        Environment.NewLine & Environment.NewLine &
                        "This usually means the model got stuck reasoning in a loop, or the job's prompt/tools made it take unusually long. Try simplifying the prompt, disabling tools it doesn't need, or asking it to keep its answer shorter."
                    jobFailed = True
                Catch ex As Exception
                    ' Without this, a real failure (most notably Lemonade
                    ' rejecting the request outright because the prompt/tool
                    ' history already exceeds the model's context size, which
                    ' - unlike the live chat path's SSE-embedded-error case,
                    ' see StreamOneReplyAsync's own comment - comes back as a
                    ' genuine non-2xx HTTP status from this non-streaming
                    ' GetResponseAsync call, so the OpenAI SDK throws instead
                    ' of returning a normal-looking response) propagated all
                    ' the way out to SchedulerModule.OnPollTimerTick's own
                    ' best-effort Catch and was silently swallowed there -
                    ' confirmed live this left the job's session created (see
                    ' CreateSession above) but with zero messages saved below,
                    ' so nothing in the sidebar or the chat itself gave any
                    ' clue a job had even run, let alone why it failed.
                    Dim rawMessage = ex.Message
                    Dim clientResultEx = TryCast(ex, System.ClientModel.ClientResultException)
                    If clientResultEx IsNot Nothing Then
                        Try
                            ' Same GetRawResponse().Content pattern already
                            ' used by ImageGenerationService for the same SDK -
                            ' ex.Message alone is often just a generic "service
                            ' request failed" summary, not Lemonade's own
                            ' specific reason, which only lives in the raw
                            ' response body's JSON.
                            Dim rawBody = clientResultEx.GetRawResponse()?.Content?.ToString()
                            If Not String.IsNullOrEmpty(rawBody) Then
                                Using doc = JsonDocument.Parse(rawBody)
                                    Dim errorProp As JsonElement = Nothing
                                    Dim messageProp As JsonElement = Nothing
                                    If doc.RootElement.TryGetProperty("error", errorProp) AndAlso errorProp.TryGetProperty("message", messageProp) Then
                                        rawMessage = messageProp.GetString()
                                    End If
                                End Using
                            End If
                        Catch
                            ' Malformed/non-JSON body - rawMessage stays ex.Message from above.
                        End Try
                    End If

                    ' The SDK's own retry-exhausted message (client-side
                    ' network timeouts have no HTTP response body for the
                    ' JSON extraction above to find, so rawMessage is still
                    ' ex.Message at this point) repeats the SAME inner
                    ' failure once per retry attempt, verbatim, each in its
                    ' own parentheses - "Retry failed after 4 tries. (timed
                    ' out...) (timed out...) (timed out...) (timed out...)".
                    ' Confirmed live this is genuinely what it looks like
                    ' (not a red herring): Lemonade took over 100 seconds
                    ' just to reprocess a prompt that had grown very large,
                    ' longer than the SDK's own default per-request network
                    ' timeout, so every retry timed out the same way.
                    ' Collapsing to the one distinct reason turns that wall
                    ' of repetition into one clean sentence.
                    Dim isTimeoutFailure = rawMessage.Contains("Retry failed after", StringComparison.OrdinalIgnoreCase) OrElse
                        rawMessage.Contains("configured timeout", StringComparison.OrdinalIgnoreCase)
                    If isTimeoutFailure Then
                        Dim distinctReasons = Regex.Matches(rawMessage, "\(([^()]*)\)").
                            Cast(Of Match)().
                            Select(Function(m) m.Groups(1).Value.Trim()).
                            Where(Function(r) r.Length > 0).
                            Distinct().
                            ToList()
                        rawMessage = If(distinctReasons.Count > 0,
                            $"The request timed out and Lemonade never replied in time ({distinctReasons(0)})",
                            "The request timed out and Lemonade never replied in time.")
                    End If

                    ' Same hint split as SendAsync's own error branch, worded
                    ' for an unattended job rather than "try again" (nobody's
                    ' watching this one happen). The timeout case gets its
                    ' own hint rather than falling into the generic one below -
                    ' a slow/overly broad tool call (confirmed live: an
                    ' unsuitable web search provider pulling in far more
                    ' content than needed) is a much more likely cause here
                    ' than the wrong kind of model being selected.
                    Dim hint = If(rawMessage.Contains("exceeds the available context size", StringComparison.OrdinalIgnoreCase),
                        "Try loading this model with a larger context size, disabling modules/tools this job doesn't need, or shortening its prompt.",
                        If(isTimeoutFailure,
                            "This usually means the request grew very large before Lemonade could respond - often a slow or overly broad tool call (e.g. the wrong web search provider pulling in too much content). Check this job's tools/settings, or increase Lemonade's network timeout.",
                            "Check Lemonade is running a real chat model, and that any modules this job depends on (e.g. which web search provider is selected) are configured correctly."))
                    replyText = $"⚠️ This scheduled job failed to complete: {rawMessage.TrimEnd("."c, " "c)}." & Environment.NewLine & Environment.NewLine & hint
                    jobFailed = True
                End Try
            Finally
                _approvalStore.IsRunningScheduledJob = False
            End Try

            If String.IsNullOrWhiteSpace(replyText) Then
                replyText = "(No response - the model's reasoning didn't lead to an answer.)"
            End If

            _sessionRepository.SaveMessage(sessionId, ChatRole.Assistant.Value, replyText, reasoningContent:="")
            _notifier.RaiseSessionUpdated(sessionId)

            ' Best-effort, same as a live turn's fact extraction - a
            ' scheduled run is a real exchange too, worth learning from.
            ' Skipped on jobFailed - same reasoning as SendAsync's own
            ' errorMessage-gated extraction call: an error message isn't a
            ' real exchange worth mining for facts.
            Try
                If Not jobFailed Then
                    Using extractionTimeoutCts As New CancellationTokenSource(TimeSpan.FromSeconds(60))
                        Await _memoryService.ExtractAndSaveFactsAsync(job.Prompt, replyText, extractionTimeoutCts.Token)
                    End Using
                End If
            Catch
            End Try
        End Function

    End Class

End Namespace
