Imports System.Text.RegularExpressions
Imports Microsoft.CodeAnalysis
Imports Microsoft.CodeAnalysis.CSharp
Imports CSharpSyntax = Microsoft.CodeAnalysis.CSharp.Syntax
Imports Microsoft.CodeAnalysis.VisualBasic
Imports VBSyntax = Microsoft.CodeAnalysis.VisualBasic.Syntax

Namespace Modules.Coder

    ''' <summary>
    ''' Structural summary (class/method/property signatures, no bodies) for
    ''' C#/VB.NET files, using Roslyn since C#/VB.NET are this app's own
    ''' actual target languages. A general multi-language outline
    ''' (tree-sitter, needing native per-platform binaries) is deliberately
    ''' out of scope.
    ''' </summary>
    Public Module CodeOutlineGenerator

        ''' <summary>Nothing if this file extension isn't a supported language - caller substitutes its own "not available" message.</summary>
        Public Function TryGenerate(filePath As String, content As String) As String
            Select Case IO.Path.GetExtension(filePath).ToLowerInvariant()
                Case ".cs"
                    Return GenerateCSharpOutline(content)
                Case ".vb"
                    Return GenerateVisualBasicOutline(content)
                Case Else
                    Return Nothing
            End Select
        End Function

        Private Function NormalizeSignature(text As String) As String
            Return Regex.Replace(text, "\s+", " ").Trim()
        End Function

        Private Function GenerateCSharpOutline(content As String) As String
            Dim root = CSharpSyntaxTree.ParseText(content).GetRoot()
            Dim walker As New CSharpOutlineWalker()
            walker.Visit(root)
            Return If(walker.Lines.Count = 0, "(no classes/methods found)", String.Join(Environment.NewLine, walker.Lines))
        End Function

        Private Function GenerateVisualBasicOutline(content As String) As String
            Dim root = VisualBasicSyntaxTree.ParseText(content).GetRoot()
            Dim walker As New VisualBasicOutlineWalker()
            walker.Visit(root)
            Return If(walker.Lines.Count = 0, "(no classes/methods found)", String.Join(Environment.NewLine, walker.Lines))
        End Function

        ''' <summary>
        ''' Overrides each container type (namespace/class/interface/struct)
        ''' to print its header then explicitly continue descending via
        ''' DefaultVisit, and each member (method/property/constructor) to
        ''' print only its signature and deliberately NOT descend - that's
        ''' what keeps method/property bodies out of the output, not a
        ''' filter applied after the fact.
        ''' </summary>
        Private Class CSharpOutlineWalker
            Inherits CSharpSyntaxWalker

            Public ReadOnly Lines As New List(Of String)
            Private _depth As Integer = 0

            Private Sub AppendLine(text As String)
                Lines.Add(New String(" "c, _depth * 2) & NormalizeSignature(text))
            End Sub

            Private Sub VisitContainer(node As SyntaxNode, header As String)
                AppendLine(header)
                _depth += 1
                MyBase.DefaultVisit(node)
                _depth -= 1
            End Sub

            Public Overrides Sub VisitNamespaceDeclaration(node As CSharpSyntax.NamespaceDeclarationSyntax)
                VisitContainer(node, $"namespace {node.Name}")
            End Sub

            Public Overrides Sub VisitFileScopedNamespaceDeclaration(node As CSharpSyntax.FileScopedNamespaceDeclarationSyntax)
                VisitContainer(node, $"namespace {node.Name}")
            End Sub

            Public Overrides Sub VisitClassDeclaration(node As CSharpSyntax.ClassDeclarationSyntax)
                VisitContainer(node, TypeHeader(node))
            End Sub

            Public Overrides Sub VisitInterfaceDeclaration(node As CSharpSyntax.InterfaceDeclarationSyntax)
                VisitContainer(node, TypeHeader(node))
            End Sub

            Public Overrides Sub VisitStructDeclaration(node As CSharpSyntax.StructDeclarationSyntax)
                VisitContainer(node, TypeHeader(node))
            End Sub

            Public Overrides Sub VisitRecordDeclaration(node As CSharpSyntax.RecordDeclarationSyntax)
                VisitContainer(node, TypeHeader(node))
            End Sub

            Private Function TypeHeader(node As CSharpSyntax.TypeDeclarationSyntax) As String
                Dim modifiers = String.Join(" ", node.Modifiers.Select(Function(m) m.Text))
                Dim typeParams = If(node.TypeParameterList IsNot Nothing, node.TypeParameterList.ToString(), "")
                Dim baseList = If(node.BaseList IsNot Nothing, " " & node.BaseList.ToString(), "")
                Return $"{modifiers} {node.Keyword.Text} {node.Identifier.Text}{typeParams}{baseList}"
            End Function

            Public Overrides Sub VisitEnumDeclaration(node As CSharpSyntax.EnumDeclarationSyntax)
                AppendLine($"enum {node.Identifier.Text}")
            End Sub

            Public Overrides Sub VisitMethodDeclaration(node As CSharpSyntax.MethodDeclarationSyntax)
                AppendLine(HeaderOnly(node, If(CType(node.Body, SyntaxNode), node.ExpressionBody)))
            End Sub

            Public Overrides Sub VisitConstructorDeclaration(node As CSharpSyntax.ConstructorDeclarationSyntax)
                AppendLine(HeaderOnly(node, If(CType(node.Body, SyntaxNode), node.ExpressionBody)))
            End Sub

            Public Overrides Sub VisitPropertyDeclaration(node As CSharpSyntax.PropertyDeclarationSyntax)
                AppendLine(node.ToString())
            End Sub

            ''' <summary>Cuts a method/constructor's text off at the start of its body/expression-body, so only the signature remains - the whole point of an "outline".</summary>
            Private Function HeaderOnly(node As SyntaxNode, bodyOrExpr As SyntaxNode) As String
                Dim fullText = node.ToString()
                If bodyOrExpr Is Nothing Then Return fullText.TrimEnd(";"c)
                Dim relativeEnd = Math.Max(0, Math.Min(fullText.Length, bodyOrExpr.SpanStart - node.SpanStart))
                Return fullText.Substring(0, relativeEnd)
            End Function

        End Class

        Private Class VisualBasicOutlineWalker
            Inherits VisualBasicSyntaxWalker

            Public ReadOnly Lines As New List(Of String)
            Private _depth As Integer = 0

            Private Sub AppendLine(text As String)
                Lines.Add(New String(" "c, _depth * 2) & NormalizeSignature(text))
            End Sub

            Private Sub VisitContainer(node As SyntaxNode, header As String)
                AppendLine(header)
                _depth += 1
                MyBase.DefaultVisit(node)
                _depth -= 1
            End Sub

            Public Overrides Sub VisitNamespaceBlock(node As VBSyntax.NamespaceBlockSyntax)
                VisitContainer(node, node.NamespaceStatement.ToString())
            End Sub

            Public Overrides Sub VisitModuleBlock(node As VBSyntax.ModuleBlockSyntax)
                VisitContainer(node, node.ModuleStatement.ToString())
            End Sub

            Public Overrides Sub VisitClassBlock(node As VBSyntax.ClassBlockSyntax)
                VisitContainer(node, node.ClassStatement.ToString())
            End Sub

            Public Overrides Sub VisitInterfaceBlock(node As VBSyntax.InterfaceBlockSyntax)
                VisitContainer(node, node.InterfaceStatement.ToString())
            End Sub

            Public Overrides Sub VisitStructureBlock(node As VBSyntax.StructureBlockSyntax)
                VisitContainer(node, node.StructureStatement.ToString())
            End Sub

            Public Overrides Sub VisitEnumBlock(node As VBSyntax.EnumBlockSyntax)
                AppendLine(node.EnumStatement.ToString())
            End Sub

            Public Overrides Sub VisitMethodBlock(node As VBSyntax.MethodBlockSyntax)
                AppendLine(node.SubOrFunctionStatement.ToString())
            End Sub

            ''' <summary>Interface method signatures have no block/body - they appear directly as members, never inside a MethodBlockSyntax.</summary>
            Public Overrides Sub VisitMethodStatement(node As VBSyntax.MethodStatementSyntax)
                AppendLine(node.ToString())
            End Sub

            Public Overrides Sub VisitConstructorBlock(node As VBSyntax.ConstructorBlockSyntax)
                AppendLine(node.SubNewStatement.ToString())
            End Sub

            Public Overrides Sub VisitPropertyBlock(node As VBSyntax.PropertyBlockSyntax)
                AppendLine(node.PropertyStatement.ToString())
            End Sub

            ''' <summary>Auto-properties (and interface property signatures) have no Get/Set block - standalone members, never inside a PropertyBlockSyntax.</summary>
            Public Overrides Sub VisitPropertyStatement(node As VBSyntax.PropertyStatementSyntax)
                AppendLine(node.ToString())
            End Sub

        End Class

    End Module

End Namespace
