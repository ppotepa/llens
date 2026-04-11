using System.Text.RegularExpressions;
using Llens.Languages.Rust;
using Llens.Tests.Support;
using Xunit;

namespace Llens.Tests.Capabilities.Rust;

/// <summary>
/// Unit tests: known usages in simple_crate fixture files.
/// Comparison tests: our regex extractor vs GrepSimulator for obvious call patterns.
/// Our extractor must be a superset of what grep finds for function calls.
/// </summary>
public sealed class RustUsageExtractorTests
{
    private readonly RustUsageExtractor _extractor = new();

    private static readonly Regex CallPattern =
        new(@"\b([_A-Za-z][_A-Za-z0-9]*)\s*\(", RegexOptions.Compiled);

    // -------------------------------------------------------------------------
    // Unit tests — known-answer
    // -------------------------------------------------------------------------

    [Fact]
    public void MainRs_FindsCreateOrder_Usage()
    {
        var (path, lines) = Load("simple_crate/src/main.rs");
        var usages = _extractor.Extract(path, lines).ToList();

        Assert.Contains(usages, u => u.Token == "create_order");
    }

    [Fact]
    public void MainRs_FindsCancelOrder_Usage()
    {
        var (path, lines) = Load("simple_crate/src/main.rs");
        var usages = _extractor.Extract(path, lines).ToList();

        Assert.Contains(usages, u => u.Token == "cancel_order");
    }

    [Fact]
    public void OrderServiceRs_FindsOrder_TypeAnnotation()
    {
        var (path, lines) = Load("simple_crate/src/services/order_service.rs");
        var usages = _extractor.Extract(path, lines).ToList();

        // "-> Order" and "order: &Order" are type annotations
        Assert.Contains(usages, u => u.Token == "Order");
    }

    [Fact]
    public void OrderServiceRs_FindsOrderStatus_TypeAnnotation()
    {
        var (path, lines) = Load("simple_crate/src/services/order_service.rs");
        var usages = _extractor.Extract(path, lines).ToList();

        Assert.Contains(usages, u => u.Token == "OrderStatus");
    }

    [Fact]
    public void AllUsages_HaveValidLineNumbers()
    {
        var (path, lines) = Load("simple_crate/src/services/order_service.rs");
        var usages = _extractor.Extract(path, lines).ToList();

        Assert.All(usages, u =>
        {
            Assert.True(u.Line >= 1, $"Line {u.Line} < 1 for token '{u.Token}'");
            Assert.True(u.Line <= lines.Count, $"Line {u.Line} > file length {lines.Count}");
        });
    }

    [Fact]
    public void AllUsages_HaveNonEmptyContext()
    {
        var (path, lines) = Load("simple_crate/src/services/order_service.rs");
        var usages = _extractor.Extract(path, lines).ToList();

        Assert.All(usages, u => Assert.False(string.IsNullOrWhiteSpace(u.Context),
            $"Token '{u.Token}' at line {u.Line} has empty context"));
    }

    [Fact]
    public void EmptyFile_ReturnsNoUsages()
    {
        var usages = _extractor.Extract("/fake/empty.rs", []).ToList();
        Assert.Empty(usages);
    }

    // -------------------------------------------------------------------------
    // Comparison tests — our extractor vs GrepSimulator
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("simple_crate/src/main.rs")]
    [InlineData("simple_crate/src/services/order_service.rs")]
    [InlineData("simple_crate/src/models/order.rs")]
    public void OurExtractor_IsSupersetOf_GrepCallPattern(string fixture)
    {
        var (path, lines) = Load(fixture);

        var grepTokens = GrepSimulator.Tokens(lines, CallPattern, group: 1)
            .Select(x => x.Token)
            .Where(t => t.Length >= 2)
            .ToHashSet(StringComparer.Ordinal);

        var ourTokens = _extractor.Extract(path, lines)
            .Select(u => u.Token)
            .ToHashSet(StringComparer.Ordinal);

        // Remove Rust keywords that grep finds but we intentionally filter
        grepTokens.ExceptWith(["fn", "pub", "let", "mut", "if", "for", "while", "impl", "match"]);

        var missed = grepTokens.Except(ourTokens).ToList();
        Assert.True(missed.Count == 0,
            $"Grep found tokens our extractor missed in {fixture}: {string.Join(", ", missed)}");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static (string Path, IReadOnlyList<string> Lines) Load(string fixture)
    {
        var path = Fixtures.Rust(fixture);
        return (path, Fixtures.ReadLines(path));
    }
}
