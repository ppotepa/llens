using System.Drawing;
using System.Windows.Forms;

namespace Llens.TestExplorer;

public sealed partial class MainForm
{
    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = AppBackground,
            Padding = new Padding(14)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 126F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.Controls.Add(BuildHeaderCard(), 0, 0);
        root.Controls.Add(BuildWorkspace(), 0, 1);
        Controls.Add(root);
    }

    private Control BuildHeaderCard()
    {
        var card = CreateCard();
        card.Padding = new Padding(14, 12, 14, 12);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var topRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2
        };
        topRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70F));
        topRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30F));

        var titleWrap = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true
        };
        titleWrap.Controls.Add(new Label
        {
            Text = "Llens Test Explorer",
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 16F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(34, 48, 55),
            Margin = new Padding(0, 0, 14, 0)
        });
        titleWrap.Controls.Add(new Label
        {
            Text = "Compare token-saving tooling against classic LLM workflows",
            AutoSize = true,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = MutedText,
            Margin = new Padding(0, 5, 0, 0)
        });

        _runMetaLabel.AutoSize = true;
        _runMetaLabel.Dock = DockStyle.Right;
        _runMetaLabel.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        _runMetaLabel.ForeColor = MutedText;
        _runMetaLabel.TextAlign = ContentAlignment.MiddleRight;
        _runMetaLabel.Text = "Run: no data loaded";

        topRow.Controls.Add(titleWrap, 0, 0);
        topRow.Controls.Add(_runMetaLabel, 1, 0);

        var inputs = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 6
        };
        inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68F));
        inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45F));
        inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52F));
        inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30F));
        inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52F));
        inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));

        inputs.Controls.Add(CreateFieldLabel("Task Pack"), 0, 0);
        inputs.Controls.Add(CreateInputBox(_tasksText), 1, 0);
        inputs.Controls.Add(CreateFieldLabel("SQLite"), 2, 0);
        inputs.Controls.Add(CreateInputBox(_sqliteText), 3, 0);
        inputs.Controls.Add(CreateFieldLabel("Search"), 4, 0);
        inputs.Controls.Add(CreateInputBox(_searchText), 5, 0);

        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 10
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58F));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28F));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74F));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34F));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92F));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90F));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112F));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96F));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70F));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38F));

        actions.Controls.Add(CreateFieldLabel("Out"), 0, 0);
        actions.Controls.Add(CreateInputBox(_outDirText), 1, 0);
        actions.Controls.Add(CreateFieldLabel("Args"), 2, 0);
        actions.Controls.Add(CreateInputBox(_extraArgsText), 3, 0);
        actions.Controls.Add(_historyOnlyCheck, 4, 0);
        actions.Controls.Add(ConfigureActionButton(_runAllButton, "Run All", AccentBlue, Color.White), 5, 0);
        actions.Controls.Add(ConfigureActionButton(_runSelectedButton, "Run Checked", AccentGreen, Color.White), 6, 0);
        actions.Controls.Add(ConfigureActionButton(_loadLatestButton, "Load Latest", AccentSand, Color.FromArgb(76, 74, 54)), 7, 0);
        actions.Controls.Add(ConfigureActionButton(_openButton, "Open", Color.FromArgb(236, 231, 220), Color.FromArgb(66, 82, 91)), 8, 0);

        _overallSummaryLabel.AutoSize = true;
        _overallSummaryLabel.Dock = DockStyle.Fill;
        _overallSummaryLabel.ForeColor = MutedText;
        _overallSummaryLabel.Text = "Keep the test list visible; use local tabs only for selected-case detail.";
        _overallSummaryLabel.TextAlign = ContentAlignment.MiddleRight;
        _overallSummaryLabel.AutoEllipsis = true;
        actions.Controls.Add(_overallSummaryLabel, 9, 0);

        layout.Controls.Add(topRow, 0, 0);
        layout.Controls.Add(inputs, 0, 1);
        layout.Controls.Add(actions, 0, 2);
        card.Controls.Add(layout);
        return card;
    }

    private Control BuildWorkspace()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 2,
            BackColor = AppBackground
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 248F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 316F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 47F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 53F));

        var catalog = BuildCatalogCard();
        layout.Controls.Add(catalog, 0, 0);
        layout.SetRowSpan(catalog, 2);

        layout.Controls.Add(BuildScenarioCard(), 1, 0);
        layout.Controls.Add(BuildSelectedSummaryCard(), 2, 0);

        var details = BuildDetailTabsCard();
        layout.Controls.Add(details, 1, 1);
        layout.SetColumnSpan(details, 2);

        return layout;
    }

    private Control BuildCatalogCard()
    {
        var card = CreateCard();
        var layout = CreateCardLayout(card, "Catalog", "Explorer stays visible. Select a view, then inspect a testcase.");
        layout.RowCount = 4;
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 134F));

        _categoryTree.Dock = DockStyle.Fill;
        _categoryTree.BorderStyle = BorderStyle.None;
        _categoryTree.BackColor = Color.White;
        _categoryTree.HideSelection = false;
        _categoryTree.FullRowSelect = true;
        _categoryTree.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        var notePanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(248, 245, 238),
            Padding = new Padding(14),
            Margin = new Padding(0, 10, 0, 0)
        };
        notePanel.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = MutedText,
            Text = "Views\n\n- Goal and tooling categories mirror the token-saving mission.\n- Use smart views to isolate winners, regressions, and cases that need review.\n- Check rows for batch runs; the active row drives the detail pane."
        });

        layout.Controls.Add(_categoryTree, 0, 2);
        layout.Controls.Add(notePanel, 0, 3);
        return card;
    }

    private Control BuildScenarioCard()
    {
        var card = CreateCard();
        var layout = CreateCardLayout(card, "Scenario List", "Primary view: scan, inspect one case, check many for batch run.");
        layout.RowCount = 4;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        _listSummaryLabel.Dock = DockStyle.Fill;
        _listSummaryLabel.ForeColor = MutedText;
        _listSummaryLabel.Text = "No run loaded.";
        _listSummaryLabel.AutoEllipsis = true;

        ConfigureScenarioGridColumns();
        layout.Controls.Add(_listSummaryLabel, 0, 2);
        layout.Controls.Add(_testsGrid, 0, 3);
        return card;
    }

    private Control BuildSelectedSummaryCard()
    {
        var card = CreateCard();
        var layout = CreateCardLayout(card, "Selected Test Summary", "Classic, compact, and hybrid stay visible together.");
        layout.RowCount = 9;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 33.333F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 33.333F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 33.333F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _selectedScenarioLabel.AutoSize = false;
        _selectedScenarioLabel.Dock = DockStyle.Fill;
        _selectedScenarioLabel.AutoEllipsis = true;
        _selectedScenarioLabel.Height = 24;
        _selectedScenarioLabel.Font = new Font("Consolas", 11F, FontStyle.Bold, GraphicsUnit.Point);
        _selectedScenarioLabel.ForeColor = Color.FromArgb(34, 48, 55);
        _selectedScenarioLabel.Text = "No testcase selected";

        _selectedMetaLabel.AutoSize = false;
        _selectedMetaLabel.Dock = DockStyle.Fill;
        _selectedMetaLabel.AutoEllipsis = true;
        _selectedMetaLabel.MaximumSize = new Size(0, 32);
        _selectedMetaLabel.Font = new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point);
        _selectedMetaLabel.ForeColor = MutedText;
        _selectedMetaLabel.Text = "Pick a testcase from the list.";

        _chipStrip.Dock = DockStyle.Fill;
        _chipStrip.WrapContents = true;
        _chipStrip.AutoSize = true;
        _chipStrip.Margin = new Padding(0, 4, 0, 4);

        layout.Controls.Add(_selectedScenarioLabel, 0, 2);
        layout.Controls.Add(_selectedMetaLabel, 0, 3);
        layout.Controls.Add(_chipStrip, 0, 4);
        layout.Controls.Add(CreateModeCard("Classic", CompareBlue, Color.FromArgb(40, 69, 90), _classicCardMetrics, _classicCardNote), 0, 5);
        layout.Controls.Add(CreateModeCard("Compact", CompareGreen, Color.FromArgb(41, 86, 63), _compactCardMetrics, _compactCardNote), 0, 6);
        layout.Controls.Add(CreateModeCard("Hybrid", CompareAmber, Color.FromArgb(117, 82, 29), _hybridCardMetrics, _hybridCardNote), 0, 7);

        _summaryStripLabel.Dock = DockStyle.Fill;
        _summaryStripLabel.Padding = new Padding(12, 6, 12, 6);
        _summaryStripLabel.BackColor = Color.FromArgb(247, 242, 232);
        _summaryStripLabel.ForeColor = MutedText;
        _summaryStripLabel.Text = "Classic -> Compact -> Hybrid";
        _summaryStripLabel.AutoEllipsis = true;
        _summaryStripLabel.MaximumSize = new Size(0, 28);
        _summaryStripLabel.Margin = new Padding(0, 6, 0, 0);
        layout.Controls.Add(_summaryStripLabel, 0, 8);

        return card;
    }

    private Control BuildDetailTabsCard()
    {
        var card = CreateCard();
        var layout = CreateCardLayout(card, "Selected Test Details", "Tabs are local to the selected testcase. They never replace the list view.");
        layout.RowCount = 3;
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        _detailsTabs.Dock = DockStyle.Fill;
        _detailsTabs.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold, GraphicsUnit.Point);
        _detailsTabs.Padding = new Point(12, 4);
        _detailsTabs.DrawMode = TabDrawMode.OwnerDrawFixed;
        _detailsTabs.ItemSize = new Size(96, 28);
        _detailsTabs.TabPages.Clear();
        _detailsTabs.TabPages.Add(BuildCompareTab());
        _detailsTabs.TabPages.Add(BuildTraceTab());
        _detailsTabs.TabPages.Add(BuildRawIoTab());
        _detailsTabs.TabPages.Add(BuildSqlHistoryTab());
        _detailsTabs.TabPages.Add(BuildRunLogTab());

        layout.Controls.Add(_detailsTabs, 0, 2);
        return card;
    }

    private TabPage BuildCompareTab()
    {
        var page = new TabPage("Compare") { BackColor = CardBackground };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(6)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));

        layout.Controls.Add(CreateComparePane("Classic Request / Return", CompareBlue, _compareClassicText), 0, 0);
        layout.Controls.Add(CreateComparePane("Compact Request / Return", CompareGreen, _compareCompactText), 1, 0);
        layout.Controls.Add(CreateComparePane("Hybrid Request / Return", CompareAmber, _compareHybridText), 2, 0);
        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildTraceTab()
    {
        var page = new TabPage("Trace") { BackColor = CardBackground };
        ConfigureStepsGridColumns();
        page.Controls.Add(_stepsGrid);
        return page;
    }

    private TabPage BuildRawIoTab()
    {
        var page = new TabPage("Raw IO") { BackColor = CardBackground };
        page.Controls.Add(WrapTextControl(_rawIoText));
        return page;
    }

    private TabPage BuildSqlHistoryTab()
    {
        var page = new TabPage("SQL History") { BackColor = CardBackground };
        ConfigureHistoryGridColumns();
        page.Controls.Add(_historyGrid);
        return page;
    }

    private TabPage BuildRunLogTab()
    {
        var page = new TabPage("Run Log") { BackColor = CardBackground };
        page.Controls.Add(WrapTextControl(_logText));
        return page;
    }

    private Panel CreateCard()
    {
        return new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = CardBackground,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 0, 14, 14),
            Padding = new Padding(14)
        };
    }

    private TableLayoutPanel CreateCardLayout(Control owner, string title, string subtitle)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            GrowStyle = TableLayoutPanelGrowStyle.AddRows
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18F));

        layout.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(32, 48, 56)
        }, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = subtitle,
            Dock = DockStyle.Fill,
            AutoSize = true,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = MutedText
        }, 0, 1);
        owner.Controls.Add(layout);
        return layout;
    }

    private static Label CreateFieldLabel(string text)
    {
        return new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = MutedText,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point)
        };
    }

    private static Control CreateInputBox(TextBox textBox)
    {
        textBox.Dock = DockStyle.Fill;
        textBox.BorderStyle = BorderStyle.FixedSingle;
        textBox.Margin = new Padding(0, 0, 10, 0);
        return textBox;
    }

    private static Control ConfigureActionButton(Button button, string text, Color backColor, Color foreColor)
    {
        button.Text = text;
        button.Dock = DockStyle.Fill;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = backColor;
        button.ForeColor = foreColor;
        button.Font = new Font("Segoe UI Semibold", 8.5F, FontStyle.Bold, GraphicsUnit.Point);
        return button;
    }

    private Control CreateModeCard(string title, Color backColor, Color titleColor, Label metricsLabel, Label noteLabel)
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = backColor,
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(10),
            Margin = new Padding(0, 6, 0, 0),
            MinimumSize = new Size(0, 84)
        };

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3
        };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 20F));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 20F));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var titleLabel = new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = titleColor
        };
        metricsLabel.Dock = DockStyle.Fill;
        metricsLabel.Height = 20;
        metricsLabel.Font = new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point);
        metricsLabel.ForeColor = Color.FromArgb(28, 46, 53);
        metricsLabel.Text = "Tokens: - | Calls: - | Latency: -";
        metricsLabel.AutoEllipsis = true;

        noteLabel.Dock = DockStyle.Fill;
        noteLabel.AutoSize = false;
        noteLabel.AutoEllipsis = true;
        noteLabel.Font = new Font("Segoe UI", 8F, FontStyle.Regular, GraphicsUnit.Point);
        noteLabel.ForeColor = MutedText;
        noteLabel.Text = "No data.";
        noteLabel.Padding = new Padding(0, 2, 0, 0);

        content.Controls.Add(titleLabel, 0, 0);
        content.Controls.Add(metricsLabel, 0, 1);
        content.Controls.Add(noteLabel, 0, 2);
        panel.Controls.Add(content);
        return panel;
    }

    private Control CreateComparePane(string title, Color backColor, TextBox textBox)
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = backColor,
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(14),
            Margin = new Padding(6)
        };
        panel.Controls.Add(textBox);
        panel.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Top,
            Height = 24,
            Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(32, 48, 56)
        });
        return panel;
    }

    private Control WrapTextControl(Control control)
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            BackColor = CardBackground
        };
        panel.Controls.Add(control);
        return panel;
    }
}
