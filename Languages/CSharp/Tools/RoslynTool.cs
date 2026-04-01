using Llens.Tools;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Llens.Models;

namespace Llens.Languages.CSharp;

public class RoslynTool : ITool<CSharp>
{
    public ToolKind Kind => ToolKind.Roslyn;
    public ToolPurpose Purpose => ToolPurpose.Indexing;

    public async Task<ToolResult> ExecuteAsync(ToolContext context, CancellationToken ct = default)
    {
        var source = await File.ReadAllTextAsync(context.FilePath, ct);
        var tree = CSharpSyntaxTree.ParseText(source, cancellationToken: ct);
        var root = await tree.GetRootAsync(ct);

        var symbols = root.DescendantNodes()
            .SelectMany<Microsoft.CodeAnalysis.SyntaxNode, CodeSymbol>(node => node switch
            {
                ClassDeclarationSyntax c     => [Make(context, c.Identifier.Text, SymbolKind.Class, c)],
                InterfaceDeclarationSyntax i => [Make(context, i.Identifier.Text, SymbolKind.Interface, i)],
                MethodDeclarationSyntax m    => [Make(context, m.Identifier.Text, SymbolKind.Method, m, m.ToString().Split('\n')[0].Trim())],
                PropertyDeclarationSyntax p  => [Make(context, p.Identifier.Text, SymbolKind.Property, p)],
                EnumDeclarationSyntax e      => [Make(context, e.Identifier.Text, SymbolKind.Enum, e)],
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

    private static CodeSymbol Make(
        ToolContext ctx, string name, SymbolKind kind,
        Microsoft.CodeAnalysis.SyntaxNode node, string? signature = null)
    {
        var span = node.GetLocation().GetLineSpan();
        return new CodeSymbol
        {
            Id = $"{ctx.RepoName}::{ctx.FilePath}::{name}::{kind}",
            RepoName = ctx.RepoName,
            FilePath = ctx.FilePath,
            Name = name,
            Kind = kind,
            LineStart = span.StartLinePosition.Line + 1,
            LineEnd = span.EndLinePosition.Line + 1,
            Signature = signature
        };
    }
}
