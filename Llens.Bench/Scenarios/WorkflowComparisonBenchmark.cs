using System.Diagnostics;
using System.Text.RegularExpressions;
using Llens.Bench.Support;
using Llens.Languages.CSharp;
using Llens.Tools;

namespace Llens.Bench.Scenarios;

/// <summary>
/// End-to-end retrieval workflow comparison:
/// - Baseline: grep-style broad scan + full-file read.
/// - Llens: symbol resolve + focused span read (with fallback search).
/// </summary>
public sealed class WorkflowComparisonBenchmark : IBenchmarkScenario
{
    public string Name => "Workflow Compare";

    private readonly RoslynExtractor _csharpExtractor = new();
    private Dictionary<string, IReadOnlyList<string>>? _warmCorpus;
    private List<SymbolEntry>? _warmIndex;

    private static readonly Regex RustDefinitionRegex =
        new(@"^\s*(?:pub\s+)?(?:fn|struct|enum)\s+([_A-Za-z][_A-Za-z0-9]*)", RegexOptions.Compiled);

    private static readonly string[] CSharpFiles =
    [
        "SimpleClass.cs",
        "MethodCalls.cs",
        "InheritanceChain.cs",
        "GenericTypes.cs",
    ];

    private static readonly string[] RustFiles =
    [
        "simple_crate/src/main.rs",
        "simple_crate/src/services/order_service.rs",
        "simple_crate/src/models/order.rs",
    ];

    private static readonly WorkflowTask[] Tasks =
    [
        new("GetName", "CSharp/SimpleClass.cs", "GetName"),
        new("RunAll", "CSharp/MethodCalls.cs", "RunAll"),
        new("Area", "CSharp/InheritanceChain.cs", "Area"),
        new("Describe", "CSharp/InheritanceChain.cs", "Describe"),
        new("Repository", "CSharp/GenericTypes.cs", "Repository"),
        new("Find", "CSharp/GenericTypes.cs", "Find"),
        new("Status", "CSharp/GenericTypes.cs", "Status"),
        new("create_order", "Rust/simple_crate/src/services/order_service.rs", "create_order"),
        new("cancel_order", "Rust/simple_crate/src/services/order_service.rs", "cancel_order"),
        new("is_valid", "Rust/simple_crate/src/models/order.rs", "is_valid"),
        new("OrderStatus", "Rust/simple_crate/src/models/order.rs", "OrderStatus"),
    ];

    public async Task<IReadOnlyList<BenchmarkResult>> RunAsync(Llens.Bench.BenchmarkRunOptions? options = null, CancellationToken ct = default)
    {
        options ??= new Llens.Bench.BenchmarkRunOptions();

        var corpus = options.UseWarmCaches && _warmCorpus is not null
            ? _warmCorpus
            : BuildCorpus();
        if (options.UseWarmCaches && _warmCorpus is null)
            _warmCorpus = corpus;

        var index = options.UseWarmCaches && _warmIndex is not null
            ? _warmIndex
            : await BuildSymbolIndexAsync(corpus, ct);
        if (options.UseWarmCaches && _warmIndex is null)
            _warmIndex = index;

        var results = new List<BenchmarkResult>(Tasks.Length);

        foreach (var task in Tasks)
        {
            var activeIndex = index;
            if (!options.UseWarmCaches)
                activeIndex = await BuildSymbolIndexAsync(corpus, ct);

            var baseline = RunTraditional(task, corpus);
            var ours = RunLlens(task, corpus, activeIndex);
            var success = ours.Success;

            var note = success
                ? "resolved"
                : $"missed target (picked: {ours.SelectedRelativePath ?? "none"})";

            results.Add(new BenchmarkResult(
                Scenario: task.Query,
                Fixture: task.ExpectedRelativePath,
                BaselineCount: baseline.HitCount,
                OurCount: ours.HitCount,
                CoveragePercent: success ? 100.0 : 0.0,
                Extra: ours.HitCount - baseline.HitCount,
                OurMs: ours.ElapsedMs,
                IsWorkflow: true,
                BaselineMs: baseline.ElapsedMs,
                BaselineTokens: baseline.TokenEstimate,
                OurTokens: ours.TokenEstimate,
                BaselineCalls: baseline.CallCount,
                OurCalls: ours.CallCount,
                Success: success,
                Notes: note));
        }

        return results;
    }

    private static Dictionary<string, IReadOnlyList<string>> BuildCorpus()
    {
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var rel in CSharpFiles)
        {
            var path = FixturePaths.CSharp(rel);
            map[ToRelative(path)] = FixturePaths.ReadLines(path);
        }

        foreach (var rel in RustFiles)
        {
            var path = FixturePaths.Rust(rel);
            map[ToRelative(path)] = FixturePaths.ReadLines(path);
        }

        return map;
    }

    private async Task<List<SymbolEntry>> BuildSymbolIndexAsync(
        Dictionary<string, IReadOnlyList<string>> corpus,
        CancellationToken ct)
    {
        var index = new List<SymbolEntry>(128);

        foreach (var rel in CSharpFiles)
        {
            var full = FixturePaths.CSharp(rel);
            var result = await _csharpExtractor.ExtractAsync(new ToolContext("bench", full), ct);
            foreach (var s in result.Symbols)
                index.Add(new SymbolEntry(s.Name, ToRelative(s.FilePath), Math.Max(1, s.LineStart)));
        }

        foreach (var rel in RustFiles)
        {
            var full = FixturePaths.Rust(rel);
            var relative = ToRelative(full);
            if (!corpus.TryGetValue(relative, out var lines)) continue;
            for (var i = 0; i < lines.Count; i++)
            {
                var m = RustDefinitionRegex.Match(lines[i]);
                if (m.Success)
                    index.Add(new SymbolEntry(m.Groups[1].Value, relative, i + 1));
            }
        }

        return index;
    }

    private static RunOutcome RunTraditional(WorkflowTask task, Dictionary<string, IReadOnlyList<string>> corpus)
    {
        var sw = Stopwatch.StartNew();
        var callCount = 1; // broad grep-like scan
        var scannedTokens = 0;
        var hits = new List<Hit>();

        foreach (var (rel, lines) in corpus)
        {
            foreach (var line in lines)
                scannedTokens += EstimateTokens(line);

            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].Contains(task.Query, StringComparison.OrdinalIgnoreCase))
                    hits.Add(new Hit(rel, i + 1));
            }
        }

        var selected = hits.FirstOrDefault();
        var readTokens = 0;
        var evidenceSeen = false;
        if (selected is not null && corpus.TryGetValue(selected.RelativePath, out var selectedFileLines))
        {
            callCount++; // full read of selected file
            readTokens = EstimateTokens(selectedFileLines);
            evidenceSeen = selectedFileLines.Any(l => l.Contains(task.ExpectedEvidence, StringComparison.Ordinal));
        }

        sw.Stop();
        var success = selected is not null
            && selected.RelativePath.Equals(task.ExpectedRelativePath, StringComparison.OrdinalIgnoreCase)
            && evidenceSeen;

        return new RunOutcome(
            Success: success,
            HitCount: hits.Count,
            ElapsedMs: sw.ElapsedMilliseconds,
            TokenEstimate: scannedTokens + readTokens,
            CallCount: callCount,
            SelectedRelativePath: selected?.RelativePath);
    }

    private static RunOutcome RunLlens(
        WorkflowTask task,
        Dictionary<string, IReadOnlyList<string>> corpus,
        List<SymbolEntry> index)
    {
        var sw = Stopwatch.StartNew();
        var calls = 1; // resolve
        var resolvePayloadTokens = 0;

        var candidates = index
            .Where(s => s.Name.Contains(task.Query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => Score(s.Name, task.Query))
            .Take(5)
            .ToList();

        resolvePayloadTokens += EstimateTokens(string.Join('\n', candidates.Select(c => $"{c.Name}|{c.RelativePath}|{c.Line}")));

        SymbolEntry? selected = candidates.FirstOrDefault();
        if (selected is null)
        {
            // fallback text search
            calls++;
            var fallback = FindFirstTextHit(task.Query, corpus);
            if (fallback is not null)
                selected = new SymbolEntry(task.Query, fallback.RelativePath, fallback.Line);
        }

        var spanTokens = 0;
        var evidenceSeen = false;
        if (selected is not null && corpus.TryGetValue(selected.RelativePath, out var selectedFileLines))
        {
            calls++; // focused span read
            var (from, to) = ClampSpan(selected.Line, selectedFileLines.Count, radius: 4);
            var span = selectedFileLines.Skip(from - 1).Take(to - from + 1).ToList();
            spanTokens = EstimateTokens(span);
            evidenceSeen = span.Any(l => l.Contains(task.ExpectedEvidence, StringComparison.Ordinal));
        }

        sw.Stop();
        var success = selected is not null
            && selected.RelativePath.Equals(task.ExpectedRelativePath, StringComparison.OrdinalIgnoreCase)
            && evidenceSeen;

        return new RunOutcome(
            Success: success,
            HitCount: candidates.Count,
            ElapsedMs: sw.ElapsedMilliseconds,
            TokenEstimate: resolvePayloadTokens + spanTokens,
            CallCount: calls,
            SelectedRelativePath: selected?.RelativePath);
    }

    private static Hit? FindFirstTextHit(string query, Dictionary<string, IReadOnlyList<string>> corpus)
    {
        foreach (var (rel, lines) in corpus)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                    return new Hit(rel, i + 1);
            }
        }
        return null;
    }

    private static int Score(string symbolName, string query)
    {
        if (symbolName.Equals(query, StringComparison.OrdinalIgnoreCase)) return 300;
        if (symbolName.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 200;
        if (symbolName.Contains(query, StringComparison.OrdinalIgnoreCase)) return 100;
        return 0;
    }

    private static (int From, int To) ClampSpan(int center, int maxLines, int radius)
    {
        var from = Math.Max(1, center - radius);
        var to = Math.Min(maxLines, center + radius);
        return (from, to);
    }

    private static string ToRelative(string absolutePath)
    {
        var normalized = absolutePath.Replace('\\', '/');
        var marker = normalized.IndexOf("/Fixtures/", StringComparison.OrdinalIgnoreCase);
        return marker >= 0 ? normalized[(marker + "/Fixtures/".Length)..] : normalized;
    }

    private static int EstimateTokens(IEnumerable<string> lines)
        => EstimateTokens(string.Join('\n', lines));

    private static int EstimateTokens(string text)
        => Math.Max(1, text.Length / 4);

    private sealed record WorkflowTask(string Query, string ExpectedRelativePath, string ExpectedEvidence);
    private sealed record SymbolEntry(string Name, string RelativePath, int Line);
    private sealed record Hit(string RelativePath, int Line);

    private sealed record RunOutcome(
        bool Success,
        int HitCount,
        long ElapsedMs,
        int TokenEstimate,
        int CallCount,
        string? SelectedRelativePath);
}
