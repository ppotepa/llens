using Llens.Caching;
using Llens.Models;

namespace Llens.Api;

public static class ProjectEndpoints
{
    public static void MapProjectRoutes(this WebApplication app)
    {
        var g = app.MapGroup("/api/projects");

        // All registered projects — gives LLM a fast orientation without any file scanning
        g.MapGet("/", (ProjectRegistry projects) =>
        {
            var summary = projects.All.Select(p => new
            {
                p.Name,
                p.Config.Path,
                languages = p.Languages.All.Select(l => new
                {
                    l.Name,
                    l.Extensions,
                    capabilities = new[]
                    {
                        "SymbolExtraction",
                        l.ImportResolver   is not null ? "ImportResolution"    : null,
                        l.UsageExtractor   is not null ? "UsageExtraction"     : null,
                        l.ReferenceResolver is not null ? "ReferenceResolution" : null,
                    }.OfType<string>()
                })
            });
            return Results.Ok(summary);
        });

        // Per-project summary — file count, symbol count, languages
        // Gives LLM a map of the project before it starts exploring
        g.MapGet("/{name}/summary", async (string name, ProjectRegistry projects, ICodeMapCache cache) =>
        {
            var project = projects.Resolve(name);
            if (project is null) return Results.NotFound();

            var files = (await cache.GetAllFilesAsync(name)).ToList();
            var byLanguage = files.GroupBy(f => f.Language).Select(g => new
            {
                language = g.Key,
                fileCount = g.Count(),
                totalSymbols = g.Sum(f => f.SymbolCount)
            });

            return Results.Ok(new
            {
                project.Name,
                project.Config.Path,
                totalFiles = files.Count,
                byLanguage
            });
        });
    }
}
