using Microsoft.Extensions.AI;
using LemonRindBlazor.Services;

namespace LemonRindBlazor.Memories;

/// <summary>One durable fact the extraction call decided to remember.</summary>
public class ExtractedFact
{
    public string Content { get; set; } = "";

    /// <summary>True for core identity/preference facts worth always keeping in context; false for more specific facts still worth remembering but not needed every turn.</summary>
    public bool IsPinned { get; set; }
}

public class FactExtractionResult
{
    public List<ExtractedFact> Facts { get; set; } = [];
}

/// <summary>
/// Extracts durable facts about the user after each turn via structured
/// output (not free-text parsing), and builds the text MainViewModel folds
/// into the system prompt each turn (pinned facts always, semantically-
/// relevant ones on top). Ported directly from the VB.NET/WPF LemonRind
/// app's Memories\MemoryService.vb.
/// </summary>
public class MemoryService
{
    // A similarity below this isn't "relevant", it's just "the least
    // irrelevant of what's stored" - without a floor, a brand-new memory
    // store with only 1-2 unrelated facts would still inject its "closest"
    // match every turn regardless of whether it's actually related.
    private const double MinRelevantSimilarity = 0.55;
    private const int MaxRelevantMemories = 3;

    // A similarity this high isn't "related", it's "the same fact said
    // slightly differently" - without this check, fact extraction would
    // re-save essentially the same fact after almost every turn.
    private const double MinDuplicateSimilarity = 0.85;

    // The assistant's own replies can describe knowledge-base contents,
    // file/document analysis, web search results, or other tool output - a
    // looser prompt could "extract" that as if it were a fact about the
    // user. Being explicit about this failure mode is what prevents it.
    private const string ExtractionSystemPrompt =
        "You extract durable, worth-remembering facts about the USER from one exchange of a " +
        "conversation with an AI assistant. Only extract facts that would still be true and useful " +
        "in future, unrelated conversations - stable preferences, identity details (name, role, " +
        "location), ongoing projects, working style. Do not extract facts about the assistant, " +
        "one-off requests, or transient details. If nothing durable was said, return an empty list.\n\n" +
        "Critical: only extract something the user stated in their own words, about themselves. " +
        "Never extract information that merely appeared in an attached file, a knowledge base's " +
        "contents or file list, a web search/page-read result, or any other tool output - even if " +
        "the assistant's reply repeats or summarizes it. A name, company, or other detail found " +
        "INSIDE a document or webpage is NOT the user's own identity unless the user explicitly " +
        "claims it as theirs (e.g. 'my name is X'). Facts about what files/sources exist in a " +
        "knowledge base, what a search returned, or what a document contains are session context, " +
        "not durable facts about the user - never extract these.\n\n" +
        "Mark a fact isPinned=true only if it is a core identity/preference fact worth always having " +
        "in context (e.g. the user's name or role); mark isPinned=false for more specific facts still " +
        "worth remembering but not needed in every single turn.";

    private readonly IChatClient _chatClient;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly MemoryRepository _repository;

    public MemoryService(IChatClient chatClient, EmbeddingClientFactory embeddingClientFactory, MemoryRepository repository)
    {
        _chatClient = chatClient;
        _embeddingGenerator = embeddingClientFactory.CreateEmbeddingGenerator();
        _repository = repository;
    }

    /// <summary>Pinned facts, formatted for folding into the system prompt - empty string if there are none yet.</summary>
    public string GetPinnedFactsText()
    {
        var pinned = _repository.ListPinned();
        if (pinned.Count == 0) return "";

        var lines = pinned.Select(m => $"- {m.Content}");
        return "Known facts about the user:\n" + string.Join("\n", lines);
    }

    /// <summary>
    /// Semantically-relevant memories for the given user message, formatted
    /// for folding into the system prompt - empty string if nothing stored
    /// clears the relevance bar. Best-effort - never blocks a chat turn.
    /// </summary>
    public async Task<string> GetRelevantMemoriesTextAsync(string userMessage, CancellationToken cancellationToken)
    {
        try
        {
            var queryEmbeddingResult = await _embeddingGenerator.GenerateAsync([userMessage], cancellationToken: cancellationToken);
            var queryEmbedding = queryEmbeddingResult[0].Vector.ToArray();

            var relevant = _repository.SearchSimilar(queryEmbedding)
                .Where(r => r.Similarity >= MinRelevantSimilarity)
                .Take(MaxRelevantMemories)
                .ToList();

            if (relevant.Count == 0) return "";

            var lines = relevant.Select(r => $"- {r.Item.Content}");
            return "Possibly relevant things you know about the user:\n" + string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            LogBestEffortFailure(nameof(GetRelevantMemoriesTextAsync), ex);
            return "";
        }
    }

    /// <summary>All stored memories, pinned and searchable - for a future review screen.</summary>
    public List<MemoryItem> ListAllFacts() => _repository.ListAll();

    /// <summary>Permanently removes one stored memory - for a future review screen.</summary>
    public void DeleteFact(string id) => _repository.Delete(id);

    /// <summary>
    /// Edits a stored memory's text. A pinned fact is always injected
    /// verbatim (never semantically searched), so there's nothing to
    /// re-embed. A searchable fact's embedding is regenerated from the new
    /// text so future relevance searches match what it now actually says.
    /// </summary>
    public async Task UpdateFactAsync(MemoryItem item, string newContent, CancellationToken cancellationToken)
    {
        if (item.IsPinned)
        {
            _repository.UpdateContent(item.Id, newContent);
        }
        else
        {
            var embeddingResult = await _embeddingGenerator.GenerateAsync([newContent], cancellationToken: cancellationToken);
            _repository.UpdateContentAndEmbedding(item.Id, newContent, embeddingResult[0].Vector.ToArray());
        }
    }

    /// <summary>
    /// Runs after a turn completes - a separate, tools-free chat call
    /// (combining tools with structured output fails) with its own minimal
    /// message list, independent of the main conversation history. Best-
    /// effort throughout: a failed extraction just means memory doesn't
    /// grow this turn, it never affects the reply the user already
    /// received.
    /// </summary>
    public async Task ExtractAndSaveFactsAsync(string userMessage, string assistantReply, CancellationToken cancellationToken)
    {
        try
        {
            var extractionMessages = new List<ChatMessage>
            {
                new(ChatRole.System, ExtractionSystemPrompt),
                new(ChatRole.User, $"User said: \"{userMessage}\"\nAssistant replied: \"{assistantReply}\""),
            };

            var response = await _chatClient.GetResponseAsync<FactExtractionResult>(extractionMessages, cancellationToken: cancellationToken);
            var result = response.Result;
            if (result?.Facts is null) return;

            foreach (var fact in result.Facts)
            {
                if (string.IsNullOrWhiteSpace(fact.Content)) continue;

                // Every candidate fact gets embedded up front - both to
                // store (searchable facts) and, for pinned facts, purely to
                // run the duplicate check below.
                var embeddingResult = await _embeddingGenerator.GenerateAsync([fact.Content], cancellationToken: cancellationToken);
                var embedding = embeddingResult[0].Vector.ToArray();

                var closestMatch = _repository.FindMostSimilar(embedding);
                if (closestMatch is not null && closestMatch.Value.Similarity >= MinDuplicateSimilarity) continue;

                if (fact.IsPinned)
                {
                    _repository.SavePinned(fact.Content, embedding);
                }
                else
                {
                    _repository.SaveSearchable(fact.Content, embedding);
                }
            }
        }
        catch (Exception ex)
        {
            // Best-effort - see the summary above. Still logged (not just
            // swallowed) - a genuinely broken extraction path should be
            // visible somewhere, even though it never blocks the turn.
            LogBestEffortFailure(nameof(ExtractAndSaveFactsAsync), ex);
        }
    }

    /// <summary>Reuses the same crash.log file/format App.axaml.cs's own unhandled-exception handler writes to, tagged to make clear this isn't a crash.</summary>
    private static void LogBestEffortFailure(string context, Exception ex)
    {
        try
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "crash.log");
            File.AppendAllText(logPath, $"{DateTime.Now:O} [MemoryService best-effort failure in {context}]{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging itself must never throw back into a best-effort path.
        }
    }
}
