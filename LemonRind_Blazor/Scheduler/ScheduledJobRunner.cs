using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using LemonRindBlazor.Configuration;
using LemonRindBlazor.Data;
using LemonRindBlazor.Memories;
using LemonRindBlazor.Modules;
using LemonRindBlazor.Modules.FileSystem;
using LemonRindBlazor.Services;

namespace LemonRindBlazor.Scheduler;

/// <summary>How a run ended ("ok", "failed", "timed out" or "cut short") and how long it took.</summary>
public record JobRunResult(string Status, double Seconds);

/// <summary>
/// Runs one scheduled job's prompt through the normal tool-enabled chat
/// pipeline, in a fresh, isolated session - not the currently-open chat in
/// the main window, so a job firing never disturbs whatever the user is
/// doing. The new session gets a "scheduled" tag so a job's output is
/// identifiable in the sidebar at a glance.
///
/// Ported directly from the VB.NET/WPF LemonRind app's Scheduler\ScheduledJobRunner.vb,
/// including its own multi-round reliability fixes (the OperationCanceledException-
/// vs-generic-Exception split, the timeout-message collapsing, the raw JSON
/// error extraction, MaxOutputTokens cap, TimeAwareness, auto-approved file
/// writes) - all found through live testing.
/// </summary>
public class ScheduledJobRunner(
    LemonadeChatClientFactory chatClientFactory,
    Func<ModuleRegistry> moduleRegistry,
    ChatSessionRepository sessionRepository,
    MemoryService memoryService,
    SchedulerNotifier notifier,
    FileWriteApprovalStore approvalStore,
    AppSettings settings,
    RunningJobTracker runningJobs)
{
    // What is sent to carry on a reply that stopped at the output limit.
    private const string ContinuePrompt =
        "Your last reply was cut off at the length limit. Continue exactly from where it stopped, without repeating what you already wrote.";

    // A fixed fallback prompt, independent of AssistantSettings.SystemPrompt -
    // a scheduled job's system prompt doesn't currently track a user-edited
    // System Prompt from Settings.
    private const string SystemPrompt = "You are a helpful local AI assistant running on the user's own machine.";

    public async Task<JobRunResult> RunJobAsync(ScheduledJob job, CancellationToken cancellationToken)
    {
        // Read fresh on every run, so a change made in Settings applies from
        // the next run without a restart. (How many tool-call rounds one job
        // may use matters because a research job that searches and reads many
        // pages needs far more than the library's default of 40, and an
        // unattended job has nobody to say "keep going". This runner's own
        // chat client is identical to the app's shared one apart from that.)
        // Say so while this job works, with the model it uses (its own, else the default chat model), so the screen can
        // show "Job running" and ask before another model is loaded.
        using var running = runningJobs.Start(job.Name, string.IsNullOrWhiteSpace(job.ModelId) ? settings.Lemonade.ChatModel : job.ModelId);

        var limits = settings.Scheduler;
        var maxToolRounds = limits.MaxToolRounds;
        var chatClient = chatClientFactory.CreateChatClient(maxToolRounds);
        var stopwatch = Stopwatch.StartNew();

        // Plain job.Name, not a "SCHEDULED: " prefix - the "scheduled" tag
        // (below) is what identifies a scheduled session in the sidebar.
        var sessionId = sessionRepository.CreateSession(job.Name);
        sessionRepository.AddTagToSession(sessionId, "scheduled");

        // Saved and surfaced immediately, before the model is even called -
        // a live chat's own user message appears the moment Send is
        // pressed, not only once a reply comes back; this matches that.
        sessionRepository.SaveMessage(sessionId, ChatRole.User.Value, job.Prompt, reasoningContent: null);
        notifier.RaiseSessionUpdated(sessionId);

        // GetResponseAsync below runs the whole tool-invocation loop
        // internally and only returns once completely done, which can take
        // several minutes with nothing streamed in between - without any
        // sign of life, watching this session looks identical to a hang.
        // One plain note, left in the transcript permanently, not removed
        // once the real reply lands.
        sessionRepository.SaveMessage(sessionId, ChatRole.Assistant.Value, "🔄 Running in the background - this can take several minutes, especially with tool calls like web search.", reasoningContent: null);
        notifier.RaiseSessionUpdated(sessionId);

        // Same pinned-facts + relevance-searched-against-the-prompt shape as
        // a normal chat turn's system prompt - a scheduled run benefits
        // from the same "learned about the user" context a live chat would.
        var relevantMemoriesText = await memoryService.GetRelevantMemoriesTextAsync(job.Prompt, cancellationToken);
        var pinnedFactsText = memoryService.GetPinnedFactsText();

        var systemSections = new List<string> { SystemPrompt, TimeAwareness.BuildTimeAwarenessText() };
        if (!string.IsNullOrEmpty(pinnedFactsText)) systemSections.Add(pinnedFactsText);
        if (!string.IsNullOrEmpty(relevantMemoriesText)) systemSections.Add(relevantMemoriesText);

        var history = new List<ChatMessage>
        {
            new(ChatRole.System, string.Join(Environment.NewLine + Environment.NewLine, systemSections)),
            new(ChatRole.User, job.Prompt),
        };

        // Without this, a single completion has nothing stopping it from
        // running to Lemonade's own server-side default n_predict - a
        // single stuck/looping generation could produce tens of thousands
        // of output tokens on its own, eating the whole job timeout. A
        // model's thinking counts towards it, so it needs room for both the
        // thinking and the report that follows.
        var maxOutputTokensPerReply = limits.MaxOutputTokens;

        var options = new ChatOptions
        {
            Tools = moduleRegistry().GetEnabledTools(),
            MaxOutputTokens = maxOutputTokensPerReply,
        };
        if (!string.IsNullOrEmpty(job.ModelId))
        {
            options.ModelId = job.ModelId;
        }

        // _approvalStore.IsRunningScheduledJob is set for exactly this call
        // chain (GetResponseAsync runs the whole tool-invocation loop
        // internally, including any write_file write) - nobody's present to
        // click the approval dialog for a job firing unattended, so without
        // this it would just hang until the job's own timeout kills it.
        var replyText = "";
        var jobFailed = false;
        var status = "ok";
        var continues = 0;
        var usedUpLimitThinking = false; // the reply ended at the limit with no text at all
        var notes = new List<string>();  // saved as their own messages, after the reply
        approvalStore.IsRunningScheduledJob = true;
        try
        {
            try
            {
                const int maxAttempts = 2;
                var attempt = 1;
                while (attempt <= maxAttempts)
                {
                    var response = await chatClient.GetResponseAsync(history, options, cancellationToken);
                    replyText = response.Text;

                    // The tool-calling loop stops quietly once it reaches its round
                    // limit, so say so in the job's output rather than leaving a blank or
                    // truncated-looking reply (and don't retry: a second attempt would
                    // just burn through the same number of rounds again).
                    var toolRounds = response.Messages.Count(m => m.Role == ChatRole.Assistant && m.Contents.OfType<FunctionCallContent>().Any());
                    if (toolRounds >= maxToolRounds - 1)
                    {
                        replyText = (replyText + Environment.NewLine + Environment.NewLine +
                            $"⚠️ This job used its maximum of {maxToolRounds} tool-call rounds, so it may have stopped before finishing. Try splitting it into smaller jobs, or ask it to search and read less.").Trim();
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(replyText))
                    {
                        usedUpLimitThinking = response.FinishReason == ChatFinishReason.Length;
                        attempt++;
                        continue;
                    }
                    usedUpLimitThinking = false;

                    // A reply that stopped at the output limit is carried on,
                    // so a long report arrives whole. The model has to see
                    // what it wrote (and any tool results behind it), so the
                    // whole response goes back into the history first.
                    while (response.FinishReason == ChatFinishReason.Length && continues < limits.MaxContinuations)
                    {
                        continues++;
                        history.AddMessages(response);
                        history.Add(new ChatMessage(ChatRole.User, ContinuePrompt));
                        response = await chatClient.GetResponseAsync(history, options, cancellationToken);
                        if (string.IsNullOrWhiteSpace(response.Text)) break; // it used the whole limit thinking again: nothing to add
                        replyText += response.Text;
                    }

                    // Still at the limit: what was written stops part way (a
                    // report cut off in the middle of a table, say). Calling
                    // that "ok" would hide it.
                    if (response.FinishReason == ChatFinishReason.Length)
                    {
                        status = "cut short";
                        var tried = continues > 0 ? $" even after {continues} automatic continue{(continues == 1 ? "" : "s")}" : "";
                        notes.Add($"⚠️ The model's reply was cut off at the output limit ({maxOutputTokensPerReply:N0} tokens, which includes its thinking){tried}, so it is probably incomplete. Raise 'Longest reply' in Settings > Scheduler, use a model that thinks less, or ask for a shorter report.");
                    }
                    else if (continues > 0)
                    {
                        notes.Add($"Note: the reply reached the output limit and was continued automatically {continues} time{(continues == 1 ? "" : "s")}, so it arrives in {continues + 1} parts.");
                    }
                    break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The When filter narrows this to THIS job's own overall
                // time-limit token specifically, not a separate per-request
                // SDK timeout (which falls through to the generic catch
                // below instead, with its own timeout detection).
                status = "timed out";
                replyText = "⚠️ This scheduled job didn't finish before its own time limit and was cancelled." +
                    Environment.NewLine + Environment.NewLine +
                    "This usually means the model got stuck reasoning in a loop, or the job's prompt/tools made it take unusually long. Try simplifying the prompt, disabling tools it doesn't need, or asking it to keep its answer shorter.";
                jobFailed = true;
            }
            catch (Exception ex)
            {
                // A real failure (most notably Lemonade rejecting the
                // request because the prompt/tool history already exceeds
                // the model's context size) comes back as a genuine non-2xx
                // HTTP status from this non-streaming GetResponseAsync call,
                // so the OpenAI SDK throws instead of returning a normal-
                // looking response.
                var rawMessage = ex.Message;
                if (ex is System.ClientModel.ClientResultException clientResultEx)
                {
                    try
                    {
                        // Same GetRawResponse().Content pattern used
                        // elsewhere for this SDK - ex.Message alone is often
                        // just a generic summary, not Lemonade's own
                        // specific reason, which only lives in the raw
                        // response body's JSON.
                        var rawBody = clientResultEx.GetRawResponse()?.Content?.ToString();
                        if (!string.IsNullOrEmpty(rawBody))
                        {
                            using var doc = JsonDocument.Parse(rawBody);
                            if (doc.RootElement.TryGetProperty("error", out var errorProp) && errorProp.TryGetProperty("message", out var messageProp))
                            {
                                rawMessage = messageProp.GetString() ?? rawMessage;
                            }
                        }
                    }
                    catch
                    {
                        // Malformed/non-JSON body - rawMessage stays ex.Message from above.
                    }
                }

                // The SDK's own retry-exhausted message repeats the same
                // inner failure once per retry attempt, verbatim, each in
                // its own parentheses. Collapsing to the one distinct
                // reason turns that wall of repetition into one sentence.
                var isTimeoutFailure = rawMessage.Contains("Retry failed after", StringComparison.OrdinalIgnoreCase) ||
                    rawMessage.Contains("configured timeout", StringComparison.OrdinalIgnoreCase);
                if (isTimeoutFailure)
                {
                    var distinctReasons = Regex.Matches(rawMessage, @"\(([^()]*)\)")
                        .Select(m => m.Groups[1].Value.Trim())
                        .Where(r => r.Length > 0)
                        .Distinct()
                        .ToList();
                    rawMessage = distinctReasons.Count > 0
                        ? $"The request timed out and Lemonade never replied in time ({distinctReasons[0]})"
                        : "The request timed out and Lemonade never replied in time.";
                }

                var hint = rawMessage.Contains("exceeds the available context size", StringComparison.OrdinalIgnoreCase)
                    ? "Try loading this model with a larger context size, disabling modules/tools this job doesn't need, or shortening its prompt."
                    : isTimeoutFailure
                        ? "This usually means the request grew very large before Lemonade could respond - often a slow or overly broad tool call (e.g. the wrong web search provider pulling in too much content). Check this job's tools/settings, or increase Lemonade's network timeout."
                        : "Check Lemonade is running a real chat model, and that any modules this job depends on (e.g. which web search provider is selected) are configured correctly.";
                replyText = $"⚠️ This scheduled job failed to complete: {rawMessage.TrimEnd('.', ' ')}." + Environment.NewLine + Environment.NewLine + hint;
                status = "failed";
                jobFailed = true;
            }
        }
        finally
        {
            approvalStore.IsRunningScheduledJob = false;
        }

        if (string.IsNullOrWhiteSpace(replyText))
        {
            replyText = usedUpLimitThinking
                ? $"(No response - the model used up its output limit ({maxOutputTokensPerReply:N0} tokens) while thinking, so there is no answer. Raise 'Longest reply' in Settings > Scheduler or use a model that thinks less.)"
                : "(No response - the model's reasoning didn't lead to an answer.)";
            status = usedUpLimitThinking ? "cut short" : "failed";
            jobFailed = true;
        }

        sessionRepository.SaveMessage(sessionId, ChatRole.Assistant.Value, replyText, reasoningContent: null);
        notifier.RaiseSessionUpdated(sessionId);

        foreach (var note in notes)
        {
            sessionRepository.SaveMessage(sessionId, ChatRole.Assistant.Value, note, reasoningContent: null);
        }

        // When the run ended and how long it took, as the last line of its
        // chat - whatever the outcome, so a slow or timed-out job shows it too.
        var seconds = stopwatch.Elapsed.TotalSeconds;
        var endedAt = DateTime.Now;
        sessionRepository.SaveMessage(sessionId, ChatRole.Assistant.Value,
            $"This run ended at {endedAt:HH:mm} on {endedAt:dd/MM/yyyy} and took {RunDuration.Format(seconds)}.", reasoningContent: null);
        notifier.RaiseSessionUpdated(sessionId);

        // Best-effort, same as a live turn's fact extraction. Skipped on
        // jobFailed - an error message isn't a real exchange worth mining
        // for facts.
        try
        {
            if (!jobFailed)
            {
                using var extractionTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await memoryService.ExtractAndSaveFactsAsync(job.Prompt, replyText, extractionTimeoutCts.Token);
            }
        }
        catch
        {
            // Best-effort.
        }

        return new JobRunResult(status, seconds);
    }
}
