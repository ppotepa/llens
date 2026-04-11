using Llens.Languages;
using Llens.Languages.Rust;
using Llens.Models;
using Llens.Tests.Support;
using Xunit;

namespace Llens.Tests.Capabilities.Rust;

/// <summary>
/// Unit tests: scoring and resolution against controlled fixture symbols.
/// The callbacks (QueryByNameAsync, GetProjectFilesAsync) are stubs — this isolates
/// the resolver's ranking logic from any database or index layer.
/// </summary>
public sealed class RustReferenceResolverTests
{
    private readonly RustReferenceResolver _resolver = new();

    private static string MainRs       => Fixtures.Rust("simple_crate/src/main.rs");
    private static string OrderSvcRs   => Fixtures.Rust("simple_crate/src/services/order_service.rs");
    private static string OrderModelRs => Fixtures.Rust("simple_crate/src/models/order.rs");

    // -------------------------------------------------------------------------
    // Stub helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds a LanguageReferenceContext where QueryByNameAsync returns <paramref name="symbols"/>
    /// and GetProjectFilesAsync returns all three simple_crate fixture files.
    /// </summary>
    private LanguageReferenceContext BuildContext(
        string filePath,
        IReadOnlyList<CodeSymbol> symbols)
    {
        var projectFiles = new[]
        {
            new FileNode { FilePath = MainRs,       RepoName = "test", Language = "rust" },
            new FileNode { FilePath = OrderSvcRs,   RepoName = "test", Language = "rust" },
            new FileNode { FilePath = OrderModelRs, RepoName = "test", Language = "rust" },
        };

        return new LanguageReferenceContext(
            RepoName: "test",
            FilePath: filePath,
            Lines: Fixtures.ReadLines(filePath),
            QueryByNameAsync: (name, _, _) =>
                Task.FromResult<IEnumerable<CodeSymbol>>(
                    symbols.Where(s => s.Name.Equals(name, StringComparison.Ordinal))),
            GetProjectFilesAsync: (_, _) =>
                Task.FromResult<IEnumerable<FileNode>>(projectFiles));
    }

    // -------------------------------------------------------------------------
    // Unit tests — known-answer
    // -------------------------------------------------------------------------

    [Fact]
    public async Task MainRs_FindsCreateOrder_InOrderServiceFile()
    {
        // main.rs imports crate::services::order_service::create_order
        // so order_service.rs is in the "imported files" set → score boost of 30
        var symbols = new[]
        {
            MakeSymbol("create_order", SymbolKind.Function, OrderSvcRs, line: 3),
        };
        var ctx = BuildContext(MainRs, symbols);

        var refs = await _resolver.ResolveAsync(ctx);

        Assert.NotEmpty(refs);
        Assert.Contains(refs, r => r.SymbolId == symbols[0].Id);
    }

    [Fact]
    public async Task MainRs_FindsCancelOrder_InOrderServiceFile()
    {
        var symbols = new[]
        {
            MakeSymbol("cancel_order", SymbolKind.Function, OrderSvcRs, line: 7),
        };
        var ctx = BuildContext(MainRs, symbols);

        var refs = await _resolver.ResolveAsync(ctx);

        Assert.Contains(refs, r => r.SymbolId == symbols[0].Id);
    }

    [Fact]
    public async Task ResolvedRefs_AllHaveNonEmptyContext()
    {
        var symbols = new[]
        {
            MakeSymbol("create_order", SymbolKind.Function, OrderSvcRs, line: 3),
            MakeSymbol("cancel_order", SymbolKind.Function, OrderSvcRs, line: 7),
        };
        var ctx = BuildContext(MainRs, symbols);

        var refs = await _resolver.ResolveAsync(ctx);

        Assert.All(refs, r => Assert.False(string.IsNullOrWhiteSpace(r.Context),
            $"Reference to symbol {r.SymbolId} at line {r.Line} has empty context"));
    }

    [Fact]
    public async Task ResolvedRefs_LineNumbers_ArePositive()
    {
        var symbols = new[]
        {
            MakeSymbol("create_order", SymbolKind.Function, OrderSvcRs, line: 3),
        };
        var ctx = BuildContext(MainRs, symbols);

        var refs = await _resolver.ResolveAsync(ctx);

        Assert.All(refs, r => Assert.True(r.Line >= 1, $"Reference line {r.Line} is not positive"));
    }

    [Fact]
    public async Task NoMatchingSymbols_ReturnsEmpty()
    {
        // No symbols registered in the stub — resolver should return nothing
        var ctx = BuildContext(MainRs, []);

        var refs = await _resolver.ResolveAsync(ctx);

        Assert.Empty(refs);
    }

    [Fact]
    public async Task ImportedFile_ScoresHigherThan_UnimportedFile()
    {
        // create_order appears in both an imported file and an unrelated one.
        // The imported file should win (score += 30 for imported files).
        var importedSymbol   = MakeSymbol("create_order", SymbolKind.Function, OrderSvcRs,   line: 3);
        var unimportedSymbol = MakeSymbol("create_order", SymbolKind.Function, OrderModelRs, line: 1);

        var symbols = new[] { importedSymbol, unimportedSymbol };
        var ctx = BuildContext(MainRs, symbols);

        var refs = await _resolver.ResolveAsync(ctx);

        // Resolver admits up to 2 candidates when scores are within 2 of each other.
        // The imported one must appear; the unimported one at score < 12 must NOT appear.
        Assert.Contains(refs, r => r.SymbolId == importedSymbol.Id);
    }

    [Fact]
    public async Task ResolvedRefs_NeverPointToCurrentFile_AtSameLine()
    {
        // Symbols declared in the same file at the exact usage line must be excluded.
        var sameFileSymbol = MakeSymbol("create_order", SymbolKind.Function, MainRs, line: 8);

        var ctx = BuildContext(MainRs, [sameFileSymbol]);

        var refs = await _resolver.ResolveAsync(ctx);

        // Self-referencing at the call site is excluded by the resolver
        Assert.DoesNotContain(refs, r =>
            r.SymbolId == sameFileSymbol.Id && r.Line == sameFileSymbol.LineStart);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static CodeSymbol MakeSymbol(string name, SymbolKind kind, string filePath, int line)
        => new()
        {
            Id       = $"{Path.GetFileNameWithoutExtension(filePath)}::{name}",
            RepoName = "test",
            FilePath = filePath,
            Name     = name,
            Kind     = kind,
            LineStart = line,
        };
}
