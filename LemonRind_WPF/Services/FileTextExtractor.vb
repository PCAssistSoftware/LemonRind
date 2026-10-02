Imports System.IO
Imports System.Text

Namespace Services

    ''' <summary>
    ''' File text extraction - shared by two callers with different needs: one-off chat
    ''' attachment (ExtractText, capped so one huge file can't dwarf a single
    ''' turn's context) and RAG knowledge-base ingestion
    ''' (ExtractTextUncapped, via KnowledgeIngestionService - chunking is
    ''' what keeps a large document manageable there, not a hard character cap).
    ''' Images are not handled here - they need a vision-capable model
    ''' rather than text extraction, and aren't supported by this app's
    ''' currently configured model; deferred rather than half-built.
    ''' </summary>
    Public Module FileTextExtractor

        Public ReadOnly SupportedExtensions As String() = {".pdf", ".docx", ".xlsx", ".txt", ".md"}

        ' A very large document turned into raw text could dwarf the model's
        ' whole context window on its own - capped rather than silently
        ' overwhelming the next request (same "protect the context window"
        ' concern behind the short-term compaction trigger). Only applies to
        ' the one-off chat-attachment path - see ExtractTextUncapped.
        Private Const MaxExtractedCharacters = 50000

        Public Function IsSupported(filePath As String) As Boolean
            Return SupportedExtensions.Contains(Path.GetExtension(filePath).ToLowerInvariant())
        End Function

        ''' <summary>Extracts plain text from a supported file, capped to MaxExtractedCharacters for one-off chat attachment. Throws NotSupportedException for anything else - callers show that message directly, it's already user-facing.</summary>
        Public Function ExtractText(filePath As String) As String
            Dim text = ExtractTextUncapped(filePath)

            If text.Length > MaxExtractedCharacters Then
                Return text.Substring(0, MaxExtractedCharacters) & Environment.NewLine &
                    $"[... truncated - the file is longer than the {MaxExtractedCharacters:N0}-character limit for a one-off attachment ...]"
            End If
            Return text
        End Function

        ''' <summary>The same extraction, without the one-off attachment's character cap - for RAG ingestion, where chunking (not truncation) is what handles a large document.</summary>
        Public Function ExtractTextUncapped(filePath As String) As String
            Dim extension = Path.GetExtension(filePath).ToLowerInvariant()
            Dim text As String
            Select Case extension
                Case ".pdf"
                    text = ExtractPdf(filePath)
                Case ".docx"
                    text = ExtractDocx(filePath)
                Case ".xlsx"
                    text = ExtractXlsx(filePath)
                Case ".txt", ".md"
                    text = File.ReadAllText(filePath)
                Case Else
                    Throw New NotSupportedException($"Unsupported file type '{Path.GetExtension(filePath)}' - supported: {String.Join(", ", SupportedExtensions)}")
            End Select

            ' A scanned/image-based PDF opens fine (PdfPig doesn't throw) but
            ' genuinely has no embedded text layer at all to extract - PdfPig
            ' has no OCR support, in any version. Without this check, the
            ' caller would silently get an empty string back, and (for the
            ' chat-attachment path) that empty text would still flow into
            ' the model's context as "[Attached file: X.pdf]" with nothing
            ' after it - the model would have no way to know WHY there was
            ' nothing there, and would reasonably (but unhelpfully) go
            ' looking for the file itself with its own tools instead of
            ' giving a direct, correct answer. Throwing here routes into the
            ' same error handling both callers already have (AttachFile's
            ' Catch shows a chat-visible error immediately, before the
            ' message is even sent; KnowledgeIngestionService's own Catch
            ' does the same for RAG ingestion) - a real, specific error the
            ' user actually sees, instead of a confusing/incorrect answer
            ' from the model.
            If String.IsNullOrWhiteSpace(text) Then
                Dim reason = If(extension = ".pdf",
                    "no extractable text layer was found - it's likely a scanned or image-based PDF, which requires OCR that this app doesn't currently do",
                    "no extractable text was found in the file")
                Throw New InvalidOperationException($"{Path.GetFileName(filePath)}: {reason}.")
            End If

            Return text
        End Function

        Private Function ExtractPdf(filePath As String) As String
            Using document = UglyToad.PdfPig.PdfDocument.Open(filePath)
                Dim builder As New StringBuilder()
                For Each page In document.GetPages()
                    builder.AppendLine(page.Text)
                Next
                Return builder.ToString()
            End Using
        End Function

        Private Function ExtractDocx(filePath As String) As String
            Using document = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(filePath, isEditable:=False)
                Return document.MainDocumentPart.Document.Body.InnerText
            End Using
        End Function

        ''' <summary>Renders each sheet as a tab-separated text table - simple, and good enough for a model to read tabular data from.</summary>
        Private Function ExtractXlsx(filePath As String) As String
            Using workbook As New ClosedXML.Excel.XLWorkbook(filePath)
                Dim builder As New StringBuilder()
                For Each worksheet In workbook.Worksheets
                    builder.AppendLine($"# Sheet: {worksheet.Name}")

                    Dim usedRange = worksheet.RangeUsed()
                    If usedRange IsNot Nothing Then
                        Dim rowsByNumber = usedRange.Cells().GroupBy(Function(c) c.Address.RowNumber).OrderBy(Function(g) g.Key)
                        For Each row In rowsByNumber
                            Dim cellsInOrder = row.OrderBy(Function(c) c.Address.ColumnNumber).Select(Function(c) c.GetString())
                            builder.AppendLine(String.Join(vbTab, cellsInOrder))
                        Next
                    End If

                    builder.AppendLine()
                Next
                Return builder.ToString()
            End Using
        End Function

    End Module

End Namespace
