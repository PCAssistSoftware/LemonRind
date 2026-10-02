using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;

namespace LemonRindAvalonia.Views;

/// <summary>
/// Read-only view of the real, current system message (_history[0] in
/// MainViewModel). Shows the exact live text, not a reconstruction - see
/// MainViewModel's OpenSystemPromptCommand.
///
/// Ported from the VB.NET/WPF LemonRind app's SystemPromptDialog.xaml(.vb).
/// Generalized with optional title/description parameters so it is also
/// used for the turn-context viewer (OpenTurnContextAsync), which needs the
/// same "show some read-only text with a caption" shape - the same pattern
/// as FileWriteApprovalDialog's reuse by the Coder module.
/// </summary>
public partial class SystemPromptDialog : Window
{
    public SystemPromptDialog()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    public static async Task ShowAsync(string promptText, string title = "System prompt",
        string description = "Exactly what's currently sitting at the front of the model's context for this chat - the same text that gets rebuilt each turn.")
    {
        var dialog = new SystemPromptDialog { Title = title };
        dialog.DescriptionTextBlock.Text = description;
        dialog.PromptTextBox.Text = promptText;

        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            await dialog.ShowDialog(owner);
        }
        else
        {
            dialog.Show();
        }
    }
}
