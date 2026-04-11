using Llens.Tools;

namespace Llens.Languages;

/// <summary>
/// Extracts named symbols and raw import strings from a single file in one pass.
/// Non-generic base used by <c>CodeIndexer</c> for polymorphic dispatch without knowing the language marker.
/// </summary>
public interface ISymbolExtractor
{
    Task<ToolResult> ExtractAsync(ToolContext context, CancellationToken ct = default);
}

/// <summary>
/// Typed symbol extractor — binds to a specific language at compile time via the phantom marker.
/// </summary>
public interface ISymbolExtractor<TLanguage> : ISymbolExtractor, ILanguageCapability<TLanguage>
    where TLanguage : ILanguageMarker { }
