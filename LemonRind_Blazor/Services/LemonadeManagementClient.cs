using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using LemonRindBlazor.Configuration;

namespace LemonRindBlazor.Services;

/// <summary>
/// One model entry from GET /v1/models. Property names use JsonPropertyName
/// rather than relying on System.Text.Json's default naming policy, so the
/// mapping to Lemonade's actual JSON field names (snake_case) is explicit.
/// </summary>
public class LemonadeModelInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("labels")]
    public List<string> Labels { get; set; } = [];

    [JsonPropertyName("downloaded")]
    public bool Downloaded { get; set; }

    [JsonPropertyName("size")]
    public double SizeGb { get; set; }

    [JsonPropertyName("max_context_window")]
    public int MaxContextWindow { get; set; }

    /// <summary>Backend family, e.g. "llamacpp"/"sd-cpp"/"thenoise" - present for every downloaded model whether currently loaded or not.</summary>
    [JsonPropertyName("recipe")]
    public string? Recipe { get; set; }
}

internal class LemonadeModelsResponse
{
    [JsonPropertyName("data")]
    public List<LemonadeModelInfo> Data { get; set; } = [];
}

/// <summary>
/// Recipe-specific runtime detail for one currently-loaded model - only
/// meaningful fields for an "llamacpp" LLM are mapped (ctx_size,
/// llamacpp_args); image-recipe fields aren't needed here.
/// </summary>
public class LemonadeLoadedModelRecipeOptions
{
    [JsonPropertyName("ctx_size")]
    public int? CtxSize { get; set; }

    /// <summary>The exact llama-server CLI flags Lemonade launched this model with - temperature/top-k/top-p/repeat-penalty/etc.</summary>
    [JsonPropertyName("llamacpp_args")]
    public string? LlamacppArgs { get; set; }
}

/// <summary>One entry in GET /v1/health's all_models_loaded array - the currently-running instance's real detail.</summary>
public class LemonadeLoadedModelInfo
{
    [JsonPropertyName("model_name")]
    public string ModelName { get; set; } = "";

    [JsonPropertyName("device")]
    public string? Device { get; set; }

    [JsonPropertyName("recipe")]
    public string? Recipe { get; set; }

    [JsonPropertyName("recipe_options")]
    public LemonadeLoadedModelRecipeOptions? RecipeOptions { get; set; }

    [JsonPropertyName("max_context_window")]
    public int? MaxContextWindow { get; set; }
}

/// <summary>The parts of GET /v1/health this app currently uses.</summary>
public class LemonadeHealthInfo
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("model_loaded")]
    public string? ModelLoaded { get; set; }

    [JsonPropertyName("websocket_port")]
    public int WebsocketPort { get; set; }

    /// <summary>Every currently-loaded model's real runtime detail - not just the active chat model.</summary>
    [JsonPropertyName("all_models_loaded")]
    public List<LemonadeLoadedModelInfo> AllModelsLoaded { get; set; } = [];
}

internal class LemonadeLoadRequest
{
    [JsonPropertyName("model_name")]
    public string ModelName { get; set; } = "";
}

internal class LemonadeLoadResponse
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

internal class LemonadeTokenizeRequest
{
    [JsonPropertyName("content")]
    public string Content { get; set; } = "";
}

internal class LemonadeTokenizeResponse
{
    [JsonPropertyName("tokens")]
    public List<int>? Tokens { get; set; }
}

/// <summary>
/// GET /v1/stats - performance figures for the last request, measured
/// server-side by Lemonade itself.
/// </summary>
public class LemonadeStatsInfo
{
    [JsonPropertyName("time_to_first_token")]
    public double TimeToFirstToken { get; set; }

    [JsonPropertyName("tokens_per_second")]
    public double TokensPerSecond { get; set; }

    /// <summary>Tokens actually PROCESSED this request - excludes anything served from the prefix cache. Legitimate for a per-turn "tokens in" figure, NOT for the context-usage bar (use PromptTokens for that).</summary>
    [JsonPropertyName("input_tokens")]
    public int InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int OutputTokens { get; set; }

    /// <summary>"Total prompt tokens including cached tokens" - the real full prompt size for the last request, unlike InputTokens which excludes whatever was served from the prefix cache.</summary>
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }
}

/// <summary>
/// Talks to Lemonade's own management endpoints (not the OpenAI-compatible
/// chat surface). Ported directly from the VB.NET/WPF LemonRind app's
/// Services\LemonadeManagementClient.vb - a thin HttpClient wrapper against
/// the documented endpoints at lemonade-server.ai/docs/api, brought over in
/// full (including TokenizeAsync, a later-stage feature) since it's small,
/// self-contained, and non-UI.
/// </summary>
public class LemonadeManagementClient
{
    private readonly HttpClient _httpClient;

    public LemonadeManagementClient(AppSettings settings)
    {
        _httpClient = new HttpClient { BaseAddress = new Uri(settings.Lemonade.BaseUrl) };

        // This plain HttpClient (unlike the OpenAI-SDK-based chat client)
        // only sets an Authorization header when a key has actually been
        // configured - Lemonade doesn't require one.
        if (!string.IsNullOrWhiteSpace(settings.Lemonade.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.Lemonade.ApiKey);
        }
    }

    /// <summary>Models Lemonade already has on disk, ready to load - not the full downloadable catalog.</summary>
    public async Task<List<LemonadeModelInfo>> ListDownloadedModelsAsync(CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetFromJsonAsync<LemonadeModelsResponse>("models", cancellationToken);
        return response?.Data.Where(m => m.Downloaded).ToList() ?? [];
    }

    public async Task<LemonadeHealthInfo?> GetHealthAsync(CancellationToken cancellationToken)
        => await _httpClient.GetFromJsonAsync<LemonadeHealthInfo>("health", cancellationToken);

    /// <summary>Stats for the most recent request only - call this right after a chat completion finishes.</summary>
    public async Task<LemonadeStatsInfo?> GetStatsAsync(CancellationToken cancellationToken)
        => await _httpClient.GetFromJsonAsync<LemonadeStatsInfo>("stats", cancellationToken);

    /// <summary>Explicitly loads a model into memory. Can take a long time for a large model - callers should show a busy state.</summary>
    public async Task LoadModelAsync(string modelName, CancellationToken cancellationToken)
    {
        var request = new LemonadeLoadRequest { ModelName = modelName };
        var httpResponse = await _httpClient.PostAsJsonAsync("load", request, cancellationToken);
        httpResponse.EnsureSuccessStatusCode();

        var result = await httpResponse.Content.ReadFromJsonAsync<LemonadeLoadResponse>(cancellationToken);
        if (result?.Status != "success")
        {
            throw new InvalidOperationException($"Lemonade failed to load model '{modelName}': {result?.Message}");
        }
    }

    /// <summary>Real tokenization via the currently-loaded model's own tokenizer - an exact count, not a chars/4 approximation.</summary>
    public async Task<int> TokenizeAsync(string content, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(content)) return 0;

        var request = new LemonadeTokenizeRequest { Content = content };
        var httpResponse = await _httpClient.PostAsJsonAsync("tokenize", request, cancellationToken);
        httpResponse.EnsureSuccessStatusCode();

        var result = await httpResponse.Content.ReadFromJsonAsync<LemonadeTokenizeResponse>(cancellationToken);
        return result?.Tokens?.Count ?? 0;
    }
}
