namespace LemonRindBlazor.Data;

/// <summary>One row loaded back from Messages - ready to rebuild the chat window's history from.</summary>
public class StoredChatMessage
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    public string? ReasoningContent { get; set; }
    public DateTime CreatedAt { get; set; }

    // The tool calls made for this message, as written by
    // ToolCallResult.SerializeForStorage (older rows hold just comma-separated
    // tool names, e.g. "search_web,read_webpage").
    public string? ToolCalls { get; set; }
}

public class FolderSummary
{
    public string? Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsCollapsed { get; set; }
}

public class TagSummary
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>
/// Data-access layer for chat sessions/messages, including folder/tag
/// assignment.
///
/// Ported from the VB.NET/WPF LemonRind app's Data\ChatSessionRepository.vb.
/// Folders/Tags/SessionTags tables (including the Folders.IsCollapsed
/// column) are created in AppDatabase.cs; this class holds the queries
/// that use them.
/// </summary>
public class ChatSessionRepository(AppDatabase database)
{
    public string CreateSession(string title)
    {
        var id = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow.ToString("o");

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Sessions (Id, Title, FolderId, CreatedAt, UpdatedAt)
            VALUES (@id, @title, NULL, @now, @now);
            """;
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@title", title);
        command.Parameters.AddWithValue("@now", now);
        command.ExecuteNonQuery();
        return id;
    }

    public void SaveMessage(string sessionId, string role, string content, string? reasoningContent, string? toolCalls = null)
    {
        var now = DateTime.UtcNow.ToString("o");
        using var connection = database.OpenConnection();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO Messages (SessionId, Role, Content, ReasoningContent, ToolCalls, CreatedAt)
                VALUES (@sessionId, @role, @content, @reasoningContent, @toolCalls, @now);
                """;
            command.Parameters.AddWithValue("@sessionId", sessionId);
            command.Parameters.AddWithValue("@role", role);
            command.Parameters.AddWithValue("@content", content);
            command.Parameters.AddWithValue("@reasoningContent", (object?)reasoningContent ?? DBNull.Value);
            command.Parameters.AddWithValue("@toolCalls", (object?)toolCalls ?? DBNull.Value);
            command.Parameters.AddWithValue("@now", now);
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE Sessions SET UpdatedAt = @now WHERE Id = @id;";
            command.Parameters.AddWithValue("@now", now);
            command.Parameters.AddWithValue("@id", sessionId);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Which Knowledge Base (if any) is attached to this session, persisted so switching back to a chat later remembers it.</summary>
    public void SetAttachedKnowledgeBase(string sessionId, string? knowledgeBaseId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Sessions SET AttachedKnowledgeBaseId = @kbId WHERE Id = @id;";
        command.Parameters.AddWithValue("@kbId", (object?)knowledgeBaseId ?? DBNull.Value);
        command.Parameters.AddWithValue("@id", sessionId);
        command.ExecuteNonQuery();
    }

    public string? GetAttachedKnowledgeBase(string sessionId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT AttachedKnowledgeBaseId FROM Sessions WHERE Id = @id;";
        command.Parameters.AddWithValue("@id", sessionId);
        var result = command.ExecuteScalar();
        return result is null or DBNull ? null : (string)result;
    }

    /// <summary>Idempotent get-or-create by name (INSERT OR IGNORE) - safe to call on every scheduled-job run/image generation without piling up duplicate tags.</summary>
    public void AddTagToSession(string sessionId, string tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName)) return;
        var normalizedName = tagName.Trim();

        using var connection = database.OpenConnection();

        string? tagId = null;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Id FROM Tags WHERE Name = @name;";
            command.Parameters.AddWithValue("@name", normalizedName);
            var result = command.ExecuteScalar();
            if (result is not null) tagId = (string)result;
        }

        if (tagId is null)
        {
            tagId = Guid.NewGuid().ToString();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO Tags (Id, Name) VALUES (@id, @name);";
            command.Parameters.AddWithValue("@id", tagId);
            command.Parameters.AddWithValue("@name", normalizedName);
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT OR IGNORE INTO SessionTags (SessionId, TagId) VALUES (@sessionId, @tagId);";
            command.Parameters.AddWithValue("@sessionId", sessionId);
            command.Parameters.AddWithValue("@tagId", tagId);
            command.ExecuteNonQuery();
        }
    }

    public void RemoveTagFromSession(string sessionId, string tagName)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM SessionTags
            WHERE SessionId = @sessionId
              AND TagId = (SELECT Id FROM Tags WHERE Name = @name);
            """;
        command.Parameters.AddWithValue("@sessionId", sessionId);
        command.Parameters.AddWithValue("@name", tagName);
        command.ExecuteNonQuery();
    }

    /// <summary>Every tag that exists anywhere, alphabetical - for autocomplete/suggestion in the tag manager, not scoped to one session.</summary>
    public List<TagSummary> ListAllTags()
    {
        var results = new List<TagSummary>();
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name FROM Tags ORDER BY Name ASC;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new TagSummary { Id = reader.GetString(0), Name = reader.GetString(1) });
        }
        return results;
    }

    public string CreateFolder(string name)
    {
        var id = Guid.NewGuid().ToString();
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Folders (Id, Name) VALUES (@id, @name);";
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@name", name);
        command.ExecuteNonQuery();
        return id;
    }

    public List<FolderSummary> ListFolders()
    {
        var results = new List<FolderSummary>();
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, IsCollapsed FROM Folders ORDER BY Name ASC;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new FolderSummary
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                IsCollapsed = reader.GetInt64(2) != 0,
            });
        }
        return results;
    }

    /// <summary>Persists a folder's collapsed/expanded state in the sidebar, so it survives an app restart.</summary>
    public void SetFolderCollapsed(string folderId, bool isCollapsed)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Folders SET IsCollapsed = @isCollapsed WHERE Id = @id;";
        command.Parameters.AddWithValue("@isCollapsed", isCollapsed ? 1 : 0);
        command.Parameters.AddWithValue("@id", folderId);
        command.ExecuteNonQuery();
    }

    /// <summary>Null folderId clears the assignment (moves the session to Unfiled).</summary>
    public void SetSessionFolder(string sessionId, string? folderId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Sessions SET FolderId = @folderId WHERE Id = @id;";
        command.Parameters.AddWithValue("@folderId", (object?)folderId ?? DBNull.Value);
        command.Parameters.AddWithValue("@id", sessionId);
        command.ExecuteNonQuery();
    }

    /// <summary>Deletes a folder - sessions in it become Unfiled, not deleted themselves.</summary>
    public void DeleteFolder(string folderId)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE Sessions SET FolderId = NULL WHERE FolderId = @folderId;";
            command.Parameters.AddWithValue("@folderId", folderId);
            command.ExecuteNonQuery();
        }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM Folders WHERE Id = @id;";
            command.Parameters.AddWithValue("@id", folderId);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void RenameSession(string sessionId, string title)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Sessions SET Title = @title WHERE Id = @id;";
        command.Parameters.AddWithValue("@title", title);
        command.Parameters.AddWithValue("@id", sessionId);
        command.ExecuteNonQuery();
    }

    /// <summary>Deletes a session and its messages together - a session with no messages left behind would just be sidebar clutter.</summary>
    public void DeleteSession(string sessionId)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM SessionTags WHERE SessionId = @id;";
            command.Parameters.AddWithValue("@id", sessionId);
            command.ExecuteNonQuery();
        }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM Messages WHERE SessionId = @id;";
            command.Parameters.AddWithValue("@id", sessionId);
            command.ExecuteNonQuery();
        }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM Sessions WHERE Id = @id;";
            command.Parameters.AddWithValue("@id", sessionId);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>All sessions - folders first (alphabetical), "Unfiled" sessions last, most recently active first within each group.</summary>
    public List<ChatSessionSummary> ListSessions()
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.Id, s.Title, s.UpdatedAt, s.FolderId, f.Name
            FROM Sessions s
            LEFT JOIN Folders f ON f.Id = s.FolderId
            ORDER BY (CASE WHEN s.FolderId IS NULL THEN 1 ELSE 0 END), f.Name, s.UpdatedAt DESC;
            """;
        return ReadSummaries(connection, command);
    }

    /// <summary>
    /// Sessions whose title, folder name or a tag contains the query, or that contain a message
    /// matching it via the MessagesFts full-text index. Each search word is
    /// treated as an independent prefix match (implicit AND) rather than
    /// passing the raw query straight to FTS5's MATCH syntax, so
    /// punctuation/quotes the user types can't produce an invalid FTS5
    /// query string.
    /// </summary>
    public List<ChatSessionSummary> SearchSessions(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return ListSessions();

        var ftsQuery = BuildFtsPrefixQuery(query);

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT s.Id, s.Title, s.UpdatedAt, s.FolderId, f.Name
            FROM Sessions s
            LEFT JOIN Folders f ON f.Id = s.FolderId
            WHERE s.Title LIKE @likeQuery
               OR f.Name LIKE @likeQuery
               OR s.Id IN (
                    SELECT st.SessionId FROM SessionTags st JOIN Tags t ON t.Id = st.TagId WHERE t.Name LIKE @likeQuery
               )
               OR s.Id IN (
                    SELECT m.SessionId FROM Messages m
                    WHERE m.Id IN (SELECT rowid FROM MessagesFts WHERE MessagesFts MATCH @ftsQuery)
               )
            ORDER BY (CASE WHEN s.FolderId IS NULL THEN 1 ELSE 0 END), f.Name, s.UpdatedAt DESC;
            """;
        command.Parameters.AddWithValue("@likeQuery", $"%{query}%");
        command.Parameters.AddWithValue("@ftsQuery", ftsQuery);
        return ReadSummaries(connection, command);
    }

    /// <summary>All messages in a session, oldest first.</summary>
    public List<StoredChatMessage> LoadMessages(string sessionId)
    {
        var results = new List<StoredChatMessage>();

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Role, Content, ReasoningContent, CreatedAt, ToolCalls FROM Messages
            WHERE SessionId = @sessionId
            ORDER BY Id ASC;
            """;
        command.Parameters.AddWithValue("@sessionId", sessionId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new StoredChatMessage
            {
                Role = reader.GetString(0),
                Content = reader.GetString(1),
                ReasoningContent = reader.IsDBNull(2) ? null : reader.GetString(2),
                CreatedAt = DateTimeHelpers.ParseUtc(reader.GetString(3)),
                ToolCalls = reader.IsDBNull(4) ? null : reader.GetString(4),
            });
        }

        return results;
    }

    /// <summary>
    /// Reads Id/Title/UpdatedAt/FolderId/FolderName from the command's own
    /// result set, then a separate all-sessions-at-once tag query (not one
    /// query per session) merged in by SessionId - avoids N+1 queries for
    /// what's otherwise a single ListSessions()/SearchSessions() call.
    /// </summary>
    private static List<ChatSessionSummary> ReadSummaries(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteCommand command)
    {
        var results = new List<ChatSessionSummary>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                results.Add(new ChatSessionSummary
                {
                    Id = reader.GetString(0),
                    Title = reader.GetString(1),
                    UpdatedAt = DateTimeHelpers.ParseUtc(reader.GetString(2)),
                    FolderId = reader.IsDBNull(3) ? null : reader.GetString(3),
                    FolderName = reader.IsDBNull(4) ? null : reader.GetString(4),
                });
            }
        }

        if (results.Count == 0) return results;

        var tagsBySession = new Dictionary<string, List<string>>();
        using (var tagsCommand = connection.CreateCommand())
        {
            tagsCommand.CommandText = """
                SELECT st.SessionId, t.Name
                FROM SessionTags st
                JOIN Tags t ON t.Id = st.TagId
                ORDER BY t.Name ASC;
                """;
            using var reader = tagsCommand.ExecuteReader();
            while (reader.Read())
            {
                var sessionId = reader.GetString(0);
                var tagName = reader.GetString(1);
                if (!tagsBySession.TryGetValue(sessionId, out var list))
                {
                    list = [];
                    tagsBySession[sessionId] = list;
                }
                list.Add(tagName);
            }
        }

        foreach (var summary in results)
        {
            if (tagsBySession.TryGetValue(summary.Id, out var tags))
            {
                foreach (var tagName in tags) summary.Tags.Add(tagName);
            }
        }

        return results;
    }

    private static string BuildFtsPrefixQuery(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sanitizedTerms = words.Select(w => $"\"{w.Replace("\"", "")}\"*");
        return string.Join(" ", sanitizedTerms);
    }
}
