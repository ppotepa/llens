using Llens.Indexing;
using Llens.Models;

namespace Llens.Watching;

public class RepoWatcherService(
    IEnumerable<RepoConfig> repos,
    ICodeIndexer indexer,
    ILogger<RepoWatcherService> logger) : BackgroundService
{
    private readonly List<FileSystemWatcher> _watchers = [];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        foreach (var repo in repos)
        {
            if (!Directory.Exists(repo.Path))
            {
                logger.LogWarning("Skipping watcher for missing path: {Path}", repo.Path);
                continue;
            }

            logger.LogInformation("Starting initial index for repo: {Name}", repo.Name);
            await indexer.IndexRepoAsync(repo, ct);

            var watcher = new FileSystemWatcher(repo.Path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };

            watcher.Changed += (_, e) => OnFileChanged(repo, e.FullPath, ct);
            watcher.Created += (_, e) => OnFileChanged(repo, e.FullPath, ct);
            watcher.Deleted += (_, e) => OnFileDeleted(repo, e.FullPath, ct);
            watcher.Renamed += (_, e) =>
            {
                OnFileDeleted(repo, e.OldFullPath, ct);
                OnFileChanged(repo, e.FullPath, ct);
            };

            _watchers.Add(watcher);
            logger.LogInformation("Watching repo: {Name} at {Path}", repo.Name, repo.Path);
        }

        await Task.Delay(Timeout.Infinite, ct);
    }

    private void OnFileChanged(RepoConfig repo, string filePath, CancellationToken ct)
    {
        if (!repo.IncludeExtensions.Contains(Path.GetExtension(filePath))) return;

        Task.Run(async () =>
        {
            try { await indexer.IndexFileAsync(repo.Name, filePath, ct); }
            catch (Exception ex) { logger.LogError(ex, "Failed to re-index {File}", filePath); }
        }, ct);
    }

    private void OnFileDeleted(RepoConfig repo, string filePath, CancellationToken ct)
    {
        Task.Run(async () =>
        {
            try { await indexer.RemoveFileAsync(repo.Name, filePath, ct); }
            catch (Exception ex) { logger.LogError(ex, "Failed to remove {File}", filePath); }
        }, ct);
    }

    public override void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        base.Dispose();
    }
}
