using Llens.Caching;
using Llens.Models;

namespace Llens.Api;

public static class FileEndpoints
{
    public static void MapFileRoutes(this WebApplication app)
    {
        var g = app.MapGroup("/api/files");

        // Full file tree — replaces: find . -name "*.cs" or glob patterns
        g.MapGet("/", async (string project, ICodeMapCache cache) =>
        {
            var files = await cache.GetAllFilesAsync(project);
            return Results.Ok(files);
        });

        // File metadata + its imports — replaces: reading the file and parsing imports manually
        g.MapGet("/node", async (string path, string? project, ProjectRegistry projects, ICodeMapCache cache, CancellationToken ct) =>
        {
            var (resolvedPath, error) = await IndexedPathResolver.ResolveAsync(path, project, projects, cache, ct);
            if (error is not null) return Results.BadRequest(error);

            var node = await cache.GetFileNodeAsync(resolvedPath!, ct);
            return node is null ? Results.NotFound() : Results.Ok(node);
        });

        // What imports this file — replaces: grep -rn "use crate::models::product" . or similar
        g.MapGet("/dependents", async (string path, string? project, ProjectRegistry projects, ICodeMapCache cache, CancellationToken ct) =>
        {
            var (resolvedPath, error) = await IndexedPathResolver.ResolveAsync(path, project, projects, cache, ct);
            if (error is not null) return Results.BadRequest(error);

            var dependents = await cache.GetDependentsAsync(resolvedPath!, project, ct);
            return Results.Ok(dependents);
        });

        // Source context window — replaces: reading an entire file to find 10 relevant lines
        g.MapGet("/context", async (string path, int line, int radius, string? project, ProjectRegistry projects, ICodeMapCache cache, CancellationToken ct) =>
        {
            var (resolvedPath, error) = await IndexedPathResolver.ResolveAsync(path, project, projects, cache, ct);
            if (error is not null) return Results.BadRequest(error);

            var actualRadius = radius == 0 ? 20 : radius;
            var context = await cache.GetSourceContextAsync(resolvedPath!, line, actualRadius, ct);
            return context is null
                ? Results.NotFound()
                : Results.Ok(new { path = resolvedPath, line, radius = actualRadius, context });
        });
    }
}
