namespace Llens.Languages;

/// <summary>
/// Extracts token usages (calls, type references, member accesses) from source lines.
/// Used as input to reference resolution and as a fallback for the lexical indexer.
/// Non-generic base used by <c>CodeIndexer</c>.
/// </summary>
public interface IUsageExtractor
{
    IEnumerable<(string Token, int Line, string Context)> Extract(string filePath, IReadOnlyList<string> lines);
}

/// <summary>
/// Typed usage extractor — binds to a specific language at compile time.
/// </summary>
public interface IUsageExtractor<TLanguage> : IUsageExtractor, ILanguageCapability<TLanguage>
    where TLanguage : ILanguageMarker { }
