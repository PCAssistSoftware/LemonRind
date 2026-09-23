Imports System.Threading
Imports Microsoft.Extensions.AI
Imports LemonRind.Services

Namespace Memories

    ''' <summary>One durable fact the extraction call decided to remember.</summary>
    Public Class ExtractedFact
        Public Property Content As String

        ''' <summary>True for core identity/preference facts worth always keeping in context; false for more specific facts that are still worth remembering but don't need to be.</summary>
        Public Property IsPinned As Boolean
    End Class

    Public Class FactExtractionResult
        Public Property Facts As List(Of ExtractedFact)
    End Class

    ''' <summary>
    ''' Extracts durable facts about the user after each turn via
    ''' structured output (not free-text parsing), and builds the text
    ''' MainViewModel folds into the system prompt each turn (pinned facts
    ''' always, semantically-relevant ones on top).
    ''' </summary>
    Public Class MemoryService

        ' A similarity below this isn't "relevant", it's just "the least
        ' irrelevant of what's stored" - without a floor, a brand-new memory
        ' store with only 1-2 unrelated facts would still inject its
        ' "closest" match every single turn regardless of whether it's
        ' actually related to what the user just said. Tuned against the
        ' currently-configured embedding model, not a general constant -
        ' revisit if Lemonade.EmbeddingModel ever changes.
        Private Const MinRelevantSimilarity = 0.55
        Private Const MaxRelevantMemories = 3

        ' A similarity this high isn't "related", it's "the same fact said
        ' slightly differently" (e.g. "User's name is Darren." vs "The
        ' user's name is Darren.") - without this check, fact extraction
        ' would re-save essentially the same fact after almost every turn
        ' (since it has no visibility into what's already stored), silently
        ' filling the pinned-facts list with near-duplicates.
        Private Const MinDuplicateSimilarity = 0.85

        ' The assistant's own replies can describe knowledge-base contents,
        ' file/document analysis, web search results, or other tool output -
        ' a looser prompt could "extract" that as if it were a fact about
        ' the user (e.g. "the user's knowledge base contains invoice.pdf"
        ' pinned as a fact, or a name found INSIDE an analyzed document
        ' extracted as "the user's name"), which would resurface in later,
        ' unrelated chats and visibly confuse the model. Being explicit
        ' about this failure mode, not just "durable facts" in the
        ' abstract, is what prevents it.
        Private Shared ReadOnly ExtractionSystemPrompt As String =
            "You extract durable, worth-remembering facts about the USER from one exchange of a " &
            "conversation with an AI assistant. Only extract facts that would still be true and useful " &
            "in future, unrelated conversations - stable preferences, identity details (name, role, " &
            "location), ongoing projects, working style. Do not extract facts about the assistant, " &
            "one-off requests, or transient details. If nothing durable was said, return an empty list. " &
            Environment.NewLine & Environment.NewLine &
            "Critical: only extract something the user stated in their own words, about themselves. " &
            "Never extract information that merely appeared in an attached file, a knowledge base's " &
            "contents or file list, a web search/page-read result, or any other tool output - even if " &
            "the assistant's reply repeats or summarizes it. A name, company, or other detail found " &
            "INSIDE a document or webpage is NOT the user's own identity unless the user explicitly " &
            "claims it as theirs (e.g. 'my name is X'). Facts about what files/sources exist in a " &
            "knowledge base, what a search returned, or what a document contains are session context, " &
            "not durable facts about the user - never extract these." &
            Environment.NewLine & Environment.NewLine &
            "Mark a fact isPinned=true only if it is a core identity/preference fact worth always having " &
            "in context (e.g. the user's name or role); mark isPinned=false for more specific facts still " &
            "worth remembering but not needed in every single turn."

        Private ReadOnly _chatClient As IChatClient
        Private ReadOnly _embeddingGenerator As IEmbeddingGenerator(Of String, Embedding(Of Single))
        Private ReadOnly _repository As MemoryRepository

        Public Sub New(chatClient As IChatClient, embeddingClientFactory As EmbeddingClientFactory, repository As MemoryRepository)
            _chatClient = chatClient
            _embeddingGenerator = embeddingClientFactory.CreateEmbeddingGenerator()
            _repository = repository
        End Sub

        ''' <summary>Pinned facts, formatted for folding into the system prompt - empty string if there are none yet.</summary>
        Public Function GetPinnedFactsText() As String
            Dim pinned = _repository.ListPinned()
            If pinned.Count = 0 Then Return ""

            Dim lines = pinned.Select(Function(m) $"- {m.Content}")
            Return "Known facts about the user:" & Environment.NewLine & String.Join(Environment.NewLine, lines)
        End Function

        ''' <summary>
        ''' Semantically-relevant memories for the given user message,
        ''' formatted for folding into the system prompt - empty string if
        ''' nothing stored clears the relevance bar (including if there's
        ''' nothing stored at all, or if embedding/search fails - this is
        ''' best-effort, not something that should ever block a chat turn).
        ''' </summary>
        Public Async Function GetRelevantMemoriesTextAsync(userMessage As String, cancellationToken As CancellationToken) As Task(Of String)
            Try
                Dim queryEmbeddingResult = Await _embeddingGenerator.GenerateAsync({userMessage}, cancellationToken:=cancellationToken)
                Dim queryEmbedding = queryEmbeddingResult(0).Vector.ToArray()

                Dim relevant = _repository.SearchSimilar(queryEmbedding).
                    Where(Function(r) r.Similarity >= MinRelevantSimilarity).
                    Take(MaxRelevantMemories).
                    ToList()

                If relevant.Count = 0 Then Return ""

                Dim lines = relevant.Select(Function(r) $"- {r.Item.Content}")
                Return "Possibly relevant things you know about the user:" & Environment.NewLine & String.Join(Environment.NewLine, lines)
            Catch ex As Exception
                LogBestEffortFailure(NameOf(GetRelevantMemoriesTextAsync), ex)
                Return ""
            End Try
        End Function

        ''' <summary>All stored memories, pinned and searchable - for the Memories review screen.</summary>
        Public Function ListAllFacts() As List(Of MemoryItem)
            Return _repository.ListAll()
        End Function

        ''' <summary>Permanently removes one stored memory - for the Memories review screen.</summary>
        Public Sub DeleteFact(id As String)
            _repository.Delete(id)
        End Sub

        ''' <summary>
        ''' Edits a stored memory's text from the review screen. A pinned
        ''' fact is always injected verbatim (never semantically searched),
        ''' so there's nothing to re-embed - generating one here anyway
        ''' would be a wasted embedding call plus a stored vector that
        ''' FindMostSimilar/SearchSimilar would never read back for a pinned
        ''' fact. A searchable fact's embedding is regenerated from
        ''' the new text so future relevance searches match what it now
        ''' actually says, not the stale text it was first extracted with.
        ''' </summary>
        Public Async Function UpdateFactAsync(item As MemoryItem, newContent As String, cancellationToken As CancellationToken) As Task
            If item.IsPinned Then
                _repository.UpdateContent(item.Id, newContent)
            Else
                Dim embeddingResult = Await _embeddingGenerator.GenerateAsync({newContent}, cancellationToken:=cancellationToken)
                _repository.UpdateContentAndEmbedding(item.Id, newContent, embeddingResult(0).Vector.ToArray())
            End If
        End Function

        ''' <summary>
        ''' Runs after a turn completes - a separate, tools-free chat call
        ''' (combining tools with structured output fails) with its own
        ''' minimal message list
        ''' satisfying Qwen's chat-template rules (system message first, at
        ''' least one user message) independent of the main conversation
        ''' history. Best-effort throughout: a failed extraction just means
        ''' memory doesn't grow this turn, it never affects the reply the
        ''' user already received.
        ''' </summary>
        Public Async Function ExtractAndSaveFactsAsync(userMessage As String, assistantReply As String, cancellationToken As CancellationToken) As Task
            Try
                Dim extractionMessages As New List(Of ChatMessage) From {
                    New ChatMessage(ChatRole.System, ExtractionSystemPrompt),
                    New ChatMessage(ChatRole.User, $"User said: ""{userMessage}""{Environment.NewLine}Assistant replied: ""{assistantReply}""")
                }

                Dim response = Await _chatClient.GetResponseAsync(Of FactExtractionResult)(extractionMessages, cancellationToken:=cancellationToken)
                Dim result = response.Result
                If result Is Nothing OrElse result.Facts Is Nothing Then Return

                For Each fact In result.Facts
                    If String.IsNullOrWhiteSpace(fact.Content) Then Continue For

                    ' Every candidate fact gets embedded up front - both to
                    ' store (searchable facts) and, for pinned facts, purely
                    ' to run the duplicate check below (see
                    ' MemoryRepository.SavePinned's comment).
                    Dim embeddingResult = Await _embeddingGenerator.GenerateAsync({fact.Content}, cancellationToken:=cancellationToken)
                    Dim embedding = embeddingResult(0).Vector.ToArray()

                    Dim closestMatch = _repository.FindMostSimilar(embedding)
                    If closestMatch IsNot Nothing AndAlso closestMatch.Value.Similarity >= MinDuplicateSimilarity Then
                        Continue For
                    End If

                    If fact.IsPinned Then
                        _repository.SavePinned(fact.Content, embedding)
                    Else
                        _repository.SaveSearchable(fact.Content, embedding)
                    End If
                Next
            Catch ex As Exception
                ' Best-effort - see the summary above. Still logged (not
                ' just swallowed) - a genuinely broken extraction path (a
                ' broken embedding model, a SQLite failure) should be
                ' visible somewhere, even though it correctly never blocks
                ' or fails the turn itself.
                LogBestEffortFailure(NameOf(ExtractAndSaveFactsAsync), ex)
            End Try
        End Function

        ''' <summary>
        ''' Reuses the same crash.log file/format Application.xaml.vb's own
        ''' DispatcherUnhandledException handler already writes to, rather
        ''' than inventing a second logging mechanism for one class - this
        ''' isn't a crash (the caller always continues normally), so the
        ''' entry is tagged to make that distinction clear when read later.
        ''' </summary>
        Private Shared Sub LogBestEffortFailure(context As String, ex As Exception)
            Try
                Dim logPath = IO.Path.Combine(AppContext.BaseDirectory, "crash.log")
                IO.File.AppendAllText(logPath, $"{DateTime.Now:O} [MemoryService best-effort failure in {context}]{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}")
            Catch
                ' Logging itself must never throw back into a best-effort path.
            End Try
        End Sub

    End Class

End Namespace
