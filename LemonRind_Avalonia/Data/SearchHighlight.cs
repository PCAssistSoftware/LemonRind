using System.Text;
using System.Text.RegularExpressions;

namespace LemonRindAvalonia.Data;

/// <summary>One piece of a text, and whether it is a part the search matched.</summary>
public readonly record struct HighlightPart(string Text, bool IsMatch);

/// <summary>
/// Helpers for showing what a chat search matched: the title, a tag, or a few words of a message with the matched
/// words picked out. Matching is by whole typed words, ignoring upper/lower case, the same as the Python edition.
/// </summary>
public static class SearchHighlight
{
    /// <summary>The characters SQLite's snippet() is told to put round each matched word.</summary>
    public const char SnippetStart = '\u0002';
    public const char SnippetEnd = '\u0003';

    private static List<string> TypedWords(string? query) =>
        (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Distinct().OrderByDescending(w => w.Length).ToList(); // longest first, so "hiking" wins over "hik"

    /// <summary>
    /// Splits text into pieces, flagging the ones that match a word the person typed. "Plan a hike" searched for
    /// "hik" gives "Plan a ", "hik" (a match), "e". An empty query highlights nothing.
    /// </summary>
    public static List<HighlightPart> HighlightParts(string? text, string? query)
    {
        var result = new List<HighlightPart>();
        if (string.IsNullOrEmpty(text)) return result;

        var words = TypedWords(query);
        if (words.Count == 0)
        {
            result.Add(new HighlightPart(text, false));
            return result;
        }

        var pattern = new Regex(string.Join("|", words.Select(Regex.Escape)), RegexOptions.IgnoreCase);
        var position = 0;
        foreach (Match found in pattern.Matches(text))
        {
            if (found.Length == 0) continue;
            if (found.Index > position) result.Add(new HighlightPart(text[position..found.Index], false));
            result.Add(new HighlightPart(found.Value, true));
            position = found.Index + found.Length;
        }
        if (position < text.Length) result.Add(new HighlightPart(text[position..], false));
        return result;
    }

    /// <summary>
    /// A snippet of the text around the first word that starts with a typed word, in the same marker format SQLite's
    /// snippet() gives. Used for the one case the database's version cannot be used: a message whose stored text
    /// begins with an attached-picture marker (a file path) that must not be shown. Null when no word matches.
    /// </summary>
    public static string? SnippetFromText(string? text, string? query, int wordsAround = 8)
    {
        var words = TypedWords(query);
        if (words.Count == 0 || string.IsNullOrEmpty(text)) return null;

        // (?<!\w) means "not directly after a letter or digit"; a plain word boundary would not work for a typed
        // word that starts with punctuation, such as "(5".
        var pattern = new Regex(@"(?<!\w)(?:" + string.Join("|", words.Select(Regex.Escape)) + ")", RegexOptions.IgnoreCase);
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var first = Array.FindIndex(tokens, t => pattern.IsMatch(t));
        if (first < 0) return null;

        var start = Math.Max(0, first - wordsAround);
        var end = Math.Min(tokens.Length, first + wordsAround + 1);
        var window = string.Join(" ", tokens[start..end]);
        var marked = pattern.Replace(window, m => $"{SnippetStart}{m.Value}{SnippetEnd}");
        return (start > 0 ? "..." : "") + marked + (end < tokens.Length ? "..." : "");
    }

    /// <summary>Turns a snippet from the database (matches wrapped in the start/end characters) into flagged pieces.</summary>
    public static List<HighlightPart> ParseSnippet(string? raw)
    {
        var parts = new List<HighlightPart>();
        if (string.IsNullOrEmpty(raw)) return parts;

        var chunks = raw.Split(SnippetStart);
        for (var i = 0; i < chunks.Length; i++)
        {
            var chunk = chunks[i];
            var endAt = i == 0 ? -1 : chunk.IndexOf(SnippetEnd);
            if (endAt >= 0)
            {
                // this chunk began at a start marker: the text before the end marker is the match
                if (endAt > 0) parts.Add(new HighlightPart(chunk[..endAt], true));
                if (endAt + 1 < chunk.Length) parts.Add(new HighlightPart(chunk[(endAt + 1)..], false));
            }
            else if (chunk.Length > 0)
            {
                parts.Add(new HighlightPart(chunk, false));
            }
        }
        return parts;
    }

    /// <summary>Whether any typed word is found in the text (used to mark a tag chip the search matched).</summary>
    public static bool Matches(string? text, string? query) => HighlightParts(text, query).Any(p => p.IsMatch);
}
