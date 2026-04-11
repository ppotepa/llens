namespace Llens.Languages;

/// <summary>
/// Non-generic base — used by the registry and indexer to handle languages polymorphically.
/// Exposes capabilities via typed interfaces so <c>CodeIndexer</c> needs no runtime casting.
/// </summary>
public interface ILanguage
{
    LanguageId Id { get; }
    string Name { get; }
    IReadOnlyList<string> Extensions { get; }

    bool CanHandle(string filePath) => Extensions.Contains(Path.GetExtension(filePath));

    /// <summary>Required. Extracts symbols and raw imports from a single file.</summary>
    ISymbolExtractor SymbolExtractor { get; }

    /// <summary>Optional. Normalizes raw imports into canonical file paths. Falls back to Distinct() if absent.</summary>
    IImportResolver? ImportResolver { get; }

    /// <summary>Optional. Extracts token usages for reference resolution. Falls back to lexical scan if absent.</summary>
    IUsageExtractor? UsageExtractor { get; }

    /// <summary>Optional. Builds semantic cross-file references. Falls back to brute-force matching if absent.</summary>
    IReferenceResolver? ReferenceResolver { get; }
}

/// <summary>
/// Generic typed language — enforces at compile time that only capabilities matching
/// this language's marker can be registered. The explicit interface implementations
/// satisfy the non-generic <see cref="ILanguage"/> base used by <c>CodeIndexer</c>.
/// </summary>
public interface ILanguage<TMarker> : ILanguage where TMarker : ILanguageMarker
{
    new ISymbolExtractor<TMarker> SymbolExtractor { get; }
    new IImportResolver<TMarker>? ImportResolver { get; }
    new IUsageExtractor<TMarker>? UsageExtractor { get; }
    new IReferenceResolver<TMarker>? ReferenceResolver { get; }

    ISymbolExtractor ILanguage.SymbolExtractor => SymbolExtractor;
    IImportResolver? ILanguage.ImportResolver => ImportResolver;
    IUsageExtractor? ILanguage.UsageExtractor => UsageExtractor;
    IReferenceResolver? ILanguage.ReferenceResolver => ReferenceResolver;
}
