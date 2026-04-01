namespace Llens.Tools;

public record ToolContext(
    string RepoName,
    string FilePath,
    string? WorkspaceRoot = null
);
