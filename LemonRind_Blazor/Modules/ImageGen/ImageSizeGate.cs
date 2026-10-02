namespace LemonRindBlazor.Modules.ImageGen;

/// <summary>
/// Blazor-native replacement for the desktop apps' modal ImageSizeDialog -
/// same "ask and wait" TaskCompletionSource pattern as
/// Modules\FileSystem\FileWriteApprovalGate.cs (there's no blocking
/// Window.ShowDialog() equivalent in a web app). Size is confirmed every
/// time a generate_image call happens, not silently defaulted, unlike
/// steps/cfg_scale/seed which stay quiet Settings-level defaults.
/// </summary>
public record struct ImageSizeResult(bool Confirmed, int Width, int Height);

public class ImageSizeRequest(string prompt, int defaultWidth, int defaultHeight)
{
    public string Prompt { get; } = prompt;
    public int DefaultWidth { get; } = defaultWidth;
    public int DefaultHeight { get; } = defaultHeight;
    public TaskCompletionSource<ImageSizeResult> Completion { get; } = new();
}

public class ImageSizeGate
{
    public event Action<ImageSizeRequest?>? OnPendingRequestChanged;

    public Task<ImageSizeResult> RequestSizeAsync(string prompt, int defaultWidth, int defaultHeight)
    {
        var request = new ImageSizeRequest(prompt, defaultWidth, defaultHeight);
        OnPendingRequestChanged?.Invoke(request);
        _ = request.Completion.Task.ContinueWith(_ => OnPendingRequestChanged?.Invoke(null), TaskScheduler.Default);
        return request.Completion.Task;
    }
}
