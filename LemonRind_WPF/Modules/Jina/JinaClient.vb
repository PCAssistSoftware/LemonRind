Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Threading
Imports LemonRind.Configuration

Namespace Modules.Jina

    ''' <summary>
    ''' Talks to Jina AI's Reader (r.jina.ai) and Search (s.jina.ai) APIs.
    ''' Reader returns clean plain text (Title/URL Source/Markdown Content)
    ''' and works without an API key at a lower rate; Search requires one
    ''' (returns 401 without one).
    ''' </summary>
    Public Class JinaClient

        Private ReadOnly _httpClient As HttpClient
        Private ReadOnly _webSearchSettings As WebSearchSettings

        Public Sub New(settings As AppSettings)
            _httpClient = New HttpClient()
            _webSearchSettings = settings.WebSearch
        End Sub

        Public Async Function ReadPageAsync(url As String, cancellationToken As CancellationToken) As Task(Of String)
            Return Await SendAsync($"https://r.jina.ai/{url}", cancellationToken)
        End Function

        Public Async Function SearchAsync(query As String, cancellationToken As CancellationToken) As Task(Of String)
            Return Await SendAsync($"https://s.jina.ai/?q={Uri.EscapeDataString(query)}", cancellationToken)
        End Function

        ''' <summary>
        ''' Reads the response body before checking success, and includes it
        ''' in the thrown message rather than a bare status code - Jina's
        ''' own JSON error responses (e.g. the 401 for Search with no key)
        ''' already contain a clear, human-readable explanation, which is
        ''' exactly the kind of message worth reaching the model directly
        ''' (see LemonadeChatClientFactory's IncludeDetailedErrors setting).
        ''' </summary>
        Private Async Function SendAsync(url As String, cancellationToken As CancellationToken) As Task(Of String)
            Dim request As New HttpRequestMessage(HttpMethod.Get, url)
            If Not String.IsNullOrWhiteSpace(_webSearchSettings.JinaApiKey) Then
                request.Headers.Authorization = New AuthenticationHeaderValue("Bearer", _webSearchSettings.JinaApiKey)
            End If

            Dim response = Await _httpClient.SendAsync(request, cancellationToken)
            Dim body = Await response.Content.ReadAsStringAsync(cancellationToken)

            If Not response.IsSuccessStatusCode Then
                Throw New InvalidOperationException($"Jina returned {CInt(response.StatusCode)}: {body}")
            End If

            Return body
        End Function

    End Class

End Namespace
