using System.Diagnostics;
using System.Text;

namespace LemonRindBlazor.Services;

/// <summary>
/// Decides what the assistant's cat (wwwroot/avatar/holo-cat.html, shown in an iframe) should be doing, and sends the
/// page a message whenever that changes:
/// listening while you type; thinking from sending a message until the first words arrive, and while a tool runs;
/// speaking while the reply arrives (the text is passed on so the mouth follows it); idle otherwise; asleep when no
/// chat model is loaded or nothing has happened for a while. A reply that finishes makes it happy for a moment, one
/// that fails makes it annoyed. Only calls <c>send</c>, so it has no idea about the screen.
/// </summary>
public sealed class AvatarBrain(Action<object> send, Func<double>? clock = null)
{
    public const double SleepAfterSeconds = 300;   // nothing happening for this long: the cat goes to sleep
    public const double ListenSeconds = 4;         // after the last key press, back to idle
    public const double MoodSeconds = 6;           // how long a happy or annoyed look lasts
    public const double TalkAfterSeconds = 2.5;    // the mouth keeps going this long after the reply arrived
    public const int TextSpeed = 45;               // characters per second the mouth follows

    private static readonly Stopwatch Uptime = Stopwatch.StartNew();
    private readonly Func<double> _clock = clock ?? (() => Uptime.Elapsed.TotalSeconds);
    private readonly StringBuilder _text = new();  // reply text not yet passed to the page
    private double _lastActivity = (clock ?? (() => Uptime.Elapsed.TotalSeconds))();
    private double _listeningUntil;
    private double _moodUntil;
    private double _stopTalkAt;

    public string State { get; private set; } = "idle";
    public string Mood { get; private set; } = "neutral";
    public bool Busy { get; private set; }
    public bool ModelReady { get; private set; } = true;

    /// <summary>The cat page's address for one of the cats (genie, jess or sid).</summary>
    public static string PageUrl(string cat) => $"avatar/holo-cat.html?debug=0&cat={Uri.EscapeDataString(cat)}&cps={TextSpeed}";

    public void Typing()
    {
        _lastActivity = _clock();
        if (Busy || !ModelReady) return;
        _listeningUntil = _clock() + ListenSeconds;
        SetState("listening");
    }

    public void TurnStarted()
    {
        Busy = true;
        _lastActivity = _clock();
        _text.Clear();
        _stopTalkAt = 0;
        send(new { type = "textStop" });
        SetMood("neutral");
        SetState("thinking");
    }

    /// <summary>A piece of the reply text arrived.</summary>
    public void OnText(string piece)
    {
        if (string.IsNullOrEmpty(piece)) return;
        _text.Append(piece);   // passed on in batches by Tick
        State = "speaking";    // the page switches itself when the first text arrives
    }

    /// <summary>Reasoning arrived, or a tool is about to run.</summary>
    public void OnThinking()
    {
        Flush();
        if (State == "speaking") send(new { type = "textStop" });
        SetState("thinking");
    }

    public void TurnFinished(bool ok)
    {
        Busy = false;
        _lastActivity = _clock();
        Flush();
        if (ok)
        {
            send(new { type = "textEnd" });
            _stopTalkAt = _clock() + TalkAfterSeconds;
            SetMood("happy");
        }
        else
        {
            send(new { type = "textStop" });
            SetState("idle");
            SetMood("annoyed");
        }
        _moodUntil = _clock() + MoodSeconds;
    }

    public void TurnStopped()
    {
        Busy = false;
        _lastActivity = _clock();
        _text.Clear();
        send(new { type = "textStop" });
        SetState("idle");
    }

    /// <summary>Whether a chat model is loaded: with none, the cat sleeps.</summary>
    public void SetModelReady(bool ready)
    {
        ModelReady = ready;
        if (Busy) return; // sending a message loads the model; the reply decides the state
        if (!ready) SetState("sleep");
        else if (State == "sleep" && !Sleepy()) SetState("idle");
    }

    /// <summary>Called a few times a second: pass on new text, and end listening, moods, talking and wakefulness.</summary>
    public void Tick()
    {
        Flush();
        var now = _clock();
        if (_stopTalkAt > 0 && now >= _stopTalkAt)
        {
            _stopTalkAt = 0;
            send(new { type = "textStop" });
            SetState("idle");
        }
        if (State == "listening" && now >= _listeningUntil) SetState("idle");
        if (Mood != "neutral" && now >= _moodUntil) SetMood("neutral");
        if (!Busy && State == "idle" && (!ModelReady || Sleepy())) SetState("sleep");
    }

    public void Flush()
    {
        if (_text.Length == 0) return;
        send(new { type = "text", value = _text.ToString() });
        _text.Clear();
    }

    /// <summary>Tell a freshly loaded page the current state and mood.</summary>
    public void Resend()
    {
        send(new { type = "state", value = State });
        send(new { type = "mood", value = Mood });
    }

    private bool Sleepy() => _clock() - _lastActivity >= SleepAfterSeconds;

    private void SetState(string state)
    {
        if (state == State) return;
        State = state;
        send(new { type = "state", value = state });
    }

    private void SetMood(string mood)
    {
        if (mood == Mood) return;
        Mood = mood;
        send(new { type = "mood", value = mood });
    }
}
