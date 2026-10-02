using CommunityToolkit.Mvvm.ComponentModel;

namespace LemonRindAvalonia.ViewModels;

/// <summary>
/// One scheduled job's row in the Settings screen - Name/CronExpressionText/
/// Prompt/ModelId are editable, TwoWay-bound directly in the row's own
/// TextBoxes (the "Save" button in the row commits them). Ported in spirit
/// from the VB.NET/WPF LemonRind app's own ScheduledJobViewModel.
/// </summary>
public partial class ScheduledJobRowViewModel : ObservableObject
{
    public string Id { get; }

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _cronExpressionText;
    [ObservableProperty] private string _prompt;
    [ObservableProperty] private string _modelId;
    [ObservableProperty] private string _lastRunDisplay;
    [ObservableProperty] private string _nextRunDisplay;

    [ObservableProperty]
    private bool _isEnabled;

    // Named PersistIsEnabled, not OnIsEnabledChanged - same source-generator
    // naming collision reasoning as McpServerRowViewModel's own comment.
    public Action<bool>? PersistIsEnabled { get; set; }

    public ScheduledJobRowViewModel(string id, string name, string cronExpressionText, string prompt, string modelId, string lastRunDisplay, string nextRunDisplay, bool isEnabled)
    {
        Id = id;
        _name = name;
        _cronExpressionText = cronExpressionText;
        _prompt = prompt;
        _modelId = modelId;
        _lastRunDisplay = lastRunDisplay;
        _nextRunDisplay = nextRunDisplay;
        _isEnabled = isEnabled;
    }

    partial void OnIsEnabledChanged(bool value) => PersistIsEnabled?.Invoke(value);
}
