using Llens.Models;
using Llens.Tools;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelSymbolKind = Llens.Models.SymbolKind;

namespace Llens.Languages.CSharp;

/// <summary>
/// Roslyn-based symbol and import extractor for C#.
/// Covers both capabilities in a single AST pass — no need to parse the file twice.
/// </summary>
public sealed class RoslynExtractor : ISymbolExtractor<CSharp>
{
    public async Task<ToolResult> ExtractAsync(ToolContext context, CancellationToken ct = default)
    {
        var source = await File.ReadAllTextAsync(context.FilePath, ct);
        var tree = CSharpSyntaxTree.ParseText(source, cancellationToken: ct);
        var root = await tree.GetRootAsync(ct);

        var symbols = root.DescendantNodes()
            .SelectMany<Microsoft.CodeAnalysis.SyntaxNode, CodeSymbol>(node => node switch
            {
                ClassDeclarationSyntax c     => [Make(context, c.Identifier.Text, ModelSymbolKind.Class, c, BuildTypeSignature(c.Identifier.Text, c.BaseList))],
                InterfaceDeclarationSyntax i => [Make(context, i.Identifier.Text, ModelSymbolKind.Interface, i, BuildTypeSignature(i.Identifier.Text, i.BaseList))],
                MethodDeclarationSyntax m    => [Make(context, m.Identifier.Text, ModelSymbolKind.Method, m, m.ToString().Split('\n')[0].Trim())],
                PropertyDeclarationSyntax p  => [Make(context, p.Identifier.Text, ModelSymbolKind.Property, p)],
                EnumDeclarationSyntax e      => [Make(context, e.Identifier.Text, ModelSymbolKind.Enum, e)],
                _                            => []
            })
            .ToList();

        var imports = root.DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .Select(u => u.Name?.ToString())
            .OfType<string>()
            .ToArray();

        return ToolResult.Ok(symbols, imports);
    }

    private static string BuildTypeSignature(string name, BaseListSyntax? baseList)
    {
        if (baseList is null || baseList.Types.Count == 0) return name;
        var bases = string.Join(", ", baseList.Types.Select(t => t.Type.ToString()));
        return $"{name} : {bases}";
    }

    private static CodeSymbol Make(
        ToolContext ctx, string name, ModelSymbolKind kind,
        Microsoft.CodeAnalysis.SyntaxNode node, string? signature = null)
    {
        var span = node.GetLocation().GetLineSpan();
        var lineStart = span.StartLinePosition.Line + 1;
        return new CodeSymbol
        {
            Id = $"{ctx.RepoName}::{ctx.FilePath}::{name}::{kind}::{lineStart}",
            RepoName = ctx.RepoName,
            FilePath = ctx.FilePath,
            Name = name,
            Kind = kind,
            LineStart = lineStart,
            LineEnd = span.EndLinePosition.Line + 1,
            Signature = signature
        };
    }
}
