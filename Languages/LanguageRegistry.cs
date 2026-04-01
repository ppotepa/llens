namespace Llens.Languages;

public class LanguageRegistry
{
    private readonly IReadOnlyList<ILanguage> _languages;

    public LanguageRegistry(IEnumerable<ILanguage> languages)
    {
        _languages = [.. languages];
        SupportedExtensions = _languages.SelectMany(l => l.Extensions).ToHashSet();
    }

    public ILanguage? Resolve(string filePath)
        => _languages.FirstOrDefault(l => l.CanHandle(filePath));

    public IReadOnlyList<ILanguage> All => _languages;
    public HashSet<string> SupportedExtensions { get; }
}
