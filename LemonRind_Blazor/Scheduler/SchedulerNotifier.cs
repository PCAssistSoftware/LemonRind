namespace LemonRindBlazor.Scheduler;

/// <summary>
/// DI singleton pub/sub between a scheduled job (running on the poll
/// timer's own background thread) and the main window - both its sidebar
/// (a new/updated session appearing) and, if the user happens to have that
/// exact session open, the visible chat transcript itself.
/// MainViewModel subscribes and marshals the actual work onto the UI
/// thread, since this event is raised from ScheduledJobRunner running on
/// the Timer's own thread pool thread. Fires more than once per job - when
/// the prompt is first saved, when the "running in background" note is
/// saved, and when the final reply/error lands.
///
/// Ported directly from the VB.NET/WPF LemonRind app's Scheduler\SchedulerNotifier.vb.
/// </summary>
public class SchedulerNotifier
{
    public event EventHandler<string>? SessionUpdated;

    public void RaiseSessionUpdated(string sessionId) => SessionUpdated?.Invoke(this, sessionId);
}
