using System.Text.RegularExpressions;

namespace Llens.Tests.Support;

/// <summary>
/// Pure .NET implementation of common grep operations.
/// Cross-platform ground truth for comparison tests — no shell, no process spawn,
/// no platform differences between Windows findstr and Linux grep.
/// </summary>
public static class GrepSimulator
{
    /// <summary>
    /// Simulates: grep -n "literal" file
    /// Returns every line containing the literal string.
    /// </summary>
    public static IReadOnlyList<(int Line, string Content)> Literal(
        IReadOnlyList<string> lines,
        string term,
        StringComparison comparison = StringComparison.Ordinal)
        => lines
            .Select((l, i) => (Line: i + 1, Content: l))
            .Where(x => x.Content.Contains(term, comparison))
            .ToList();

    /// <summary>
    /// Simulates: grep -nP "regex" file
    /// Returns every line matched by the pattern.
    /// </summary>
    public static IReadOnlyList<(int Line, string Content)> Pattern(
        IReadOnlyList<string> lines,
        Regex pattern)
        => lines
            .Select((l, i) => (Line: i + 1, Content: l))
            .Where(x => pattern.IsMatch(x.Content))
            .ToList();

    /// <summary>
    /// Simulates: grep -oP "regex" file (token extraction via capture group).
    /// Returns every captured token and the line it appeared on.
    /// </summary>
    public static IReadOnlyList<(int Line, string Token)> Tokens(
        IReadOnlyList<string> lines,
        Regex pattern,
        int group = 1)
        => lines
            .SelectMany((l, i) => pattern
                .Matches(l)
                .Where(m => m.Groups[group].Success)
                .Select(m => (Line: i + 1, Token: m.Groups[group].Value)))
            .ToList();

    /// <summary>
    /// Simulates: grep -c "literal" file
    /// Returns count of matching lines.
    /// </summary>
    public static int Count(IReadOnlyList<string> lines, string term,
        StringComparison comparison = StringComparison.Ordinal)
        => lines.Count(l => l.Contains(term, comparison));
}
