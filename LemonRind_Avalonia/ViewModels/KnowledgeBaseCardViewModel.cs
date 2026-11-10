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

    // The embedding model that built this knowledge base's vectors, and - when it differs from the one in use now -
    // why it cannot be searched (with a Re-embed button), see KnowledgeCompatibility.
    [ObservableProperty] private string _modelInfoText = "";
    [ObservableProperty] private string _warningText = "";
    [ObservableProperty] private bool _hasWarning;
    [ObservableProperty] private bool _canReembed;

    public KnowledgeBaseCardViewModel(KnowledgeBaseSummary summary, KnowledgeRepository repository, KnowledgeIngestionService ingestionService, Action onDeleted)
    {
        Id = summary.Id;
        Name = summary.Name;
        Description = summary.Description;
        _repository = repository;
        _ingestionService = ingestionService;
        _onDeleted = onDeleted;
        RefreshSources();
        RefreshCompatibility();
    }

    /// <summary>Reads the knowledge base again (the model is recorded the first time something is embedded) and works out whether it can be used with the embedding model in use now.</summary>
    public void RefreshCompatibility()
    {
        var kb = _repository.GetKnowledgeBase(Id);
        ModelInfoText = kb is not null && !string.IsNullOrEmpty(kb.EmbeddingModel) ? $"Embedding model: {kb.EmbeddingModel}" : "";
        WarningText = kb is null ? "" : KnowledgeCompatibility.Check(kb, _ingestionService.EmbeddingModelName);
        HasWarning = WarningText.Length > 0;
        CanReembed = HasWarning && !KnowledgeCompatibility.IsReembedding(Id);
    }

    /// <summary>Embeds every chunk again with the current embedding model - its stored text is used, so the original documents are not needed. Takes a while for a big knowledge base.</summary>
    [RelayCommand]
    private async Task ReembedAsync()
    {
        IsBusy = true;
        StatusText = "Re-embedding... this takes a while for a big knowledge base.";
        try
        {
            var count = await _ingestionService.ReembedAsync(Id, CancellationToken.None);
            StatusText = $"Re-embedded {count:N0} chunks with {_ingestionService.EmbeddingModelName}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't re-embed: {ex.Message}";
        }
        finally { IsBusy = false; }
        RefreshCompatibility();
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
            RefreshCompatibility();
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
            RefreshCompatibility();
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
            RefreshCompatibility();
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
            RefreshCompatibility();
            StatusText = "";
        }
        finally { IsBusy = false; }
    }

    /// <summary>Shows what the vision model wrote about a picture - the text that is searched.</summary>
    [RelayCommand]
    private async Task ViewPictureTextAsync(KnowledgeSource? source)
    {
        if (source is null || !source.HasPictureText) return;
        await Views.SystemPromptDialog.ShowAsync(source.PictureText, title: source.DisplayName,
            description: "What the model wrote about this picture. This text is what gets searched.");
    }

    /// <summary>Reads a picture again with the model that is loaded now (works after the original file has gone).</summary>
    [RelayCommand]
    private async Task ReadPictureAgainAsync(KnowledgeSource? source)
    {
        if (source is null) return;
        IsBusy = true;
        StatusText = $"Reading {source.DisplayName} again...";
        try
        {
            await _ingestionService.RereadPictureAsync(Id, source.Id, CancellationToken.None);
            RefreshSources();
            RefreshCompatibility();
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
