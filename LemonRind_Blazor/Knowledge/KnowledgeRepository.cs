using LemonRindBlazor.Data;
using LemonRindBlazor.Services;

namespace LemonRindBlazor.Knowledge;

public class KnowledgeBaseSummary
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime CreatedAt { get; set; }

    /// <summary>The embedding model that built this knowledge base's vectors ("" if not recorded - built before it was kept).</summary>
    public string EmbeddingModel { get; set; } = "";

    /// <summary>How many numbers each stored vector has (0 if not recorded).</summary>
    public int EmbeddingDimensions { get; set; }
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

    /// <summary>What a vision model wrote about a picture source; empty for any other source.</summary>
    public string PictureText { get; set; } = "";

    /// <summary>A file source that is a picture (read by a vision model rather than as text).</summary>
    public bool IsPicture => SourceType == "File" && Services.PictureReader.IsPicture(Reference);

    public bool HasPictureText => PictureText.Length > 0;

    /// <summary>Whether "Read again" is offered: a picture that is not being read right now.</summary>
    public bool CanReadAgain => IsPicture && Status != "Ingesting";
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
        command.CommandText = "SELECT Id, Name, Description, CreatedAt, EmbeddingModel, EmbeddingDimensions FROM KnowledgeBases ORDER BY CreatedAt ASC;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new KnowledgeBaseSummary
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Description = reader.IsDBNull(2) ? "" : reader.GetString(2),
                CreatedAt = DateTimeHelpers.ParseUtc(reader.GetString(3)),
                EmbeddingModel = reader.IsDBNull(4) ? "" : reader.GetString(4),
                EmbeddingDimensions = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
            });
        }
        return results;
    }

    public KnowledgeBaseSummary? GetKnowledgeBase(string id) => ListKnowledgeBases().FirstOrDefault(kb => kb.Id == id);

    /// <summary>Records which embedding model built a knowledge base's vectors, and how many numbers each has.</summary>
    public void SetEmbeddingModel(string knowledgeBaseId, string model, int dimensions)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE KnowledgeBases SET EmbeddingModel = @model, EmbeddingDimensions = @dimensions WHERE Id = @id;";
        command.Parameters.AddWithValue("@model", string.IsNullOrWhiteSpace(model) ? DBNull.Value : model);
        command.Parameters.AddWithValue("@dimensions", dimensions > 0 ? dimensions : DBNull.Value);
        command.Parameters.AddWithValue("@id", knowledgeBaseId);
        command.ExecuteNonQuery();
    }

    /// <summary>How many numbers each stored vector has, read from the first stored chunk (0 if there are no chunks) - the way to compare a knowledge base that never recorded its model.</summary>
    public int StoredVectorDimensions(string knowledgeBaseId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT length(Embedding) FROM KnowledgeChunks WHERE KnowledgeBaseId = @kbId LIMIT 1;";
        command.Parameters.AddWithValue("@kbId", knowledgeBaseId);
        var bytes = command.ExecuteScalar();
        return bytes is long length ? (int)(length / sizeof(float)) : 0;
    }

    /// <summary>Every chunk's id and text, in order - the text is what gets embedded again when a knowledge base is re-embedded, so the original documents are not needed.</summary>
    public List<(string Id, string Content)> ListChunkContents(string knowledgeBaseId)
    {
        var results = new List<(string, string)>();
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Content FROM KnowledgeChunks WHERE KnowledgeBaseId = @kbId ORDER BY SourceId, ChunkIndex;";
        command.Parameters.AddWithValue("@kbId", knowledgeBaseId);
        using var reader = command.ExecuteReader();
        while (reader.Read()) results.Add((reader.GetString(0), reader.GetString(1)));
        return results;
    }

    /// <summary>
    /// Swaps in new vectors for chunks and records the model that made them, all in one transaction: a failure part way
    /// leaves the knowledge base exactly as it was, never half old vectors and half new.
    /// </summary>
    public void ReplaceEmbeddings(string knowledgeBaseId, IReadOnlyList<(string ChunkId, float[] Vector)> vectors, string model)
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        foreach (var (chunkId, vector) in vectors)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE KnowledgeChunks SET Embedding = @embedding WHERE Id = @id;";
            command.Parameters.AddWithValue("@embedding", VectorMath.ToBytes(vector));
            command.Parameters.AddWithValue("@id", chunkId);
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE KnowledgeBases SET EmbeddingModel = @model, EmbeddingDimensions = @dimensions WHERE Id = @id;";
            command.Parameters.AddWithValue("@model", string.IsNullOrWhiteSpace(model) ? DBNull.Value : model);
            command.Parameters.AddWithValue("@dimensions", vectors.Count > 0 ? vectors[0].Vector.Length : DBNull.Value);
            command.Parameters.AddWithValue("@id", knowledgeBaseId);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
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

    /// <summary>Keeps what a vision model wrote about a picture and/or a shrunk copy of the picture on its source. Null leaves that part as it is.</summary>
    public void SetPicture(string sourceId, string? text, byte[]? image)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE KnowledgeSources
            SET PictureText = CASE WHEN @hasText = 1 THEN @text ELSE PictureText END,
                PictureImage = CASE WHEN @hasImage = 1 THEN @image ELSE PictureImage END
            WHERE Id = @id;
            """;
        command.Parameters.AddWithValue("@hasText", text is null ? 0 : 1);
        command.Parameters.AddWithValue("@text", (object?)text ?? DBNull.Value);
        command.Parameters.AddWithValue("@hasImage", image is null ? 0 : 1);
        command.Parameters.AddWithValue("@image", (object?)image ?? DBNull.Value);
        command.Parameters.AddWithValue("@id", sourceId);
        command.ExecuteNonQuery();
    }

    /// <summary>The shrunk copy of a picture source, or null.</summary>
    public byte[]? GetPictureImage(string sourceId)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT PictureImage FROM KnowledgeSources WHERE Id = @id;";
        command.Parameters.AddWithValue("@id", sourceId);
        return command.ExecuteScalar() is byte[] bytes ? bytes : null;
    }

    /// <summary>Puts a source back into the Ingesting state and throws away its chunks, ready to be read again.</summary>
    public void BeginRereading(string sourceId)
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
            command.CommandText = "UPDATE KnowledgeSources SET Status = 'Ingesting', ErrorMessage = NULL, ChunkCount = 0 WHERE Id = @id;";
            command.Parameters.AddWithValue("@id", sourceId);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
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
            SELECT Id, KnowledgeBaseId, SourceType, Reference, DisplayName, Status, ErrorMessage, ChunkCount, AddedAt, PictureText
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
                PictureText = reader.IsDBNull(9) ? "" : reader.GetString(9),
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
            // A chunk whose vector has a different size was made by a different embedding model: comparing it would
            // be meaningless (or read past the end), so it is left out.
            if (embeddingBytes.Length != queryEmbedding.Length * sizeof(float)) continue;
            var similarity = VectorMath.CosineSimilarity(queryEmbedding, VectorMath.FromBytes(embeddingBytes));
            results.Add((content, similarity));
        }

        return results.OrderByDescending(r => r.Item2).ToList();
    }
}
