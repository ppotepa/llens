using Llens.Bench.TaskPacks;
using Xunit;

namespace Llens.Tests;

public sealed class AgentHistoryTaskPackTests
{
    private const string PackRelativePath = "Llens.Bench/TaskPacks/agent-rust-csharp-history-100.tasks.json";
    private static readonly string[] SupportedKinds =
        ["history_latest_touch", "history_first_touch", "history_touch_count"];

    [Fact]
    public void AgentHistoryPack_HasExpectedShape()
    {
        var pack = TaskPackLoader.Load(GetPackPath());

        Assert.Equal("agent-rust-csharp-history-100", pack.Name);
        Assert.Equal(100, pack.Tasks.Count);
        Assert.All(pack.Tasks, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Id));
            Assert.Contains(t.Kind, SupportedKinds);
            var isCs = t.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
            var isRs = t.Path.EndsWith(".rs", StringComparison.OrdinalIgnoreCase);
            Assert.True(isCs || isRs, $"Unsupported path extension in task '{t.Id}': {t.Path}");
        });
    }

    [Fact]
    public void AgentHistoryPack_BalancesCSharpAndRust()
    {
        var pack = TaskPackLoader.Load(GetPackPath());

        var cs = pack.Tasks.Count(t => t.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
        var rs = pack.Tasks.Count(t => t.Path.EndsWith(".rs", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(50, cs);
        Assert.Equal(50, rs);
        Assert.Equal(pack.Tasks.Count, pack.Tasks.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count());
    }

    private static string GetPackPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, PackRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not locate task pack: {PackRelativePath}");
    }
}
