namespace Llens.Bench.Support;

internal static class FixturePaths
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static string CSharp(string relativePath)
        => Path.GetFullPath(Path.Combine(Root, "CSharp", relativePath));

    public static string Rust(string relativePath)
        => Path.GetFullPath(Path.Combine(Root, "Rust", relativePath));

    public static IReadOnlyList<string> ReadLines(string absolutePath)
        => File.ReadAllLines(absolutePath);
}
