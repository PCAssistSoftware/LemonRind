using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using LemonRindAvalonia.ViewModels;

namespace LemonRindAvalonia.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Restarts the whole app so the just-saved settings that cannot change
    /// at runtime (Lemonade base URL/models, data folder) take effect.
    /// </summary>
    private void OnRestartClick(object? sender, RoutedEventArgs e)
    {
        var exePath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exePath))
        {
            Process.Start(exePath);
        }

        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    // Same reasoning as MainWindow's OnAttachFileClick: Avalonia's
    // StorageProvider file/folder pickers are a View-side concern, so these
    // stay plain code-behind handlers that hand the picked path to whichever
    // card's DataContext triggered them, rather than [RelayCommand]s.
    private async void OnAddFileSourceClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: KnowledgeBaseCardViewModel card }) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add a file to this knowledge base",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Supported files") { Patterns = ["*.pdf", "*.docx", "*.xlsx", "*.txt", "*.md"] },
                FilePickerFileTypes.All,
            ],
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } localPath)
        {
            await card.AddFileSourceAsync(localPath);
        }
    }

    private async void OnAddFolderSourceClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: KnowledgeBaseCardViewModel card }) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Add a folder to this knowledge base",
            AllowMultiple = false,
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } localPath)
        {
            await card.AddFolderSourceAsync(localPath);
        }
    }
}
