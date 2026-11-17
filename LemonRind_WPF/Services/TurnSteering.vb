Imports System.Threading

Namespace Services

    ''' <summary>
    ''' Messages the person types while the assistant is still working on a reply. They wait here; the next tool result the
    ''' model receives carries them (so it can change course at its next step), and anything still waiting when the reply
    ''' ends is for the screen to send as an ordinary message. Like TurnUsage, one belongs to the code that began the turn
    ''' and everything it awaits, so a chat and a scheduled job running together keep separate queues.
    ''' </summary>
    Public Class TurnSteering

        ''' <summary>Put in front of the person's words so the model treats them as a change of plan, not a new question.</summary>
        Public Const Prefix As String = "The user sent this message while you were still working. Take it into account and adjust your plan if it changes what you should do:"

        Private Shared ReadOnly CurrentTurn As New AsyncLocal(Of TurnSteering)()

        Private ReadOnly _gate As New Object()
        Private ReadOnly _waiting As New List(Of String)()
        Private ReadOnly _applied As New List(Of String)()

        ''' <summary>Raised when something is queued, taken back or handed to the model (possibly from another thread).</summary>
        Public Event Changed As Action

        ''' <summary>The queue of the turn the caller is running inside, if any.</summary>
        Public Shared ReadOnly Property Current As TurnSteering
            Get
                Return CurrentTurn.Value
            End Get
        End Property

        ''' <summary>Starts a queue for the turn the caller is about to run (call it in the method that runs the turn).</summary>
        Public Shared Function Begin() As TurnSteering
            Dim steering As New TurnSteering()
            CurrentTurn.Value = steering
            Return steering
        End Function

        Public ReadOnly Property Waiting As List(Of String)
            Get
                SyncLock _gate
                    Return New List(Of String)(_waiting)
                End SyncLock
            End Get
        End Property

        ''' <summary>The words most recently handed to the model, or Nothing if none has been yet.</summary>
        Public ReadOnly Property LastHandedOver As String
            Get
                SyncLock _gate
                    Return If(_applied.Count = 0, Nothing, _applied(_applied.Count - 1))
                End SyncLock
            End Get
        End Property

        Public Sub Queue(text As String)
            If String.IsNullOrWhiteSpace(text) Then Return
            SyncLock _gate
                _waiting.Add(text.Trim())
            End SyncLock
            RaiseEvent Changed()
        End Sub

        ''' <summary>Takes back every message still waiting (for the screen to send or restore).</summary>
        Public Function Cancel() As List(Of String)
            Dim taken As List(Of String)
            SyncLock _gate
                taken = New List(Of String)(_waiting)
                _waiting.Clear()
            End SyncLock
            RaiseEvent Changed()
            Return taken
        End Function

        ''' <summary>The messages the model was given during the turn (the screen shows and saves them once the turn ends).</summary>
        Public Function TakeApplied() As List(Of String)
            SyncLock _gate
                Dim taken = New List(Of String)(_applied)
                _applied.Clear()
                Return taken
            End SyncLock
        End Function

        ''' <summary>
        ''' Called as a tool result is handed back to the model: returns the waiting messages worded for the model (and marks
        ''' them as handed over), or Nothing when nothing is waiting.
        ''' </summary>
        Public Function TakeForModel() As String
            Dim text As String
            SyncLock _gate
                If _waiting.Count = 0 Then Return Nothing
                text = String.Join(vbLf & vbLf, _waiting)
                _applied.AddRange(_waiting)
                _waiting.Clear()
            End SyncLock
            RaiseEvent Changed()
            Return Prefix & vbLf & vbLf & text
        End Function

    End Class

End Namespace
