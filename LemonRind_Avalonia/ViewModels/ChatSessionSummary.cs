using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LemonRindAvalonia.ViewModels;

/// <summary>
/// One sidebar row. ObservableObject (not a plain record) so an inline
/// rename updates the sidebar's displayed title live, without a full list
/// rebuild - same reasoning as the VB.NET/WPF LemonRind app's own
/// ChatSessionSummary.
/// </summary>
public partial class ChatSessionSummary : ObservableObject
{
    public string Id { get; set; } = "";

    [ObservableProperty]
    private string _title = "";

    public DateTime UpdatedAt { get; set; }

    /// <summary>Null if this session isn't in a folder.</summary>
    public string? FolderId { get; set; }
    public string? FolderName { get; set; }

    /// <summary>Grouping key - "Unfiled" (not a real folder name, just a display fallback) for a session with none, so every session has something to group by either way. Not currently bound to directly (RebuildSidebarItems interleaves its own FolderHeaderSummary rows instead), kept for parity/possible future direct use.</summary>
    public string FolderDisplayName => string.IsNullOrEmpty(FolderName) ? "Unfiled" : FolderName;

    /// <summary>Compact "how long ago" shown on the sidebar row: now, 5m, 3h, Yesterday, 4d, then a short date. A snapshot taken when the list is built.</summary>
    public string RelativeTime
    {
        get
        {
            var age = DateTime.UtcNow - UpdatedAt.ToUniversalTime();
            if (age.TotalMinutes < 1) return "now";
            if (age.TotalHours < 1) return $"{(int)age.TotalMinutes}m";
            if (age.TotalDays < 1) return $"{(int)age.TotalHours}h";
            var local = UpdatedAt.ToLocalTime();
            if (local.Date == DateTime.Today.AddDays(-1)) return "Yesterday";
            if (age.TotalDays < 7) return $"{(int)age.TotalDays}d";
            return local.ToString("d MMM");
        }
    }

    public ObservableCollection<string> Tags { get; } = [];

    // ObservableCollection<T>'s own Count doesn't feed into a separately-
    // bound IsVisible (same gap already hit for ChatMessageViewModel.
    // ToolCalls) - this listens for CollectionChanged and republishes it as
    // a real bool property the tag-chip row's IsVisible can bind to.
    public bool HasTags => Tags.Count > 0;

    public ChatSessionSummary()
    {
        Tags.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTags));
    }

    /// <summary>
    /// UI-only, never persisted - toggles the row between a plain clickable
    /// label (click = select the chat) and an editable TextBox (click = an
    /// edit toggle button, not the label itself). An always-editable title
    /// TextBox leaves only a thin sliver of the row that actually selects it,
    /// which is why a pencil-icon toggle is used instead of a plain always-on
    /// TextBox.
    /// </summary>
    [ObservableProperty]
    private bool _isEditingTitle;
}
