using Llens.Indexing;
using Llens.Models;
using Llens.Tools;
using System.Text.RegularExpressions;

namespace Llens.Languages.Rust;

public class RustLanguage : ILanguage<Rust>, ILanguageIndexingPlugin
{
    private static readonly Regex RustUseRegex = new(@"^\s*(?:pub\s+)?use\s+(.+?)\s*;\s*$", RegexOptions.Compiled);
    private static readonly Regex RustCallRegex = new(@"(?:(?:\b|::|\.)([_A-Za-z][_A-Za-z0-9]*))\s*\(", RegexOptions.Compiled);
    private static readonly Regex RustPathRegex = new(@"\b(?:crate|self|super|[_A-Za-z][_A-Za-z0-9]*)(?:::[_A-Za-z][_A-Za-z0-9]*)+\b", RegexOptions.Compiled);
    private static readonly Regex RustTypeAnnotationRegex = new(@"(?:->|:)\s*&?\s*(?:'[_A-Za-z][_A-Za-z0-9]*\s+)?(?:mut\s+)?([_A-Za-z][_A-Za-z0-9]*)", RegexOptions.Compiled);
    private static readonly Regex RustGenericArgRegex = new(@"(?:<|,)\s*&?\s*(?:'[_A-Za-z][_A-Za-z0-9]*\s+)?([_A-Za-z][_A-Za-z0-9]*)", RegexOptions.Compiled);
    private static readonly Regex RustStructLiteralRegex = new(@"\b([A-Z][_A-Za-z0-9]*)\s*\{", RegexOptions.Compiled);
    private static readonly HashSet<string> KeywordStopWords = new(StringComparer.Ordinal)
    {
        "fn", "mod", "pub", "crate", "self", "super", "impl", "trait", "let", "mut", "match", "where", "const", "static",
        "use", "enum", "struct", "type", "unsafe", "move", "ref", "as", "in", "loop", "dyn", "Self"
    };

    public LanguageId Id => LanguageId.Rust;
    public string Name => "Rust";
    public IReadOnlyList<string> Extensions => [".rs"];

    public IReadOnlyList<ITool<Rust>> Tools =>
    [
        new SynShimTool()
    ];

    public IReadOnlyList<string> NormalizeImports(string repoRoot, string filePath, IReadOnlyList<string> imports)
        => RustImportResolver.ResolveToFilePaths(repoRoot, filePath, imports);

    public IEnumerable<(string Token, int Line, string Context)> ExtractUsages(string filePath, IReadOnlyList<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var raw = lines[i];
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var line = RemoveRustCommentTail(raw);
            if (string.IsNullOrWhiteSpace(line)) continue;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (Match m in RustCallRegex.Matches(line))
            {
                if (TryGetToken(m.Groups[1].Value, out var token) && seen.Add(token))
                    yield return (token, i + 1, raw.Trim());
            }

            foreach (Match m in RustPathRegex.Matches(line))
            {
                var path = m.Value;
                var segments = path.Split("::", StringSplitOptions.RemoveEmptyEntries);
                foreach (var segment in segments)
                {
                    if (TryGetToken(segment, out var token) && seen.Add(token))
                        yield return (token, i + 1, raw.Trim());
                }
            }

            foreach (Match m in RustTypeAnnotationRegex.Matches(line))
            {
                if (TryGetToken(m.Groups[1].Value, out var token) && seen.Add(token))
                    yield return (token, i + 1, raw.Trim());
            }

            foreach (Match m in RustGenericArgRegex.Matches(line))
            {
                if (TryGetToken(m.Groups[1].Value, out var token) && seen.Add(token))
                    yield return (token, i + 1, raw.Trim());
            }

            foreach (Match m in RustStructLiteralRegex.Matches(line))
            {
                if (TryGetToken(m.Groups[1].Value, out var token) && seen.Add(token))
                    yield return (token, i + 1, raw.Trim());
            }
        }
    }

    public Task<IReadOnlyList<SymbolReference>> BuildSemanticReferencesAsync(LanguageReferenceContext context, CancellationToken ct)
        => BuildSemanticReferencesInternalAsync(context, ct);

    private async Task<IReadOnlyList<SymbolReference>> BuildSemanticReferencesInternalAsync(LanguageReferenceContext context, CancellationToken ct)
    {
        var rawImports = context.Lines
            .Select(line => RustUseRegex.Match(line))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var importedFiles = await ResolveImportedFilesAsync(context, rawImports, ct);
        var usageItems = ExtractUsages(context.FilePath, context.Lines)
            .Select(x => new UsageItem(x.Token, x.Line, x.Context))
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

            var ranked = RankCandidates(candidates, usage, importedFiles, context.FilePath)
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
        UsageItem usage,
        HashSet<string> importedFiles,
        string currentFilePath)
    {
        var callLike = IsCallLike(usage.Context, usage.Token);
        var typeLike = IsTypeLike(usage.Context, usage.Token);
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
                        score += Math.Max(0, 16 - Math.Min(16, Math.Abs(symbol.LineStart - usage.Line)));
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

    private static async Task<HashSet<string>> ResolveImportedFilesAsync(
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

        var resolved = RustImportResolver.ResolveToFilePaths(repoRoot, context.FilePath, rawImports);
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

    private sealed record UsageItem(string Token, int Line, string Context);

    private static string RemoveRustCommentTail(string line)
    {
        var idx = line.IndexOf("//", StringComparison.Ordinal);
        return idx >= 0 ? line[..idx] : line;
    }

    private static bool TryGetToken(string raw, out string token)
    {
        token = raw.Trim();
        if (string.IsNullOrWhiteSpace(token) || token.Length < 2)
            return false;
        if (KeywordStopWords.Contains(token))
            return false;
        if (token.All(c => c == '_'))
            return false;
        return true;
    }
}
