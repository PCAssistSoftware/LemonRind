using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using LemonRindAvalonia.Data;

namespace LemonRindAvalonia.Views;

/// <summary>
/// Assigns a session to a folder, or creates a new one on the fly (the
/// whole point of not having a separate "manage folders" screen - creating
/// one happens naturally the first time you move a session into it).
///
/// Ported from the VB.NET/WPF LemonRind app's MoveToFolderDialog.xaml(.vb).
/// Unlike that app's own version (which creates the new folder itself via
/// a passed-in ChatSessionRepository before returning), this one just
/// returns the typed name and lets MainViewModel create it - a dialog
/// reaching into the data layer directly felt like the wrong owner for
/// that decision once porting it fresh, not a functional difference.
/// </summary>
public partial class MoveToFolderDialog : Window
{
    public record struct Result(bool Confirmed, string? FolderId, string? NewFolderName);

    private static readonly FolderSummary UnfiledSentinel = new() { Id = null, Name = "(Unfiled)" };

    public MoveToFolderDialog()
    {
        InitializeComponent();
    }

    public MoveToFolderDialog(List<FolderSummary> folders, string? currentFolderId) : this()
    {
        var comboItems = new List<FolderSummary> { UnfiledSentinel };
        comboItems.AddRange(folders);
        FolderComboBox.ItemsSource = comboItems;
        FolderComboBox.SelectedItem = comboItems.FirstOrDefault(f => f.Id == currentFolderId) ?? comboItems[0];
    }

    private void OnMoveClick(object? sender, RoutedEventArgs e)
    {
        var newFolderName = NewFolderNameBox.Text?.Trim() ?? "";
        if (!string.IsNullOrEmpty(newFolderName))
        {
            Close(new Result(true, null, newFolderName));
            return;
        }

        var selectedFolder = FolderComboBox.SelectedItem as FolderSummary;
        Close(new Result(true, selectedFolder?.Id, null));
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(new Result(false, null, null));

    public static async Task<Result> ShowAsync(List<FolderSummary> folders, string? currentFolderId)
    {
        var dialog = new MoveToFolderDialog(folders, currentFolderId);

        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            return await dialog.ShowDialog<Result>(owner);
        }

        dialog.Show();
        return new Result(false, null, null);
    }
}
