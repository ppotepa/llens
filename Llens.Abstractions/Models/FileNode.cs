namespace Llens.Models;

/// <summary>
/// Represents a file in the code map, including its import/dependency relationships.
/// Enables answering "what does this file use?" and "what uses this file?" without scanning.
/// </summary>
public class FileNode
{
    public required string FilePath { get; init; }
    public required string RepoName { get; init; }
    public required string Language { get; init; }
    public string[] Imports { get; init; } = [];      // files this file imports/uses
    public string[] ImportedBy { get; init; } = [];   // files that import this file
    public int SymbolCount { get; init; }
    public long LastIndexedAt { get; init; }          // unix timestamp
}
