Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Net.Http.Json
Imports System.Text.Json.Serialization
Imports System.Threading
Imports LemonRind.Configuration

Namespace Modules.Tavily

    Public Class TavilySearchResultItem
        <JsonPropertyName("title")>
        Public Property Title As String
        <JsonPropertyName("url")>
        Public Property Url As String
        <JsonPropertyName("content")>
        Public Property Content As String
    End Class

    Friend Class TavilySearchResponse
        <JsonPropertyName("answer")>
        Public Property Answer As String
        <JsonPropertyName("results")>
        Public Property Results As List(Of TavilySearchResultItem)
    End Class

    Friend Class TavilyExtractResultItem
        <JsonPropertyName("url")>
        Public Property Url As String
        <JsonPropertyName("raw_content")>
        Public Property RawContent As String
    End Class

    Friend Class TavilyExtractFailure
        <JsonPropertyName("url")>
        Public Property Url As String
        ''' <summary>Named ErrorMessage, not Error - "Error" is a reserved VB keyword (the legacy VB6-era Error statement) and can't be used as an identifier.</summary>
        <JsonPropertyName("error")>
        Public Property ErrorMessage As String
    End Class

    Friend Class TavilyExtractResponse
        <JsonPropertyName("results")>
        Public Property Results As List(Of TavilyExtractResultItem)
        <JsonPropertyName("failed_results")>
        Public Property FailedResults As List(Of TavilyExtractFailure)
    End Class

    ''' <summary>
    ''' Talks to Tavily's Search/Extract APIs. Both endpoints are POST with
    ''' a JSON body and a Bearer token, unlike SearXNG/Jina's GET+query-string
    ''' shape.
    ''' </summary>
    Public Class TavilyClient

        Private Const BaseUrl = "https://api.tavily.com"

        Private ReadOnly _httpClient As HttpClient
        Private ReadOnly _webSearchSettings As WebSearchSettings

        Public Sub New(settings As AppSettings)
            _httpClient = New HttpClient()
            _webSearchSettings = settings.WebSearch
        End Sub

        ''' <summary>
        ''' Tavily's response can include a synthesized "answer" alongside
        ''' the raw results. Included first when present since it's the most
        ''' directly useful part for the model to read.
        ''' </summary>
        Public Async Function SearchAsync(query As String, cancellationToken As CancellationToken) As Task(Of String)
            Dim requestBody = New With {.query = query, .max_results = 5}
            Dim response = Await SendAsync("/search", requestBody, cancellationToken)
            Dim result = Await response.Content.ReadFromJsonAsync(Of TavilySearchResponse)(cancellationToken:=cancellationToken)

            Dim sections As New List(Of String)
            If Not String.IsNullOrWhiteSpace(result.Answer) Then sections.Add($"Answer: {result.Answer}")
            If result.Results IsNot Nothing AndAlso result.Results.Count > 0 Then
                Dim lines = result.Results.Select(Function(r) $"- {r.Title}: {r.Content} ({r.Url})")
                sections.Add(String.Join(Environment.NewLine, lines))
            End If

            Return If(sections.Count > 0, String.Join(Environment.NewLine & Environment.NewLine, sections), "No results found.")
        End Function

        ''' <summary>
        ''' A per-URL failure can appear in failed_results even though the
        ''' overall HTTP response is a plain 200 - checking only the status
        ''' code would miss this and silently show ✓ for a page that was
        ''' never actually read.
        ''' </summary>
        Public Async Function ExtractAsync(url As String, cancellationToken As CancellationToken) As Task(Of String)
            Dim requestBody = New With {.urls = url}
            Dim response = Await SendAsync("/extract", requestBody, cancellationToken)
            Dim result = Await response.Content.ReadFromJsonAsync(Of TavilyExtractResponse)(cancellationToken:=cancellationToken)

            Dim failure = result.FailedResults?.FirstOrDefault()
            If failure IsNot Nothing Then
                Throw New InvalidOperationException($"Tavily couldn't extract '{failure.Url}': {failure.ErrorMessage}")
            End If

            Dim page = result.Results?.FirstOrDefault()
            If page Is Nothing OrElse String.IsNullOrWhiteSpace(page.RawContent) Then
                Throw New InvalidOperationException($"Tavily returned no content for '{url}'.")
            End If

            Return page.RawContent
        End Function

        Private Async Function SendAsync(path As String, body As Object, cancellationToken As CancellationToken) As Task(Of HttpResponseMessage)
            Dim request As New HttpRequestMessage(HttpMethod.Post, BaseUrl & path) With {
                .Content = JsonContent.Create(body)
            }
            request.Headers.Authorization = New AuthenticationHeaderValue("Bearer", _webSearchSettings.TavilyApiKey)

            Dim response = Await _httpClient.SendAsync(request, cancellationToken)
            If Not response.IsSuccessStatusCode Then
                Dim errorBody = Await response.Content.ReadAsStringAsync(cancellationToken)
                Throw New InvalidOperationException($"Tavily returned {CInt(response.StatusCode)}: {errorBody}")
            End If

            Return response
        End Function

    End Class

End Namespace
