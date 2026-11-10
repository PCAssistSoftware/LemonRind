Imports System.Threading
Imports Microsoft.Extensions.AI

Namespace Services

    ''' <summary>The numbers for one finished turn (a chat reply, or a whole scheduled job run), added up over every model request it made.</summary>
    Public NotInheritable Class TurnStats

        Public ReadOnly Property Model As String
        Public ReadOnly Property InputTokens As Integer
        Public ReadOnly Property OutputTokens As Integer
        Public ReadOnly Property TokensPerSecond As Double
        Public ReadOnly Property TimeToFirstToken As Double

        Public Sub New(model As String, inputTokens As Integer, outputTokens As Integer, tokensPerSecond As Double, timeToFirstToken As Double)
            Me.Model = model
            Me.InputTokens = inputTokens
            Me.OutputTokens = outputTokens
            Me.TokensPerSecond = tokensPerSecond
            Me.TimeToFirstToken = timeToFirstToken
        End Sub

    End Class

    ''' <summary>
    ''' Collects the numbers behind one turn while it runs, so they can be saved with the reply and shown on the usage screen.
    ''' A turn that uses tools makes several model requests; each one adds its own numbers here (see
    ''' UsageTrackingChatClient) and each tool call is noted (see ToolResultHolder). The collector belongs to the code
    ''' that began the turn and everything it awaits, so a chat and a scheduled job running together keep separate sets.
    ''' </summary>
    Public NotInheritable Class TurnUsage

        Private Shared ReadOnly CurrentTurn As New AsyncLocal(Of TurnUsage)()

        Private ReadOnly _gate As New Object()
        Private _closed As Boolean
        Private _model As String
        Private _requests As Integer
        Private _inputTokens As Integer
        Private _outputTokens As Integer
        Private _tokensPerSecond As Double
        Private _timeToFirstToken As Double
        Private ReadOnly _toolCalls As New List(Of (Name As String, Failed As Boolean))()

        ''' <summary>The turn being collected for the code running now, if any.</summary>
        Public Shared ReadOnly Property Current As TurnUsage
            Get
                Return CurrentTurn.Value
            End Get
        End Property

        ''' <summary>Starts collecting for the turn the caller is about to run (call it in the method that runs the turn).</summary>
        Public Shared Function Begin() As TurnUsage
            Dim turn As New TurnUsage()
            CurrentTurn.Value = turn
            Return turn
        End Function

        Public ReadOnly Property IsClosed As Boolean
            Get
                SyncLock _gate
                    Return _closed
                End SyncLock
            End Get
        End Property

        ''' <summary>
        ''' Adds one finished request. Work done adds up; the speed is the later request's (when it has one); the wait for the
        ''' first token stays the first request's, because that is how long you waited before anything appeared.
        ''' </summary>
        Public Sub AddRequest(model As String, inputTokens As Integer, outputTokens As Integer, tokensPerSecond As Double, timeToFirstToken As Double)
            SyncLock _gate
                If _closed Then Return
                _requests += 1
                If Not String.IsNullOrWhiteSpace(model) Then _model = model
                _inputTokens += inputTokens
                _outputTokens += outputTokens
                If tokensPerSecond > 0 Then _tokensPerSecond = tokensPerSecond
                If _requests = 1 Then _timeToFirstToken = timeToFirstToken
            End SyncLock
        End Sub

        Public Sub AddToolCall(name As String, failed As Boolean)
            SyncLock _gate
                If Not _closed Then _toolCalls.Add((name, failed))
            End SyncLock
        End Sub

        ''' <summary>The tool calls as saved with a message: "web_search:1,read_webpage:0" (1 worked, 0 failed). Nothing when none.</summary>
        Public Function ToolCallsForStorage() As String
            SyncLock _gate
                If _toolCalls.Count = 0 Then Return Nothing
                Return String.Join(",", _toolCalls.Select(Function(t) $"{t.Name}:{If(t.Failed, 0, 1)}"))
            End SyncLock
        End Function

        ''' <summary>Stops collecting (later requests, such as a background fact-extraction call, are not part of the turn) and returns the totals, or Nothing if no request was recorded.</summary>
        Public Function Close() As TurnStats
            SyncLock _gate
                _closed = True
                If _requests = 0 Then Return Nothing
                Return New TurnStats(_model, _inputTokens, _outputTokens, _tokensPerSecond, _timeToFirstToken)
            End SyncLock
        End Function

    End Class

    ''' <summary>
    ''' Sits inside the tool-calling loop, so it sees every request to the model. After each one finishes it asks Lemonade for
    ''' that request's own numbers (its /stats endpoint describes the most recent request) and adds them to the turn being
    ''' collected. Best effort: if the numbers cannot be had, the reply is unaffected and the turn simply has fewer numbers.
    ''' </summary>
    Public NotInheritable Class UsageTrackingChatClient
        Inherits DelegatingChatClient

        Private ReadOnly _management As LemonadeManagementClient

        Public Sub New(inner As IChatClient, management As LemonadeManagementClient)
            MyBase.New(inner)
            _management = management
        End Sub

        Public Overrides Async Function GetResponseAsync(messages As IEnumerable(Of ChatMessage), Optional options As ChatOptions = Nothing, Optional cancellationToken As CancellationToken = Nothing) As Task(Of ChatResponse)
            Dim response = Await MyBase.GetResponseAsync(messages, options, cancellationToken)
            Await RecordAsync(options)
            Return response
        End Function

        ' VB cannot write an async iterator, so the stream is wrapped by hand: it passes every update through and, once the
        ' reply has finished, records the request's numbers.
        Public Overrides Function GetStreamingResponseAsync(messages As IEnumerable(Of ChatMessage), Optional options As ChatOptions = Nothing, Optional cancellationToken As CancellationToken = Nothing) As IAsyncEnumerable(Of ChatResponseUpdate)
            Return New RecordingStream(MyBase.GetStreamingResponseAsync(messages, options, cancellationToken), Me, options)
        End Function

        Private Async Function RecordAsync(options As ChatOptions) As Task
            Dim turn = TurnUsage.Current
            If turn Is Nothing OrElse turn.IsClosed Then Return

            Try
                Using timeout As New CancellationTokenSource(TimeSpan.FromSeconds(3))
                    Dim stats = Await _management.GetStatsAsync(timeout.Token)
                    If stats Is Nothing OrElse (stats.InputTokens = 0 AndAlso stats.OutputTokens = 0) Then Return

                    Dim model As String = If(options?.ModelId, Me.GetService(Of ChatClientMetadata)()?.DefaultModelId)
                    turn.AddRequest(model, stats.InputTokens, stats.OutputTokens, stats.TokensPerSecond, stats.TimeToFirstToken)
                End Using
            Catch
                ' Best effort, as with the stats panel.
            End Try
        End Function

        Private NotInheritable Class RecordingStream
            Implements IAsyncEnumerable(Of ChatResponseUpdate)

            Private ReadOnly _source As IAsyncEnumerable(Of ChatResponseUpdate)
            Private ReadOnly _owner As UsageTrackingChatClient
            Private ReadOnly _options As ChatOptions

            Public Sub New(source As IAsyncEnumerable(Of ChatResponseUpdate), owner As UsageTrackingChatClient, options As ChatOptions)
                _source = source
                _owner = owner
                _options = options
            End Sub

            Public Function GetAsyncEnumerator(Optional cancellationToken As CancellationToken = Nothing) As IAsyncEnumerator(Of ChatResponseUpdate) Implements IAsyncEnumerable(Of ChatResponseUpdate).GetAsyncEnumerator
                Return New RecordingEnumerator(_source.GetAsyncEnumerator(cancellationToken), _owner, _options)
            End Function
        End Class

        Private NotInheritable Class RecordingEnumerator
            Implements IAsyncEnumerator(Of ChatResponseUpdate)

            Private ReadOnly _inner As IAsyncEnumerator(Of ChatResponseUpdate)
            Private ReadOnly _owner As UsageTrackingChatClient
            Private ReadOnly _options As ChatOptions

            Public Sub New(inner As IAsyncEnumerator(Of ChatResponseUpdate), owner As UsageTrackingChatClient, options As ChatOptions)
                _inner = inner
                _owner = owner
                _options = options
            End Sub

            Public ReadOnly Property Current As ChatResponseUpdate Implements IAsyncEnumerator(Of ChatResponseUpdate).Current
                Get
                    Return _inner.Current
                End Get
            End Property

            Public Function MoveNextAsync() As ValueTask(Of Boolean) Implements IAsyncEnumerator(Of ChatResponseUpdate).MoveNextAsync
                Return New ValueTask(Of Boolean)(MoveNextCoreAsync())
            End Function

            ' Async Function As Task, because VB's Async cannot return a ValueTask directly.
            Private Async Function MoveNextCoreAsync() As Task(Of Boolean)
                If Await _inner.MoveNextAsync() Then Return True
                Await _owner.RecordAsync(_options) ' only reached when the reply finished: a stopped one has no numbers worth keeping
                Return False
            End Function

            Public Function DisposeAsync() As ValueTask Implements IAsyncDisposable.DisposeAsync
                Return _inner.DisposeAsync()
            End Function
        End Class

    End Class

End Namespace
