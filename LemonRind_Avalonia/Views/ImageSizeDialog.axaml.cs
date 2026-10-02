using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;

namespace LemonRindAvalonia.Views;

/// <summary>
/// Confirms/overrides the width and height before generate_image actually
/// calls Lemonade - size is asked about every time, not silently defaulted,
/// unlike steps/cfg_scale/seed which are quiet Settings-level defaults.
///
/// Ported from the VB.NET/WPF LemonRind app's ImageSizeDialog.xaml(.vb) -
/// Avalonia's own async ShowDialog&lt;TResult&gt; replaces that app's
/// Dispatcher.Invoke-from-a-tool-method blocking pattern (see
/// FileSystemModule's own equivalent comment for the same reasoning).
/// </summary>
public partial class ImageSizeDialog : Window
{
    public record struct Result(bool Confirmed, int Width, int Height);

    public ImageSizeDialog()
    {
        InitializeComponent();
    }

    public ImageSizeDialog(string prompt, int defaultWidth, int defaultHeight) : this()
    {
        // Full prompt, not truncated (a 140-char cutoff hid most of any real prompt).
        // TextWrapping="Wrap" plus the window's own SizeToContent="Height"
        // means a long prompt just makes the dialog taller, not clipped.
        PromptPreviewText.Text = prompt;
        WidthBox.Text = defaultWidth.ToString();
        HeightBox.Text = defaultHeight.ToString();
    }

    private void OnGenerateClick(object? sender, RoutedEventArgs e)
    {
        if (!int.TryParse(WidthBox.Text, out var width) || width <= 0) width = 512;
        if (!int.TryParse(HeightBox.Text, out var height) || height <= 0) height = 512;
        Close(new Result(true, width, height));
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(new Result(false, 0, 0));

    public static async Task<Result> ShowAsync(string prompt, int defaultWidth, int defaultHeight)
    {
        var dialog = new ImageSizeDialog(prompt, defaultWidth, defaultHeight);

        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            return await dialog.ShowDialog<Result>(owner);
        }

        dialog.Show();
        return new Result(false, 0, 0);
    }
}
