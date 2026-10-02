using System.Net.Http.Json;
using System.Text.Json.Serialization;
using LemonRindAvalonia.Configuration;

namespace LemonRindAvalonia.Modules.WebSearch;

public class SearXngResult
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";
}

internal class SearXngResponse
{
    [JsonPropertyName("results")]
    public List<SearXngResult> Results { get; set; } = [];

    /// <summary>
    /// Each entry is [engine_name, reason] - this is how SearXNG itself
    /// distinguishes "the search backend is degraded" from "this specific
    /// query genuinely has no matches".
    /// </summary>
    [JsonPropertyName("unresponsive_engines")]
    public List<List<string>>? UnresponsiveEngines { get; set; }
}

/// <summary>Wraps a search's results together with which engines (if any) were unresponsive for it, so the caller can tell a real backend problem apart from a genuinely empty result.</summary>
public class SearXngSearchResult
{
    public List<SearXngResult> Results { get; set; } = [];
    public List<List<string>>? UnresponsiveEngines { get; set; }
}

/// <summary>
/// Talks to the user's self-hosted SearXNG instance. Response shape
/// (results[].title/url/content) matches SearXNG's own JSON output.
///
/// Ported from the VB.NET/WPF LemonRind app's Modules\WebSearch\SearXngClient.vb.
/// </summary>
public class SearXngClient
{
    private readonly HttpClient _httpClient;
    private readonly WebSearchSettings _webSearchSettings;

    /// <summary>
    /// No BaseAddress set on the HttpClient - SearXngBaseUrl is read fresh
    /// from _webSearchSettings on every search instead, so changing it in
    /// Settings takes effect on the very next search with no restart.
    /// </summary>
    public SearXngClient(AppSettings settings)
    {
        _httpClient = new HttpClient();
        _webSearchSettings = settings.WebSearch;
    }

    /// <summary>Top results for a query, capped at maxResults. timeRange (SearXNG's own "day"/"week"/"month"/"year", or null/empty for no filter) restricts results to a recent window.</summary>
    public async Task<SearXngSearchResult> SearchAsync(string query, int maxResults, CancellationToken cancellationToken, string? timeRange = null)
    {
        var baseUri = new Uri(_webSearchSettings.SearXngBaseUrl);
        var timeRangeParam = string.IsNullOrWhiteSpace(timeRange) ? "" : $"&time_range={Uri.EscapeDataString(timeRange)}";
        var url = new Uri(baseUri, $"search?q={Uri.EscapeDataString(query)}&format=json{timeRangeParam}");
        var response = await _httpClient.GetFromJsonAsync<SearXngResponse>(url, cancellationToken)
            ?? throw new InvalidOperationException("SearXNG returned an empty response.");
        return new SearXngSearchResult
        {
            Results = response.Results.Take(maxResults).ToList(),
            UnresponsiveEngines = response.UnresponsiveEngines,
        };
    }
}
