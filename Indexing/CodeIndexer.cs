using Llens.Caching;
using Llens.Models;
using Llens.Tools;

namespace Llens.Indexing;

public class CodeIndexer(ProjectRegistry projects, ICodeMapCache cache, ILogger<CodeIndexer> logger) : ICodeIndexer
{
    public async Task IndexRepoAsync(RepoConfig repo, CancellationToken ct = default)
    {
        if (!Directory.Exists(repo.Path))
        {
            logger.LogWarning("Repo path does not exist: {Path}", repo.Path);
            return;
        }

        var project = projects.Resolve(repo.Name);
        if (project is null)
        {
            logger.LogWarning("No project registered for repo: {Name}", repo.Name);
            return;
        }

        var extensions = project.Languages.SupportedExtensions;
        var files = Directory
            .EnumerateFiles(repo.Path, "*", SearchOption.AllDirectories)
            .Where(f => extensions.Contains(Path.GetExtension(f)))
            .Where(f => !repo.ExcludePaths.Any(ex => f.Contains(Path.DirectorySeparatorChar + ex + Path.DirectorySeparatorChar)));

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            await IndexFileAsync(repo.Name, file, ct);
        }

        logger.LogInformation("Indexed project {Name}", repo.Name);
    }

    public async Task IndexFileAsync(string repoName, string filePath, CancellationToken ct = default)
    {
        var project = projects.Resolve(repoName);
        var language = project?.Languages.Resolve(filePath);
        if (language is null)
        {
            logger.LogDebug("No language handler for {File} in project {Repo}", filePath, repoName);
            return;
        }

        var indexingTools = language.Tools.Where(t => t.Purpose == ToolPurpose.Indexing);
        var context = new ToolContext(repoName, filePath);
        var allSymbols = new List<CodeSymbol>();

        foreach (var tool in indexingTools)
        {
            var result = await tool.ExecuteAsync(context, ct);
            if (result.Success)
                allSymbols.AddRange(result.Symbols);
            else
                logger.LogWarning("Tool {Tool} failed on {File}: {Error}", tool.Name, filePath, result.Error);
        }

        if (allSymbols.Count > 0)
            await cache.StoreSymbolsAsync(filePath, allSymbols, ct);
    }

    public Task RemoveFileAsync(string repoName, string filePath, CancellationToken ct = default)
        => cache.RemoveFileAsync(filePath, ct);
}
