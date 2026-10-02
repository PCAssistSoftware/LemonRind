namespace LemonRindBlazor.Data;

/// <summary>
/// One sidebar row's data. A plain POCO here, not an ObservableObject the
/// way the Avalonia/WPF apps' own ChatSessionSummary is - this app's UI
/// layer re-renders via Blazor's own StateHasChanged, not property-changed
/// bindings, so there's nothing for an INotifyPropertyChanged base class to
/// do here. UI-only concerns that lived on the Avalonia version (IsEditingTitle,
/// a CollectionChanged-driven HasTags) are dropped entirely - those belong
/// in whichever Razor component actually needs them, not in a query-result
/// shape returned by the data layer.
/// </summary>
public class ChatSessionSummary
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime UpdatedAt { get; set; }

    /// <summary>Null if this session isn't in a folder.</summary>
    public string? FolderId { get; set; }
    public string? FolderName { get; set; }

    /// <summary>Grouping key - "Unfiled" (not a real folder name, just a display fallback) for a session with none, so every session has something to group by either way.</summary>
    public string FolderDisplayName => string.IsNullOrEmpty(FolderName) ? "Unfiled" : FolderName;

    public List<string> Tags { get; set; } = [];
    public bool HasTags => Tags.Count > 0;
}
