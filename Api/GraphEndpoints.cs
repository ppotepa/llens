using Llens.Caching;
using Llens.Models;
using Llens.Observability;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Llens.Api;

public static class GraphEndpoints
{
    private static readonly Regex CallTokenRegex = new(@"(?:(?:\b|::|\.)([_A-Za-z][_A-Za-z0-9]*))\s*\(", RegexOptions.Compiled);

    public static void MapGraphRoutes(this WebApplication app)
    {
        app.MapPost("/api/graph/query", async (GraphQueryRequest request, ProjectRegistry projects, ICodeMapCache cache, QueryTelemetry telemetry, CancellationToken ct) =>
        {
            var sw = Stopwatch.StartNew();
            if (string.IsNullOrWhiteSpace(request.Project))
                return Results.BadRequest("'project' is required.");

            if (projects.Resolve(request.Project) is null)
                return Results.NotFound($"Project '{request.Project}' is not registered.");

            if (request.Seed is null)
                return Results.BadRequest("'seed' is required.");

            var depth = Math.Clamp(request.Expand?.Depth ?? 1, 1, 4);
            var maxNodes = Math.Clamp(request.Expand?.MaxNodes ?? 120, 10, 400);
            var direction = NormalizeDirection(request.Expand?.Direction);
            var edgeTypes = ParseEdgeTypes(request.Expand?.EdgeTypes);
            if (edgeTypes.Count == 0)
                return Results.BadRequest("At least one edge type is required.");

            var result = await BuildGraphAsync(
                request.Project,
                request.Seed,
                depth,
                maxNodes,
                direction,
                edgeTypes,
                [],
                [],
                cache,
                ct);

            if (result.Seed is null)
                return Results.NotFound("Seed node was not found.");

            var response = new GraphQueryResponse
            {
                Project = request.Project,
                SeedId = result.Seed.Id,
                Nodes = result.Nodes,
                Edges = result.Edges,
            };

            response.Summary = new GraphSummary
            {
                NodeCount = response.Nodes.Count,
                EdgeCount = response.Edges.Count,
                Depth = depth
            };

            var includeSnippets = request.Include?.Snippets ?? true;
            if (includeSnippets)
                response.ContextPack = await BuildContextPackAsync(response.Nodes, request.Include?.Radius ?? 6, result.State);

            telemetry.Record(
                endpoint: "/api/graph/query",
                mode: "graph-query",
                project: request.Project,
                elapsedMs: sw.ElapsedMilliseconds,
                resultCount: response.Nodes.Count + response.Edges.Count,
                isEmpty: response.Nodes.Count == 0,
                usedFallback: false,
                estimatedTokens: EstimateGraphTokens(response.ContextPack));

            return Results.Ok(response);
        });

        app.MapPost("/api/graph/expand", async (GraphExpandRequest request, ProjectRegistry projects, ICodeMapCache cache, QueryTelemetry telemetry, CancellationToken ct) =>
        {
            var sw = Stopwatch.StartNew();
            if (string.IsNullOrWhiteSpace(request.Project))
                return Results.BadRequest("'project' is required.");
            if (projects.Resolve(request.Project) is null)
                return Results.NotFound($"Project '{request.Project}' is not registered.");
            if (request.Seed is null)
                return Results.BadRequest("'seed' is required.");

            var depth = Math.Clamp(request.Depth <= 0 ? 1 : request.Depth, 1, 2);
            var maxNodes = Math.Clamp(request.MaxNodes <= 0 ? 120 : request.MaxNodes, 10, 400);
            var direction = NormalizeDirection(request.Direction);
            var edgeTypes = ParseEdgeTypes(request.EdgeTypes);
            if (edgeTypes.Count == 0)
                return Results.BadRequest("At least one edge type is required.");

            var pageLimit = Math.Clamp(request.Page?.Limit ?? 120, 10, 400);
            var pageOffset = Math.Max(0, request.Page?.Offset ?? 0);

            var result = await BuildGraphAsync(
                request.Project,
                request.Seed,
                depth,
                maxNodes,
                direction,
                edgeTypes,
                request.ExcludeNodeIds ?? [],
                request.ExcludeEdgeIds ?? [],
                cache,
                ct);

            if (result.Seed is null)
                return Results.NotFound("Seed node was not found.");

            var pagedEdges = result.Edges.Skip(pageOffset).Take(pageLimit).ToList();
            var nodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { result.Seed.Id };
            foreach (var e in pagedEdges)
            {
                nodeIds.Add(e.From);
                nodeIds.Add(e.To);
            }

            var pagedNodes = result.Nodes.Where(n => nodeIds.Contains(n.Id)).ToList();
            var nextOffset = pageOffset + pageLimit;
            var hasMore = nextOffset < result.Edges.Count;

            var response = new GraphExpandResponse
            {
                Project = request.Project,
                SeedId = result.Seed.Id,
                Nodes = pagedNodes,
                Edges = pagedEdges,
                Page = new GraphPageResult
                {
                    Offset = pageOffset,
                    Limit = pageLimit,
                    TotalEdges = result.Edges.Count,
                    HasMore = hasMore,
                    NextOffset = hasMore ? nextOffset : null
                }
            };

            telemetry.Record(
                endpoint: "/api/graph/expand",
                mode: "graph-expand",
                project: request.Project,
                elapsedMs: sw.ElapsedMilliseconds,
                resultCount: response.Nodes.Count + response.Edges.Count,
                isEmpty: response.Nodes.Count == 0 && response.Edges.Count == 0,
                usedFallback: false,
                estimatedTokens: 0);

            return Results.Ok(response);
        });

        app.MapPost("/api/graph/collapse", async (GraphCollapseRequest request, ProjectRegistry projects, ICodeMapCache cache, QueryTelemetry telemetry, CancellationToken ct) =>
        {
            var sw = Stopwatch.StartNew();
            if (string.IsNullOrWhiteSpace(request.Project))
                return Results.BadRequest("'project' is required.");
            if (projects.Resolve(request.Project) is null)
                return Results.NotFound($"Project '{request.Project}' is not registered.");
            if (request.Seed is null)
                return Results.BadRequest("'seed' is required.");

            var depth = Math.Clamp(request.Depth <= 0 ? 1 : request.Depth, 1, 2);
            var maxNodes = Math.Clamp(request.MaxNodes <= 0 ? 120 : request.MaxNodes, 10, 400);
            var direction = NormalizeDirection(request.Direction);
            var edgeTypes = ParseEdgeTypes(request.EdgeTypes);
            if (edgeTypes.Count == 0)
                return Results.BadRequest("At least one edge type is required.");

            var result = await BuildGraphAsync(
                request.Project,
                request.Seed,
                depth,
                maxNodes,
                direction,
                edgeTypes,
                [],
                [],
                cache,
                ct);

            if (result.Seed is null)
                return Results.NotFound("Seed node was not found.");

            var removeNodeIds = result.Nodes.Select(n => n.Id).Where(id => !id.Equals(result.Seed.Id, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var removeEdgeIds = result.Edges.Select(EdgeId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var response = new GraphCollapseResponse
            {
                Project = request.Project,
                SeedId = result.Seed.Id,
                RemoveNodeIds = removeNodeIds,
                RemoveEdgeIds = removeEdgeIds
            };

            telemetry.Record(
                endpoint: "/api/graph/collapse",
                mode: "graph-collapse",
                project: request.Project,
                elapsedMs: sw.ElapsedMilliseconds,
                resultCount: response.RemoveNodeIds.Count + response.RemoveEdgeIds.Count,
                isEmpty: response.RemoveNodeIds.Count == 0 && response.RemoveEdgeIds.Count == 0,
                usedFallback: false,
                estimatedTokens: 0);

            return Results.Ok(response);
        });
    }

    private static int EstimateGraphTokens(GraphContextPack? contextPack)
    {
        if (contextPack is null) return 0;
        var chars = contextPack.TopFiles.Sum(x => x.Length)
                    + contextPack.TopSymbols.Sum(x => x.Length)
                    + contextPack.Snippets.Sum(s => s.Context.Length);
        return Math.Max(0, chars / 4);
    }

    private static async Task<GraphBuildResult> BuildGraphAsync(
        string project,
        GraphSeed seed,
        int depth,
        int maxNodes,
        string direction,
        HashSet<GraphEdgeType> edgeTypes,
        IEnumerable<string> excludeNodeIds,
        IEnumerable<string> excludeEdgeIds,
        ICodeMapCache cache,
        CancellationToken ct)
    {
        var excludedNodes = excludeNodeIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excludedEdges = excludeEdgeIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var state = new BuildState(project, cache, ct);
        var seedNode = await ResolveSeedAsync(project, seed, state);
        if (seedNode is null)
            return GraphBuildResult.Empty(state);

        if (excludedNodes.Contains(seedNode.Id))
            excludedNodes.Remove(seedNode.Id);

        state.AddNode(seedNode, distance: 0);
        var queue = new Queue<(GraphNode Node, int Distance)>();
        queue.Enqueue((seedNode, 0));

        while (queue.Count > 0 && state.Nodes.Count < maxNodes)
        {
            var (current, distance) = queue.Dequeue();
            if (distance >= depth) continue;

            var outgoing = await GetOutgoingNeighborsAsync(current, edgeTypes, state);
            foreach (var rel in outgoing)
            {
                if (!Allows(direction, outgoing: true)) continue;
                if (excludedEdges.Contains(EdgeId(rel.Edge))) continue;
                if (excludedNodes.Contains(rel.To.Id)) continue;
                if (!state.Nodes.ContainsKey(rel.To.Id) && state.Nodes.Count >= maxNodes) continue;

                var added = state.AddNode(rel.To, distance + 1);
                state.AddEdge(rel.Edge);
                if (added && state.Nodes.Count < maxNodes)
                    queue.Enqueue((rel.To, distance + 1));
            }

            var incoming = await GetIncomingNeighborsAsync(current, edgeTypes, state);
            foreach (var rel in incoming)
            {
                if (!Allows(direction, outgoing: false)) continue;
                if (excludedEdges.Contains(EdgeId(rel.Edge))) continue;
                if (excludedNodes.Contains(rel.From.Id)) continue;
                if (!state.Nodes.ContainsKey(rel.From.Id) && state.Nodes.Count >= maxNodes) continue;

                var added = state.AddNode(rel.From, distance + 1);
                state.AddEdge(rel.Edge);
                if (added && state.Nodes.Count < maxNodes)
                    queue.Enqueue((rel.From, distance + 1));
            }
        }

        var nodes = state.Nodes.Values
            .Where(n => !excludedNodes.Contains(n.Id))
            .OrderBy(n => n.Distance)
            .ThenBy(n => n.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(n => n.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var edges = state.Edges.Values
            .Where(e => !excludedEdges.Contains(EdgeId(e)))
            .OrderBy(e => e.Type, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.From, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.To, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new GraphBuildResult(state, seedNode, nodes, edges);
    }

    private static string EdgeId(GraphEdge edge)
        => $"{edge.From}|{edge.To}|{edge.Type}";

    private static async Task<GraphContextPack> BuildContextPackAsync(List<GraphNode> nodes, int radius, BuildState state)
    {
        var clampedRadius = Math.Clamp(radius, 2, 16);
        var files = nodes.Where(n => n.Type == GraphNodeType.File)
            .Select(n => n.FilePath!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();

        var symbols = nodes.Where(n => n.Type == GraphNodeType.Symbol)
            .Select(n => n.Label)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToList();

        var snippets = new List<GraphSnippet>();
        foreach (var node in nodes.Where(n => n.Type == GraphNodeType.Symbol && n.FilePath is not null && n.LineStart > 0).Take(12))
        {
            var context = await state.Cache.GetSourceContextAsync(node.FilePath!, node.LineStart, clampedRadius, state.Ct);
            if (context is null) continue;
            snippets.Add(new GraphSnippet
            {
                FilePath = node.FilePath!,
                Line = node.LineStart,
                Context = context
            });
        }

        return new GraphContextPack
        {
            TopFiles = files,
            TopSymbols = symbols,
            Snippets = snippets
        };
    }

    private static string NormalizeDirection(string? direction)
        => direction?.Trim().ToLowerInvariant() switch
        {
            "in" => "in",
            "out" => "out",
            _ => "both"
        };

    private static bool Allows(string direction, bool outgoing)
        => direction == "both" || (direction == "out" && outgoing) || (direction == "in" && !outgoing);

    private static HashSet<GraphEdgeType> ParseEdgeTypes(IEnumerable<string>? raw)
    {
        if (raw is null) return [GraphEdgeType.Contains, GraphEdgeType.Imports, GraphEdgeType.References];

        var set = new HashSet<GraphEdgeType>();
        foreach (var value in raw)
        {
            if (Enum.TryParse<GraphEdgeType>(value, true, out var parsed))
                set.Add(parsed);
        }

        return set;
    }

    private static async Task<GraphNode?> ResolveSeedAsync(string project, GraphSeed seed, BuildState state)
    {
        var type = (seed.Type ?? "").Trim().ToLowerInvariant();
        if (type == "file")
        {
            var path = seed.Path ?? seed.Id;
            if (string.IsNullOrWhiteSpace(path)) return null;
            path = NormalizeFileSeed(path);
            return await ResolveFileNodeAsync(project, path, state);
        }

        if (type == "symbol")
        {
            var queryName = string.IsNullOrWhiteSpace(seed.Name) ? "" : seed.Name!;
            var normalizedSymbolId = NormalizeSymbolSeed(seed.Id);
            if (!string.IsNullOrWhiteSpace(normalizedSymbolId))
            {
                var direct = await ResolveSymbolByIdAsync(normalizedSymbolId!, state);
                if (direct is not null) return ToSymbolNode(direct);

                var candidatesById = (await state.Cache.QueryByNameAsync(queryName, project, state.Ct)).ToList();
                var exactId = candidatesById.FirstOrDefault(s => s.Id.Equals(normalizedSymbolId, StringComparison.OrdinalIgnoreCase));
                if (exactId is not null) return ToSymbolNode(exactId);

                if (queryName.Length == 0)
                {
                    var wide = await state.Cache.QueryByNameAsync("", project, state.Ct);
                    exactId = wide.FirstOrDefault(s => s.Id.Equals(normalizedSymbolId, StringComparison.OrdinalIgnoreCase));
                    if (exactId is not null) return ToSymbolNode(exactId);
                }
            }

            var candidates = (await state.Cache.QueryByNameAsync(queryName, project, state.Ct)).ToList();

            var best = candidates
                .OrderByDescending(s => ScoreSeedCandidate(s, seed))
                .ThenBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.LineStart)
                .FirstOrDefault();
            if (best is not null) return ToSymbolNode(best);
        }

        return null;
    }

    private static int ScoreSeedCandidate(CodeSymbol symbol, GraphSeed seed)
    {
        var score = 0;
        var normalizedSeedId = NormalizeSymbolSeed(seed.Id);

        if (!string.IsNullOrWhiteSpace(normalizedSeedId) && symbol.Id.Equals(normalizedSeedId, StringComparison.OrdinalIgnoreCase)) score += 2000;
        if (!string.IsNullOrWhiteSpace(seed.Name))
        {
            if (symbol.Name.Equals(seed.Name, StringComparison.OrdinalIgnoreCase)) score += 600;
            else if (symbol.Name.StartsWith(seed.Name, StringComparison.OrdinalIgnoreCase)) score += 300;
            else if (symbol.Name.Contains(seed.Name, StringComparison.OrdinalIgnoreCase)) score += 120;
        }

        if (!string.IsNullOrWhiteSpace(seed.SymbolKind)
            && symbol.Kind.ToString().Equals(seed.SymbolKind, StringComparison.OrdinalIgnoreCase))
            score += 120;

        if (!string.IsNullOrWhiteSpace(seed.PathHint)
            && symbol.FilePath.Contains(seed.PathHint, StringComparison.OrdinalIgnoreCase))
            score += 90;

        // Prefer concrete symbol kinds over unknown.
        if (symbol.Kind != SymbolKind.Unknown) score += 12;
        return score;
    }

    private static string? NormalizeSymbolSeed(string? seedId)
    {
        if (string.IsNullOrWhiteSpace(seedId)) return seedId;
        return seedId.StartsWith("symbol:", StringComparison.OrdinalIgnoreCase)
            ? seedId["symbol:".Length..]
            : seedId;
    }

    private static string NormalizeFileSeed(string seedPath)
        => seedPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            ? seedPath["file:".Length..]
            : seedPath;

    private static async Task<List<GraphRelation>> GetOutgoingNeighborsAsync(GraphNode current, HashSet<GraphEdgeType> edgeTypes, BuildState state)
    {
        var rel = new List<GraphRelation>();
        if (current.Type == GraphNodeType.File && current.FilePath is not null)
        {
            if (edgeTypes.Contains(GraphEdgeType.Contains))
            {
                var symbols = await state.Cache.QueryByFileAsync(current.FilePath, state.Ct);
                foreach (var s in symbols)
                {
                    var symbolNode = ToSymbolNode(s);
                    rel.Add(GraphRelation.Out(current, symbolNode, GraphEdgeType.Contains));
                }
            }

            if (edgeTypes.Contains(GraphEdgeType.Imports))
            {
                var fileNode = await state.GetFileNodeAsync(current.FilePath);
                foreach (var import in fileNode?.Imports ?? [])
                {
                    var to = await ResolveImportNodeAsync(state.Project, import, state);
                    rel.Add(GraphRelation.Out(current, to, GraphEdgeType.Imports));
                }
            }
        }
        else if (current.Type == GraphNodeType.Symbol && !string.IsNullOrWhiteSpace(current.SymbolId))
        {
            if (edgeTypes.Contains(GraphEdgeType.References))
            {
                var refs = await state.Cache.QueryReferencesAsync(current.SymbolId!, state.Project, state.Ct);
                foreach (var r in refs)
                {
                    var file = await ResolveFileNodeAsync(state.Project, r.InFilePath, state);
                    if (file is null) continue;
                    rel.Add(GraphRelation.Out(current, file, GraphEdgeType.References));
                }
            }

            // For outgoing callers, expose symbols called by current symbol (heuristic):
            // find referenced targets invoked from the current symbol body.
            if (edgeTypes.Contains(GraphEdgeType.Callers) && current.FilePath is not null)
            {
                var self = await ResolveSymbolByIdAsync(current.SymbolId!, state);
                if (self is not null)
                {
                    var outgoingTargets = await FindOutgoingCallTargetsAsync(self, state);
                    foreach (var target in outgoingTargets.Take(250))
                        rel.Add(GraphRelation.Out(current, ToSymbolNode(target), GraphEdgeType.Callers));
                }
            }
        }

        return rel;
    }

    private static async Task<List<GraphRelation>> GetIncomingNeighborsAsync(GraphNode current, HashSet<GraphEdgeType> edgeTypes, BuildState state)
    {
        var rel = new List<GraphRelation>();
        if (current.Type == GraphNodeType.File && current.FilePath is not null)
        {
            if (edgeTypes.Contains(GraphEdgeType.Imports))
            {
                var dependents = await state.Cache.GetDependentsAsync(current.FilePath, state.Project, state.Ct);
                foreach (var dep in dependents)
                {
                    var from = await ResolveFileNodeAsync(state.Project, dep.FilePath, state);
                    if (from is null) continue;
                    rel.Add(GraphRelation.In(from, current, GraphEdgeType.Imports));
                }
            }

            if (edgeTypes.Contains(GraphEdgeType.Contains))
            {
                var symbols = await state.Cache.QueryByFileAsync(current.FilePath, state.Ct);
                foreach (var s in symbols)
                {
                    var from = ToSymbolNode(s);
                    rel.Add(GraphRelation.In(from, current, GraphEdgeType.Contains));
                }
            }
        }
        else if (current.Type == GraphNodeType.Symbol && current.FilePath is not null)
        {
            if (edgeTypes.Contains(GraphEdgeType.Contains))
            {
                var file = await ResolveFileNodeAsync(state.Project, current.FilePath, state);
                if (file is not null)
                    rel.Add(GraphRelation.In(file, current, GraphEdgeType.Contains));
            }

            if (edgeTypes.Contains(GraphEdgeType.Callers) && !string.IsNullOrWhiteSpace(current.SymbolId))
            {
                var refs = await state.Cache.QueryReferencesAsync(current.SymbolId!, state.Project, state.Ct);
                var seenCaller = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in refs.Take(250))
                {
                    var caller = await ResolveEnclosingSymbolAsync(r.InFilePath, r.Line, state);
                    if (caller is null || caller.Id.Equals(current.SymbolId, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!seenCaller.Add(caller.Id)) continue;
                    rel.Add(GraphRelation.In(ToSymbolNode(caller), current, GraphEdgeType.Callers));
                }

                if (rel.All(x => !x.Edge.Type.Equals(GraphEdgeType.Callers.ToString().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase)))
                {
                    var fallback = await FindIncomingCallersByNameFallbackAsync(current, state);
                    foreach (var caller in fallback.Take(250))
                    {
                        if (!seenCaller.Add(caller.Id)) continue;
                        rel.Add(GraphRelation.In(ToSymbolNode(caller), current, GraphEdgeType.Callers));
                    }
                }
            }

            if (edgeTypes.Contains(GraphEdgeType.Implements))
            {
                var implementors = await state.Cache.QueryImplementorsAsync(current.Label, state.Project, state.Ct);
                foreach (var impl in implementors.Take(250))
                {
                    if (impl.Id.Equals(current.SymbolId, StringComparison.OrdinalIgnoreCase))
                        continue;
                    rel.Add(GraphRelation.In(ToSymbolNode(impl), current, GraphEdgeType.Implements));
                }
            }
        }

        return rel;
    }

    private static async Task<CodeSymbol?> ResolveSymbolByIdAsync(string symbolId, BuildState state)
    {
        var all = await state.GetAllSymbolsAsync();
        return all.FirstOrDefault(s => s.Id.Equals(symbolId, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<List<CodeSymbol>> FindOutgoingCallTargetsAsync(CodeSymbol self, BuildState state)
    {
        if (!File.Exists(self.FilePath) || self.LineStart <= 0)
            return [];

        var lines = await File.ReadAllLinesAsync(self.FilePath, state.Ct);
        if (lines.Length == 0) return [];

        var from = Math.Clamp(self.LineStart, 1, lines.Length);
        var to = self.LineEnd > 0
            ? Math.Clamp(self.LineEnd, from, lines.Length)
            : Math.Min(lines.Length, from + 220);

        var tokens = new HashSet<string>(StringComparer.Ordinal);
        for (var i = from; i <= to; i++)
        {
            foreach (Match m in CallTokenRegex.Matches(lines[i - 1]))
            {
                var token = m.Groups[1].Value;
                if (token.Length < 2) continue;
                tokens.Add(token);
                if (tokens.Count >= 48) break;
            }
            if (tokens.Count >= 48) break;
        }

        if (tokens.Count == 0) return [];

        var checkedCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new Dictionary<string, CodeSymbol>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in tokens)
        {
            var candidates = (await state.Cache.QueryByNameAsync(token, state.Project, state.Ct))
                .Where(s => s.Name.Equals(token, StringComparison.Ordinal))
                .Where(s => s.Kind is SymbolKind.Method or SymbolKind.Function or SymbolKind.Unknown)
                .Take(36)
                .ToList();

            foreach (var candidate in candidates)
            {
                if (candidate.Id.Equals(self.Id, StringComparison.OrdinalIgnoreCase)) continue;
                if (!checkedCandidates.Add(candidate.Id)) continue;

                var refs = await state.Cache.QueryReferencesAsync(candidate.Id, state.Project, state.Ct);
                if (refs.Any(r => r.InFilePath.Equals(self.FilePath, StringComparison.OrdinalIgnoreCase) && IsInsideSymbol(self, r.Line)))
                    targets[candidate.Id] = candidate;
            }
        }

        return [.. targets.Values];
    }

    private static async Task<List<CodeSymbol>> FindIncomingCallersByNameFallbackAsync(GraphNode current, BuildState state)
    {
        if (string.IsNullOrWhiteSpace(current.Label)) return [];

        var symbols = (await state.Cache.QueryByNameAsync(current.Label, state.Project, state.Ct))
            .Where(s => s.Name.Equals(current.Label, StringComparison.Ordinal))
            .Take(64)
            .ToList();

        var incoming = new Dictionary<string, CodeSymbol>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in symbols)
        {
            var refs = await state.Cache.QueryReferencesAsync(s.Id, state.Project, state.Ct);
            foreach (var r in refs.Take(240))
            {
                var caller = await ResolveEnclosingSymbolAsync(r.InFilePath, r.Line, state);
                if (caller is null) continue;
                if (!string.IsNullOrWhiteSpace(current.SymbolId) && caller.Id.Equals(current.SymbolId, StringComparison.OrdinalIgnoreCase))
                    continue;
                incoming[caller.Id] = caller;
            }
        }

        return [.. incoming.Values];
    }

    private static async Task<CodeSymbol?> ResolveEnclosingSymbolAsync(string filePath, int line, BuildState state)
    {
        var symbols = (await state.Cache.QueryByFileAsync(filePath, state.Ct))
            .OrderBy(s => s.LineStart)
            .ToList();
        if (symbols.Count == 0) return null;

        var containing = symbols
            .Where(s => IsInsideSymbol(s, line))
            .OrderBy(s => s.LineEnd <= 0 ? int.MaxValue : (s.LineEnd - s.LineStart))
            .FirstOrDefault();
        if (containing is not null) return containing;

        // Fallback to the nearest previous declaration.
        return symbols
            .Where(s => s.LineStart <= line)
            .OrderByDescending(s => s.LineStart)
            .FirstOrDefault();
    }

    private static bool IsInsideSymbol(CodeSymbol symbol, int line)
    {
        if (line < symbol.LineStart) return false;
        if (symbol.LineEnd <= 0) return line >= symbol.LineStart;
        return line <= symbol.LineEnd;
    }

    private static async Task<GraphNode?> ResolveFileNodeAsync(string project, string filePath, BuildState state)
    {
        var normalized = Path.GetFullPath(filePath);
        var node = await state.GetFileNodeAsync(normalized);
        return node is null
            ? null
            : new GraphNode
            {
                Id = $"file:{normalized}",
                Type = GraphNodeType.File,
                Label = Path.GetFileName(normalized),
                FilePath = normalized,
                Language = node.Language
            };
    }

    private static async Task<GraphNode> ResolveImportNodeAsync(string project, string import, BuildState state)
    {
        var maybePath = import.Contains(Path.DirectorySeparatorChar) || import.Contains(Path.AltDirectorySeparatorChar)
            ? Path.GetFullPath(import)
            : null;

        if (maybePath is not null)
        {
            var file = await ResolveFileNodeAsync(project, maybePath, state);
            if (file is not null) return file;
        }

        return new GraphNode
        {
            Id = $"import:{import}",
            Type = GraphNodeType.Import,
            Label = import,
            ImportPath = import
        };
    }

    private static GraphNode ToSymbolNode(CodeSymbol symbol)
        => new()
        {
            Id = $"symbol:{symbol.Id}",
            SymbolId = symbol.Id,
            Type = GraphNodeType.Symbol,
            Label = symbol.Name,
            FilePath = symbol.FilePath,
            LineStart = symbol.LineStart,
            SymbolKind = symbol.Kind.ToString(),
            Signature = symbol.Signature
        };

    private sealed class BuildState(string project, ICodeMapCache cache, CancellationToken ct)
    {
        private readonly Dictionary<string, FileNode?> _fileNodes = new(StringComparer.OrdinalIgnoreCase);
        private List<CodeSymbol>? _allSymbols;
        public string Project { get; } = project;
        public ICodeMapCache Cache { get; } = cache;
        public CancellationToken Ct { get; } = ct;
        public Dictionary<string, GraphNode> Nodes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, GraphEdge> Edges { get; } = new(StringComparer.OrdinalIgnoreCase);

        public async Task<FileNode?> GetFileNodeAsync(string path)
        {
            var normalized = Path.GetFullPath(path);
            if (_fileNodes.TryGetValue(normalized, out var cached))
                return cached;
            var file = await Cache.GetFileNodeAsync(normalized, Ct);
            _fileNodes[normalized] = file;
            return file;
        }

        public async Task<List<CodeSymbol>> GetAllSymbolsAsync()
        {
            if (_allSymbols is not null) return _allSymbols;
            _allSymbols = (await Cache.QueryByNameAsync("", Project, Ct)).ToList();
            return _allSymbols;
        }

        public bool AddNode(GraphNode node, int distance)
        {
            if (Nodes.TryGetValue(node.Id, out var existing))
            {
                if (distance < existing.Distance) existing.Distance = distance;
                return false;
            }
            node.Distance = distance;
            Nodes[node.Id] = node;
            return true;
        }

        public void AddEdge(GraphEdge edge)
        {
            var key = $"{edge.From}|{edge.To}|{edge.Type}";
            Edges[key] = edge;
        }
    }

    private sealed class GraphRelation
    {
        public required GraphNode From { get; init; }
        public required GraphNode To { get; init; }
        public required GraphEdge Edge { get; init; }

        public static GraphRelation Out(GraphNode from, GraphNode to, GraphEdgeType type)
            => new()
            {
                From = from,
                To = to,
                Edge = new GraphEdge { From = from.Id, To = to.Id, Type = type.ToString().ToLowerInvariant() }
            };

        public static GraphRelation In(GraphNode from, GraphNode to, GraphEdgeType type)
            => Out(from, to, type);
    }

    private sealed record GraphBuildResult(
        BuildState State,
        GraphNode? Seed,
        List<GraphNode> Nodes,
        List<GraphEdge> Edges)
    {
        public static GraphBuildResult Empty(BuildState state)
            => new(state, null, [], []);
    }
}

public class GraphQueryRequest
{
    public string Project { get; init; } = "";
    public GraphSeed? Seed { get; init; }
    public GraphExpand? Expand { get; init; }
    public GraphInclude? Include { get; init; }
}

public class GraphExpandRequest
{
    public string Project { get; init; } = "";
    public GraphSeed? Seed { get; init; }
    public string[]? EdgeTypes { get; init; }
    public string? Direction { get; init; } = "both";
    public int Depth { get; init; } = 1;
    public int MaxNodes { get; init; } = 160;
    public string[]? ExcludeNodeIds { get; init; }
    public string[]? ExcludeEdgeIds { get; init; }
    public GraphPageRequest? Page { get; init; }
}

public class GraphCollapseRequest
{
    public string Project { get; init; } = "";
    public GraphSeed? Seed { get; init; }
    public string[]? EdgeTypes { get; init; }
    public string? Direction { get; init; } = "both";
    public int Depth { get; init; } = 1;
    public int MaxNodes { get; init; } = 160;
}

public class GraphSeed
{
    public string Type { get; init; } = "";
    public string? Id { get; init; }
    public string? Path { get; init; }
    public string? Name { get; init; }
    public string? SymbolKind { get; init; }
    public string? PathHint { get; init; }
}

public class GraphExpand
{
    public string[]? EdgeTypes { get; init; }
    public string? Direction { get; init; } = "both";
    public int Depth { get; init; } = 1;
    public int MaxNodes { get; init; } = 160;
}

public class GraphInclude
{
    public bool Snippets { get; init; } = true;
    public int Radius { get; init; } = 6;
}

public class GraphQueryResponse
{
    public string Project { get; set; } = "";
    public string SeedId { get; set; } = "";
    public GraphSummary Summary { get; set; } = new();
    public List<GraphNode> Nodes { get; set; } = [];
    public List<GraphEdge> Edges { get; set; } = [];
    public GraphContextPack? ContextPack { get; set; }
}

public class GraphExpandResponse
{
    public string Project { get; set; } = "";
    public string SeedId { get; set; } = "";
    public List<GraphNode> Nodes { get; set; } = [];
    public List<GraphEdge> Edges { get; set; } = [];
    public GraphPageResult Page { get; set; } = new();
}

public class GraphCollapseResponse
{
    public string Project { get; set; } = "";
    public string SeedId { get; set; } = "";
    public List<string> RemoveNodeIds { get; set; } = [];
    public List<string> RemoveEdgeIds { get; set; } = [];
}

public class GraphSummary
{
    public int NodeCount { get; set; }
    public int EdgeCount { get; set; }
    public int Depth { get; set; }
}

public class GraphPageRequest
{
    public int Offset { get; init; }
    public int Limit { get; init; } = 120;
}

public class GraphPageResult
{
    public int Offset { get; set; }
    public int Limit { get; set; }
    public int TotalEdges { get; set; }
    public bool HasMore { get; set; }
    public int? NextOffset { get; set; }
}

public class GraphNode
{
    public string Id { get; set; } = "";
    public GraphNodeType Type { get; set; }
    public string Label { get; set; } = "";
    public string? FilePath { get; set; }
    public string? SymbolId { get; set; }
    public string? SymbolKind { get; set; }
    public string? Signature { get; set; }
    public string? Language { get; set; }
    public string? ImportPath { get; set; }
    public int LineStart { get; set; }
    public int Distance { get; set; }
}

public class GraphEdge
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Type { get; set; } = "";
}

public class GraphContextPack
{
    public List<string> TopFiles { get; set; } = [];
    public List<string> TopSymbols { get; set; } = [];
    public List<GraphSnippet> Snippets { get; set; } = [];
}

public class GraphSnippet
{
    public string FilePath { get; set; } = "";
    public int Line { get; set; }
    public string Context { get; set; } = "";
}

public enum GraphNodeType
{
    File,
    Symbol,
    Import
}

public enum GraphEdgeType
{
    Contains,
    Imports,
    References,
    Callers,
    Implements
}
