using Llens.Models;

namespace Llens.Caching;

public interface ICodeMapCache
{
    Task StoreSymbolsAsync(string filePath, IEnumerable<CodeSymbol> symbols, CancellationToken ct = default);
    Task RemoveFileAsync(string filePath, CancellationToken ct = default);
    Task<IEnumerable<CodeSymbol>> QueryByNameAsync(string name, string? repoName = null, CancellationToken ct = default);
    Task<IEnumerable<CodeSymbol>> QueryByFileAsync(string filePath, CancellationToken ct = default);
    Task<IEnumerable<CodeSymbol>> QueryByKindAsync(SymbolKind kind, string? repoName = null, CancellationToken ct = default);
}
