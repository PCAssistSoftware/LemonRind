using CommunityToolkit.Mvvm.Input;

namespace LemonRindAvalonia.ViewModels;

/// <summary>
/// One collapsible folder header row in the sidebar's flat SidebarItems
/// list (see MainViewModel.RebuildSidebarItems). A plain data object with
/// its own ToggleExpandedCommand, not a shared command on MainViewModel
/// with a CommandParameter - the row's DataTemplate just binds directly to
/// it, ordinary MVVM commanding.
///
/// Ported directly from the VB.NET/WPF LemonRind app's ViewModels\FolderHeaderSummary.vb.
/// </summary>
public class FolderHeaderSummary
{
    public string? FolderId { get; set; }
    public string Name { get; set; } = "";
    public bool IsCollapsed { get; set; }

    /// <summary>False only for the synthetic "Unfiled" bucket - it has no real Folders row to persist a collapsed state against, so it always stays expanded and isn't clickable.</summary>
    public bool IsCollapsible { get; set; } = true;

    public IRelayCommand? ToggleExpandedCommand { get; set; }
}
