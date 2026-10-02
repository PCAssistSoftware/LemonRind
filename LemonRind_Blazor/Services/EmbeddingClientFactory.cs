using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using LemonRindBlazor.Configuration;

namespace LemonRindBlazor.Services;

/// <summary>
/// Builds the embedding generator this app uses for long-term memory - same
/// "point an OpenAI-compatible client at Lemonade's embeddings endpoint"
/// pattern as LemonadeChatClientFactory, just for embeddings instead of
/// chat. Ported directly from the VB.NET/WPF LemonRind app's
/// Services\EmbeddingClientFactory.vb.
/// </summary>
public class EmbeddingClientFactory(AppSettings settings)
{
    private readonly LemonadeSettings _settings = settings.Lemonade;

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator()
    {
        var options = new OpenAIClientOptions { Endpoint = new Uri(_settings.BaseUrl) };
        var credential = new ApiKeyCredential(string.IsNullOrWhiteSpace(_settings.ApiKey) ? "lemonade" : _settings.ApiKey);
        var openAiClient = new OpenAIClient(credential, options);

        // Falls back to a placeholder when no embedding model is configured
        // yet - the OpenAI SDK throws on an empty model name at client-
        // construction time.
        var embeddingModel = string.IsNullOrWhiteSpace(_settings.EmbeddingModel) ? "unset" : _settings.EmbeddingModel;
        return openAiClient.GetEmbeddingClient(embeddingModel).AsIEmbeddingGenerator();
    }
}
