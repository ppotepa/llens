using Llens.Tools;

namespace Llens.Languages;

/// <summary>
/// Non-generic base — used by the registry to route files to the right language.
/// </summary>
public interface ILanguage
{
    string Name { get; }
    IReadOnlyList<string> Extensions { get; }
    IReadOnlyList<ITool> Tools { get; }
    bool CanHandle(string filePath) => Extensions.Contains(Path.GetExtension(filePath));
}

/// <summary>
/// Generic typed language — enforces that only <see cref="ITool{TMarker}"/> matching
/// this language's marker can be registered.
/// </summary>
public interface ILanguage<TMarker> : ILanguage where TMarker : ILanguageMarker
{
    new IReadOnlyList<ITool<TMarker>> Tools { get; }
    IReadOnlyList<ITool> ILanguage.Tools => [.. Tools]; // covariant bridge
}
