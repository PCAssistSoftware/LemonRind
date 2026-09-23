Imports System.Threading
Imports Microsoft.Extensions.AI

Namespace Services

    ''' <summary>
    ''' Hand-rolled short-term memory / context compaction - the same mental
    ''' model as Microsoft.Agents.AI.Compaction's
    ''' ContextWindowCompactionStrategy (trigger once context usage crosses a
    ''' percentage of the model's real max context window), without pulling
    ''' in the whole Agent Framework layer just for this. One strategy only -
    ''' summarize the oldest messages into a running summary and drop them -
    ''' not the full graduated pipeline (trim tool results, drop turns,
    ''' summarize) Microsoft.Agents.AI.Compaction offers; appropriate for
    ''' this app's actual scale, not a hypothetical one.
    ''' </summary>
    Public Class ConversationCompactionService

        Private Const SummarizationSystemPrompt =
            "Summarize the following conversation excerpt concisely, in plain prose. Preserve concrete " &
            "facts, decisions, names, numbers, and anything the assistant would need to continue the " &
            "conversation naturally. Omit small talk and filler. Output only the summary text, with no " &
            "preamble or commentary about the summary itself."

        Private ReadOnly _chatClient As IChatClient

        Public Sub New(chatClient As IChatClient)
            _chatClient = chatClient
        End Sub

        ''' <summary>
        ''' Summarizes a run of messages into one paragraph - a separate,
        ''' tools-free chat call (same reasoning as MemoryService's fact
        ''' extraction: a fresh, minimal, Qwen-chat-template-compliant
        ''' message list of its own, independent of the conversation being
        ''' summarized) so it can't be affected by whatever tools the main
        ''' conversation has in play.
        ''' </summary>
        Public Async Function SummarizeAsync(messagesToSummarize As IReadOnlyList(Of ChatMessage), cancellationToken As CancellationToken) As Task(Of String)
            Dim transcript = String.Join(Environment.NewLine, messagesToSummarize.Select(Function(m) $"{m.Role}: {m.Text}"))

            Dim summarizationMessages As New List(Of ChatMessage) From {
                New ChatMessage(ChatRole.System, SummarizationSystemPrompt),
                New ChatMessage(ChatRole.User, transcript)
            }

            Dim response = Await _chatClient.GetResponseAsync(summarizationMessages, cancellationToken:=cancellationToken)
            Return response.Text
        End Function

    End Class

End Namespace
