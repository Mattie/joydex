using Joydex.Core.TaskAlerts;
using Joydex.Windows.TaskAlerts;

namespace Joydex.App;

/// <summary>
/// Shows the task and workspace rules that are excluded from Joydex status signaling.
/// </summary>
internal sealed class IgnoredTaskSourcesForm : ThemedForm
{
    private static readonly Size PreferredMinimumSize = new(620, 360);
    private readonly TaskAlertCoordinator _coordinator;
    private readonly Label _summary;
    private readonly ModernDataGridView _rules;
    private readonly Label _empty;
    private readonly RoundedButton _reenable;
    private readonly RoundedButton _close;

    public IgnoredTaskSourcesForm(TaskAlertCoordinator coordinator)
    {
        _coordinator = coordinator;
        Text = "Ignored Task Status Sources";
        StartPosition = FormStartPosition.CenterParent;
        SetLogicalMinimumSize(PreferredMinimumSize);
        Size = new Size(760, 440);
        ShowIcon = false;
        ShowInTaskbar = false;
        MinimizeBox = false;

        var root = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            RowCount = 3,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 12),
            RowCount = 2,
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _summary = new Label
        {
            AutoSize = true,
            Font = JoydexTheme.UiSemiboldFont,
            Margin = new Padding(0, 0, 0, 5),
        };
        header.Controls.Add(_summary, 0, 0);
        header.Controls.Add(new Label
        {
            AutoSize = true,
            Tag = ThemeTone.Subtle,
            Text = "These sources do not claim a wireless-pad or throttle status slot.",
        }, 0, 1);
        root.Controls.Add(header, 0, 0);

        _rules = new ModernDataGridView
        {
            AccessibleName = "Ignored task status sources",
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AutoGenerateColumns = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            Dock = DockStyle.Fill,
            ReadOnly = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        };
        _rules.Columns.Add(new DataGridViewTextBoxColumn
        {
            FillWeight = 55,
            HeaderText = "Scope",
            MinimumWidth = 100,
            Name = "Scope",
        });
        _rules.Columns.Add(new DataGridViewTextBoxColumn
        {
            FillWeight = 90,
            HeaderText = "Source",
            MinimumWidth = 160,
            Name = "Source",
        });
        _rules.Columns.Add(new DataGridViewTextBoxColumn
        {
            FillWeight = 220,
            HeaderText = "Match",
            MinimumWidth = 300,
            Name = "Match",
        });
        _empty = new Label
        {
            AccessibleName = "No ignored task status sources",
            Dock = DockStyle.Fill,
            Font = JoydexTheme.UiSemiboldFont,
            Tag = ThemeTone.Subtle,
            Text = "No ignored sources.\n\nSelect a task in Task Alerts and use Ignore selected ▾ to add one.",
            TextAlign = ContentAlignment.MiddleCenter,
        };
        var content = new Panel
        {
            AccessibleName = "Ignored source list",
            AccessibleRole = AccessibleRole.Pane,
            Dock = DockStyle.Fill,
        };
        content.Controls.Add(_rules);
        content.Controls.Add(_empty);
        root.Controls.Add(content, 0, 1);

        var footer = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 12, 0, 0),
            RowCount = 1,
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _reenable = new RoundedButton
        {
            AccessibleName = "Re-enable selected task status source",
            Enabled = false,
            Margin = new Padding(0, 0, 8, 0),
            Text = "Re-enable selected",
        };
        _reenable.Click += (_, _) => ReenableSelected();
        _rules.SelectionChanged += (_, _) =>
            _reenable.Enabled = _rules.SelectedRows.Count == 1;
        _close = new RoundedButton
        {
            AccessibleName = "Close ignored task status sources",
            DialogResult = DialogResult.Cancel,
            Text = "Close",
        };
        footer.Controls.Add(new Panel { Dock = DockStyle.Fill }, 0, 0);
        footer.Controls.Add(_reenable, 1, 0);
        footer.Controls.Add(_close, 2, 0);
        root.Controls.Add(footer, 0, 2);
        Controls.Add(root);
        CancelButton = _close;

        RefreshRules();
    }

    protected override void OnShown(EventArgs eventArgs)
    {
        base.OnShown(eventArgs);
        ActiveControl = _close;
        ClearRuleSelection();
        BeginInvoke(() =>
        {
            ActiveControl = _close;
            ClearRuleSelection();
        });
    }

    private void RefreshRules()
    {
        var suppressions = (_coordinator.GetSnapshot().Suppressions ?? [])
            .OrderBy(rule => rule.Scope)
            .ThenBy(rule => rule.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _rules.Rows.Clear();
        foreach (var rule in suppressions)
        {
            var rowIndex = _rules.Rows.Add(
                rule.Scope == TaskAlertSuppressionScope.Task ? "TASK" : "WORKSPACE",
                DisplaySource(rule),
                rule.Value);
            _rules.Rows[rowIndex].Tag = rule;
            _rules.Rows[rowIndex].Cells["Match"].ToolTipText = rule.Value;
        }

        ClearRuleSelection();
        _rules.Visible = suppressions.Length > 0;
        _empty.Visible = suppressions.Length == 0;
        if (_empty.Visible)
        {
            _empty.BringToFront();
        }

        _summary.Text = suppressions.Length == 1
            ? "1 ignored status source"
            : $"{suppressions.Length} ignored status sources";
        _reenable.Enabled = false;
    }

    private void ClearRuleSelection()
    {
        _rules.ClearSelection();
        _rules.CurrentCell = null;
        _reenable.Enabled = false;
    }

    private void ReenableSelected()
    {
        var rule = _rules.SelectedRows.Count == 1
            ? _rules.SelectedRows[0].Tag as TaskAlertSuppressionRule
            : null;
        if (rule is null)
        {
            return;
        }

        try
        {
            _ = _coordinator.RemoveSuppression(rule);
            RefreshRules();
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException)
        {
            MessageBox.Show(
                exception.Message,
                "Joydex task alerts",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static string DisplaySource(TaskAlertSuppressionRule rule)
    {
        if (rule.Scope == TaskAlertSuppressionScope.Task)
        {
            return rule.Value.Length <= 20 ? rule.Value : $"{rule.Value[..12]}...";
        }

        var name = Path.GetFileName(rule.Value.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? rule.Value : name;
    }
}
