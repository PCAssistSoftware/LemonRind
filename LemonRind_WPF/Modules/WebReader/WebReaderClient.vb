Imports System.Threading
Imports HtmlAgilityPack

Namespace Modules.WebReader

    Public Class WebPageContent
        Public Property Title As String
        Public Property Text As String
    End Class

    ''' <summary>
    ''' Fetches a page (via SsrfSafeHttpFetcher) and extracts its readable
    ''' text with HtmlAgilityPack, as one flattened text block rather than
    ''' separate headers/paragraphs/links arrays - worth revisiting if a
    ''' future need (e.g. wanting just the links) shows up, not built ahead
    ''' of it.
    ''' </summary>
    Public Class WebReaderClient

        Private Const MaxTextLength = 8000 ' keeps one fetched page from dominating a single chat turn's context window.

        Private ReadOnly _fetcher As SsrfSafeHttpFetcher

        Public Sub New(fetcher As SsrfSafeHttpFetcher)
            _fetcher = fetcher
        End Sub

        ''' <summary>
        ''' maxLength defaults to MaxTextLength (right for folding one page
        ''' into one chat turn); KnowledgeIngestionService passes a much
        ''' larger value, since RAG ingestion chunks a page's full text
        ''' rather than needing it to fit in a single turn.
        ''' </summary>
        Public Async Function ReadAsync(url As String, cancellationToken As CancellationToken, Optional maxLength As Integer = MaxTextLength) As Task(Of WebPageContent)
            Dim html = Await _fetcher.FetchAsync(url, cancellationToken)

            Dim doc As New HtmlDocument()
            doc.LoadHtml(html)

            ' Strip elements that are never "content" - scripts/styles would
            ' otherwise leak raw JS/CSS into the extracted text, and nav/footer
            ' are boilerplate that just wastes tokens without informing the
            ' model about the page's actual subject.
            For Each tagName In {"script", "style", "noscript", "nav", "footer", "header"}
                Dim nodes = doc.DocumentNode.SelectNodes($"//{tagName}")
                If nodes IsNot Nothing Then
                    For Each node In nodes
                        node.Remove()
                    Next
                End If
            Next

            Dim title = doc.DocumentNode.SelectSingleNode("//title")?.InnerText.Trim()
            Dim bodyText = doc.DocumentNode.SelectSingleNode("//body")?.InnerText

            Dim cleanedText = CleanWhitespace(HtmlEntity.DeEntitize(bodyText))
            If cleanedText.Length > maxLength Then
                cleanedText = cleanedText.Substring(0, maxLength) & "... [truncated]"
            End If

            Return New WebPageContent With {
                .Title = If(String.IsNullOrWhiteSpace(title), "(untitled)", title),
                .Text = cleanedText
            }
        End Function

        Private Shared Function CleanWhitespace(text As String) As String
            If String.IsNullOrEmpty(text) Then Return ""
            ' Collapses the runs of blank lines/spaces HTML text extraction
            ' typically leaves behind, without a full-blown regex dependency.
            Dim lines = text.Split({Environment.NewLine, vbLf}, StringSplitOptions.None).
                Select(Function(l) l.Trim()).
                Where(Function(l) l.Length > 0)
            Return String.Join(Environment.NewLine, lines)
        End Function

    End Class

End Namespace
