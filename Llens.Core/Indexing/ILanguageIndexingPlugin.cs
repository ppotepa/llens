using Llens.Models;

namespace Llens.Indexing;

public interface ILanguageIndexingPlugin
{
    IReadOnlyList<string> NormalizeImports(string repoRoot, string filePath, IReadOnlyList<string> imports);
    IEnumerable<(string Token, int Line, string Context)> ExtractUsages(string filePath, IReadOnlyList<string> lines);
    Task<IReadOnlyList<SymbolReference>> BuildSemanticReferencesAsync(LanguageReferenceContext context, CancellationToken ct);
}

public sealed record LanguageReferenceContext(
    string RepoName,
    string FilePath,
    IReadOnlyList<string> Lines,
    Func<string, string?, CancellationToken, Task<IEnumerable<CodeSymbol>>> QueryByNameAsync,
    Func<string, CancellationToken, Task<IEnumerable<FileNode>>> GetProjectFilesAsync);
