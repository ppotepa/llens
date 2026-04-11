using System.Diagnostics;
using System.Text.RegularExpressions;
using Llens.Bench.Support;
using Llens.Languages.CSharp;

namespace Llens.Bench.Scenarios;

/// <summary>
/// GrepSimulator call regex vs RoslynUsageExtractor.
/// Our Roslyn-based extractor should be a superset of what grep finds for method calls.
/// </summary>
public sealed class CSharpUsageBenchmark : IBenchmarkScenario
{
    public string Name => "C# Usage";

    private static readonly Regex CallPattern =
        new(@"\b([_A-Za-z][_A-Za-z0-9]*)\s*\(", RegexOptions.Compiled);

    private static readonly Regex MethodDeclarationPattern =
        new(@"^\s*(?:public|private|protected|internal|static|virtual|override|sealed|abstract|async|partial|\s)+[\w<>\[\],\s\?]+\s+([_A-Za-z][_A-Za-z0-9]*)\s*\(",
            RegexOptions.Compiled);

    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
        { "var", "new", "return", "if", "for", "while", "foreach", "using", "namespace", "class" };

    private static readonly string[] Fixtures = ["SimpleClass.cs", "MethodCalls.cs"];

    private readonly RoslynUsageExtractor _extractor = new();

    public Task<IReadOnlyList<BenchmarkResult>> RunAsync(Llens.Bench.BenchmarkRunOptions? options = null, CancellationToken ct = default)
    {
        var results = new List<BenchmarkResult>();

        foreach (var fixture in Fixtures)
        {
            var path  = FixturePaths.CSharp(fixture);
            var lines = FixturePaths.ReadLines(path);

            var baselineTokens = GrepSimulator
                .Tokens(lines, CallPattern, group: 1)
                .Where(x =>
                {
                    var token = x.Token;
                    if (token.Length < 3 || CSharpKeywords.Contains(token))
                        return false;

                    var rawLine = lines[x.Line - 1];
                    return !IsMethodDeclaration(rawLine, token);
                })
                .Select(x => x.Token)
                .ToHashSet(StringComparer.Ordinal);

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
                Fixture:         fixture,
                BaselineCount:   baselineTokens.Count,
                OurCount:        ourTokens.Count,
                CoveragePercent: coverage,
                Extra:           ourTokens.Count - baselineTokens.Count,
                OurMs:           sw.ElapsedMilliseconds));
        }

        return Task.FromResult<IReadOnlyList<BenchmarkResult>>(results);
    }

    private static bool IsMethodDeclaration(string line, string token)
    {
        var m = MethodDeclarationPattern.Match(line);
        return m.Success && string.Equals(m.Groups[1].Value, token, StringComparison.Ordinal);
    }
}
