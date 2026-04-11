using Llens.Bench;
using Llens.Bench.RepoGen;
using Llens.Bench.Scenarios;
using Llens.Bench.TaskPacks;
using Microsoft.Data.Sqlite;
using Spectre.Console;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

if (HasArg(args, "--repo-gen"))
{
    var output = GetArg(args, "--output")
        ?? GetArg(args, "--out-dir")
        ?? "Llens.Bench/generated/synth-repo";
    var commits = ParseIntArg(args, "--commits", 100, 1, 5000);
    var files = ParseIntArg(args, "--files", 100, 1, 5000);
    var seed = ParseIntArg(args, "--seed", 42, 0, int.MaxValue);
    var force = HasArg(args, "--force");

    var options = new SyntheticRepoGenOptions(
        OutputPath: output,
        CommitCount: commits,
        FileTarget: files,
        Seed: seed,
        Force: force);

    return await SyntheticRepoGenerator.GenerateAsync(options);
}

var repoArg = GetArg(args, "--repo") ?? "Llens.Bench/generated/synth-repo-100";
var tasksArg = GetArg(args, "--tasks") ?? "Llens.Bench/TaskPacks/synthetic-history.tasks.json";
if (File.Exists(tasksArg))
{
    try
    {
        var pack = TaskPackLoader.Load(tasksArg);
        if (!string.IsNullOrWhiteSpace(pack.Repo) && GetArg(args, "--repo") is null)
            repoArg = pack.Repo!;
    }
    catch
    {
        // keep explicit/default args; scenario will report loader error during run
    }
}

var scenarioFactories = new List<Func<IBenchmarkScenario>>
{
    static () => new RustUsageBenchmark(),
    static () => new CSharpUsageBenchmark(),
    static () => new CSharpSymbolBenchmark(),
    static () => new CargoImportBenchmark(),
    static () => new WorkflowComparisonBenchmark(),
    () => new HistoryTaskPackBenchmark(repoArg, tasksArg),
};

if (HasArg(args, "--history-only"))
{
    scenarioFactories =
    [
        () => new HistoryTaskPackBenchmark(repoArg, tasksArg)
    ];
}

var filter = args.SkipWhile(a => a != "--filter").Skip(1).FirstOrDefault();
if (!string.IsNullOrWhiteSpace(filter))
{
    scenarioFactories = scenarioFactories
        .Where(f => f().Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
        .ToList();
}

var repeatsArg = args.SkipWhile(a => a != "--repeats").Skip(1).FirstOrDefault();
var repeats = 1;
if (!string.IsNullOrWhiteSpace(repeatsArg) && int.TryParse(repeatsArg, out var parsed))
    repeats = Math.Clamp(parsed, 1, 20);

var tempArg = args.SkipWhile(a => a is not ("--temperature" or "--temp")).Skip(1).FirstOrDefault();
var temperatures = ParseTemperatures(tempArg);

var outDirArg = args.SkipWhile(a => a != "--out").Skip(1).FirstOrDefault();
var outDir = string.IsNullOrWhiteSpace(outDirArg) ? null : outDirArg.Trim();
var sqliteArg = GetArg(args, "--sqlite");

AnsiConsole.Write(new Rule("[bold cyan]Llens Benchmarks[/]").RuleStyle("grey"));
AnsiConsole.WriteLine();

var allResults = new List<BenchmarkResult>();

await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .SpinnerStyle(Style.Parse("cyan"))
    .StartAsync("Running scenarios...", async ctx =>
    {
        foreach (var temperature in temperatures)
        {
            foreach (var factory in scenarioFactories)
            {
                var warmSharedScenario = temperature == BenchmarkTemperature.Warm ? factory() : null;
                var scenarioName = (warmSharedScenario ?? factory()).Name;

                for (var i = 1; i <= repeats; i++)
                {
                    ctx.Status($"[grey]{temperature}[/] [cyan]{scenarioName}[/] [grey]({i}/{repeats})[/]...");
                    var scenario = warmSharedScenario ?? factory();
                    var options = new BenchmarkRunOptions(temperature, i, repeats);
                    var results = await scenario.RunAsync(options);
                    allResults.AddRange(results.Select(r => r with { Temperature = options.TemperatureLabel }));
                }
            }
        }
    });

if (repeats > 1)
{
    allResults = allResults
        .GroupBy(r => new { r.Scenario, r.Fixture, r.IsWorkflow, r.Temperature })
        .Select(g =>
        {
            var first = g.First();
            return new BenchmarkResult(
                Scenario: first.Scenario,
                Fixture: first.Fixture,
                BaselineCount: (int)Math.Round(g.Average(x => x.BaselineCount)),
                OurCount: (int)Math.Round(g.Average(x => x.OurCount)),
                CoveragePercent: g.Average(x => x.CoveragePercent),
                Extra: (int)Math.Round(g.Average(x => x.Extra)),
                OurMs: (long)Math.Round(g.Average(x => x.OurMs)),
                IsWorkflow: first.IsWorkflow,
                BaselineMs: first.BaselineMs.HasValue ? (long)Math.Round(g.Average(x => x.BaselineMs ?? 0)) : null,
                BaselineTokens: first.BaselineTokens.HasValue ? (int)Math.Round(g.Average(x => x.BaselineTokens ?? 0)) : null,
                OurTokens: first.OurTokens.HasValue ? (int)Math.Round(g.Average(x => x.OurTokens ?? 0)) : null,
                HybridTokens: first.HybridTokens.HasValue ? (int)Math.Round(g.Average(x => x.HybridTokens ?? 0)) : null,
                BaselineCalls: first.BaselineCalls.HasValue ? (int)Math.Round(g.Average(x => x.BaselineCalls ?? 0)) : null,
                OurCalls: first.OurCalls.HasValue ? (int)Math.Round(g.Average(x => x.OurCalls ?? 0)) : null,
                HybridCalls: first.HybridCalls.HasValue ? (int)Math.Round(g.Average(x => x.HybridCalls ?? 0)) : null,
                Success: first.Success.HasValue ? g.All(x => x.Success == true) : null,
                Notes: $"avg of {repeats}",
                Temperature: first.Temperature,
                BaselineInput: first.BaselineInput,
                BaselineOutput: first.BaselineOutput,
                OurInput: first.OurInput,
                OurOutput: first.OurOutput,
                HybridInput: first.HybridInput,
                HybridOutput: first.HybridOutput,
                TraceJson: first.TraceJson);
        })
        .OrderBy(x => x.IsWorkflow)
        .ThenBy(x => x.Temperature, StringComparer.OrdinalIgnoreCase)
        .ThenBy(x => x.Scenario, StringComparer.OrdinalIgnoreCase)
        .ThenBy(x => x.Fixture, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

var showTemp = temperatures.Count > 1;
var table = new Table()
    .Border(TableBorder.Rounded)
    .BorderStyle(Style.Parse("grey"))
    .AddColumn("[bold]Scenario[/]");
if (showTemp) table.AddColumn(new TableColumn("[bold]Temp[/]").Centered());
table
    .AddColumn(new TableColumn("[bold]Fixture[/]"))
    .AddColumn(new TableColumn("[bold]Baseline[/]").RightAligned())
    .AddColumn(new TableColumn("[bold]Ours[/]").RightAligned())
    .AddColumn(new TableColumn("[bold]Coverage[/]").RightAligned())
    .AddColumn(new TableColumn("[bold]+Extra[/]").RightAligned())
    .AddColumn(new TableColumn("[bold]ms[/]").RightAligned())
    .AddColumn(new TableColumn("[bold]  [/]").Centered());

foreach (var r in allResults)
{
    var coverageColor = r.CoveragePercent >= 100.0 ? "green"
        : r.CoveragePercent >= 80.0 ? "yellow"
        : "red";
    var extraLabel = r.Extra > 0 ? $"[blue]+{r.Extra}[/]"
        : r.Extra < 0 ? $"[red]{r.Extra}[/]"
        : "[grey]  0[/]";
    var statusMark = r.Passed ? "[green]✓[/]" : "[red]✗[/]";

    var row = new List<string>
    {
        r.Scenario
    };
    if (showTemp) row.Add(r.Temperature);
    row.AddRange(
    [
        r.Fixture,
        r.BaselineCount.ToString(),
        r.OurCount.ToString(),
        $"[{coverageColor}]{r.CoveragePercent:F1}%[/]",
        extraLabel,
        r.OurMs.ToString(),
        statusMark
    ]);

    table.AddRow(row.ToArray());
}

AnsiConsole.Write(table);
AnsiConsole.WriteLine();

if (repeats > 1)
{
    AnsiConsole.MarkupLine($"[grey]Averaged across {repeats} runs per scenario.[/]");
    AnsiConsole.WriteLine();
}

var workflow = allResults.Where(r => r.IsWorkflow).ToList();
var historyWorkflow = workflow
    .Where(r => GetHistoryKind(r) is not null)
    .ToList();
var coreWorkflow = workflow
    .Where(r => GetHistoryKind(r) is null)
    .ToList();

if (coreWorkflow.Count > 0)
{
    var workflowTable = new Table()
        .Border(TableBorder.Rounded)
        .BorderStyle(Style.Parse("grey"))
        .AddColumn("[bold]Task[/]");
    if (showTemp) workflowTable.AddColumn(new TableColumn("[bold]Temp[/]").Centered());
    workflowTable
        .AddColumn(new TableColumn("[bold]Baseline tok[/]").RightAligned())
        .AddColumn(new TableColumn("[bold]Llens tok[/]").RightAligned())
        .AddColumn(new TableColumn("[bold]Baseline ms[/]").RightAligned())
        .AddColumn(new TableColumn("[bold]Llens ms[/]").RightAligned())
        .AddColumn(new TableColumn("[bold]Baseline calls[/]").RightAligned())
        .AddColumn(new TableColumn("[bold]Llens calls[/]").RightAligned())
        .AddColumn(new TableColumn("[bold]Result[/]").Centered());

    foreach (var r in coreWorkflow)
    {
        var tokDelta = (r.BaselineTokens ?? 0) - (r.OurTokens ?? 0);
        var result = r.Passed
            ? (tokDelta >= 0 ? "[green]win[/]" : "[yellow]quality-only[/]")
            : "[red]miss[/]";

        var row = new List<string> { r.Scenario };
        if (showTemp) row.Add(r.Temperature);
        row.AddRange(
        [
            (r.BaselineTokens ?? 0).ToString(),
            (r.OurTokens ?? 0).ToString(),
            (r.BaselineMs ?? 0).ToString(),
            r.OurMs.ToString(),
            (r.BaselineCalls ?? 0).ToString(),
            (r.OurCalls ?? 0).ToString(),
            result
        ]);
        workflowTable.AddRow(row.ToArray());
    }

    AnsiConsole.Write(new Rule("[bold cyan]Workflow Comparison[/]").RuleStyle("grey"));
    AnsiConsole.Write(workflowTable);
    AnsiConsole.WriteLine();
}

if (historyWorkflow.Count > 0)
{
    var historyTable = new Table()
        .Border(TableBorder.Rounded)
        .BorderStyle(Style.Parse("grey"))
        .AddColumn("[bold]Task[/]");
    if (showTemp) historyTable.AddColumn(new TableColumn("[bold]Temp[/]").Centered());
    historyTable
        .AddColumn("[bold]Kind[/]")
        .AddColumn(new TableColumn("[bold]Baseline tok[/]").RightAligned())
        .AddColumn(new TableColumn("[bold]Compact tok[/]").RightAligned())
        .AddColumn(new TableColumn("[bold]Hybrid tok[/]").RightAligned())
        .AddColumn(new TableColumn("[bold]Δ tok[/]").RightAligned())
        .AddColumn(new TableColumn("[bold]Baseline ms[/]").RightAligned())
        .AddColumn(new TableColumn("[bold]Compact ms[/]").RightAligned())
        .AddColumn(new TableColumn("[bold]Result[/]").Centered());

    foreach (var r in historyWorkflow)
    {
        var tokDelta = (r.BaselineTokens ?? 0) - (r.OurTokens ?? 0);
        var deltaLabel = tokDelta >= 0 ? $"[green]+{tokDelta}[/]" : $"[red]{tokDelta}[/]";
        var result = r.Passed ? "[green]win[/]" : "[red]miss[/]";
        var kind = FormatHistoryKind(GetHistoryKind(r)) ?? "history";

        var row = new List<string> { r.Scenario };
        if (showTemp) row.Add(r.Temperature);
        row.AddRange(
        [
            kind,
            (r.BaselineTokens ?? 0).ToString(),
            (r.OurTokens ?? 0).ToString(),
            (r.HybridTokens ?? 0).ToString(),
            deltaLabel,
            (r.BaselineMs ?? 0).ToString(),
            r.OurMs.ToString(),
            result
        ]);
        historyTable.AddRow(row.ToArray());
    }

    AnsiConsole.Write(new Rule("[bold cyan]History TaskPack (X vs Y)[/]").RuleStyle("grey"));
    AnsiConsole.Write(historyTable);
    AnsiConsole.WriteLine();
}

var passed = allResults.Count(r => r.Passed);
var total = allResults.Count;
var avgCov = allResults.Count > 0 ? allResults.Average(r => r.CoveragePercent) : 0;
var avgExtra = allResults.Count > 0 ? allResults.Average(r => r.Extra) : 0;
var allPass = passed == total;

var summaryColor = allPass ? "green" : "red";
AnsiConsole.MarkupLine(
    $" [{summaryColor}]{passed}/{total} passed[/]   " +
    $"Coverage: [cyan]{avgCov:F1}%[/] avg   " +
    $"AST advantage: [blue]+{avgExtra:F1}[/] tokens avg");
AnsiConsole.WriteLine();

if (coreWorkflow.Count > 0)
{
    foreach (var group in coreWorkflow.GroupBy(r => r.Temperature).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
    {
        var rows = group.ToList();
        var workflowWins = rows.Count(r => r.Passed && (r.OurTokens ?? int.MaxValue) <= (r.BaselineTokens ?? 0));
        var workflowOk = rows.Count(r => r.Passed);
        var workflowAvgTokSave = rows.Average(r => (r.BaselineTokens ?? 0) - (r.OurTokens ?? 0));
        AnsiConsole.MarkupLine(
            $" Workflow ({group.Key}): [green]{workflowWins}/{rows.Count}[/] token wins   " +
            $"[cyan]{workflowOk}/{rows.Count}[/] quality pass   " +
            $"avg token delta: [blue]{workflowAvgTokSave:+0.0;-0.0;0}[/]");
    }
    AnsiConsole.WriteLine();
}

if (historyWorkflow.Count > 0)
{
    foreach (var group in historyWorkflow.GroupBy(r => r.Temperature).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
    {
        var rows = group.ToList();
        var wins = rows.Count(r => r.Passed && (r.OurTokens ?? int.MaxValue) <= (r.BaselineTokens ?? 0));
        var quality = rows.Count(r => r.Passed);
        var avgTokSave = rows.Average(r => (r.BaselineTokens ?? 0) - (r.OurTokens ?? 0));
        AnsiConsole.MarkupLine(
            $" History ({group.Key}): [green]{wins}/{rows.Count}[/] token wins   " +
            $"[cyan]{quality}/{rows.Count}[/] quality pass   " +
            $"avg token delta: [blue]{avgTokSave:+0.0;-0.0;0}[/]");
    }
    AnsiConsole.WriteLine();
}

if (!string.IsNullOrWhiteSpace(outDir))
{
    var now = DateTimeOffset.UtcNow;
    var stamp = now.ToString("yyyyMMdd-HHmmss");
    var commit = GitCommitOrUnknown();
    Directory.CreateDirectory(outDir);

    var payload = new
    {
        generatedAtUtc = now,
        commit,
        repeats,
        temperatures = temperatures.Select(t => t.ToString().ToLowerInvariant()).ToArray(),
        historyBenchmark = BuildHistoryMetadata(repoArg, tasksArg),
        results = allResults
    };

    var jsonPath = Path.Combine(outDir, $"bench-{stamp}-{commit}.json");
    var csvPath = Path.Combine(outDir, $"bench-{stamp}-{commit}.csv");

    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(jsonPath, json);
    File.WriteAllText(csvPath, ToCsv(allResults));

    AnsiConsole.MarkupLine($"[grey]Exported:[/] [cyan]{jsonPath}[/]");
    AnsiConsole.MarkupLine($"[grey]Exported:[/] [cyan]{csvPath}[/]");
    AnsiConsole.WriteLine();
}

if (!string.IsNullOrWhiteSpace(sqliteArg))
{
    var sqlitePath = Path.GetFullPath(sqliteArg);
    SaveResultsToSqlite(sqlitePath, allResults, repeats, temperatures, repoArg, tasksArg);
    AnsiConsole.MarkupLine($"[grey]Exported:[/] [cyan]{sqlitePath}[/] [grey](sqlite)[/]");
    AnsiConsole.WriteLine();
}

return allPass ? 0 : 1;

static List<BenchmarkTemperature> ParseTemperatures(string? arg)
{
    var value = (arg ?? "warm").Trim().ToLowerInvariant();
    return value switch
    {
        "warm" => [BenchmarkTemperature.Warm],
        "cold" => [BenchmarkTemperature.Cold],
        "both" => [BenchmarkTemperature.Warm, BenchmarkTemperature.Cold],
        _ => [BenchmarkTemperature.Warm]
    };
}

static string ToCsv(IReadOnlyList<BenchmarkResult> rows)
{
    static string Esc(string? s)
    {
        if (s is null) return "";
        var needsQuote = s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r');
        if (!needsQuote) return s;
        return $"\"{s.Replace("\"", "\"\"")}\"";
    }

    var sb = new StringBuilder();
    sb.AppendLine("scenario,fixture,temperature,is_workflow,passed,baseline_count,our_count,coverage_percent,extra,baseline_ms,our_ms,baseline_tokens,our_tokens,hybrid_tokens,baseline_calls,our_calls,hybrid_calls,notes");
    foreach (var r in rows)
    {
        sb.AppendLine(string.Join(",",
            Esc(r.Scenario),
            Esc(r.Fixture),
            Esc(r.Temperature),
            r.IsWorkflow ? "true" : "false",
            r.Passed ? "true" : "false",
            r.BaselineCount,
            r.OurCount,
            r.CoveragePercent.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
            r.Extra,
            r.BaselineMs?.ToString() ?? "",
            r.OurMs,
            r.BaselineTokens?.ToString() ?? "",
            r.OurTokens?.ToString() ?? "",
            r.HybridTokens?.ToString() ?? "",
            r.BaselineCalls?.ToString() ?? "",
            r.OurCalls?.ToString() ?? "",
            r.HybridCalls?.ToString() ?? "",
            Esc(r.Notes)));
    }
    return sb.ToString();
}

static string GitCommitOrUnknown()
{
    try
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = "rev-parse --short HEAD",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var p = Process.Start(psi);
        if (p is null) return "unknown";
        var stdout = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit(3000);
        return p.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout) ? stdout : "unknown";
    }
    catch
    {
        return "unknown";
    }
}

static object? BuildHistoryMetadata(string repoArg, string tasksArg)
{
    if (!File.Exists(tasksArg) && !Directory.Exists(repoArg))
        return null;

    TaskPackExportMetadata? taskPack = null;
    try
    {
        if (File.Exists(tasksArg))
        {
            var pack = TaskPackLoader.Load(tasksArg);
            taskPack = new TaskPackExportMetadata(
                Path: Path.GetFullPath(tasksArg),
                Name: pack.Name,
                TaskCount: pack.Tasks.Count,
                Repo: pack.Repo is null ? null : Path.GetFullPath(pack.Repo));
        }
    }
    catch
    {
        // Export remains useful even if task-pack parsing fails.
    }

    FixtureExportMetadata? fixture = null;
    try
    {
        if (Directory.Exists(repoArg))
        {
            var manifestPath = Path.Combine(repoArg, "llens-bench-fixture.json");
            if (File.Exists(manifestPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                var root = doc.RootElement;
                fixture = new FixtureExportMetadata(
                    Path: Path.GetFullPath(manifestPath),
                    CommitCount: GetInt(root, "commitCount"),
                    FileCount: GetInt(root, "currentFileCount"),
                    Seed: GetInt(root, "seed"),
                    Head: GetString(root, "head"),
                    GeneratedAtUtc: GetString(root, "generatedAtUtc"));
            }
        }
    }
    catch
    {
        // Keep export resilient when fixture metadata is unavailable.
    }

    return new
    {
        repo = Directory.Exists(repoArg) ? Path.GetFullPath(repoArg) : repoArg,
        tasks = File.Exists(tasksArg) ? Path.GetFullPath(tasksArg) : tasksArg,
        taskPack,
        fixture
    };
}

static int? GetInt(JsonElement root, string name)
{
    if (!root.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Number)
        return null;
    return prop.TryGetInt32(out var v) ? v : null;
}

static string? GetString(JsonElement root, string name)
{
    if (!root.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
        return null;
    return prop.GetString();
}

static bool HasArg(string[] args, string key)
    => args.Any(a => string.Equals(a, key, StringComparison.OrdinalIgnoreCase));

static string? GetArg(string[] args, string key)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    }
    return null;
}

static int ParseIntArg(string[] args, string key, int fallback, int min, int max)
{
    var raw = GetArg(args, key);
    if (string.IsNullOrWhiteSpace(raw) || !int.TryParse(raw, out var parsed))
        return fallback;
    return Math.Clamp(parsed, min, max);
}

static string? GetHistoryKind(BenchmarkResult row)
{
    if (!row.IsWorkflow || string.IsNullOrWhiteSpace(row.Notes))
        return null;
    return row.Notes.StartsWith("history_", StringComparison.OrdinalIgnoreCase)
        ? row.Notes
        : null;
}

static string? FormatHistoryKind(string? historyKind)
{
    if (string.IsNullOrWhiteSpace(historyKind))
        return null;
    return historyKind switch
    {
        "history_latest_touch" => "latest",
        "history_first_touch" => "first",
        "history_touch_count" => "count",
        _ => historyKind.StartsWith("history_", StringComparison.OrdinalIgnoreCase)
            ? historyKind["history_".Length..]
            : historyKind
    };
}

static void SaveResultsToSqlite(
    string sqlitePath,
    IReadOnlyList<BenchmarkResult> rows,
    int repeats,
    IReadOnlyList<BenchmarkTemperature> temperatures,
    string repoArg,
    string tasksArg)
{
    var dir = Path.GetDirectoryName(sqlitePath);
    if (!string.IsNullOrWhiteSpace(dir))
        Directory.CreateDirectory(dir);

    var runId = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
    var createdAt = DateTimeOffset.UtcNow.ToString("O");
    var commit = GitCommitOrUnknown();
    var tempCsv = string.Join(",", temperatures.Select(t => t.ToString().ToLowerInvariant()));

    using var conn = new SqliteConnection($"Data Source={sqlitePath}");
    conn.Open();

    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS benchmark_runs (
              run_id TEXT PRIMARY KEY,
              created_at_utc TEXT NOT NULL,
              commit_hash TEXT NOT NULL,
              repeats INTEGER NOT NULL,
              temperatures TEXT NOT NULL,
              repo TEXT,
              tasks TEXT
            );
            CREATE TABLE IF NOT EXISTS benchmark_results (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              run_id TEXT NOT NULL,
              scenario TEXT NOT NULL,
              fixture TEXT NOT NULL,
              temperature TEXT NOT NULL,
              is_workflow INTEGER NOT NULL,
              passed INTEGER NOT NULL,
              baseline_count INTEGER NOT NULL,
              our_count INTEGER NOT NULL,
              coverage_percent REAL NOT NULL,
              extra INTEGER NOT NULL,
              baseline_ms INTEGER,
              our_ms INTEGER NOT NULL,
              baseline_tokens INTEGER,
              our_tokens INTEGER,
              hybrid_tokens INTEGER,
              baseline_calls INTEGER,
              our_calls INTEGER,
              hybrid_calls INTEGER,
              notes TEXT,
              baseline_input TEXT,
              baseline_output TEXT,
              compact_input TEXT,
              compact_output TEXT,
              hybrid_input TEXT,
              hybrid_output TEXT,
              trace_json TEXT,
              FOREIGN KEY(run_id) REFERENCES benchmark_runs(run_id)
            );
            CREATE TABLE IF NOT EXISTS benchmark_mode_results (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              run_id TEXT NOT NULL,
              scenario TEXT NOT NULL,
              fixture TEXT NOT NULL,
              mode TEXT NOT NULL,
              input TEXT,
              output TEXT,
              tokens INTEGER,
              calls INTEGER,
              elapsed_ms INTEGER,
              success INTEGER NOT NULL,
              notes TEXT,
              FOREIGN KEY(run_id) REFERENCES benchmark_runs(run_id)
            );
            CREATE TABLE IF NOT EXISTS benchmark_steps (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              run_id TEXT NOT NULL,
              scenario TEXT NOT NULL,
              fixture TEXT NOT NULL,
              step_no INTEGER NOT NULL,
              mode TEXT NOT NULL,
              operation TEXT NOT NULL,
              input TEXT,
              output TEXT,
              tokens INTEGER,
              elapsed_ms INTEGER,
              status TEXT NOT NULL,
              notes TEXT,
              FOREIGN KEY(run_id) REFERENCES benchmark_runs(run_id)
            );
            CREATE INDEX IF NOT EXISTS idx_results_run ON benchmark_results(run_id);
            CREATE INDEX IF NOT EXISTS idx_results_scenario_temp ON benchmark_results(scenario, temperature);
            CREATE INDEX IF NOT EXISTS idx_mode_run ON benchmark_mode_results(run_id);
            CREATE INDEX IF NOT EXISTS idx_steps_run ON benchmark_steps(run_id);
            CREATE INDEX IF NOT EXISTS idx_runs_created ON benchmark_runs(created_at_utc);
            """;
        cmd.ExecuteNonQuery();
    }

    EnsureColumn(conn, "benchmark_results", "hybrid_tokens", "INTEGER");
    EnsureColumn(conn, "benchmark_results", "hybrid_calls", "INTEGER");
    EnsureColumn(conn, "benchmark_results", "baseline_input", "TEXT");
    EnsureColumn(conn, "benchmark_results", "baseline_output", "TEXT");
    EnsureColumn(conn, "benchmark_results", "compact_input", "TEXT");
    EnsureColumn(conn, "benchmark_results", "compact_output", "TEXT");
    EnsureColumn(conn, "benchmark_results", "hybrid_input", "TEXT");
    EnsureColumn(conn, "benchmark_results", "hybrid_output", "TEXT");
    EnsureColumn(conn, "benchmark_results", "trace_json", "TEXT");

    using var tx = conn.BeginTransaction();

    using (var cmd = conn.CreateCommand())
    {
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO benchmark_runs (run_id, created_at_utc, commit_hash, repeats, temperatures, repo, tasks)
            VALUES ($run_id, $created_at_utc, $commit_hash, $repeats, $temperatures, $repo, $tasks);
            """;
        cmd.Parameters.AddWithValue("$run_id", runId);
        cmd.Parameters.AddWithValue("$created_at_utc", createdAt);
        cmd.Parameters.AddWithValue("$commit_hash", commit);
        cmd.Parameters.AddWithValue("$repeats", repeats);
        cmd.Parameters.AddWithValue("$temperatures", tempCsv);
        cmd.Parameters.AddWithValue("$repo", repoArg);
        cmd.Parameters.AddWithValue("$tasks", tasksArg);
        cmd.ExecuteNonQuery();
    }

    using (var cmd = conn.CreateCommand())
    {
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO benchmark_results (
                run_id, scenario, fixture, temperature, is_workflow, passed,
                baseline_count, our_count, coverage_percent, extra,
                baseline_ms, our_ms, baseline_tokens, our_tokens, hybrid_tokens,
                baseline_calls, our_calls, hybrid_calls, notes,
                baseline_input, baseline_output, compact_input, compact_output,
                hybrid_input, hybrid_output, trace_json)
            VALUES (
                $run_id, $scenario, $fixture, $temperature, $is_workflow, $passed,
                $baseline_count, $our_count, $coverage_percent, $extra,
                $baseline_ms, $our_ms, $baseline_tokens, $our_tokens, $hybrid_tokens,
                $baseline_calls, $our_calls, $hybrid_calls, $notes,
                $baseline_input, $baseline_output, $compact_input, $compact_output,
                $hybrid_input, $hybrid_output, $trace_json
            );
            """;

        var pRunId = cmd.Parameters.Add("$run_id", SqliteType.Text);
        var pScenario = cmd.Parameters.Add("$scenario", SqliteType.Text);
        var pFixture = cmd.Parameters.Add("$fixture", SqliteType.Text);
        var pTemperature = cmd.Parameters.Add("$temperature", SqliteType.Text);
        var pIsWorkflow = cmd.Parameters.Add("$is_workflow", SqliteType.Integer);
        var pPassed = cmd.Parameters.Add("$passed", SqliteType.Integer);
        var pBaselineCount = cmd.Parameters.Add("$baseline_count", SqliteType.Integer);
        var pOurCount = cmd.Parameters.Add("$our_count", SqliteType.Integer);
        var pCoverage = cmd.Parameters.Add("$coverage_percent", SqliteType.Real);
        var pExtra = cmd.Parameters.Add("$extra", SqliteType.Integer);
        var pBaselineMs = cmd.Parameters.Add("$baseline_ms", SqliteType.Integer);
        var pOurMs = cmd.Parameters.Add("$our_ms", SqliteType.Integer);
        var pBaselineTokens = cmd.Parameters.Add("$baseline_tokens", SqliteType.Integer);
        var pOurTokens = cmd.Parameters.Add("$our_tokens", SqliteType.Integer);
        var pHybridTokens = cmd.Parameters.Add("$hybrid_tokens", SqliteType.Integer);
        var pBaselineCalls = cmd.Parameters.Add("$baseline_calls", SqliteType.Integer);
        var pOurCalls = cmd.Parameters.Add("$our_calls", SqliteType.Integer);
        var pHybridCalls = cmd.Parameters.Add("$hybrid_calls", SqliteType.Integer);
        var pNotes = cmd.Parameters.Add("$notes", SqliteType.Text);
        var pBaselineInput = cmd.Parameters.Add("$baseline_input", SqliteType.Text);
        var pBaselineOutput = cmd.Parameters.Add("$baseline_output", SqliteType.Text);
        var pCompactInput = cmd.Parameters.Add("$compact_input", SqliteType.Text);
        var pCompactOutput = cmd.Parameters.Add("$compact_output", SqliteType.Text);
        var pHybridInput = cmd.Parameters.Add("$hybrid_input", SqliteType.Text);
        var pHybridOutput = cmd.Parameters.Add("$hybrid_output", SqliteType.Text);
        var pTraceJson = cmd.Parameters.Add("$trace_json", SqliteType.Text);

        foreach (var row in rows)
        {
            pRunId.Value = runId;
            pScenario.Value = row.Scenario;
            pFixture.Value = row.Fixture;
            pTemperature.Value = row.Temperature;
            pIsWorkflow.Value = row.IsWorkflow ? 1 : 0;
            pPassed.Value = row.Passed ? 1 : 0;
            pBaselineCount.Value = row.BaselineCount;
            pOurCount.Value = row.OurCount;
            pCoverage.Value = row.CoveragePercent;
            pExtra.Value = row.Extra;
            pBaselineMs.Value = (object?)row.BaselineMs ?? DBNull.Value;
            pOurMs.Value = row.OurMs;
            pBaselineTokens.Value = (object?)row.BaselineTokens ?? DBNull.Value;
            pOurTokens.Value = (object?)row.OurTokens ?? DBNull.Value;
            pHybridTokens.Value = (object?)row.HybridTokens ?? DBNull.Value;
            pBaselineCalls.Value = (object?)row.BaselineCalls ?? DBNull.Value;
            pOurCalls.Value = (object?)row.OurCalls ?? DBNull.Value;
            pHybridCalls.Value = (object?)row.HybridCalls ?? DBNull.Value;
            pNotes.Value = (object?)row.Notes ?? DBNull.Value;
            pBaselineInput.Value = (object?)row.BaselineInput ?? DBNull.Value;
            pBaselineOutput.Value = (object?)row.BaselineOutput ?? DBNull.Value;
            pCompactInput.Value = (object?)row.OurInput ?? DBNull.Value;
            pCompactOutput.Value = (object?)row.OurOutput ?? DBNull.Value;
            pHybridInput.Value = (object?)row.HybridInput ?? DBNull.Value;
            pHybridOutput.Value = (object?)row.HybridOutput ?? DBNull.Value;
            pTraceJson.Value = (object?)row.TraceJson ?? DBNull.Value;

            cmd.ExecuteNonQuery();
        }
    }

    InsertModeAndStepRows(conn, tx, runId, rows);

    tx.Commit();
}

static void InsertModeAndStepRows(
    SqliteConnection conn,
    SqliteTransaction tx,
    string runId,
    IReadOnlyList<BenchmarkResult> rows)
{
    using var modeCmd = conn.CreateCommand();
    modeCmd.Transaction = tx;
    modeCmd.CommandText =
        """
        INSERT INTO benchmark_mode_results (
            run_id, scenario, fixture, mode, input, output, tokens, calls, elapsed_ms, success, notes)
        VALUES (
            $run_id, $scenario, $fixture, $mode, $input, $output, $tokens, $calls, $elapsed_ms, $success, $notes
        );
        """;

    var mRunId = modeCmd.Parameters.Add("$run_id", SqliteType.Text);
    var mScenario = modeCmd.Parameters.Add("$scenario", SqliteType.Text);
    var mFixture = modeCmd.Parameters.Add("$fixture", SqliteType.Text);
    var mMode = modeCmd.Parameters.Add("$mode", SqliteType.Text);
    var mInput = modeCmd.Parameters.Add("$input", SqliteType.Text);
    var mOutput = modeCmd.Parameters.Add("$output", SqliteType.Text);
    var mTokens = modeCmd.Parameters.Add("$tokens", SqliteType.Integer);
    var mCalls = modeCmd.Parameters.Add("$calls", SqliteType.Integer);
    var mElapsedMs = modeCmd.Parameters.Add("$elapsed_ms", SqliteType.Integer);
    var mSuccess = modeCmd.Parameters.Add("$success", SqliteType.Integer);
    var mNotes = modeCmd.Parameters.Add("$notes", SqliteType.Text);

    using var stepCmd = conn.CreateCommand();
    stepCmd.Transaction = tx;
    stepCmd.CommandText =
        """
        INSERT INTO benchmark_steps (
            run_id, scenario, fixture, step_no, mode, operation, input, output, tokens, elapsed_ms, status, notes)
        VALUES (
            $run_id, $scenario, $fixture, $step_no, $mode, $operation, $input, $output, $tokens, $elapsed_ms, $status, $notes
        );
        """;

    var sRunId = stepCmd.Parameters.Add("$run_id", SqliteType.Text);
    var sScenario = stepCmd.Parameters.Add("$scenario", SqliteType.Text);
    var sFixture = stepCmd.Parameters.Add("$fixture", SqliteType.Text);
    var sStepNo = stepCmd.Parameters.Add("$step_no", SqliteType.Integer);
    var sMode = stepCmd.Parameters.Add("$mode", SqliteType.Text);
    var sOperation = stepCmd.Parameters.Add("$operation", SqliteType.Text);
    var sInput = stepCmd.Parameters.Add("$input", SqliteType.Text);
    var sOutput = stepCmd.Parameters.Add("$output", SqliteType.Text);
    var sTokens = stepCmd.Parameters.Add("$tokens", SqliteType.Integer);
    var sElapsedMs = stepCmd.Parameters.Add("$elapsed_ms", SqliteType.Integer);
    var sStatus = stepCmd.Parameters.Add("$status", SqliteType.Text);
    var sNotes = stepCmd.Parameters.Add("$notes", SqliteType.Text);

    foreach (var row in rows)
    {
        InsertMode(modeCmd, mRunId, mScenario, mFixture, mMode, mInput, mOutput, mTokens, mCalls, mElapsedMs, mSuccess, mNotes,
            runId, row, "classic", row.BaselineInput, row.BaselineOutput, row.BaselineTokens, row.BaselineCalls, row.BaselineMs, row.Passed, row.Notes);
        InsertMode(modeCmd, mRunId, mScenario, mFixture, mMode, mInput, mOutput, mTokens, mCalls, mElapsedMs, mSuccess, mNotes,
            runId, row, "compact", row.OurInput, row.OurOutput, row.OurTokens, row.OurCalls, row.OurMs, row.Passed, row.Notes);
        InsertMode(modeCmd, mRunId, mScenario, mFixture, mMode, mInput, mOutput, mTokens, mCalls, mElapsedMs, mSuccess, mNotes,
            runId, row, "hybrid", row.HybridInput ?? row.OurInput, row.HybridOutput ?? row.OurOutput, row.HybridTokens ?? row.OurTokens,
            row.HybridCalls ?? row.OurCalls, row.OurMs, row.Passed, row.Notes);

        var steps = ParseSteps(row.TraceJson);
        var stepNo = 1;
        foreach (var step in steps)
        {
            sRunId.Value = runId;
            sScenario.Value = row.Scenario;
            sFixture.Value = row.Fixture;
            sStepNo.Value = stepNo++;
            sMode.Value = step.Mode;
            sOperation.Value = "git";
            sInput.Value = (object?)step.Input ?? DBNull.Value;
            sOutput.Value = (object?)step.Output ?? DBNull.Value;
            sTokens.Value = (object?)step.Tokens ?? DBNull.Value;
            sElapsedMs.Value = (object?)step.ElapsedMs ?? DBNull.Value;
            sStatus.Value = "ok";
            sNotes.Value = step.UsedFallback == true ? "fallback" : "";
            stepCmd.ExecuteNonQuery();
        }
    }
}

static void InsertMode(
    SqliteCommand modeCmd,
    SqliteParameter mRunId,
    SqliteParameter mScenario,
    SqliteParameter mFixture,
    SqliteParameter mMode,
    SqliteParameter mInput,
    SqliteParameter mOutput,
    SqliteParameter mTokens,
    SqliteParameter mCalls,
    SqliteParameter mElapsedMs,
    SqliteParameter mSuccess,
    SqliteParameter mNotes,
    string runId,
    BenchmarkResult row,
    string mode,
    string? input,
    string? output,
    int? tokens,
    int? calls,
    long? elapsedMs,
    bool success,
    string? notes)
{
    mRunId.Value = runId;
    mScenario.Value = row.Scenario;
    mFixture.Value = row.Fixture;
    mMode.Value = mode;
    mInput.Value = (object?)input ?? DBNull.Value;
    mOutput.Value = (object?)output ?? DBNull.Value;
    mTokens.Value = (object?)tokens ?? DBNull.Value;
    mCalls.Value = (object?)calls ?? DBNull.Value;
    mElapsedMs.Value = (object?)elapsedMs ?? DBNull.Value;
    mSuccess.Value = success ? 1 : 0;
    mNotes.Value = (object?)notes ?? DBNull.Value;
    modeCmd.ExecuteNonQuery();
}

static IReadOnlyList<TraceModeRow> ParseSteps(string? traceJson)
{
    if (string.IsNullOrWhiteSpace(traceJson))
        return [];

    try
    {
        using var doc = JsonDocument.Parse(traceJson);
        if (!doc.RootElement.TryGetProperty("modes", out var modes) || modes.ValueKind != JsonValueKind.Array)
            return [];

        var rows = new List<TraceModeRow>();
        foreach (var mode in modes.EnumerateArray())
        {
            rows.Add(new TraceModeRow(
                Mode: mode.TryGetProperty("mode", out var m) ? m.GetString() ?? "unknown" : "unknown",
                Input: mode.TryGetProperty("input", out var i) ? i.GetString() : null,
                Output: mode.TryGetProperty("output", out var o) ? o.GetString() : null,
                Tokens: mode.TryGetProperty("tokens", out var t) && t.TryGetInt32(out var tv) ? tv : null,
                ElapsedMs: mode.TryGetProperty("elapsedMs", out var e) && e.TryGetInt64(out var ev) ? ev : null,
                UsedFallback: mode.TryGetProperty("usedFallback", out var f) && f.ValueKind == JsonValueKind.True));
        }

        return rows;
    }
    catch
    {
        return [];
    }
}

static void EnsureColumn(SqliteConnection conn, string table, string column, string typeSql)
{
    using var check = conn.CreateCommand();
    check.CommandText = $"PRAGMA table_info({table});";
    using var rd = check.ExecuteReader();
    while (rd.Read())
    {
        if (rd.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase))
            return;
    }

    using var alter = conn.CreateCommand();
    alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {typeSql};";
    alter.ExecuteNonQuery();
}

internal sealed record TaskPackExportMetadata(
    string Path,
    string Name,
    int TaskCount,
    string? Repo);

internal sealed record FixtureExportMetadata(
    string Path,
    int? CommitCount,
    int? FileCount,
    int? Seed,
    string? Head,
    string? GeneratedAtUtc);

internal sealed record TraceModeRow(
    string Mode,
    string? Input,
    string? Output,
    int? Tokens,
    long? ElapsedMs,
    bool? UsedFallback);
