using System.Text.RegularExpressions;
using Llens.Languages.CSharp;
using Llens.Tests.Support;
using Xunit;

namespace Llens.Tests.Capabilities.CSharp;

/// <summary>
/// Unit tests: known usages in MethodCalls.cs.
/// Comparison tests: our AST extractor vs GrepSimulator for obvious call patterns.
/// Our extractor must be a superset of what grep finds for method calls.
/// </summary>
public sealed class RoslynUsageExtractorTests
{
    private readonly RoslynUsageExtractor _extractor = new();

    private static readonly Regex CallPattern =
        new(@"\b([_A-Za-z][_A-Za-z0-9]*)\s*\(", RegexOptions.Compiled);

    private static readonly Regex NewPattern =
        new(@"\bnew\s+([_A-Za-z][_A-Za-z0-9]*)\s*[({]", RegexOptions.Compiled);

    // -------------------------------------------------------------------------
    // Unit tests — known-answer
    // -------------------------------------------------------------------------

    [Fact]
    public void MethodCalls_FindsGetName_Usage()
    {
        var (path, lines) = Load("MethodCalls.cs");
        var usages = _extractor.Extract(path, lines).ToList();

        Assert.Contains(usages, u => u.Token == "GetName");
    }

    [Fact]
    public void MethodCalls_FindsAdd_Usage()
    {
        var (path, lines) = Load("MethodCalls.cs");
        var usages = _extractor.Extract(path, lines).ToList();

        Assert.Contains(usages, u => u.Token == "Add");
    }

    [Fact]
    public void MethodCalls_FindsArea_UsedOnBothShapes()
    {
        var (path, lines) = Load("MethodCalls.cs");
        var usages = _extractor.Extract(path, lines).ToList();

        var areaUsages = usages.Where(u => u.Token == "Area").ToList();
        Assert.True(areaUsages.Count >= 2,
            $"Expected at least 2 usages of 'Area', found {areaUsages.Count}");
    }

    [Fact]
    public void MethodCalls_AllUsages_HaveValidLineNumbers()
    {
        var (path, lines) = Load("MethodCalls.cs");
        var usages = _extractor.Extract(path, lines).ToList();

        Assert.All(usages, u =>
        {
            Assert.True(u.Line >= 1, $"Line {u.Line} < 1 for token '{u.Token}'");
            Assert.True(u.Line <= lines.Count, $"Line {u.Line} > file length {lines.Count}");
        });
    }

    [Fact]
    public void MethodCalls_AllUsages_HaveNonEmptyContext()
    {
        var (path, lines) = Load("MethodCalls.cs");
        var usages = _extractor.Extract(path, lines).ToList();

        Assert.All(usages, u => Assert.False(string.IsNullOrWhiteSpace(u.Context),
            $"Token '{u.Token}' at line {u.Line} has empty context"));
    }

    [Fact]
    public void MethodCalls_DoesNotEmitDeclarationTokens()
    {
        var (path, lines) = Load("MethodCalls.cs");
        var usages = _extractor.Extract(path, lines).ToList();

        // "MethodCalls" is the class name — a declaration, not a usage
        Assert.DoesNotContain(usages, u => u.Token == "MethodCalls" && u.Line == 7);
    }

    // -------------------------------------------------------------------------
    // Comparison tests — our extractor vs GrepSimulator
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("MethodCalls.cs")]
    [InlineData("SimpleClass.cs")]
    public void OurExtractor_IsSupersetOf_GrepCallPattern(string fixture)
    {
        var (path, lines) = Load(fixture);

        var grepLines = GrepSimulator.Tokens(lines, CallPattern, group: 1)
            .Select(x => x.Token)
            .Where(t => t.Length >= 3)
            .ToHashSet(StringComparer.Ordinal);

        var ourTokens = _extractor.Extract(path, lines)
            .Select(u => u.Token)
            .ToHashSet(StringComparer.Ordinal);

        // Remove known false positives grep finds that we intentionally filter
        grepLines.ExceptWith(["var", "new", "return", "if", "for", "while", "foreach"]);

        var missed = grepLines.Except(ourTokens).ToList();
        Assert.True(missed.Count == 0,
            $"Grep found tokens our extractor missed in {fixture}: {string.Join(", ", missed)}");
    }

    [Theory]
    [InlineData("MethodCalls.cs")]
    public void OurExtractor_FindsNewExpressions_ThatGrepAlsoFinds(string fixture)
    {
        var (path, lines) = Load(fixture);

        var grepTypes = GrepSimulator.Tokens(lines, NewPattern, group: 1)
            .Select(x => x.Token)
            .ToHashSet(StringComparer.Ordinal);

        var ourTokens = _extractor.Extract(path, lines)
            .Select(u => u.Token)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var t in grepTypes)
            Assert.Contains(t, ourTokens);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static (string Path, IReadOnlyList<string> Lines) Load(string fixture)
    {
        var path = Fixtures.CSharp(fixture);
        return (path, Fixtures.ReadLines(path));
    }
}
