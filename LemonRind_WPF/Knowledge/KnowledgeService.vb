Imports System.Threading
Imports Microsoft.Extensions.AI
Imports LemonRind.Services

Namespace Knowledge

    ''' <summary>
    ''' Chat-time retrieval from an attached Knowledge Base - mirrors
    ''' MemoryService.GetRelevantMemoriesTextAsync's shape (embed the query,
    ''' search, filter by a similarity floor, format the top few), against
    ''' KnowledgeRepository instead of MemoryRepository. Also always lists the
    ''' knowledge base's source filenames (see GetSourceManifestText) -
    ''' content-similarity search alone can't answer a meta-question like
    ''' "what files are in here", since that's a request for the source
    ''' list, not semantically close to any chunk's actual content, and the
    ''' model would otherwise fall back to its (unrelated) file-system tool
    ''' instead of admitting it didn't know. A plain source manifest answers
    ''' that class of question without needing similarity search at all.
    ''' </summary>
    Public Class KnowledgeService

        ' Lower than MemoryService's 0.55 - document chunks are longer and
        ' more heterogeneous than memory's short pinned-fact-style text, so
        ' a real, directly-relevant question can score lower here even
        ' against a genuinely matching chunk; a higher floor tuned for
        ' short-fact text would silently drop it. Tuned against the
        ' currently-configured embedding model, not a general constant -
        ' revisit if Lemonade.EmbeddingModel ever changes.
        Private Const MinRelevantSimilarity = 0.35
        Private Const MaxRelevantChunks = 5

        Private ReadOnly _repository As KnowledgeRepository
        Private ReadOnly _embeddingGenerator As IEmbeddingGenerator(Of String, Embedding(Of Single))

        Public Sub New(repository As KnowledgeRepository, embeddingClientFactory As EmbeddingClientFactory)
            _repository = repository
            _embeddingGenerator = embeddingClientFactory.CreateEmbeddingGenerator()
        End Sub

        ''' <summary>
        ''' The source manifest (always, if any sources are Ready) plus
        ''' similarity-relevant chunks (only above the floor) for the user's
        ''' message, formatted for folding into the system prompt. Chunk
        ''' search is best-effort (embedding/search failure just means no
        ''' chunks get added, never blocks a chat turn) - the manifest
        ''' doesn't depend on it, so a search failure still leaves the file
        ''' list available.
        ''' </summary>
        Public Async Function GetRelevantChunksTextAsync(knowledgeBaseId As String, userMessage As String, cancellationToken As CancellationToken) As Task(Of String)
            ' Explicit, not silence - saying nothing at all about "knowledge
            ' base" would leave the model to guess what the term meant, and
            ' it could guess its file system tool's sandboxed workspace
            ' (wrong) rather than concluding none is attached. Same
            ' underlying lesson as GetSourceManifestText's disambiguation:
            ' silence doesn't reliably read as "I don't know" to the model.
            If String.IsNullOrEmpty(knowledgeBaseId) Then
                Return "No knowledge base is attached to this chat. If asked what's in ""the knowledge base"", " &
                    "say none is attached rather than checking file system tools - a knowledge base is never " &
                    "the same thing as file system access, and file tools can never see one anyway."
            End If

            Dim sections As New List(Of String)

            Dim manifestText = GetSourceManifestText(knowledgeBaseId)
            If Not String.IsNullOrEmpty(manifestText) Then sections.Add(manifestText)

            Try
                Dim queryEmbeddingResult = Await _embeddingGenerator.GenerateAsync({userMessage}, cancellationToken:=cancellationToken)
                Dim queryEmbedding = queryEmbeddingResult(0).Vector.ToArray()

                Dim relevant = _repository.SearchSimilar(knowledgeBaseId, queryEmbedding).
                    Where(Function(r) r.Similarity >= MinRelevantSimilarity).
                    Take(MaxRelevantChunks).
                    ToList()

                If relevant.Count > 0 Then
                    Dim lines = relevant.Select(Function(r) $"---{Environment.NewLine}{r.Content}")
                    sections.Add("Relevant content from the attached knowledge base:" & Environment.NewLine & String.Join(Environment.NewLine, lines))
                End If
            Catch
                ' Best-effort - see this method's summary.
            End Try

            If sections.Count = 0 Then Return ""
            Return String.Join(Environment.NewLine & Environment.NewLine, sections)
        End Function

        ''' <summary>A plain list of this knowledge base's ready source filenames - lets the model correctly answer "what files/sources are in here" without relying on content-similarity search.</summary>
        Private Function GetSourceManifestText(knowledgeBaseId As String) As String
            Dim readyNames = _repository.ListSources(knowledgeBaseId).
                Where(Function(s) s.Status = "Ready").
                Select(Function(s) s.DisplayName).
                ToList()

            If readyNames.Count = 0 Then Return ""
            ' Explicit disambiguation from the file system module's sandboxed
            ' workspace - without this, the model could conflate the two
            ' ("the knowledge base (the workspace folder)...") and use
            ' list_directory/read_file to "double check", finding unrelated
            ' workspace files instead of trusting this list. The knowledge
            ' base isn't a folder on disk the model
            ' can browse - it's this app's own separate ingested-and-embedded
            ' store, so a file tool can never see it, only this list can.
            Return "This chat has a knowledge base attached, separate from any file system access you may have - " &
                "it is not a folder you can browse or read with file tools, and file system tools cannot see it. " &
                "It currently contains these sources:" & Environment.NewLine &
                String.Join(Environment.NewLine, readyNames.Select(Function(name) $"- {name}")) & Environment.NewLine &
                "Treat this list as authoritative for what the knowledge base contains."
        End Function

    End Class

End Namespace
