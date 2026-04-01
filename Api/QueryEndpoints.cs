using Llens.Caching;
using Llens.Models;
using Llens.Observability;
using System.Diagnostics;

namespace Llens.Api;

public static class QueryEndpoints
{
    public static void MapQueryRoutes(this WebApplication app)
    {
        app.MapPost("/api/query", async (QueryRequest request, ProjectRegistry projects, ICodeMapCache cache, QueryTelemetry telemetry, CancellationToken ct) =>
        {
            var sw = Stopwatch.StartNew();
            if (string.IsNullOrWhiteSpace(request.Project) || string.IsNullOrWhiteSpace(request.Query))
                return Results.BadRequest("Both 'project' and 'query' are required.");

            if (projects.Resolve(request.Project) is null)
                return Results.NotFound($"Project '{request.Project}' is not registered.");

            var mode = request.Mode.Trim().ToLowerInvariant();
            var limit = Math.Clamp(request.Limit <= 0 ? 20 : request.Limit, 1, 50);
            var radius = Math.Clamp(request.SnippetRadius <= 0 ? 6 : request.SnippetRadius, 1, 20);
            var kindFilter = ParseKindFilter(request.Filters?.Kind);
            if (kindFilter.Error is not null)
                return Results.BadRequest(kindFilter.Error);

            var pathPrefix = request.Filters?.PathPrefix?.Trim();
            var query = request.Query.Trim();
            if (mode is not ("snippet" or "fuzzy" or "references"))
                return Results.BadRequest("Unsupported mode. Use one of: snippet, fuzzy, references.");

            QueryResponse response = mode switch
            {
                "snippet" => await RunSnippetModeAsync(request.Project, query, pathPrefix, limit, radius, cache, ct),
                "fuzzy" => await RunFuzzyModeAsync(request.Project, query, kindFilter.Kinds, pathPrefix, limit, radius, cache, ct),
                "references" => await RunReferencesModeAsync(request.Project, query, kindFilter.Kinds, pathPrefix, limit, radius, cache, ct),
                _ => throw new UnreachableException()
            };

            response.ResultCount = CountResults(response);
            telemetry.Record(
                endpoint: "/api/query",
                mode: response.Mode,
                project: response.Project,
                elapsedMs: sw.ElapsedMilliseconds,
                resultCount: response.ResultCount,
                isEmpty: response.ResultCount == 0,
                usedFallback: response.FallbackUsed,
                estimatedTokens: EstimateTokens(response));
            return Results.Ok(response);
        });
    }

    private static async Task<QueryResponse> RunSnippetModeAsync(
        string project,
        string query,
        string? pathPrefix,
        int limit,
        int radius,
        ICodeMapCache cache,
        CancellationToken ct)
    {
        var files = await cache.GetAllFilesAsync(project, ct);
        var snippets = new List<QuerySnippetMatch>(capacity: Math.Min(limit, 32));

        foreach (var file in files.Where(f => PathMatches(f.FilePath, pathPrefix)))
        {
            if (!File.Exists(file.FilePath))
                continue;

            var lines = await File.ReadAllLinesAsync(file.FilePath, ct);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!ContainsIgnoreCase(lines[i], query))
                    continue;

                var from = Math.Max(1, i + 1 - radius);
                var to = Math.Min(lines.Length, i + 1 + radius);
                snippets.Add(new QuerySnippetMatch
                {
                    FilePath = file.FilePath,
                    MatchLine = i + 1,
                    FromLine = from,
                    ToLine = to,
                    Context = string.Join('\n', lines[(from - 1)..to])
                });

                if (snippets.Count >= limit)
                    break;
            }

            if (snippets.Count >= limit)
                break;
        }

        return new QueryResponse
        {
            Mode = "snippet",
            Project = project,
            Query = query,
            Summary = snippets.Count == 0
                ? "No matching code portions were found."
                : $"Found {snippets.Count} snippet match(es).",
            Confidence = snippets.Count == 0 ? "low" : "high",
            Snippets = snippets,
            NextSuggestions = snippets.Count == 0
                ? ["Try mode='fuzzy' or reduce the query to fewer tokens."]
                : []
        };
    }

    private static async Task<QueryResponse> RunFuzzyModeAsync(
        string project,
        string query,
        HashSet<SymbolKind>? kinds,
        string? pathPrefix,
        int limit,
        int radius,
        ICodeMapCache cache,
        CancellationToken ct)
    {
        var tokens = Tokenize(query);
        if (tokens.Count == 0)
            return Empty("fuzzy", project, query, "No fuzzy tokens provided.");

        var scored = new Dictionary<string, (CodeSymbol Symbol, int Score)>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in tokens)
        {
            var candidates = await cache.QueryByNameAsync(token, project, ct);
            foreach (var symbol in candidates)
            {
                if (!PassesSymbolFilters(symbol, kinds, pathPrefix))
                    continue;

                var score = ScoreSymbol(symbol, query, tokens);
                if (score <= 0)
                    continue;

                if (!scored.TryGetValue(symbol.Id, out var prev) || score > prev.Score)
                    scored[symbol.Id] = (symbol, score);
            }
        }

        var best = scored.Values
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Symbol.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();

        var symbols = best.Select(x => new QuerySymbolMatch
        {
            Id = x.Symbol.Id,
            Name = x.Symbol.Name,
            Kind = x.Symbol.Kind.ToString(),
            FilePath = x.Symbol.FilePath,
            LineStart = x.Symbol.LineStart,
            LineEnd = x.Symbol.LineEnd,
            Signature = x.Symbol.Signature,
            Score = x.Score
        }).ToList();

        var snippets = new List<QuerySnippetMatch>();
        foreach (var symbol in best.Take(Math.Min(10, best.Count)))
        {
            var context = await cache.GetSourceContextAsync(symbol.Symbol.FilePath, symbol.Symbol.LineStart, radius, ct);
            if (context is null)
                continue;

            snippets.Add(new QuerySnippetMatch
            {
                FilePath = symbol.Symbol.FilePath,
                MatchLine = symbol.Symbol.LineStart,
                FromLine = Math.Max(1, symbol.Symbol.LineStart - radius),
                ToLine = symbol.Symbol.LineStart + radius,
                Context = context
            });
        }

        if (symbols.Count == 0)
        {
            var fallback = await RunSnippetModeAsync(project, query, pathPrefix, limit, radius, cache, ct);
            fallback.Mode = "fuzzy";
            fallback.Summary = "No symbol-level fuzzy matches found; returning snippet fallback.";
            fallback.FallbackUsed = true;
            return fallback;
        }

        return new QueryResponse
        {
            Mode = "fuzzy",
            Project = project,
            Query = query,
            Summary = $"Found {symbols.Count} fuzzy symbol match(es).",
            Confidence = symbols.Count >= 3 ? "high" : "medium",
            Symbols = symbols,
            Snippets = snippets,
            NextSuggestions = ["Try mode='references' with the top symbol name to explore usages."]
        };
    }

    private static async Task<QueryResponse> RunReferencesModeAsync(
        string project,
        string query,
        HashSet<SymbolKind>? kinds,
        string? pathPrefix,
        int limit,
        int radius,
        ICodeMapCache cache,
        CancellationToken ct)
    {
        var candidates = await cache.QueryByNameAsync(query, project, ct);
        var ranked = candidates
            .Where(s => PassesSymbolFilters(s, kinds, pathPrefix))
            .Select(s => (Symbol: s, Score: ScoreSymbol(s, query, Tokenize(query))))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Symbol.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ranked.Count == 0)
            return Empty("references", project, query, "No matching symbol found for reference lookup.");

        var selected = ranked[0].Symbol;
        var refs = (await cache.QueryReferencesAsync(selected.Id, project, ct))
            .Take(limit)
            .ToList();

        var refMatches = refs.Select(r => new QueryReferenceMatch
        {
            SymbolId = r.SymbolId,
            InFilePath = r.InFilePath,
            Line = r.Line,
            Context = r.Context
        }).ToList();

        var snippets = new List<QuerySnippetMatch>();
        foreach (var r in refs.Take(Math.Min(10, refs.Count)))
        {
            var ctx = await cache.GetSourceContextAsync(r.InFilePath, r.Line, radius, ct);
            if (ctx is null)
                continue;

            snippets.Add(new QuerySnippetMatch
            {
                FilePath = r.InFilePath,
                MatchLine = r.Line,
                FromLine = Math.Max(1, r.Line - radius),
                ToLine = r.Line + radius,
                Context = ctx
            });
        }

        return new QueryResponse
        {
            Mode = "references",
            Project = project,
            Query = query,
            Summary = refs.Count == 0
                ? $"Resolved symbol '{selected.Name}', but no references are indexed yet."
                : $"Resolved '{selected.Name}' with {refs.Count} reference(s).",
            Confidence = refs.Count == 0 ? "medium" : "high",
            SelectedSymbol = new QuerySymbolMatch
            {
                Id = selected.Id,
                Name = selected.Name,
                Kind = selected.Kind.ToString(),
                FilePath = selected.FilePath,
                LineStart = selected.LineStart,
                LineEnd = selected.LineEnd,
                Signature = selected.Signature,
                Score = ranked[0].Score
            },
            Symbols = ranked.Take(Math.Min(10, ranked.Count)).Select(x => new QuerySymbolMatch
            {
                Id = x.Symbol.Id,
                Name = x.Symbol.Name,
                Kind = x.Symbol.Kind.ToString(),
                FilePath = x.Symbol.FilePath,
                LineStart = x.Symbol.LineStart,
                LineEnd = x.Symbol.LineEnd,
                Signature = x.Symbol.Signature,
                Score = x.Score
            }).ToList(),
            References = refMatches,
            Snippets = snippets,
            NextSuggestions = refs.Count == 0
                ? ["Reference tracking is not populated yet. Use mode='snippet' for usage-like text matches."]
                : []
        };
    }

    private static (HashSet<SymbolKind>? Kinds, string? Error) ParseKindFilter(IEnumerable<string>? rawKinds)
    {
        if (rawKinds is null)
            return (null, null);

        var kinds = new HashSet<SymbolKind>();
        foreach (var raw in rawKinds)
        {
            if (!Enum.TryParse<SymbolKind>(raw, true, out var parsed))
                return (null, $"Unknown kind '{raw}'.");
            kinds.Add(parsed);
        }

        return (kinds.Count == 0 ? null : kinds, null);
    }

    private static bool PassesSymbolFilters(CodeSymbol symbol, HashSet<SymbolKind>? kinds, string? pathPrefix)
        => (kinds is null || kinds.Contains(symbol.Kind)) && PathMatches(symbol.FilePath, pathPrefix);

    private static bool PathMatches(string filePath, string? pathPrefix)
        => string.IsNullOrWhiteSpace(pathPrefix)
           || filePath.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase)
           || filePath.Contains(pathPrefix, StringComparison.OrdinalIgnoreCase);

    private static int ScoreSymbol(CodeSymbol symbol, string query, IReadOnlyList<string> tokens)
    {
        var score = 0;

        if (symbol.Name.Equals(query, StringComparison.OrdinalIgnoreCase)) score += 100;
        else if (symbol.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) score += 70;
        else if (symbol.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) score += 50;

        foreach (var token in tokens)
        {
            if (symbol.Name.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 12;
            if (!string.IsNullOrEmpty(symbol.Signature) && symbol.Signature.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 6;
            if (symbol.FilePath.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 4;
        }

        return score;
    }

    private static List<string> Tokenize(string query)
        => query
            .Split([' ', '\t', '\r', '\n', '.', ':', '/', '\\', '-', '_', '(', ')', '[', ']', '{', '}', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool ContainsIgnoreCase(string source, string query)
        => source.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static QueryResponse Empty(string mode, string project, string query, string summary)
        => new()
        {
            Mode = mode,
            Project = project,
            Query = query,
            Summary = summary,
            Confidence = "low",
            NextSuggestions = ["Try a shorter query or switch mode."]
        };

    private static int CountResults(QueryResponse response)
        => response.Symbols.Count
            + response.References.Count
            + response.Snippets.Count
            + (response.SelectedSymbol is null ? 0 : 1);

    private static int EstimateTokens(QueryResponse response)
    {
        var chars = (response.Summary?.Length ?? 0)
                    + response.Symbols.Sum(s => (s.Name?.Length ?? 0) + (s.Signature?.Length ?? 0))
                    + response.References.Sum(r => r.Context?.Length ?? 0)
                    + response.Snippets.Sum(s => s.Context?.Length ?? 0);
        return Math.Max(1, chars / 4);
    }
}

public class QueryRequest
{
    public string Project { get; init; } = "";
    public string Mode { get; init; } = "fuzzy";
    public string Query { get; init; } = "";
    public QueryFilters? Filters { get; init; }
    public int Limit { get; init; } = 20;
    public int SnippetRadius { get; init; } = 6;
}

public class QueryFilters
{
    public string[]? Kind { get; init; }
    public string? PathPrefix { get; init; }
}

public class QueryResponse
{
    public string Mode { get; set; } = "";
    public string Project { get; set; } = "";
    public string Query { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Confidence { get; set; } = "low";
    public QuerySymbolMatch? SelectedSymbol { get; set; }
    public List<QuerySymbolMatch> Symbols { get; set; } = [];
    public List<QueryReferenceMatch> References { get; set; } = [];
    public List<QuerySnippetMatch> Snippets { get; set; } = [];
    public List<string> NextSuggestions { get; set; } = [];
    public bool FallbackUsed { get; set; }
    public int ResultCount { get; set; }
}

public class QuerySymbolMatch
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string FilePath { get; set; } = "";
    public int LineStart { get; set; }
    public int LineEnd { get; set; }
    public string? Signature { get; set; }
    public int Score { get; set; }
}

public class QueryReferenceMatch
{
    public string SymbolId { get; set; } = "";
    public string InFilePath { get; set; } = "";
    public int Line { get; set; }
    public string Context { get; set; } = "";
}

public class QuerySnippetMatch
{
    public string FilePath { get; set; } = "";
    public int MatchLine { get; set; }
    public int FromLine { get; set; }
    public int ToLine { get; set; }
    public string Context { get; set; } = "";
}
