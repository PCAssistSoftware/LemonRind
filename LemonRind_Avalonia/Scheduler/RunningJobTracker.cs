namespace LemonRindAvalonia.Scheduler;

/// <summary>One scheduled job that is running right now, and the model it is using.</summary>
public sealed record RunningJob(string Name, string Model);

/// <summary>
/// Which scheduled jobs are running now, so the rest of the app can say so and be careful: loading a different
/// model while a job runs can push the job's model out of Lemonade's memory and stop the job. A job registers
/// itself while it works (Start returns a token that ends it when disposed) and anyone interested listens to
/// Changed (raised from the job's own thread, so a screen has to marshal to its own thread).
/// </summary>
public class RunningJobTracker
{
    private readonly object _gate = new();
    private readonly List<RunningJob> _running = [];

    public event Action? Changed;

    public IReadOnlyList<RunningJob> Current
    {
        get
        {
            lock (_gate) return _running.ToList();
        }
    }

    /// <summary>A job has started; dispose the returned token when it ends.</summary>
    public IDisposable Start(string name, string model)
    {
        var job = new RunningJob(name, model);
        lock (_gate) _running.Add(job);
        Changed?.Invoke();
        return new Ending(this, job);
    }

    private sealed class Ending(RunningJobTracker tracker, RunningJob job) : IDisposable
    {
        public void Dispose()
        {
            lock (tracker._gate) tracker._running.Remove(job);
            tracker.Changed?.Invoke();
        }
    }

    /// <summary>The one line for the header: "Weekly report (Qwen3-8B-GGUF)".</summary>
    public static string Describe(IEnumerable<RunningJob> jobs) =>
        string.Join(", ", jobs.Select(j => string.IsNullOrWhiteSpace(j.Model) ? j.Name : $"{j.Name} ({j.Model})"));

    /// <summary>
    /// The running jobs that would be affected by loading this model: those using a different one. Picking the job's
    /// own model changes nothing for it, so that needs no question. Nothing is blocked either way: Lemonade may have
    /// room for both models, which this app cannot know.
    /// </summary>
    public List<RunningJob> UsingOtherModel(string model) =>
        Current.Where(j => !string.Equals(j.Model, model, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>What to ask before switching: "Weekly report is using Qwen3-8B-GGUF. Loading Other-Model may stop it. Switch anyway?"</summary>
    public static string SwitchQuestion(IEnumerable<RunningJob> affected, string newModel)
    {
        var using_ = string.Join("; ", affected.Select(j =>
            $"{(string.IsNullOrWhiteSpace(j.Name) ? "A scheduled job" : j.Name)} is using {(string.IsNullOrWhiteSpace(j.Model) ? "a model" : j.Model)}"));
        return $"{using_}. Loading {newModel} may stop it. Switch anyway?";
    }
}
