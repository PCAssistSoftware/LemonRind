Imports System.Net.WebSockets
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Serialization
Imports System.Threading

Namespace Services

    Public Class LemonadeLogEntry
        <JsonPropertyName("seq")>
        Public Property Seq As Long

        <JsonPropertyName("timestamp")>
        Public Property Timestamp As String

        <JsonPropertyName("severity")>
        Public Property Severity As String

        <JsonPropertyName("tag")>
        Public Property Tag As String

        <JsonPropertyName("line")>
        Public Property Line As String
    End Class

    Friend Class LemonadeLogSnapshotMessage
        <JsonPropertyName("type")>
        Public Property Type As String
        <JsonPropertyName("entries")>
        Public Property Entries As List(Of LemonadeLogEntry)
    End Class

    Friend Class LemonadeLogEntryMessage
        <JsonPropertyName("type")>
        Public Property Type As String
        <JsonPropertyName("entry")>
        Public Property Entry As LemonadeLogEntry
    End Class

    ''' <summary>
    ''' Lemonade's real-time log streaming WebSocket API (documented
    ''' `WS /logs/stream`). Severity values are "Info"/"Warn"/"Error", and
    ''' the snapshot message arrives split across multiple WebSocket frames,
    ''' needing accumulation until EndOfMessage rather than a single read.
    ''' </summary>
    Public Class LemonadeLogClient

        Private ReadOnly _managementClient As LemonadeManagementClient
        Private ReadOnly _baseUrl As Uri
        Private ReadOnly _apiKey As String

        Public Sub New(settings As Configuration.AppSettings, managementClient As LemonadeManagementClient)
            _managementClient = managementClient
            _baseUrl = New Uri(settings.Lemonade.BaseUrl)
            _apiKey = settings.Lemonade.ApiKey
        End Sub

        ''' <summary>
        ''' Connects, subscribes, and streams log data until cancelled or
        ''' the connection drops. onSnapshot fires once, with every entry
        ''' from the initial snapshot (oldest to newest) - kept separate
        ''' from onEntry (one live entry at a time) specifically so the
        ''' caller can add the whole snapshot to its UI collection in one
        ''' batch rather than one Dispatcher round-trip per entry, which
        ''' would be genuinely slow for a snapshot that can run into the
        ''' thousands of entries (even a server only up a few hours can
        ''' accumulate hundreds). Callers own the CancellationTokenSource's lifetime
        ''' (see LogViewerWindow, which cancels it when the window closes).
        ''' </summary>
        Public Async Function StreamAsync(onSnapshot As Action(Of List(Of LemonadeLogEntry)), onEntry As Action(Of LemonadeLogEntry), cancellationToken As CancellationToken) As Task
            Dim health = Await _managementClient.GetHealthAsync(cancellationToken)
            Dim wsUri As New UriBuilder(_baseUrl) With {
                .Scheme = If(_baseUrl.Scheme = "https", "wss", "ws"),
                .Port = health.WebsocketPort,
                .Path = "/logs/stream"
            }

            Using ws As New ClientWebSocket()
                ' Same optional credential as LemonadeManagementClient's REST
                ' calls - the WebSocket handshake is a real HTTP request too.
                If Not String.IsNullOrWhiteSpace(_apiKey) Then
                    ws.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}")
                End If
                Await ws.ConnectAsync(wsUri.Uri, cancellationToken)

                Dim subscribeJson = JsonSerializer.Serialize(New With {.type = "logs.subscribe", .after_seq = CType(Nothing, Long?)})
                Dim subscribeBytes = Encoding.UTF8.GetBytes(subscribeJson)
                Await ws.SendAsync(New ArraySegment(Of Byte)(subscribeBytes), WebSocketMessageType.Text, endOfMessage:=True, cancellationToken)

                While ws.State = WebSocketState.Open AndAlso Not cancellationToken.IsCancellationRequested
                    Dim messageText = Await ReceiveFullMessageAsync(ws, cancellationToken)
                    If messageText Is Nothing Then Exit While ' server closed the connection

                    ' Peek at "type" via a lightweight parse rather than
                    ' guessing which DTO to deserialize into first - the two
                    ' message shapes are different enough (entries array vs.
                    ' a single nested entry) that trying one then falling
                    ' back to the other would silently misparse either kind.
                    Using doc = JsonDocument.Parse(messageText)
                        Dim messageType = doc.RootElement.GetProperty("type").GetString()
                        Select Case messageType
                            Case "logs.snapshot"
                                Dim snapshot = JsonSerializer.Deserialize(Of LemonadeLogSnapshotMessage)(messageText)
                                onSnapshot(snapshot.Entries)
                            Case "logs.entry"
                                Dim entryMessage = JsonSerializer.Deserialize(Of LemonadeLogEntryMessage)(messageText)
                                onEntry(entryMessage.Entry)
                        End Select
                    End Using
                End While
            End Using
        End Function

        ''' <summary>Loops ReceiveAsync and accumulates until EndOfMessage - a large snapshot message can arrive split across multiple frames, so a single ReceiveAsync call isn't enough. Nothing if the server closed the connection.</summary>
        Private Shared Async Function ReceiveFullMessageAsync(ws As ClientWebSocket, cancellationToken As CancellationToken) As Task(Of String)
            Dim buffer(65535) As Byte
            Using ms As New IO.MemoryStream()
                Dim result As WebSocketReceiveResult
                Do
                    result = Await ws.ReceiveAsync(New ArraySegment(Of Byte)(buffer), cancellationToken)
                    If result.MessageType = WebSocketMessageType.Close Then Return Nothing
                    ms.Write(buffer, 0, result.Count)
                Loop While Not result.EndOfMessage

                Return Encoding.UTF8.GetString(ms.ToArray())
            End Using
        End Function

    End Class

End Namespace
