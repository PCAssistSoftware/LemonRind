using Microsoft.Extensions.AI;
using LemonRindBlazor.Modules.WebReader;
using LemonRindBlazor.Services;

namespace LemonRindBlazor.Knowledge;

/// <summary>
/// Ingests a source into a Knowledge Base: extract/fetch → chunk → embed →
/// persist. Reuses FileTextExtractor (its uncapped variant - chunking, not
/// truncation, is what handles a large document here) and WebReaderClient
/// rather than building separate extraction code.
///
/// Ported from the VB.NET/WPF LemonRind app's Knowledge\KnowledgeIngestionService.vb.
/// </summary>
public class KnowledgeIngestionService
{
    // Generous compared to WebReaderClient's default 8000 (right for
    // folding one page into a single chat turn) - ingestion chunks the
    // whole page, so there's no reason to cap it that tightly, just a sane
    // outer bound against a truly pathological page.
    private const int IngestionWebTextLimit = 500_000;

    private readonly KnowledgeRepository _repository;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly WebReaderClient _webReaderClient;

    public KnowledgeIngestionService(KnowledgeRepository repository, EmbeddingClientFactory embeddingClientFactory, WebReaderClient webReaderClient)
    {
        _repository = repository;
        _embeddingGenerator = embeddingClientFactory.CreateEmbeddingGenerator();
        _webReaderClient = webReaderClient;
    }

    public Task AddFileSourceAsync(string knowledgeBaseId, string filePath, CancellationToken cancellationToken) =>
        IngestSingleTextAsync(knowledgeBaseId, "File", filePath, Path.GetFileName(filePath),
            () => Task.FromResult(FileTextExtractor.ExtractTextUncapped(filePath)), cancellationToken);

    public Task AddWebsiteSourceAsync(string knowledgeBaseId, string url, CancellationToken cancellationToken) =>
        IngestSingleTextAsync(knowledgeBaseId, "Website", url, url,
            async () =>
            {
                var page = await _webReaderClient.ReadAsync(url, cancellationToken, IngestionWebTextLimit);
                return page.Text;
            }, cancellationToken);

    public Task AddTextSourceAsync(string knowledgeBaseId, string text, string displayName, CancellationToken cancellationToken) =>
        IngestSingleTextAsync(knowledgeBaseId, "Text", null, displayName, () => Task.FromResult(text), cancellationToken);

    /// <summary>
    /// A folder is ONE source (not one per file inside it) - a folder shows
    /// as a single entry, and deleting it removes everything ingested from
    /// it in one action. Each file's extraction is isolated in its own try
    /// so one unreadable file doesn't fail the whole folder - only reported
    /// as failed overall if literally nothing could be read from it.
    /// </summary>
    public async Task AddFolderSourceAsync(string knowledgeBaseId, string folderPath, CancellationToken cancellationToken)
    {
        var displayName = new DirectoryInfo(folderPath).Name;
        var sourceId = _repository.CreateSource(knowledgeBaseId, "Folder", folderPath, displayName);

        try
        {
            var files = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
                .Where(FileTextExtractor.IsSupported).ToList();

            var totalChunkCount = 0;
            var readableFileCount = 0;

            foreach (var filePath in files)
            {
                try
                {
                    var text = FileTextExtractor.ExtractTextUncapped(filePath);
                    totalChunkCount += await EmbedAndSaveChunksAsync(knowledgeBaseId, sourceId, text, totalChunkCount, cancellationToken);
                    readableFileCount++;
                }
                catch
                {
                    // Skipped - see this method's summary. One bad file
                    // (corrupt PDF, permission error, ...) shouldn't sink
                    // ingesting the other 49 good ones in the folder.
                }
            }

            if (readableFileCount == 0)
            {
                _repository.MarkSourceFailed(sourceId, files.Count == 0 ? "No supported files found in this folder." : "None of the files in this folder could be read.");
            }
            else
            {
                _repository.MarkSourceReady(sourceId, totalChunkCount);
            }
        }
        catch (Exception ex)
        {
            _repository.MarkSourceFailed(sourceId, ex.Message);
        }
    }

    private async Task IngestSingleTextAsync(string knowledgeBaseId, string sourceType, string? reference, string displayName, Func<Task<string>> getText, CancellationToken cancellationToken)
    {
        var sourceId = _repository.CreateSource(knowledgeBaseId, sourceType, reference, displayName);
        try
        {
            var text = await getText();
            var chunkCount = await EmbedAndSaveChunksAsync(knowledgeBaseId, sourceId, text, startingChunkIndex: 0, cancellationToken);

            if (chunkCount == 0)
            {
                _repository.MarkSourceFailed(sourceId, "Nothing to ingest - the source had no readable text.");
            }
            else
            {
                _repository.MarkSourceReady(sourceId, chunkCount);
            }
        }
        catch (Exception ex)
        {
            _repository.MarkSourceFailed(sourceId, ex.Message);
        }
    }

    /// <summary>Chunks text, embeds every chunk in one batched call (far fewer round-trips than one call per chunk), and saves them. Returns how many chunks were saved.</summary>
    private async Task<int> EmbedAndSaveChunksAsync(string knowledgeBaseId, string sourceId, string text, int startingChunkIndex, CancellationToken cancellationToken)
    {
        var chunks = TextChunker.ChunkText(text);
        if (chunks.Count == 0) return 0;

        var embeddingResults = await _embeddingGenerator.GenerateAsync(chunks, cancellationToken: cancellationToken);
        for (var i = 0; i < chunks.Count; i++)
        {
            _repository.SaveChunk(knowledgeBaseId, sourceId, startingChunkIndex + i, chunks[i], embeddingResults[i].Vector.ToArray());
        }

        return chunks.Count;
    }
}
