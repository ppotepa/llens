using Llens.Tools;

namespace Llens.Languages;

/// <summary>
/// Non-generic base — used by the registry to route files to the right language.
/// </summary>
public interface ILanguage
{
    LanguageId Id { get; }
    string Name { get; }
    IReadOnlyList<string> Extensions { get; }
    IReadOnlyList<ITool> Tools { get; }
    bool CanHandle(string filePath) => Extensions.Contains(Path.GetExtension(filePath));

    /// <summary>Fast O(1) tool lookup by kind — avoids linear scan on every file.</summary>
    ITool? GetTool(ToolKind kind) => Tools.FirstOrDefault(t => t.Kind == kind);

    /// <summary>All tools for a given purpose, in priority order.</summary>
    IEnumerable<ITool> GetTools(ToolPurpose purpose) => Tools.Where(t => t.Purpose == purpose);
}

/// <summary>
/// Generic typed language — enforces that only <see cref="ITool{TMarker}"/> matching
/// this language's marker can be registered.
/// </summary>
public interface ILanguage<TMarker> : ILanguage where TMarker : ILanguageMarker
{
    new IReadOnlyList<ITool<TMarker>> Tools { get; }
    IReadOnlyList<ITool> ILanguage.Tools => [.. Tools];
}
