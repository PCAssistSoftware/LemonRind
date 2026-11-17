namespace LemonRindAvalonia.Services;

/// <summary>
/// Recognises the error Lemonade gives when a request is longer than the model's context window
/// ("request (N tokens) exceeds the available context size (M tokens)"). The chat then folds the older messages into a
/// summary and asks again once, instead of just reporting the failure.
/// </summary>
public static class ContextOverflow
{
    private const string Marker = "exceeds the available context size";

    public static bool IsOverflow(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains(Marker, StringComparison.OrdinalIgnoreCase)) return true;

            // The SDK's own message is only "Service request failed. Status: 400"; the server's reason lives in the body.
            if (current is System.ClientModel.ClientResultException clientError)
            {
                try
                {
                    var body = clientError.GetRawResponse()?.Content?.ToString();
                    if (body is not null && body.Contains(Marker, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (Exception)
                {
                    // No readable body: treat it as some other error.
                }
            }
        }
        return false;
    }
}
