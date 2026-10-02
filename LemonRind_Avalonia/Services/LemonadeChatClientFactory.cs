using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI;
using LemonRindAvalonia.Configuration;

namespace LemonRindAvalonia.Services;

/// <summary>
/// Builds the IChatClient this app talks to the model through. Lemonade
/// speaks the OpenAI wire protocol, so this points the official OpenAI.NET
/// SDK at Lemonade's own base URL instead of api.openai.com.
///
/// Ported from the VB.NET/WPF LemonRind app's
/// Services\LemonadeChatClientFactory.vb, including its most recent fix.
/// The OpenAI SDK's default NetworkTimeout/retry policy actively harms a slow
/// local server, which is why the NetworkTimeout/RetryPolicy overrides below
/// exist: the SDK's default 100s
/// timeout fired on requests that were still genuinely working, and its
/// default retry policy resubmitted the same large request to Lemonade
/// several more times rather than waiting, which could crash the server by
/// exceeding its KV cache across the duplicate concurrent requests.
/// </summary>
public class LemonadeChatClientFactory(AppSettings settings)
{
    private readonly LemonadeSettings _settings = settings.Lemonade;

    // A single completion against a local model can legitimately take
    // many minutes (a large prompt can need over a minute of prompt
    // processing before the first token, and a long reply at ~60 tokens/s
    // adds several more) - the SDK's own default NetworkTimeout (100s) is
    // tuned for a cloud API, not this. Deliberately longer than a scheduled
    // job's own overall limit (SchedulerModule.JobRunTimeoutSeconds), so that
    // outer cancellation, not this per-request timeout, is the real backstop
    // for a genuinely stuck request. (A 5 minute value cut off a healthy
    // 16,000-token reply that needed almost 6 minutes.)
    private static readonly TimeSpan RequestNetworkTimeout = TimeSpan.FromMinutes(30);

    public IChatClient CreateChatClient()
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(_settings.BaseUrl),
            NetworkTimeout = RequestNetworkTimeout,
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
        };

        var credential = new ApiKeyCredential(string.IsNullOrWhiteSpace(_settings.ApiKey) ? "lemonade" : _settings.ApiKey);
        var openAiClient = new OpenAIClient(credential, options);

        // Falls back to a placeholder when no chat model is configured yet -
        // the OpenAI SDK throws on an empty model name at client-
        // construction time, but the real model is always set per-request
        // via ChatOptions.ModelId, so this placeholder is never actually
        // sent to Lemonade.
        var chatModel = string.IsNullOrWhiteSpace(_settings.ChatModel) ? "unset" : _settings.ChatModel;

        return new ChatClientBuilder(openAiClient.GetChatClient(chatModel).AsIChatClient())
            .UseFunctionInvocation(configure: client => client.IncludeDetailedErrors = true)
            .Build();
    }
}
