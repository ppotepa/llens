namespace Llens.Tests.Support;

/// <summary>
/// Resolves fixture file paths relative to the test output directory.
/// Fixtures are copied to output via CopyToOutputDirectory in the csproj.
/// </summary>
public static class Fixtures
{
    private static readonly string Root = Path.Combine(
        AppContext.BaseDirectory, "Fixtures");

    public static string CSharp(string relativePath)
        => Path.GetFullPath(Path.Combine(Root, "CSharp", relativePath));

    public static string Rust(string relativePath)
        => Path.GetFullPath(Path.Combine(Root, "Rust", relativePath));

    public static string[] ReadLines(string absolutePath)
        => File.ReadAllLines(absolutePath);
}
