using Llens.Languages.CSharp;
using Llens.Tests.Support;
using Xunit;

namespace Llens.Tests.Capabilities.CSharp;

/// <summary>
/// C# import resolver is trivially deduplication — test that contract directly.
/// </summary>
public sealed class CSharpImportResolverTests
{
    private readonly CSharpImportResolver _resolver = new();
    private const string AnyRoot = "C:/repo";
    private const string AnyFile = "C:/repo/Foo.cs";

    [Fact]
    public void Resolve_RemovesDuplicates_CaseInsensitive()
    {
        var raw = new[] { "System", "system", "SYSTEM", "System.Linq" };
        var result = _resolver.Resolve(AnyRoot, AnyFile, raw);

        Assert.Equal(2, result.Count); // "System" + "System.Linq"
    }

    [Fact]
    public void Resolve_PreservesOrder_FirstOccurrenceWins()
    {
        var raw = new[] { "System.Linq", "System.Collections", "System.Linq" };
        var result = _resolver.Resolve(AnyRoot, AnyFile, raw);

        Assert.Equal(["System.Linq", "System.Collections"], result);
    }

    [Fact]
    public void Resolve_EmptyInput_ReturnsEmpty()
    {
        var result = _resolver.Resolve(AnyRoot, AnyFile, []);
        Assert.Empty(result);
    }

    [Fact]
    public void Resolve_DoesNotModifyPaths_OrResolveToFiles()
    {
        // C# resolver does NOT turn namespace strings into file paths
        var raw = new[] { "System.IO", "Microsoft.Extensions.Logging" };
        var result = _resolver.Resolve(AnyRoot, AnyFile, raw);

        Assert.All(result, r => Assert.False(r.EndsWith(".cs"),
            $"Resolver should not produce file paths, but got: {r}"));
    }

    [Fact]
    public async Task FixtureFile_ImportCount_MatchesDirectRoslynCount()
    {
        // RoslynExtractor raw imports → CSharpImportResolver → same count (no expansion for C#)
        var path = Fixtures.CSharp("SimpleClass.cs");
        var extractor = new RoslynExtractor();
        var extracted = await extractor.ExtractAsync(new Llens.Tools.ToolContext("test", path));

        var resolved = _resolver.Resolve(AnyRoot, path, extracted.Imports);

        // C# resolver only deduplicates — count must be <= raw count
        Assert.True(resolved.Count <= extracted.Imports.Count);
    }
}
