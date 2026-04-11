using System.Diagnostics;
using System.Text.RegularExpressions;
using Llens.Bench.Support;
using Llens.Languages.Rust;

namespace Llens.Bench.Scenarios;

/// <summary>
/// GrepSimulator call regex vs RustUsageExtractor.
/// Our extractor must be a superset of what grep finds; Extra = AST-only tokens.
/// </summary>
public sealed class RustUsageBenchmark : IBenchmarkScenario
{
    public string Name => "Rust Usage";

    private static readonly Regex CallPattern =
        new(@"\b([_A-Za-z][_A-Za-z0-9]*)\s*\(", RegexOptions.Compiled);

    private static readonly HashSet<string> RustKeywords = new(StringComparer.Ordinal)
        { "fn", "pub", "let", "mut", "if", "for", "while", "impl", "match", "use", "mod" };

    private static readonly string[] Fixtures =
    [
        "simple_crate/src/main.rs",
        "simple_crate/src/services/order_service.rs",
        "simple_crate/src/models/order.rs",
    ];

    private readonly RustUsageExtractor _extractor = new();

    public Task<IReadOnlyList<BenchmarkResult>> RunAsync(Llens.Bench.BenchmarkRunOptions? options = null, CancellationToken ct = default)
    {
        var results = new List<BenchmarkResult>();

        foreach (var fixture in Fixtures)
        {
            var path  = FixturePaths.Rust(fixture);
            var lines = FixturePaths.ReadLines(path);

            // Baseline: GrepSimulator call pattern, filtered to non-keywords
            var baselineTokens = GrepSimulator
                .Tokens(lines, CallPattern, group: 1)
                .Select(x => x.Token)
                .Where(t => t.Length >= 2 && !RustKeywords.Contains(t))
                .ToHashSet(StringComparer.Ordinal);

            // Ours: RustUsageExtractor
            var sw = Stopwatch.StartNew();
            var ourTokens = _extractor.Extract(path, lines)
                .Select(u => u.Token)
                .ToHashSet(StringComparer.Ordinal);
            sw.Stop();

            var missed   = baselineTokens.Except(ourTokens).Count();
            var covered  = baselineTokens.Count - missed;
            var coverage = baselineTokens.Count == 0 ? 100.0
                : covered / (double)baselineTokens.Count * 100.0;

            results.Add(new BenchmarkResult(
                Scenario:        Name,
                Fixture:         Path.GetFileName(fixture),
                BaselineCount:   baselineTokens.Count,
                OurCount:        ourTokens.Count,
                CoveragePercent: coverage,
                Extra:           ourTokens.Count - baselineTokens.Count,
                OurMs:           sw.ElapsedMilliseconds));
        }

        return Task.FromResult<IReadOnlyList<BenchmarkResult>>(results);
    }
}
