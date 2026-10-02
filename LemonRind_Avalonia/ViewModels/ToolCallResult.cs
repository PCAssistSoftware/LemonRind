using CommunityToolkit.Mvvm.ComponentModel;

namespace LemonRindAvalonia.ViewModels;

/// <summary>
/// One tool call's real outcome, not just its name. Succeeded starts null
/// (call made, no result yet) rather than defaulting to true - an
/// always-true checkmark before the real result exists would be misleading,
/// so showing nothing until the real result is known matters, not just
/// cosmetically.
///
/// Ported from the VB.NET/WPF LemonRind app's ViewModels\ToolCallResult.vb.
/// </summary>
public partial class ToolCallResult : ObservableObject
{
    public required string Name { get; init; }

    /// <summary>Correlates a later FunctionResultContent back to this call - Microsoft.Extensions.AI gives both the same CallId.</summary>
    public string? CallId { get; init; }

    [ObservableProperty]
    private bool? _succeeded;

    /// <summary>The real exception message when Succeeded = false - shown as a ToolTip on the chip, same "compact indicator + hover for detail" pattern as the Lemonade-unreachable fix.</summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>"name:1" (succeeded), "name:0" (failed), "name:?" (call made, no result ever arrived - e.g. the turn was stopped mid-call), comma-separated - what actually gets persisted to Messages.ToolCalls.</summary>
    public static string SerializeForStorage(IEnumerable<ToolCallResult> toolCalls)
        => string.Join(",", toolCalls.Select(t =>
        {
            var flag = t.Succeeded is { } succeeded ? (succeeded ? "1" : "0") : "?";
            return $"{t.Name}:{flag}";
        }));

    /// <summary>Parses SerializeForStorage's format back into real objects - a bare name with no colon (the original format, before success tracking) restores fine as Succeeded=null, not an error.</summary>
    public static List<ToolCallResult> ParseFromStorage(string? stored)
    {
        var results = new List<ToolCallResult>();
        if (string.IsNullOrEmpty(stored)) return results;

        foreach (var entry in stored.Split(','))
        {
            var colonIndex = entry.LastIndexOf(':');
            if (colonIndex < 0)
            {
                results.Add(new ToolCallResult { Name = entry });
            }
            else
            {
                var name = entry[..colonIndex];
                var flag = entry[(colonIndex + 1)..];
                bool? succeeded = flag == "1" ? true : flag == "0" ? false : null;
                results.Add(new ToolCallResult { Name = name, Succeeded = succeeded });
            }
        }
        return results;
    }
}
