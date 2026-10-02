using global::Firecrawl;
using global::Firecrawl.Models;
using LemonRindAvalonia.Configuration;

namespace LemonRindAvalonia.Modules.Firecrawl;

/// <summary>
/// Wraps the official Firecrawl .NET SDK (firecrawl-sdk on NuGet) - Crawl
/// is a real async job (POST to start, poll to completion), and the
/// official SDK already solves that polling/pagination correctly rather
/// than this app reimplementing it by hand. A fresh FirecrawlClient is
/// constructed per call (cheap - it just wraps the one shared HttpClient
/// below) reading the API key live from settings each time, rather than
/// baking the key in once at DI-singleton construction.
///
/// Ported directly from the VB.NET/WPF LemonRind app's Modules\Firecrawl\FirecrawlService.vb.
/// Method signatures confirmed via reflection against the installed
/// Firecrawl.dll before writing this, not assumed.
/// </summary>
public class FirecrawlService
{
    private readonly HttpClient _httpClient = new();
    private readonly WebSearchSettings _webSearchSettings;

    public FirecrawlService(AppSettings settings)
    {
        _webSearchSettings = settings.WebSearch;
    }

    private FirecrawlClient CreateClient() => new(_webSearchSettings.FirecrawlApiKey, httpClient: _httpClient);

    public async Task<string> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var options = new SearchOptions { Limit = 5 };
        var result = await CreateClient().SearchAsync(query, options, cancellationToken);

        if (result.Web is not { Count: > 0 }) return "No results found.";

        var lines = result.Web.Select(hit => $"- {hit.Title}: {hit.Description} ({hit.Url})");
        return string.Join(Environment.NewLine, lines);
    }

    public async Task<string> ScrapeAsync(string url, CancellationToken cancellationToken)
    {
        var document = await CreateClient().ScrapeAsync(url, new ScrapeOptions(), cancellationToken);

        if (string.IsNullOrWhiteSpace(document.Markdown))
        {
            throw new InvalidOperationException($"Firecrawl returned no readable content for '{url}'.");
        }

        return document.Markdown;
    }

    /// <summary>
    /// maxPages deliberately caps CrawlOptions.Limit rather than trusting
    /// the API's own raw default (10,000 per Firecrawl's own docs) - an
    /// unbounded crawl on a real site would take a long time and burn a lot
    /// of credits for one tool call. The 120s timeout / 3s poll interval
    /// are generous for a bounded crawl this size, not for an unbounded
    /// one - same "bounded timeout on every long-running call" pattern
    /// already used for Scheduler jobs/memory extraction elsewhere in this
    /// app. focus, when given, is passed straight through as
    /// CrawlOptions.Prompt - a natural-language field Firecrawl itself uses
    /// server-side to generate includePaths/excludePaths. Without it, a
    /// crawl tends to return mostly shallow nav/structure pages rather than
    /// the deeper content actually relevant to the request, since an
    /// unscoped crawl has no way to know which of a site's many links
    /// matter.
    /// </summary>
    public async Task<string> CrawlAsync(string url, int maxPages, string? focus, CancellationToken cancellationToken)
    {
        var options = new CrawlOptions { Limit = Math.Max(1, Math.Min(maxPages, 30)) };
        if (!string.IsNullOrWhiteSpace(focus)) options.Prompt = focus;
        var job = await CreateClient().CrawlAsync(url, options, pollIntervalSec: 3, timeoutSec: 120, cancellationToken: cancellationToken);

        if (!job.IsDone || job.Data is not { Count: > 0 })
        {
            throw new InvalidOperationException($"Firecrawl crawl of '{url}' finished with no pages (status: {job.Status}).");
        }

        var pages = job.Data.Select((doc, index) =>
        {
            string? sourceUrl = null;
            if (doc.Metadata is not null && doc.Metadata.TryGetValue("sourceURL", out var value))
            {
                sourceUrl = value?.ToString();
            }
            return $"--- {sourceUrl ?? $"page {index + 1}"} ---{Environment.NewLine}{doc.Markdown}";
        });

        return string.Join(Environment.NewLine + Environment.NewLine, pages);
    }
}
