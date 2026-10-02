namespace LemonRindBlazor.Knowledge;

/// <summary>
/// Word-count chunking with overlap (1000 words/200 overlap), not
/// token-aware - good enough in practice; revisit if chunk-size-vs-real-token-
/// count ever causes a problem.
///
/// Ported from the VB.NET/WPF LemonRind app's Knowledge\TextChunker.vb.
/// </summary>
public static class TextChunker
{
    private const int WordsPerChunk = 1000;
    private const int OverlapWords = 200;

    /// <summary>Splits text into overlapping chunks. A short text (under one chunk's worth of words) comes back as a single chunk, unsplit.</summary>
    public static List<string> ChunkText(string text)
    {
        var words = text.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [];

        var chunks = new List<string>();
        var startIndex = 0;
        const int stepSize = WordsPerChunk - OverlapWords;

        while (startIndex < words.Length)
        {
            var chunkWords = words.Skip(startIndex).Take(WordsPerChunk);
            chunks.Add(string.Join(' ', chunkWords));

            if (startIndex + WordsPerChunk >= words.Length) break;
            startIndex += stepSize;
        }

        return chunks;
    }
}
