using System.Text;

namespace LemonRindAvalonia.Services;

/// <summary>
/// File text extraction for one-off chat attachment - PDF (PdfPig), DOCX
/// (DocumentFormat.OpenXml), XLSX (ClosedXML, rendered as a tab-separated
/// text table per sheet), plain .txt/.md. Images are not handled here -
/// they need a vision-capable model and are attached separately.
///
/// Ported from the VB.NET/WPF LemonRind app's Services\FileTextExtractor.vb.
/// ExtractTextUncapped is kept alongside the capped ExtractText even though
/// knowledge-base ingestion uses the uncapped path (chunking handles size
/// there instead of truncation).
/// </summary>
public static class FileTextExtractor
{
    public static readonly string[] SupportedExtensions = [".pdf", ".docx", ".xlsx", ".txt", ".md"];

    // A very large document turned into raw text could dwarf the model's
    // whole context window on its own - capped rather than silently
    // overwhelming the next request.
    private const int MaxExtractedCharacters = 50_000;

    public static bool IsSupported(string filePath) =>
        SupportedExtensions.Contains(Path.GetExtension(filePath).ToLowerInvariant());

    /// <summary>Extracts plain text from a supported file, capped to MaxExtractedCharacters for one-off chat attachment. Throws NotSupportedException for anything else - callers show that message directly, it's already user-facing.</summary>
    public static string ExtractText(string filePath)
    {
        var text = ExtractTextUncapped(filePath);

        if (text.Length > MaxExtractedCharacters)
        {
            return text[..MaxExtractedCharacters] + Environment.NewLine +
                $"[... truncated - the file is longer than the {MaxExtractedCharacters:N0}-character limit for a one-off attachment ...]";
        }
        return text;
    }

    /// <summary>The same extraction, without the one-off attachment's character cap - for RAG ingestion, where chunking (not truncation) is what handles a large document.</summary>
    public static string ExtractTextUncapped(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        var text = extension switch
        {
            ".pdf" => ExtractPdf(filePath),
            ".docx" => ExtractDocx(filePath),
            ".xlsx" => ExtractXlsx(filePath),
            ".txt" or ".md" => File.ReadAllText(filePath),
            _ => throw new NotSupportedException($"Unsupported file type '{Path.GetExtension(filePath)}' - supported: {string.Join(", ", SupportedExtensions)}"),
        };

        // A scanned/image-based PDF opens fine (PdfPig doesn't throw) but
        // genuinely has no embedded text layer at all to extract - PdfPig
        // has no OCR support. Without this check, the caller would silently
        // get an empty string back and the model would have no way to know
        // why, and would reasonably (but unhelpfully) go looking for the
        // file with its own tools instead of giving a direct answer.
        // Throwing here routes into AttachFile's own error handling (a
        // real, specific error the user actually sees).
        if (string.IsNullOrWhiteSpace(text))
        {
            var reason = extension == ".pdf"
                ? "no extractable text layer was found - it's likely a scanned or image-based PDF, which requires OCR that this app doesn't currently do"
                : "no extractable text was found in the file";
            throw new InvalidOperationException($"{Path.GetFileName(filePath)}: {reason}.");
        }

        return text;
    }

    private static string ExtractPdf(string filePath)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(filePath);
        var builder = new StringBuilder();
        foreach (var page in document.GetPages())
        {
            builder.AppendLine(page.Text);
        }
        return builder.ToString();
    }

    private static string ExtractDocx(string filePath)
    {
        using var document = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(filePath, isEditable: false);
        return document.MainDocumentPart!.Document!.Body!.InnerText;
    }

    /// <summary>Renders each sheet as a tab-separated text table - simple, and good enough for a model to read tabular data from.</summary>
    private static string ExtractXlsx(string filePath)
    {
        using var workbook = new ClosedXML.Excel.XLWorkbook(filePath);
        var builder = new StringBuilder();
        foreach (var worksheet in workbook.Worksheets)
        {
            builder.AppendLine($"# Sheet: {worksheet.Name}");

            var usedRange = worksheet.RangeUsed();
            if (usedRange is not null)
            {
                var rowsByNumber = usedRange.Cells().GroupBy(c => c.Address.RowNumber).OrderBy(g => g.Key);
                foreach (var row in rowsByNumber)
                {
                    var cellsInOrder = row.OrderBy(c => c.Address.ColumnNumber).Select(c => c.GetString());
                    builder.AppendLine(string.Join('\t', cellsInOrder));
                }
            }

            builder.AppendLine();
        }
        return builder.ToString();
    }
}
