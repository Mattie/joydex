using Joydex.Contracts;
using Joydex.Core.TaskAlerts;
using Joydex.Windows.TaskAlerts;

namespace Joydex.App;

/// <summary>Shows every pending question with its own exact-key dismissal action.</summary>
internal sealed class PendingTaskQuestionsForm : ThemedForm
{
    private readonly Func<TaskAlertSnapshot> _getSnapshot;
    private readonly Func<RuntimeTaskAlertQuestionDismissal, Guid, Task> _dismiss;
    private readonly ModernDataGridView _questions;
    private readonly Label _summary;
    private readonly Label _empty;
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 500 };
    private QuestionRow[] _displayed = [];
    private bool _dismissing;

    public PendingTaskQuestionsForm(Func<TaskAlertSnapshot> getSnapshot,
        Func<RuntimeTaskAlertQuestionDismissal, Guid, Task> dismiss)
    {
        _getSnapshot = getSnapshot;
        _dismiss = dismiss;
        Text = "Pending questions";
        StartPosition = FormStartPosition.CenterParent;
        SetLogicalMinimumSize(new Size(640, 360));
        Size = new Size(900, 480);
        ShowIcon = false;
        MinimizeBox = false;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 3,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _summary = new Label
        {
            AutoSize = true, Font = JoydexTheme.UiSemiboldFont, Margin = new Padding(0, 0, 0, 12),
        };
        root.Controls.Add(_summary, 0, 0);
        _questions = new ModernDataGridView
        {
            AccessibleName = "Pending task questions",
            Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false, AutoGenerateColumns = false, ReadOnly = true,
            RowHeadersVisible = false, MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
        };
        _questions.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _questions.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Question", HeaderText = "Question", FillWeight = 240, MinimumWidth = 280,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _questions.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Task", HeaderText = "Task", FillWeight = 100, MinimumWidth = 160,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _questions.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "Dismiss", HeaderText = "", Text = "Dismiss", UseColumnTextForButtonValue = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells,
        });
        _questions.CellContentClick += async (_, args) =>
        {
            if (_dismissing || args.RowIndex < 0 || args.ColumnIndex != _questions.Columns["Dismiss"].Index
                || _questions.Rows[args.RowIndex].Tag is not QuestionRow question) return;
            try { await DismissQuestionAsync(question.SessionId, question.Key); }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, "Could not dismiss question",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        _empty = new Label
        {
            AccessibleName = "No pending questions", Text = "No pending questions.",
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Tag = ThemeTone.Subtle,
        };
        var content = new Panel { Dock = DockStyle.Fill };
        content.Controls.Add(_questions);
        content.Controls.Add(_empty);
        root.Controls.Add(content, 0, 1);
        var close = new RoundedButton
        {
            Text = "Close", DialogResult = DialogResult.Cancel, Anchor = AnchorStyles.Right,
            Margin = new Padding(0, 12, 0, 0),
        };
        root.Controls.Add(close, 0, 2);
        Controls.Add(root);
        CancelButton = close;
        _refresh.Tick += (_, _) => RefreshQuestions();
        RefreshQuestions();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _refresh.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _refresh.Dispose();
        base.Dispose(disposing);
    }

    internal void RefreshQuestions()
    {
        if (_dismissing) return;
        var rows = _getSnapshot().Assignments.SelectMany(assignment =>
            (assignment.PendingAsyncQuestions ?? []).Select(key =>
            {
                var detail = assignment.Questions?.FirstOrDefault(question => question.Key == key);
                var workspace = Path.GetFileName(assignment.Workspace?.TrimEnd('\\', '/'));
                var task = string.IsNullOrWhiteSpace(workspace) ? assignment.SessionId : workspace;
                return new QuestionRow(assignment.SessionId, key,
                    detail?.Title ?? "Question text unavailable", task);
            })).ToArray();
        if (!_displayed.SequenceEqual(rows))
        {
            _questions.Rows.Clear();
            foreach (var row in rows)
            {
                var index = _questions.Rows.Add(row.Title, row.Task);
                _questions.Rows[index].Tag = row;
                _questions.Rows[index].Cells["Task"].ToolTipText = row.SessionId;
            }
            _questions.ClearSelection();
            _displayed = rows;
        }
        _summary.Text = rows.Length == 1 ? "1 pending question" : $"{rows.Length} pending questions";
        _empty.Visible = rows.Length == 0;
        _questions.Visible = rows.Length > 0;
    }

    /// <summary>Re-resolves the task slot and dismisses only the displayed question key.</summary>
    internal async Task DismissQuestionAsync(string sessionId, string questionKey)
    {
        if (_dismissing || !_displayed.Any(row => row.SessionId == sessionId && row.Key == questionKey)) return;
        var assignment = _getSnapshot().Assignments.FirstOrDefault(item => item.SessionId == sessionId
            && item.PendingAsyncQuestions?.Contains(questionKey, StringComparer.Ordinal) == true);
        if (assignment is null) { RefreshQuestions(); return; }
        try
        {
            _dismissing = true;
            _questions.Enabled = false;
            await _dismiss(new(assignment.Slot, sessionId, [questionKey]), Guid.NewGuid());
        }
        finally
        {
            _dismissing = false;
            _questions.Enabled = true;
            RefreshQuestions();
        }
    }

    private sealed record QuestionRow(string SessionId, string Key, string Title, string Task);
}
