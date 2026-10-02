using CommunityToolkit.Mvvm.ComponentModel;

namespace LemonRindAvalonia.ViewModels;

/// <summary>
/// One stored memory's row in the Memories review screen. A separate
/// observable wrapper (not Memories.MemoryItem itself) because Content
/// needs to be two-way-bindable and edited in place before the user clicks
/// Save - MemoryItem is a plain data-access DTO, not meant to carry UI edit
/// state.
///
/// Ported from the VB.NET/WPF LemonRind app's ViewModels\SettingsViewModel.vb's
/// nested MemoryItemViewModel class.
/// </summary>
public partial class MemoryItemViewModel : ObservableObject
{
    public required string Id { get; init; }
    public required bool IsPinned { get; init; }
    public required DateTime CreatedAt { get; init; }

    [ObservableProperty]
    private string _content = "";
}
