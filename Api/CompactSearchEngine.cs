using Llens.Caching;
using Llens.Models;

namespace Llens.Api;

internal static class CompactSearchEngine
{
    public static async Task<List<CompactItem>> RunExactAsync(ICodeMapCache cache, string project, string q, int limit, CancellationToken ct)
    {
        var hits = await cache.QueryByNameAsync(q, project, ct);
        return [.. hits
            .Where(s => s.Name.Equals(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(ToExactItem)];
    }

    public static async Task<List<CompactItem>> RunFuzzyAsync(ICodeMapCache cache, string project, string q, int limit, CancellationToken ct)
    {
        var tokens = Tokenize(q);
        if (tokens.Count == 0) tokens = [q];

        var scored = new Dictionary<string, (CodeSymbol S, int Score)>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in tokens.Take(12))
        {
            var hits = await cache.QueryByNameAsync(token, project, ct);
            foreach (var symbol in hits.Take(200))
            {
                var score = ScoreSymbol(symbol, q, tokens);
                if (score <= 0) continue;
                if (!scored.TryGetValue(symbol.Id, out var prev) || score > prev.Score)
                    scored[symbol.Id] = (symbol, score);
            }
        }

        return [.. scored.Values
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.S.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(x => new CompactItem(
                Id: $"symbol:{x.S.Id}",
                T: "s",
                N: x.S.Name,
                P: x.S.FilePath,
                L: x.S.LineStart,
                K: x.S.Kind.ToString(),
                Sc: x.Score))];
    }

    public static async Task<List<CompactItem>> RunSnippetAsync(ICodeMapCache cache, string project, string q, int limit, CancellationToken ct)
    {
        var files = await cache.GetAllFilesAsync(project, ct);
        var items = new List<CompactItem>(capacity: limit);
        foreach (var file in files)
        {
            if (!File.Exists(file.FilePath)) continue;
            var lines = await File.ReadAllLinesAsync(file.FilePath, ct);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
                items.Add(new CompactItem(
                    Id: $"file:{file.FilePath}:{i + 1}",
                    T: "m",
                    N: Path.GetFileName(file.FilePath),
                    P: file.FilePath,
                    L: i + 1,
                    K: file.Language,
                    Sc: 1));
                if (items.Count >= limit) return items;
            }
        }

        return items;
    }

    public static async Task<List<CompactItem>> RunReferencesAsync(ICodeMapCache cache, string project, string q, int limit, CancellationToken ct)
    {
        var symbols = await cache.QueryByNameAsync(q, project, ct);
        var selected = symbols
            .OrderByDescending(s => ScoreSymbol(s, q, Tokenize(q)))
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (selected is null) return [];

        var refs = await cache.QueryReferencesAsync(selected.Id, project, ct);
        return [.. refs.Take(limit).Select(r => new CompactItem(
            Id: $"ref:{selected.Id}:{r.InFilePath}:{r.Line}",
            T: "r",
            N: selected.Name,
            P: r.InFilePath,
            L: r.Line,
            K: "ref",
            Sc: 1))];
    }

    public static async Task<(string Stage, List<CompactItem> Items)> ResolveBestEffortAsync(ICodeMapCache cache, string project, string query, int limit, CancellationToken ct)
    {
        var stage = "exact";
        var items = await RunExactAsync(cache, project, query, limit, ct);
        if (items.Count == 0)
        {
            stage = "fuzzy";
            items = await RunFuzzyAsync(cache, project, query, limit, ct);
        }

        if (items.Count == 0)
        {
            stage = "snippet";
            items = await RunSnippetAsync(cache, project, query, limit, ct);
        }

        return (stage, items);
    }

    public static async Task<List<CompactItem>> ExpandWithReferencesForImpactAsync(
        ICodeMapCache cache,
        string project,
        string query,
        int maxItems,
        string mode,
        CancellationToken ct)
    {
        var items = await RunFuzzyAsync(cache, project, query, maxItems, ct);
        if (mode is not ("references" or "impact"))
            return items;

        var refs = await RunReferencesAsync(cache, project, query, maxItems, ct);
        foreach (var candidate in refs)
        {
            if (items.Any(i => i.Id == candidate.Id && i.P == candidate.P && i.L == candidate.L)) continue;
            items.Add(candidate);
            if (items.Count >= maxItems) break;
        }

        return items;
    }

    public static List<string> Tokenize(string query)
        => query
            .Split([' ', '\t', '\r', '\n', '.', ':', '/', '\\', '-', '_', '(', ')', '[', ']', '{', '}', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static int ScoreSymbol(CodeSymbol symbol, string query, IReadOnlyList<string> tokens)
    {
        var score = 0;
        if (symbol.Name.Equals(query, StringComparison.OrdinalIgnoreCase)) score += 100;
        else if (symbol.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) score += 70;
        else if (symbol.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) score += 50;

        foreach (var token in tokens)
        {
            if (symbol.Name.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 12;
            if (!string.IsNullOrWhiteSpace(symbol.Signature) && symbol.Signature.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 6;
            if (symbol.FilePath.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 3;
        }

        return score;
    }

    public static int EstimateCompactTokens(IEnumerable<CompactItem> items)
        => Math.Max(1, items.Sum(i =>
            (i.Id?.Length ?? 0) + (i.N?.Length ?? 0) + (i.P?.Length ?? 0) + (i.K?.Length ?? 0)) / 4);

    public static string? NormalizeSymbolSeed(string? seedId)
    {
        if (string.IsNullOrWhiteSpace(seedId)) return null;
        return seedId.StartsWith("symbol:", StringComparison.OrdinalIgnoreCase)
            ? seedId["symbol:".Length..]
            : seedId;
    }

    public static string? NormalizeFileSeed(string? seedPath)
    {
        if (string.IsNullOrWhiteSpace(seedPath)) return null;
        return seedPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            ? seedPath["file:".Length..]
            : seedPath;
    }

    private static CompactItem ToExactItem(CodeSymbol symbol)
        => new(
            Id: $"symbol:{symbol.Id}",
            T: "s",
            N: symbol.Name,
            P: symbol.FilePath,
            L: symbol.LineStart,
            K: symbol.Kind.ToString(),
            Sc: 200);
}
