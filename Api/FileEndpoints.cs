using Llens.Caching;

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
        g.MapGet("/node", async (string path, ICodeMapCache cache) =>
        {
            var node = await cache.GetFileNodeAsync(path);
            return node is null ? Results.NotFound() : Results.Ok(node);
        });

        // What imports this file — replaces: grep -rn "use crate::models::product" . or similar
        g.MapGet("/dependents", async (string path, string? project, ICodeMapCache cache) =>
        {
            var dependents = await cache.GetDependentsAsync(path, project);
            return Results.Ok(dependents);
        });

        // Source context window — replaces: reading an entire file to find 10 relevant lines
        g.MapGet("/context", async (string path, int line, int radius, ICodeMapCache cache) =>
        {
            var context = await cache.GetSourceContextAsync(path, line, radius == 0 ? 20 : radius);
            return context is null ? Results.NotFound() : Results.Ok(new { path, line, radius, context });
        });
    }
}
