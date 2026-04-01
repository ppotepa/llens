namespace Llens.Tools;

/// <summary>
/// Describes what a tool contributes. A single tool can declare multiple capabilities.
/// The indexer dispatches by capability — multiple tools can serve the same one.
/// </summary>
public enum ToolCapability
{
    /// <summary>
    /// Extracts named symbols: classes, interfaces, structs, traits, enums, methods, properties, functions.
    /// Implementations: Roslyn (C#), SynShim (Rust).
    /// </summary>
    SymbolExtraction,

    /// <summary>
    /// Extracts import/dependency relationships between files: using, use, import, require.
    /// Implementations: Roslyn (C#), SynShim (Rust).
    /// </summary>
    ImportExtraction,

    /// <summary>
    /// Tracks cross-file usages of a symbol — who calls or references X.
    /// Requires a full project build context.
    /// Implementations: Roslyn semantic model, rust-analyzer.
    /// </summary>
    ReferenceTracking,

    /// <summary>
    /// Resolves inferred and explicit types across files.
    /// Requires a full project build context.
    /// Implementations: Roslyn semantic model, rust-analyzer.
    /// </summary>
    TypeResolution,

    /// <summary>
    /// Provides embedding-based or natural-language search over the code map.
    /// Implementations: future vector store integration.
    /// </summary>
    SemanticSearch,
}
