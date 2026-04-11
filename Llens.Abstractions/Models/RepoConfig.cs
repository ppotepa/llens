using Llens.Languages;

namespace Llens.Models;

public class RepoConfig
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public LanguageId[] Languages { get; init; } = [];
    public string[] ExcludePaths { get; init; } = ["bin", "obj", "node_modules", ".git"];
    public string ResolvedPath => System.IO.Path.GetFullPath(Path);
}
