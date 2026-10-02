using CommunityToolkit.Mvvm.ComponentModel;

namespace LemonRindAvalonia.ViewModels;

/// <summary>One row in the Settings screen's Modules list - a module's display Name/ConfigKey plus a live-editable enabled flag.</summary>
public partial class ModuleToggleItem(string name, string description, string configKey, bool isEnabled) : ObservableObject
{
    public string Name { get; } = name;
    public string Description { get; } = description;
    public string ConfigKey { get; } = configKey;

    [ObservableProperty]
    private bool _isEnabled = isEnabled;
}
