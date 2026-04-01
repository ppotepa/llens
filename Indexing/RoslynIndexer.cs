using Llens.Caching;
using Llens.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Llens.Indexing;

public class RoslynIndexer(ICodeMapCache cache, ILogger<RoslynIndexer> logger) : ICodeIndexer
{
    public async Task IndexRepoAsync(RepoConfig repo, CancellationToken ct = default)
    {
        if (!Directory.Exists(repo.Path))
        {
            logger.LogWarning("Repo path does not exist: {Path}", repo.Path);
            return;
        }

        var files = Directory
            .EnumerateFiles(repo.Path, "*", SearchOption.AllDirectories)
            .Where(f => repo.IncludeExtensions.Contains(Path.GetExtension(f)))
            .Where(f => !repo.ExcludePaths.Any(ex => f.Contains(Path.DirectorySeparatorChar + ex + Path.DirectorySeparatorChar)));

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            await IndexFileAsync(repo.Name, file, ct);
        }

        logger.LogInformation("Indexed repo {Name}", repo.Name);
    }

    public async Task IndexFileAsync(string repoName, string filePath, CancellationToken ct = default)
    {
        if (!filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return; // only Roslyn for C# files for now

        var source = await File.ReadAllTextAsync(filePath, ct);
        var tree = CSharpSyntaxTree.ParseText(source, cancellationToken: ct);
        var root = await tree.GetRootAsync(ct);

        var symbols = ExtractSymbols(repoName, filePath, root);
        await cache.StoreSymbolsAsync(filePath, symbols, ct);

        logger.LogDebug("Indexed {Count} symbols from {File}", symbols.Count, filePath);
    }

    public Task RemoveFileAsync(string repoName, string filePath, CancellationToken ct = default)
        => cache.RemoveFileAsync(filePath, ct);

    private static List<CodeSymbol> ExtractSymbols(string repoName, string filePath, SyntaxNode root)
    {
        var symbols = new List<CodeSymbol>();
        var text = root.SyntaxTree.GetText();

        foreach (var node in root.DescendantNodes())
        {
            CodeSymbol? symbol = node switch
            {
                ClassDeclarationSyntax c => Make(repoName, filePath, c.Identifier.Text, Models.SymbolKind.Class, c, text),
                InterfaceDeclarationSyntax i => Make(repoName, filePath, i.Identifier.Text, Models.SymbolKind.Interface, i, text),
                MethodDeclarationSyntax m => Make(repoName, filePath, m.Identifier.Text, Models.SymbolKind.Method, m, text, m.ToString().Split('\n')[0].Trim()),
                PropertyDeclarationSyntax p => Make(repoName, filePath, p.Identifier.Text, Models.SymbolKind.Property, p, text),
                EnumDeclarationSyntax e => Make(repoName, filePath, e.Identifier.Text, Models.SymbolKind.Enum, e, text),
                _ => null
            };

            if (symbol is not null)
                symbols.Add(symbol);
        }

        return symbols;
    }

    private static CodeSymbol Make(
        string repoName, string filePath, string name, Models.SymbolKind kind,
        SyntaxNode node, Microsoft.CodeAnalysis.Text.SourceText text, string? signature = null)
    {
        var span = node.GetLocation().GetLineSpan();
        return new CodeSymbol
        {
            Id = $"{repoName}::{filePath}::{name}::{kind}",
            RepoName = repoName,
            FilePath = filePath,
            Name = name,
            Kind = kind,
            LineStart = span.StartLinePosition.Line + 1,
            LineEnd = span.EndLinePosition.Line + 1,
            Signature = signature
        };
    }
}
