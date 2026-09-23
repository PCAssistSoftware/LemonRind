Namespace Scheduler

    ''' <summary>
    ''' DI singleton pub/sub between a scheduled job firing (on the
    ''' background poll Timer's thread - see SchedulerModule.OnPollTimerTick)
    ''' and the main window's sidebar. Window.Activated alone
    ''' (MainWindow.xaml.vb) only refreshes on focus change, which would
    ''' miss a job that completes while the window is already the
    ''' focused/active one. MainViewModel subscribes and marshals the actual
    ''' RefreshSessions() call onto the UI thread, since this event is raised
    ''' from ScheduledJobRunner running on the Timer's own thread pool thread.
    ''' </summary>
    Public Class SchedulerNotifier

        Public Event JobCompleted As EventHandler

        Public Sub RaiseJobCompleted()
            RaiseEvent JobCompleted(Me, EventArgs.Empty)
        End Sub

    End Class

End Namespace
