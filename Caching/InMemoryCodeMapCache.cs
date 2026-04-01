using System.Collections.Concurrent;
using Llens.Models;

namespace Llens.Caching;

public class InMemoryCodeMapCache : ICodeMapCache
{
    private readonly ConcurrentDictionary<string, List<CodeSymbol>> _symbolsByFile = new();
    private readonly ConcurrentDictionary<string, List<SymbolReference>> _referencesBySymbol = new();
    private readonly ConcurrentDictionary<string, FileNode> _fileNodes = new();

    // --- Symbols ---

    public Task StoreSymbolsAsync(string filePath, IEnumerable<CodeSymbol> symbols, CancellationToken ct = default)
    {
        _symbolsByFile[filePath] = [.. symbols];
        return Task.CompletedTask;
    }

    public Task RemoveFileAsync(string filePath, CancellationToken ct = default)
    {
        _symbolsByFile.TryRemove(filePath, out _);
        _fileNodes.TryRemove(filePath, out _);
        return Task.CompletedTask;
    }

    public Task<IEnumerable<CodeSymbol>> QueryByNameAsync(string name, string? repoName = null, CancellationToken ct = default)
    {
        var results = AllSymbols()
            .Where(s => s.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            .Where(s => repoName is null || s.RepoName == repoName);
        return Task.FromResult(results);
    }

    public Task<IEnumerable<CodeSymbol>> QueryByFileAsync(string filePath, CancellationToken ct = default)
    {
        var results = _symbolsByFile.TryGetValue(filePath, out var symbols)
            ? symbols.AsEnumerable()
            : Enumerable.Empty<CodeSymbol>();
        return Task.FromResult(results);
    }

    public Task<IEnumerable<CodeSymbol>> QueryByKindAsync(SymbolKind kind, string? repoName = null, CancellationToken ct = default)
    {
        var results = AllSymbols()
            .Where(s => s.Kind == kind)
            .Where(s => repoName is null || s.RepoName == repoName);
        return Task.FromResult(results);
    }

    public Task<IEnumerable<CodeSymbol>> QueryImplementorsAsync(string symbolName, string? repoName = null, CancellationToken ct = default)
    {
        // TODO: needs base_type tracking from indexer — placeholder for now
        return Task.FromResult(Enumerable.Empty<CodeSymbol>());
    }

    // --- References ---

    public Task StoreReferencesAsync(IEnumerable<SymbolReference> references, CancellationToken ct = default)
    {
        foreach (var r in references)
        {
            _referencesBySymbol
                .GetOrAdd(r.SymbolId, _ => [])
                .Add(r);
        }
        return Task.CompletedTask;
    }

    public Task<IEnumerable<SymbolReference>> QueryReferencesAsync(string symbolId, string? repoName = null, CancellationToken ct = default)
    {
        var results = _referencesBySymbol.TryGetValue(symbolId, out var refs)
            ? refs.Where(r => repoName is null || r.RepoName == repoName)
            : Enumerable.Empty<SymbolReference>();
        return Task.FromResult(results);
    }

    // --- File graph ---

    public Task StoreFileNodeAsync(FileNode file, CancellationToken ct = default)
    {
        _fileNodes[file.FilePath] = file;
        return Task.CompletedTask;
    }

    public Task<FileNode?> GetFileNodeAsync(string filePath, CancellationToken ct = default)
    {
        _fileNodes.TryGetValue(filePath, out var node);
        return Task.FromResult(node);
    }

    public Task<IEnumerable<FileNode>> GetDependentsAsync(string filePath, string? repoName = null, CancellationToken ct = default)
    {
        var results = _fileNodes.Values
            .Where(f => f.Imports.Contains(filePath))
            .Where(f => repoName is null || f.RepoName == repoName);
        return Task.FromResult(results);
    }

    public Task<IEnumerable<FileNode>> GetAllFilesAsync(string repoName, CancellationToken ct = default)
    {
        var results = _fileNodes.Values.Where(f => f.RepoName == repoName);
        return Task.FromResult(results);
    }

    // --- Context extraction ---

    public async Task<string?> GetSourceContextAsync(string filePath, int line, int radiusLines = 20, CancellationToken ct = default)
    {
        if (!File.Exists(filePath)) return null;
        var lines = await File.ReadAllLinesAsync(filePath, ct);
        var from = Math.Max(0, line - radiusLines - 1);
        var to = Math.Min(lines.Length - 1, line + radiusLines - 1);
        return string.Join('\n', lines[from..(to + 1)]);
    }

    private IEnumerable<CodeSymbol> AllSymbols()
        => _symbolsByFile.Values.SelectMany(s => s);
}
