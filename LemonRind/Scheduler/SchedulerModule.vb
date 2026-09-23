Imports System.Threading
Imports Cronos
Imports Microsoft.Extensions.AI
Imports LemonRind.Configuration
Imports LemonRind.Modules

Namespace Scheduler

    ''' <summary>
    ''' Chat-driven + GUI scheduling - in-process, app-must-be-running (no
    ''' tray/background-service persistence). Two entry points into the same
    ''' SchedulerRepository: this module's schedule_job/list_scheduled_jobs/
    ''' cancel_scheduled_job tools (chat-driven - "remind me every Monday at
    ''' 9am to...") and the Scheduler Settings section (GUI-driven, raw cron
    ''' expressions for anyone who already knows the syntax). A simple
    ''' polling Timer checks for due jobs while the module is running -
    ''' deliberately not Quartz.NET or similar; a full scheduling framework's
    ''' job stores/misfire handling/persistence are solving problems this
    ''' single-user, app-must-be-running design doesn't have.
    ''' </summary>
    Public Class SchedulerModule
        Implements IAssistantModule

        ' How often the poller checks for due jobs - frequent enough that a
        ' job scheduled "in a minute" for testing doesn't feel broken, coarse
        ' enough not to hammer the database for a feature that fires at most
        ' once a minute anyway (standard cron has no finer resolution).
        Private Const PollIntervalSeconds = 30

        ' Generous - a real job prompt can chain several tool calls (search,
        ' then search again, then compose, then send an email), each a real
        ' round-trip. Still bounded, not CancellationToken.None - a stuck
        ' scheduled job (e.g. the model stuck in a non-terminating generation
        ' loop) would otherwise run forever with no visible chat turn to
        ' even notice it's happening, same risk as fact extraction/
        ' compaction (see MainViewModel's BackgroundModelCallTimeout).
        Private Const JobRunTimeoutSeconds = 600

        Private ReadOnly _repository As SchedulerRepository
        Private ReadOnly _runner As ScheduledJobRunner
        Private ReadOnly _moduleSettings As ModuleSettings

        Private _pollTimer As Timer
        ' Guards against a poll tick starting while the previous one (a real
        ' chat completion, potentially slow) is still running - a plain
        ' Timer doesn't wait for its callback to finish before scheduling
        ' the next one.
        Private _isPolling As Boolean = False

        Public Sub New(settings As AppSettings, repository As SchedulerRepository, runner As ScheduledJobRunner)
            _repository = repository
            _runner = runner
            _moduleSettings = settings.Modules
        End Sub

        Public ReadOnly Property Name As String Implements IAssistantModule.Name
            Get
                Return "Scheduler"
            End Get
        End Property

        Public ReadOnly Property ConfigKey As String Implements IAssistantModule.ConfigKey
            Get
                Return "Scheduler"
            End Get
        End Property

        Public ReadOnly Property Description As String Implements IAssistantModule.Description
            Get
                Return "Runs a saved prompt through the assistant on a cron schedule (e.g. ""every Friday at 19:00"")."
            End Get
        End Property

        ''' <summary>Read live off the shared Modules.Enabled dictionary each time, not cached at construction - toggling this module in Settings takes effect immediately, no restart needed. Actually starting/stopping the poll timer still needs OnStartupAsync/OnShutdownAsync to run again - see ModuleRegistry.ReconcileEnabledModulesAsync, called from SettingsViewModel.Save().</summary>
        Public ReadOnly Property IsEnabled As Boolean Implements IAssistantModule.IsEnabled
            Get
                Return _moduleSettings.Enabled.GetValueOrDefault(ConfigKey, False)
            End Get
        End Property

        Public Function OnStartupAsync(cancellationToken As CancellationToken) As Task Implements IAssistantModule.OnStartupAsync
            _pollTimer = New Timer(AddressOf OnPollTimerTick, Nothing, TimeSpan.FromSeconds(PollIntervalSeconds), TimeSpan.FromSeconds(PollIntervalSeconds))
            Return Task.CompletedTask
        End Function

        Public Function OnShutdownAsync(cancellationToken As CancellationToken) As Task Implements IAssistantModule.OnShutdownAsync
            _pollTimer?.Dispose()
            Return Task.CompletedTask
        End Function

        Public Function GetTools() As IEnumerable(Of AITool) Implements IAssistantModule.GetTools
            Return {
                AIFunctionFactory.Create(
                    method:=Function(name As String, cronExpressionText As String, prompt As String) ScheduleJob(name, cronExpressionText, prompt),
                    name:="schedule_job",
                    description:="Creates a scheduled job that runs a prompt through the assistant (with full tool access - " &
                        "web search, files, email, etc.) on a recurring cron schedule. cronExpressionText is a standard 5-field " &
                        "cron expression (minute hour day-of-month month day-of-week), e.g. '0 19 * * 5' for 7pm every Friday."),
                AIFunctionFactory.Create(
                    method:=Function() ListScheduledJobs(),
                    name:="list_scheduled_jobs",
                    description:="Lists every scheduled job, its cron schedule, and when it last/next runs."),
                AIFunctionFactory.Create(
                    method:=Function(name As String) CancelScheduledJob(name),
                    name:="cancel_scheduled_job",
                    description:="Permanently deletes the scheduled job with this exact name.")
            }
        End Function

        ''' <summary>
        ''' Parameter deliberately not named "cronExpression" - VB is
        ''' case-insensitive, so a parameter with that exact name would
        ''' shadow the Cronos.CronExpression *type* itself within this
        ''' method, breaking "CronExpression.Parse(...)" below.
        '''
        ''' Cron fields are interpreted in the machine's own local timezone
        ''' (TimeZoneInfo.Local, so BST/GMT is handled automatically, not a
        ''' hardcoded offset) via Cronos' timezone-aware overload - NOT UTC,
        ''' since a cron expression entered expecting local time should fire
        ''' at that local time, not be silently off by the local UTC offset.
        ''' The returned occurrence is still real UTC (Kind=Utc) for
        ''' storage/comparison - only the *interpretation* of the cron
        ''' fields changes, not how the result is stored.
        ''' </summary>
        Private Function ScheduleJob(name As String, cronExpressionText As String, prompt As String) As String
            Try
                Dim parsed = CronExpression.Parse(cronExpressionText)
                Dim nextRunAt = parsed.GetNextOccurrence(DateTime.UtcNow, TimeZoneInfo.Local, inclusive:=False)
                _repository.CreateJob(name, cronExpressionText, prompt, nextRunAt)
                Return If(nextRunAt.HasValue,
                    $"Scheduled ""{name}"" - next run at {nextRunAt.Value.ToLocalTime():dd/MM/yyyy HH:mm} (your local time).",
                    $"Scheduled ""{name}"", but that cron expression has no future occurrence - it will never actually run. Double-check it.")
            Catch ex As Exception
                Return $"Couldn't schedule that: {ex.Message}. cronExpression must be a standard 5-field cron expression, e.g. '0 19 * * 5' for 7pm every Friday, interpreted in your own local timezone."
            End Try
        End Function

        Private Function ListScheduledJobs() As String
            Dim jobs = _repository.ListJobs()
            If jobs.Count = 0 Then Return "No scheduled jobs."

            Dim lines = jobs.Select(Function(j)
                Dim status = If(j.IsEnabled, "enabled", "disabled")
                Dim nextRun = If(j.NextRunAt.HasValue, $"{j.NextRunAt.Value.ToLocalTime():dd/MM/yyyy HH:mm} (local time)", "never")
                Return $"- ""{j.Name}"" ({status}): {j.CronExpression}, next run {nextRun}"
            End Function)
            Return String.Join(Environment.NewLine, lines)
        End Function

        Private Function CancelScheduledJob(name As String) As String
            Dim job = _repository.ListJobs().FirstOrDefault(Function(j) String.Equals(j.Name, name, StringComparison.OrdinalIgnoreCase))
            If job Is Nothing Then Return $"No scheduled job named ""{name}"" found."

            _repository.DeleteJob(job.Id)
            Return $"Deleted the scheduled job ""{job.Name}""."
        End Function

        Private Async Sub OnPollTimerTick(state As Object)
            If _isPolling Then Return
            _isPolling = True
            Try
                Dim now = DateTime.UtcNow
                Dim dueJobs = _repository.ListJobs().Where(Function(j) j.IsEnabled AndAlso j.NextRunAt.HasValue AndAlso j.NextRunAt.Value <= now).ToList()

                ' Sequential, not parallel - a local Lemonade instance has
                ' limited concurrent generation slots, so running several
                ' scheduled jobs' full tool-enabled turns at once would just
                ' contend with each other and any real user request in
                ' flight, not actually finish faster.
                For Each job In dueJobs
                    Try
                        Using jobTimeoutCts As New CancellationTokenSource(TimeSpan.FromSeconds(JobRunTimeoutSeconds))
                            Await _runner.RunJobAsync(job, jobTimeoutCts.Token)
                        End Using
                    Catch
                        ' Best-effort - one job failing (a bad prompt, a tool
                        ' erroring, Lemonade briefly unreachable) shouldn't
                        ' stop the others from running this tick or break the
                        ' poller for future ticks.
                    Finally
                        ' TimeZoneInfo.Local here too - see ScheduleJob's comment.
                        Dim parsed = CronExpression.Parse(job.CronExpression)
                        _repository.RecordRun(job.Id, now, parsed.GetNextOccurrence(now, TimeZoneInfo.Local, inclusive:=False))
                    End Try
                Next
            Finally
                _isPolling = False
            End Try
        End Sub

    End Class

End Namespace
