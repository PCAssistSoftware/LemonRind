using System.Text.Json;
using Microsoft.Extensions.AI;
using LemonRindBlazor.Configuration;

namespace LemonRindBlazor.Modules;

/// <summary>
/// Gives the model a long tool result one piece at a time, and keeps the whole
/// text so it can ask for the rest.
///
/// A long result goes into the model's limited context window, so only the
/// first piece (Settings > Modules > "Longest piece of a tool result") is
/// sent. The rest is not thrown away: the whole text is held under a short id
/// ("r1", "r2", ...), and the note at the end of the piece tells the model how
/// to read on with the built-in read_more tool. Without this a big web page
/// (or any long result) was simply cut off and the model went round in circles
/// asking for "the rest", or rebuilt the page from other pages.
///
/// Every tool a module offers is wrapped (ModuleRegistry.GetEnabledTools), so
/// it works the same for web pages from any search service, MCP servers and
/// the file tools.
/// </summary>
public class ToolResultHolder(AppSettings settings)
{
    public const string ReadMoreName = "read_more";

    // How many cut-short results are kept for read_more (the newest).
    private const int HeldResults = 20;

    private readonly object _gate = new();
    private readonly Dictionary<string, string> _held = [];
    private readonly List<string> _order = []; // oldest first
    private int _heldCount;                    // only ever grows, so an id is never reused
    private AITool? _readMoreTool;

    /// <summary>The longest piece sent to the model in one go, kept inside a sensible range.</summary>
    private int MaxChars => Math.Clamp(settings.Modules.MaxToolOutputChars, 500, 1_000_000);

    /// <summary>The built-in tool that reads on through a result that was cut short.</summary>
    public AITool ReadMoreTool => _readMoreTool ??= AIFunctionFactory.Create(
        method: (string result, int start) => ReadMore(result, start),
        name: ReadMoreName,
        description: "Read on through a tool result that was cut short. A result that is too long is shown only in part, " +
            "and ends with a note giving the result id and the number to use here as start. " +
            "result is the id from that note, for example r3. start is the character number to continue from, as given in that note.");

    /// <summary>Wraps a tool so that a long result is held and shortened; anything that is not a function is passed through.</summary>
    public AITool Wrap(AITool tool) =>
        tool is AIFunction function && function.Name != ReadMoreName ? new HoldingFunction(function, this) : tool;

    /// <summary>The result as the model will see it: unchanged if short enough, else the first piece with a note on how to read on.</summary>
    internal object? Shorten(object? result)
    {
        var text = result switch
        {
            null => null,
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement element => element.GetRawText(),
            _ => result.ToString(),
        };
        if (text is null || text.Length <= MaxChars) return result;

        string id;
        lock (_gate)
        {
            _heldCount++;
            id = $"r{_heldCount}";
            _held[id] = text;
            _order.Add(id);
            while (_order.Count > HeldResults)
            {
                _held.Remove(_order[0]);
                _order.RemoveAt(0);
            }
        }
        return Piece(id, text, 0);
    }

    /// <summary>MaxChars of the text from start, with a note on how to continue if there is more.</summary>
    private string Piece(string id, string content, int start)
    {
        var end = start + MaxChars;
        if (end < content.Length) end = TidyEnd(content, start, end);
        var piece = content.Substring(start, Math.Min(end, content.Length) - start);
        if (end >= content.Length)
        {
            return piece + (start > 0 ? $"\n[End of result {id}.]" : "");
        }
        return piece + $"\n[... {content.Length - end:N0} more characters not shown. To read on, call {ReadMoreName} with result=\"{id}\" and start={end}.]";
    }

    /// <summary>
    /// Moves a piece's end back to the end of a line (or at least a word), so a
    /// piece never stops half way through a word and the next one never starts
    /// in the middle of one. Only the last tenth of the piece is searched, so a
    /// text with no breaks at all is simply cut at the limit.
    /// </summary>
    private static int TidyEnd(string content, int start, int end)
    {
        var earliest = end - Math.Max(1, (end - start) / 10);
        foreach (var separator in new[] { '\n', ' ' })
        {
            var found = content.LastIndexOf(separator, end - 1, end - earliest);
            if (found >= earliest) return found + 1;
        }
        return end;
    }

    private string ReadMore(string result, int start)
    {
        var id = (result ?? "").Trim();
        string? content;
        string kept;
        lock (_gate)
        {
            _held.TryGetValue(id, out content);
            kept = _order.Count > 0 ? string.Join(", ", _order) : "none";
            if (content is not null)
            {
                _order.Remove(id); // still in use: keep it longer
                _order.Add(id);
            }
        }
        if (content is null)
        {
            return $"There is nothing saved as '{result}'. Only the last {HeldResults} long results are kept (now: {kept}); run the tool again to get a fresh copy.";
        }
        if (start < 0 || start >= content.Length)
        {
            return $"start must be between 0 and {content.Length - 1:N0} for result {id}.";
        }
        return Piece(id, content, start);
    }

    /// <summary>A tool whose result goes through the holder.</summary>
    private sealed class HoldingFunction(AIFunction inner, ToolResultHolder holder) : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var result = await base.InvokeCoreAsync(arguments, cancellationToken);
            return holder.Shorten(result);
        }
    }
}
