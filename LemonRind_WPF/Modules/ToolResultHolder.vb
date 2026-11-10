Imports System.Text.Json
Imports System.Threading
Imports Microsoft.Extensions.AI
Imports LemonRind.Configuration

Namespace Modules

    ''' <summary>
    ''' Gives the model a long tool result one piece at a time, and keeps the
    ''' whole text so it can ask for the rest.
    '''
    ''' A long result goes into the model's limited context window, so only
    ''' the first piece (Settings > Modules > "Longest piece of a tool
    ''' result") is sent. The rest is not thrown away: the whole text is held
    ''' under a short id ("r1", "r2", ...), and the note at the end of the
    ''' piece tells the model how to read on with the built-in read_more
    ''' tool. Without this a big web page (or any long result) was simply cut
    ''' off and the model went round in circles asking for "the rest", or
    ''' rebuilt the page from other pages.
    '''
    ''' Every tool a module offers is wrapped (ModuleRegistry.GetEnabledTools),
    ''' so it works the same for web pages from any search service, MCP
    ''' servers and the file tools.
    ''' </summary>
    Public Class ToolResultHolder

        Public Const ReadMoreName As String = "read_more"

        ' How many cut-short results are kept for read_more (the newest).
        Private Const HeldResults As Integer = 20

        Private ReadOnly _settings As AppSettings
        Private ReadOnly _gate As New Object()
        Private ReadOnly _held As New Dictionary(Of String, String)()
        Private ReadOnly _order As New List(Of String)() ' oldest first
        Private _heldCount As Integer                   ' only ever grows, so an id is never reused
        Private _readMoreTool As AITool

        Public Sub New(settings As AppSettings)
            _settings = settings
        End Sub

        ''' <summary>The longest piece sent to the model in one go, kept inside a sensible range.</summary>
        Private ReadOnly Property MaxChars As Integer
            Get
                Return Math.Clamp(_settings.Modules.MaxToolOutputChars, 500, 1000000)
            End Get
        End Property

        ''' <summary>The built-in tool that reads on through a result that was cut short.</summary>
        Public ReadOnly Property ReadMoreTool As AITool
            Get
                If _readMoreTool Is Nothing Then
                    _readMoreTool = AIFunctionFactory.Create(
                        method:=Function(result As String, start As Integer) ReadMore(result, start),
                        name:=ReadMoreName,
                        description:="Read on through a tool result that was cut short. A result that is too long is shown only in part, " &
                            "and ends with a note giving the result id and the number to use here as start. " &
                            "result is the id from that note, for example r3. start is the character number to continue from, as given in that note.")
                End If
                Return _readMoreTool
            End Get
        End Property

        ''' <summary>Wraps a tool so that a long result is held and shortened; anything that is not a function is passed through.</summary>
        Public Function Wrap(tool As AITool) As AITool
            Dim [function] = TryCast(tool, AIFunction)
            If [function] IsNot Nothing AndAlso [function].Name <> ReadMoreName Then
                Return New HoldingFunction([function], Me)
            End If
            Return tool
        End Function

        ''' <summary>The result as the model will see it: unchanged if short enough, else the first piece with a note on how to read on.</summary>
        Friend Function Shorten(result As Object) As Object
            Dim text As String = Nothing
            If result Is Nothing Then
                Return result
            ElseIf TypeOf result Is String Then
                text = DirectCast(result, String)
            ElseIf TypeOf result Is JsonElement Then
                Dim element = DirectCast(result, JsonElement)
                text = If(element.ValueKind = JsonValueKind.String, element.GetString(), element.GetRawText())
            Else
                text = result.ToString()
            End If
            If text Is Nothing OrElse text.Length <= MaxChars Then Return result

            Dim id As String
            SyncLock _gate
                _heldCount += 1
                id = $"r{_heldCount}"
                _held(id) = text
                _order.Add(id)
                While _order.Count > HeldResults
                    _held.Remove(_order(0))
                    _order.RemoveAt(0)
                End While
            End SyncLock
            Return Piece(id, text, 0)
        End Function

        ''' <summary>MaxChars of the text from start, with a note on how to continue if there is more.</summary>
        Private Function Piece(id As String, content As String, start As Integer) As String
            Dim [end] = start + MaxChars
            If [end] < content.Length Then [end] = TidyEnd(content, start, [end])
            Dim piece1 = content.Substring(start, Math.Min([end], content.Length) - start)
            If [end] >= content.Length Then
                Return piece1 & If(start > 0, vbLf & $"[End of result {id}.]", "")
            End If
            Return piece1 & vbLf & $"[... {content.Length - [end]:N0} more characters not shown. To read on, call {ReadMoreName} with result=""{id}"" and start={[end]}.]"
        End Function

        ''' <summary>
        ''' Moves a piece's end back to the end of a line (or at least a word),
        ''' so a piece never stops half way through a word and the next one
        ''' never starts in the middle of one. Only the last tenth of the piece
        ''' is searched, so a text with no breaks at all is simply cut at the
        ''' limit.
        ''' </summary>
        Private Shared Function TidyEnd(content As String, start As Integer, [end] As Integer) As Integer
            Dim earliest = [end] - Math.Max(1, ([end] - start) \ 10)
            For Each separator In {ControlChars.Lf, " "c}
                Dim found = content.LastIndexOf(separator, [end] - 1, [end] - earliest)
                If found >= earliest Then Return found + 1
            Next
            Return [end]
        End Function

        Private Function ReadMore(result As String, start As Integer) As String
            Dim id = If(result, "").Trim()
            Dim content As String = Nothing
            Dim kept As String
            SyncLock _gate
                _held.TryGetValue(id, content)
                kept = If(_order.Count > 0, String.Join(", ", _order), "none")
                If content IsNot Nothing Then
                    _order.Remove(id) ' still in use: keep it longer
                    _order.Add(id)
                End If
            End SyncLock
            If content Is Nothing Then
                Return $"There is nothing saved as '{result}'. Only the last {HeldResults} long results are kept (now: {kept}); run the tool again to get a fresh copy."
            End If
            If start < 0 OrElse start >= content.Length Then
                Return $"start must be between 0 and {content.Length - 1:N0} for result {id}."
            End If
            Return Piece(id, content, start)
        End Function

        ''' <summary>A tool whose result goes through the holder.</summary>
        Private NotInheritable Class HoldingFunction
            Inherits DelegatingAIFunction

            Private ReadOnly _holder As ToolResultHolder

            Public Sub New(inner As AIFunction, holder As ToolResultHolder)
                MyBase.New(inner)
                _holder = holder
            End Sub

            Protected Overrides Function InvokeCoreAsync(arguments As AIFunctionArguments, cancellationToken As CancellationToken) As ValueTask(Of Object)
                Return New ValueTask(Of Object)(InvokeAndShortenAsync(arguments, cancellationToken))
            End Function

            ' Async Function As Task, because VB's Async cannot return a ValueTask directly.
            Private Async Function InvokeAndShortenAsync(arguments As AIFunctionArguments, cancellationToken As CancellationToken) As Task(Of Object)
                Dim result As Object
                Try
                    result = Await MyBase.InvokeCoreAsync(arguments, cancellationToken)
                Catch ex As Exception When Not cancellationToken.IsCancellationRequested
                    Services.TurnUsage.Current?.AddToolCall(Name, True) ' counted on the usage screen
                    Throw
                End Try
                Services.TurnUsage.Current?.AddToolCall(Name, False)
                Return _holder.Shorten(result)
            End Function
        End Class

    End Class

End Namespace
