using CommunityToolkit.Mvvm.ComponentModel;

namespace LemonRindAvalonia.ViewModels;

/// <summary>
/// One configured MCP server's row in the Settings screen. Ported in spirit
/// from the VB.NET/WPF LemonRind app's own McpServerViewModel - a plain
/// two-way CheckBox binding only changes the in-memory IsEnabled property,
/// nothing more, so PersistIsEnabled is set by SettingsViewModel when
/// constructing each row to trigger the real repository write from the
/// property's own change hook, matching the same pattern already used for
/// the module toggles.
/// </summary>
public partial class McpServerRowViewModel : ObservableObject
{
    public string Id { get; }
    public string Name { get; }
    public string Command { get; }
    public string ArgumentsDisplay { get; }
    public string EnvironmentVariableNamesDisplay { get; }

    [ObservableProperty]
    private bool _isEnabled;

    // Named PersistIsEnabled, not OnIsEnabledChanged - the source generator
    // for [ObservableProperty] IsEnabled already emits its own partial
    // method literally named OnIsEnabledChanged(bool value), which would
    // otherwise collide with a same-named public member on this same class.
    public Action<bool>? PersistIsEnabled { get; set; }

    public McpServerRowViewModel(string id, string name, string command, string argumentsDisplay, string environmentVariableNamesDisplay, bool isEnabled)
    {
        Id = id;
        Name = name;
        Command = command;
        ArgumentsDisplay = argumentsDisplay;
        EnvironmentVariableNamesDisplay = environmentVariableNamesDisplay;
        _isEnabled = isEnabled;
    }

    partial void OnIsEnabledChanged(bool value) => PersistIsEnabled?.Invoke(value);
}
