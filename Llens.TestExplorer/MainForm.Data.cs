using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Data.Sqlite;

namespace Llens.TestExplorer;

public sealed partial class MainForm
{
    private async Task RunAllAsync()
    {
        await RunBenchAsync(ResolvePath(_tasksText.Text));
    }

    private async Task RunSelectedAsync()
    {
        var selected = GetCheckedScenarios();
        if (selected.Count == 0)
        {
            AppendLog("Run Checked skipped: no scenarios checked.");
            return;
        }

        var subsetPath = BuildSubsetTaskPack(selected);
        await RunBenchAsync(subsetPath);
    }

    private async Task RunBenchAsync(string tasksPath)
    {
        var outDir = ResolvePath(_outDirText.Text);
        var sqlitePath = ResolvePath(_sqliteText.Text);
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(Path.GetDirectoryName(sqlitePath) ?? outDir);

        ResetLogState();
        _logText.Clear();
        AppendLog($"Running bench with task pack: {tasksPath}");

        SetRunningState(true);
        try
        {
            using var process = new Process
            {
                StartInfo = BuildBenchProcessInfo(tasksPath, outDir, sqlitePath),
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    BeginInvoke(new Action(() => AppendLog(e.Data!)));
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    BeginInvoke(new Action(() => AppendLog(e.Data!)));
                }
            };

            if (!process.Start())
            {
                AppendLog("Failed to start benchmark process.");
                return;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            FlushLastLogLine();

            AppendLog(process.ExitCode == 0
                ? "Benchmark completed successfully."
                : $"Benchmark exited with code {process.ExitCode}.");

            if (File.Exists(sqlitePath))
            {
                await LoadLatestSqlRunAsync();
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Benchmark run failed: {ex.Message}");
        }
        finally
        {
            SetRunningState(false);
        }
    }

    private ProcessStartInfo BuildBenchProcessInfo(string tasksPath, string outDir, string sqlitePath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = _repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add("Llens.Bench/Llens.Bench.csproj");
        psi.ArgumentList.Add("--");
        if (_historyOnlyCheck.Checked)
        {
            psi.ArgumentList.Add("--history-only");
        }

        psi.ArgumentList.Add("--tasks");
        psi.ArgumentList.Add(tasksPath);
        psi.ArgumentList.Add("--out");
        psi.ArgumentList.Add(outDir);
        psi.ArgumentList.Add("--sqlite");
        psi.ArgumentList.Add(sqlitePath);

        foreach (var arg in SplitArguments(_extraArgsText.Text))
        {
            psi.ArgumentList.Add(arg);
        }

        return psi;
    }

    private static IEnumerable<string> SplitArguments(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            yield break;
        }

        var buffer = new StringBuilder();
        var quoted = false;
        foreach (var ch in raw)
        {
            if (ch == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (buffer.Length > 0)
                {
                    yield return buffer.ToString();
                    buffer.Clear();
                }

                continue;
            }

            buffer.Append(ch);
        }

        if (buffer.Length > 0)
        {
            yield return buffer.ToString();
        }
    }

    private string BuildSubsetTaskPack(IReadOnlyCollection<ScenarioSummary> selected)
    {
        var path = ResolvePath(_tasksText.Text);
        var pack = JsonSerializer.Deserialize<TaskPackFile>(File.ReadAllText(path), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Failed to parse task pack.");

        var ids = selected.Select(x => x.Scenario).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var subset = new TaskPackFile
        {
            Name = $"{pack.Name}-subset-{DateTime.UtcNow:yyyyMMddHHmmss}",
            Repo = pack.Repo,
            Tasks = pack.Tasks
                .Where(x => ids.Contains(x.Id))
                .Select(x => new TaskPackItem
                {
                    Id = x.Id,
                    Title = x.Title,
                    Kind = x.Kind,
                    Path = x.Path,
                    Note = x.Note
                })
                .ToList()
        };

        var subsetPath = Path.Combine(_tmpDir, $"subset-{DateTime.UtcNow:yyyyMMddHHmmssfff}.tasks.json");
        File.WriteAllText(subsetPath, JsonSerializer.Serialize(subset, new JsonSerializerOptions { WriteIndented = true }));
        AppendLog($"Temporary subset task pack created: {subset.Tasks.Count} tasks.");
        return subsetPath;
    }

    private async Task LoadLatestSqlRunAsync()
    {
        var sqlitePath = ResolvePath(_sqliteText.Text);
        if (!File.Exists(sqlitePath))
        {
            AppendLog($"SQLite file not found: {sqlitePath}");
            return;
        }

        try
        {
            using var conn = OpenSqlite(sqlitePath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                SELECT run_id, created_at_utc, commit_hash, tasks
                FROM benchmark_runs
                ORDER BY created_at_utc DESC
                LIMIT 1;
                """;

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                AppendLog("SQLite database is present but contains no benchmark runs.");
                return;
            }

            _loadedRunId = reader.GetString(0);
            var createdAt = reader.GetString(1);
            var commit = reader.GetString(2);
            var tasks = reader.IsDBNull(3) ? "" : reader.GetString(3);
            _loadedTasksPath = tasks;
            _runMetaLabel.Text = $"Run: {createdAt} | Commit: {ShortCommit(commit)} | SQLite attached";
            _toolTip.SetToolTip(_runMetaLabel, $"Run: {createdAt} | Commit: {commit} | Tasks: {tasks}");

            LoadTaskCatalog(tasks);
            LoadFlakyScenarios(conn);
            LoadScenarioRows(conn, _loadedRunId);
            BuildCategoryTree();
            ApplyFilters();
            AppendLog($"Loaded latest run: {_loadedRunId} from {Path.GetFileName(sqlitePath)}");
            _overallSummaryLabel.Text = $"Tasks: {Path.GetFileName(tasks)} | Repo: {Path.GetFileName(_repoRoot)} | SQLite: {Path.GetFileName(sqlitePath)}";
            ApplySummaryTooltips();
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to load latest SQL run: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    private void LoadTaskCatalog(string? tasksPath)
    {
        _scenarioCatalog = new Dictionary<string, ScenarioCatalogItem>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(tasksPath))
        {
            return;
        }

        try
        {
            var resolved = ResolvePath(tasksPath);
            if (!File.Exists(resolved))
            {
                return;
            }

            var pack = JsonSerializer.Deserialize<TaskPackFile>(File.ReadAllText(resolved), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (pack is null)
            {
                return;
            }

            _scenarioCatalog = pack.Tasks
                .Where(task => !string.IsNullOrWhiteSpace(task.Id))
                .ToDictionary(
                    task => task.Id,
                    task => new ScenarioCatalogItem(
                        task.Id,
                        task.Title ?? DeriveScenarioTitle(task.Kind, task.Path, task.Id),
                        task.Kind,
                        task.Path,
                        task.Note ?? ""),
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            AppendLog($"Task catalog load failed: {ex.Message}");
        }
    }

    private void LoadFlakyScenarios(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT scenario, passed,
                   COALESCE(baseline_tokens, 0),
                   COALESCE(our_tokens, 0),
                   COALESCE(hybrid_tokens, 0)
            FROM benchmark_results
            ORDER BY scenario;
            """;

        using var reader = cmd.ExecuteReader();
        var buckets = new Dictionary<string, List<(bool Passed, int BestDelta)>>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            var scenario = reader.GetString(0);
            var passed = reader.GetInt64(1) == 1;
            var classic = reader.GetInt32(2);
            var compact = reader.GetInt32(3);
            var hybrid = reader.GetInt32(4);
            var delta = classic - Math.Min(compact, hybrid);

            if (!buckets.TryGetValue(scenario, out var list))
            {
                list = [];
                buckets[scenario] = list;
            }

            list.Add((passed, delta));
        }

        _flakyScenarioIds = buckets
            .Where(kvp => kvp.Value.Count >= 2 &&
                          (kvp.Value.Select(x => x.Passed).Distinct().Count() > 1 ||
                           kvp.Value.Select(x => Math.Sign(x.BestDelta)).Distinct().Count() > 1))
            .Select(kvp => kvp.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private void LoadScenarioRows(SqliteConnection conn, string runId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT scenario, fixture, notes, passed,
                   COALESCE(baseline_tokens, 0),
                   COALESCE(our_tokens, 0),
                   COALESCE(hybrid_tokens, 0)
            FROM benchmark_results
            WHERE run_id = $run_id
            ORDER BY scenario;
            """;
        cmd.Parameters.AddWithValue("$run_id", runId);

        using var reader = cmd.ExecuteReader();
        var rows = new List<ScenarioSummary>();
        while (reader.Read())
        {
            var scenario = reader.GetString(0);
            var fixture = reader.GetString(1);
            var notes = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var passed = reader.GetInt64(3) == 1;
            var classicTokens = reader.GetInt32(4);
            var compactTokens = reader.GetInt32(5);
            var hybridTokens = reader.GetInt32(6);
            var meta = _scenarioCatalog.TryGetValue(scenario, out var item) ? item : null;
            var mergedNotes = string.Join(" | ", new[] { notes, meta?.Notes }.Where(x => !string.IsNullOrWhiteSpace(x)));
            var kind = meta?.Kind ?? "";
            var tooling = DetectTooling($"{mergedNotes} {kind}", scenario);

            rows.Add(new ScenarioSummary
            {
                Scenario = scenario,
                DisplayName = meta?.Title ?? DeriveScenarioTitle(kind, fixture, scenario),
                Fixture = fixture,
                Tooling = tooling,
                Goal = DetectGoal(tooling, kind, scenario),
                Kind = kind,
                Language = DetectLanguage(fixture, scenario),
                Notes = mergedNotes,
                Passed = passed,
                ClassicTokens = classicTokens,
                CompactTokens = compactTokens,
                HybridTokens = hybridTokens
            });
        }

        _allRows = rows;
    }

    private void BuildCategoryTree()
    {
        var current = _activeCategoryFilter;
        _categoryTree.BeginUpdate();
        _categoryTree.Nodes.Clear();

        var root = new TreeNode($"All Tests ({_allRows.Count})") { Tag = "all" };
        _categoryTree.Nodes.Add(root);

        AddCategoryGroup("By Goal", ("goal:Token Savings", "Token Savings"), ("goal:Latency", "Latency"), ("goal:Correctness", "Correctness"), ("goal:Determinism", "Determinism"));
        AddCategoryGroup("By Tooling", ("tooling:History / Git", "History / Git"), ("tooling:Search / Grep", "Search / Grep"), ("tooling:Resolve / Symbol", "Resolve / Symbol"), ("tooling:Workflow / E2E", "Workflow / E2E"));
        AddCategoryGroup("By Language", ("language:CSharp", "CSharp"), ("language:Rust", "Rust"), ("language:Mixed", "Mixed"));
        AddCategoryGroup("Smart Views", ("smart:winners", "Savings Winners"), ("smart:regressions", "Regressions"), ("smart:flaky", "Flaky"), ("smart:review", "Needs Review"));

        _categoryTree.ExpandAll();
        _categoryTree.EndUpdate();

        var node = FindNodeByTag(_categoryTree.Nodes, current) ?? root;
        _categoryTree.SelectedNode = node;
    }

    private void AddCategoryGroup(string title, params (string Tag, string Label)[] items)
    {
        var group = new TreeNode(title) { Tag = $"group:{title}" };
        foreach (var item in items)
        {
            group.Nodes.Add(new TreeNode($"{item.Label} ({CountForTag(item.Tag)})") { Tag = item.Tag });
        }

        _categoryTree.Nodes.Add(group);
    }

    private int CountForTag(string tag) => _allRows.Count(row => MatchesCategory(row, tag));

    private static TreeNode? FindNodeByTag(TreeNodeCollection nodes, string tag)
    {
        foreach (TreeNode node in nodes)
        {
            if (string.Equals(node.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }

            var child = FindNodeByTag(node.Nodes, tag);
            if (child is not null)
            {
                return child;
            }
        }

        return null;
    }

    private void OnCategorySelected(TreeNode? node)
    {
        var tag = node?.Tag?.ToString();
        if (string.IsNullOrWhiteSpace(tag) || tag.StartsWith("group:", StringComparison.Ordinal))
        {
            return;
        }

        _activeCategoryFilter = tag;
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        var targetScenarioId = _activeScenarioId;
        var search = _searchText.Text.Trim();
        var filtered = _allRows
            .Where(row => MatchesCategory(row, _activeCategoryFilter))
            .Where(row => MatchesSearch(row, search))
            .OrderByDescending(row => row.HasRegression)
            .ThenByDescending(row => row.BestDelta)
            .ThenBy(row => row.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Scenario, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _scenarioBinding.DataSource = filtered;
        _listSummaryLabel.Text = BuildListSummary(filtered);
        UpdateRunSelectionUi();
        ApplySummaryTooltips();

        if (_testsGrid.Rows.Count == 0)
        {
            ClearSelectedScenario();
            return;
        }

        if (TrySelectScenarioById(targetScenarioId))
        {
            return;
        }

        SelectScenarioRow(0, 1);
    }

    private static bool MatchesSearch(ScenarioSummary row, string search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        return $"{row.Scenario} {row.DisplayName} {row.Fixture} {row.Tooling} {row.Goal} {row.Language}"
            .Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesCategory(ScenarioSummary row, string tag)
    {
        return tag switch
        {
            "all" => true,
            "goal:Token Savings" => row.Goal == "Token Savings",
            "goal:Latency" => row.Goal == "Latency",
            "goal:Correctness" => row.Goal == "Correctness",
            "goal:Determinism" => row.Goal == "Determinism",
            "tooling:History / Git" => row.Tooling == "History / Git",
            "tooling:Search / Grep" => row.Tooling == "Search / Grep",
            "tooling:Resolve / Symbol" => row.Tooling == "Resolve / Symbol",
            "tooling:Workflow / E2E" => row.Tooling == "Workflow / E2E",
            "language:CSharp" => row.Language == "CSharp",
            "language:Rust" => row.Language == "Rust",
            "language:Mixed" => row.Language == "Mixed",
            "smart:winners" => row.IsWinner,
            "smart:regressions" => row.HasRegression,
            "smart:flaky" => _flakyScenarioIds.Contains(row.Scenario),
            "smart:review" => row.NeedsReview,
            _ => true
        };
    }

    private void LoadSelectedScenarioDetails()
    {
        if (_loadedRunId is null || GetCurrentScenario() is not { } row)
        {
            ClearSelectedScenario();
            return;
        }

        try
        {
            using var conn = OpenSqlite(ResolvePath(_sqliteText.Text));
            var modes = LoadModes(conn, _loadedRunId, row.Scenario, row.Fixture);
            var classic = modes.TryGetValue("classic", out var c) ? c : EmptyMode("classic");
            var compact = modes.TryGetValue("compact", out var o) ? o : EmptyMode("compact");
            var hybrid = modes.TryGetValue("hybrid", out var h) ? h : EmptyMode("hybrid");

            _activeScenarioId = row.Scenario;
            _selectedScenarioLabel.Text = row.DisplayName;
            _selectedMetaLabel.Text = $"{row.Scenario} • {row.Tooling} • {row.Language} • {row.Goal}";
            RenderChips(row);

            RenderModeCard(_classicCardMetrics, _classicCardNote, classic, row, "classic");
            RenderModeCard(_compactCardMetrics, _compactCardNote, compact, row, "compact");
            RenderModeCard(_hybridCardMetrics, _hybridCardNote, hybrid, row, "hybrid");

            _summaryStripLabel.Text = $"{row.ClassicTokens} -> {row.CompactTokens} -> {row.HybridTokens} | {row.DeltaDisplay} | {row.SavingsDisplay}";
            ApplySummaryTooltips(row, classic, compact, hybrid);

            _compareClassicText.Text = FormatModeText("Classic", classic, row);
            _compareCompactText.Text = FormatModeText("Compact", compact, row);
            _compareHybridText.Text = FormatModeText("Hybrid", hybrid, row);
            _rawIoText.Text = FormatRawIo(row, classic, compact, hybrid);

            _stepsGrid.DataSource = LoadSteps(conn, _loadedRunId, row.Scenario, row.Fixture);
            _historyGrid.DataSource = LoadHistory(conn, row.Scenario);
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to load testcase detail: {ex.Message}");
        }
    }

    private Dictionary<string, ModeDetail> LoadModes(SqliteConnection conn, string runId, string scenario, string fixture)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT mode, COALESCE(input, ''), COALESCE(output, ''), COALESCE(tokens, 0), COALESCE(calls, 0),
                   COALESCE(elapsed_ms, 0), success, COALESCE(notes, '')
            FROM benchmark_mode_results
            WHERE run_id = $run_id AND scenario = $scenario AND fixture = $fixture
            ORDER BY CASE mode WHEN 'classic' THEN 0 WHEN 'compact' THEN 1 ELSE 2 END;
            """;
        cmd.Parameters.AddWithValue("$run_id", runId);
        cmd.Parameters.AddWithValue("$scenario", scenario);
        cmd.Parameters.AddWithValue("$fixture", fixture);

        using var reader = cmd.ExecuteReader();
        var result = new Dictionary<string, ModeDetail>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            var detail = new ModeDetail(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt64(5),
                reader.GetInt64(6) == 1,
                reader.GetString(7));
            result[detail.Mode] = detail;
        }

        return result;
    }

    private List<StepRow> LoadSteps(SqliteConnection conn, string runId, string scenario, string fixture)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT step_no, mode, operation, COALESCE(tokens, 0), COALESCE(elapsed_ms, 0), status,
                   COALESCE(input, ''), COALESCE(output, ''), COALESCE(notes, '')
            FROM benchmark_steps
            WHERE run_id = $run_id AND scenario = $scenario AND fixture = $fixture
            ORDER BY step_no;
            """;
        cmd.Parameters.AddWithValue("$run_id", runId);
        cmd.Parameters.AddWithValue("$scenario", scenario);
        cmd.Parameters.AddWithValue("$fixture", fixture);

        using var reader = cmd.ExecuteReader();
        var rows = new List<StepRow>();
        while (reader.Read())
        {
            rows.Add(new StepRow(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt64(4),
                reader.GetString(5),
                Preview(reader.GetString(6)),
                Preview(reader.GetString(7)),
                reader.GetString(8)));
        }

        return rows;
    }

    private List<HistoryRow> LoadHistory(SqliteConnection conn, string scenario)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT r.created_at_utc, b.run_id, b.passed,
                   COALESCE(b.baseline_tokens, 0), COALESCE(b.our_tokens, 0), COALESCE(b.hybrid_tokens, 0)
            FROM benchmark_results b
            JOIN benchmark_runs r ON r.run_id = b.run_id
            WHERE b.scenario = $scenario
            ORDER BY r.created_at_utc DESC
            LIMIT 20;
            """;
        cmd.Parameters.AddWithValue("$scenario", scenario);

        using var reader = cmd.ExecuteReader();
        var rows = new List<HistoryRow>();
        while (reader.Read())
        {
            var classic = reader.GetInt32(3);
            var compact = reader.GetInt32(4);
            var hybrid = reader.GetInt32(5);
            var best = classic - Math.Min(compact, hybrid);
            var savings = classic <= 0 ? "0.0%" : $"{Math.Round((best * 100.0) / classic, 1):0.0}%";
            rows.Add(new HistoryRow(
                reader.GetString(0),
                ShortRun(reader.GetString(1)),
                reader.GetInt64(2) == 1,
                classic,
                compact,
                hybrid,
                best,
                savings));
        }

        return rows;
    }

    private void RenderChips(ScenarioSummary row)
    {
        _chipStrip.Controls.Clear();
        _chipStrip.Controls.Add(CreateChip(row.Tooling, Color.FromArgb(231, 243, 239), Color.FromArgb(45, 109, 78)));
        _chipStrip.Controls.Add(CreateChip(row.Language, Color.FromArgb(238, 243, 251), Color.FromArgb(56, 86, 106)));
        _chipStrip.Controls.Add(CreateChip(row.Goal.Replace(" ", string.Empty), Color.FromArgb(245, 241, 231), Color.FromArgb(95, 90, 70)));

        var statusColor = row.PassDisplay switch
        {
            "PASS" => SuccessGreen,
            "REG" => DangerRed,
            "WARN" => WarnAmber,
            _ => DangerRed
        };
        _chipStrip.Controls.Add(CreateChip(row.PassDisplay, statusColor, Color.White));
    }

    private static Control CreateChip(string text, Color backColor, Color foreColor)
    {
        return new Label
        {
            AutoSize = true,
            Padding = new Padding(10, 4, 10, 4),
            Margin = new Padding(0, 0, 6, 0),
            BackColor = backColor,
            ForeColor = foreColor,
            Font = new Font("Segoe UI Semibold", 8F, FontStyle.Bold, GraphicsUnit.Point),
            Text = text
        };
    }

    private void RenderModeCard(Label metricsLabel, Label noteLabel, ModeDetail detail, ScenarioSummary row, string mode)
    {
        metricsLabel.Text = $"Tokens: {detail.Tokens}    Calls: {detail.Calls}    Latency: {detail.ElapsedMs} ms";
        noteLabel.Text = BuildModeNote(detail, row, mode);
    }

    private static string BuildModeNote(ModeDetail detail, ScenarioSummary row, string mode)
    {
        var lineCount = detail.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;
        return mode switch
        {
            "classic" when lineCount > 1 => "Broad output",
            "classic" => "Narrow output",
            "compact" when row.CompactDelta > 0 => "Saved tokens",
            "compact" when row.CompactDelta < 0 => "Regression",
            "hybrid" when detail.Input.Contains("fallback", StringComparison.OrdinalIgnoreCase) || detail.Notes.Contains("fallback", StringComparison.OrdinalIgnoreCase) => "Fallback used",
            "hybrid" => "Fallback not needed",
            _ => detail.Notes
        };
    }

    private static string FormatModeText(string title, ModeDetail detail, ScenarioSummary row)
    {
        return
            $"{title.ToUpperInvariant()}\r\n" +
            $"Scenario: {row.DisplayName}\r\n" +
            $"Id: {row.Scenario}\r\n" +
            $"Fixture: {row.Fixture}\r\n" +
            $"Tokens: {detail.Tokens} | Calls: {detail.Calls} | Latency: {detail.ElapsedMs} ms | Success: {detail.Success}\r\n\r\n" +
            "INPUT\r\n" +
            $"{NormalizeBlock(detail.Input)}\r\n\r\n" +
            "OUTPUT\r\n" +
            $"{NormalizeBlock(detail.Output)}";
    }

    private static string FormatRawIo(ScenarioSummary row, ModeDetail classic, ModeDetail compact, ModeDetail hybrid)
    {
        return
            $"SCENARIO {row.DisplayName}\r\n" +
            $"ID       {row.Scenario}\r\n" +
            $"FIXTURE  {row.Fixture}\r\n" +
            $"TOOLING  {row.Tooling}\r\n\r\n" +
            Section("CLASSIC", classic) +
            Section("COMPACT", compact) +
            Section("HYBRID", hybrid);

        static string Section(string title, ModeDetail detail)
        {
            return
                $"[{title}]\r\n" +
                $"Tokens: {detail.Tokens}\r\n" +
                $"Calls: {detail.Calls}\r\n" +
                $"Latency: {detail.ElapsedMs} ms\r\n" +
                "Input:\r\n" +
                $"{NormalizeBlock(detail.Input)}\r\n" +
                "Output:\r\n" +
                $"{NormalizeBlock(detail.Output)}\r\n\r\n";
        }
    }

    private static string NormalizeBlock(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "<empty>";
        }

        return text.Replace("\n", "\r\n").Trim();
    }

    private static string Preview(string text)
    {
        var singleLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
        if (singleLine.Length <= 96)
        {
            return singleLine;
        }

        return singleLine[..93] + "...";
    }

    private ScenarioSummary? GetCurrentScenario()
        => _testsGrid.CurrentRow?.DataBoundItem as ScenarioSummary;

    private List<ScenarioSummary> GetCheckedScenarios()
        => _allRows.Where(row => row.IsChecked).ToList();

    private void ClearSelectedScenario()
    {
        _activeScenarioId = null;
        _selectedScenarioLabel.Text = "No testcase selected";
        _selectedMetaLabel.Text = "Pick a testcase from the list.";
        _chipStrip.Controls.Clear();
        _summaryStripLabel.Text = "Classic -> Compact -> Hybrid";
        _classicCardMetrics.Text = "Tokens: - | Calls: - | Latency: -";
        _compactCardMetrics.Text = "Tokens: - | Calls: - | Latency: -";
        _hybridCardMetrics.Text = "Tokens: - | Calls: - | Latency: -";
        _classicCardNote.Text = "No data.";
        _compactCardNote.Text = "No data.";
        _hybridCardNote.Text = "No data.";
        _compareClassicText.Clear();
        _compareCompactText.Clear();
        _compareHybridText.Clear();
        _rawIoText.Clear();
        _stepsGrid.DataSource = null;
        _historyGrid.DataSource = null;
        ApplySummaryTooltips();
    }

    private static ModeDetail EmptyMode(string mode) => new(mode, "", "", 0, 0, 0, false, "");

    private void SetRunningState(bool running)
    {
        _isRunning = running;
        _runAllButton.Enabled = !running;
        _loadLatestButton.Enabled = !running;
        _openButton.Enabled = !running;
        _tasksText.Enabled = !running;
        _sqliteText.Enabled = !running;
        _outDirText.Enabled = !running;
        _extraArgsText.Enabled = !running;
        _historyOnlyCheck.Enabled = !running;
        UpdateRunSelectionUi();
    }

    private static SqliteConnection OpenSqlite(string sqlitePath)
    {
        var conn = new SqliteConnection($"Data Source={sqlitePath}");
        conn.Open();
        return conn;
    }

    private void OpenArtifacts()
    {
        var sqlitePath = ResolvePath(_sqliteText.Text);
        var outDir = ResolvePath(_outDirText.Text);
        var target = File.Exists(sqlitePath) ? sqlitePath : Directory.Exists(outDir) ? outDir : _repoRoot;
        Process.Start(new ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = true
        });
    }

    private static string DeriveScenarioTitle(string kind, string path, string scenarioId)
    {
        var file = Path.GetFileName(path.Replace('\\', Path.DirectorySeparatorChar));
        var label = string.IsNullOrWhiteSpace(file) ? scenarioId : file;
        return kind switch
        {
            "history_latest_touch" => $"Latest touch for {label}",
            "history_first_touch" => $"First touch for {label}",
            "history_touch_count" => $"Touch count for {label}",
            _ => HumanizeScenarioId(scenarioId)
        };
    }

    private static string HumanizeScenarioId(string scenarioId)
    {
        var text = scenarioId.Replace('.', ' ').Replace('_', ' ').Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return scenarioId;
        }

        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    private void OnScenarioGridCellClick(int rowIndex, int columnIndex)
    {
        if (rowIndex < 0 || rowIndex >= _testsGrid.Rows.Count)
        {
            return;
        }

        if (columnIndex >= 0 && _testsGrid.Columns[columnIndex].Name == ScenarioCheckColumnName)
        {
            ToggleScenarioChecked(rowIndex);
        }

        var targetColumn = columnIndex >= 0 && _testsGrid.Columns[columnIndex].Name == ScenarioCheckColumnName ? 1 : columnIndex;
        SelectScenarioRow(rowIndex, targetColumn);
    }

    private void SelectScenarioRow(int rowIndex, int columnIndex)
    {
        if (rowIndex < 0 || rowIndex >= _testsGrid.Rows.Count)
        {
            return;
        }

        var row = _testsGrid.Rows[rowIndex];
        _testsGrid.ClearSelection();
        row.Selected = true;
        var targetColumn = Math.Max(0, Math.Min(columnIndex, row.Cells.Count - 1));
        _testsGrid.CurrentCell = row.Cells[targetColumn];
        LoadSelectedScenarioDetails();
    }

    private void ToggleScenarioChecked(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _testsGrid.Rows.Count)
        {
            return;
        }

        if (_testsGrid.Rows[rowIndex].DataBoundItem is not ScenarioSummary row)
        {
            return;
        }

        row.IsChecked = !row.IsChecked;
        _testsGrid.Rows[rowIndex].Cells[ScenarioCheckColumnName].Value = row.IsChecked;
        _testsGrid.InvalidateRow(rowIndex);
        UpdateRunSelectionUi();
        _listSummaryLabel.Text = BuildListSummary(_scenarioBinding.List.Cast<ScenarioSummary>().ToList());
    }

    private bool TrySelectScenarioById(string? scenarioId)
    {
        if (string.IsNullOrWhiteSpace(scenarioId))
        {
            return false;
        }

        foreach (DataGridViewRow gridRow in _testsGrid.Rows)
        {
            if (gridRow.DataBoundItem is ScenarioSummary row &&
                string.Equals(row.Scenario, scenarioId, StringComparison.OrdinalIgnoreCase))
            {
                SelectScenarioRow(gridRow.Index, 1);
                return true;
            }
        }

        return false;
    }

    private void UpdateRunSelectionUi()
    {
        var checkedCount = _allRows.Count(row => row.IsChecked);
        _runSelectedButton.Text = checkedCount > 0 ? $"Run Checked ({checkedCount})" : "Run Checked";
        _runSelectedButton.Enabled = !_isRunning && checkedCount > 0;
    }

    private void DrawDetailTab(DrawItemEventArgs e)
    {
        var page = _detailsTabs.TabPages[e.Index];
        var bounds = e.Bounds;
        var selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        using var background = new SolidBrush(selected ? AccentBlue : Color.FromArgb(239, 232, 215));
        using var textBrush = new SolidBrush(selected ? Color.White : Color.FromArgb(79, 94, 103));
        using var borderPen = new Pen(Color.FromArgb(221, 213, 200));

        e.Graphics.FillRectangle(background, bounds);
        e.Graphics.DrawRectangle(borderPen, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        TextRenderer.DrawText(
            e.Graphics,
            page.Text,
            _detailsTabs.Font,
            bounds,
            textBrush.Color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private static string DetectTooling(string notes, string scenario)
    {
        var value = $"{notes} {scenario}".ToLowerInvariant();
        if (value.Contains("grep") || value.Contains("search"))
        {
            return "Search / Grep";
        }

        if (value.Contains("symbol") || value.Contains("resolve"))
        {
            return "Resolve / Symbol";
        }

        if (value.Contains("workflow") || value.Contains("e2e"))
        {
            return "Workflow / E2E";
        }

        return "History / Git";
    }

    private static string DetectGoal(string tooling, string kind, string scenario)
    {
        var value = $"{kind} {scenario}".ToLowerInvariant();
        if (value.Contains("latency"))
        {
            return "Latency";
        }

        if (value.Contains("determin"))
        {
            return "Determinism";
        }

        if (value.Contains("correct"))
        {
            return "Correctness";
        }

        return tooling switch
        {
            "Resolve / Symbol" => "Correctness",
            _ => "Token Savings"
        };
    }

    private static string DetectLanguage(string fixture, string scenario)
    {
        var value = $"{fixture} {scenario}".ToLowerInvariant();
        if (value.Contains(".cs") || value.Contains("csharp") || value.Contains(".csproj"))
        {
            return "CSharp";
        }

        if (value.Contains(".rs") || value.Contains("rust") || value.Contains("cargo"))
        {
            return "Rust";
        }

        return "Mixed";
    }

    private string BuildListSummary(IReadOnlyCollection<ScenarioSummary> rows)
    {
        if (rows.Count == 0)
        {
            return "No scenarios match the current filters.";
        }

        var checkedCount = _allRows.Count(x => x.IsChecked);
        var passCount = rows.Count(x => x.Passed);
        var avgSavings = rows.Average(x => x.BestSavingsPercent);
        var regressions = rows.Count(x => x.HasRegression);
        return $"{rows.Count} visible | checked {checkedCount} | pass {passCount}/{rows.Count} | avg savings {avgSavings:0.0}% | regressions {regressions}";
    }

    private void ApplySummaryTooltips(
        ScenarioSummary? row = null,
        ModeDetail? classic = null,
        ModeDetail? compact = null,
        ModeDetail? hybrid = null)
    {
        _toolTip.SetToolTip(_selectedScenarioLabel, row?.DisplayName ?? "");
        _toolTip.SetToolTip(_selectedMetaLabel, row is null ? "" : $"{row.Scenario} | {row.Tooling} | {row.Language} | {row.Goal} | {row.Fixture}");
        _toolTip.SetToolTip(_summaryStripLabel, row is null ? "" : $"Classic {row.ClassicTokens} -> Compact {row.CompactTokens} -> Hybrid {row.HybridTokens} | Best delta {row.DeltaDisplay} | Savings {row.SavingsDisplay}");
        _toolTip.SetToolTip(_classicCardMetrics, classic is null ? "" : $"Tokens: {classic.Tokens} | Calls: {classic.Calls} | Latency: {classic.ElapsedMs} ms");
        _toolTip.SetToolTip(_compactCardMetrics, compact is null ? "" : $"Tokens: {compact.Tokens} | Calls: {compact.Calls} | Latency: {compact.ElapsedMs} ms");
        _toolTip.SetToolTip(_hybridCardMetrics, hybrid is null ? "" : $"Tokens: {hybrid.Tokens} | Calls: {hybrid.Calls} | Latency: {hybrid.ElapsedMs} ms");
        _toolTip.SetToolTip(_classicCardNote, _classicCardNote.Text);
        _toolTip.SetToolTip(_compactCardNote, _compactCardNote.Text);
        _toolTip.SetToolTip(_hybridCardNote, _hybridCardNote.Text);
        _toolTip.SetToolTip(_listSummaryLabel, _listSummaryLabel.Text);
        _toolTip.SetToolTip(_overallSummaryLabel, _overallSummaryLabel.Text);
    }

    private void ConfigureScenarioGridColumns()
    {
        _testsGrid.Columns.Clear();
        _testsGrid.Columns.Add(CreateCheckColumn());
        _testsGrid.Columns.Add(CreateFillColumn("Name", nameof(ScenarioSummary.DisplayName), 180F));
        _testsGrid.Columns.Add(CreateTextColumn("Id", nameof(ScenarioSummary.Scenario), 106));
        _testsGrid.Columns.Add(CreateTextColumn("Goal", nameof(ScenarioSummary.Goal), 92));
        _testsGrid.Columns.Add(CreateTextColumn("Lang", nameof(ScenarioSummary.Language), 74));
        _testsGrid.Columns.Add(CreateTextColumn("Classic", nameof(ScenarioSummary.ClassicTokens), 72, DataGridViewContentAlignment.MiddleRight));
        _testsGrid.Columns.Add(CreateTextColumn("Compact", nameof(ScenarioSummary.CompactTokens), 72, DataGridViewContentAlignment.MiddleRight));
        _testsGrid.Columns.Add(CreateTextColumn("Hybrid", nameof(ScenarioSummary.HybridTokens), 72, DataGridViewContentAlignment.MiddleRight));
        _testsGrid.Columns.Add(CreateTextColumn("Delta", nameof(ScenarioSummary.DeltaDisplay), 70, DataGridViewContentAlignment.MiddleRight));
        _testsGrid.Columns.Add(CreateTextColumn("Savings", nameof(ScenarioSummary.SavingsDisplay), 78, DataGridViewContentAlignment.MiddleRight));
        _testsGrid.Columns.Add(CreateTextColumn("Pass", nameof(ScenarioSummary.PassDisplay), 64, DataGridViewContentAlignment.MiddleCenter));
        _testsGrid.Columns.Add(CreateFillColumn("Fixture", nameof(ScenarioSummary.Fixture), 120F));
    }

    private void ConfigureStepsGridColumns()
    {
        _stepsGrid.Columns.Clear();
        _stepsGrid.AutoGenerateColumns = false;
        _stepsGrid.Columns.Add(CreateTextColumn("#", nameof(StepRow.StepNo), 40, DataGridViewContentAlignment.MiddleRight));
        _stepsGrid.Columns.Add(CreateTextColumn("Mode", nameof(StepRow.Mode), 70));
        _stepsGrid.Columns.Add(CreateTextColumn("Operation", nameof(StepRow.Operation), 110));
        _stepsGrid.Columns.Add(CreateTextColumn("Tokens", nameof(StepRow.Tokens), 70, DataGridViewContentAlignment.MiddleRight));
        _stepsGrid.Columns.Add(CreateTextColumn("Ms", nameof(StepRow.ElapsedMs), 60, DataGridViewContentAlignment.MiddleRight));
        _stepsGrid.Columns.Add(CreateTextColumn("Status", nameof(StepRow.Status), 70));
        _stepsGrid.Columns.Add(CreateTextColumn("Input", nameof(StepRow.InputPreview), 260));
        _stepsGrid.Columns.Add(CreateFillColumn("Output", nameof(StepRow.OutputPreview)));
    }

    private void ConfigureHistoryGridColumns()
    {
        _historyGrid.Columns.Clear();
        _historyGrid.AutoGenerateColumns = false;
        _historyGrid.Columns.Add(CreateTextColumn("Created", nameof(HistoryRow.CreatedAtUtc), 154));
        _historyGrid.Columns.Add(CreateTextColumn("Run", nameof(HistoryRow.RunId), 76));
        _historyGrid.Columns.Add(CreateTextColumn("Pass", nameof(HistoryRow.Passed), 50, DataGridViewContentAlignment.MiddleCenter));
        _historyGrid.Columns.Add(CreateTextColumn("Classic", nameof(HistoryRow.ClassicTokens), 64, DataGridViewContentAlignment.MiddleRight));
        _historyGrid.Columns.Add(CreateTextColumn("Compact", nameof(HistoryRow.CompactTokens), 64, DataGridViewContentAlignment.MiddleRight));
        _historyGrid.Columns.Add(CreateTextColumn("Hybrid", nameof(HistoryRow.HybridTokens), 64, DataGridViewContentAlignment.MiddleRight));
        _historyGrid.Columns.Add(CreateTextColumn("Delta", nameof(HistoryRow.BestDelta), 64, DataGridViewContentAlignment.MiddleRight));
        _historyGrid.Columns.Add(CreateFillColumn("Savings", nameof(HistoryRow.Savings)));
    }

    private static DataGridViewColumn CreateCheckColumn()
    {
        return new DataGridViewCheckBoxColumn
        {
            Name = ScenarioCheckColumnName,
            HeaderText = "",
            DataPropertyName = nameof(ScenarioSummary.IsChecked),
            Width = 36,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            ReadOnly = true,
            Frozen = true
        };
    }

    private static DataGridViewColumn CreateTextColumn(string header, string propertyName, int width, DataGridViewContentAlignment alignment = DataGridViewContentAlignment.MiddleLeft)
    {
        return new DataGridViewTextBoxColumn
        {
            HeaderText = header,
            DataPropertyName = propertyName,
            Width = width,
            SortMode = DataGridViewColumnSortMode.Automatic,
            ReadOnly = true,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = alignment }
        };
    }

    private static DataGridViewColumn CreateFillColumn(string header, string propertyName, float fillWeight = 100F)
    {
        return new DataGridViewTextBoxColumn
        {
            HeaderText = header,
            DataPropertyName = propertyName,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = fillWeight,
            SortMode = DataGridViewColumnSortMode.Automatic,
            ReadOnly = true
        };
    }

    private void PaintScenarioRow(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _testsGrid.Rows.Count)
        {
            return;
        }

        if (_testsGrid.Rows[rowIndex].DataBoundItem is not ScenarioSummary row)
        {
            return;
        }

        var back = row.PassDisplay switch
        {
            "PASS" => Color.FromArgb(235, 246, 239),
            "REG" => Color.FromArgb(255, 242, 239),
            "WARN" => Color.FromArgb(255, 249, 239),
            _ => Color.FromArgb(255, 242, 239)
        };

        var gridRow = _testsGrid.Rows[rowIndex];
        gridRow.DefaultCellStyle.BackColor = back;
        gridRow.DefaultCellStyle.SelectionBackColor = AccentBlue;
        gridRow.DefaultCellStyle.SelectionForeColor = Color.White;
    }

    private void AppendLog(string line)
    {
        var sanitized = SanitizeLogLine(line);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return;
        }

        if (string.Equals(_lastLogLine, sanitized, StringComparison.Ordinal))
        {
            _lastLogCount++;
            return;
        }

        FlushLastLogLine();
        _lastLogLine = sanitized;
        _lastLogCount = 1;
    }

    private void FlushLastLogLine()
    {
        if (string.IsNullOrWhiteSpace(_lastLogLine))
        {
            return;
        }

        var line = _lastLogCount > 1 ? $"{_lastLogLine} (x{_lastLogCount})" : _lastLogLine;
        _logText.AppendText(line + Environment.NewLine);
        _logText.SelectionStart = _logText.TextLength;
        _logText.ScrollToCaret();
        _lastLogLine = null;
        _lastLogCount = 0;
    }

    private void ResetLogState()
    {
        _lastLogLine = null;
        _lastLogCount = 0;
    }

    private static string SanitizeLogLine(string raw)
    {
        var line = AnsiRegex.Replace(raw, "");
        line = line.Replace('\u2502', '|').Replace('\u2551', '|').Replace('\u2500', '-').TrimEnd();
        if (IsNoiseLine(line))
        {
            return "";
        }

        return line;
    }

    private static bool IsNoiseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return true;
        }

        var trimmed = line.Trim();
        const string noiseChars = "-|+:.";
        return trimmed.All(ch => noiseChars.Contains(ch));
    }

    private static string ShortCommit(string commit)
        => string.IsNullOrWhiteSpace(commit) ? "-" : commit[..Math.Min(7, commit.Length)];

    private static string ShortRun(string runId)
        => runId.Length <= 8 ? runId : runId[..8];
}
