using Llens.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Llens.Languages.CSharp;

/// <summary>
/// Roslyn semantic-model reference resolver for C#.
/// Builds a partial compilation from project files, then uses the semantic model
/// to resolve identifiers to their declaration sites and map them to indexed symbols.
/// </summary>
public sealed class RoslynReferenceResolver : IReferenceResolver<CSharp>
{
    public async Task<IReadOnlyList<SymbolReference>> ResolveAsync(LanguageReferenceContext context, CancellationToken ct = default)
    {
        var source = string.Join('\n', context.Lines);
        var tree = CSharpSyntaxTree.ParseText(source, path: context.FilePath, cancellationToken: ct);
        var root = await tree.GetRootAsync(ct);
        var projectTrees = await BuildProjectSyntaxTreesAsync(context, tree, root, ct);
        var compilation = CSharpCompilation.Create(
            assemblyName: "LlensSemanticRefs",
            syntaxTrees: projectTrees,
            references: LoadFrameworkReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
        var dedupe = new HashSet<string>(StringComparer.Ordinal);
        var byNameCache = new Dictionary<string, List<CodeSymbol>>(StringComparer.Ordinal);
        var references = new List<SymbolReference>(capacity: 256);

        var usageNodes = root.DescendantNodes().Where(IsSemanticUsageNode);
        foreach (var node in usageNodes)
        {
            var info = model.GetSymbolInfo(node, ct);
            var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            if (symbol is null) continue;

            var sourceLoc = symbol.Locations.FirstOrDefault(l => l.IsInSource && !string.IsNullOrWhiteSpace(l.SourceTree?.FilePath));
            if (sourceLoc is null) continue;

            var targetPath = Path.GetFullPath(sourceLoc.SourceTree!.FilePath);
            var targetLine = sourceLoc.GetLineSpan().StartLinePosition.Line + 1;
            var usageLine = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            if (usageLine <= 0 || usageLine > context.Lines.Count) continue;

            var symbolName = symbol.Name;
            if (string.IsNullOrWhiteSpace(symbolName) || symbolName.Length < 2) continue;
            if (!byNameCache.TryGetValue(symbolName, out var candidates))
            {
                candidates = (await context.QueryByNameAsync(symbolName, context.RepoName, ct))
                    .Where(s => s.Name.Equals(symbolName, StringComparison.Ordinal))
                    .Take(64)
                    .ToList();
                byNameCache[symbolName] = candidates;
            }

            var mapped = candidates
                .Where(s => s.FilePath.Equals(targetPath, StringComparison.OrdinalIgnoreCase))
                .Where(s => targetLine >= s.LineStart && (s.LineEnd <= 0 || targetLine <= s.LineEnd))
                .OrderBy(s => Math.Abs(s.LineStart - targetLine))
                .FirstOrDefault();
            if (mapped is null) continue;

            if (mapped.FilePath.Equals(context.FilePath, StringComparison.OrdinalIgnoreCase) && mapped.LineStart == usageLine)
                continue;

            var key = $"{mapped.Id}|{usageLine}";
            if (!dedupe.Add(key)) continue;

            references.Add(new SymbolReference
            {
                SymbolId = mapped.Id,
                InFilePath = context.FilePath,
                RepoName = context.RepoName,
                Line = usageLine,
                Context = context.Lines[usageLine - 1].Trim()
            });

            if (references.Count >= 2600) break;
        }

        return references;
    }

    private static async Task<List<SyntaxTree>> BuildProjectSyntaxTreesAsync(
        LanguageReferenceContext context,
        SyntaxTree currentTree,
        SyntaxNode currentRoot,
        CancellationToken ct)
    {
        const int maxSameDirectoryFiles = 64;
        const int maxCandidateFiles = 160;
        var trees = new List<SyntaxTree> { currentTree };
        var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(context.FilePath)
        };

        var allFiles = (await context.GetProjectFilesAsync(context.RepoName, ct))
            .Select(f => Path.GetFullPath(f.FilePath))
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var currentDir = Path.GetDirectoryName(Path.GetFullPath(context.FilePath)) ?? "";
        foreach (var file in allFiles.Where(f => string.Equals(Path.GetDirectoryName(f), currentDir, StringComparison.OrdinalIgnoreCase))
                                     .Take(maxSameDirectoryFiles))
        {
            if (!included.Add(file)) continue;
            var tree = await TryParseTreeAsync(file, ct);
            if (tree is null) continue;
            trees.Add(tree);
        }

        var tokenNames = currentRoot.DescendantNodes()
            .OfType<SimpleNameSyntax>()
            .Select(n => n.Identifier.Text)
            .Where(n => n.Length >= 3)
            .Distinct(StringComparer.Ordinal)
            .Take(32)
            .ToList();

        foreach (var token in tokenNames)
        {
            var hits = await context.QueryByNameAsync(token, context.RepoName, ct);
            foreach (var hit in hits.Where(s => s.Name.Equals(token, StringComparison.Ordinal))
                                    .Select(s => Path.GetFullPath(s.FilePath))
                                    .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                                    .Where(File.Exists)
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .Take(maxCandidateFiles))
            {
                if (!included.Add(hit)) continue;
                var tree = await TryParseTreeAsync(hit, ct);
                if (tree is null) continue;
                trees.Add(tree);
                if (included.Count >= 1 + maxSameDirectoryFiles + maxCandidateFiles)
                    return trees;
            }
        }

        return trees;
    }

    private static async Task<SyntaxTree?> TryParseTreeAsync(string filePath, CancellationToken ct)
    {
        try
        {
            var source = await File.ReadAllTextAsync(filePath, ct);
            return CSharpSyntaxTree.ParseText(source, path: filePath, cancellationToken: ct);
        }
        catch
        {
            return null;
        }
    }

    private static MetadataReference[] LoadFrameworkReferences()
    {
        var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (string.IsNullOrWhiteSpace(tpa)) return [];
        return tpa
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }

    private static bool IsSemanticUsageNode(SyntaxNode node)
    {
        if (node is InvocationExpressionSyntax
            or ObjectCreationExpressionSyntax
            or MemberAccessExpressionSyntax
            or IdentifierNameSyntax
            or QualifiedNameSyntax)
        {
            var p = node.Parent;
            return p is not ClassDeclarationSyntax
                and not InterfaceDeclarationSyntax
                and not MethodDeclarationSyntax
                and not PropertyDeclarationSyntax
                and not EnumDeclarationSyntax
                and not VariableDeclaratorSyntax
                and not ParameterSyntax
                and not NamespaceDeclarationSyntax
                and not FileScopedNamespaceDeclarationSyntax;
        }
        return false;
    }
}
