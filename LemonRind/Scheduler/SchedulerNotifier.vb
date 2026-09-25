Namespace Scheduler

    ''' <summary>
    ''' DI singleton pub/sub between a scheduled job (running on the poll
    ''' Timer's own background thread - see SchedulerModule.OnPollTimerTick)
    ''' and the main window - both its sidebar (a new/updated session
    ''' appearing) and, if the user happens to have that exact session open,
    ''' the visible chat transcript itself (see MainViewModel's
    ''' ReloadCurrentSessionIfMatching). Window.Activated alone
    ''' (MainWindow.xaml.vb) only refreshes on focus change, which would
    ''' miss a job completing while the window is already the focused/active
    ''' one. MainViewModel subscribes and marshals the actual work onto the
    ''' UI thread, since this event is raised from ScheduledJobRunner running
    ''' on the Timer's own thread pool thread. Fires more than once per job
    ''' (see ScheduledJobRunner) - when the prompt is first saved, when the
    ''' "running in background" note is saved, and when the final reply/
    ''' error lands - not only on completion, despite the older name this
    ''' replaced (JobCompleted).
    ''' </summary>
    Public Class SchedulerNotifier

        Public Event SessionUpdated As EventHandler(Of String)

        Public Sub RaiseSessionUpdated(sessionId As String)
            RaiseEvent SessionUpdated(Me, sessionId)
        End Sub

    End Class

End Namespace
