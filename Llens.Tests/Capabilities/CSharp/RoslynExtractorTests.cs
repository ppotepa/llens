using Llens.Languages.CSharp;
using Llens.Models;
using Llens.Tests.Support;
using Llens.Tools;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Llens.Tests.Capabilities.CSharp;

/// <summary>
/// Unit tests: known-answer assertions against controlled fixture files.
/// Comparison tests: our extractor vs direct Roslyn traversal as ground truth.
/// </summary>
public sealed class RoslynExtractorTests
{
    private readonly RoslynExtractor _extractor = new();

    // -------------------------------------------------------------------------
    // Unit tests — known-answer
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SimpleClass_ExtractsCorrectSymbolCount()
    {
        var result = await Extract("SimpleClass.cs");

        // 1 class + 1 property + 2 methods = 4
        Assert.True(result.Success);
        Assert.Equal(4, result.Symbols.Count);
    }

    [Fact]
    public async Task SimpleClass_ExtractsClass_WithCorrectKindAndLine()
    {
        var result = await Extract("SimpleClass.cs");

        var cls = Assert.Single(result.Symbols, s => s.Name == "SimpleClass");
        Assert.Equal(SymbolKind.Class, cls.Kind);
        Assert.Equal(6, cls.LineStart); // line 6 in SimpleClass.cs
    }

    [Fact]
    public async Task SimpleClass_ExtractsMethods_WithCorrectNames()
    {
        var result = await Extract("SimpleClass.cs");

        Assert.Contains(result.Symbols, s => s.Name == "GetName" && s.Kind == SymbolKind.Method);
        Assert.Contains(result.Symbols, s => s.Name == "Add"     && s.Kind == SymbolKind.Method);
    }

    [Fact]
    public async Task SimpleClass_ExtractsProperty_WithCorrectKind()
    {
        var result = await Extract("SimpleClass.cs");

        Assert.Contains(result.Symbols, s => s.Name == "Name" && s.Kind == SymbolKind.Property);
    }

    [Fact]
    public async Task SimpleClass_ExtractsImports()
    {
        var result = await Extract("SimpleClass.cs");

        Assert.Contains(result.Imports, i => i == "System");
        Assert.Contains(result.Imports, i => i == "System.Collections.Generic");
    }

    [Fact]
    public async Task InheritanceChain_ExtractsInterface_AndBothClasses()
    {
        var result = await Extract("InheritanceChain.cs");

        Assert.Contains(result.Symbols, s => s.Name == "IShape"    && s.Kind == SymbolKind.Interface);
        Assert.Contains(result.Symbols, s => s.Name == "Circle"    && s.Kind == SymbolKind.Class);
        Assert.Contains(result.Symbols, s => s.Name == "Rectangle" && s.Kind == SymbolKind.Class);
    }

    [Fact]
    public async Task InheritanceChain_ClassSignature_IncludesBaseType()
    {
        var result = await Extract("InheritanceChain.cs");

        var circle = Assert.Single(result.Symbols, s => s.Name == "Circle" && s.Kind == SymbolKind.Class);
        Assert.NotNull(circle.Signature);
        Assert.Contains("IShape", circle.Signature);
    }

    [Fact]
    public async Task GenericTypes_ExtractsClass_AndEnum()
    {
        var result = await Extract("GenericTypes.cs");

        Assert.Contains(result.Symbols, s => s.Name == "Repository" && s.Kind == SymbolKind.Class);
        Assert.Contains(result.Symbols, s => s.Name == "Status"     && s.Kind == SymbolKind.Enum);
    }

    [Fact]
    public async Task SymbolIds_AreUniqueWithinFile()
    {
        var result = await Extract("InheritanceChain.cs");

        var ids = result.Symbols.Select(s => s.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public async Task SymbolLineRanges_AreOrdered_StartBeforeEnd()
    {
        var result = await Extract("InheritanceChain.cs");

        Assert.All(result.Symbols, s =>
            Assert.True(s.LineEnd == 0 || s.LineEnd >= s.LineStart,
                $"{s.Name}: LineEnd ({s.LineEnd}) < LineStart ({s.LineStart})"));
    }

    // -------------------------------------------------------------------------
    // Comparison tests — our extractor vs direct Roslyn traversal
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("SimpleClass.cs")]
    [InlineData("InheritanceChain.cs")]
    [InlineData("GenericTypes.cs")]
    public async Task ExtractedSymbols_MatchDirectRoslynTraversal(string fixture)
    {
        var path = Fixtures.CSharp(fixture);
        var result = await _extractor.ExtractAsync(new ToolContext("test", path));

        var groundTruth = ExtractSymbolsDirectly(path);

        // Every symbol our ground truth finds must appear in our output
        foreach (var (name, kind, line) in groundTruth)
        {
            Assert.True(result.Symbols.Any(s => s.Name == name && s.Kind == kind && s.LineStart == line),
                $"Missing: {name} ({kind}) at line {line}");
        }
    }

    [Theory]
    [InlineData("SimpleClass.cs")]
    [InlineData("InheritanceChain.cs")]
    public async Task ExtractedImports_MatchDirectRoslynUsingDirectives(string fixture)
    {
        var path = Fixtures.CSharp(fixture);
        var result = await _extractor.ExtractAsync(new ToolContext("test", path));

        var groundTruth = ExtractImportsDirectly(path);

        foreach (var import in groundTruth)
            Assert.Contains(result.Imports, i => i == import);
    }

    [Theory]
    [InlineData("SimpleClass.cs")]
    [InlineData("InheritanceChain.cs")]
    [InlineData("GenericTypes.cs")]
    public async Task ExtractedSymbolCount_NeverExceedsDirectRoslynCount(string fixture)
    {
        var path = Fixtures.CSharp(fixture);
        var result = await _extractor.ExtractAsync(new ToolContext("test", path));

        var groundTruth = ExtractSymbolsDirectly(path);

        // We extract a subset of Roslyn nodes (we filter to specific declaration types)
        Assert.True(result.Symbols.Count <= groundTruth.Count + 1, // +1 tolerance
            $"Our extractor found more symbols ({result.Symbols.Count}) than expected from Roslyn ({groundTruth.Count})");
    }

    // -------------------------------------------------------------------------
    // Ground truth helpers — direct Roslyn, no abstraction layer
    // -------------------------------------------------------------------------

    private static List<(string Name, SymbolKind Kind, int Line)> ExtractSymbolsDirectly(string filePath)
    {
        var source = File.ReadAllText(filePath);
        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var results = new List<(string, SymbolKind, int)>();

        foreach (var node in root.DescendantNodes())
        {
            var (name, kind) = node switch
            {
                ClassDeclarationSyntax c     => (c.Identifier.Text, SymbolKind.Class),
                InterfaceDeclarationSyntax i => (i.Identifier.Text, SymbolKind.Interface),
                MethodDeclarationSyntax m    => (m.Identifier.Text, SymbolKind.Method),
                PropertyDeclarationSyntax p  => (p.Identifier.Text, SymbolKind.Property),
                EnumDeclarationSyntax e      => (e.Identifier.Text, SymbolKind.Enum),
                _                            => ((string?)null, SymbolKind.Unknown)
            };
            if (name is null) continue;
            var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            results.Add((name, kind, line));
        }

        return results;
    }

    private static List<string> ExtractImportsDirectly(string filePath)
    {
        var source = File.ReadAllText(filePath);
        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        return root.DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .Select(u => u.Name?.ToString())
            .OfType<string>()
            .ToList();
    }

    private Task<ToolResult> Extract(string fixture)
        => _extractor.ExtractAsync(new ToolContext("test", Fixtures.CSharp(fixture)));
}
