using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.VisualBasic;

namespace LemonRindAvalonia.Modules.Coder;

/// <summary>
/// Syntax-only validation (no project references, so no semantic/binding
/// checks - just "does this parse") before CoderModule ever writes a
/// .cs/.vb file to disk, so a bad edit can't silently save syntactically
/// broken code. Uses Roslyn for real compiler diagnostics, not just
/// parse/no-parse. Other file types are intentionally not validated - a
/// deliberate scope decision, not an oversight.
///
/// Ported directly from the VB.NET/WPF LemonRind app's
/// Modules\Coder\CodeSyntaxValidator.vb.
/// </summary>
public static class CodeSyntaxValidator
{
    /// <summary>Null if valid or the file type isn't a supported language (skipped, not an error) - otherwise a formatted list of syntax errors.</summary>
    public static string? Validate(string filePath, string content)
    {
        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".cs" => FormatDiagnostics(CSharpSyntaxTree.ParseText(content).GetDiagnostics()),
            ".vb" => FormatDiagnostics(VisualBasicSyntaxTree.ParseText(content).GetDiagnostics()),
            _ => null,
        };
    }

    private static string? FormatDiagnostics(IEnumerable<Diagnostic> diagnostics)
    {
        var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(20).ToList();
        if (errors.Count == 0) return null;

        var lines = errors.Select(d =>
        {
            var pos = d.Location.GetLineSpan().StartLinePosition;
            return $"Line {pos.Line + 1}, Col {pos.Character + 1}: {d.GetMessage()}";
        });
        return string.Join(Environment.NewLine, lines);
    }
}
