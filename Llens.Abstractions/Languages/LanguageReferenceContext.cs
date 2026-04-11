using Llens.Models;

namespace Llens.Languages;

/// <summary>
/// Context passed to <see cref="IReferenceResolver.ResolveAsync"/> containing everything
/// a reference resolver needs: the file being analyzed, its source lines,
/// and async callbacks to query the live symbol index.
/// </summary>
public sealed record LanguageReferenceContext(
    string RepoName,
    string FilePath,
    IReadOnlyList<string> Lines,
    Func<string, string?, CancellationToken, Task<IEnumerable<CodeSymbol>>> QueryByNameAsync,
    Func<string, CancellationToken, Task<IEnumerable<FileNode>>> GetProjectFilesAsync);
