namespace Llens.Languages;

/// <summary>
/// Per-project registry of language handlers. Implements <see cref="ILanguageRegistry"/>.
/// All lookups are O(1) via dictionary — no linear scans.
/// </summary>
public class LanguageRegistry : ILanguageRegistry
{
    private readonly IReadOnlyDictionary<LanguageId, ILanguage> _byId;
    private readonly IReadOnlyDictionary<string, ILanguage> _byExtension;

    public LanguageRegistry(IEnumerable<ILanguage> languages)
    {
        var list = languages.ToList();
        _byId = list.ToDictionary(l => l.Id);
        _byExtension = list
            .SelectMany(l => l.Extensions.Select(ext => (ext, l)))
            .ToDictionary(x => x.ext, x => x.l, StringComparer.OrdinalIgnoreCase);

        SupportedExtensions = _byExtension.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Resolve language by file extension — O(1).</summary>
    public ILanguage? Resolve(string filePath)
        => _byExtension.TryGetValue(Path.GetExtension(filePath), out var lang) ? lang : null;

    /// <summary>Resolve language by id — O(1).</summary>
    public ILanguage? Resolve(LanguageId id)
        => _byId.TryGetValue(id, out var lang) ? lang : null;

    public IReadOnlyList<ILanguage> All => [.. _byId.Values];
    public HashSet<string> SupportedExtensions { get; }
}
