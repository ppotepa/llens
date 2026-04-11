using System.Text.RegularExpressions;

namespace Llens.Bench.Support;

/// <summary>
/// Pure .NET simulation of grep — no shell spawn, fully cross-platform.
/// Mirrors the version in Llens.Tests so benchmarks don't depend on the test project.
/// </summary>
internal static class GrepSimulator
{
    public static IReadOnlyList<(int Line, string Token)> Tokens(
        IReadOnlyList<string> lines,
        Regex pattern,
        int group = 1)
    {
        var results = new List<(int, string)>();
        for (var i = 0; i < lines.Count; i++)
        {
            foreach (Match m in pattern.Matches(lines[i]))
            {
                var value = m.Groups[group].Value;
                if (!string.IsNullOrWhiteSpace(value))
                    results.Add((i + 1, value));
            }
        }
        return results;
    }
}
