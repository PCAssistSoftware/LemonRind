using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using OpenAI.Images;
using LemonRindBlazor.Configuration;

namespace LemonRindBlazor.Services;

/// <summary>
/// The actual "call Lemonade, save the file, return a file:// URI" work,
/// shared by ImageGenModule's generate_image tool and the direct
/// image-model path in MainViewModel so the request-building/response-parsing
/// logic isn't duplicated.
///
/// Sends a hand-built JSON request via ImageClient's low-level protocol
/// method rather than the SDK's strongly-typed convenience method, because
/// Lemonade's /v1/images/generations extends the standard OpenAI request
/// shape with steps/cfg_scale/seed that the typed OpenAI.Images.
/// ImageGenerationOptions class has no fields for at all.
///
/// Ported directly from the VB.NET/WPF LemonRind app's Services\ImageGenerationService.vb.
/// </summary>
public class ImageGenerationService
{
    private readonly Func<ImageClient> _createImageClient;
    private readonly string _imagesFolder;

    public ImageGenerationService(ImageClientFactory imageClientFactory, AppSettings settings)
    {
        // Func<ImageClient>, not the client directly - an empty ImageModel
        // setting must only fail when a generation is actually attempted,
        // not eagerly at DI-graph-construction time, since every module
        // gets constructed eagerly regardless of whether it's enabled.
        _createImageClient = imageClientFactory.CreateImageClient;
        _imagesFolder = Path.Combine(settings.AppData.ResolvedDataFolder(), "Workspace", "GeneratedImages");
    }

    /// <summary>Generates one image and saves it to disk, returning its local file:// URI. Throws on any failure - callers decide how to surface that.</summary>
    public async Task<string> GenerateAsync(string prompt, string modelId, int width, int height, int steps, double cfgScale, int seed, CancellationToken cancellationToken)
    {
        var imageClient = _createImageClient();

        var requestBody = new Dictionary<string, object>
        {
            ["model"] = modelId,
            ["prompt"] = prompt,
            ["size"] = $"{width}x{height}",
            ["steps"] = steps,
            ["cfg_scale"] = cfgScale,
            ["seed"] = seed,
            ["response_format"] = "b64_json",
        };
        var requestJson = JsonSerializer.Serialize(requestBody);

        var result = await imageClient.GenerateImagesAsync(
            BinaryContent.CreateJson(requestJson),
            new RequestOptions { CancellationToken = cancellationToken });

        var responseText = result.GetRawResponse().Content.ToString();
        using var doc = JsonDocument.Parse(responseText);
        var dataArray = doc.RootElement.GetProperty("data");
        if (dataArray.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Image generation returned no image data.");
        }

        var base64 = dataArray[0].GetProperty("b64_json").GetString()!;
        var imageBytes = Convert.FromBase64String(base64);

        Directory.CreateDirectory(_imagesFolder);
        var fileName = $"img_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}.png";
        var fullPath = Path.Combine(_imagesFolder, fileName);
        await File.WriteAllBytesAsync(fullPath, imageBytes, cancellationToken);

        // file:// URI, not a bare Windows path - matches the well-formed
        // URI the WPF app's Markdig.Wpf image renderer needs;
        // this app parses the same URI back out on the display side (see
        // MainViewModel's own image-detection regex) rather than rendering
        // full Markdown.
        return new Uri(fullPath).AbsoluteUri;
    }
}
