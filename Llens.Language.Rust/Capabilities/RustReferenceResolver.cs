using System.Text.RegularExpressions;
using Llens.Models;

namespace Llens.Languages.Rust;

/// <summary>
/// Heuristic reference resolver for Rust.
/// Scores symbol candidates based on imports, symbol kind, and call-site context.
/// Does not require an in-process compiler — all resolution is index-driven.
/// </summary>
public sealed class RustReferenceResolver : IReferenceResolver<Rust>
{
    private static readonly Regex RustUseRegex = new(@"^\s*(?:pub\s+)?use\s+(.+?)\s*;\s*$", RegexOptions.Compiled);

    private readonly RustUsageExtractor _usageExtractor = new();
    private readonly CargoImportResolver _importResolver = new();

    public Task<IReadOnlyList<SymbolReference>> ResolveAsync(LanguageReferenceContext context, CancellationToken ct = default)
        => BuildReferencesAsync(context, ct);

    private async Task<IReadOnlyList<SymbolReference>> BuildReferencesAsync(LanguageReferenceContext context, CancellationToken ct)
    {
        var rawImports = context.Lines
            .Select(line => RustUseRegex.Match(line))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var importedFiles = await ResolveImportedFilesAsync(context, rawImports, ct);
        var usageItems = _usageExtractor.Extract(context.FilePath, context.Lines)
            .Take(3200)
            .ToList();
        if (usageItems.Count == 0)
            return [];

        var byNameCache = new Dictionary<string, List<CodeSymbol>>(StringComparer.Ordinal);
        var dedupe = new HashSet<string>(StringComparer.Ordinal);
        var refs = new List<SymbolReference>(capacity: 320);

        foreach (var usage in usageItems)
        {
            ct.ThrowIfCancellationRequested();
            if (RustUseRegex.IsMatch(usage.Context))
                continue;

            if (!byNameCache.TryGetValue(usage.Token, out var candidates))
            {
                candidates = (await context.QueryByNameAsync(usage.Token, context.RepoName, ct))
                    .Where(s => s.Name.Equals(usage.Token, StringComparison.Ordinal))
                    .Take(96)
                    .ToList();
                byNameCache[usage.Token] = candidates;
            }

            if (candidates.Count == 0) continue;

            var ranked = RankCandidates(candidates, usage.Token, usage.Line, usage.Context, importedFiles, context.FilePath)
                .Take(4)
                .ToList();
            if (ranked.Count == 0 || ranked[0].Score < 12)
                continue;

            var best = new List<CodeSymbol> { ranked[0].Symbol };
            if (ranked.Count > 1 && ranked[1].Score >= 12 && ranked[0].Score - ranked[1].Score <= 2)
                best.Add(ranked[1].Symbol);

            foreach (var target in best)
            {
                if (target.FilePath.Equals(context.FilePath, StringComparison.OrdinalIgnoreCase) && target.LineStart == usage.Line)
                    continue;

                var key = $"{target.Id}|{usage.Line}";
                if (!dedupe.Add(key)) continue;

                refs.Add(new SymbolReference
                {
                    SymbolId = target.Id,
                    InFilePath = context.FilePath,
                    RepoName = context.RepoName,
                    Line = usage.Line,
                    Context = usage.Context
                });

                if (refs.Count >= 2800)
                    return refs;
            }
        }

        return refs;
    }

    private static IEnumerable<(CodeSymbol Symbol, int Score)> RankCandidates(
        IEnumerable<CodeSymbol> candidates,
        string token,
        int usageLine,
        string usageContext,
        HashSet<string> importedFiles,
        string currentFilePath)
    {
        var callLike = IsCallLike(usageContext, token);
        var typeLike = IsTypeLike(usageContext, token);
        var sameFile = Path.GetFullPath(currentFilePath);

        return candidates
            .Select(symbol =>
            {
                var score = 0;
                var symbolFile = Path.GetFullPath(symbol.FilePath);
                if (symbolFile.Equals(sameFile, StringComparison.OrdinalIgnoreCase))
                {
                    score += 18;
                    if (symbol.LineStart > 0)
                        score += Math.Max(0, 16 - Math.Min(16, Math.Abs(symbol.LineStart - usageLine)));
                }

                if (importedFiles.Contains(symbolFile))
                    score += 30;

                if (callLike && symbol.Kind is SymbolKind.Function or SymbolKind.Method)
                    score += 24;
                if (typeLike && symbol.Kind is SymbolKind.Struct or SymbolKind.Trait or SymbolKind.Enum or SymbolKind.Class or SymbolKind.Interface)
                    score += 22;

                if (!callLike && !typeLike)
                    score += symbol.Kind switch
                    {
                        SymbolKind.Function or SymbolKind.Method => 9,
                        SymbolKind.Struct or SymbolKind.Class or SymbolKind.Interface or SymbolKind.Trait => 8,
                        _ => 3
                    };

                return (Symbol: symbol, Score: score);
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Symbol.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Symbol.LineStart);
    }

    private static bool IsCallLike(string line, string token)
        => line.Contains($"{token}(", StringComparison.Ordinal)
           || line.Contains($"::{token}(", StringComparison.Ordinal)
           || line.Contains($".{token}(", StringComparison.Ordinal);

    private static bool IsTypeLike(string line, string token)
        => line.Contains($": {token}", StringComparison.Ordinal)
           || line.Contains($"-> {token}", StringComparison.Ordinal)
           || line.Contains($"<{token}>", StringComparison.Ordinal)
           || line.Contains($" {token} {{", StringComparison.Ordinal);

    private async Task<HashSet<string>> ResolveImportedFilesAsync(
        LanguageReferenceContext context,
        IReadOnlyList<string> rawImports,
        CancellationToken ct)
    {
        if (rawImports.Count == 0)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var projectFiles = (await context.GetProjectFilesAsync(context.RepoName, ct))
            .Select(f => Path.GetFullPath(f.FilePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (projectFiles.Count == 0)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var repoRoot = FindCommonAncestor(projectFiles);
        if (string.IsNullOrWhiteSpace(repoRoot))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var resolved = _importResolver.Resolve(repoRoot, context.FilePath, rawImports);
        return new HashSet<string>(resolved.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
    }

    private static string? FindCommonAncestor(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return null;
        var split = paths
            .Select(p => Path.GetDirectoryName(p) ?? p)
            .Select(p => p.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            .ToList();
        if (split.Count == 0) return null;

        var minLen = split.Min(s => s.Length);
        var parts = new List<string>(capacity: minLen);
        for (var i = 0; i < minLen; i++)
        {
            var segment = split[0][i];
            if (split.Any(s => !string.Equals(s[i], segment, StringComparison.OrdinalIgnoreCase)))
                break;
            parts.Add(segment);
        }

        if (parts.Count == 0)
            return Path.GetPathRoot(paths[0]);

        var root = Path.GetPathRoot(paths[0]) ?? Path.DirectorySeparatorChar.ToString();
        return Path.Combine([root, .. parts]);
    }
}
