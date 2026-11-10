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
    private readonly PictureReader _pictureReader;
    private readonly string _embeddingModel;

    /// <summary>The embedding model in use (the one this service's generator was built with) - what a knowledge base is checked against.</summary>
    public string EmbeddingModelName => _embeddingModel;

    public KnowledgeIngestionService(KnowledgeRepository repository, EmbeddingClientFactory embeddingClientFactory, WebReaderClient webReaderClient, PictureReader pictureReader)
    {
        _pictureReader = pictureReader;
        _repository = repository;
        _embeddingGenerator = embeddingClientFactory.CreateEmbeddingGenerator();
        _embeddingModel = embeddingClientFactory.ModelName;
        _webReaderClient = webReaderClient;
    }

    public Task AddFileSourceAsync(string knowledgeBaseId, string filePath, CancellationToken cancellationToken) =>
        PictureReader.IsPicture(filePath)
            ? AddPictureSourceAsync(knowledgeBaseId, filePath, cancellationToken)
            : IngestSingleTextAsync(knowledgeBaseId, "File", filePath, Path.GetFileName(filePath),
                () => Task.FromResult(FileTextExtractor.ExtractTextUncapped(filePath)), cancellationToken);

    private async Task AddPictureSourceAsync(string knowledgeBaseId, string filePath, CancellationToken cancellationToken)
    {
        var sourceId = _repository.CreateSource(knowledgeBaseId, "File", filePath, Path.GetFileName(filePath));
        await ReadPictureAsync(knowledgeBaseId, sourceId, Path.GetFileName(filePath), filePath, cancellationToken);
    }

    /// <summary>
    /// Reads a picture source again with the model that is loaded now (its first reading was poor, say). Uses the shrunk
    /// copy kept in the database, so it works even after the original file has gone.
    /// </summary>
    public async Task RereadPictureAsync(string knowledgeBaseId, string sourceId, CancellationToken cancellationToken)
    {
        var source = _repository.ListSources(knowledgeBaseId).FirstOrDefault(s => s.Id == sourceId);
        if (source is null || !PictureReader.IsPicture(source.Reference)) return;

        _repository.BeginRereading(sourceId);
        await ReadPictureAsync(knowledgeBaseId, sourceId, source.DisplayName, null, cancellationToken);
    }

    /// <summary>What the loaded vision model says about a picture inside a folder. Throws PictureModelException if no loaded model can see.</summary>
    private async Task<string> ReadPictureTextAsync(string path, CancellationToken cancellationToken)
    {
        var image = _pictureReader.ShrinkPicture(path);
        var (text, _) = await _pictureReader.DescribeAsync(image, Path.GetFileName(path), cancellationToken);
        return text;
    }

    /// <summary>Describes the picture, keeps the description on the source, then chunks and embeds it like any text.</summary>
    private async Task ReadPictureAsync(string knowledgeBaseId, string sourceId, string displayName, string? path, CancellationToken cancellationToken)
    {
        try
        {
            byte[] image;
            if (path is not null)
            {
                image = _pictureReader.ShrinkPicture(path);
                // kept before reading, so "Read again" works even if this first reading fails (the loaded model cannot
                // see pictures, say, and the person loads one that can)
                _repository.SetPicture(sourceId, null, image);
            }
            else
            {
                image = _repository.GetPictureImage(sourceId)
                    ?? throw new PictureReadException("The picture is no longer kept here to read again. Add it again.");
            }

            var (text, _) = await _pictureReader.DescribeAsync(image, displayName, cancellationToken);
            _repository.SetPicture(sourceId, text, null);

            var chunkCount = await EmbedAndSaveChunksAsync(knowledgeBaseId, sourceId, text, startingChunkIndex: 0, cancellationToken);
            if (chunkCount == 0) _repository.MarkSourceFailed(sourceId, "The description had no text.");
            else _repository.MarkSourceReady(sourceId, chunkCount);
        }
        catch (Exception ex)
        {
            _repository.MarkSourceFailed(sourceId, ex.Message);
        }
    }

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
                .Where(f => FileTextExtractor.IsSupported(f) || PictureReader.IsPicture(f)).ToList();

            var totalChunkCount = 0;
            var readableFileCount = 0;
            var skippedPictures = 0;
            string? pictureProblem = null; // why the loaded model cannot read pictures right now, once known

            foreach (var filePath in files)
            {
                try
                {
                    string text;
                    if (PictureReader.IsPicture(filePath))
                    {
                        if (pictureProblem is null)
                        {
                            try
                            {
                                text = await ReadPictureTextAsync(filePath, cancellationToken);
                            }
                            catch (PictureModelException ex)
                            {
                                pictureProblem = ex.Message; // every other picture would fail the same way
                                text = "";
                            }
                        }
                        else
                        {
                            text = "";
                        }

                        if (pictureProblem is not null)
                        {
                            skippedPictures++;
                            continue;
                        }
                    }
                    else
                    {
                        text = FileTextExtractor.ExtractTextUncapped(filePath);
                    }

                    totalChunkCount += await EmbedAndSaveChunksAsync(knowledgeBaseId, sourceId, text, totalChunkCount, cancellationToken);
                    readableFileCount++;
                }
                catch (KnowledgeModelMismatchException)
                {
                    throw; // not one bad file: the whole knowledge base cannot take this model, so stop and say why
                }
                catch
                {
                }
            }

            if (readableFileCount == 0)
            {
                _repository.MarkSourceFailed(sourceId, pictureProblem ?? (files.Count == 0 ? "No supported files found in this folder." : "None of the files in this folder could be read."));
            }
            else
            {
                var note = skippedPictures > 0 ? $"{skippedPictures} picture{(skippedPictures == 1 ? "" : "s")} not read: {pictureProblem}" : null;
                _repository.MarkSourceReady(sourceId, totalChunkCount, note);
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

        // Refuse before spending time embedding: mixing models in one knowledge base would ruin its search.
        var kb = _repository.GetKnowledgeBase(knowledgeBaseId);
        if (kb is not null)
        {
            var problem = KnowledgeCompatibility.Check(kb, _embeddingModel);
            if (problem.Length > 0) throw new KnowledgeModelMismatchException(problem);
        }

        var embeddingResults = await _embeddingGenerator.GenerateAsync(chunks, cancellationToken: cancellationToken);
        var dimensions = embeddingResults[0].Vector.Length;

        // A knowledge base that never recorded its model is checked by the size of its vectors instead.
        if (kb is not null)
        {
            var storedSize = kb.EmbeddingDimensions > 0 ? kb.EmbeddingDimensions : _repository.StoredVectorDimensions(knowledgeBaseId);
            if (storedSize > 0 && storedSize != dimensions)
            {
                throw new KnowledgeModelMismatchException(KnowledgeCompatibility.DimensionMessage(kb, storedSize, dimensions));
            }
        }

        for (var i = 0; i < chunks.Count; i++)
        {
            _repository.SaveChunk(knowledgeBaseId, sourceId, startingChunkIndex + i, chunks[i], embeddingResults[i].Vector.ToArray());
        }

        // The first time vectors are stored (or the first since this was recorded), remember which model made them.
        if (kb is not null && string.IsNullOrWhiteSpace(kb.EmbeddingModel))
        {
            _repository.SetEmbeddingModel(knowledgeBaseId, _embeddingModel, dimensions);
        }

        return chunks.Count;
    }

    /// <summary>
    /// Embeds every chunk of a knowledge base again with the current embedding model. Returns how many chunks were
    /// done. The chunk text is stored, so the original documents are not needed. The new vectors are all computed
    /// first and swapped in together, so a failure part way leaves the knowledge base as it was.
    /// </summary>
    public async Task<int> ReembedAsync(string knowledgeBaseId, CancellationToken cancellationToken)
    {
        if (!KnowledgeCompatibility.TryStartReembedding(knowledgeBaseId)) return 0;
        try
        {
            var chunks = _repository.ListChunkContents(knowledgeBaseId);
            if (chunks.Count == 0)
            {
                _repository.SetEmbeddingModel(knowledgeBaseId, _embeddingModel, 0); // nothing stored: just adopt the current model
                return 0;
            }

            const int batchSize = 32;
            var vectors = new List<(string ChunkId, float[] Vector)>(chunks.Count);
            for (var start = 0; start < chunks.Count; start += batchSize)
            {
                var batch = chunks.Skip(start).Take(batchSize).ToList();
                var results = await _embeddingGenerator.GenerateAsync(batch.Select(c => c.Content).ToList(), cancellationToken: cancellationToken);
                for (var i = 0; i < batch.Count; i++) vectors.Add((batch[i].Id, results[i].Vector.ToArray()));
            }

            _repository.ReplaceEmbeddings(knowledgeBaseId, vectors, _embeddingModel);
            return chunks.Count;
        }
        finally
        {
            KnowledgeCompatibility.FinishReembedding(knowledgeBaseId);
        }
    }
}
