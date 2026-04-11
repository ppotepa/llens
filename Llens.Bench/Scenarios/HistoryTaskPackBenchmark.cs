using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Text.Json;
using Llens.Bench.TaskPacks;

namespace Llens.Bench.Scenarios;

/// <summary>
/// Runs history-aware benchmark tasks from a JSON task pack.
/// Compares broad/traditional git queries (baseline) vs bounded/targeted queries (ours).
/// </summary>
public sealed class HistoryTaskPackBenchmark : IBenchmarkScenario
{
    public string Name => "History TaskPack";

    private readonly string _repoPath;
    private readonly string _taskPackPath;

    private static readonly Regex HashRegex = new("^[0-9a-f]{7,40}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public HistoryTaskPackBenchmark(string repoPath, string taskPackPath)
    {
        _repoPath = repoPath;
        _taskPackPath = taskPackPath;
    }

    public Task<IReadOnlyList<BenchmarkResult>> RunAsync(BenchmarkRunOptions? options = null, CancellationToken ct = default)
    {
        options ??= new BenchmarkRunOptions();

        var results = new List<BenchmarkResult>();
        if (!Directory.Exists(_repoPath))
        {
            results.Add(new BenchmarkResult(
                Scenario: Name,
                Fixture: _repoPath,
                BaselineCount: 0,
                OurCount: 0,
                CoveragePercent: 0,
                Extra: 0,
                OurMs: 0,
                IsWorkflow: true,
                Success: false,
                Notes: $"repo not found: {_repoPath}"));
            return Task.FromResult<IReadOnlyList<BenchmarkResult>>(results);
        }

        TaskPack pack;
        try
        {
            pack = TaskPackLoader.Load(_taskPackPath);
        }
        catch (Exception ex)
        {
            results.Add(new BenchmarkResult(
                Scenario: Name,
                Fixture: _taskPackPath,
                BaselineCount: 0,
                OurCount: 0,
                CoveragePercent: 0,
                Extra: 0,
                OurMs: 0,
                IsWorkflow: true,
                Success: false,
                Notes: $"task pack load failed: {ex.Message}"));
            return Task.FromResult<IReadOnlyList<BenchmarkResult>>(results);
        }

        foreach (var task in pack.Tasks)
        {
            ct.ThrowIfCancellationRequested();
            var filePath = task.Path.Replace('\\', '/');
            var fullPath = Path.Combine(_repoPath, filePath.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(fullPath))
            {
                results.Add(new BenchmarkResult(
                    Scenario: task.Id,
                    Fixture: filePath,
                    BaselineCount: 0,
                    OurCount: 0,
                    CoveragePercent: 0,
                    Extra: 0,
                    OurMs: 0,
                    IsWorkflow: true,
                    Success: false,
                    Notes: "fixture path missing"));
                continue;
            }

            var baseline = RunBaseline(task.Kind, filePath);
            var ours = RunOurs(task.Kind, filePath);
            var hybrid = RunHybrid(task.Kind, filePath, baseline, ours);
            var success = Evaluate(task.Kind, baseline.Output, ours.Output);
            var traceJson = BuildTrace(task, baseline, ours, hybrid);

            results.Add(new BenchmarkResult(
                Scenario: task.Id,
                Fixture: filePath,
                BaselineCount: baseline.LineCount,
                OurCount: ours.LineCount,
                CoveragePercent: success ? 100.0 : 0.0,
                Extra: ours.LineCount - baseline.LineCount,
                OurMs: ours.ElapsedMs,
                IsWorkflow: true,
                BaselineMs: baseline.ElapsedMs,
                BaselineTokens: EstimateTokens(baseline.Output),
                OurTokens: EstimateTokens(ours.Output),
                HybridTokens: EstimateTokens(hybrid.Output),
                BaselineCalls: 1,
                OurCalls: 1,
                HybridCalls: hybrid.CallCount,
                Success: success,
                Notes: $"{task.Kind}|hybrid:{(hybrid.UsedFallback ? "fallback" : "compact")}",
                BaselineInput: baseline.Input,
                BaselineOutput: baseline.Output,
                OurInput: ours.Input,
                OurOutput: ours.Output,
                HybridInput: hybrid.Input,
                HybridOutput: hybrid.Output,
                TraceJson: traceJson));
        }

        return Task.FromResult<IReadOnlyList<BenchmarkResult>>(results);
    }

    private RunData RunBaseline(string kind, string relativePath)
    {
        var args = kind switch
        {
            "history_latest_touch" => $"log --pretty=format:%H -- \"{relativePath}\"",
            "history_first_touch" => $"log --reverse --pretty=format:%H -- \"{relativePath}\"",
            "history_touch_count" => $"log --pretty=format:%H -- \"{relativePath}\"",
            _ => $"log --pretty=format:%H -- \"{relativePath}\""
        };
        return RunGit(args);
    }

    private RunData RunOurs(string kind, string relativePath)
    {
        var args = kind switch
        {
            "history_latest_touch" => $"log -n 1 --pretty=format:%H -- \"{relativePath}\"",
            "history_first_touch" => $"log --diff-filter=A --pretty=format:%H -- \"{relativePath}\"",
            "history_touch_count" => $"rev-list --count HEAD -- \"{relativePath}\"",
            _ => $"log -n 1 --pretty=format:%H -- \"{relativePath}\""
        };
        return RunGit(args);
    }

    private RunData RunHybrid(string kind, string relativePath, RunData baseline, RunData compact)
    {
        // Hybrid mode: prefer compact query; fallback to broad baseline output only if compact result is invalid.
        var compactOk = Evaluate(kind, baseline.Output, compact.Output);
        if (compactOk)
        {
            return compact with
            {
                Mode = "hybrid",
                Input = $"hybrid(prefer compact): {compact.Input}",
                CallCount = 1,
                UsedFallback = false
            };
        }

        var fallbackOutput = kind switch
        {
            "history_touch_count" => baseline.LineCount.ToString(),
            _ => SplitLines(baseline.Output).FirstOrDefault() ?? ""
        };

        return new RunData(
            Output: fallbackOutput,
            ElapsedMs: compact.ElapsedMs + baseline.ElapsedMs,
            LineCount: SplitLines(fallbackOutput).Count,
            Input: $"hybrid(fallback): compact=({compact.Input}) + baseline=({baseline.Input})",
            Mode: "hybrid",
            CallCount: 2,
            UsedFallback: true);
    }

    private bool Evaluate(string kind, string baselineOutput, string oursOutput)
    {
        var baselineLines = SplitLines(baselineOutput);
        var oursLines = SplitLines(oursOutput);
        if (baselineLines.Count == 0 || oursLines.Count == 0)
            return false;

        return kind switch
        {
            "history_latest_touch" => baselineLines[0].Equals(oursLines[0], StringComparison.OrdinalIgnoreCase)
                                      && HashRegex.IsMatch(oursLines[0]),
            "history_first_touch" => baselineLines[0].Equals(oursLines[0], StringComparison.OrdinalIgnoreCase)
                                     && HashRegex.IsMatch(oursLines[0]),
            "history_touch_count" => int.TryParse(oursLines[0], out var oursCount)
                                     && baselineLines.Count == oursCount,
            _ => baselineLines[0].Equals(oursLines[0], StringComparison.OrdinalIgnoreCase)
        };
    }

    private RunData RunGit(string gitArgs)
    {
        var repo = Path.GetFullPath(_repoPath).Replace('\\', '/');
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"-c safe.directory=\"{repo}\" -C \"{_repoPath}\" {gitArgs}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var sw = Stopwatch.StartNew();
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git process.");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        sw.Stop();

        var output = p.ExitCode == 0 ? stdout.Trim() : $"{stdout}\n{stderr}".Trim();
        return new RunData(
            Output: output,
            ElapsedMs: sw.ElapsedMilliseconds,
            LineCount: SplitLines(output).Count,
            Input: $"git {gitArgs}",
            Mode: "unknown",
            CallCount: 1,
            UsedFallback: false);
    }

    private static List<string> SplitLines(string text)
        => text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToList();

    private static int EstimateTokens(string text)
        => Math.Max(1, text.Length / 4);

    private static string BuildTrace(HistoryTask task, RunData baseline, RunData ours, RunData hybrid)
    {
        var payload = new
        {
            task = new
            {
                id = task.Id,
                kind = task.Kind,
                path = task.Path
            },
            modes = new[]
            {
                new
                {
                    mode = "classic",
                    input = baseline.Input,
                    output = baseline.Output,
                    elapsedMs = baseline.ElapsedMs,
                    lineCount = baseline.LineCount,
                    tokens = EstimateTokens(baseline.Output),
                    callCount = baseline.CallCount,
                    usedFallback = baseline.UsedFallback
                },
                new
                {
                    mode = "compact",
                    input = ours.Input,
                    output = ours.Output,
                    elapsedMs = ours.ElapsedMs,
                    lineCount = ours.LineCount,
                    tokens = EstimateTokens(ours.Output),
                    callCount = ours.CallCount,
                    usedFallback = ours.UsedFallback
                },
                new
                {
                    mode = "hybrid",
                    input = hybrid.Input,
                    output = hybrid.Output,
                    elapsedMs = hybrid.ElapsedMs,
                    lineCount = hybrid.LineCount,
                    tokens = EstimateTokens(hybrid.Output),
                    callCount = hybrid.CallCount,
                    usedFallback = hybrid.UsedFallback
                }
            }
        };

        return JsonSerializer.Serialize(payload);
    }

    private sealed record RunData(
        string Output,
        long ElapsedMs,
        int LineCount,
        string Input,
        string Mode,
        int CallCount,
        bool UsedFallback);
}
