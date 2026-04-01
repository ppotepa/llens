namespace Llens.Tools;

public enum ToolPurpose
{
    /// <summary>Parse files into symbols and populate the code map.</summary>
    Indexing,

    /// <summary>Resolve cross-file/cross-crate references for a symbol.</summary>
    ReferenceResolution,

    /// <summary>Provide semantic type information (requires full build context).</summary>
    SemanticAnalysis,

    /// <summary>Answer natural-language or structured queries over the code map.</summary>
    Search
}
