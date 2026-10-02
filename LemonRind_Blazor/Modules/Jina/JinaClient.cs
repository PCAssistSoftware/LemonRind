using System.Net.Http.Headers;
using LemonRindBlazor.Configuration;

namespace LemonRindBlazor.Modules.Jina;

/// <summary>
/// Talks to Jina AI's Reader (r.jina.ai) and Search (s.jina.ai) APIs.
/// Reader returns clean plain text (Title/URL Source/Markdown Content) and
/// works without an API key at a lower rate; Search requires one (returns
/// 401 without one).
///
/// Ported directly from the VB.NET/WPF LemonRind app's Modules\Jina\JinaClient.vb.
/// </summary>
public class JinaClient
{
    private readonly HttpClient _httpClient = new();
    private readonly WebSearchSettings _webSearchSettings;

    public JinaClient(AppSettings settings)
    {
        _webSearchSettings = settings.WebSearch;
    }

    public Task<string> ReadPageAsync(string url, CancellationToken cancellationToken)
        => SendAsync($"https://r.jina.ai/{url}", cancellationToken);

    public Task<string> SearchAsync(string query, CancellationToken cancellationToken)
        => SendAsync($"https://s.jina.ai/?q={Uri.EscapeDataString(query)}", cancellationToken);

    /// <summary>
    /// Reads the response body before checking success, and includes it in
    /// the thrown message rather than a bare status code - Jina's own JSON
    /// error responses (e.g. the 401 for Search with no key) already
    /// contain a clear, human-readable explanation, which is exactly the
    /// kind of message worth reaching the model directly (see
    /// LemonadeChatClientFactory's IncludeDetailedErrors setting).
    /// </summary>
    private async Task<string> SendAsync(string url, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(_webSearchSettings.JinaApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _webSearchSettings.JinaApiKey);
        }

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Jina returned {(int)response.StatusCode}: {body}");
        }

        return body;
    }
}
