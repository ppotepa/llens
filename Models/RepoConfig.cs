namespace Llens.Models;

public class RepoConfig
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public string[] IncludeExtensions { get; init; } = [".cs", ".ts", ".py", ".go", ".js", ".tsx"];
    public string[] ExcludePaths { get; init; } = ["bin", "obj", "node_modules", ".git"];
}
