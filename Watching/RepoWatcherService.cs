using Llens.Indexing;
using Llens.Models;

namespace Llens.Watching;

public class RepoWatcherService(
    ProjectRegistry projects,
    ICodeIndexer indexer,
    ILogger<RepoWatcherService> logger) : BackgroundService
{
    private readonly List<FileSystemWatcher> _watchers = [];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        foreach (var project in projects.All)
        {
            var repo = project.Config;

            if (!Directory.Exists(repo.Path))
            {
                logger.LogWarning("Skipping watcher for missing path: {Path}", repo.Path);
                continue;
            }

            logger.LogInformation("Starting initial index for project: {Name}", project.Name);
            await indexer.IndexRepoAsync(repo, ct);

            var extensions = project.Languages.SupportedExtensions;

            var watcher = new FileSystemWatcher(repo.Path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };

            watcher.Changed += (_, e) => OnFileChanged(repo, e.FullPath, extensions, ct);
            watcher.Created += (_, e) => OnFileChanged(repo, e.FullPath, extensions, ct);
            watcher.Deleted += (_, e) => OnFileDeleted(repo, e.FullPath, ct);
            watcher.Renamed += (_, e) =>
            {
                OnFileDeleted(repo, e.OldFullPath, ct);
                OnFileChanged(repo, e.FullPath, extensions, ct);
            };

            _watchers.Add(watcher);
            logger.LogInformation("Watching project: {Name} at {Path}", project.Name, repo.Path);
        }

        await Task.Delay(Timeout.Infinite, ct);
    }

    private void OnFileChanged(RepoConfig repo, string filePath, HashSet<string> extensions, CancellationToken ct)
    {
        if (!extensions.Contains(Path.GetExtension(filePath))) return;

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
