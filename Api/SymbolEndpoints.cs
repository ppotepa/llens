using Llens.Caching;
using Llens.Models;

namespace Llens.Api;

public static class SymbolEndpoints
{
    public static void MapSymbolRoutes(this WebApplication app)
    {
        var g = app.MapGroup("/api/symbols");

        // Search by name — replaces: grep -r "ClassName" .
        g.MapGet("/search", async (string q, string? project, string? kind, ICodeMapCache cache) =>
        {
            if (kind is not null && !Enum.TryParse<SymbolKind>(kind, ignoreCase: true, out _))
                return Results.BadRequest($"Unknown kind '{kind}'.");

            var symbols = kind is null
                ? await cache.QueryByNameAsync(q, project)
                : await cache.QueryByKindAsync(Enum.Parse<SymbolKind>(kind, ignoreCase: true), project);

            return Results.Ok(symbols.Where(s => s.Name.Contains(q, StringComparison.OrdinalIgnoreCase)));
        });

        // Resolve exact definition — replaces: grep -rn "class Foo" . or find . -name "Foo.cs"
        g.MapGet("/resolve", async (string name, string? project, ICodeMapCache cache) =>
        {
            var symbols = await cache.QueryByNameAsync(name, project);
            var exact = symbols.Where(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            return Results.Ok(exact);
        });

        // Usages — replaces: grep -rn "Foo" . (noisy, matches strings/comments too)
        g.MapGet("/references", async (string symbolId, string? project, ICodeMapCache cache) =>
        {
            var refs = await cache.QueryReferencesAsync(symbolId, project);
            return Results.Ok(refs);
        });

        // Implementors — replaces: multiple greps for class X : IFoo or impl Trait for X
        g.MapGet("/implementors", async (string name, string? project, ICodeMapCache cache) =>
        {
            var symbols = await cache.QueryImplementorsAsync(name, project);
            return Results.Ok(symbols);
        });

        // All symbols in a file
        g.MapGet("/in-file", async (string path, string? project, ProjectRegistry projects, ICodeMapCache cache, CancellationToken ct) =>
        {
            var (resolvedPath, error) = await IndexedPathResolver.ResolveAsync(path, project, projects, cache, ct);
            if (error is not null) return Results.BadRequest(error);

            var symbols = await cache.QueryByFileAsync(resolvedPath!, ct);
            return Results.Ok(symbols);
        });
    }
}
