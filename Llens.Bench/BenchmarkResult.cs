namespace Llens.Bench;

/// <summary>
/// Result of running one scenario against one fixture file.
/// </summary>
public sealed record BenchmarkResult(
    string Scenario,
    string Fixture,
    int BaselineCount,    // what the ground-truth tool finds (grep / direct Roslyn)
    int OurCount,         // what our capability extractor finds
    double CoveragePercent, // % of baseline we capture (100 = full superset, goal is ≥100)
    int Extra,            // OurCount - BaselineCount: tokens the AST finds beyond grep
    long OurMs,           // wall-clock time for our extractor
    bool IsWorkflow = false,
    long? BaselineMs = null,
    int? BaselineTokens = null,
    int? OurTokens = null,
    int? HybridTokens = null,
    int? BaselineCalls = null,
    int? OurCalls = null,
    int? HybridCalls = null,
    bool? Success = null,
    string? Notes = null,
    string Temperature = "warm",
    string? BaselineInput = null,
    string? BaselineOutput = null,
    string? OurInput = null,
    string? OurOutput = null,
    string? HybridInput = null,
    string? HybridOutput = null,
    string? TraceJson = null)
{
    public bool Passed => Success ?? CoveragePercent >= 100.0;
}
