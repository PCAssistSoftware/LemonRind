Imports Microsoft.Data.Sqlite
Imports LemonRind.Data
Imports LemonRind.Services

' Namespace is "Memories" (plural), not "Memory" - .NET already has a very
' well-known System.Memory(Of T) type, and declaring a namespace with the
' exact same name would risk ambiguity anywhere both are in scope together
' (e.g. this project's own VectorMath.vb already uses
' ReadOnlyMemory(Of Single)).
Namespace Memories

    ''' <summary>One stored fact, pinned or semantic-searchable.</summary>
    Public Class MemoryItem
        Public Property Id As String
        Public Property Content As String
        Public Property IsPinned As Boolean
        Public Property CreatedAt As DateTime
    End Class

    ''' <summary>
    ''' Data access for long-term memory: a pinned-facts + semantic-store
    ''' design. Search is brute-force cosine similarity over everything with
    ''' an embedding, computed in .NET after loading rows back from SQLite
    ''' (not a SQL-level vector search) - simple, and appropriate while the
    ''' number of memories stays in the hundreds, not needing a real vector
    ''' index.
    ''' </summary>
    Public Class MemoryRepository

        Private ReadOnly _database As AppDatabase

        Public Sub New(database As AppDatabase)
            _database = database
        End Sub

        ''' <summary>
        ''' Pinned facts are always injected verbatim, never semantically
        ''' searched - but they still get an embedding stored, purely so
        ''' FindMostSimilar can catch a re-extracted restatement of a fact
        ''' already known (see MemoryService.ExtractAndSaveFactsAsync).
        ''' </summary>
        Public Function SavePinned(content As String, embedding As Single()) As String
            Return Save(content, isPinned:=True, embedding:=embedding)
        End Function

        Public Function SaveSearchable(content As String, embedding As Single()) As String
            Return Save(content, isPinned:=False, embedding:=embedding)
        End Function

        Private Function Save(content As String, isPinned As Boolean, embedding As Single()) As String
            Dim id = Guid.NewGuid().ToString()
            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "
                        INSERT INTO Memories (Id, Content, IsPinned, Embedding, CreatedAt)
                        VALUES (@id, @content, @isPinned, @embedding, @createdAt);"
                    command.Parameters.AddWithValue("@id", id)
                    command.Parameters.AddWithValue("@content", content)
                    command.Parameters.AddWithValue("@isPinned", If(isPinned, 1, 0))
                    command.Parameters.AddWithValue("@embedding", If(embedding Is Nothing, CObj(DBNull.Value), VectorMath.ToBytes(embedding)))
                    command.Parameters.AddWithValue("@createdAt", DateTime.UtcNow.ToString("o"))
                    command.ExecuteNonQuery()
                End Using
            End Using
            Return id
        End Function

        Public Function ListPinned() As List(Of MemoryItem)
            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "SELECT Id, Content, IsPinned, CreatedAt FROM Memories WHERE IsPinned = 1 ORDER BY CreatedAt ASC;"
                    Return ReadItems(command)
                End Using
            End Using
        End Function

        ''' <summary>Every stored memory, pinned and searchable - for the review screen.</summary>
        Public Function ListAll() As List(Of MemoryItem)
            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "SELECT Id, Content, IsPinned, CreatedAt FROM Memories ORDER BY CreatedAt DESC;"
                    Return ReadItems(command)
                End Using
            End Using
        End Function

        ''' <summary>
        ''' Every searchable (non-pinned) memory ranked by cosine similarity
        ''' to queryEmbedding, highest first - callers decide how many to use
        ''' and what similarity counts as "relevant enough" (see
        ''' MemoryService), this just returns the full ranked list.
        ''' </summary>
        Public Function SearchSimilar(queryEmbedding As Single()) As List(Of (Item As MemoryItem, Similarity As Double))
            Dim results As New List(Of (MemoryItem, Double))

            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "SELECT Id, Content, IsPinned, CreatedAt, Embedding FROM Memories WHERE IsPinned = 0 AND Embedding IS NOT NULL;"
                    Using reader = command.ExecuteReader()
                        While reader.Read()
                            Dim item As New MemoryItem With {
                                .Id = reader.GetString(0),
                                .Content = reader.GetString(1),
                                .IsPinned = reader.GetInt32(2) <> 0,
                                .CreatedAt = ParseUtc(reader.GetString(3))
                            }
                            Dim embeddingBytes = CType(reader(4), Byte())
                            Dim similarity = VectorMath.CosineSimilarity(queryEmbedding, VectorMath.FromBytes(embeddingBytes))
                            results.Add((item, similarity))
                        End While
                    End Using
                End Using
            End Using

            Return results.OrderByDescending(Function(r) r.Item2).ToList()
        End Function

        ''' <summary>
        ''' The single closest existing memory to candidateEmbedding, pinned
        ''' or searchable - unlike SearchSimilar (which only looks at
        ''' searchable memories, for context injection), this scans
        ''' everything, since a re-extracted duplicate of a *pinned* fact is
        ''' just as much a duplicate as a re-extracted searchable one. Nothing
        ''' if there are no memories with an embedding yet.
        ''' </summary>
        Public Function FindMostSimilar(candidateEmbedding As Single()) As (Item As MemoryItem, Similarity As Double)?
            Dim best As (MemoryItem, Double)? = Nothing

            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "SELECT Id, Content, IsPinned, CreatedAt, Embedding FROM Memories WHERE Embedding IS NOT NULL;"
                    Using reader = command.ExecuteReader()
                        While reader.Read()
                            Dim item As New MemoryItem With {
                                .Id = reader.GetString(0),
                                .Content = reader.GetString(1),
                                .IsPinned = reader.GetInt32(2) <> 0,
                                .CreatedAt = ParseUtc(reader.GetString(3))
                            }
                            Dim embeddingBytes = CType(reader(4), Byte())
                            Dim similarity = VectorMath.CosineSimilarity(candidateEmbedding, VectorMath.FromBytes(embeddingBytes))
                            If best Is Nothing OrElse similarity > best.Value.Item2 Then
                                best = (item, similarity)
                            End If
                        End While
                    End Using
                End Using
            End Using

            Return best
        End Function

        Public Sub UpdateContent(id As String, newContent As String)
            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "UPDATE Memories SET Content = @content WHERE Id = @id;"
                    command.Parameters.AddWithValue("@content", newContent)
                    command.Parameters.AddWithValue("@id", id)
                    command.ExecuteNonQuery()
                End Using
            End Using
        End Sub

        ''' <summary>
        ''' Same as UpdateContent, but also replaces the stored embedding -
        ''' for editing a searchable (non-pinned) memory from the review
        ''' screen, where the old embedding would otherwise keep matching
        ''' searches against text that no longer exists (see MemoryService.UpdateFactAsync).
        ''' </summary>
        Public Sub UpdateContentAndEmbedding(id As String, newContent As String, embedding As Single())
            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "UPDATE Memories SET Content = @content, Embedding = @embedding WHERE Id = @id;"
                    command.Parameters.AddWithValue("@content", newContent)
                    command.Parameters.AddWithValue("@embedding", VectorMath.ToBytes(embedding))
                    command.Parameters.AddWithValue("@id", id)
                    command.ExecuteNonQuery()
                End Using
            End Using
        End Sub

        Public Sub Delete(id As String)
            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "DELETE FROM Memories WHERE Id = @id;"
                    command.Parameters.AddWithValue("@id", id)
                    command.ExecuteNonQuery()
                End Using
            End Using
        End Sub

        Private Shared Function ReadItems(command As SqliteCommand) As List(Of MemoryItem)
            Dim results As New List(Of MemoryItem)
            Using reader = command.ExecuteReader()
                While reader.Read()
                    results.Add(New MemoryItem With {
                        .Id = reader.GetString(0),
                        .Content = reader.GetString(1),
                        .IsPinned = reader.GetInt32(2) <> 0,
                        .CreatedAt = ParseUtc(reader.GetString(3))
                    })
                End While
            End Using
            Return results
        End Function

    End Class

End Namespace
