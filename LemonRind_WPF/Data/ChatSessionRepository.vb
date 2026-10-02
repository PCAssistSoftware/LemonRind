Imports System.Collections.ObjectModel
Imports CommunityToolkit.Mvvm.ComponentModel
Imports Microsoft.Data.Sqlite

Namespace Data

    ''' <summary>
    ''' One row for the chats sidebar. Inherits ObservableObject (not a plain
    ''' DTO) so renaming a session updates the sidebar's binding immediately,
    ''' and so IsEditing can drive the inline-rename UI directly from this
    ''' object rather than MainViewModel tracking "which row is being edited"
    ''' separately. FolderId/FolderName/Tags are plain (not SetProperty-
    ''' backed) - unlike Title/IsEditing, moving a session between folders or
    ''' changing its tags goes through a full RefreshSessions() rebuild
    ''' (same "just refresh the whole list" pattern already used everywhere
    ''' else in this app - Sessions, Modules, AvailableModels), not live
    ''' in-place mutation of an existing object, so there's no need for
    ''' change notification on these specifically.
    ''' </summary>
    Public Class ChatSessionSummary
        Inherits ObservableObject

        Public Property Id As String

        Private _title As String
        Public Property Title As String
            Get
                Return _title
            End Get
            Set(value As String)
                SetProperty(_title, value)
            End Set
        End Property

        Public Property UpdatedAt As DateTime

        ''' <summary>Nothing if this session isn't in a folder.</summary>
        Public Property FolderId As String
        Public Property FolderName As String

        ''' <summary>Grouping key for the sidebar's CollectionViewSource - "Unfiled" (not a real folder name, just a display fallback) for a session with none, so every session has something to group by either way.</summary>
        Public ReadOnly Property FolderDisplayName As String
            Get
                Return If(String.IsNullOrEmpty(FolderName), "Unfiled", FolderName)
            End Get
        End Property

        Public ReadOnly Property Tags As New ObservableCollection(Of String)()

        Private _isEditing As Boolean = False
        Public Property IsEditing As Boolean
            Get
                Return _isEditing
            End Get
            Set(value As Boolean)
                SetProperty(_isEditing, value)
            End Set
        End Property

        ''' <summary>Title as it was before the current edit started, so Escape can revert to it.</summary>
        Friend Property TitleBeforeEdit As String
    End Class

    ''' <summary>One stored message, as loaded back when a session is opened.</summary>
    Public Class StoredChatMessage
        Public Property Role As String
        Public Property Content As String
        Public Property ReasoningContent As String
        Public Property ToolCalls As String
        Public Property CreatedAt As DateTime
    End Class

    Public Class FolderSummary
        Public Property Id As String
        Public Property Name As String
        Public Property IsCollapsed As Boolean
    End Class

    Public Class TagSummary
        Public Property Id As String
        Public Property Name As String
    End Class

    ''' <summary>
    ''' Persists chat sessions/messages to the app's SQLite database and
    ''' supports listing/searching them, including folder/tag assignment.
    ''' </summary>
    Public Class ChatSessionRepository

        Private ReadOnly _database As AppDatabase

        Public Sub New(database As AppDatabase)
            _database = database
        End Sub

        ''' <summary>Creates a new, empty session and returns its Id.</summary>
        Public Function CreateSession(title As String) As String
            Dim sessionId = Guid.NewGuid().ToString()
            Dim now = DateTime.UtcNow.ToString("o")

            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "
                        INSERT INTO Sessions (Id, Title, FolderId, CreatedAt, UpdatedAt)
                        VALUES (@id, @title, NULL, @now, @now);"
                    command.Parameters.AddWithValue("@id", sessionId)
                    command.Parameters.AddWithValue("@title", title)
                    command.Parameters.AddWithValue("@now", now)
                    command.ExecuteNonQuery()
                End Using
            End Using

            Return sessionId
        End Function

        ''' <summary>
        ''' Appends one message and bumps the session's UpdatedAt, so the
        ''' sidebar's most-recently-active-first ordering stays correct
        ''' without a separate "touch" call. toolCalls is a plain
        ''' comma-separated list of tool names, persisted so a Markdown
        ''' export can show them for any session, not just the
        ''' currently-open one.
        ''' </summary>
        Public Sub SaveMessage(sessionId As String, role As String, content As String, reasoningContent As String, Optional toolCalls As String = Nothing)
            Dim now = DateTime.UtcNow.ToString("o")

            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "
                        INSERT INTO Messages (SessionId, Role, Content, ReasoningContent, ToolCalls, CreatedAt)
                        VALUES (@sessionId, @role, @content, @reasoningContent, @toolCalls, @now);"
                    command.Parameters.AddWithValue("@sessionId", sessionId)
                    command.Parameters.AddWithValue("@role", role)
                    command.Parameters.AddWithValue("@content", content)
                    command.Parameters.AddWithValue("@reasoningContent", If(String.IsNullOrEmpty(reasoningContent), CObj(DBNull.Value), reasoningContent))
                    command.Parameters.AddWithValue("@toolCalls", If(String.IsNullOrEmpty(toolCalls), CObj(DBNull.Value), toolCalls))
                    command.Parameters.AddWithValue("@now", now)
                    command.ExecuteNonQuery()
                End Using

                Using command = connection.CreateCommand()
                    command.CommandText = "UPDATE Sessions SET UpdatedAt = @now WHERE Id = @id;"
                    command.Parameters.AddWithValue("@now", now)
                    command.Parameters.AddWithValue("@id", sessionId)
                    command.ExecuteNonQuery()
                End Using
            End Using
        End Sub

        ''' <summary>
        ''' Records how many tokens the model's context held after this
        ''' session's most recent turn, so switching back to this session
        ''' later can show that real number instead of guessing or showing 0
        ''' (Lemonade's /v1/stats only ever reports the last request, so this
        ''' is the one place that number survives across a session switch).
        ''' </summary>
        Public Sub UpdateLastContextTokens(sessionId As String, contextTokens As Integer)
            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "UPDATE Sessions SET LastContextTokens = @contextTokens WHERE Id = @id;"
                    command.Parameters.AddWithValue("@contextTokens", contextTokens)
                    command.Parameters.AddWithValue("@id", sessionId)
                    command.ExecuteNonQuery()
                End Using
            End Using
        End Sub

        ''' <summary>The context-token count saved by UpdateLastContextTokens, or Nothing if this session has never completed a turn.</summary>
        Public Function GetLastContextTokens(sessionId As String) As Integer?
            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "SELECT LastContextTokens FROM Sessions WHERE Id = @id;"
                    command.Parameters.AddWithValue("@id", sessionId)
                    Dim result = command.ExecuteScalar()
                    If result Is Nothing OrElse result Is DBNull.Value Then Return Nothing
                    Return CInt(CLng(result))
                End Using
            End Using
        End Function

        ''' <summary>Sets (or clears, with Nothing) which Knowledge Base is attached to this session - one at a time by design.</summary>
        Public Sub SetAttachedKnowledgeBase(sessionId As String, knowledgeBaseId As String)
            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "UPDATE Sessions SET AttachedKnowledgeBaseId = @kbId WHERE Id = @id;"
                    command.Parameters.AddWithValue("@kbId", If(knowledgeBaseId Is Nothing, CObj(DBNull.Value), knowledgeBaseId))
                    command.Parameters.AddWithValue("@id", sessionId)
                    command.ExecuteNonQuery()
                End Using
            End Using
        End Sub

        ''' <summary>The Knowledge Base attached to this session, or Nothing if none is.</summary>
        Public Function GetAttachedKnowledgeBase(sessionId As String) As String
            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "SELECT AttachedKnowledgeBaseId FROM Sessions WHERE Id = @id;"
                    command.Parameters.AddWithValue("@id", sessionId)
                    Dim result = command.ExecuteScalar()
                    If result Is Nothing OrElse result Is DBNull.Value Then Return Nothing
                    Return CStr(result)
                End Using
            End Using
        End Function

        Public Sub RenameSession(sessionId As String, title As String)
            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "UPDATE Sessions SET Title = @title WHERE Id = @id;"
                    command.Parameters.AddWithValue("@title", title)
                    command.Parameters.AddWithValue("@id", sessionId)
                    command.ExecuteNonQuery()
                End Using
            End Using
        End Sub

        ''' <summary>
        ''' Permanently deletes a session and its messages. Wrapped in a
        ''' transaction so a failure partway through can't leave messages
        ''' orphaned from a half-deleted session. Deleting from Messages first
        ''' fires the Messages_AfterDelete trigger (see AppDatabase.vb) that
        ''' keeps the MessagesFts search index in sync.
        ''' </summary>
        Public Sub DeleteSession(sessionId As String)
            Using connection = _database.OpenConnection()
                Using transaction = connection.BeginTransaction()
                    Using command = connection.CreateCommand()
                        command.Transaction = transaction
                        command.CommandText = "DELETE FROM Messages WHERE SessionId = @id;"
                        command.Parameters.AddWithValue("@id", sessionId)
                        command.ExecuteNonQuery()
                    End Using

                    Using command = connection.CreateCommand()
                        command.Transaction = transaction
                        command.CommandText = "DELETE FROM SessionTags WHERE SessionId = @id;"
                        command.Parameters.AddWithValue("@id", sessionId)
                        command.ExecuteNonQuery()
                    End Using

                    Using command = connection.CreateCommand()
                        command.Transaction = transaction
                        command.CommandText = "DELETE FROM Sessions WHERE Id = @id;"
                        command.Parameters.AddWithValue("@id", sessionId)
                        command.ExecuteNonQuery()
                    End Using

                    transaction.Commit()
                End Using
            End Using
        End Sub

        ''' <summary>All sessions - folders first (alphabetical), "Unfiled" sessions last, most recently active first within each group. The grouping order matters here, not just filtering - see FolderDisplayName's own comment on why PropertyGroupDescription needs the source list pre-sorted this way.</summary>
        Public Function ListSessions() As List(Of ChatSessionSummary)
            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "
                        SELECT s.Id, s.Title, s.UpdatedAt, s.FolderId, f.Name
                        FROM Sessions s
                        LEFT JOIN Folders f ON f.Id = s.FolderId
                        ORDER BY (CASE WHEN s.FolderId IS NULL THEN 1 ELSE 0 END), f.Name, s.UpdatedAt DESC;"
                    Return ReadSummaries(connection, command)
                End Using
            End Using
        End Function

        ''' <summary>
        ''' Sessions whose title contains the query, or that contain a message
        ''' matching it via the MessagesFts full-text index. Each search word
        ''' is treated as an independent prefix match (implicit AND) rather
        ''' than passing the raw query straight to FTS5's MATCH syntax, so
        ''' punctuation/quotes the user types can't produce an invalid FTS5
        ''' query string.
        ''' </summary>
        Public Function SearchSessions(query As String) As List(Of ChatSessionSummary)
            If String.IsNullOrWhiteSpace(query) Then
                Return ListSessions()
            End If

            Dim ftsQuery = BuildFtsPrefixQuery(query)

            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "
                        SELECT DISTINCT s.Id, s.Title, s.UpdatedAt, s.FolderId, f.Name
                        FROM Sessions s
                        LEFT JOIN Folders f ON f.Id = s.FolderId
                        WHERE s.Title LIKE @likeQuery
                           OR s.Id IN (
                                SELECT m.SessionId FROM Messages m
                                WHERE m.Id IN (SELECT rowid FROM MessagesFts WHERE MessagesFts MATCH @ftsQuery)
                           )
                        ORDER BY (CASE WHEN s.FolderId IS NULL THEN 1 ELSE 0 END), f.Name, s.UpdatedAt DESC;"
                    command.Parameters.AddWithValue("@likeQuery", $"%{query}%")
                    command.Parameters.AddWithValue("@ftsQuery", ftsQuery)
                    Return ReadSummaries(connection, command)
                End Using
            End Using
        End Function

        ''' <summary>All messages in a session, oldest first - ready to rebuild the chat window's history from.</summary>
        Public Function LoadMessages(sessionId As String) As List(Of StoredChatMessage)
            Dim results As New List(Of StoredChatMessage)

            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "
                        SELECT Role, Content, ReasoningContent, ToolCalls, CreatedAt FROM Messages
                        WHERE SessionId = @sessionId
                        ORDER BY Id ASC;"
                    command.Parameters.AddWithValue("@sessionId", sessionId)

                    Using reader = command.ExecuteReader()
                        While reader.Read()
                            results.Add(New StoredChatMessage With {
                                .Role = reader.GetString(0),
                                .Content = reader.GetString(1),
                                .ReasoningContent = If(reader.IsDBNull(2), "", reader.GetString(2)),
                                .ToolCalls = If(reader.IsDBNull(3), "", reader.GetString(3)),
                                .CreatedAt = ParseUtc(reader.GetString(4))
                            })
                        End While
                    End Using
                End Using
            End Using

            Return results
        End Function

#Region "Folders"

        Public Function CreateFolder(name As String) As String
            Dim id = Guid.NewGuid().ToString()
            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "INSERT INTO Folders (Id, Name) VALUES (@id, @name);"
                    command.Parameters.AddWithValue("@id", id)
                    command.Parameters.AddWithValue("@name", name)
                    command.ExecuteNonQuery()
                End Using
            End Using
            Return id
        End Function

        Public Function ListFolders() As List(Of FolderSummary)
            Dim results As New List(Of FolderSummary)
            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "SELECT Id, Name, IsCollapsed FROM Folders ORDER BY Name ASC;"
                    Using reader = command.ExecuteReader()
                        While reader.Read()
                            results.Add(New FolderSummary With {
                                .Id = reader.GetString(0),
                                .Name = reader.GetString(1),
                                .IsCollapsed = reader.GetInt64(2) <> 0
                            })
                        End While
                    End Using
                End Using
            End Using
            Return results
        End Function

        ''' <summary>Persists a folder's collapsed/expanded state in the sidebar, so it survives an app restart.</summary>
        Public Sub SetFolderCollapsed(folderId As String, isCollapsed As Boolean)
            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "UPDATE Folders SET IsCollapsed = @isCollapsed WHERE Id = @id;"
                    command.Parameters.AddWithValue("@isCollapsed", If(isCollapsed, 1, 0))
                    command.Parameters.AddWithValue("@id", folderId)
                    command.ExecuteNonQuery()
                End Using
            End Using
        End Sub

        ''' <summary>Nothing clears the assignment (moves the session to Unfiled).</summary>
        Public Sub SetSessionFolder(sessionId As String, folderId As String)
            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "UPDATE Sessions SET FolderId = @folderId WHERE Id = @id;"
                    command.Parameters.AddWithValue("@folderId", If(folderId Is Nothing, CObj(DBNull.Value), folderId))
                    command.Parameters.AddWithValue("@id", sessionId)
                    command.ExecuteNonQuery()
                End Using
            End Using
        End Sub

        ''' <summary>Deletes a folder - sessions in it become Unfiled (FolderId set NULL via the FK's default behavior here, done explicitly since SQLite FKs aren't ON DELETE SET NULL by default), not deleted themselves.</summary>
        Public Sub DeleteFolder(folderId As String)
            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "UPDATE Sessions SET FolderId = NULL WHERE FolderId = @folderId;"
                    command.Parameters.AddWithValue("@folderId", folderId)
                    command.ExecuteNonQuery()
                End Using
                Using command = connection.CreateCommand()
                    command.CommandText = "DELETE FROM Folders WHERE Id = @id;"
                    command.Parameters.AddWithValue("@id", folderId)
                    command.ExecuteNonQuery()
                End Using
            End Using
        End Sub

#End Region

#Region "Tags"

        ''' <summary>Tags a session by name, creating the tag if it doesn't already exist - the normal way tags get added, since the UI only ever deals in names, never Ids directly. Idempotent (INSERT OR IGNORE) - tagging an already-tagged session with the same name is a harmless no-op, not an error, which matters for the auto-tagging done by Scheduler/Image generation (every scheduled run, every image - not just the first).</summary>
        Public Sub AddTagToSession(sessionId As String, tagName As String)
            If String.IsNullOrWhiteSpace(tagName) Then Return
            Dim normalizedName = tagName.Trim()

            Using connection = _database.OpenConnection()
                Dim tagId As String = Nothing
                Using command = connection.CreateCommand()
                    command.CommandText = "SELECT Id FROM Tags WHERE Name = @name;"
                    command.Parameters.AddWithValue("@name", normalizedName)
                    Dim result = command.ExecuteScalar()
                    If result IsNot Nothing Then tagId = CStr(result)
                End Using

                If tagId Is Nothing Then
                    tagId = Guid.NewGuid().ToString()
                    Using command = connection.CreateCommand()
                        command.CommandText = "INSERT INTO Tags (Id, Name) VALUES (@id, @name);"
                        command.Parameters.AddWithValue("@id", tagId)
                        command.Parameters.AddWithValue("@name", normalizedName)
                        command.ExecuteNonQuery()
                    End Using
                End If

                Using command = connection.CreateCommand()
                    command.CommandText = "INSERT OR IGNORE INTO SessionTags (SessionId, TagId) VALUES (@sessionId, @tagId);"
                    command.Parameters.AddWithValue("@sessionId", sessionId)
                    command.Parameters.AddWithValue("@tagId", tagId)
                    command.ExecuteNonQuery()
                End Using
            End Using
        End Sub

        Public Sub RemoveTagFromSession(sessionId As String, tagName As String)
            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "
                        DELETE FROM SessionTags
                        WHERE SessionId = @sessionId
                          AND TagId = (SELECT Id FROM Tags WHERE Name = @name);"
                    command.Parameters.AddWithValue("@sessionId", sessionId)
                    command.Parameters.AddWithValue("@name", tagName)
                    command.ExecuteNonQuery()
                End Using
            End Using
        End Sub

        ''' <summary>Every tag that exists anywhere, alphabetical - for autocomplete/suggestion in the tag manager, not scoped to one session.</summary>
        Public Function ListAllTags() As List(Of TagSummary)
            Dim results As New List(Of TagSummary)
            Using connection = _database.OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = "SELECT Id, Name FROM Tags ORDER BY Name ASC;"
                    Using reader = command.ExecuteReader()
                        While reader.Read()
                            results.Add(New TagSummary With {.Id = reader.GetString(0), .Name = reader.GetString(1)})
                        End While
                    End Using
                End Using
            End Using
            Return results
        End Function

#End Region

        ''' <summary>
        ''' Reads Id/Title/UpdatedAt/FolderId/FolderName from the command's
        ''' own result set, then a separate all-sessions-at-once tag query
        ''' (not one query per session) merged in by SessionId - fine at
        ''' personal-chat-app scale, and avoids N+1 queries for what's
        ''' otherwise a single ListSessions()/SearchSessions() call.
        ''' </summary>
        Private Shared Function ReadSummaries(connection As SqliteConnection, command As SqliteCommand) As List(Of ChatSessionSummary)
            Dim results As New List(Of ChatSessionSummary)
            Using reader = command.ExecuteReader()
                While reader.Read()
                    results.Add(New ChatSessionSummary With {
                        .Id = reader.GetString(0),
                        .Title = reader.GetString(1),
                        .UpdatedAt = ParseUtc(reader.GetString(2)),
                        .FolderId = If(reader.IsDBNull(3), Nothing, reader.GetString(3)),
                        .FolderName = If(reader.IsDBNull(4), Nothing, reader.GetString(4))
                    })
                End While
            End Using

            If results.Count = 0 Then Return results

            Dim tagsBySession As New Dictionary(Of String, List(Of String))
            Using tagsCommand = connection.CreateCommand()
                tagsCommand.CommandText = "
                    SELECT st.SessionId, t.Name
                    FROM SessionTags st
                    JOIN Tags t ON t.Id = st.TagId
                    ORDER BY t.Name ASC;"
                Using reader = tagsCommand.ExecuteReader()
                    While reader.Read()
                        Dim sessionId = reader.GetString(0)
                        Dim tagName = reader.GetString(1)
                        If Not tagsBySession.ContainsKey(sessionId) Then tagsBySession(sessionId) = New List(Of String)
                        tagsBySession(sessionId).Add(tagName)
                    End While
                End Using
            End Using

            For Each summary In results
                If tagsBySession.ContainsKey(summary.Id) Then
                    For Each tagName In tagsBySession(summary.Id)
                        summary.Tags.Add(tagName)
                    Next
                End If
            Next

            Return results
        End Function

        Private Shared Function BuildFtsPrefixQuery(query As String) As String
            Dim words = query.Split(" "c, StringSplitOptions.RemoveEmptyEntries)
            Dim sanitizedTerms = words.Select(Function(w) $"""{w.Replace("""", "")}""*")
            Return String.Join(" ", sanitizedTerms)
        End Function

    End Class

End Namespace
