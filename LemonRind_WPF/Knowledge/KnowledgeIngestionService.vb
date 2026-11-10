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
        Private ReadOnly _embeddingModel As String
        Private ReadOnly _pictureReader As PictureReader

        ''' <summary>The embedding model in use (the one this service's generator was built with) - what a knowledge base is checked against.</summary>
        Public ReadOnly Property EmbeddingModelName As String
            Get
                Return _embeddingModel
            End Get
        End Property

        Public Sub New(repository As KnowledgeRepository, embeddingClientFactory As EmbeddingClientFactory, webReaderClient As WebReaderClient, pictureReader As PictureReader)
            _pictureReader = pictureReader
            _repository = repository
            _embeddingGenerator = embeddingClientFactory.CreateEmbeddingGenerator()
            _embeddingModel = embeddingClientFactory.ModelName
            _webReaderClient = webReaderClient
        End Sub

        Public Async Function AddFileSourceAsync(knowledgeBaseId As String, filePath As String, cancellationToken As CancellationToken) As Task
            If PictureReader.IsPicture(filePath) Then
                Dim sourceId = _repository.CreateSource(knowledgeBaseId, "File", filePath, Path.GetFileName(filePath))
                Await ReadPictureAsync(knowledgeBaseId, sourceId, Path.GetFileName(filePath), filePath, cancellationToken)
                Return
            End If

            Await IngestSingleTextAsync(knowledgeBaseId, "File", filePath, Path.GetFileName(filePath),
                Function() Task.FromResult(FileTextExtractor.ExtractTextUncapped(filePath)), cancellationToken)
        End Function

        ''' <summary>
        ''' Reads a picture source again with the model that is loaded now (its first reading was poor, say). Uses the shrunk
        ''' copy kept in the database, so it works even after the original file has gone.
        ''' </summary>
        Public Async Function RereadPictureAsync(knowledgeBaseId As String, sourceId As String, cancellationToken As CancellationToken) As Task
            Dim source = _repository.ListSources(knowledgeBaseId).FirstOrDefault(Function(s) s.Id = sourceId)
            If source Is Nothing OrElse Not PictureReader.IsPicture(source.Reference) Then Return

            _repository.BeginRereading(sourceId)
            Await ReadPictureAsync(knowledgeBaseId, sourceId, source.DisplayName, Nothing, cancellationToken)
        End Function

        ''' <summary>What the loaded vision model says about a picture inside a folder. Throws PictureModelException if no loaded model can see.</summary>
        Private Async Function ReadPictureTextAsync(pictureFile As String, cancellationToken As CancellationToken) As Task(Of String)
            Dim image = _pictureReader.ShrinkPicture(pictureFile)
            Dim description = Await _pictureReader.DescribeAsync(image, Path.GetFileName(pictureFile), cancellationToken)
            Return description.Text
        End Function

        ''' <summary>Describes the picture, keeps the description on the source, then chunks and embeds it like any text.</summary>
        Private Async Function ReadPictureAsync(knowledgeBaseId As String, sourceId As String, displayName As String, path As String, cancellationToken As CancellationToken) As Task
            Try
                Dim image As Byte()
                If path IsNot Nothing Then
                    image = _pictureReader.ShrinkPicture(path)
                    ' kept before reading, so "Read again" works even if this first reading fails (the loaded model cannot
                    ' see pictures, say, and the person loads one that can)
                    _repository.SetPicture(sourceId, Nothing, image)
                Else
                    image = _repository.GetPictureImage(sourceId)
                    If image Is Nothing Then Throw New PictureReadException("The picture is no longer kept here to read again. Add it again.")
                End If

                Dim description = Await _pictureReader.DescribeAsync(image, displayName, cancellationToken)
                _repository.SetPicture(sourceId, description.Text, Nothing)

                Dim chunkCount = Await EmbedAndSaveChunksAsync(knowledgeBaseId, sourceId, description.Text, 0, cancellationToken)
                If chunkCount = 0 Then
                    _repository.MarkSourceFailed(sourceId, "The description had no text.")
                Else
                    _repository.MarkSourceReady(sourceId, chunkCount)
                End If
            Catch ex As Exception
                _repository.MarkSourceFailed(sourceId, ex.Message)
            End Try
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
                    Where(Function(f) FileTextExtractor.IsSupported(f) OrElse PictureReader.IsPicture(f)).ToList()

                Dim totalChunkCount = 0
                Dim readableFileCount = 0
                Dim skippedPictures = 0
                Dim pictureProblem As String = Nothing ' why the loaded model cannot read pictures right now, once known

                For Each filePath In files
                    Try
                        Dim text As String
                        If PictureReader.IsPicture(filePath) Then
                            If pictureProblem Is Nothing Then
                                Try
                                    text = Await ReadPictureTextAsync(filePath, cancellationToken)
                                Catch pictureEx As PictureModelException
                                    pictureProblem = pictureEx.Message ' every other picture would fail the same way
                                End Try
                            End If
                            If pictureProblem IsNot Nothing Then
                                skippedPictures += 1
                                Continue For
                            End If
                        Else
                            text = FileTextExtractor.ExtractTextUncapped(filePath)
                        End If

                        totalChunkCount += Await EmbedAndSaveChunksAsync(knowledgeBaseId, sourceId, text, totalChunkCount, cancellationToken)
                        readableFileCount += 1
                    Catch ex As KnowledgeModelMismatchException
                        Throw ' not one bad file: the whole knowledge base cannot take this model, so stop and say why
                    Catch
                        ' Skipped - see this method's summary. One bad file
                        ' (corrupt PDF, permission error, ...) shouldn't sink
                        ' ingesting the other 49 good ones in the folder.
                    End Try
                Next

                If readableFileCount = 0 Then
                    _repository.MarkSourceFailed(sourceId, If(pictureProblem, If(files.Count = 0, "No supported files found in this folder.", "None of the files in this folder could be read.")))
                Else
                    Dim note As String = If(skippedPictures > 0, $"{skippedPictures} picture{If(skippedPictures = 1, "", "s")} not read: {pictureProblem}", Nothing)
                    _repository.MarkSourceReady(sourceId, totalChunkCount, note)
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

            ' Refuse before spending time embedding: mixing models in one knowledge base would ruin its search.
            Dim kb = _repository.GetKnowledgeBase(knowledgeBaseId)
            If kb IsNot Nothing Then
                Dim problem = KnowledgeCompatibility.Check(kb, _embeddingModel)
                If problem.Length > 0 Then Throw New KnowledgeModelMismatchException(problem)
            End If

            Dim embeddingResults = Await _embeddingGenerator.GenerateAsync(chunks, cancellationToken:=cancellationToken)
            Dim dimensions = embeddingResults(0).Vector.Length

            ' A knowledge base that never recorded its model is checked by the size of its vectors instead.
            If kb IsNot Nothing Then
                Dim storedSize = If(kb.EmbeddingDimensions > 0, kb.EmbeddingDimensions, _repository.StoredVectorDimensions(knowledgeBaseId))
                If storedSize > 0 AndAlso storedSize <> dimensions Then
                    Throw New KnowledgeModelMismatchException(KnowledgeCompatibility.DimensionMessage(kb, storedSize, dimensions))
                End If
            End If

            For i = 0 To chunks.Count - 1
                _repository.SaveChunk(knowledgeBaseId, sourceId, startingChunkIndex + i, chunks(i), embeddingResults(i).Vector.ToArray())
            Next

            ' The first time vectors are stored (or the first since this was recorded), remember which model made them.
            If kb IsNot Nothing AndAlso String.IsNullOrWhiteSpace(kb.EmbeddingModel) Then
                _repository.SetEmbeddingModel(knowledgeBaseId, _embeddingModel, dimensions)
            End If

            Return chunks.Count
        End Function

        ''' <summary>
        ''' Embeds every chunk of a knowledge base again with the current embedding model. Returns how many chunks were
        ''' done. The chunk text is stored, so the original documents are not needed. The new vectors are all computed
        ''' first and swapped in together, so a failure part way leaves the knowledge base as it was.
        ''' </summary>
        Public Async Function ReembedAsync(knowledgeBaseId As String, cancellationToken As CancellationToken) As Task(Of Integer)
            If Not KnowledgeCompatibility.TryStartReembedding(knowledgeBaseId) Then Return 0
            Try
                Dim chunks = _repository.ListChunkContents(knowledgeBaseId)
                If chunks.Count = 0 Then
                    _repository.SetEmbeddingModel(knowledgeBaseId, _embeddingModel, 0) ' nothing stored: just adopt the current model
                    Return 0
                End If

                Const batchSize = 32
                Dim vectors As New List(Of (ChunkId As String, Vector As Single()))(chunks.Count)
                For start = 0 To chunks.Count - 1 Step batchSize
                    Dim batch = chunks.Skip(start).Take(batchSize).ToList()
                    Dim results = Await _embeddingGenerator.GenerateAsync(batch.Select(Function(c) c.Content).ToList(), cancellationToken:=cancellationToken)
                    For i = 0 To batch.Count - 1
                        vectors.Add((batch(i).Id, results(i).Vector.ToArray()))
                    Next
                Next

                _repository.ReplaceEmbeddings(knowledgeBaseId, vectors, _embeddingModel)
                Return chunks.Count
            Finally
                KnowledgeCompatibility.FinishReembedding(knowledgeBaseId)
            End Try
        End Function

    End Class

End Namespace
