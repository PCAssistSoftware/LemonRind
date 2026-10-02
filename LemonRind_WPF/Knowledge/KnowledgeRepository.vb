Imports Microsoft.Data.Sqlite
Imports LemonRind.Data
Imports LemonRind.Services

Namespace Knowledge

    Public Class KnowledgeBaseSummary
        Public Property Id As String
        Public Property Name As String
        Public Property Description As String
        Public Property CreatedAt As DateTime
    End Class

    ''' <summary>One ingested source within a Knowledge Base - a file, folder, website, or pasted text.</summary>
    Public Class KnowledgeSource
        Public Property Id As String
        Public Property KnowledgeBaseId As String
        Public Property SourceType As String
        Public Property Reference As String
        Public Property DisplayName As String
        Public Property Status As String
        Public Property ErrorMessage As String
        Public Property ChunkCount As Integer
        Public Property AddedAt As DateTime
    End Class

    ''' <summary>
    ''' Data access for RAG knowledge bases. Same brute-force cosine-
    ''' similarity approach as MemoryRepository, scoped per knowledge base
    ''' rather than global.
    ''' </summary>
    Public Class KnowledgeRepository

        Private ReadOnly _database As AppDatabase

        Public Sub New(database As AppDatabase)
            _database = database
        End Sub

        Public Function CreateKnowledgeBase(name As String, description As String) As String
            Dim id = Guid.NewGuid().ToString()
            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "
                        INSERT INTO KnowledgeBases (Id, Name, Description, CreatedAt)
                        VALUES (@id, @name, @description, @createdAt);"
                    command.Parameters.AddWithValue("@id", id)
                    command.Parameters.AddWithValue("@name", name)
                    command.Parameters.AddWithValue("@description", If(String.IsNullOrWhiteSpace(description), CObj(DBNull.Value), description))
                    command.Parameters.AddWithValue("@createdAt", DateTime.UtcNow.ToString("o"))
                    command.ExecuteNonQuery()
                End Using
            End Using
            Return id
        End Function

        Public Sub RenameKnowledgeBase(id As String, name As String, description As String)
            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "UPDATE KnowledgeBases SET Name = @name, Description = @description WHERE Id = @id;"
                    command.Parameters.AddWithValue("@name", name)
                    command.Parameters.AddWithValue("@description", If(String.IsNullOrWhiteSpace(description), CObj(DBNull.Value), description))
                    command.Parameters.AddWithValue("@id", id)
                    command.ExecuteNonQuery()
                End Using
            End Using
        End Sub

        ''' <summary>Deletes a knowledge base and everything in it (sources and chunks) - wrapped in a transaction, same pattern as ChatSessionRepository.DeleteSession.</summary>
        Public Sub DeleteKnowledgeBase(id As String)
            Using connection = _database.OpenConnection()
                Using transaction = connection.BeginTransaction()
                    For Each tableName In {"KnowledgeChunks", "KnowledgeSources"}
                        Using command = connection.CreateCommand()
                            command.Transaction = transaction
                            command.CommandText = $"DELETE FROM {tableName} WHERE KnowledgeBaseId = @id;"
                            command.Parameters.AddWithValue("@id", id)
                            command.ExecuteNonQuery()
                        End Using
                    Next

                    Using command = connection.CreateCommand()
                        command.Transaction = transaction
                        command.CommandText = "DELETE FROM KnowledgeBases WHERE Id = @id;"
                        command.Parameters.AddWithValue("@id", id)
                        command.ExecuteNonQuery()
                    End Using

                    transaction.Commit()
                End Using
            End Using
        End Sub

        Public Function ListKnowledgeBases() As List(Of KnowledgeBaseSummary)
            Dim results As New List(Of KnowledgeBaseSummary)
            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "SELECT Id, Name, Description, CreatedAt FROM KnowledgeBases ORDER BY CreatedAt ASC;"
                    Using reader = command.ExecuteReader()
                        While reader.Read()
                            results.Add(New KnowledgeBaseSummary With {
                                .Id = reader.GetString(0),
                                .Name = reader.GetString(1),
                                .Description = If(reader.IsDBNull(2), "", reader.GetString(2)),
                                .CreatedAt = ParseUtc(reader.GetString(3))
                            })
                        End While
                    End Using
                End Using
            End Using
            Return results
        End Function

        ''' <summary>Creates a source row up front (Status="Ingesting") so it shows in the UI immediately, before extraction/embedding finishes - see KnowledgeIngestionService.</summary>
        Public Function CreateSource(knowledgeBaseId As String, sourceType As String, reference As String, displayName As String) As String
            Dim id = Guid.NewGuid().ToString()
            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "
                        INSERT INTO KnowledgeSources (Id, KnowledgeBaseId, SourceType, Reference, DisplayName, Status, ErrorMessage, ChunkCount, AddedAt)
                        VALUES (@id, @kbId, @sourceType, @reference, @displayName, 'Ingesting', NULL, 0, @addedAt);"
                    command.Parameters.AddWithValue("@id", id)
                    command.Parameters.AddWithValue("@kbId", knowledgeBaseId)
                    command.Parameters.AddWithValue("@sourceType", sourceType)
                    command.Parameters.AddWithValue("@reference", If(reference Is Nothing, CObj(DBNull.Value), reference))
                    command.Parameters.AddWithValue("@displayName", displayName)
                    command.Parameters.AddWithValue("@addedAt", DateTime.UtcNow.ToString("o"))
                    command.ExecuteNonQuery()
                End Using
            End Using
            Return id
        End Function

        Public Sub MarkSourceReady(sourceId As String, chunkCount As Integer)
            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "UPDATE KnowledgeSources SET Status = 'Ready', ChunkCount = @chunkCount, ErrorMessage = NULL WHERE Id = @id;"
                    command.Parameters.AddWithValue("@chunkCount", chunkCount)
                    command.Parameters.AddWithValue("@id", sourceId)
                    command.ExecuteNonQuery()
                End Using
            End Using
        End Sub

        Public Sub MarkSourceFailed(sourceId As String, errorMessage As String)
            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "UPDATE KnowledgeSources SET Status = 'Failed', ErrorMessage = @error WHERE Id = @id;"
                    command.Parameters.AddWithValue("@error", errorMessage)
                    command.Parameters.AddWithValue("@id", sourceId)
                    command.ExecuteNonQuery()
                End Using
            End Using
        End Sub

        Public Function ListSources(knowledgeBaseId As String) As List(Of KnowledgeSource)
            Dim results As New List(Of KnowledgeSource)
            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "
                        SELECT Id, KnowledgeBaseId, SourceType, Reference, DisplayName, Status, ErrorMessage, ChunkCount, AddedAt
                        FROM KnowledgeSources WHERE KnowledgeBaseId = @kbId ORDER BY AddedAt ASC;"
                    command.Parameters.AddWithValue("@kbId", knowledgeBaseId)
                    Using reader = command.ExecuteReader()
                        While reader.Read()
                            results.Add(New KnowledgeSource With {
                                .Id = reader.GetString(0),
                                .KnowledgeBaseId = reader.GetString(1),
                                .SourceType = reader.GetString(2),
                                .Reference = If(reader.IsDBNull(3), "", reader.GetString(3)),
                                .DisplayName = reader.GetString(4),
                                .Status = reader.GetString(5),
                                .ErrorMessage = If(reader.IsDBNull(6), "", reader.GetString(6)),
                                .ChunkCount = reader.GetInt32(7),
                                .AddedAt = ParseUtc(reader.GetString(8))
                            })
                        End While
                    End Using
                End Using
            End Using
            Return results
        End Function

        ''' <summary>Deletes a source and its chunks - wrapped in a transaction so a failure can't leave chunks orphaned from a half-deleted source.</summary>
        Public Sub DeleteSource(sourceId As String)
            Using connection = _database.OpenConnection()
                Using transaction = connection.BeginTransaction()
                    Using command = connection.CreateCommand()
                        command.Transaction = transaction
                        command.CommandText = "DELETE FROM KnowledgeChunks WHERE SourceId = @id;"
                        command.Parameters.AddWithValue("@id", sourceId)
                        command.ExecuteNonQuery()
                    End Using

                    Using command = connection.CreateCommand()
                        command.Transaction = transaction
                        command.CommandText = "DELETE FROM KnowledgeSources WHERE Id = @id;"
                        command.Parameters.AddWithValue("@id", sourceId)
                        command.ExecuteNonQuery()
                    End Using

                    transaction.Commit()
                End Using
            End Using
        End Sub

        Public Sub SaveChunk(knowledgeBaseId As String, sourceId As String, chunkIndex As Integer, content As String, embedding As Single())
            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "
                        INSERT INTO KnowledgeChunks (Id, KnowledgeBaseId, SourceId, ChunkIndex, Content, Embedding, CreatedAt)
                        VALUES (@id, @kbId, @sourceId, @chunkIndex, @content, @embedding, @createdAt);"
                    command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString())
                    command.Parameters.AddWithValue("@kbId", knowledgeBaseId)
                    command.Parameters.AddWithValue("@sourceId", sourceId)
                    command.Parameters.AddWithValue("@chunkIndex", chunkIndex)
                    command.Parameters.AddWithValue("@content", content)
                    command.Parameters.AddWithValue("@embedding", VectorMath.ToBytes(embedding))
                    command.Parameters.AddWithValue("@createdAt", DateTime.UtcNow.ToString("o"))
                    command.ExecuteNonQuery()
                End Using
            End Using
        End Sub

        ''' <summary>Every chunk in one knowledge base ranked by cosine similarity to queryEmbedding, highest first - callers decide how many to use and what similarity counts as "relevant enough" (see KnowledgeService).</summary>
        Public Function SearchSimilar(knowledgeBaseId As String, queryEmbedding As Single()) As List(Of (Content As String, Similarity As Double))
            Dim results As New List(Of (String, Double))

            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "SELECT Content, Embedding FROM KnowledgeChunks WHERE KnowledgeBaseId = @kbId;"
                    command.Parameters.AddWithValue("@kbId", knowledgeBaseId)
                    Using reader = command.ExecuteReader()
                        While reader.Read()
                            Dim content = reader.GetString(0)
                            Dim embeddingBytes = CType(reader(1), Byte())
                            Dim similarity = VectorMath.CosineSimilarity(queryEmbedding, VectorMath.FromBytes(embeddingBytes))
                            results.Add((content, similarity))
                        End While
                    End Using
                End Using
            End Using

            Return results.OrderByDescending(Function(r) r.Item2).ToList()
        End Function

    End Class

End Namespace
