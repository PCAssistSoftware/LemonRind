using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using LemonRindAvalonia.Data;

namespace LemonRindAvalonia.Views;

/// <summary>
/// Add/remove tags on one session. Unlike ImageSizeDialog/MoveToFolderDialog
/// (stage changes, apply on confirm), this one writes to the database
/// immediately as you add/remove each tag - "Done" is just a close button,
/// not a commit step.
///
/// Ported from the VB.NET/WPF LemonRind app's TagManagerDialog.xaml(.vb).
/// </summary>
public partial class TagManagerDialog : Window
{
    private string _sessionId = "";
    private ChatSessionRepository? _sessionRepository;
    private readonly ObservableCollection<string> _tags = [];

    public TagManagerDialog()
    {
        InitializeComponent();
    }

    public TagManagerDialog(string sessionId, IEnumerable<string> initialTags, ChatSessionRepository sessionRepository) : this()
    {
        _sessionId = sessionId;
        _sessionRepository = sessionRepository;
        foreach (var tagName in initialTags) _tags.Add(tagName);
        TagsItemsControl.ItemsSource = _tags;
    }

    private void OnAddTagClick(object? sender, RoutedEventArgs e) => AddTag();

    private void OnNewTagBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddTag();
    }

    private void AddTag()
    {
        var tagName = NewTagBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(tagName)) return;
        if (_tags.Any(t => string.Equals(t, tagName, StringComparison.OrdinalIgnoreCase)))
        {
            NewTagBox.Text = "";
            return;
        }

        _sessionRepository!.AddTagToSession(_sessionId, tagName);
        _tags.Add(tagName);
        NewTagBox.Text = "";
        NewTagBox.Focus();
    }

    private void OnRemoveTagClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tagName }) return;
        _sessionRepository!.RemoveTagFromSession(_sessionId, tagName);
        _tags.Remove(tagName);
    }

    private void OnDoneClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Opens the dialog and waits until closed - changes are already saved by then, so there's nothing for the caller to apply, just a RefreshSessions() to pick up the new tag chips (see MainViewModel's own caller).</summary>
    public static async Task ShowAsync(string sessionId, IEnumerable<string> currentTags, ChatSessionRepository sessionRepository)
    {
        var dialog = new TagManagerDialog(sessionId, currentTags, sessionRepository);

        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            await dialog.ShowDialog(owner);
            return;
        }

        dialog.Show();
    }
}
