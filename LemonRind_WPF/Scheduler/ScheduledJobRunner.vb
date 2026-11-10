Imports System.Diagnostics
Imports System.Text.Json
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Microsoft.Extensions.AI
Imports LemonRind.Configuration
Imports LemonRind.Data
Imports LemonRind.Memories
Imports LemonRind.Modules
Imports LemonRind.Modules.FileSystem
Imports LemonRind.Services

Namespace Scheduler

    ''' <summary>How a run ended ("ok", "failed", "timed out" or "cut short") and how long it took.</summary>
    Public NotInheritable Class JobRunResult
        Public ReadOnly Property Status As String
        Public ReadOnly Property Seconds As Double

        Public Sub New(status As String, seconds As Double)
            Me.Status = status
            Me.Seconds = seconds
        End Sub
    End Class

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

        ' What is sent to carry on a reply that stopped at the output limit.
        Private Const ContinuePrompt As String = "Your last reply was cut off at the length limit. Continue exactly from where it stopped, without repeating what you already wrote."

        Private ReadOnly _chatClientFactory As LemonadeChatClientFactory
        Private ReadOnly _settings As AppSettings
        Private ReadOnly _runningJobs As RunningJobTracker
        Private ReadOnly _managementClient As LemonadeManagementClient

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

        Public Sub New(chatClientFactory As LemonadeChatClientFactory, moduleRegistry As Func(Of ModuleRegistry), sessionRepository As ChatSessionRepository, memoryService As MemoryService, notifier As SchedulerNotifier, approvalStore As FileWriteApprovalStore, settings As AppSettings, runningJobs As RunningJobTracker, managementClient As LemonadeManagementClient)
            _chatClientFactory = chatClientFactory
            _managementClient = managementClient
            _settings = settings
            _runningJobs = runningJobs
            _moduleRegistry = moduleRegistry
            _sessionRepository = sessionRepository
            _memoryService = memoryService
            _notifier = notifier
            _approvalStore = approvalStore
        End Sub

        ''' <summary>
        ''' Says so while the job works, with the model it uses (its own, else the default chat model), so the screen can
        ''' show "Job running" and ask before another model is loaded.
        ''' </summary>
        Public Async Function RunJobAsync(job As ScheduledJob, cancellationToken As CancellationToken) As Task(Of JobRunResult)
            ' A job with no model of its own uses the default chat model; if Lemonade no longer has it, the loaded chat model.
            Dim choice = Await ChooseModelAsync(job, cancellationToken)
            Using running = _runningJobs.Start(job.Name, If(choice.Model, If(String.IsNullOrWhiteSpace(job.ModelId), _settings.Lemonade.ChatModel, job.ModelId)))
                Return Await RunJobCoreAsync(job, choice, cancellationToken)
            End Using
        End Function

        ''' <summary>Asks Lemonade which models it has and which are loaded, to settle the model for a job that has none of its own. If Lemonade cannot be asked, nothing changes: the run goes ahead as before and reports its own error.</summary>
        Private Async Function ChooseModelAsync(job As ScheduledJob, cancellationToken As CancellationToken) As Task(Of JobModelChoice)
            If Not String.IsNullOrWhiteSpace(job.ModelId) Then Return New JobModelChoice(job.ModelId, "", "")
            Try
                Using timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                    timeout.CancelAfter(TimeSpan.FromSeconds(10))
                    Dim models = Await _managementClient.ListDownloadedModelsAsync(timeout.Token)
                    Dim health = Await _managementClient.GetHealthAsync(timeout.Token)
                    Return DefaultModelCheck.ChooseForJob(job.ModelId, _settings.Lemonade.ChatModel, models, health)
                End Using
            Catch ex As Exception When Not cancellationToken.IsCancellationRequested
                Return New JobModelChoice(Nothing, "", "")
            End Try
        End Function

        Private Async Function RunJobCoreAsync(job As ScheduledJob, choice As JobModelChoice, cancellationToken As CancellationToken) As Task(Of JobRunResult)
            ' Read fresh on every run, so a change made in Settings applies
            ' from the next run without a restart. (How many tool-call
            ' rounds one job may use matters because a research job that
            ' searches and reads many pages needs far more than the
            ' library's default of 40, and an unattended job has nobody to
            ' say "keep going". This runner's own chat client is identical
            ' to the app's shared one apart from that.)
            Dim limits = _settings.Scheduler
            Dim maxToolRounds = limits.MaxToolRounds
            Dim chatClient = _chatClientFactory.CreateChatClient(maxToolRounds)
            Dim stopwatch = Diagnostics.Stopwatch.StartNew()
            Dim usage = TurnUsage.Begin() ' collects what the whole run cost (every model request and tool call), saved with the reply for the usage screen

            ' Plain job.Name, not a "SCHEDULED: " prefix - the "scheduled"
            ' tag (below) is what identifies a scheduled session in the
            ' sidebar, rather than baking it into the title text.
            Dim sessionId = _sessionRepository.CreateSession(job.Name)
            _sessionRepository.AddTagToSession(sessionId, "scheduled")

            ' Saved and surfaced immediately, before the model is even
            ' called - not deferred to sit alongside the reply/error at the
            ' very end. Otherwise the sidebar entry itself could appear
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
            ' TimeAwareness - included here as in every live chat turn (see
            ' MainViewModel.BuildLiveTurnContextTextAsync). With no real date to
            ' anchor to, the model invents a plausible-sounding but
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
            ' the same cap the live chat path uses (see MainViewModel.
            ' SendAsync's own MaxOutputTokens comment): a single stuck/looping
            ' generation has produced over 12,000 output tokens on its own, eating most of
            ' JobRunTimeoutSeconds and getting the whole job cancelled.
            ' It is a setting now (Settings > Scheduler), bounded so one runaway
            ' completion can't consume the entire job. A model's thinking
            ' counts towards it, so it needs room for both the thinking and
            ' the report that follows.
            Dim maxOutputTokensPerReply = limits.MaxOutputTokens

            Dim options As New ChatOptions With {
                .Tools = _moduleRegistry().GetEnabledTools().ToList(),
                .MaxOutputTokens = maxOutputTokensPerReply
            }
            ' Same override mechanism as a live chat turn's SelectedModel
            ' (MainViewModel.SendAsync) - Nothing/empty ModelId just means
            ' "whatever the client defaults to", so an unset job.ModelId is a
            ' no-op, not an error.
            If Not String.IsNullOrEmpty(job.ModelId) Then
                options.ModelId = job.ModelId
            ElseIf choice.Model IsNot Nothing Then
                options.ModelId = choice.Model
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
            Dim status = "ok"
            Dim continues = 0
            Dim usedUpLimitThinking = False ' the reply ended at the limit with no text at all
            Dim notes As New List(Of String) ' saved as their own messages, after the reply
            If choice.Note.Length > 0 Then notes.Add(choice.Note)
            _approvalStore.IsRunningScheduledJob = True
            Try
                Try
                    If choice.Problem.Length > 0 Then Throw New InvalidOperationException(choice.Problem)

                    Const maxAttempts = 2
                    Dim attempt = 1
                    While attempt <= maxAttempts
                        Dim response = Await chatClient.GetResponseAsync(history, options, cancellationToken)
                        replyText = response.Text
                        
                        ' The tool-calling loop stops quietly once it reaches its round
                        ' limit, so say so in the job's output rather than leaving a blank or
                        ' truncated-looking reply (and don't retry: a second attempt would
                        ' just burn through the same number of rounds again).
                        Dim toolRounds = response.Messages.Where(Function(m) m.Role = ChatRole.Assistant AndAlso m.Contents.OfType(Of FunctionCallContent)().Any()).Count()
                        If toolRounds >= maxToolRounds - 1 Then
                            replyText = (replyText & Environment.NewLine & Environment.NewLine &
                                $"⚠️ This job used its maximum of {maxToolRounds} tool-call rounds, so it may have stopped before finishing. Try splitting it into smaller jobs, or ask it to search and read less.").Trim()
                            Exit While
                        End If

                        If String.IsNullOrWhiteSpace(replyText) Then
                            usedUpLimitThinking = (response.FinishReason = ChatFinishReason.Length)
                            attempt += 1
                            Continue While
                        End If
                        usedUpLimitThinking = False

                        ' A reply that stopped at the output limit is carried
                        ' on, so a long report arrives whole. The model has to
                        ' see what it wrote (and any tool results behind it),
                        ' so the whole response goes back into the history first.
                        While response.FinishReason = ChatFinishReason.Length AndAlso continues < limits.MaxContinuations
                            continues += 1
                            history.AddMessages(response)
                            history.Add(New ChatMessage(ChatRole.User, ContinuePrompt))
                            response = Await chatClient.GetResponseAsync(history, options, cancellationToken)
                            If String.IsNullOrWhiteSpace(response.Text) Then Exit While ' it used the whole limit thinking again: nothing to add
                            replyText &= response.Text
                        End While

                        ' Still at the limit: what was written stops part way
                        ' (a report cut off in the middle of a table, say).
                        ' Calling that "ok" would hide it.
                        Dim continuesPlural = If(continues = 1, "", "s")
                        If response.FinishReason = ChatFinishReason.Length Then
                            status = "cut short"
                            Dim tried = If(continues > 0, $" even after {continues} automatic continue{continuesPlural}", "")
                            notes.Add($"⚠️ The model's reply was cut off at the output limit ({maxOutputTokensPerReply:N0} tokens, which includes its thinking){tried}, so it is probably incomplete. Raise 'Longest reply' in Settings > Scheduler, use a model that thinks less, or ask for a shorter report.")
                        ElseIf continues > 0 Then
                            notes.Add($"Note: the reply reached the output limit and was continued automatically {continues} time{continuesPlural}, so it arrives in {continues + 1} parts.")
                        End If
                        Exit While
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
                    ' detection instead). Without this separate Catch, the generic one
                    ' below would catch this too and show the
                    ' unhelpful "Check Lemonade is running a real chat
                    ' model..." hint, which has nothing to do with a job
                    ' simply running out of time.
                    status = "timed out"
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
                    ' this left the job's session created (see
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
                    ' This is genuinely what it looks like
                    ' (not a red herring): Lemonade can take over 100 seconds
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
                    ' a slow/overly broad tool call (e.g. an
                    ' unsuitable web search provider pulling in far more
                    ' content than needed) is a much more likely cause here
                    ' than the wrong kind of model being selected.
                    Dim hint = If(rawMessage.Contains("exceeds the available context size", StringComparison.OrdinalIgnoreCase),
                        "Try loading this model with a larger context size, disabling modules/tools this job doesn't need, or shortening its prompt.",
                        If(isTimeoutFailure,
                            "This usually means the request grew very large before Lemonade could respond - often a slow or overly broad tool call (e.g. the wrong web search provider pulling in too much content). Check this job's tools/settings, or increase Lemonade's network timeout.",
                            "Check Lemonade is running a real chat model, and that any modules this job depends on (e.g. which web search provider is selected) are configured correctly."))
                    replyText = $"⚠️ This scheduled job failed to complete: {rawMessage.TrimEnd("."c, " "c)}." & Environment.NewLine & Environment.NewLine & hint
                    status = "failed"
                    jobFailed = True
                End Try
            Finally
                _approvalStore.IsRunningScheduledJob = False
            End Try

            If String.IsNullOrWhiteSpace(replyText) Then
                If usedUpLimitThinking Then
                    replyText = $"(No response - the model used up its output limit ({maxOutputTokensPerReply:N0} tokens) while thinking, so there is no answer. Raise 'Longest reply' in Settings > Scheduler or use a model that thinks less.)"
                    status = "cut short"
                Else
                    replyText = "(No response - the model's reasoning didn't lead to an answer.)"
                    status = "failed"
                End If
                jobFailed = True
            End If

            _sessionRepository.SaveMessage(sessionId, ChatRole.Assistant.Value, replyText, reasoningContent:="",
                toolCalls:=usage.ToolCallsForStorage(), stats:=usage.Close())
            _notifier.RaiseSessionUpdated(sessionId)

            For Each note In notes
                _sessionRepository.SaveMessage(sessionId, ChatRole.Assistant.Value, note, reasoningContent:="")
            Next

            ' When the run ended and how long it took, as the last line of
            ' its chat - whatever the outcome, so a slow or timed-out job
            ' shows it too.
            Dim seconds = stopwatch.Elapsed.TotalSeconds
            Dim endedAt = DateTime.Now
            _sessionRepository.SaveMessage(sessionId, ChatRole.Assistant.Value,
                $"This run ended at {endedAt:HH:mm} on {endedAt:dd/MM/yyyy} and took {RunDuration.Format(seconds)}.", reasoningContent:="")
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

            Return New JobRunResult(status, seconds)
        End Function

    End Class

End Namespace
