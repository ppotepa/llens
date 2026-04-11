namespace Llens.Bench.Scenarios;

public interface IBenchmarkScenario
{
    string Name { get; }
    Task<IReadOnlyList<BenchmarkResult>> RunAsync(Llens.Bench.BenchmarkRunOptions? options = null, CancellationToken ct = default);
}
