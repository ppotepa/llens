namespace Llens.Languages;

/// <summary>
/// Holds all registered languages. Given a file path, returns the matching language
/// and its tools so the indexer knows what to run.
/// </summary>
public class LanguageRegistry
{
    private readonly IReadOnlyList<ILanguage> _languages;

    public LanguageRegistry(IEnumerable<ILanguage> languages)
    {
        _languages = [.. languages];
    }

    public ILanguage? Resolve(string filePath)
        => _languages.FirstOrDefault(l => l.CanHandle(filePath));

    public IReadOnlyList<ILanguage> All => _languages;
}
