namespace LemonRindBlazor.Services;

/// <summary>
/// Messages the person types while the assistant is still working on a reply. They wait here; the next tool result the
/// model receives carries them (so it can change course at its next step), and anything still waiting when the reply
/// ends is for the screen to send as an ordinary message. Like TurnUsage, one belongs to the code that began the turn
/// and everything it awaits, so a chat and a scheduled job running together keep separate queues.
/// </summary>
public sealed class TurnSteering
{
    /// <summary>Put in front of the person's words so the model treats them as a change of plan, not a new question.</summary>
    public const string Prefix = "The user sent this message while you were still working. Take it into account and adjust your plan if it changes what you should do:";

    private static readonly AsyncLocal<TurnSteering?> CurrentTurn = new();

    private readonly object _gate = new();
    private readonly List<string> _waiting = [];
    private readonly List<string> _applied = [];

    /// <summary>Raised when something is queued, taken back or handed to the model (possibly from another thread).</summary>
    public event Action? Changed;

    /// <summary>The queue of the turn the caller is running inside, if any.</summary>
    public static TurnSteering? Current => CurrentTurn.Value;

    /// <summary>Starts a queue for the turn the caller is about to run (call it in the method that runs the turn).</summary>
    public static TurnSteering Begin()
    {
        var steering = new TurnSteering();
        CurrentTurn.Value = steering;
        return steering;
    }

    public IReadOnlyList<string> Waiting
    {
        get { lock (_gate) return _waiting.ToList(); }
    }

    /// <summary>The words most recently handed to the model, or null if none has been yet.</summary>
    public string? LastHandedOver
    {
        get { lock (_gate) return _applied.Count == 0 ? null : _applied[^1]; }
    }

    public void Queue(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (_gate) _waiting.Add(text.Trim());
        Changed?.Invoke();
    }

    /// <summary>Takes back every message still waiting (for the screen to send or restore).</summary>
    public List<string> Cancel()
    {
        List<string> taken;
        lock (_gate)
        {
            taken = [.. _waiting];
            _waiting.Clear();
        }
        Changed?.Invoke();
        return taken;
    }

    /// <summary>The messages the model was given during the turn (the screen shows and saves them once the turn ends).</summary>
    public List<string> TakeApplied()
    {
        lock (_gate)
        {
            var taken = _applied.ToList();
            _applied.Clear();
            return taken;
        }
    }

    /// <summary>
    /// Called as a tool result is handed back to the model: returns the waiting messages worded for the model (and marks
    /// them as handed over), or null when nothing is waiting.
    /// </summary>
    public string? TakeForModel()
    {
        string text;
        lock (_gate)
        {
            if (_waiting.Count == 0) return null;
            text = string.Join("\n\n", _waiting);
            _applied.AddRange(_waiting);
            _waiting.Clear();
        }
        Changed?.Invoke();
        return $"{Prefix}\n\n{text}";
    }
}
