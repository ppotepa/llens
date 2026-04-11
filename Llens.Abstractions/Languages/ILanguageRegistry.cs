namespace Llens.Languages;

/// <summary>
/// Per-project registry of language handlers.
/// Decouples <see cref="Llens.Models.Project"/> from the concrete <c>LanguageRegistry</c> implementation in Llens.Core.
/// </summary>
public interface ILanguageRegistry
{
    /// <summary>Resolve language by file extension — O(1).</summary>
    ILanguage? Resolve(string filePath);

    /// <summary>Resolve language by id — O(1).</summary>
    ILanguage? Resolve(LanguageId id);

    IReadOnlyList<ILanguage> All { get; }
    HashSet<string> SupportedExtensions { get; }
}
