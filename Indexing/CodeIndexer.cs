using Llens.Caching;
using Llens.Languages;
using Llens.Models;
using Llens.Scanning;
using Llens.Tools;

namespace Llens.Indexing;

public class CodeIndexer(
    ProjectRegistry projects,
    ICodeMapCache cache,
    IFileScanner scanner,
    ILogger<CodeIndexer> logger) : ICodeIndexer
{
    public async Task IndexRepoAsync(RepoConfig repo, CancellationToken ct = default)
    {
        if (!Directory.Exists(repo.ResolvedPath))
        {
            logger.LogWarning("Repo path does not exist: {Path}", repo.ResolvedPath);
            return;
        }

        var project = projects.Resolve(repo.Name);
        if (project is null)
        {
            logger.LogWarning("No project registered for repo: {Name}", repo.Name);
            return;
        }

        var extensions = project.Languages.SupportedExtensions;
        var count = 0;

        await foreach (var file in scanner.GetFilesAsync(repo.ResolvedPath, extensions, ct))
        {
            ct.ThrowIfCancellationRequested();
            await IndexFileAsync(repo.Name, file, ct);
            count++;
        }

        logger.LogInformation("Indexed {Count} files in project {Name}", count, repo.Name);
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

        var context = new ToolContext(repoName, filePath);
        var allSymbols = new List<CodeSymbol>();
        var allImports = new List<string>();

        foreach (var tool in language.GetTools(ToolCapability.SymbolExtraction))
        {
            var result = await tool.ExecuteAsync(context, ct);
            if (result.Success)
            {
                allSymbols.AddRange(result.Symbols);
                allImports.AddRange(result.Imports);
            }
            else
                logger.LogWarning("[{Tool}] failed on {File}: {Error}", tool.GetType().Name, filePath, result.Error);
        }

        await cache.StoreSymbolsAsync(filePath, allSymbols, ct);
        await cache.StoreFileNodeAsync(new FileNode
        {
            FilePath = filePath,
            RepoName = repoName,
            Language = language.Name,
            Imports = [.. allImports.Distinct()],
            SymbolCount = allSymbols.Count,
            LastIndexedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        }, ct);

        logger.LogDebug("Indexed {Count} symbols in {File}", allSymbols.Count, filePath);
    }

    public Task RemoveFileAsync(string repoName, string filePath, CancellationToken ct = default)
        => cache.RemoveFileAsync(filePath, ct);
}
