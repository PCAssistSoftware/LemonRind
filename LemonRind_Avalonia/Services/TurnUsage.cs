using Microsoft.Extensions.AI;
namespace LemonRindAvalonia.Services;

/// <summary>The numbers for one finished turn (a chat reply, or a whole scheduled job run), added up over every model request it made.</summary>
public sealed record TurnStats(string? Model, int InputTokens, int OutputTokens, double TokensPerSecond, double TimeToFirstToken);

/// <summary>
/// Collects the numbers behind one turn while it runs, so they can be saved with the reply and shown on the usage screen.
/// A turn that uses tools makes several model requests; each one adds its own numbers here (see
/// UsageTrackingChatClient) and each tool call is noted (see ToolResultHolder). The collector belongs to the code
/// that began the turn and everything it awaits, so a chat and a scheduled job running together keep separate sets.
/// </summary>
public sealed class TurnUsage
{
    private static readonly AsyncLocal<TurnUsage?> CurrentTurn = new();

    private readonly object _gate = new();
    private bool _closed;
    private string? _model;
    private int _requests;
    private int _inputTokens;
    private int _outputTokens;
    private double _tokensPerSecond;
    private double _timeToFirstToken;
    private readonly List<(string Name, bool Failed)> _toolCalls = [];

    public bool IsClosed
    {
        get { lock (_gate) return _closed; }
    }

    /// <summary>The turn being collected for the code running now, if any.</summary>
    public static TurnUsage? Current => CurrentTurn.Value;

    /// <summary>Starts collecting for the turn the caller is about to run (call it in the method that runs the turn).</summary>
    public static TurnUsage Begin()
    {
        var turn = new TurnUsage();
        CurrentTurn.Value = turn;
        return turn;
    }

    /// <summary>
    /// Adds one finished request. Work done adds up; the speed is the later request's (when it has one); the wait for the
    /// first token stays the first request's, because that is how long you waited before anything appeared.
    /// </summary>
    public void AddRequest(string? model, int inputTokens, int outputTokens, double tokensPerSecond, double timeToFirstToken)
    {
        lock (_gate)
        {
            if (_closed) return;
            _requests++;
            if (!string.IsNullOrWhiteSpace(model)) _model = model;
            _inputTokens += inputTokens;
            _outputTokens += outputTokens;
            if (tokensPerSecond > 0) _tokensPerSecond = tokensPerSecond;
            if (_requests == 1) _timeToFirstToken = timeToFirstToken;
        }
    }

    public void AddToolCall(string name, bool failed)
    {
        lock (_gate)
        {
            if (!_closed) _toolCalls.Add((name, failed));
        }
    }

    /// <summary>The tool calls as saved with a message: "web_search:1,read_webpage:0" (1 worked, 0 failed). Null when none.</summary>
    public string? ToolCallsForStorage()
    {
        lock (_gate)
        {
            return _toolCalls.Count == 0 ? null : string.Join(",", _toolCalls.Select(t => $"{t.Name}:{(t.Failed ? 0 : 1)}"));
        }
    }

    /// <summary>Stops collecting (later requests, such as a background fact-extraction call, are not part of the turn) and returns the totals, or null if no request was recorded.</summary>
    public TurnStats? Close()
    {
        lock (_gate)
        {
            _closed = true;
            return _requests == 0 ? null : new TurnStats(_model, _inputTokens, _outputTokens, _tokensPerSecond, _timeToFirstToken);
        }
    }
}

/// <summary>
/// Sits inside the tool-calling loop, so it sees every request to the model. After each one finishes it asks Lemonade for
/// that request's own numbers (its /stats endpoint describes the most recent request) and adds them to the turn being
/// collected. Best effort: if the numbers cannot be had, the reply is unaffected and the turn simply has fewer numbers.
/// </summary>
public sealed class UsageTrackingChatClient(IChatClient inner, LemonadeManagementClient management) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        await RecordAsync(options);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            yield return update;
        }
        await RecordAsync(options); // only reached when the reply finished: a stopped one has no numbers worth keeping
    }

    private async Task RecordAsync(ChatOptions? options)
    {
        var turn = TurnUsage.Current;
        if (turn is null || turn.IsClosed) return;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var stats = await management.GetStatsAsync(timeout.Token);
            if (stats is null || (stats.InputTokens == 0 && stats.OutputTokens == 0)) return;

            var model = options?.ModelId ?? this.GetService<ChatClientMetadata>()?.DefaultModelId;
            turn.AddRequest(model, stats.InputTokens, stats.OutputTokens, stats.TokensPerSecond, stats.TimeToFirstToken);
        }
        catch
        {
            // Best effort, as with the stats panel.
        }
    }
}
