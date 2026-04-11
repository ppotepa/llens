using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Data.Sqlite;

namespace Llens.TestExplorer;

public sealed partial class MainForm : Form
{
    private const string ScenarioCheckColumnName = "ScenarioCheck";

    private static readonly Color AppBackground = Color.FromArgb(247, 244, 236);
    private static readonly Color CardBackground = Color.FromArgb(255, 255, 255);
    private static readonly Color CardBorder = Color.FromArgb(221, 213, 200);
    private static readonly Color HeaderBackground = Color.FromArgb(36, 53, 63);
    private static readonly Color MutedText = Color.FromArgb(103, 120, 129);
    private static readonly Color AccentBlue = Color.FromArgb(32, 79, 103);
    private static readonly Color AccentGreen = Color.FromArgb(47, 111, 86);
    private static readonly Color AccentSand = Color.FromArgb(234, 223, 191);
    private static readonly Color SuccessGreen = Color.FromArgb(45, 143, 93);
    private static readonly Color WarnAmber = Color.FromArgb(187, 142, 43);
    private static readonly Color DangerRed = Color.FromArgb(196, 90, 78);
    private static readonly Color CompareBlue = Color.FromArgb(238, 244, 251);
    private static readonly Color CompareGreen = Color.FromArgb(235, 246, 239);
    private static readonly Color CompareAmber = Color.FromArgb(255, 246, 231);
    private static readonly Regex AnsiRegex = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);

    private readonly string _repoRoot;
    private readonly string _tmpDir;

    private readonly TextBox _tasksText = new();
    private readonly TextBox _sqliteText = new();
    private readonly TextBox _outDirText = new();
    private readonly TextBox _extraArgsText = new();
    private readonly TextBox _searchText = new();
    private readonly CheckBox _historyOnlyCheck = new();
    private readonly Button _runAllButton = new();
    private readonly Button _runSelectedButton = new();
    private readonly Button _loadLatestButton = new();
    private readonly Button _openButton = new();
    private readonly TabControl _detailsTabs = new();
    private readonly ToolTip _toolTip = new();

    private readonly Label _runMetaLabel = new();
    private readonly Label _overallSummaryLabel = new();
    private readonly Label _listSummaryLabel = new();
    private readonly Label _selectedScenarioLabel = new();
    private readonly Label _selectedMetaLabel = new();
    private readonly Label _summaryStripLabel = new();

    private readonly FlowLayoutPanel _chipStrip = new();

    private readonly Label _classicCardMetrics = new();
    private readonly Label _compactCardMetrics = new();
    private readonly Label _hybridCardMetrics = new();
    private readonly Label _classicCardNote = new();
    private readonly Label _compactCardNote = new();
    private readonly Label _hybridCardNote = new();

    private readonly TreeView _categoryTree = new();
    private readonly DataGridView _testsGrid = new();
    private readonly DataGridView _stepsGrid = new();
    private readonly DataGridView _historyGrid = new();

    private readonly TextBox _compareClassicText = new();
    private readonly TextBox _compareCompactText = new();
    private readonly TextBox _compareHybridText = new();
    private readonly TextBox _rawIoText = new();
    private readonly TextBox _logText = new();

    private readonly BindingSource _scenarioBinding = new();

    private List<ScenarioSummary> _allRows = [];
    private Dictionary<string, ScenarioCatalogItem> _scenarioCatalog = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _flakyScenarioIds = new(StringComparer.OrdinalIgnoreCase);
    private string? _loadedRunId;
    private string? _loadedTasksPath;
    private string? _activeScenarioId;
    private string _activeCategoryFilter = "all";
    private string? _lastLogLine;
    private int _lastLogCount;
    private bool _isRunning;

    public MainForm()
    {
        _repoRoot = FindRepoRoot();
        _tmpDir = Path.Combine(_repoRoot, ".tmp", "test-explorer");
        Directory.CreateDirectory(_tmpDir);

        ConfigureForm();
        ConfigureDefaults();
        BuildLayout();
        HookEvents();
    }

    private void ConfigureForm()
    {
        Text = "Llens Test Explorer";
        BackColor = AppBackground;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        MinimumSize = new Size(1480, 960);
        WindowState = FormWindowState.Maximized;
        StartPosition = FormStartPosition.CenterScreen;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        UpdateStyles();
    }

    private void ConfigureDefaults()
    {
        _tasksText.Text = @"Llens.Bench/TaskPacks/agent-rust-csharp-history-100.tasks.json";
        _sqliteText.Text = @"Llens.Bench/out-gui/bench-results.db";
        _outDirText.Text = @"Llens.Bench/out-gui";
        _searchText.PlaceholderText = "Search scenario, path, language";
        _extraArgsText.PlaceholderText = "--repeats 1";
        _historyOnlyCheck.Text = "history-only";
        _historyOnlyCheck.Checked = true;

        ConfigureEditor(_compareClassicText);
        ConfigureEditor(_compareCompactText);
        ConfigureEditor(_compareHybridText);
        ConfigureEditor(_rawIoText);
        ConfigureEditor(_logText);
        _logText.WordWrap = false;
        _toolTip.ShowAlways = true;

        ConfigureGrid(_testsGrid);
        ConfigureGrid(_stepsGrid);
        ConfigureGrid(_historyGrid);

        _testsGrid.MultiSelect = false;
        _testsGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _testsGrid.AutoGenerateColumns = false;
        _testsGrid.EditMode = DataGridViewEditMode.EditProgrammatically;
        _testsGrid.RowTemplate.Height = 30;
        _testsGrid.Cursor = Cursors.Hand;
        _testsGrid.DataSource = _scenarioBinding;
    }

    private void HookEvents()
    {
        Shown += async (_, _) => await LoadLatestSqlRunAsync();
        _runAllButton.Click += async (_, _) => await RunAllAsync();
        _runSelectedButton.Click += async (_, _) => await RunSelectedAsync();
        _loadLatestButton.Click += async (_, _) => await LoadLatestSqlRunAsync();
        _openButton.Click += (_, _) => OpenArtifacts();
        _searchText.TextChanged += (_, _) => ApplyFilters();
        _categoryTree.AfterSelect += (_, e) => OnCategorySelected(e.Node);
        _testsGrid.SelectionChanged += (_, _) => LoadSelectedScenarioDetails();
        _testsGrid.CellClick += (_, e) => OnScenarioGridCellClick(e.RowIndex, e.ColumnIndex);
        _testsGrid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0)
            {
                _detailsTabs.SelectedIndex = 0;
            }
        };
        _testsGrid.RowPrePaint += (_, e) => PaintScenarioRow(e.RowIndex);
        _detailsTabs.DrawItem += (_, e) => DrawDetailTab(e);
    }

    private static void ConfigureEditor(TextBox textBox)
    {
        textBox.Multiline = true;
        textBox.ReadOnly = true;
        textBox.ScrollBars = ScrollBars.Both;
        textBox.Dock = DockStyle.Fill;
        textBox.BorderStyle = BorderStyle.None;
        textBox.BackColor = Color.White;
        textBox.Font = new Font("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point);
    }

    private static void ConfigureGrid(DataGridView grid)
    {
        grid.Dock = DockStyle.Fill;
        grid.BackgroundColor = CardBackground;
        grid.BorderStyle = BorderStyle.None;
        grid.ReadOnly = true;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.RowHeadersVisible = false;
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.ColumnHeadersHeight = 34;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(245, 241, 231);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(81, 101, 110);
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold, GraphicsUnit.Point);
        grid.DefaultCellStyle.SelectionBackColor = AccentBlue;
        grid.DefaultCellStyle.SelectionForeColor = Color.White;
        grid.DefaultCellStyle.BackColor = Color.White;
        grid.DefaultCellStyle.ForeColor = Color.FromArgb(28, 46, 53);
        grid.DefaultCellStyle.Padding = new Padding(4, 1, 4, 1);
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(252, 250, 246);
        grid.GridColor = Color.FromArgb(236, 229, 216);
    }

    private string ResolvePath(string rawPath)
        => Path.GetFullPath(Path.IsPathRooted(rawPath) ? rawPath : Path.Combine(_repoRoot, rawPath));

    private static string FindRepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (Directory.Exists(Path.Combine(current, "Llens.Bench")) &&
                Directory.Exists(Path.Combine(current, "Llens.TestExplorer")))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        return Directory.GetCurrentDirectory();
    }
}

internal sealed class ScenarioSummary
{
    public bool IsChecked { get; set; }
    public required string Scenario { get; init; }
    public required string DisplayName { get; init; }
    public required string Fixture { get; init; }
    public required string Tooling { get; init; }
    public required string Goal { get; init; }
    public required string Kind { get; init; }
    public required string Language { get; init; }
    public required string Notes { get; init; }
    public required bool Passed { get; init; }
    public required int ClassicTokens { get; init; }
    public required int CompactTokens { get; init; }
    public required int HybridTokens { get; init; }

    public int CompactDelta => ClassicTokens - CompactTokens;
    public int HybridDelta => ClassicTokens - HybridTokens;
    public int BestDelta => Math.Max(CompactDelta, HybridDelta);
    public double BestSavingsPercent => SafeSavingsPercent(ClassicTokens, Math.Min(CompactTokens, HybridTokens));
    public bool HasRegression => CompactDelta < 0 || HybridDelta < 0;
    public bool IsWinner => Passed && BestDelta > 0;
    public bool NeedsReview => !Passed || HasRegression || CompactTokens <= 0;

    public string DeltaDisplay => FormatSigned(BestDelta);
    public string SavingsDisplay => $"{BestSavingsPercent:0.0}%";
    public string PassDisplay => !Passed ? "FAIL" : HasRegression ? "REG" : BestDelta <= 0 ? "WARN" : "PASS";

    private static double SafeSavingsPercent(int baseline, int optimized)
        => baseline <= 0 ? 0 : Math.Round(((baseline - optimized) * 100.0) / baseline, 1);

    private static string FormatSigned(int value) => value > 0 ? $"+{value}" : value.ToString();
}

internal sealed record ModeDetail(
    string Mode,
    string Input,
    string Output,
    int Tokens,
    int Calls,
    long ElapsedMs,
    bool Success,
    string Notes);

internal sealed record StepRow(
    int StepNo,
    string Mode,
    string Operation,
    int Tokens,
    long ElapsedMs,
    string Status,
    string InputPreview,
    string OutputPreview,
    string Notes);

internal sealed record HistoryRow(
    string CreatedAtUtc,
    string RunId,
    bool Passed,
    int ClassicTokens,
    int CompactTokens,
    int HybridTokens,
    int BestDelta,
    string Savings);

internal sealed record ScenarioCatalogItem(
    string Scenario,
    string Title,
    string Kind,
    string Path,
    string Notes);

internal sealed class TaskPackFile
{
    public string Name { get; set; } = "subset-pack";
    public string? Repo { get; set; }
    public List<TaskPackItem> Tasks { get; set; } = [];
}

internal sealed class TaskPackItem
{
    public string Id { get; set; } = "";
    public string? Title { get; set; }
    public string Kind { get; set; } = "";
    public string Path { get; set; } = "";
    public string? Note { get; set; }
}
