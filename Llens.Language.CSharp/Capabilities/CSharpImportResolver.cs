namespace Llens.Languages.CSharp;

/// <summary>
/// C# import resolver. C# using directives are namespace names, not file paths,
/// so normalization is just deduplication — no filesystem resolution needed.
/// </summary>
public sealed class CSharpImportResolver : IImportResolver<CSharp>
{
    public IReadOnlyList<string> Resolve(string repoRoot, string filePath, IReadOnlyList<string> rawImports)
        => [.. rawImports.Distinct(StringComparer.OrdinalIgnoreCase)];
}
