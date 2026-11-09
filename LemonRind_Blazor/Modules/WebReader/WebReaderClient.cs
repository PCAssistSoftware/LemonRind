using HtmlAgilityPack;

namespace LemonRindBlazor.Modules.WebReader;

public class WebPageContent
{
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>
/// Fetches a page (via SsrfSafeHttpFetcher) and extracts its readable text
/// with HtmlAgilityPack, as one flattened text block.
///
/// Ported from the VB.NET/WPF LemonRind app's Modules\WebReader\WebReaderClient.vb.
/// </summary>
public class WebReaderClient(SsrfSafeHttpFetcher fetcher)
{
    // Only a safety bound against an absurdly large page: how much of a page the model sees at once is decided by
    // the tool-result pieces (ToolResultHolder), which hold the whole text and let the model read on to the end.
    private const int MaxTextLength = 2_000_000;

    public async Task<WebPageContent> ReadAsync(string url, CancellationToken cancellationToken, int maxLength = MaxTextLength)
    {
        var html = await fetcher.FetchAsync(url, cancellationToken);

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Strip elements that are never "content" - scripts/styles would
        // otherwise leak raw JS/CSS into the extracted text, and nav/footer
        // are boilerplate that just wastes tokens.
        foreach (var tagName in new[] { "script", "style", "noscript", "nav", "footer", "header" })
        {
            var nodes = doc.DocumentNode.SelectNodes($"//{tagName}");
            if (nodes is null) continue;
            foreach (var node in nodes) node.Remove();
        }

        var title = doc.DocumentNode.SelectSingleNode("//title")?.InnerText.Trim();
        var bodyText = doc.DocumentNode.SelectSingleNode("//body")?.InnerText;

        var cleanedText = CleanWhitespace(HtmlEntity.DeEntitize(bodyText));
        if (cleanedText.Length > maxLength)
        {
            cleanedText = cleanedText[..maxLength] + "... [truncated]";
        }

        return new WebPageContent
        {
            Title = string.IsNullOrWhiteSpace(title) ? "(untitled)" : title,
            Text = cleanedText,
        };
    }

    private static string CleanWhitespace(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        // Collapses the runs of blank lines/spaces HTML text extraction
        // typically leaves behind, without a full-blown regex dependency.
        var lines = text.Split([Environment.NewLine, "\n"], StringSplitOptions.None)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0);
        return string.Join(Environment.NewLine, lines);
    }
}
