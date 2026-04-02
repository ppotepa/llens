using System.Diagnostics;
using Llens.Api;
using Llens.Caching;
using Llens.Indexing;
using Llens.Models;
using Llens.Scanning;
using Llens.Shared;

namespace Llens.Application.Reindex;

public sealed class CompactReindexService : ICompactReindexService
{
    private readonly ICodeMapCache _cache;
    private readonly ICodeIndexer _indexer;
    private readonly IFileScanner _scanner;

    public CompactReindexService(ICodeMapCache cache, ICodeIndexer indexer, IFileScanner scanner)
    {
        _cache = cache;
        _indexer = indexer;
        _scanner = scanner;
    }

    public async Task<CompactReindexResponse> RunAsync(Project project, CompactReindexRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var root = project.Config.ResolvedPath;
        var extensions = project.Languages.SupportedExtensions;
        var maxFiles = Math.Clamp(request.MaxFiles <= 0 ? 5000 : request.MaxFiles, 1, 50000);

        var mode = "project";
        var indexed = 0;
        var removed = 0;
        var skipped = 0;
        var failed = 0;
        var targetPath = request.Path;

        if (string.IsNullOrWhiteSpace(request.Path))
        {
            if (request.PruneStale)
                removed += await PruneMissingIndexedFilesAsync(project, null, ct);

            await _indexer.IndexRepoAsync(project.Config, ct);
            indexed = (await _cache.GetAllFilesAsync(project.Name, ct)).Count();
        }
        else
        {
            var scope = ProjectPathHelper.EnsureWithinProject(root, request.Path!);
            if (scope is null)
                throw new InvalidOperationException("Path is outside project root.");

            if (File.Exists(scope))
            {
                mode = "file";
                if (!await _scanner.ShouldIndexAsync(root, scope, extensions, ct))
                {
                    skipped = 1;
                }
                else
                {
                    await _indexer.IndexFileAsync(project.Name, scope, ct);
                    indexed = 1;
                }
                targetPath = Path.GetRelativePath(root, scope);
            }
            else if (Directory.Exists(scope))
            {
                mode = "directory";
                if (request.PruneStale)
                    removed += await PruneMissingIndexedFilesAsync(project, scope, ct);

                await foreach (var file in _scanner.GetFilesAsync(scope, extensions, ct))
                {
                    if (indexed >= maxFiles) break;
                    if (!await _scanner.ShouldIndexAsync(root, file, extensions, ct))
                    {
                        skipped++;
                        continue;
                    }

                    try
                    {
                        await _indexer.IndexFileAsync(project.Name, file, ct);
                        indexed++;
                    }
                    catch
                    {
                        failed++;
                    }
                }
                targetPath = Path.GetRelativePath(root, scope);
            }
            else
            {
                throw new FileNotFoundException("Path not found.");
            }
        }

        return new CompactReindexResponse(
            project.Name,
            mode,
            targetPath,
            indexed,
            removed,
            skipped,
            failed,
            request.PruneStale,
            sw.ElapsedMilliseconds);
    }

    private async Task<int> PruneMissingIndexedFilesAsync(Project project, string? scopePath, CancellationToken ct)
    {
        var removed = 0;
        var indexedFiles = await _cache.GetAllFilesAsync(project.Name, ct);
        foreach (var file in indexedFiles)
        {
            if (scopePath is not null && !ProjectPathHelper.IsPathWithin(file.FilePath, scopePath))
                continue;
            if (File.Exists(file.FilePath))
                continue;

            await _indexer.RemoveFileAsync(project.Name, file.FilePath, ct);
            removed++;
        }

        return removed;
    }
}
