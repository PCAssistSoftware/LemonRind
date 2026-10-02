using System.ClientModel;
using OpenAI;
using OpenAI.Images;
using LemonRindBlazor.Configuration;

namespace LemonRindBlazor.Services;

/// <summary>
/// Builds the raw OpenAI.Images.ImageClient this app uses for text-to-image
/// generation - same "point the official OpenAI.NET SDK at Lemonade's own
/// base URL" pattern as LemonadeChatClientFactory/EmbeddingClientFactory,
/// just for images.
///
/// Deliberately the RAW ImageClient, not wrapped as Microsoft.Extensions.AI's
/// IImageGenerator - that type's own ImageGenerationOptions has no fields at
/// all for steps/cfg_scale/seed (only size/count/model/response-format),
/// because it's modelled on the standard DALL-E-shaped OpenAI API. Lemonade's
/// own /v1/images/generations extends that with steps/cfg_scale/seed as
/// real, documented request fields - getting real control over those needs
/// ImageClient's low-level protocol method (GenerateImagesAsync(BinaryContent,
/// RequestOptions)), which sends whatever raw JSON body it's given, rather
/// than the strongly-typed convenience method. See ImageGenerationService
/// for where that JSON body actually gets built.
///
/// Ported directly from the VB.NET/WPF LemonRind app's Services\ImageClientFactory.vb
/// (its final, reworked form - the earlier IImageGenerator-based version it
/// replaced isn't reproduced here, since that whole abstraction was removed
/// there once steps/cfg_scale/seed needed real control).
/// </summary>
public class ImageClientFactory(AppSettings settings)
{
    private readonly LemonadeSettings _settings = settings.Lemonade;
    private readonly ImageGenSettings _imageGenSettings = settings.ImageGen;

    public ImageClient CreateImageClient()
    {
        var options = new OpenAIClientOptions { Endpoint = new Uri(_settings.BaseUrl) };
        var credential = new ApiKeyCredential(string.IsNullOrWhiteSpace(_settings.ApiKey) ? "lemonade" : _settings.ApiKey);
        var openAiClient = new OpenAIClient(credential, options);
        return openAiClient.GetImageClient(_imageGenSettings.ModelId);
    }
}
