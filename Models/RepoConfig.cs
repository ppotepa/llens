namespace Llens.Models;

public class RepoConfig
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public string[] Languages { get; init; } = ["CSharp"];
    public string[] ExcludePaths { get; init; } = ["bin", "obj", "node_modules", ".git"];
}
