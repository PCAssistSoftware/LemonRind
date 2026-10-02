using Microsoft.Extensions.AI;
using LemonRindBlazor.Services;

namespace LemonRindBlazor.Knowledge;

/// <summary>
/// Chat-time retrieval from an attached Knowledge Base - mirrors
/// MemoryService.GetRelevantMemoriesTextAsync's shape (embed the query,
/// search, filter by a similarity floor, format the top few), against
/// KnowledgeRepository instead of MemoryRepository. Also always lists the
/// knowledge base's source filenames (see GetSourceManifestText) - content-
/// similarity search alone can't answer a meta-question like "what files
/// are in here", since that's a request for the source list, not
/// semantically close to any chunk's actual content.
///
/// Ported from the VB.NET/WPF LemonRind app's Knowledge\KnowledgeService.vb.
/// </summary>
public class KnowledgeService
{
    // Lower than MemoryService's 0.55 - document chunks are longer and more
    // heterogeneous than memory's short pinned-fact-style text, so a real,
    // directly-relevant question can score lower here even against a
    // genuinely matching chunk. Tuned against the currently-configured
    // embedding model, not a general constant.
    private const double MinRelevantSimilarity = 0.35;
    private const int MaxRelevantChunks = 5;

    private readonly KnowledgeRepository _repository;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;

    public KnowledgeService(KnowledgeRepository repository, EmbeddingClientFactory embeddingClientFactory)
    {
        _repository = repository;
        _embeddingGenerator = embeddingClientFactory.CreateEmbeddingGenerator();
    }

    /// <summary>
    /// The source manifest (always, if any sources are Ready) plus
    /// similarity-relevant chunks (only above the floor) for the user's
    /// message, formatted for folding into the system prompt. Chunk search
    /// is best-effort (embedding/search failure just means no chunks get
    /// added, never blocks a chat turn) - the manifest doesn't depend on
    /// it, so a search failure still leaves the file list available.
    /// </summary>
    public async Task<string> GetRelevantChunksTextAsync(string? knowledgeBaseId, string userMessage, CancellationToken cancellationToken)
    {
        // Explicit, not silence - saying nothing at all about "knowledge
        // base" would leave the model to guess what the term meant, and it
        // could guess its file system tool's sandboxed workspace (wrong)
        // rather than concluding none is attached.
        if (string.IsNullOrEmpty(knowledgeBaseId))
        {
            return "No knowledge base is attached to this chat. If asked what's in \"the knowledge base\", " +
                "say none is attached rather than checking file system tools - a knowledge base is never " +
                "the same thing as file system access, and file tools can never see one anyway.";
        }

        var sections = new List<string>();

        var manifestText = GetSourceManifestText(knowledgeBaseId);
        if (!string.IsNullOrEmpty(manifestText)) sections.Add(manifestText);

        try
        {
            var queryEmbeddingResult = await _embeddingGenerator.GenerateAsync([userMessage], cancellationToken: cancellationToken);
            var queryEmbedding = queryEmbeddingResult[0].Vector.ToArray();

            var relevant = _repository.SearchSimilar(knowledgeBaseId, queryEmbedding)
                .Where(r => r.Similarity >= MinRelevantSimilarity)
                .Take(MaxRelevantChunks)
                .ToList();

            if (relevant.Count > 0)
            {
                var lines = relevant.Select(r => $"---{Environment.NewLine}{r.Content}");
                sections.Add("Relevant content from the attached knowledge base:" + Environment.NewLine + string.Join(Environment.NewLine, lines));
            }
        }
        catch
        {
            // Best-effort - see this method's summary.
        }

        return sections.Count == 0 ? "" : string.Join(Environment.NewLine + Environment.NewLine, sections);
    }

    /// <summary>A plain list of this knowledge base's ready source filenames - lets the model correctly answer "what files/sources are in here" without relying on content-similarity search.</summary>
    private string GetSourceManifestText(string knowledgeBaseId)
    {
        var readyNames = _repository.ListSources(knowledgeBaseId)
            .Where(s => s.Status == "Ready")
            .Select(s => s.DisplayName)
            .ToList();

        if (readyNames.Count == 0) return "";

        // Explicit disambiguation from the file system module's sandboxed
        // workspace - without this, the model could conflate the two and
        // use list_directory/read_file to "double check", finding unrelated
        // workspace files instead of trusting this list.
        return "This chat has a knowledge base attached, separate from any file system access you may have - " +
            "it is not a folder you can browse or read with file tools, and file system tools cannot see it. " +
            "It currently contains these sources:" + Environment.NewLine +
            string.Join(Environment.NewLine, readyNames.Select(name => $"- {name}")) + Environment.NewLine +
            "Treat this list as authoritative for what the knowledge base contains.";
    }
}
