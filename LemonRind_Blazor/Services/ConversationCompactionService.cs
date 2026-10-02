using Microsoft.Extensions.AI;

namespace LemonRindBlazor.Services;

/// <summary>
/// Hand-rolled short-term memory / context compaction - trigger once
/// context usage crosses a percentage of the model's real max context
/// window, summarize the oldest messages into a running summary and drop
/// them. Ported directly from the VB.NET/WPF LemonRind app's
/// Services\ConversationCompactionService.vb.
/// </summary>
public class ConversationCompactionService(IChatClient chatClient)
{
    private const string SummarizationSystemPrompt =
        "Summarize the following conversation excerpt concisely, in plain prose. Preserve concrete " +
        "facts, decisions, names, numbers, and anything the assistant would need to continue the " +
        "conversation naturally. Omit small talk and filler. Output only the summary text, with no " +
        "preamble or commentary about the summary itself.";

    /// <summary>
    /// Summarizes a run of messages into one paragraph - a separate, tools-
    /// free chat call with its own minimal message list, independent of the
    /// conversation being summarized.
    /// </summary>
    public async Task<string> SummarizeAsync(IReadOnlyList<ChatMessage> messagesToSummarize, CancellationToken cancellationToken)
    {
        var transcript = string.Join(Environment.NewLine, messagesToSummarize.Select(m => $"{m.Role}: {m.Text}"));

        var summarizationMessages = new List<ChatMessage>
        {
            new(ChatRole.System, SummarizationSystemPrompt),
            new(ChatRole.User, transcript),
        };

        var response = await chatClient.GetResponseAsync(summarizationMessages, cancellationToken: cancellationToken);
        return response.Text;
    }
}
