using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using LemonRindAvalonia.Configuration;

namespace LemonRindAvalonia.Modules.Tavily;

public class TavilySearchResultItem
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";
    [JsonPropertyName("content")]
    public string Content { get; set; } = "";
}

internal class TavilySearchResponse
{
    [JsonPropertyName("answer")]
    public string? Answer { get; set; }
    [JsonPropertyName("results")]
    public List<TavilySearchResultItem>? Results { get; set; }
}

internal class TavilyExtractResultItem
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";
    [JsonPropertyName("raw_content")]
    public string? RawContent { get; set; }
}

internal class TavilyExtractFailure
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";
    [JsonPropertyName("error")]
    public string ErrorMessage { get; set; } = "";
}

internal class TavilyExtractResponse
{
    [JsonPropertyName("results")]
    public List<TavilyExtractResultItem>? Results { get; set; }
    [JsonPropertyName("failed_results")]
    public List<TavilyExtractFailure>? FailedResults { get; set; }
}

/// <summary>
/// Talks to Tavily's Search/Extract APIs. Both endpoints are POST with a
/// JSON body and a Bearer token, unlike SearXNG/Jina's GET+query-string
/// shape.
///
/// Ported directly from the VB.NET/WPF LemonRind app's Modules\Tavily\TavilyClient.vb.
/// </summary>
public class TavilyClient
{
    private const string BaseUrl = "https://api.tavily.com";

    private readonly HttpClient _httpClient = new();
    private readonly WebSearchSettings _webSearchSettings;

    public TavilyClient(AppSettings settings)
    {
        _webSearchSettings = settings.WebSearch;
    }

    /// <summary>Tavily's response can include a synthesized "answer" alongside the raw results. Included first when present since it's the most directly useful part for the model to read.</summary>
    public async Task<string> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var requestBody = new { query, max_results = 5 };
        var response = await SendAsync("/search", requestBody, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<TavilySearchResponse>(cancellationToken: cancellationToken);

        var sections = new List<string>();
        if (!string.IsNullOrWhiteSpace(result?.Answer)) sections.Add($"Answer: {result.Answer}");
        if (result?.Results is { Count: > 0 })
        {
            var lines = result.Results.Select(r => $"- {r.Title}: {r.Content} ({r.Url})");
            sections.Add(string.Join(Environment.NewLine, lines));
        }

        return sections.Count > 0 ? string.Join(Environment.NewLine + Environment.NewLine, sections) : "No results found.";
    }

    /// <summary>A per-URL failure can appear in failed_results even though the overall HTTP response is a plain 200 - checking only the status code would miss this and silently show ✓ for a page that was never actually read.</summary>
    public async Task<string> ExtractAsync(string url, CancellationToken cancellationToken)
    {
        var requestBody = new { urls = url };
        var response = await SendAsync("/extract", requestBody, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<TavilyExtractResponse>(cancellationToken: cancellationToken);

        var failure = result?.FailedResults?.FirstOrDefault();
        if (failure is not null)
        {
            throw new InvalidOperationException($"Tavily couldn't extract '{failure.Url}': {failure.ErrorMessage}");
        }

        var page = result?.Results?.FirstOrDefault();
        if (page is null || string.IsNullOrWhiteSpace(page.RawContent))
        {
            throw new InvalidOperationException($"Tavily returned no content for '{url}'.");
        }

        return page.RawContent;
    }

    private async Task<HttpResponseMessage> SendAsync(string path, object body, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _webSearchSettings.TavilyApiKey);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Tavily returned {(int)response.StatusCode}: {errorBody}");
        }

        return response;
    }
}
