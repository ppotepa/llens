using Llens.Bench.Scenarios;
using Xunit;

namespace Llens.Tests;

public sealed class WorkflowTokenSavingsTests
{
    [Fact]
    public async Task WorkflowComparison_ReportsTokens_ForEveryResult()
    {
        var scenario = new WorkflowComparisonBenchmark();
        var results = await scenario.RunAsync();

        Assert.NotEmpty(results);
        Assert.All(results, r =>
        {
            Assert.True(r.BaselineTokens.HasValue, $"Missing BaselineTokens for scenario '{r.Scenario}'.");
            Assert.True(r.OurTokens.HasValue, $"Missing OurTokens for scenario '{r.Scenario}'.");
            Assert.True(r.BaselineTokens > 0, $"BaselineTokens <= 0 for scenario '{r.Scenario}'.");
            Assert.True(r.OurTokens > 0, $"OurTokens <= 0 for scenario '{r.Scenario}'.");
        });
    }

    [Fact]
    public async Task WorkflowComparison_SavesTokens_AgainstClassicBaseline()
    {
        var scenario = new WorkflowComparisonBenchmark();
        var results = await scenario.RunAsync();

        Assert.NotEmpty(results);
        var tokenized = results.Where(r => r.BaselineTokens.HasValue && r.OurTokens.HasValue).ToList();
        Assert.NotEmpty(tokenized);

        var wins = tokenized.Count(r => r.OurTokens <= r.BaselineTokens);
        var winRate = (double)wins / tokenized.Count;
        var avgSavedTokens = tokenized.Average(r => r.BaselineTokens!.Value - r.OurTokens!.Value);

        Assert.True(
            winRate >= 0.90,
            $"Expected >= 90% token wins, got {wins}/{tokenized.Count} ({winRate:P1}).");
        Assert.True(
            avgSavedTokens > 0,
            $"Expected positive avg token savings, got {avgSavedTokens:F1}.");
    }
}
