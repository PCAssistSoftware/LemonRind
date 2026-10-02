Imports System.IO
Imports System.Threading
Imports Microsoft.Extensions.AI
Imports LemonRind.Modules.WebReader
Imports LemonRind.Services

Namespace Knowledge

    ''' <summary>
    ''' Ingests a source into a Knowledge Base: extract/fetch → chunk → embed
    ''' → persist. Reuses FileTextExtractor (its uncapped variant -
    ''' chunking, not truncation, is what handles a large document here)
    ''' and WebReaderClient rather than building separate extraction code.
    ''' </summary>
    Public Class KnowledgeIngestionService

        ' Generous compared to WebReaderClient's default 8000 (right for
        ' folding one page into a single chat turn) - ingestion chunks the
        ' whole page, so there's no reason to cap it that tightly, just a
        ' sane outer bound against a truly pathological page.
        Private Const IngestionWebTextLimit = 500000

        Private ReadOnly _repository As KnowledgeRepository
        Private ReadOnly _embeddingGenerator As IEmbeddingGenerator(Of String, Embedding(Of Single))
        Private ReadOnly _webReaderClient As WebReaderClient

        Public Sub New(repository As KnowledgeRepository, embeddingClientFactory As EmbeddingClientFactory, webReaderClient As WebReaderClient)
            _repository = repository
            _embeddingGenerator = embeddingClientFactory.CreateEmbeddingGenerator()
            _webReaderClient = webReaderClient
        End Sub

        Public Async Function AddFileSourceAsync(knowledgeBaseId As String, filePath As String, cancellationToken As CancellationToken) As Task
            Await IngestSingleTextAsync(knowledgeBaseId, "File", filePath, Path.GetFileName(filePath),
                Function() Task.FromResult(FileTextExtractor.ExtractTextUncapped(filePath)), cancellationToken)
        End Function

        Public Async Function AddWebsiteSourceAsync(knowledgeBaseId As String, url As String, cancellationToken As CancellationToken) As Task
            Await IngestSingleTextAsync(knowledgeBaseId, "Website", url, url,
                Async Function() As Task(Of String)
                    Dim page = Await _webReaderClient.ReadAsync(url, cancellationToken, IngestionWebTextLimit)
                    Return page.Text
                End Function, cancellationToken)
        End Function

        Public Async Function AddTextSourceAsync(knowledgeBaseId As String, text As String, displayName As String, cancellationToken As CancellationToken) As Task
            Await IngestSingleTextAsync(knowledgeBaseId, "Text", Nothing, displayName,
                Function() Task.FromResult(text), cancellationToken)
        End Function

        ''' <summary>
        ''' A folder is ONE source (not one per file inside it) - a folder
        ''' shows as a single entry, and deleting it removes everything
        ''' ingested from it in one action. Each file's
        ''' extraction is isolated in its own Try so one unreadable file
        ''' doesn't fail the whole folder - only reported as failed overall
        ''' if literally nothing could be read from it.
        ''' </summary>
        Public Async Function AddFolderSourceAsync(knowledgeBaseId As String, folderPath As String, cancellationToken As CancellationToken) As Task
            Dim displayName = New DirectoryInfo(folderPath).Name
            Dim sourceId = _repository.CreateSource(knowledgeBaseId, "Folder", folderPath, displayName)

            Try
                Dim files = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories).
                    Where(Function(f) FileTextExtractor.IsSupported(f)).ToList()

                Dim totalChunkCount = 0
                Dim readableFileCount = 0

                For Each filePath In files
                    Try
                        Dim text = FileTextExtractor.ExtractTextUncapped(filePath)
                        totalChunkCount += Await EmbedAndSaveChunksAsync(knowledgeBaseId, sourceId, text, totalChunkCount, cancellationToken)
                        readableFileCount += 1
                    Catch
                        ' Skipped - see this method's summary. One bad file
                        ' (corrupt PDF, permission error, ...) shouldn't sink
                        ' ingesting the other 49 good ones in the folder.
                    End Try
                Next

                If readableFileCount = 0 Then
                    _repository.MarkSourceFailed(sourceId, If(files.Count = 0, "No supported files found in this folder.", "None of the files in this folder could be read."))
                Else
                    _repository.MarkSourceReady(sourceId, totalChunkCount)
                End If
            Catch ex As Exception
                _repository.MarkSourceFailed(sourceId, ex.Message)
            End Try
        End Function

        Private Async Function IngestSingleTextAsync(knowledgeBaseId As String, sourceType As String, reference As String, displayName As String, getText As Func(Of Task(Of String)), cancellationToken As CancellationToken) As Task
            Dim sourceId = _repository.CreateSource(knowledgeBaseId, sourceType, reference, displayName)
            Try
                Dim text = Await getText()
                Dim chunkCount = Await EmbedAndSaveChunksAsync(knowledgeBaseId, sourceId, text, startingChunkIndex:=0, cancellationToken)

                If chunkCount = 0 Then
                    _repository.MarkSourceFailed(sourceId, "Nothing to ingest - the source had no readable text.")
                Else
                    _repository.MarkSourceReady(sourceId, chunkCount)
                End If
            Catch ex As Exception
                _repository.MarkSourceFailed(sourceId, ex.Message)
            End Try
        End Function

        ''' <summary>Chunks text, embeds every chunk in one batched call (far fewer round-trips than one call per chunk), and saves them. Returns how many chunks were saved.</summary>
        Private Async Function EmbedAndSaveChunksAsync(knowledgeBaseId As String, sourceId As String, text As String, startingChunkIndex As Integer, cancellationToken As CancellationToken) As Task(Of Integer)
            Dim chunks = TextChunker.ChunkText(text)
            If chunks.Count = 0 Then Return 0

            Dim embeddingResults = Await _embeddingGenerator.GenerateAsync(chunks, cancellationToken:=cancellationToken)
            For i = 0 To chunks.Count - 1
                _repository.SaveChunk(knowledgeBaseId, sourceId, startingChunkIndex + i, chunks(i), embeddingResults(i).Vector.ToArray())
            Next

            Return chunks.Count
        End Function

    End Class

End Namespace
