namespace Llens.Scanning;

public interface IFileScanner
{
    /// <summary>
    /// Returns all indexable files under <paramref name="repoPath"/>,
    /// filtered by extension and respecting any ignore rules (gitignore, etc).
    /// </summary>
    IAsyncEnumerable<string> GetFilesAsync(string repoPath, IReadOnlySet<string> extensions, CancellationToken ct = default);

    /// <summary>
    /// Returns true if the given file should be indexed —
    /// used by the file watcher to filter change events cheaply.
    /// </summary>
    Task<bool> ShouldIndexAsync(string repoPath, string filePath, IReadOnlySet<string> extensions, CancellationToken ct = default);
}
