namespace LemonRindAvalonia.ViewModels;

/// <summary>Read-only status row for the right panel's Modules tab - a snapshot, not live-bound (IAssistantModule doesn't implement INotifyPropertyChanged), refreshed explicitly on startup and whenever Settings closes.</summary>
public class ModuleStatusItem(string name, string description, bool isEnabled)
{
    public string Name { get; } = name;
    public string Description { get; } = description;
    public bool IsEnabled { get; } = isEnabled;
    public string StatusText => IsEnabled ? "Enabled" : "Disabled";
}
