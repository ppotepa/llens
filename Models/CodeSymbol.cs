namespace Llens.Models;

public class CodeSymbol
{
    public required string Id { get; init; }
    public required string RepoName { get; init; }
    public required string FilePath { get; init; }
    public required string Name { get; init; }
    public required SymbolKind Kind { get; init; }
    public int LineStart { get; init; }
    public int LineEnd { get; init; }
    public string? Signature { get; init; }
    public string? DocComment { get; init; }
    public string[] References { get; init; } = [];
}

public enum SymbolKind
{
    Class,
    Interface,
    Method,
    Property,
    Field,
    Enum,
    Unknown
}
