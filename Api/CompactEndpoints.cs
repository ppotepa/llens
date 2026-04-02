using Llens.Caching;
using Llens.Models;
using Llens.Observability;
using System.Diagnostics;

namespace Llens.Api;

public static class CompactEndpoints
{
    public static void MapCompactRoutes(this WebApplication app)
    {
        var g = app.MapGroup("/api/compact");

        g.MapPost("/query", async (
            CompactQueryRequest request,
            ProjectRegistry projects,
            ICodeMapCache cache,
            QueryTelemetry telemetry,
            CancellationToken ct) =>
        {
            var sw = Stopwatch.StartNew();
            if (string.IsNullOrWhiteSpace(request.Project) || string.IsNullOrWhiteSpace(request.Q))
                return Results.BadRequest("'project' and 'q' are required.");
            if (projects.Resolve(request.Project) is null)
                return Results.NotFound($"Project '{request.Project}' is not registered.");

            var mode = NormalizeMode(request.Mode);
            var limit = Math.Clamp(request.Limit <= 0 ? 12 : request.Limit, 1, 40);
            var q = request.Q.Trim();
            var items = mode switch
            {
                "snippet" => await CompactSearchEngine.RunSnippetAsync(cache, request.Project, q, limit, ct),
                "references" => await CompactSearchEngine.RunReferencesAsync(cache, request.Project, q, limit, ct),
                _ => await CompactSearchEngine.RunFuzzyAsync(cache, request.Project, q, limit, ct)
            };

            var response = new CompactQueryResponse
            {
                Project = request.Project,
                Mode = mode,
                Q = q,
                Items = items,
                Count = items.Count,
                Tokens = CompactSearchEngine.EstimateCompactTokens(items)
            };

            telemetry.Record(
                endpoint: "/api/compact/query",
                mode,
                project: request.Project,
                elapsedMs: sw.ElapsedMilliseconds,
                resultCount: items.Count,
                isEmpty: items.Count == 0,
                usedFallback: false,
                estimatedTokens: response.Tokens);

            if (IsTupleFormat(request.Format))
                return Results.Ok(CompactTupleCodec.FromQuery(response));
            return Results.Ok(response);
        });

        g.MapPost("/resolve", async (
            CompactResolveRequest request,
            ProjectRegistry projects,
            ICodeMapCache cache,
            QueryTelemetry telemetry,
            CancellationToken ct) =>
        {
            var sw = Stopwatch.StartNew();
            if (string.IsNullOrWhiteSpace(request.Project) || string.IsNullOrWhiteSpace(request.Q))
                return Results.BadRequest("'project' and 'q' are required.");
            if (projects.Resolve(request.Project) is null)
                return Results.NotFound($"Project '{request.Project}' is not registered.");

            var q = request.Q.Trim();
            var limit = Math.Clamp(request.Limit <= 0 ? 12 : request.Limit, 1, 40);
            var (stage, items) = await CompactSearchEngine.ResolveBestEffortAsync(cache, request.Project, q, limit, ct);

            var response = new CompactResolveResponse
            {
                Project = request.Project,
                Q = q,
                Stage = stage,
                Count = items.Count,
                Tokens = CompactSearchEngine.EstimateCompactTokens(items),
                Items = items
            };

            telemetry.Record(
                endpoint: "/api/compact/resolve",
                mode: $"resolve:{stage}",
                project: request.Project,
                elapsedMs: sw.ElapsedMilliseconds,
                resultCount: items.Count,
                isEmpty: items.Count == 0,
                usedFallback: stage != "exact",
                estimatedTokens: response.Tokens);

            return Results.Ok(response);
        });

        g.MapPost("/context-pack", async (
            CompactContextPackRequest request,
            ProjectRegistry projects,
            ICodeMapCache cache,
            QueryTelemetry telemetry,
            CancellationToken ct) =>
        {
            var sw = Stopwatch.StartNew();
            if (string.IsNullOrWhiteSpace(request.Project))
                return Results.BadRequest("'project' is required.");
            if (projects.Resolve(request.Project) is null)
                return Results.NotFound($"Project '{request.Project}' is not registered.");

            var q = request.Q?.Trim() ?? "";
            var tokenBudget = Math.Clamp(request.TokenBudget <= 0 ? 600 : request.TokenBudget, 200, 4000);
            var maxItems = Math.Clamp(request.MaxItems <= 0 ? 30 : request.MaxItems, 5, 80);
            var mode = NormalizeMode(request.Mode);
            var previousIds = request.PreviousIds?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

            var items = await CompactSearchEngine.ExpandWithReferencesForImpactAsync(
                cache,
                request.Project,
                q,
                maxItems,
                mode,
                ct);

            var packed = new List<CompactItem>(capacity: Math.Min(maxItems, items.Count));
            var used = 0;
            foreach (var item in items)
            {
                if (previousIds.Contains(item.Id)) continue;
                var est = CompactSearchEngine.EstimateCompactTokens([item]);
                if (used + est > tokenBudget) break;
                used += est;
                packed.Add(item);
                if (packed.Count >= maxItems) break;
            }

            var response = new CompactContextPackResponse
            {
                Project = request.Project,
                Mode = mode,
                Q = q,
                TokenBudget = tokenBudget,
                Tokens = used,
                Count = packed.Count,
                Items = packed
            };

            telemetry.Record(
                endpoint: "/api/compact/context-pack",
                mode,
                project: request.Project,
                elapsedMs: sw.ElapsedMilliseconds,
                resultCount: packed.Count,
                isEmpty: packed.Count == 0,
                usedFallback: false,
                estimatedTokens: used);

            if (IsTupleFormat(request.Format))
                return Results.Ok(CompactTupleCodec.FromContextPack(response));
            return Results.Ok(response);
        });

        g.MapPost("/graph", async (
            CompactGraphRequest request,
            ProjectRegistry projects,
            ICodeMapCache cache,
            QueryTelemetry telemetry,
            CancellationToken ct) =>
        {
            var sw = Stopwatch.StartNew();
            if (string.IsNullOrWhiteSpace(request.Project) || request.Seed is null)
                return Results.BadRequest("'project' and 'seed' are required.");
            if (projects.Resolve(request.Project) is null)
                return Results.NotFound($"Project '{request.Project}' is not registered.");

            var maxNodes = Math.Clamp(request.MaxNodes <= 0 ? 120 : request.MaxNodes, 10, 300);
            var depth = Math.Clamp(request.Depth <= 0 ? 1 : request.Depth, 1, 3);
            var seed = await ResolveSeedAsync(cache, request.Project, request.Seed, ct);
            if (seed is null) return Results.NotFound("Seed not found.");

            var nodes = new Dictionary<string, CompactGraphNode>(StringComparer.OrdinalIgnoreCase)
            {
                [seed.Id] = seed with { D = 0 }
            };
            var edges = new Dictionary<string, CompactGraphEdge>(StringComparer.OrdinalIgnoreCase);
            var q = new Queue<(CompactGraphNode N, int D)>();
            q.Enqueue((seed, 0));

            while (q.Count > 0 && nodes.Count < maxNodes)
            {
                var (cur, d) = q.Dequeue();
                if (d >= depth) continue;

                foreach (var rel in await ExpandNeighborsAsync(cache, request.Project, cur, ct))
                {
                    if (!nodes.ContainsKey(rel.To.Id) && nodes.Count >= maxNodes) continue;
                    if (!nodes.ContainsKey(rel.To.Id))
                    {
                        nodes[rel.To.Id] = rel.To with { D = d + 1 };
                        q.Enqueue((nodes[rel.To.Id], d + 1));
                    }

                    var key = $"{rel.From.Id}|{rel.To.Id}|{rel.E}";
                    edges[key] = new CompactGraphEdge(rel.From.Id, rel.To.Id, rel.E);
                }
            }

            var response = new CompactGraphResponse
            {
                Project = request.Project,
                Seed = seed.Id,
                Nodes = [.. nodes.Values.OrderBy(x => x.D).ThenBy(x => x.L, StringComparer.OrdinalIgnoreCase)],
                Edges = [.. edges.Values]
            };

            telemetry.Record(
                endpoint: "/api/compact/graph",
                mode: "graph",
                project: request.Project,
                elapsedMs: sw.ElapsedMilliseconds,
                resultCount: response.Nodes.Count + response.Edges.Count,
                isEmpty: response.Nodes.Count == 0,
                usedFallback: false,
                estimatedTokens: EstimateCompactGraphTokens(response));

            return Results.Ok(response);
        });

        g.MapPost("/references-tree", async (
            CompactReferencesTreeRequest request,
            ProjectRegistry projects,
            ICodeMapCache cache,
            QueryTelemetry telemetry,
            CancellationToken ct) =>
        {
            var sw = Stopwatch.StartNew();
            if (string.IsNullOrWhiteSpace(request.Project) || request.Seed is null)
                return Results.BadRequest("'project' and 'seed' are required.");
            if (projects.Resolve(request.Project) is null)
                return Results.NotFound($"Project '{request.Project}' is not registered.");

            var maxNodes = Math.Clamp(request.MaxNodes <= 0 ? 180 : request.MaxNodes, 10, 400);
            var depth = Math.Clamp(request.Depth <= 0 ? 2 : request.Depth, 1, 4);
            var root = await ResolveSymbolSeedAsync(cache, request.Project, request.Seed, ct);
            if (root is null) return Results.NotFound("Symbol seed not found.");

            var nodes = new Dictionary<string, CompactGraphNode>(StringComparer.OrdinalIgnoreCase);
            var edges = new Dictionary<string, CompactGraphEdge>(StringComparer.OrdinalIgnoreCase);
            nodes[root.Id] = root;

            var queue = new Queue<(CompactGraphNode Symbol, int Depth)>();
            queue.Enqueue((root, 0));
            while (queue.Count > 0 && nodes.Count < maxNodes)
            {
                var (current, currentDepth) = queue.Dequeue();
                if (currentDepth >= depth) continue;
                if (string.IsNullOrWhiteSpace(current.Sid)) continue;

                var refs = await cache.QueryReferencesAsync(current.Sid!, request.Project, ct);
                foreach (var r in refs.Take(300))
                {
                    var caller = await ResolveEnclosingSymbolAsync(cache, r.InFilePath, r.Line, ct);
                    if (caller is null || caller.Id.Equals(current.Id, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!nodes.ContainsKey(caller.Id) && nodes.Count < maxNodes)
                    {
                        nodes[caller.Id] = caller with { D = currentDepth + 1 };
                        queue.Enqueue((nodes[caller.Id], currentDepth + 1));
                    }

                    var key = $"{caller.Id}|{current.Id}|call";
                    edges[key] = new CompactGraphEdge(caller.Id, current.Id, "call");
                }
            }

            var response = new CompactReferencesTreeResponse
            {
                Project = request.Project,
                Root = root.Id,
                Nodes = [.. nodes.Values.OrderBy(x => x.D).ThenBy(x => x.L, StringComparer.OrdinalIgnoreCase)],
                Edges = [.. edges.Values]
            };

            telemetry.Record(
                endpoint: "/api/compact/references-tree",
                mode: "references-tree",
                project: request.Project,
                elapsedMs: sw.ElapsedMilliseconds,
                resultCount: response.Nodes.Count + response.Edges.Count,
                isEmpty: response.Nodes.Count <= 1,
                usedFallback: false,
                estimatedTokens: EstimateCompactGraphTokens(new CompactGraphResponse
                {
                    Project = response.Project,
                    Seed = response.Root,
                    Nodes = response.Nodes,
                    Edges = response.Edges
                }));

            return Results.Ok(response);
        });
    }

    private static async Task<CompactGraphNode?> ResolveSeedAsync(ICodeMapCache cache, string project, GraphSeed seed, CancellationToken ct)
    {
        var type = (seed.Type ?? "").Trim().ToLowerInvariant();
        if (type == "file")
        {
            var p = CompactSearchEngine.NormalizeFileSeed(seed.Path ?? seed.Id);
            if (string.IsNullOrWhiteSpace(p)) return null;
            var full = Path.GetFullPath(p!);
            var file = await cache.GetFileNodeAsync(full, ct);
            if (file is null) return null;
            return new CompactGraphNode($"file:{full}", "f", Path.GetFileName(full), full, 0, file.Language, null);
        }

        if (type == "symbol")
            return await ResolveSymbolSeedAsync(cache, project, seed, ct);

        return null;
    }

    private static async Task<CompactGraphNode?> ResolveSymbolSeedAsync(ICodeMapCache cache, string project, GraphSeed seed, CancellationToken ct)
    {
        var symbolId = CompactSearchEngine.NormalizeSymbolSeed(seed.Id);
        if (!string.IsNullOrWhiteSpace(symbolId))
        {
            var all = await cache.QueryByNameAsync("", project, ct);
            var exact = all.FirstOrDefault(s => s.Id.Equals(symbolId, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return ToNode(exact);
        }

        var q = seed.Name ?? "";
        if (string.IsNullOrWhiteSpace(q)) return null;
        var byName = await cache.QueryByNameAsync(q, project, ct);
        var best = byName
            .OrderByDescending(s => CompactSearchEngine.ScoreSymbol(s, q, CompactSearchEngine.Tokenize(q)))
            .ThenBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.LineStart)
            .FirstOrDefault();
        return best is null ? null : ToNode(best);
    }

    private static async Task<List<(CompactGraphNode From, CompactGraphNode To, string E)>> ExpandNeighborsAsync(
        ICodeMapCache cache, string project, CompactGraphNode current, CancellationToken ct)
    {
        var list = new List<(CompactGraphNode, CompactGraphNode, string)>();
        if (current.T == "f" && !string.IsNullOrWhiteSpace(current.P))
        {
            var symbols = await cache.QueryByFileAsync(current.P!, ct);
            foreach (var s in symbols)
                list.Add((current, ToNode(s), "c"));

            var file = await cache.GetFileNodeAsync(current.P!, ct);
            if (file is not null)
            {
                foreach (var imp in file.Imports.Take(120))
                {
                    var n = await cache.GetFileNodeAsync(imp, ct);
                    if (n is null) continue;
                    var to = new CompactGraphNode($"file:{n.FilePath}", "f", Path.GetFileName(n.FilePath), n.FilePath, 0, n.Language, null);
                    list.Add((current, to, "i"));
                }
            }

            var deps = await cache.GetDependentsAsync(current.P!, project, ct);
            foreach (var dep in deps.Take(120))
            {
                var from = new CompactGraphNode($"file:{dep.FilePath}", "f", Path.GetFileName(dep.FilePath), dep.FilePath, 0, dep.Language, null);
                list.Add((from, current, "i"));
            }
        }
        else if (current.T == "s" && !string.IsNullOrWhiteSpace(current.Sid))
        {
            var refs = await cache.QueryReferencesAsync(current.Sid!, project, ct);
            foreach (var r in refs.Take(180))
            {
                var fn = await cache.GetFileNodeAsync(r.InFilePath, ct);
                if (fn is null) continue;
                var to = new CompactGraphNode($"file:{fn.FilePath}", "f", Path.GetFileName(fn.FilePath), fn.FilePath, 0, fn.Language, null);
                list.Add((current, to, "r"));
            }

            if (!string.IsNullOrWhiteSpace(current.P))
            {
                var file = await cache.GetFileNodeAsync(current.P!, ct);
                if (file is not null)
                {
                    var fnode = new CompactGraphNode($"file:{file.FilePath}", "f", Path.GetFileName(file.FilePath), file.FilePath, 0, file.Language, null);
                    list.Add((fnode, current, "c"));
                }
            }
        }

        return list;
    }

    private static async Task<CompactGraphNode?> ResolveEnclosingSymbolAsync(ICodeMapCache cache, string filePath, int line, CancellationToken ct)
    {
        var symbols = (await cache.QueryByFileAsync(filePath, ct))
            .OrderBy(s => s.LineStart)
            .ToList();
        if (symbols.Count == 0) return null;

        var containing = symbols
            .Where(s => line >= s.LineStart && (s.LineEnd <= 0 || line <= s.LineEnd))
            .OrderBy(s => s.LineEnd <= 0 ? int.MaxValue : (s.LineEnd - s.LineStart))
            .FirstOrDefault()
            ?? symbols.Where(s => s.LineStart <= line).OrderByDescending(s => s.LineStart).FirstOrDefault();

        return containing is null ? null : ToNode(containing);
    }

    private static CompactGraphNode ToNode(CodeSymbol s)
        => new(
            Id: $"symbol:{s.Id}",
            T: "s",
            L: s.Name,
            P: s.FilePath,
            D: 0,
            K: s.Kind.ToString(),
            Sid: s.Id,
            Line: s.LineStart);

    private static string NormalizeMode(string? mode)
        => (mode ?? "fuzzy").Trim().ToLowerInvariant() switch
        {
            "snippet" => "snippet",
            "references" => "references",
            "impact" => "impact",
            _ => "fuzzy"
        };

    private static bool IsTupleFormat(string? format)
        => string.Equals(format?.Trim(), "tuple", StringComparison.OrdinalIgnoreCase);

    private static int EstimateCompactGraphTokens(CompactGraphResponse response)
        => Math.Max(1, (response.Nodes.Sum(n => (n.Id?.Length ?? 0) + (n.L?.Length ?? 0) + (n.P?.Length ?? 0))
            + response.Edges.Count * 12) / 4);
}

public class CompactQueryRequest
{
    public string Project { get; init; } = "";
    public string Q { get; init; } = "";
    public string Mode { get; init; } = "fuzzy";
    public int Limit { get; init; } = 12;
    public string? Format { get; init; }
}

public class CompactContextPackRequest
{
    public string Project { get; init; } = "";
    public string? Q { get; init; }
    public string Mode { get; init; } = "fuzzy";
    public int TokenBudget { get; init; } = 600;
    public int MaxItems { get; init; } = 30;
    public string[]? PreviousIds { get; init; }
    public string? Format { get; init; }
}

public class CompactGraphRequest
{
    public string Project { get; init; } = "";
    public GraphSeed? Seed { get; init; }
    public int Depth { get; init; } = 1;
    public int MaxNodes { get; init; } = 120;
}

public class CompactReferencesTreeRequest
{
    public string Project { get; init; } = "";
    public GraphSeed? Seed { get; init; }
    public int Depth { get; init; } = 2;
    public int MaxNodes { get; init; } = 180;
}

public class CompactQueryResponse
{
    public string Project { get; set; } = "";
    public string Mode { get; set; } = "";
    public string Q { get; set; } = "";
    public int Count { get; set; }
    public int Tokens { get; set; }
    public List<CompactItem> Items { get; set; } = [];
}

public class CompactResolveRequest
{
    public string Project { get; init; } = "";
    public string Q { get; init; } = "";
    public int Limit { get; init; } = 12;
}

public class CompactResolveResponse
{
    public string Project { get; set; } = "";
    public string Q { get; set; } = "";
    public string Stage { get; set; } = "";
    public int Count { get; set; }
    public int Tokens { get; set; }
    public List<CompactItem> Items { get; set; } = [];
}

public class CompactContextPackResponse
{
    public string Project { get; set; } = "";
    public string Mode { get; set; } = "";
    public string Q { get; set; } = "";
    public int TokenBudget { get; set; }
    public int Tokens { get; set; }
    public int Count { get; set; }
    public List<CompactItem> Items { get; set; } = [];
}

public class CompactGraphResponse
{
    public string Project { get; set; } = "";
    public string Seed { get; set; } = "";
    public List<CompactGraphNode> Nodes { get; set; } = [];
    public List<CompactGraphEdge> Edges { get; set; } = [];
}

public class CompactReferencesTreeResponse
{
    public string Project { get; set; } = "";
    public string Root { get; set; } = "";
    public List<CompactGraphNode> Nodes { get; set; } = [];
    public List<CompactGraphEdge> Edges { get; set; } = [];
}

public record CompactItem(string Id, string T, string N, string P, int L, string K, int Sc);
public record CompactGraphNode(string Id, string T, string L, string? P, int D, string? K, string? Sid, int Line = 0);
public record CompactGraphEdge(string F, string T, string E);
