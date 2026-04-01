using Llens.Caching;
using Llens.Languages;
using Llens.Models;
using Llens.Scanning;
using Llens.Tools;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.RegularExpressions;

namespace Llens.Indexing;

public class CodeIndexer(
    ProjectRegistry projects,
    ICodeMapCache cache,
    IFileScanner scanner,
    ILogger<CodeIndexer> logger) : ICodeIndexer
{
    private static readonly Regex IdentifierRegex = new(@"\b[_A-Za-z][_A-Za-z0-9]*\b", RegexOptions.Compiled);
    private static readonly Regex RustCallRegex = new(@"(?:(?:\b|::|\.)([_A-Za-z][_A-Za-z0-9]*))\s*\(", RegexOptions.Compiled);
    private static readonly Regex RustPathRegex = new(@"\b(?:crate|self|super)(?:::[_A-Za-z][_A-Za-z0-9]*)+\b", RegexOptions.Compiled);
    private static readonly MetadataReference[] FrameworkReferences = LoadFrameworkReferences();
    private static readonly HashSet<string> KeywordStopWords = new(StringComparer.Ordinal)
    {
        // C#
        "class", "interface", "struct", "enum", "namespace", "using", "public", "private", "protected", "internal", "static",
        "void", "return", "if", "else", "switch", "case", "for", "foreach", "while", "do", "break", "continue", "new", "var",
        "this", "base", "null", "true", "false", "try", "catch", "finally", "throw", "async", "await", "get", "set",
        // Rust
        "fn", "mod", "pub", "crate", "self", "super", "impl", "trait", "let", "mut", "match", "where", "const", "static",
        "use", "enum", "struct", "type", "unsafe", "move", "ref", "as", "in", "loop",
    };

    public async Task IndexRepoAsync(RepoConfig repo, CancellationToken ct = default)
    {
        if (!Directory.Exists(repo.ResolvedPath))
        {
            logger.LogWarning("Repo path does not exist: {Path}", repo.ResolvedPath);
            return;
        }

        var project = projects.Resolve(repo.Name);
        if (project is null)
        {
            logger.LogWarning("No project registered for repo: {Name}", repo.Name);
            return;
        }

        var extensions = project.Languages.SupportedExtensions;
        var count = 0;
        var indexedFiles = new List<string>(capacity: 2048);

        await foreach (var file in scanner.GetFilesAsync(repo.ResolvedPath, extensions, ct))
        {
            ct.ThrowIfCancellationRequested();
            await IndexFileAsync(repo.Name, file, ct);
            indexedFiles.Add(file);
            count++;
        }

        // Second pass: rebuild references after all symbols are in cache.
        // This improves cross-file linkage when usage files are indexed before declaration files.
        foreach (var file in indexedFiles)
        {
            ct.ThrowIfCancellationRequested();
            var lang = project.Languages.Resolve(file);
            if (lang is null) continue;
            await RefreshReferencesForFileAsync(repo.Name, file, lang.Name, ct);
        }

        logger.LogInformation("Indexed {Count} files in project {Name}", count, repo.Name);
    }

    public async Task IndexFileAsync(string repoName, string filePath, CancellationToken ct = default)
    {
        var project = projects.Resolve(repoName);
        var language = project?.Languages.Resolve(filePath);
        if (language is null)
        {
            logger.LogDebug("No language handler for {File} in project {Repo}", filePath, repoName);
            return;
        }

        var context = new ToolContext(repoName, filePath);
        var allSymbols = new List<CodeSymbol>();
        var allImports = new List<string>();

        foreach (var tool in language.GetTools(ToolCapability.SymbolExtraction))
        {
            var result = await tool.ExecuteAsync(context, ct);
            if (result.Success)
            {
                allSymbols.AddRange(result.Symbols);
                allImports.AddRange(result.Imports);
            }
            else
                logger.LogWarning("[{Tool}] failed on {File}: {Error}", tool.GetType().Name, filePath, result.Error);
        }

        await cache.StoreSymbolsAsync(filePath, allSymbols, ct);

        var normalizedImports = language.Name.Equals("Rust", StringComparison.OrdinalIgnoreCase)
            ? RustImportResolver.ResolveToFilePaths(project!.Config.ResolvedPath, filePath, allImports)
            : [.. allImports.Distinct()];

        await RefreshReferencesForFileAsync(repoName, filePath, language.Name, ct);

        await cache.StoreFileNodeAsync(new FileNode
        {
            FilePath = filePath,
            RepoName = repoName,
            Language = language.Name,
            Imports = [.. normalizedImports],
            SymbolCount = allSymbols.Count,
            LastIndexedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        }, ct);

        logger.LogDebug("Indexed {Count} symbols in {File}", allSymbols.Count, filePath);
    }

    public Task RemoveFileAsync(string repoName, string filePath, CancellationToken ct = default)
        => cache.RemoveFileAsync(filePath, ct);

    private async Task RefreshReferencesForFileAsync(string repoName, string filePath, string languageName, CancellationToken ct)
    {
        await cache.RemoveReferencesInFileAsync(filePath, ct);
        if (!File.Exists(filePath)) return;

        var lines = await File.ReadAllLinesAsync(filePath, ct);
        if (lines.Length == 0) return;

        if (languageName.Equals("CSharp", StringComparison.OrdinalIgnoreCase))
        {
            var semanticRefs = await ExtractCSharpSemanticReferencesAsync(repoName, filePath, lines, ct);
            if (semanticRefs.Count > 0)
            {
                await cache.StoreReferencesAsync(semanticRefs, ct);
                return;
            }
        }

        var candidateCache = new Dictionary<string, List<CodeSymbol>>(StringComparer.Ordinal);
        var queriedTokenCount = 0;
        var references = new List<SymbolReference>(capacity: 256);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var usages = languageName.Equals("CSharp", StringComparison.OrdinalIgnoreCase)
            ? await ExtractCSharpUsagesAsync(filePath, lines, ct)
            : languageName.Equals("Rust", StringComparison.OrdinalIgnoreCase)
                ? ExtractRustUsages(lines)
                : ExtractLexicalUsages(lines);

        foreach (var usage in usages)
        {
            var token = usage.Token;
            if (!candidateCache.TryGetValue(token, out var candidates))
            {
                if (queriedTokenCount >= 220) continue;
                queriedTokenCount++;
                candidates = (await cache.QueryByNameAsync(token, repoName, ct))
                    .Where(s => s.Name.Equals(token, StringComparison.Ordinal))
                    .Take(32)
                    .ToList();
                candidateCache[token] = candidates;
            }

            foreach (var symbol in candidates)
            {
                // Skip the symbol definition line itself.
                if (symbol.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase) && symbol.LineStart == usage.Line)
                    continue;

                var dedupe = $"{symbol.Id}|{usage.Line}";
                if (!seen.Add(dedupe)) continue;

                references.Add(new SymbolReference
                {
                    SymbolId = symbol.Id,
                    InFilePath = filePath,
                    RepoName = repoName,
                    Line = usage.Line,
                    Context = usage.Context
                });

                if (references.Count >= 2400) break;
            }

            if (references.Count >= 2400) break;
        }

        if (references.Count > 0)
            await cache.StoreReferencesAsync(references, ct);
    }

    private static IEnumerable<SymbolUsage> ExtractLexicalUsages(IReadOnlyList<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            var tokens = IdentifierRegex.Matches(line)
                .Select(m => m.Value)
                .Distinct(StringComparer.Ordinal)
                .Where(t => t.Length >= 3)
                .Where(t => !KeywordStopWords.Contains(t))
                .Take(32);

            foreach (var token in tokens)
            {
                if (!IsLikelyReferenceUsage(line, token))
                    continue;
                yield return new SymbolUsage(token, i + 1, line.Trim());
            }
        }
    }

    private static async Task<List<SymbolUsage>> ExtractCSharpUsagesAsync(string filePath, IReadOnlyList<string> lines, CancellationToken ct)
    {
        var source = string.Join('\n', lines);
        var tree = CSharpSyntaxTree.ParseText(source, cancellationToken: ct);
        var root = await tree.GetRootAsync(ct);
        var usages = new List<SymbolUsage>(capacity: 256);

        var simpleNames = root.DescendantNodes().OfType<SimpleNameSyntax>();
        foreach (var name in simpleNames)
        {
            if (!IsCSharpReferenceName(name)) continue;

            var token = name.Identifier.Text;
            if (token.Length < 3 || KeywordStopWords.Contains(token)) continue;

            var span = name.GetLocation().GetLineSpan();
            var line = span.StartLinePosition.Line + 1;
            if (line <= 0 || line > lines.Count) continue;

            usages.Add(new SymbolUsage(token, line, lines[line - 1].Trim()));
            if (usages.Count >= 2600) break;
        }

        return usages;
    }

    private static List<SymbolUsage> ExtractRustUsages(IReadOnlyList<string> lines)
    {
        var usages = new List<SymbolUsage>(capacity: 512);
        for (var i = 0; i < lines.Count; i++)
        {
            var raw = lines[i];
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var line = RemoveRustCommentTail(raw);
            if (string.IsNullOrWhiteSpace(line)) continue;

            foreach (Match m in RustCallRegex.Matches(line))
            {
                var token = m.Groups[1].Value;
                if (token.Length < 2 || KeywordStopWords.Contains(token)) continue;
                usages.Add(new SymbolUsage(token, i + 1, raw.Trim()));
                if (usages.Count >= 2600) return usages;
            }

            foreach (Match m in RustPathRegex.Matches(line))
            {
                var path = m.Value;
                var last = path.Split("::", StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                if (string.IsNullOrWhiteSpace(last) || last.Length < 2 || KeywordStopWords.Contains(last)) continue;
                usages.Add(new SymbolUsage(last, i + 1, raw.Trim()));
                if (usages.Count >= 2600) return usages;
            }
        }

        return usages;
    }

    private static string RemoveRustCommentTail(string line)
    {
        var idx = line.IndexOf("//", StringComparison.Ordinal);
        return idx >= 0 ? line[..idx] : line;
    }

    private static bool IsCSharpReferenceName(SimpleNameSyntax name)
    {
        var parent = name.Parent;
        if (parent is null) return false;

        // Declaration heads (not usage sites).
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

        // Keep common usage contexts.
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

    private static bool IsLikelyReferenceUsage(string line, string token)
    {
        // Cheap structural hints to avoid flooding references with declaration/keyword noise.
        // Accept member access, calls, type/namespace paths, assignments, and generic usage.
        return line.Contains($"{token}(", StringComparison.Ordinal)
            || line.Contains($".{token}", StringComparison.Ordinal)
            || line.Contains($"::{token}", StringComparison.Ordinal)
            || line.Contains($"{token}::", StringComparison.Ordinal)
            || line.Contains($"<{token}>", StringComparison.Ordinal)
            || line.Contains($" {token}<", StringComparison.Ordinal)
            || line.Contains($" {token} ", StringComparison.Ordinal)
            || line.Contains($": {token}", StringComparison.Ordinal)
            || line.Contains($"= {token}", StringComparison.Ordinal);
    }

    private async Task<List<SymbolReference>> ExtractCSharpSemanticReferencesAsync(
        string repoName,
        string filePath,
        IReadOnlyList<string> lines,
        CancellationToken ct)
    {
        var source = string.Join('\n', lines);
        var tree = CSharpSyntaxTree.ParseText(source, cancellationToken: ct);
        var compilation = CSharpCompilation.Create(
            assemblyName: "LlensSemanticRefs",
            syntaxTrees: [tree],
            references: FrameworkReferences,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
        var root = await tree.GetRootAsync(ct);
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
            if (usageLine <= 0 || usageLine > lines.Count) continue;

            var symbolName = symbol.Name;
            if (string.IsNullOrWhiteSpace(symbolName) || symbolName.Length < 2) continue;
            if (!byNameCache.TryGetValue(symbolName, out var candidates))
            {
                candidates = (await cache.QueryByNameAsync(symbolName, repoName, ct))
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

            if (mapped.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase) && mapped.LineStart == usageLine)
                continue;

            var key = $"{mapped.Id}|{usageLine}";
            if (!dedupe.Add(key)) continue;

            references.Add(new SymbolReference
            {
                SymbolId = mapped.Id,
                InFilePath = filePath,
                RepoName = repoName,
                Line = usageLine,
                Context = lines[usageLine - 1].Trim()
            });

            if (references.Count >= 2600) break;
        }

        return references;
    }

    private static bool IsSemanticUsageNode(SyntaxNode node)
    {
        if (node is InvocationExpressionSyntax
            or ObjectCreationExpressionSyntax
            or MemberAccessExpressionSyntax
            or IdentifierNameSyntax
            or QualifiedNameSyntax)
        {
            // Exclude common declaration contexts.
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

    private readonly record struct SymbolUsage(string Token, int Line, string Context);
}
