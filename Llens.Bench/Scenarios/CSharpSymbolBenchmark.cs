using System.Diagnostics;
using Llens.Bench.Support;
using Llens.Languages.CSharp;
using Llens.Models;
using Llens.Tools;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Llens.Bench.Scenarios;

/// <summary>
/// Direct Roslyn AST traversal (ground truth) vs RoslynExtractor.
/// Coverage = % of ground-truth symbols our extractor finds.
/// </summary>
public sealed class CSharpSymbolBenchmark : IBenchmarkScenario
{
    public string Name => "C# Symbols";

    private static readonly string[] Fixtures =
        ["SimpleClass.cs", "InheritanceChain.cs", "GenericTypes.cs"];

    private readonly RoslynExtractor _extractor = new();

    public async Task<IReadOnlyList<BenchmarkResult>> RunAsync(Llens.Bench.BenchmarkRunOptions? options = null, CancellationToken ct = default)
    {
        var results = new List<BenchmarkResult>();

        foreach (var fixture in Fixtures)
        {
            var path = FixturePaths.CSharp(fixture);

            // Baseline: direct Roslyn traversal — no abstraction layer
            var groundTruth = ExtractDirectly(path);

            // Ours: RoslynExtractor via capability interface
            var sw = Stopwatch.StartNew();
            var result = await _extractor.ExtractAsync(new ToolContext("bench", path), ct);
            sw.Stop();

            var ourNames = result.Symbols
                .Select(s => (s.Name, s.Kind))
                .ToHashSet();

            var covered  = groundTruth.Count(gt => ourNames.Contains((gt.Name, gt.Kind)));
            var coverage = groundTruth.Count == 0 ? 100.0
                : covered / (double)groundTruth.Count * 100.0;

            results.Add(new BenchmarkResult(
                Scenario:        Name,
                Fixture:         fixture,
                BaselineCount:   groundTruth.Count,
                OurCount:        result.Symbols.Count,
                CoveragePercent: coverage,
                Extra:           result.Symbols.Count - groundTruth.Count,
                OurMs:           sw.ElapsedMilliseconds));
        }

        return results;
    }

    private static List<(string Name, SymbolKind Kind)> ExtractDirectly(string filePath)
    {
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(filePath)).GetRoot();
        var results = new List<(string, SymbolKind)>();

        foreach (var node in root.DescendantNodes())
        {
            var entry = node switch
            {
                ClassDeclarationSyntax c     => (c.Identifier.Text, SymbolKind.Class),
                InterfaceDeclarationSyntax i => (i.Identifier.Text, SymbolKind.Interface),
                MethodDeclarationSyntax m    => (m.Identifier.Text, SymbolKind.Method),
                PropertyDeclarationSyntax p  => (p.Identifier.Text, SymbolKind.Property),
                EnumDeclarationSyntax e      => (e.Identifier.Text, SymbolKind.Enum),
                _                            => ((string?)null, SymbolKind.Unknown)
            };
            if (entry.Item1 is not null)
                results.Add((entry.Item1, entry.Item2));
        }

        return results;
    }
}
