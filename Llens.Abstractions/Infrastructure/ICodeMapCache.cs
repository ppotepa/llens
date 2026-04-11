using Llens.Models;

namespace Llens.Caching;

public interface ICodeMapCache
{
    // --- Symbol storage ---
    Task StoreSymbolsAsync(string filePath, IEnumerable<CodeSymbol> symbols, CancellationToken ct = default);
    Task RemoveFileAsync(string filePath, CancellationToken ct = default);

    // --- Symbol queries ---
    Task<IEnumerable<CodeSymbol>> QueryByNameAsync(string name, string? repoName = null, CancellationToken ct = default);
    Task<IEnumerable<CodeSymbol>> QueryByFileAsync(string filePath, CancellationToken ct = default);
    Task<IEnumerable<CodeSymbol>> QueryByKindAsync(SymbolKind kind, string? repoName = null, CancellationToken ct = default);

    /// <summary>Find symbols that implement or extend the named type/trait/interface.</summary>
    Task<IEnumerable<CodeSymbol>> QueryImplementorsAsync(string symbolName, string? repoName = null, CancellationToken ct = default);

    // --- Reference queries ---
    /// <summary>Remove all references that originate from a file before re-indexing that file.</summary>
    Task RemoveReferencesInFileAsync(string filePath, CancellationToken ct = default);
    Task StoreReferencesAsync(IEnumerable<SymbolReference> references, CancellationToken ct = default);

    /// <summary>All usages of a symbol across the repo — replaces grepping for a name.</summary>
    Task<IEnumerable<SymbolReference>> QueryReferencesAsync(string symbolId, string? repoName = null, CancellationToken ct = default);

    // --- File graph queries ---
    Task StoreFileNodeAsync(FileNode file, CancellationToken ct = default);

    /// <summary>What files does this file import?</summary>
    Task<FileNode?> GetFileNodeAsync(string filePath, CancellationToken ct = default);

    /// <summary>What files import this file? Replaces reverse-grep for an import path.</summary>
    Task<IEnumerable<FileNode>> GetDependentsAsync(string filePath, string? repoName = null, CancellationToken ct = default);

    /// <summary>All files in a repo — replaces glob/find for project structure.</summary>
    Task<IEnumerable<FileNode>> GetAllFilesAsync(string repoName, CancellationToken ct = default);

    // --- Context extraction ---
    /// <summary>Returns a slice of source lines around a location — replaces reading whole files.</summary>
    Task<string?> GetSourceContextAsync(string filePath, int line, int radiusLines = 20, CancellationToken ct = default);
}
