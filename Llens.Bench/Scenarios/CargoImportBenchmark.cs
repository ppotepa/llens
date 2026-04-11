using System.Diagnostics;
using Llens.Bench.Support;
using Llens.Languages.Rust;

namespace Llens.Bench.Scenarios;

/// <summary>
/// Filesystem oracle (File.Exists) vs CargoImportResolver.
/// Coverage = % of resolved paths that actually exist on disk.
/// Tests both single-crate and workspace resolution.
/// </summary>
public sealed class CargoImportBenchmark : IBenchmarkScenario
{
    public string Name => "Cargo Imports";

    private readonly CargoImportResolver _resolver = new();

    public Task<IReadOnlyList<BenchmarkResult>> RunAsync(Llens.Bench.BenchmarkRunOptions? options = null, CancellationToken ct = default)
    {
        var results = new List<BenchmarkResult>
        {
            RunCase(
                label:      "simple_crate (main)",
                crateRoot:  FixturePaths.Rust("simple_crate"),
                filePath:   FixturePaths.Rust("simple_crate/src/main.rs"),
                rawImports: ["crate::services::order_service", "crate::models::order"],
                expectedDistinctFiles: 2),

            RunCase(
                label:      "workspace crate_b",
                crateRoot:  FixturePaths.Rust("workspace"),
                filePath:   FixturePaths.Rust("workspace/crate_b/src/main.rs"),
                rawImports: ["crate_a::default_config", "crate_a::Config"],
                expectedDistinctFiles: 1),
        };

        return Task.FromResult<IReadOnlyList<BenchmarkResult>>(results);
    }

    private BenchmarkResult RunCase(
        string label,
        string crateRoot,
        string filePath,
        string[] rawImports,
        int expectedDistinctFiles)
    {
        var sw = Stopwatch.StartNew();
        var resolved = _resolver.Resolve(crateRoot, filePath, rawImports);
        sw.Stop();

        var existingDistinct = resolved
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        // Exact resolver score: 100% only when the distinct existing-file count
        // matches expected exactly (penalizes both misses and over-resolution).
        var min = Math.Min(expectedDistinctFiles, existingDistinct);
        var max = Math.Max(expectedDistinctFiles, existingDistinct);
        var coverage = max == 0 ? 100.0 : min / (double)max * 100.0;

        return new BenchmarkResult(
            Scenario:        Name,
            Fixture:         label,
            BaselineCount:   expectedDistinctFiles,
            OurCount:        existingDistinct,
            CoveragePercent: coverage,
            Extra:           existingDistinct - expectedDistinctFiles,
            OurMs:           sw.ElapsedMilliseconds);
    }
}
