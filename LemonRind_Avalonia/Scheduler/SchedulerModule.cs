using System.Diagnostics;
using Cronos;
using Microsoft.Extensions.AI;
using LemonRindAvalonia.Configuration;
using LemonRindAvalonia.Modules;

namespace LemonRindAvalonia.Scheduler;

/// <summary>
/// Chat-driven scheduling - in-process, app-must-be-running (no tray/
/// background-service persistence). The module's schedule_job/
/// list_scheduled_jobs/cancel_scheduled_job tools let the model create jobs
/// from a chat request ("remind me every Monday at 9am to..."). A simple
/// polling Timer checks for due jobs while the module is running -
/// deliberately not Quartz.NET or similar; a full scheduling framework's
/// job stores/misfire handling/persistence are solving problems this
/// single-user, app-must-be-running design doesn't have.
///
/// Ported directly from the VB.NET/WPF LemonRind app's Scheduler\SchedulerModule.vb,
/// including its own JobRunTimeoutSeconds=1500 (raised from an original 600
/// after live testing showed a genuinely legitimate job routinely needing
/// more than 10 minutes on local hardware).
/// </summary>
public class SchedulerModule(AppSettings settings, SchedulerRepository repository, ScheduledJobRunner runner) : IAssistantModule
{
    // How often the poller checks for due jobs - frequent enough that a job
    // scheduled "in a minute" for testing doesn't feel broken, coarse
    // enough not to hammer the database for a feature that fires at most
    // once a minute anyway (standard cron has no finer resolution).
    private const int PollIntervalSeconds = 30;

    // How long one run may take is a setting (Settings > Scheduler), read at
    // the start of each run: settings.Scheduler.JobTimeoutSeconds.

    private readonly ModuleSettings _moduleSettings = settings.Modules;

    private Timer? _pollTimer;

    // Guards against a poll tick starting while the previous one (a real
    // chat completion, potentially slow) is still running - a plain Timer
    // doesn't wait for its callback to finish before scheduling the next one.
    private bool _isPolling;

    // One run at a time, whether it was started by the clock or by Run now: a local Lemonade has limited generation
    // slots, and two jobs at once would only slow each other down.
    private readonly SemaphoreSlim _runGate = new(1, 1);

    public string Name => "Scheduler";
    public string ConfigKey => "Scheduler";
    public string Description => "Runs a saved prompt through the assistant on a cron schedule (e.g. \"every Friday at 19:00\").";

    public bool IsEnabled => _moduleSettings.Enabled.GetValueOrDefault(ConfigKey, false);

    public Task OnStartupAsync(CancellationToken cancellationToken)
    {
        _pollTimer = new Timer(OnPollTimerTick, null, TimeSpan.FromSeconds(PollIntervalSeconds), TimeSpan.FromSeconds(PollIntervalSeconds));
        return Task.CompletedTask;
    }

    public Task OnShutdownAsync(CancellationToken cancellationToken)
    {
        _pollTimer?.Dispose();
        return Task.CompletedTask;
    }

    public IEnumerable<AITool> GetTools()
    {
        return
        [
            AIFunctionFactory.Create(
                method: (string name, string cronExpressionText, string prompt) => ScheduleJob(name, cronExpressionText, prompt),
                name: "schedule_job",
                description: "Creates a scheduled job that runs a prompt through the assistant (with full tool access - " +
                    "web search, files, email, etc.) on a recurring cron schedule. cronExpressionText is a standard 5-field " +
                    "cron expression (minute hour day-of-month month day-of-week), e.g. '0 19 * * 5' for 7pm every Friday."),
            AIFunctionFactory.Create(
                method: () => ListScheduledJobs(),
                name: "list_scheduled_jobs",
                description: "Lists every scheduled job, its cron schedule, and when it last/next runs."),
            AIFunctionFactory.Create(
                method: (string name) => CancelScheduledJob(name),
                name: "cancel_scheduled_job",
                description: "Permanently deletes the scheduled job with this exact name."),
        ];
    }

    /// <summary>
    /// Cron fields are interpreted in the machine's own local timezone
    /// (TimeZoneInfo.Local, so BST/GMT is handled automatically) via
    /// Cronos' timezone-aware overload - NOT UTC, since a cron expression
    /// entered expecting local time should fire at that local time. The
    /// returned occurrence is still real UTC for storage/comparison - only
    /// the interpretation of the cron fields changes.
    /// </summary>
    private string ScheduleJob(string name, string cronExpressionText, string prompt)
    {
        try
        {
            var parsed = CronExpression.Parse(cronExpressionText);
            var nextRunAt = parsed.GetNextOccurrence(DateTime.UtcNow, TimeZoneInfo.Local, inclusive: false);
            repository.CreateJob(name, cronExpressionText, prompt, nextRunAt);
            return nextRunAt.HasValue
                ? $"Scheduled \"{name}\" - next run at {nextRunAt.Value.ToLocalTime():dd/MM/yyyy HH:mm} (your local time)."
                : $"Scheduled \"{name}\", but that cron expression has no future occurrence - it will never actually run. Double-check it.";
        }
        catch (Exception ex)
        {
            return $"Couldn't schedule that: {ex.Message}. cronExpression must be a standard 5-field cron expression, e.g. '0 19 * * 5' for 7pm every Friday, interpreted in your own local timezone.";
        }
    }

    private string ListScheduledJobs()
    {
        var jobs = repository.ListJobs();
        if (jobs.Count == 0) return "No scheduled jobs.";

        var lines = jobs.Select(j =>
        {
            var status = j.IsEnabled ? "enabled" : "disabled";
            var nextRun = j.NextRunAt.HasValue ? $"{j.NextRunAt.Value.ToLocalTime():dd/MM/yyyy HH:mm} (local time)" : "never";
            return $"- \"{j.Name}\" ({status}): {j.CronExpression}, next run {nextRun}";
        });
        return string.Join(Environment.NewLine, lines);
    }

    private string CancelScheduledJob(string name)
    {
        var job = repository.ListJobs().FirstOrDefault(j => string.Equals(j.Name, name, StringComparison.OrdinalIgnoreCase));
        if (job is null) return $"No scheduled job named \"{name}\" found.";

        repository.DeleteJob(job.Id);
        return $"Deleted the scheduled job \"{job.Name}\".";
    }

    /// <summary>
    /// Runs one job right now (the "Run now" button), the same way a scheduled run goes, and records the result. Its normal
    /// schedule is left as it was. Waits its turn if another job is running.
    /// </summary>
    public async Task<JobRunResult> RunNowAsync(string jobId, CancellationToken cancellationToken)
    {
        var job = repository.ListJobs().FirstOrDefault(j => j.Id == jobId)
            ?? throw new InvalidOperationException("That job no longer exists.");

        await _runGate.WaitAsync(cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        var result = new JobRunResult("failed", 0);
        try
        {
            using var jobTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            jobTimeoutCts.CancelAfter(TimeSpan.FromSeconds(settings.Scheduler.JobTimeoutSeconds));
            result = await runner.RunJobAsync(job, jobTimeoutCts.Token);
        }
        catch
        {
            result = new JobRunResult("failed", stopwatch.Elapsed.TotalSeconds);
        }
        finally
        {
            try
            {
                repository.RecordRun(job.Id, DateTime.UtcNow, job.NextRunAt, result.Status, result.Seconds);
            }
            finally
            {
                _runGate.Release();
            }
        }
        return result;
    }

    private async void OnPollTimerTick(object? state)
    {
        if (_isPolling) return;
        _isPolling = true;
        try
        {
            var now = DateTime.UtcNow;
            var dueJobs = repository.ListJobs().Where(j => j.IsEnabled && j.NextRunAt.HasValue && j.NextRunAt.Value <= now).ToList();

            // Sequential, not parallel - a local Lemonade instance has
            // limited concurrent generation slots, so running several
            // scheduled jobs' full tool-enabled turns at once would just
            // contend with each other and any real user request in flight.
            foreach (var job in dueJobs)
            {
                var stopwatch = Stopwatch.StartNew();
                var result = new JobRunResult("failed", 0);
                await _runGate.WaitAsync();
                try
                {
                    using var jobTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(settings.Scheduler.JobTimeoutSeconds));
                    result = await runner.RunJobAsync(job, jobTimeoutCts.Token);
                }
                catch
                {
                    // Best-effort - one job failing shouldn't stop the
                    // others from running this tick or break the poller for
                    // future ticks.
                    result = new JobRunResult("failed", stopwatch.Elapsed.TotalSeconds);
                }
                finally
                {
                    try
                    {
                        var parsed = CronExpression.Parse(job.CronExpression);
                        repository.RecordRun(job.Id, now, parsed.GetNextOccurrence(now, TimeZoneInfo.Local, inclusive: false), result.Status, result.Seconds);
                    }
                    finally
                    {
                        _runGate.Release();
                    }
                }
            }
        }
        finally
        {
            _isPolling = false;
        }
    }
}
