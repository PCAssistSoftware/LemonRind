Imports Microsoft.CodeAnalysis
Imports Microsoft.CodeAnalysis.CSharp
Imports Microsoft.CodeAnalysis.VisualBasic

Namespace Modules.Coder

    ''' <summary>
    ''' Syntax-only validation (no project references, so no semantic/binding
    ''' checks - just "does this parse") before CoderModule ever writes a
    ''' .cs/.vb file to disk, so a bad edit can't silently save syntactically
    ''' broken code. Uses Roslyn for real compiler diagnostics, not just
    ''' parse/no-parse. Other file types are intentionally not validated -
    ''' a deliberate scope decision, not an oversight.
    ''' </summary>
    Public Module CodeSyntaxValidator

        ''' <summary>Nothing if valid or the file type isn't a supported language (skipped, not an error) - otherwise a formatted list of syntax errors.</summary>
        Public Function Validate(filePath As String, content As String) As String
            Select Case IO.Path.GetExtension(filePath).ToLowerInvariant()
                Case ".cs"
                    Return FormatDiagnostics(CSharpSyntaxTree.ParseText(content).GetDiagnostics())
                Case ".vb"
                    Return FormatDiagnostics(VisualBasicSyntaxTree.ParseText(content).GetDiagnostics())
                Case Else
                    Return Nothing
            End Select
        End Function

        Private Function FormatDiagnostics(diagnostics As IEnumerable(Of Diagnostic)) As String
            Dim errors = diagnostics.Where(Function(d) d.Severity = DiagnosticSeverity.Error).Take(20).ToList()
            If errors.Count = 0 Then Return Nothing

            Dim lines = errors.Select(
                Function(d)
                    Dim pos = d.Location.GetLineSpan().StartLinePosition
                    Return $"Line {pos.Line + 1}, Col {pos.Character + 1}: {d.GetMessage()}"
                End Function)
            Return String.Join(Environment.NewLine, lines)
        End Function

    End Module

End Namespace
