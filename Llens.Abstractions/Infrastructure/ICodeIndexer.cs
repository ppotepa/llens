using Llens.Models;

namespace Llens.Indexing;

public interface ICodeIndexer
{
    Task IndexRepoAsync(RepoConfig repo, CancellationToken ct = default);
    Task IndexFileAsync(string repoName, string filePath, CancellationToken ct = default);
    Task RemoveFileAsync(string repoName, string filePath, CancellationToken ct = default);
}
