using Microsoft.Extensions.AI;
using LemonRindAvalonia.Configuration;
using LemonRindAvalonia.Services;
using LemonRindAvalonia.Views;

namespace LemonRindAvalonia.Modules.ImageGen;

/// <summary>
/// Text-to-image generation, exposed as a single generate_image chat tool
/// so a real chat model can ask for an image mid-conversation without
/// switching models. The real WPF app's other, more direct way to generate
/// images - picking an image-labeled model straight in the main chat
/// dropdown, skipping the LLM entirely - is also supported
/// (MainViewModel.SendImageGenerationTurnAsync); both paths share
/// ImageGenerationService, so neither duplicates the request-building/
/// response-parsing logic.
///
/// Ported directly from the VB.NET/WPF LemonRind app's Modules\ImageGen\ImageGenModule.vb.
/// </summary>
public class ImageGenModule(AppSettings settings, ImageGenerationService imageGenerationService) : IAssistantModule
{
    private readonly ImageGenSettings _imageGenSettings = settings.ImageGen;
    private readonly ModuleSettings _moduleSettings = settings.Modules;

    public string Name => "Image generation";
    public string ConfigKey => "ImageGen";
    public string Description => "Generates images from a text prompt using Lemonade's configured image model, shown inline in the chat. Asks you to confirm the image size before each generation.";

    public bool IsEnabled => _moduleSettings.Enabled.GetValueOrDefault(ConfigKey, false);

    public Task OnStartupAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task OnShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public IEnumerable<AITool> GetTools()
    {
        return
        [
            AIFunctionFactory.Create(
                method: (string prompt) => GenerateImageAsync(prompt),
                name: "generate_image",
                description: "Generates an image from a text description using a local image-generation model. " +
                    "The user will be asked to confirm the image size before it actually generates - if they " +
                    "cancel, no image is created. Returns ready-made Markdown image syntax on success - include " +
                    "that exact Markdown, unchanged, somewhere in your reply so the user actually sees the " +
                    "image; do not just describe the image in words."),
        ];
    }

    private async Task<string> GenerateImageAsync(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return "Provide a text description of the image to generate.";

        var sizeChoice = await ImageSizeDialog.ShowAsync(prompt, _imageGenSettings.DefaultWidth, _imageGenSettings.DefaultHeight);
        if (!sizeChoice.Confirmed) return "The user cancelled image generation.";

        try
        {
            var imageUri = await imageGenerationService.GenerateAsync(
                prompt, _imageGenSettings.ModelId, sizeChoice.Width, sizeChoice.Height,
                _imageGenSettings.Steps, _imageGenSettings.CfgScale, _imageGenSettings.Seed,
                CancellationToken.None);

            var altText = prompt.Length > 80 ? prompt[..80] + "..." : prompt;
            return $"Image generated. Include this exact Markdown in your reply: ![{altText}]({imageUri})";
        }
        catch (Exception ex)
        {
            return $"Couldn't generate that image: {ex.Message}";
        }
    }
}
