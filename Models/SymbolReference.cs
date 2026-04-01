namespace Llens.Models;

/// <summary>
/// A usage of a symbol — where it's referenced, not where it's defined.
/// Pre-computed at index time so "what calls X?" is an instant lookup.
/// </summary>
public class SymbolReference
{
    public required string SymbolId { get; init; }      // the symbol being referenced
    public required string InFilePath { get; init; }    // file containing the reference
    public required string RepoName { get; init; }
    public int Line { get; init; }
    public required string Context { get; init; }       // the line of code containing the reference
}
