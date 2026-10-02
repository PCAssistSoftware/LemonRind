using LemonRindBlazor.Data;
using LemonRindBlazor.Services;

namespace LemonRindBlazor.Memories;

/// <summary>One stored fact, pinned or semantic-searchable.</summary>
public class MemoryItem
{
    public string Id { get; set; } = "";
    public string Content { get; set; } = "";
    public bool IsPinned { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Data access for long-term memory: a pinned-facts + semantic-store
/// design. Search is brute-force cosine similarity over everything with an
/// embedding, computed in .NET after loading rows back from SQLite - simple,
/// and appropriate while the number of memories stays in the hundreds.
/// Ported directly from the VB.NET/WPF LemonRind app's
/// Memories\MemoryRepository.vb.
/// </summary>
public class MemoryRepository(AppDatabase database)
{
    /// <summary>
    /// Pinned facts are always injected verbatim, never semantically
    /// searched - but they still get an embedding stored, purely so
    /// FindMostSimilar can catch a re-extracted restatement of a fact
    /// already known.
    /// </summary>
    public string SavePinned(string content, float[]? embedding) => Save(content, isPinned: true, embedding);

    public string SaveSearchable(string content, float[]? embedding) => Save(content, isPinned: false, embedding);

    private string Save(string content, bool isPinned, float[]? embedding)
    {
        var id = Guid.NewGuid().ToString();
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Memories (Id, Content, IsPinned, Embedding, CreatedAt)
            VALUES (@id, @content, @isPinned, @embedding, @createdAt);
            """;
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@content", content);
        command.Parameters.AddWithValue("@isPinned", isPinned ? 1 : 0);
        command.Parameters.AddWithValue("@embedding", embedding is null ? DBNull.Value : VectorMath.ToBytes(embedding));
        command.Parameters.AddWithValue("@createdAt", DateTime.UtcNow.ToString("o"));
        command.ExecuteNonQuery();
        return id;
    }

    public List<MemoryItem> ListPinned()
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Content, IsPinned, CreatedAt FROM Memories WHERE IsPinned = 1 ORDER BY CreatedAt ASC;";
        return ReadItems(command);
    }

    /// <summary>Every stored memory, pinned and searchable - for a future review screen.</summary>
    public List<MemoryItem> ListAll()
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Content, IsPinned, CreatedAt FROM Memories ORDER BY CreatedAt DESC;";
        return ReadItems(command);
    }

    /// <summary>Every searchable (non-pinned) memory ranked by cosine similarity to queryEmbedding, highest first.</summary>
    public List<(MemoryItem Item, double Similarity)> SearchSimilar(float[] queryEmbedding)
    {
        var results = new List<(MemoryItem, double)>();

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Content, IsPinned, CreatedAt, Embedding FROM Memories WHERE IsPinned = 0 AND Embedding IS NOT NULL;";
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var item = new MemoryItem
                {
                    Id = reader.GetString(0),
                    Content = reader.GetString(1),
                    IsPinned = reader.GetInt32(2) != 0,
                    CreatedAt = DateTimeHelpers.ParseUtc(reader.GetString(3)),
                };
                var embeddingBytes = (byte[])reader[4];
                var similarity = VectorMath.CosineSimilarity(queryEmbedding, VectorMath.FromBytes(embeddingBytes));
                results.Add((item, similarity));
            }
        }

        return [.. results.OrderByDescending(r => r.Item2)];
    }

    /// <summary>
    /// The single closest existing memory to candidateEmbedding, pinned or
    /// searchable - unlike SearchSimilar, scans everything, since a
    /// re-extracted duplicate of a pinned fact is just as much a duplicate.
    /// </summary>
    public (MemoryItem Item, double Similarity)? FindMostSimilar(float[] candidateEmbedding)
    {
        (MemoryItem, double)? best = null;

        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Content, IsPinned, CreatedAt, Embedding FROM Memories WHERE Embedding IS NOT NULL;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var item = new MemoryItem
            {
                Id = reader.GetString(0),
                Content = reader.GetString(1),
                IsPinned = reader.GetInt32(2) != 0,
                CreatedAt = DateTimeHelpers.ParseUtc(reader.GetString(3)),
            };
            var embeddingBytes = (byte[])reader[4];
            var similarity = VectorMath.CosineSimilarity(candidateEmbedding, VectorMath.FromBytes(embeddingBytes));
            if (best is null || similarity > best.Value.Item2)
            {
                best = (item, similarity);
            }
        }

        return best;
    }

    public void UpdateContent(string id, string newContent)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Memories SET Content = @content WHERE Id = @id;";
        command.Parameters.AddWithValue("@content", newContent);
        command.Parameters.AddWithValue("@id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>Same as UpdateContent, but also replaces the stored embedding - for editing a searchable memory so future relevance searches match the corrected text.</summary>
    public void UpdateContentAndEmbedding(string id, string newContent, float[] embedding)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Memories SET Content = @content, Embedding = @embedding WHERE Id = @id;";
        command.Parameters.AddWithValue("@content", newContent);
        command.Parameters.AddWithValue("@embedding", VectorMath.ToBytes(embedding));
        command.Parameters.AddWithValue("@id", id);
        command.ExecuteNonQuery();
    }

    public void Delete(string id)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Memories WHERE Id = @id;";
        command.Parameters.AddWithValue("@id", id);
        command.ExecuteNonQuery();
    }

    private static List<MemoryItem> ReadItems(Microsoft.Data.Sqlite.SqliteCommand command)
    {
        var results = new List<MemoryItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new MemoryItem
            {
                Id = reader.GetString(0),
                Content = reader.GetString(1),
                IsPinned = reader.GetInt32(2) != 0,
                CreatedAt = DateTimeHelpers.ParseUtc(reader.GetString(3)),
            });
        }
        return results;
    }
}
