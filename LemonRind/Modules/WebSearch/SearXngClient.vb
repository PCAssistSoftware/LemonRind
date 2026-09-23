Imports System.Net.Http
Imports System.Net.Http.Json
Imports System.Text.Json.Serialization
Imports System.Threading
Imports LemonRind.Configuration

Namespace Modules.WebSearch

    Public Class SearXngResult
        <JsonPropertyName("title")>
        Public Property Title As String

        <JsonPropertyName("url")>
        Public Property Url As String

        <JsonPropertyName("content")>
        Public Property Content As String
    End Class

    Friend Class SearXngResponse
        <JsonPropertyName("results")>
        Public Property Results As List(Of SearXngResult)

        ''' <summary>
        ''' Each entry is [engine_name, reason] (e.g. ["brave", "Suspended:
        ''' too many requests"]) - this is how SearXNG itself distinguishes
        ''' "the search backend is degraded" from "this specific query
        ''' genuinely has no matches". See WebSearchModule.SearchAsync for
        ''' why that distinction matters.
        ''' </summary>
        <JsonPropertyName("unresponsive_engines")>
        Public Property UnresponsiveEngines As List(Of List(Of String))
    End Class

    ''' <summary>Wraps a search's results together with which engines (if any) were unresponsive for it, so the caller can tell a real backend problem apart from a genuinely empty result.</summary>
    Public Class SearXngSearchResult
        Public Property Results As List(Of SearXngResult)
        Public Property UnresponsiveEngines As List(Of List(Of String))
    End Class

    ''' <summary>
    ''' Talks to the user's self-hosted SearXNG instance. Response shape
    ''' (results[].title/url/content) matches SearXNG's own JSON output.
    ''' </summary>
    Public Class SearXngClient

        Private ReadOnly _httpClient As HttpClient
        Private ReadOnly _webSearchSettings As WebSearchSettings

        ''' <summary>
        ''' No BaseAddress set on the HttpClient - SearXngBaseUrl is read
        ''' fresh from _webSearchSettings on every search instead, so
        ''' changing it in Settings takes effect on the very next search
        ''' with no restart.
        ''' </summary>
        Public Sub New(settings As AppSettings)
            _httpClient = New HttpClient()
            _webSearchSettings = settings.WebSearch
        End Sub

        ''' <summary>Top results for a query, capped at maxResults - a search tool returning everything SearXNG has would waste tokens on results the model won't use. Also surfaces which engines (if any) were unresponsive, so a genuine backend problem can be told apart from a query that legitimately has no matches. timeRange (SearXNG's own "day"/"week"/"month"/"year", or Nothing/empty for no filter) restricts results to a recent window.</summary>
        Public Async Function SearchAsync(query As String, maxResults As Integer, cancellationToken As CancellationToken, Optional timeRange As String = Nothing) As Task(Of SearXngSearchResult)
            Dim baseUri As New Uri(_webSearchSettings.SearXngBaseUrl)
            Dim timeRangeParam = If(String.IsNullOrWhiteSpace(timeRange), "", $"&time_range={Uri.EscapeDataString(timeRange)}")
            Dim url = New Uri(baseUri, $"search?q={Uri.EscapeDataString(query)}&format=json{timeRangeParam}")
            Dim response = Await _httpClient.GetFromJsonAsync(Of SearXngResponse)(url, cancellationToken)
            Return New SearXngSearchResult With {
                .Results = response.Results.Take(maxResults).ToList(),
                .UnresponsiveEngines = response.UnresponsiveEngines
            }
        End Function

    End Class

End Namespace
