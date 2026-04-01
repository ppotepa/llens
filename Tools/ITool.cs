using Llens.Languages;

namespace Llens.Tools;

/// <summary>
/// Non-generic base — used by the registry and indexer to handle tools polymorphically.
/// </summary>
public interface ITool
{
    ToolKind Kind { get; }
    ToolPurpose Purpose { get; }
    Task<ToolResult> ExecuteAsync(ToolContext context, CancellationToken ct = default);
}

/// <summary>
/// Generic typed tool — binds the tool to a specific language at compile time.
/// Prevents accidentally registering e.g. a Rust tool inside a C# language.
/// </summary>
public interface ITool<TLanguage> : ITool where TLanguage : ILanguageMarker { }
