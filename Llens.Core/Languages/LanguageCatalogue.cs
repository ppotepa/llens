namespace Llens.Languages;

/// <summary>
/// Holds every known language implementation, keyed by <see cref="LanguageId"/> for O(1) lookup.
/// Used at startup to build per-project LanguageRegistry instances from config.
/// </summary>
public class LanguageCatalogue(IEnumerable<ILanguage> all)
{
    private readonly IReadOnlyDictionary<LanguageId, ILanguage> _map =
        all.ToDictionary(l => l.Id);

    public LanguageRegistry BuildRegistry(IEnumerable<LanguageId> ids)
    {
        var languages = ids
            .Distinct()
            .Select(id => _map.TryGetValue(id, out var lang) ? lang : null)
            .OfType<ILanguage>();

        return new LanguageRegistry(languages);
    }

    public IReadOnlyCollection<LanguageId> KnownLanguages => _map.Keys.ToArray();
}
