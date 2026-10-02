using LemonRindAvalonia.Data;
using LemonRindAvalonia.Services;

namespace LemonRindAvalonia.Knowledge;

public class KnowledgeBaseSummary
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>One ingested source within a Knowledge Base - a file, folder, website, or pasted text.</summary>
public class KnowledgeSource
{
    public string Id { get; set; } = "";
    public string KnowledgeBaseId { get; set; } = "";
    public string SourceType { get; set; } = "";
    public string Reference { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Status { get; set; } = "";
    public string ErrorMessage { get; set; } = "";
    public int ChunkCount { get; set; }
    public DateTime AddedAt { get; set; }
}

/// <summary>
/// Data access for RAG knowledge bases. Same brute-force cosine-similarity
/// approach as MemoryRepository, scoped per knowledge base rather than
/// global.
///
/// Ported from the VB.NET/WPF LemonRind app's Knowledge\KnowledgeRepository.vb.
/// </summary>
public class KnowledgeRepository(AppDatabase database)
{
    public string CreateKnowledgeBase(string name, string description)
    {
        var id = Guid.NewGuid().ToString();
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO KnowledgeBases (Id, Name, Description, CreatedAt)
            VALUES (@id, @name, @description, @createdAt);
            """;
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@description", string.IsNullOrWhiteSpace(description) ? DBNull.Value : description);
        command.Parameters.AddWithValue("@createdAt", DateTime.UtcNow.ToString("o"));
        command.ExecuteNonQuery();
        return id;
    }

    /// <summary>Deletes a knowledge base and everything in it (sources and chunks) - wrapped in a transaction, same pattern as ChatSessionRepository.DeleteSession.</summary>
    public void DeleteKnowledgeBase(string id)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        foreach (var tableName in new[] { "KnowledgeChunks", "KnowledgeSources" })
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {tableName} WHERE KnowledgeBaseId = @id;";
            command.Parameters.AddWithValue("@id", id);
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM KnowledgeBases WHERE Id = @id;";
            command.Parameters.AddWithValue("@id", id);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public List<KnowledgeBaseSummary> ListKnowledgeBases()
    {
        var results = new List<KnowledgeBaseSummary>();
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, Description, CreatedAt FROM KnowledgeBases ORDER BY CreatedAt ASC;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new KnowledgeBaseSummary
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Description = reader.IsDBNull(2) ? "" : reader.GetString(2),
                CreatedAt = DateTimeHelpers.ParseUtc(reader.GetString(3)),
            });
        }
        return results;
    }

    /// <summary>Creates a source row up front (Status="Ingesting") so it shows in the UI immediately, before extraction/embedding finishes - see KnowledgeIngestionService.</summary>
    public string CreateSource(string knowledgeBaseId, string sourceType, string? reference, string displayName)
    {
        var id = Guid.NewGuid().ToString();
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO KnowledgeSources (Id, KnowledgeBaseId, SourceType, Reference, DisplayName, Status, ErrorMessage, ChunkCount, AddedAt)
            VALUES (@id, @kbId, @sourceType, @reference, @displayName, 'Ingesting', NULL, 0, @addedAt);
            """;
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@kbId", knowledgeBaseId);
        command.Parameters.AddWithValue("@sourceType", sourceType);
        command.Parameters.AddWithValue("@reference", (object?)reference ?? DBNull.Value);
        command.Parameters.AddWithValue("@displayName", displayName);
        command.Parameters.AddWithValue("@addedAt", DateTime.UtcNow.ToString("o"));
        command.ExecuteNonQuery();
        return id;
    }

    public void MarkSourceReady(string sourceId, int chunkCount)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE KnowledgeSources SET Status = 'Ready', ChunkCount = @chunkCount, ErrorMessage = NULL WHERE Id = @id;";
        command.Parameters.AddWithValue("@chunkCount", chunkCount);
        command.Parameters.AddWithValue("@id", sourceId);
        command.ExecuteNonQuery();
    }

    public void MarkSourceFailed(string sourceId, string errorMessage)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE KnowledgeSources SET Status = 'Failed', ErrorMessage = @error WHERE Id = @id;";
        command.Parameters.AddWithValue("@error", errorMessage);
        command.Parameters.AddWithValue("@id", sourceId);
        command.ExecuteNonQuery();
    }

    public List<KnowledgeSource> ListSources(string knowledgeBaseId)
    {
        var results = new List<KnowledgeSource>();
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, KnowledgeBaseId, SourceType, Reference, DisplayName, Status, ErrorMessage, ChunkCount, AddedAt
            FROM KnowledgeSources WHERE KnowledgeBaseId = @kbId ORDER BY AddedAt ASC;
            """;
        command.Parameters.AddWithValue("@kbId", knowledgeBaseId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new KnowledgeSource
            {
                Id = reader.GetString(0),
                KnowledgeBaseId = reader.GetString(1),
                SourceType = reader.GetString(2),
                Reference = reader.IsDBNull(3) ? "" : reader.GetString(3),
                DisplayName = reader.GetString(4),
                Status = reader.GetString(5),
                ErrorMessage = reader.IsDBNull(6) ? "" : reader.GetString(6),
                ChunkCount = reader.GetInt32(7),
                AddedAt = DateTimeHelpers.ParseUtc(reader.GetString(8)),
            });
        }
        return results;
    }

    /// <summary>Deletes a source and its chunks - wrapped in a transaction so a failure can't leave chunks orphaned from a half-deleted source.</summary>
    public void DeleteSource(string sourceId)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM KnowledgeChunks WHERE SourceId = @id;";
            command.Parameters.AddWithValue("@id", sourceId);
            command.ExecuteNonQuery();
        }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM KnowledgeSources WHERE Id = @id;";
            command.Parameters.AddWithValue("@id", sourceId);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void SaveChunk(string knowledgeBaseId, string sourceId, int chunkIndex, string content, float[] embedding)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO KnowledgeChunks (Id, KnowledgeBaseId, SourceId, ChunkIndex, Content, Embedding, CreatedAt)
            VALUES (@id, @kbId, @sourceId, @chunkIndex, @content, @embedding, @createdAt);
            """;
        command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("@kbId", knowledgeBaseId);
        command.Parameters.AddWithValue("@sourceId", sourceId);
        command.Parameters.AddWithValue("@chunkIndex", chunkIndex);
        command.Parameters.AddWithValue("@content", content);
        command.Parameters.AddWithValue("@embedding", VectorMath.ToBytes(embedding));
        command.Parameters.AddWithValue("@createdAt", DateTime.UtcNow.ToString("o"));
        command.ExecuteNonQuery();
    }

    /// <summary>Every chunk in one knowledge base ranked by cosine similarity to queryEmbedding, highest first - callers decide how many to use and what similarity counts as "relevant enough" (see KnowledgeService).</summary>
    public List<(string Content, double Similarity)> SearchSimilar(string knowledgeBaseId, float[] queryEmbedding)
    {
        var results = new List<(string, double)>();

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Content, Embedding FROM KnowledgeChunks WHERE KnowledgeBaseId = @kbId;";
        command.Parameters.AddWithValue("@kbId", knowledgeBaseId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var content = reader.GetString(0);
            var embeddingBytes = (byte[])reader[1];
            var similarity = VectorMath.CosineSimilarity(queryEmbedding, VectorMath.FromBytes(embeddingBytes));
            results.Add((content, similarity));
        }

        return results.OrderByDescending(r => r.Item2).ToList();
    }
}
