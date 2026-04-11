namespace Llens.Languages;

/// <summary>
/// Normalizes and resolves raw import strings (using/use/import) into canonical file paths.
/// Each language has different import semantics: C# de-duplicates, Rust resolves Cargo modules.
/// Non-generic base used by <c>CodeIndexer</c>.
/// </summary>
public interface IImportResolver
{
    IReadOnlyList<string> Resolve(string repoRoot, string filePath, IReadOnlyList<string> rawImports);
}

/// <summary>
/// Typed import resolver — binds to a specific language at compile time.
/// </summary>
public interface IImportResolver<TLanguage> : IImportResolver, ILanguageCapability<TLanguage>
    where TLanguage : ILanguageMarker { }
