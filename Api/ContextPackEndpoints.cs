using Llens.Caching;
using Llens.Models;
using Llens.Observability;
using System.Diagnostics;

namespace Llens.Api;

public static class ContextPackEndpoints
{
    public static void MapContextPackRoutes(this WebApplication app)
    {
        app.MapPost("/api/context-pack", (
            ContextPackRequest request,
            ProjectRegistry projects,
            ICodeMapCache cache,
            QueryTelemetry telemetry,
            ILoggerFactory loggerFactory,
            CancellationToken ct)
            => HandleContextPackAsync(request, projects, cache, telemetry, loggerFactory, ct));

        app.MapPost("/api/context-pack/compact", (
            ContextPackRequest request,
            ProjectRegistry projects,
            ICodeMapCache cache,
            QueryTelemetry telemetry,
            ILoggerFactory loggerFactory,
            CancellationToken ct)
            => HandleContextPackAsync(WithPreset(request, "compact", "low"), projects, cache, telemetry, loggerFactory, ct));

        app.MapPost("/api/context-pack/balanced", (
            ContextPackRequest request,
            ProjectRegistry projects,
            ICodeMapCache cache,
            QueryTelemetry telemetry,
            ILoggerFactory loggerFactory,
            CancellationToken ct)
            => HandleContextPackAsync(WithPreset(request, "hybrid", "medium"), projects, cache, telemetry, loggerFactory, ct));

        app.MapPost("/api/context-pack/detailed", (
            ContextPackRequest request,
            ProjectRegistry projects,
            ICodeMapCache cache,
            QueryTelemetry telemetry,
            ILoggerFactory loggerFactory,
            CancellationToken ct)
            => HandleContextPackAsync(WithPreset(request, "full", "high"), projects, cache, telemetry, loggerFactory, ct));
    }

    private static ContextPackRequest WithPreset(ContextPackRequest request, string format, string budgetProfile)
        => new()
        {
            Project = request.Project,
            Mode = request.Mode,
            Format = format,
            BudgetProfile = string.IsNullOrWhiteSpace(request.BudgetProfile) ? budgetProfile : request.BudgetProfile,
            Query = request.Query,
            Seed = request.Seed,
            TokenBudget = request.TokenBudget,
            MaxItems = request.MaxItems,
            SnippetRadius = request.SnippetRadius,
            MaxSnippetChars = request.MaxSnippetChars,
            MaxSnippetLines = request.MaxSnippetLines,
            HybridSnippetLimit = request.HybridSnippetLimit
        };

    private static async Task<IResult> HandleContextPackAsync(
        ContextPackRequest request,
        ProjectRegistry projects,
        ICodeMapCache cache,
        QueryTelemetry telemetry,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var logger = loggerFactory.CreateLogger("Llens.Api.ContextPack");

        if (string.IsNullOrWhiteSpace(request.Project))
            return Results.BadRequest("'project' is required.");
        if (projects.Resolve(request.Project) is null)
            return Results.NotFound($"Project '{request.Project}' is not registered.");

        var mode = NormalizeMode(request.Mode);
        var format = NormalizeFormat(request.Format);
        var query = request.Query?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(query) && request.Seed is null)
            return Results.BadRequest("Provide at least one of: 'query' or 'seed'.");

        var tokenBudget = Math.Clamp(
            request.TokenBudget <= 0
                ? DefaultTokenBudget(format, NormalizeBudgetProfile(request.BudgetProfile))
                : request.TokenBudget,
            200,
            12000);
        var maxItems = Math.Clamp(request.MaxItems <= 0 ? 30 : request.MaxItems, 5, 120);
        var snippetRadius = Math.Clamp(request.SnippetRadius <= 0 ? 6 : request.SnippetRadius, 2, 20);
        var maxSnippetChars = Math.Clamp(request.MaxSnippetChars <= 0 ? 900 : request.MaxSnippetChars, 120, 8000);
        var maxSnippetLines = Math.Clamp(request.MaxSnippetLines <= 0 ? 18 : request.MaxSnippetLines, 2, 200);
        var hybridSnippetLimit = Math.Clamp(request.HybridSnippetLimit <= 0 ? 5 : request.HybridSnippetLimit, 1, 24);

        var tokens = Tokenize(query);
        var seedSymbolId = NormalizeSymbolSeed(request.Seed?.Id);
        var seedFilePath = NormalizeFileSeed(request.Seed?.Path ?? request.Seed?.Id);

        var symbolCandidates = new Dictionary<string, CodeSymbol>(StringComparer.OrdinalIgnoreCase);
        if (tokens.Count == 0 && !string.IsNullOrWhiteSpace(query))
            tokens.Add(query);

        foreach (var token in tokens.Take(16))
        {
            var byName = await cache.QueryByNameAsync(token, request.Project, ct);
            foreach (var s in byName.Take(160))
                symbolCandidates.TryAdd(s.Id, s);
        }

        if (!string.IsNullOrWhiteSpace(seedSymbolId))
        {
            var all = await cache.QueryByNameAsync("", request.Project, ct);
            var exact = all.FirstOrDefault(s => s.Id.Equals(seedSymbolId, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) symbolCandidates[exact.Id] = exact;
        }

        var scored = new List<(CodeSymbol Symbol, int Score, int RefCount)>(capacity: symbolCandidates.Count);
        foreach (var symbol in symbolCandidates.Values.Take(300))
        {
            var score = ScoreSymbol(symbol, query, tokens, seedSymbolId, mode);
            if (score <= 0) continue;
            var refs = 0;
            if (mode is "impact" or "bug-trace")
                refs = (await cache.QueryReferencesAsync(symbol.Id, request.Project, ct)).Take(500).Count();
            scored.Add((symbol, score + refs * (mode == "impact" ? 2 : 1), refs));
        }

        var ranked = scored
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.RefCount)
            .ThenBy(x => x.Symbol.Name, StringComparer.OrdinalIgnoreCase)
            .Take(80)
            .ToList();

        var items = new List<ContextPackItem>(capacity: maxItems);
        var knownGaps = new List<string>();
        var budgetUsed = 0;
        var counter = 1;

        if (!string.IsNullOrWhiteSpace(seedFilePath) && File.Exists(seedFilePath))
        {
            var seedContext = await cache.GetSourceContextAsync(seedFilePath, 1, Math.Min(24, snippetRadius * 2), ct);
            if (!string.IsNullOrWhiteSpace(seedContext))
            {
                var includeSeedSnippet = format != "compact";
                TryAddItem(new ContextPackItem
                {
                    Id = $"cp-{counter++}",
                    Kind = "seed-file",
                    Path = seedFilePath,
                    Title = Path.GetFileName(seedFilePath),
                    Snippet = includeSeedSnippet ? PrepareSnippet(seedContext, maxSnippetChars, maxSnippetLines) : "",
                    Score = 180
                });
            }
        }

        var includedHybridSnippets = 0;
        foreach (var (symbol, score, refCount) in ranked)
        {
            if (items.Count >= maxItems) break;
            var context = await cache.GetSourceContextAsync(symbol.FilePath, symbol.LineStart, snippetRadius, ct);
            var includeSnippet = format switch
            {
                "compact" => false,
                "hybrid" => includedHybridSnippets < hybridSnippetLimit,
                _ => true
            };
            if (includeSnippet && format == "hybrid") includedHybridSnippets++;
            TryAddItem(new ContextPackItem
            {
                Id = $"cp-{counter++}",
                Kind = "symbol",
                SymbolId = symbol.Id,
                Path = symbol.FilePath,
                Line = symbol.LineStart,
                Title = format == "compact"
                    ? BuildCompactTitle(symbol)
                    : $"{symbol.Kind}: {symbol.Name}",
                Snippet = includeSnippet
                    ? PrepareSnippet(context ?? symbol.Signature ?? symbol.Name, maxSnippetChars, maxSnippetLines)
                    : "",
                Score = score,
                ReferenceCount = refCount
            });

            if (items.Count >= maxItems || mode == "lookup") continue;

            var refs = (await cache.QueryReferencesAsync(symbol.Id, request.Project, ct))
                .Take(mode == "impact" ? 10 : 6);
            foreach (var r in refs)
            {
                if (items.Count >= maxItems) break;
                var snippet = await cache.GetSourceContextAsync(r.InFilePath, r.Line, snippetRadius, ct);
                var includeRefSnippet = format == "full";
                TryAddItem(new ContextPackItem
                {
                    Id = $"cp-{counter++}",
                    Kind = "reference",
                    SymbolId = r.SymbolId,
                    Path = r.InFilePath,
                    Line = r.Line,
                    Title = $"reference -> {symbol.Name}",
                    Snippet = includeRefSnippet
                        ? PrepareSnippet(snippet ?? r.Context, maxSnippetChars, maxSnippetLines)
                        : (format == "hybrid" ? PrepareSnippet(r.Context, 220, 2) : ""),
                    Score = Math.Max(1, score - 20)
                });
            }
        }

        if (items.Count == 0)
        {
            knownGaps.Add("No ranked symbols/snippets were found for this query.");
        }

        if ((mode is "impact" or "bug-trace") && items.All(i => i.Kind != "reference"))
        {
            knownGaps.Add("Reference coverage is partial for some languages/repositories.");
        }

        var response = new ContextPackResponse
        {
            Project = request.Project,
            Mode = mode,
            Format = format,
            Query = query,
            TokenBudget = tokenBudget,
            TokensUsed = budgetUsed,
            Confidence = ScoreConfidence(items.Count, knownGaps.Count),
            KnownGaps = knownGaps,
            Items = items,
            Summary = items.Count == 0
                ? "No context-pack items were assembled."
                : $"Assembled {items.Count} context item(s) within token budget."
        };

        telemetry.Record(
            endpoint: "/api/context-pack",
            mode: $"{response.Mode}:{response.Format}",
            project: response.Project,
            elapsedMs: sw.ElapsedMilliseconds,
            resultCount: response.Items.Count,
            isEmpty: response.Items.Count == 0,
            usedFallback: false,
            estimatedTokens: response.TokensUsed);

        logger.LogInformation("context-pack project={Project} mode={Mode} items={Count} tokens={Tokens}/{Budget} latencyMs={Latency}",
            response.Project, $"{response.Mode}:{response.Format}", response.Items.Count, response.TokensUsed, response.TokenBudget, sw.ElapsedMilliseconds);

        return Results.Ok(response);

        void TryAddItem(ContextPackItem item)
        {
            if (items.Any(i =>
                    i.Kind == item.Kind
                    && string.Equals(i.SymbolId, item.SymbolId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(i.Path, item.Path, StringComparison.OrdinalIgnoreCase)
                    && i.Line == item.Line))
                return;

            item.TokenEstimate = EstimateTokens(item.Title, item.Snippet);
            if (budgetUsed + item.TokenEstimate > tokenBudget)
                return;

            budgetUsed += item.TokenEstimate;
            items.Add(item);
        }
    }

    private static string NormalizeMode(string? mode)
        => (mode ?? "lookup").Trim().ToLowerInvariant() switch
        {
            "impact" => "impact",
            "implementation" => "implementation",
            "bug-trace" => "bug-trace",
            _ => "lookup"
        };

    private static string NormalizeFormat(string? format)
        => (format ?? "full").Trim().ToLowerInvariant() switch
        {
            "compact" => "compact",
            "hybrid" => "hybrid",
            _ => "full"
        };

    private static string NormalizeBudgetProfile(string? profile)
        => (profile ?? "").Trim().ToLowerInvariant() switch
        {
            "low" => "low",
            "medium" => "medium",
            "high" => "high",
            _ => ""
        };

    private static int DefaultTokenBudget(string format, string profile)
    {
        if (profile == "low") return 600;
        if (profile == "medium") return 1200;
        if (profile == "high") return 2200;
        return format switch
        {
            "compact" => 600,
            "hybrid" => 1200,
            _ => 2200
        };
    }

    private static int ScoreSymbol(CodeSymbol symbol, string query, IReadOnlyList<string> tokens, string? seedSymbolId, string mode)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(seedSymbolId) && symbol.Id.Equals(seedSymbolId, StringComparison.OrdinalIgnoreCase))
            score += 400;

        if (!string.IsNullOrWhiteSpace(query))
        {
            if (symbol.Name.Equals(query, StringComparison.OrdinalIgnoreCase)) score += 140;
            else if (symbol.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) score += 80;
            else if (symbol.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) score += 48;
        }

        foreach (var token in tokens)
        {
            if (symbol.Name.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 16;
            if (!string.IsNullOrWhiteSpace(symbol.Signature) && symbol.Signature.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 8;
            if (symbol.FilePath.Contains(token, StringComparison.OrdinalIgnoreCase)) score += 5;
        }

        if (mode == "implementation" && symbol.Kind is SymbolKind.Method or SymbolKind.Function or SymbolKind.Class or SymbolKind.Struct)
            score += 12;
        if (mode == "bug-trace" && symbol.Kind is SymbolKind.Method or SymbolKind.Function)
            score += 10;
        if (mode == "impact" && symbol.Kind is SymbolKind.Class or SymbolKind.Struct or SymbolKind.Interface or SymbolKind.Trait)
            score += 6;

        return score;
    }

    private static int EstimateTokens(string? title, string? snippet)
        => Math.Max(1, ((title?.Length ?? 0) + (snippet?.Length ?? 0)) / 4);

    private static string BuildCompactTitle(CodeSymbol symbol)
    {
        var sig = string.IsNullOrWhiteSpace(symbol.Signature)
            ? ""
            : $" :: {symbol.Signature}";
        if (sig.Length > 120) sig = sig[..120];
        return $"{symbol.Kind}: {symbol.Name}{sig}";
    }

    private static string PrepareSnippet(string? snippet, int maxChars, int maxLines)
    {
        if (string.IsNullOrWhiteSpace(snippet)) return "";
        var normalized = snippet.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        if (lines.Length > maxLines)
            normalized = string.Join('\n', lines.Take(maxLines));
        if (normalized.Length > maxChars)
            normalized = normalized[..maxChars];
        return normalized;
    }

    private static string ScoreConfidence(int itemCount, int gapCount)
    {
        if (itemCount == 0) return "low";
        if (itemCount >= 12 && gapCount == 0) return "high";
        return "medium";
    }

    private static string? NormalizeSymbolSeed(string? seedId)
    {
        if (string.IsNullOrWhiteSpace(seedId)) return null;
        return seedId.StartsWith("symbol:", StringComparison.OrdinalIgnoreCase)
            ? seedId["symbol:".Length..]
            : seedId;
    }

    private static string? NormalizeFileSeed(string? seedPath)
    {
        if (string.IsNullOrWhiteSpace(seedPath)) return null;
        return seedPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            ? seedPath["file:".Length..]
            : seedPath;
    }

    private static List<string> Tokenize(string query)
        => query
            .Split([' ', '\t', '\r', '\n', '.', ':', '/', '\\', '-', '_', '(', ')', '[', ']', '{', '}', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

public class ContextPackRequest
{
    public string Project { get; init; } = "";
    public string Mode { get; init; } = "lookup"; // lookup | impact | implementation | bug-trace
    public string Format { get; init; } = "full"; // compact | hybrid | full
    public string? BudgetProfile { get; init; } // low | medium | high
    public string? Query { get; init; }
    public GraphSeed? Seed { get; init; }
    public int TokenBudget { get; init; }
    public int MaxItems { get; init; } = 30;
    public int SnippetRadius { get; init; } = 6;
    public int MaxSnippetChars { get; init; } = 900;
    public int MaxSnippetLines { get; init; } = 18;
    public int HybridSnippetLimit { get; init; } = 5;
}

public class ContextPackResponse
{
    public string Project { get; set; } = "";
    public string Mode { get; set; } = "lookup";
    public string Format { get; set; } = "full";
    public string Query { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Confidence { get; set; } = "low";
    public int TokenBudget { get; set; }
    public int TokensUsed { get; set; }
    public List<string> KnownGaps { get; set; } = [];
    public List<ContextPackItem> Items { get; set; } = [];
}

public class ContextPackItem
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = ""; // seed-file | symbol | reference
    public string? SymbolId { get; set; }
    public string? Path { get; set; }
    public int Line { get; set; }
    public string Title { get; set; } = "";
    public string Snippet { get; set; } = "";
    public int Score { get; set; }
    public int ReferenceCount { get; set; }
    public int TokenEstimate { get; set; }
}
