Imports System.Net.Http
Imports System.Threading
Imports LemonRind.Configuration
Imports Firecrawl
Imports Firecrawl.Models

Namespace Modules.Firecrawl

    ''' <summary>
    ''' Wraps the official Firecrawl .NET SDK (firecrawl-sdk on NuGet) -
    ''' Crawl is a real async job (POST to start, poll to completion), and
    ''' the official SDK already solves that polling/pagination correctly
    ''' rather than this app reimplementing it by hand. A fresh
    ''' FirecrawlClient is constructed per call (cheap - it just wraps the
    ''' one shared HttpClient below) reading the API key live from settings
    ''' each time, rather than baking the key in once at DI-singleton
    ''' construction.
    ''' </summary>
    Public Class FirecrawlService

        Private ReadOnly _httpClient As HttpClient
        Private ReadOnly _webSearchSettings As WebSearchSettings

        Public Sub New(settings As AppSettings)
            _httpClient = New HttpClient()
            _webSearchSettings = settings.WebSearch
        End Sub

        Private Function CreateClient() As FirecrawlClient
            Return New FirecrawlClient(_webSearchSettings.FirecrawlApiKey, httpClient:=_httpClient)
        End Function

        Public Async Function SearchAsync(query As String, cancellationToken As CancellationToken) As Task(Of String)
            Dim options As New SearchOptions With {.Limit = 5}
            Dim result = Await CreateClient().SearchAsync(query, options, cancellationToken)

            If result.Web Is Nothing OrElse result.Web.Count = 0 Then Return "No results found."

            Dim lines = result.Web.Select(Function(hit) $"- {hit.Title}: {hit.Description} ({hit.Url})")
            Return String.Join(Environment.NewLine, lines)
        End Function

        Public Async Function ScrapeAsync(url As String, cancellationToken As CancellationToken) As Task(Of String)
            Dim document = Await CreateClient().ScrapeAsync(url, New ScrapeOptions(), cancellationToken)

            If String.IsNullOrWhiteSpace(document.Markdown) Then
                Throw New InvalidOperationException($"Firecrawl returned no readable content for '{url}'.")
            End If

            Return document.Markdown
        End Function

        ''' <summary>
        ''' maxPages deliberately caps CrawlOptions.Limit rather than
        ''' trusting the API's own raw default (10,000 per Firecrawl's own
        ''' docs) - an unbounded crawl on a real site would take a long time
        ''' and burn a lot of credits for one tool call. The 120s timeout /
        ''' 3s poll interval are generous for a bounded crawl this size, not
        ''' for an unbounded one - same "bounded timeout on every long-
        ''' running call" pattern already used for Scheduler jobs/memory
        ''' extraction elsewhere in this app.
        ''' focus, when given, is passed straight through as CrawlOptions.
        ''' Prompt - a natural-language field Firecrawl itself uses
        ''' server-side to generate includePaths/excludePaths. Without it, a
        ''' crawl tends to return mostly shallow nav/structure pages rather
        ''' than the deeper content actually relevant to the request, since
        ''' an unscoped crawl has no way to know which of a site's many
        ''' links matter.
        ''' </summary>
        Public Async Function CrawlAsync(url As String, maxPages As Integer, focus As String, cancellationToken As CancellationToken) As Task(Of String)
            Dim options As New CrawlOptions With {.Limit = Math.Max(1, Math.Min(maxPages, 30))}
            If Not String.IsNullOrWhiteSpace(focus) Then options.Prompt = focus
            Dim job = Await CreateClient().CrawlAsync(url, options, pollIntervalSec:=3, timeoutSec:=120, cancellationToken:=cancellationToken)

            If Not job.IsDone OrElse job.Data Is Nothing OrElse job.Data.Count = 0 Then
                Throw New InvalidOperationException($"Firecrawl crawl of '{url}' finished with no pages (status: {job.Status}).")
            End If

            Dim pages = job.Data.Select(
                Function(doc, index)
                    Dim sourceUrl As String = Nothing
                    If doc.Metadata IsNot Nothing AndAlso doc.Metadata.ContainsKey("sourceURL") Then
                        sourceUrl = doc.Metadata("sourceURL")?.ToString()
                    End If
                    Return $"--- {If(sourceUrl, $"page {index + 1}")} ---{Environment.NewLine}{doc.Markdown}"
                End Function)

            Return String.Join(Environment.NewLine & Environment.NewLine, pages)
        End Function

    End Class

End Namespace
