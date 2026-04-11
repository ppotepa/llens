using Llens.Caching;
using Llens.Languages;
using Llens.Models;
using Llens.Scanning;
using Llens.Tools;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace Llens.Indexing;

public class CodeIndexer(
    ProjectRegistry projects,
    ICodeMapCache cache,
    IFileScanner scanner,
    ILogger<CodeIndexer> logger) : ICodeIndexer
{
    private static readonly Regex IdentifierRegex = new(@"\b[_A-Za-z][_A-Za-z0-9]*\b", RegexOptions.Compiled);
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
            await RefreshReferencesForFileAsync(repo.Name, file, lang, ct);
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
        var result = await language.SymbolExtractor.ExtractAsync(context, ct);

        if (!result.Success)
        {
            logger.LogWarning("[{Language}] extraction failed on {File}: {Error}", language.Name, filePath, result.Error);
            return;
        }

        await cache.StoreSymbolsAsync(filePath, result.Symbols, ct);

        var normalizedImports = language.ImportResolver?.Resolve(project!.Config.ResolvedPath, filePath, result.Imports)
            ?? [.. result.Imports.Distinct()];

        await RefreshReferencesForFileAsync(repoName, filePath, language, ct);

        await cache.StoreFileNodeAsync(new FileNode
        {
            FilePath = filePath,
            RepoName = repoName,
            Language = language.Name,
            Imports = [.. normalizedImports],
            SymbolCount = result.Symbols.Count,
            LastIndexedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        }, ct);

        logger.LogDebug("Indexed {Count} symbols in {File}", result.Symbols.Count, filePath);
    }

    public Task RemoveFileAsync(string repoName, string filePath, CancellationToken ct = default)
        => cache.RemoveFileAsync(filePath, ct);

    private async Task RefreshReferencesForFileAsync(string repoName, string filePath, ILanguage language, CancellationToken ct)
    {
        await cache.RemoveReferencesInFileAsync(filePath, ct);
        if (!File.Exists(filePath)) return;

        var lines = await File.ReadAllLinesAsync(filePath, ct);
        if (lines.Length == 0) return;

        if (language.ReferenceResolver is { } resolver)
        {
            var semanticRefs = await resolver.ResolveAsync(
                new LanguageReferenceContext(
                    repoName,
                    filePath,
                    lines,
                    async (name, repo, token) => await cache.QueryByNameAsync(name, repo, token),
                    async (repo, token) => await cache.GetAllFilesAsync(repo, token)),
                ct);
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

        var usages = language.UsageExtractor?.Extract(filePath, lines)
            ?? ExtractLexicalUsages(lines);

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

    private static IEnumerable<(string Token, int Line, string Context)> ExtractLexicalUsages(IReadOnlyList<string> lines)
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
                yield return (token, i + 1, line.Trim());
            }
        }
    }

    private static bool IsLikelyReferenceUsage(string line, string token)
    {
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
}
