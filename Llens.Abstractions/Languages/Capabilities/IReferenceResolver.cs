using Llens.Models;

namespace Llens.Languages;

/// <summary>
/// Builds semantic cross-file symbol references for a given file.
/// Implementations may use full compiler APIs (Roslyn), scoring heuristics (Rust), or LSP.
/// Non-generic base used by <c>CodeIndexer</c>.
/// </summary>
public interface IReferenceResolver
{
    Task<IReadOnlyList<SymbolReference>> ResolveAsync(LanguageReferenceContext context, CancellationToken ct = default);
}

/// <summary>
/// Typed reference resolver — binds to a specific language at compile time.
/// </summary>
public interface IReferenceResolver<TLanguage> : IReferenceResolver, ILanguageCapability<TLanguage>
    where TLanguage : ILanguageMarker { }
