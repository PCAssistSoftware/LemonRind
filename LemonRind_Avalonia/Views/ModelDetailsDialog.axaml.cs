using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;

namespace LemonRindAvalonia.Views;

/// <summary>
/// Read-only "what does this model actually support/how was it launched"
/// dialog, opened from a button next to the main model dropdown. Rich
/// runtime detail (device, the exact llama-server launch arguments -
/// temperature/top-k/top-p/repeat-penalty/etc) only exists for a model
/// that's actually loaded and running right now (GET /v1/health's
/// all_models_loaded array) - a model that's merely downloaded but not
/// loaded only has the static /v1/models catalog entry (labels, context
/// window, size, recipe), so this dialog shows a plain note instead of
/// guessing at runtime values it doesn't have.
///
/// Ported from the VB.NET/WPF LemonRind app's ModelDetailsDialog.xaml(.vb).
/// </summary>
public partial class ModelDetailsDialog : Window
{
    public ModelDetailsDialog()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    public static async Task ShowAsync(
        string modelId,
        IEnumerable<string>? labels,
        int maxContextWindow,
        double sizeGb,
        string? recipe,
        bool isCurrentlyLoaded,
        string? device,
        string? llamacppArgs)
    {
        var dialog = new ModelDetailsDialog();
        dialog.ModelIdText.Text = modelId;

        var labelsList = labels?.ToList() ?? [];
        dialog.LabelsText.Text = labelsList.Count > 0 ? string.Join(", ", labelsList) : "(none reported)";
        dialog.ContextWindowText.Text = maxContextWindow > 0 ? $"{maxContextWindow:N0} tokens" : "(unknown)";
        dialog.SizeText.Text = sizeGb > 0 ? $"{sizeGb:N1} GB" : "(unknown)";
        dialog.BackendText.Text = string.IsNullOrEmpty(recipe) ? "(unknown)" : recipe;

        if (isCurrentlyLoaded && !string.IsNullOrEmpty(llamacppArgs))
        {
            dialog.RuntimeDetailPanel.IsVisible = true;
            dialog.DeviceText.Text = string.IsNullOrEmpty(device) ? "(unknown)" : device;
            dialog.LlamacppArgsText.Text = llamacppArgs;
        }
        else
        {
            dialog.NotLoadedNoteText.IsVisible = true;
            dialog.NotLoadedNoteText.Text = isCurrentlyLoaded
                ? "This model is loaded, but Lemonade didn't report its launch arguments (not every backend exposes them - e.g. image/embedding models)."
                : "Load this model (select it above) to see its exact runtime arguments.";
        }

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
