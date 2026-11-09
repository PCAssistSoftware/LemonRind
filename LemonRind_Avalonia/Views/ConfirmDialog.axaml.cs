using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;

namespace LemonRindAvalonia.Views;

/// <summary>
/// A plain question with Cancel and a confirm button: ShowAsync returns true only when the confirm button was
/// pressed (closing the window any other way counts as Cancel). Used where the app asks before doing something a
/// person might not want, such as loading another model while a scheduled job is running.
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    private ConfirmDialog(string message, string title, string confirmText) : this()
    {
        Title = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e) => Close(true);
    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    public static async Task<bool> ShowAsync(string message, string title, string confirmText = "Yes")
    {
        var dialog = new ConfirmDialog(message, title, confirmText);

        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            return await dialog.ShowDialog<bool>(owner);
        }

        dialog.Show();
        return false;
    }
}
