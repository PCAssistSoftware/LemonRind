Namespace Scheduler

    ''' <summary>One scheduled job that is running right now, and the model it is using.</summary>
    Public NotInheritable Class RunningJob

        Public ReadOnly Property Name As String
        Public ReadOnly Property Model As String

        Public Sub New(name As String, model As String)
            Me.Name = name
            Me.Model = model
        End Sub

    End Class

    ''' <summary>
    ''' Which scheduled jobs are running now, so the rest of the app can say so and be careful: loading a different
    ''' model while a job runs can push the job's model out of Lemonade's memory and stop the job. A job registers
    ''' itself while it works (Start returns a token that ends it when disposed) and anyone interested listens to
    ''' Changed (raised from the job's own thread, so a screen has to marshal to its own thread).
    ''' </summary>
    Public Class RunningJobTracker

        Private ReadOnly _gate As New Object()
        Private ReadOnly _running As New List(Of RunningJob)()

        Public Event Changed As Action

        Public ReadOnly Property Current As IReadOnlyList(Of RunningJob)
            Get
                SyncLock _gate
                    Return _running.ToList()
                End SyncLock
            End Get
        End Property

        ''' <summary>A job has started; dispose the returned token when it ends.</summary>
        Public Function Start(name As String, model As String) As IDisposable
            Dim job As New RunningJob(name, model)
            SyncLock _gate
                _running.Add(job)
            End SyncLock
            RaiseChanged()
            Return New Ending(Me, job)
        End Function

        ' VB can raise an event only from inside its own class, so the token that ends a job calls this.
        Private Sub RaiseChanged()
            RaiseEvent Changed()
        End Sub

        Private NotInheritable Class Ending
            Implements IDisposable

            Private ReadOnly _tracker As RunningJobTracker
            Private ReadOnly _job As RunningJob

            Public Sub New(tracker As RunningJobTracker, job As RunningJob)
                _tracker = tracker
                _job = job
            End Sub

            Public Sub Dispose() Implements IDisposable.Dispose
                SyncLock _tracker._gate
                    _tracker._running.Remove(_job)
                End SyncLock
                _tracker.RaiseChanged()
            End Sub
        End Class

        ''' <summary>The one line for the header: "Weekly report (Qwen3-8B-GGUF)".</summary>
        Public Shared Function Describe(jobs As IEnumerable(Of RunningJob)) As String
            Return String.Join(", ", jobs.Select(Function(j) If(String.IsNullOrWhiteSpace(j.Model), j.Name, $"{j.Name} ({j.Model})")))
        End Function

        ''' <summary>
        ''' The running jobs that would be affected by loading this model: those using a different one. Picking the job's
        ''' own model changes nothing for it, so that needs no question. Nothing is blocked either way: Lemonade may have
        ''' room for both models, which this app cannot know.
        ''' </summary>
        Public Function UsingOtherModel(model As String) As List(Of RunningJob)
            Return Current.Where(Function(j) Not String.Equals(j.Model, model, StringComparison.OrdinalIgnoreCase)).ToList()
        End Function

        ''' <summary>What to ask before switching: "Weekly report is using Qwen3-8B-GGUF. Loading Other-Model may stop it. Switch anyway?"</summary>
        Public Shared Function SwitchQuestion(affected As IEnumerable(Of RunningJob), newModel As String) As String
            Dim usingText = String.Join("; ", affected.Select(Function(j)
                                                                  Dim jobName = If(String.IsNullOrWhiteSpace(j.Name), "A scheduled job", j.Name)
                                                                  Dim modelName = If(String.IsNullOrWhiteSpace(j.Model), "a model", j.Model)
                                                                  Return $"{jobName} is using {modelName}"
                                                              End Function))
            Return $"{usingText}. Loading {newModel} may stop it. Switch anyway?"
        End Function

    End Class

End Namespace
