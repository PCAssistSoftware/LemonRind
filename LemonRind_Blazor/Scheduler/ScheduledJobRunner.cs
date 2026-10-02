using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using LemonRindBlazor.Data;
using LemonRindBlazor.Memories;
using LemonRindBlazor.Modules;
using LemonRindBlazor.Modules.FileSystem;
using LemonRindBlazor.Services;

namespace LemonRindBlazor.Scheduler;

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
    IChatClient chatClient,
    Func<ModuleRegistry> moduleRegistry,
    ChatSessionRepository sessionRepository,
    MemoryService memoryService,
    SchedulerNotifier notifier,
    FileWriteApprovalStore approvalStore)
{
    // A fixed fallback prompt, independent of AssistantSettings.SystemPrompt -
    // a scheduled job's system prompt doesn't currently track a user-edited
    // System Prompt from Settings.
    private const string SystemPrompt = "You are a helpful local AI assistant running on the user's own machine.";

    public async Task RunJobAsync(ScheduledJob job, CancellationToken cancellationToken)
    {
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
        // of output tokens on its own, eating the whole job timeout.
        const int maxOutputTokensPerReply = 16384;

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
                    if (!string.IsNullOrWhiteSpace(replyText)) break;
                    attempt++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The When filter narrows this to THIS job's own overall
                // time-limit token specifically, not a separate per-request
                // SDK timeout (which falls through to the generic catch
                // below instead, with its own timeout detection).
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
                jobFailed = true;
            }
        }
        finally
        {
            approvalStore.IsRunningScheduledJob = false;
        }

        if (string.IsNullOrWhiteSpace(replyText))
        {
            replyText = "(No response - the model's reasoning didn't lead to an answer.)";
        }

        sessionRepository.SaveMessage(sessionId, ChatRole.Assistant.Value, replyText, reasoningContent: null);
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
    }
}
