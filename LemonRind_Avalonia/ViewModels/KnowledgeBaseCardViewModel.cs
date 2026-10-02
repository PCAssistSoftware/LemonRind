using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LemonRindAvalonia.Knowledge;

namespace LemonRindAvalonia.ViewModels;

/// <summary>
/// One Knowledge Base's card in the Settings screen - its sources list plus
/// the mini add-file/add-folder/add-website/add-text forms. Not ported
/// line-by-line from the VB.NET/WPF LemonRind app (its own per-KB card UI
/// is XAML-specific), but the same shape: source management lives on the
/// card, not in a separate dialog.
/// </summary>
public partial class KnowledgeBaseCardViewModel : ObservableObject
{
    private readonly KnowledgeRepository _repository;
    private readonly KnowledgeIngestionService _ingestionService;
    private readonly Action _onDeleted;

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }

    public ObservableCollection<KnowledgeSource> Sources { get; } = [];

    [ObservableProperty] private string _newWebsiteUrl = "";
    [ObservableProperty] private string _newTextName = "";
    [ObservableProperty] private string _newTextContent = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isBusy;

    public KnowledgeBaseCardViewModel(KnowledgeBaseSummary summary, KnowledgeRepository repository, KnowledgeIngestionService ingestionService, Action onDeleted)
    {
        Id = summary.Id;
        Name = summary.Name;
        Description = summary.Description;
        _repository = repository;
        _ingestionService = ingestionService;
        _onDeleted = onDeleted;
        RefreshSources();
    }

    public void RefreshSources()
    {
        Sources.Clear();
        foreach (var source in _repository.ListSources(Id)) Sources.Add(source);
    }

    /// <summary>Called from SettingsWindow's code-behind after its own Avalonia file picker returns a path - file pickers are a View-side concern, same reasoning as MainViewModel.AttachFile.</summary>
    public async Task AddFileSourceAsync(string filePath)
    {
        IsBusy = true;
        StatusText = $"Ingesting {Path.GetFileName(filePath)}...";
        try
        {
            await _ingestionService.AddFileSourceAsync(Id, filePath, CancellationToken.None);
            RefreshSources();
            StatusText = "";
        }
        finally { IsBusy = false; }
    }

    /// <summary>Called from SettingsWindow's code-behind after its own folder picker returns a path.</summary>
    public async Task AddFolderSourceAsync(string folderPath)
    {
        IsBusy = true;
        StatusText = $"Ingesting {Path.GetFileName(folderPath)}...";
        try
        {
            await _ingestionService.AddFolderSourceAsync(Id, folderPath, CancellationToken.None);
            RefreshSources();
            StatusText = "";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task AddWebsiteSourceAsync()
    {
        if (string.IsNullOrWhiteSpace(NewWebsiteUrl)) return;
        var url = NewWebsiteUrl.Trim();
        NewWebsiteUrl = "";

        IsBusy = true;
        StatusText = $"Fetching {url}...";
        try
        {
            await _ingestionService.AddWebsiteSourceAsync(Id, url, CancellationToken.None);
            RefreshSources();
            StatusText = "";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task AddTextSourceAsync()
    {
        if (string.IsNullOrWhiteSpace(NewTextContent)) return;
        var name = string.IsNullOrWhiteSpace(NewTextName) ? "Pasted text" : NewTextName.Trim();
        var content = NewTextContent;
        NewTextName = "";
        NewTextContent = "";

        IsBusy = true;
        StatusText = $"Ingesting {name}...";
        try
        {
            await _ingestionService.AddTextSourceAsync(Id, content, name, CancellationToken.None);
            RefreshSources();
            StatusText = "";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void DeleteSource(KnowledgeSource? source)
    {
        if (source is null) return;
        _repository.DeleteSource(source.Id);
        RefreshSources();
    }

    [RelayCommand]
    private void Delete()
    {
        _repository.DeleteKnowledgeBase(Id);
        _onDeleted();
    }
}
