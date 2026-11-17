namespace LemonRindBlazor.Mcp;

/// <summary>
/// Lemonade serves a few tools of its own over MCP (speech to text, picture drawing, listing and asking its models).
/// They live at /mcp on the same host and port as the chat address (which ends in /v1/), so the address is worked out
/// from the one already set in Settings, and the chat key is sent along if there is one.
/// </summary>
public static class LemonadeGateway
{
    public const string ServerName = "lemonade";

    /// <summary>The gateway's address and headers, or null when the chat address is not a web address.</summary>
    public static (string Url, Dictionary<string, string> Headers)? Describe(string baseUrl, string apiKey)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https")) return null;
        var headers = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(apiKey)) headers["Authorization"] = $"Bearer {apiKey.Trim()}";
        return ($"{address.GetLeftPart(UriPartial.Authority)}/mcp", headers);
    }
}
