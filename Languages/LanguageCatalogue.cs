namespace Llens.Languages;

/// <summary>
/// Holds every known language implementation.
/// Used to build per-project LanguageRegistry instances from name strings in config.
/// </summary>
public class LanguageCatalogue(IEnumerable<ILanguage> all)
{
    private readonly IReadOnlyDictionary<string, ILanguage> _map =
        all.ToDictionary(l => l.Name, StringComparer.OrdinalIgnoreCase);

    public LanguageRegistry BuildRegistry(IEnumerable<string> languageNames)
    {
        var languages = languageNames
            .Select(name => _map.TryGetValue(name, out var lang) ? lang : null)
            .OfType<ILanguage>();

        return new LanguageRegistry(languages);
    }

    public IReadOnlyCollection<string> KnownLanguages => _map.Keys.ToArray();
}
