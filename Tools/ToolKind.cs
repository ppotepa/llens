namespace Llens.Tools;

public enum ToolKind
{
    // C#
    Roslyn,

    // Rust
    SynShim,
    RustAnalyzer,
    RustdocJson,

    // Multi-language
    TreeSitter
}
