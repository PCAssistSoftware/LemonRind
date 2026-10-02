using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using CSharpSyntax = Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.VisualBasic;
using VBSyntax = Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace LemonRindBlazor.Modules.Coder;

/// <summary>
/// Structural summary (class/method/property signatures, no bodies) for
/// C#/VB.NET files, using Roslyn since C#/VB.NET are this app's own actual
/// target languages (VB.NET support is kept even though this app is
/// C#-only, since it's still useful for browsing the WPF edition's VB.NET
/// source). A general multi-language outline (tree-sitter,
/// needing native per-platform binaries) is deliberately out of scope.
///
/// Ported directly from the VB.NET/WPF LemonRind app's
/// Modules\Coder\CodeOutlineGenerator.vb.
/// </summary>
public static class CodeOutlineGenerator
{
    /// <summary>Null if this file extension isn't a supported language - caller substitutes its own "not available" message.</summary>
    public static string? TryGenerate(string filePath, string content)
    {
        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".cs" => GenerateCSharpOutline(content),
            ".vb" => GenerateVisualBasicOutline(content),
            _ => null,
        };
    }

    private static string NormalizeSignature(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    private static string GenerateCSharpOutline(string content)
    {
        var root = CSharpSyntaxTree.ParseText(content).GetRoot();
        var walker = new CSharpOutlineWalker();
        walker.Visit(root);
        return walker.Lines.Count == 0 ? "(no classes/methods found)" : string.Join(Environment.NewLine, walker.Lines);
    }

    private static string GenerateVisualBasicOutline(string content)
    {
        var root = VisualBasicSyntaxTree.ParseText(content).GetRoot();
        var walker = new VisualBasicOutlineWalker();
        walker.Visit(root);
        return walker.Lines.Count == 0 ? "(no classes/methods found)" : string.Join(Environment.NewLine, walker.Lines);
    }

    /// <summary>
    /// Overrides each container type (namespace/class/interface/struct) to
    /// print its header then explicitly continue descending via
    /// DefaultVisit, and each member (method/property/constructor) to
    /// print only its signature and deliberately NOT descend - that's what
    /// keeps method/property bodies out of the output, not a filter
    /// applied after the fact.
    /// </summary>
    private class CSharpOutlineWalker : CSharpSyntaxWalker
    {
        public readonly List<string> Lines = [];
        private int _depth;

        private void AppendLine(string text) => Lines.Add(new string(' ', _depth * 2) + NormalizeSignature(text));

        private void VisitContainer(SyntaxNode node, string header)
        {
            AppendLine(header);
            _depth++;
            DefaultVisit(node);
            _depth--;
        }

        public override void VisitNamespaceDeclaration(CSharpSyntax.NamespaceDeclarationSyntax node) => VisitContainer(node, $"namespace {node.Name}");
        public override void VisitFileScopedNamespaceDeclaration(CSharpSyntax.FileScopedNamespaceDeclarationSyntax node) => VisitContainer(node, $"namespace {node.Name}");
        public override void VisitClassDeclaration(CSharpSyntax.ClassDeclarationSyntax node) => VisitContainer(node, TypeHeader(node));
        public override void VisitInterfaceDeclaration(CSharpSyntax.InterfaceDeclarationSyntax node) => VisitContainer(node, TypeHeader(node));
        public override void VisitStructDeclaration(CSharpSyntax.StructDeclarationSyntax node) => VisitContainer(node, TypeHeader(node));
        public override void VisitRecordDeclaration(CSharpSyntax.RecordDeclarationSyntax node) => VisitContainer(node, TypeHeader(node));

        private static string TypeHeader(CSharpSyntax.TypeDeclarationSyntax node)
        {
            var modifiers = string.Join(" ", node.Modifiers.Select(m => m.Text));
            var typeParams = node.TypeParameterList?.ToString() ?? "";
            var baseList = node.BaseList is not null ? " " + node.BaseList : "";
            return $"{modifiers} {node.Keyword.Text} {node.Identifier.Text}{typeParams}{baseList}";
        }

        public override void VisitEnumDeclaration(CSharpSyntax.EnumDeclarationSyntax node) => AppendLine($"enum {node.Identifier.Text}");

        public override void VisitMethodDeclaration(CSharpSyntax.MethodDeclarationSyntax node) =>
            AppendLine(HeaderOnly(node, (SyntaxNode?)node.Body ?? node.ExpressionBody));

        public override void VisitConstructorDeclaration(CSharpSyntax.ConstructorDeclarationSyntax node) =>
            AppendLine(HeaderOnly(node, (SyntaxNode?)node.Body ?? node.ExpressionBody));

        public override void VisitPropertyDeclaration(CSharpSyntax.PropertyDeclarationSyntax node) => AppendLine(node.ToString());

        /// <summary>Cuts a method/constructor's text off at the start of its body/expression-body, so only the signature remains - the whole point of an "outline".</summary>
        private static string HeaderOnly(SyntaxNode node, SyntaxNode? bodyOrExpr)
        {
            var fullText = node.ToString();
            if (bodyOrExpr is null) return fullText.TrimEnd(';');
            var relativeEnd = Math.Max(0, Math.Min(fullText.Length, bodyOrExpr.SpanStart - node.SpanStart));
            return fullText[..relativeEnd];
        }
    }

    private class VisualBasicOutlineWalker : VisualBasicSyntaxWalker
    {
        public readonly List<string> Lines = [];
        private int _depth;

        private void AppendLine(string text) => Lines.Add(new string(' ', _depth * 2) + NormalizeSignature(text));

        private void VisitContainer(SyntaxNode node, string header)
        {
            AppendLine(header);
            _depth++;
            DefaultVisit(node);
            _depth--;
        }

        public override void VisitNamespaceBlock(VBSyntax.NamespaceBlockSyntax node) => VisitContainer(node, node.NamespaceStatement.ToString());
        public override void VisitModuleBlock(VBSyntax.ModuleBlockSyntax node) => VisitContainer(node, node.ModuleStatement.ToString());
        public override void VisitClassBlock(VBSyntax.ClassBlockSyntax node) => VisitContainer(node, node.ClassStatement.ToString());
        public override void VisitInterfaceBlock(VBSyntax.InterfaceBlockSyntax node) => VisitContainer(node, node.InterfaceStatement.ToString());
        public override void VisitStructureBlock(VBSyntax.StructureBlockSyntax node) => VisitContainer(node, node.StructureStatement.ToString());
        public override void VisitEnumBlock(VBSyntax.EnumBlockSyntax node) => AppendLine(node.EnumStatement.ToString());
        public override void VisitMethodBlock(VBSyntax.MethodBlockSyntax node) => AppendLine(node.SubOrFunctionStatement.ToString());

        /// <summary>Interface method signatures have no block/body - they appear directly as members, never inside a MethodBlockSyntax.</summary>
        public override void VisitMethodStatement(VBSyntax.MethodStatementSyntax node) => AppendLine(node.ToString());

        public override void VisitConstructorBlock(VBSyntax.ConstructorBlockSyntax node) => AppendLine(node.SubNewStatement.ToString());
        public override void VisitPropertyBlock(VBSyntax.PropertyBlockSyntax node) => AppendLine(node.PropertyStatement.ToString());

        /// <summary>Auto-properties (and interface property signatures) have no Get/Set block - standalone members, never inside a PropertyBlockSyntax.</summary>
        public override void VisitPropertyStatement(VBSyntax.PropertyStatementSyntax node) => AppendLine(node.ToString());
    }
}
