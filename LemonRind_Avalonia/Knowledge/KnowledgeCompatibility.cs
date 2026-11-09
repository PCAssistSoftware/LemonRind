namespace LemonRindAvalonia.Knowledge;

/// <summary>Raised when a knowledge base was built with a different embedding model than the one in use now.</summary>
public class KnowledgeModelMismatchException(string message) : Exception(message);

/// <summary>
/// Whether a knowledge base can be used with the embedding model in use now. A knowledge base is a pile of
/// vectors (the numbers an embedding model makes from each piece of text), and vectors from two different
/// models cannot be compared: searching one with the other gives meaningless results, or none. So each
/// knowledge base remembers the model that built it, and a different model is refused until the knowledge
/// base is re-embedded (its stored text is embedded again with the current model).
/// </summary>
public static class KnowledgeCompatibility
{
    // The knowledge bases being re-embedded right now (by id) - searching or adding to one meanwhile is refused.
    private static readonly HashSet<string> Reembedding = [];
    private static readonly object Gate = new();

    public static bool TryStartReembedding(string knowledgeBaseId)
    {
        lock (Gate) return Reembedding.Add(knowledgeBaseId);
    }

    public static void FinishReembedding(string knowledgeBaseId)
    {
        lock (Gate) Reembedding.Remove(knowledgeBaseId);
    }

    public static bool IsReembedding(string knowledgeBaseId)
    {
        lock (Gate) return Reembedding.Contains(knowledgeBaseId);
    }

    public static string MismatchMessage(KnowledgeBaseSummary kb, string currentModel) =>
        $"The knowledge base '{kb.Name}' was built with the embedding model '{kb.EmbeddingModel}', but '{currentModel}' is in use now. " +
        "Their numbers cannot be compared. Re-embed the knowledge base (Settings > Knowledge Bases) to use it with the current model.";

    /// <summary>
    /// Why this knowledge base cannot be used with the current embedding model, or "" if it can. A knowledge base whose
    /// model is not known (built before it was recorded) is allowed: the size of its vectors is compared instead, when
    /// something is searched or added, which catches the usual mismatch.
    /// </summary>
    public static string Check(KnowledgeBaseSummary kb, string currentModel)
    {
        if (IsReembedding(kb.Id)) return "It is being re-embedded at the moment, which takes a while.";
        if (!string.IsNullOrWhiteSpace(currentModel)
            && !string.IsNullOrWhiteSpace(kb.EmbeddingModel)
            && !string.Equals(kb.EmbeddingModel, currentModel, StringComparison.OrdinalIgnoreCase))
        {
            return MismatchMessage(kb, currentModel);
        }
        return "";
    }

    public static string DimensionMessage(KnowledgeBaseSummary kb, int storedSize, int currentSize) =>
        $"The vectors in the knowledge base '{kb.Name}' have {storedSize} numbers each, but the current embedding model makes {currentSize}. " +
        "Re-embed the knowledge base (Settings > Knowledge Bases) to use it with the current model.";
}
