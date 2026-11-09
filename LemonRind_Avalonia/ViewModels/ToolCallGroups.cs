using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.RegularExpressions;

namespace LemonRindAvalonia.ViewModels;

/// <summary>
/// One badge in the tool-call row: every call to one tool that ended the same way, with a count
/// ("web_search x 12"). Calls that succeeded and calls that failed stay as separate badges, so a
/// failure is never hidden in a total.
/// </summary>
public sealed class ToolCallGroup(string name, bool? succeeded, int count, string tooltip)
{
    public string Name { get; } = name;
    public bool? Succeeded { get; } = succeeded;
    public int Count { get; } = count;

    /// <summary>What you see on hover: short versions of the real errors for failed calls, else how the calls ended.</summary>
    public string Tooltip { get; } = tooltip;

    public string Label => Count > 1 ? $"{Name} x {Count}" : Name;
}

/// <summary>
/// The badges for a list of tool calls, kept in step with it: a long scheduled job or a chat that uses
/// tools a lot makes dozens of calls to the same few tools, and a row per call is unreadable. The groups
/// are rebuilt whenever a call is added or a call's outcome arrives (Succeeded / ErrorMessage change).
/// </summary>
public sealed class ToolCallGroups
{
    public ObservableCollection<ToolCallGroup> Groups { get; } = [];

    private ObservableCollection<ToolCallResult>? _source;
    private readonly List<ToolCallResult> _hooked = [];

    public ToolCallGroups(ObservableCollection<ToolCallResult>? source = null) => Attach(source);

    /// <summary>Follow a different list of calls (the right panel is pointed at the current reply's list).</summary>
    public void Attach(ObservableCollection<ToolCallResult>? source)
    {
        if (_source is not null) _source.CollectionChanged -= OnSourceChanged;
        foreach (var item in _hooked) item.PropertyChanged -= OnItemChanged;
        _hooked.Clear();

        _source = source;
        if (source is not null) source.CollectionChanged += OnSourceChanged;
        SyncHooks();
        Rebuild();
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncHooks();
        Rebuild();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e) => Rebuild();

    private void SyncHooks()
    {
        var current = _source ?? [];
        foreach (var gone in _hooked.Where(h => !current.Contains(h)).ToList())
        {
            gone.PropertyChanged -= OnItemChanged;
            _hooked.Remove(gone);
        }
        foreach (var added in current.Where(c => !_hooked.Contains(c)))
        {
            added.PropertyChanged += OnItemChanged;
            _hooked.Add(added);
        }
    }

    private void Rebuild()
    {
        Groups.Clear();
        foreach (var group in Group(_source ?? [])) Groups.Add(group);
    }

    /// <summary>One group per (tool, outcome), the tools in the order they were first used.</summary>
    public static List<ToolCallGroup> Group(IEnumerable<ToolCallResult> calls)
    {
        var all = calls.ToList();
        var firstUse = all.Select(c => c.Name).Distinct().Select((name, index) => (name, index)).ToDictionary(x => x.name, x => x.index);
        return all
            .GroupBy(c => (c.Name, c.Succeeded))
            .OrderBy(g => firstUse[g.Key.Name])
            .Select(g => new ToolCallGroup(g.Key.Name, g.Key.Succeeded, g.Count(), Tooltip(g.Key.Succeeded, g.ToList())))
            .ToList();
    }

    private static string Tooltip(bool? succeeded, List<ToolCallResult> calls)
    {
        var errors = calls.Select(c => ShortError(c.ErrorMessage)).Where(e => e.Length > 0).Distinct().ToList();
        if (errors.Count > 0)
        {
            var lines = errors.Take(3).ToList();
            if (errors.Count > 3) lines.Add($"(and {errors.Count - 3} more kinds of error)");
            return string.Join("\n", lines);
        }
        var title = succeeded == true ? "Succeeded" : succeeded == false ? "Failed" : "Pending";
        return calls.Count > 1 ? $"{title} ({calls.Count} calls)" : title;
    }

    /// <summary>
    /// An error boiled down for a tooltip: one line, web-address query strings removed (the long signed
    /// part is never the useful part), cut to a readable length.
    /// </summary>
    public static string ShortError(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var oneLine = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var plain = Regex.Replace(oneLine, @"\?[^\s'""]+", "?...");
        return plain.Length > 140 ? plain[..137].TrimEnd() + "..." : plain;
    }
}
