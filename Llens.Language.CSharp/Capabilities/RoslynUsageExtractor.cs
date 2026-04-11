using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Llens.Languages.CSharp;

/// <summary>
/// Roslyn AST-based usage extractor for C#.
/// Walks the syntax tree to find reference sites, filtering out declaration nodes.
/// </summary>
public sealed class RoslynUsageExtractor : IUsageExtractor<CSharp>
{
    public IEnumerable<(string Token, int Line, string Context)> Extract(string filePath, IReadOnlyList<string> lines)
    {
        var source = string.Join('\n', lines);
        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();

        foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            if (!IsCSharpReferenceName(name)) continue;

            var token = name.Identifier.Text;
            if (token.Length < 3) continue;

            var span = name.GetLocation().GetLineSpan();
            var line = span.StartLinePosition.Line + 1;
            if (line <= 0 || line > lines.Count) continue;

            yield return (token, line, lines[line - 1].Trim());
        }
    }

    private static bool IsCSharpReferenceName(SimpleNameSyntax name)
    {
        var parent = name.Parent;
        if (parent is null) return false;

        if (parent is ClassDeclarationSyntax
            or InterfaceDeclarationSyntax
            or MethodDeclarationSyntax
            or PropertyDeclarationSyntax
            or EnumDeclarationSyntax
            or VariableDeclaratorSyntax
            or ParameterSyntax
            or NamespaceDeclarationSyntax
            or FileScopedNamespaceDeclarationSyntax)
            return false;

        return parent is InvocationExpressionSyntax
            or MemberAccessExpressionSyntax
            or ObjectCreationExpressionSyntax
            or AssignmentExpressionSyntax
            or BinaryExpressionSyntax
            or ArgumentSyntax
            or ReturnStatementSyntax
            or EqualsValueClauseSyntax
            or ConditionalExpressionSyntax
            or AttributeSyntax
            or TypeArgumentListSyntax
            or QualifiedNameSyntax;
    }
}
