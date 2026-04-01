using Llens.Caching;
using Llens.Models;

namespace Llens.Api;

public static class CodeMapEndpoints
{
    public static void MapCodeMapRoutes(this WebApplication app)
    {
        var group = app.MapGroup("/api/codemap");

        group.MapGet("/search", async (string q, string? repo, ICodeMapCache cache) =>
        {
            var results = await cache.QueryByNameAsync(q, repo);
            return Results.Ok(results);
        });

        group.MapGet("/file", async (string path, ICodeMapCache cache) =>
        {
            var results = await cache.QueryByFileAsync(path);
            return Results.Ok(results);
        });

        group.MapGet("/kind/{kind}", async (string kind, string? repo, ICodeMapCache cache) =>
        {
            if (!Enum.TryParse<SymbolKind>(kind, ignoreCase: true, out var symbolKind))
                return Results.BadRequest($"Unknown kind '{kind}'. Valid: {string.Join(", ", Enum.GetNames<SymbolKind>())}");

            var results = await cache.QueryByKindAsync(symbolKind, repo);
            return Results.Ok(results);
        });
    }
}
