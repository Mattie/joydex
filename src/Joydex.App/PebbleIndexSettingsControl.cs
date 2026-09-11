using Joydex.Core.Voice;

namespace Joydex.App;

internal sealed class PebbleIndexSettingsControl : UserControl
{
    private readonly CheckBox _enabled = new() { AutoSize = true, Text = "Enable Pebble Index receiver" };
    private readonly NumericUpDown _port = new()
    {
        Minimum = PebbleIndexPreferences.MinimumPort,
        Maximum = PebbleIndexPreferences.MaximumPort,
        Width = 110,
    };
    private readonly ComboBox _target = new()
    {
        Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDown,
        Name = "PebbleIndexTargetTask",
    };
    private readonly Label _status = new()
    {
        AutoSize = true,
        ForeColor = SystemColors.GrayText,
        MaximumSize = new Size(700, 0),
        Name = "PebbleIndexStatus",
    };
    private readonly Func<string?, CancellationToken, Task<DesktopTaskCatalog>> _listTasks;
    private readonly Action<string> _copyText;
    private readonly string _secretPath;
    private readonly string _inboxDirectory;
    private readonly CancellationTokenSource _lifetime = new();

    public PebbleIndexSettingsControl(
        PebbleIndexPreferences initial,
        string secretPath,
        string inboxDirectory,
        Func<string?, CancellationToken, Task<DesktopTaskCatalog>> listTasks,
        PebbleIndexReceiverStatus status,
        Action<string>? copyText = null)
    {
        _secretPath = secretPath;
        _inboxDirectory = Path.GetFullPath(inboxDirectory);
        _listTasks = listTasks;
        _copyText = copyText ?? Clipboard.SetText;
        AutoScroll = true;
        Dock = DockStyle.Fill;
        _enabled.Checked = initial.Enabled;
        _port.Value = initial.Port;
        if (!string.IsNullOrWhiteSpace(initial.TargetTaskId))
        {
            _target.Items.Add(new TaskChoice(new DesktopTaskSummary(
                initial.TargetTaskId, initial.TargetHostId, initial.TargetTaskLabel,
                string.Empty, null, null, 0)));
            _target.SelectedIndex = 0;
        }

        var fields = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Top, Padding = new Padding(8) };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddField(fields, 0, "Listen port", _port);
        AddField(fields, 1, "Codex task or task ID", _target);

        var commands = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(8), WrapContents = true };
        var refresh = new RoundedButton { AutoSize = true, Text = "Refresh tasks", Name = "PebbleIndexRefreshTasks" };
        refresh.Click += async (_, _) => await RefreshTasksAsync(refresh);
        var copyEndpoint = new RoundedButton
            { AutoSize = true, Text = "Copy local endpoint", Name = "PebbleIndexCopyEndpoint" };
        copyEndpoint.Click += (_, _) => CopySetupValue(
            "local endpoint",
            () => $"http://127.0.0.1:{(int)_port.Value}/pebble-index");
        var copyAuthorization = new RoundedButton
            { AutoSize = true, Text = "Copy Authorization header", Name = "PebbleIndexCopyAuthorization" };
        copyAuthorization.Click += (_, _) => CopySetupValue(
            "Authorization header",
            () => "Bearer " + PebbleIndexSecretStore.LoadOrCreate(_secretPath));
        var openInbox = new RoundedButton { AutoSize = true, Text = "Open inbox", Name = "PebbleIndexOpenInbox" };
        openInbox.Click += (_, _) => OpenInbox();
        commands.Controls.Add(refresh);
        commands.Controls.Add(copyEndpoint);
        commands.Controls.Add(copyAuthorization);
        commands.Controls.Add(openInbox);

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Top, Padding = new Padding(12) };
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Text = "Pebble Index",
        }, 0, 0);
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            Padding = new Padding(0, 4, 0, 12),
            Text = "Receive authenticated transcript-only webhooks from the Index mobile app and forward each one to one deliberately selected Codex task.",
        }, 0, 1);
        layout.Controls.Add(_enabled, 0, 2);
        layout.Controls.Add(fields, 0, 3);
        layout.Controls.Add(commands, 0, 4);
        _status.Text = FormatStatus(status);
        layout.Controls.Add(_status, 0, 5);
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            MaximumSize = new Size(700, 0),
            Padding = new Padding(0, 8, 0, 0),
            Text = "A received webhook is stored before Joydex acknowledges it. Duplicate deliveries are suppressed. Unconfirmed deliveries are held for review and are never retried automatically.",
        }, 0, 6);
        Controls.Add(layout);
    }

    public PebbleIndexPreferences ReadPreferences()
    {
        var selected = (_target.SelectedItem as TaskChoice)?.Task;
        var typedTaskId = selected is null && CodexTaskReference.TryParse(_target.Text, out var parsedTaskId)
            ? parsedTaskId
            : string.Empty;
        return new PebbleIndexPreferences(
            Enabled: _enabled.Checked,
            Port: (int)_port.Value,
            TargetTaskId: selected?.Id ?? typedTaskId,
            TargetHostId: selected?.HostId ?? (typedTaskId.Length > 0 ? "local" : string.Empty),
            TargetTaskLabel: selected?.Title ?? (typedTaskId.Length > 0 ? $"Task {typedTaskId[..8]}" : string.Empty)).Normalize();
    }

    private async Task RefreshTasksAsync(Control button)
    {
        button.Enabled = false;
        try
        {
            var selectedId = (_target.SelectedItem as TaskChoice)?.Task.Id;
            if (!CodexTaskReference.TryParse(selectedId ?? _target.Text, out selectedId)) selectedId = null;
            var catalog = await _listTasks(selectedId, _lifetime.Token).ConfigureAwait(true);
            _target.BeginUpdate();
            _target.Items.Clear();
            foreach (var task in catalog.Tasks) _target.Items.Add(new TaskChoice(task));
            _target.SelectedItem = _target.Items.Cast<TaskChoice>().FirstOrDefault(choice =>
                string.Equals(choice.Task.Id, selectedId, StringComparison.OrdinalIgnoreCase));
            if (_target.SelectedIndex < 0 && selectedId is not null) _target.Text = selectedId;
            _target.EndUpdate();
            _status.Text = $"Loaded {catalog.Tasks.Count} local Codex tasks.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) { _status.Text = "Task list unavailable: " + exception.Message; }
        finally { if (!button.IsDisposed) button.Enabled = true; }
    }

    private void OpenInbox()
    {
        try
        {
            Directory.CreateDirectory(_inboxDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _inboxDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            _status.Text = "Could not open the Pebble Index inbox: " + exception.Message;
        }
    }

    private void CopySetupValue(string label, Func<string> readValue)
    {
        try
        {
            _copyText(readValue());
        }
        catch (Exception exception)
        {
            _status.Text = $"Could not copy the Pebble Index {label}: {exception.Message}";
        }
    }

    private static string FormatStatus(PebbleIndexReceiverStatus status)
    {
        if (status.OutstandingCount == 0 || status.Latest is null) return status.Message;
        return status.Message + Environment.NewLine
            + $"Latest: {status.Latest.State} — {status.Latest.Detail}";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _lifetime.Cancel(); _lifetime.Dispose(); }
        base.Dispose(disposing);
    }

    private static void AddField(TableLayoutPanel layout, int row, string label, Control control)
    {
        layout.RowCount = Math.Max(layout.RowCount, row + 1);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { AutoSize = true, Padding = new Padding(0, 7, 12, 0), Text = label }, 0, row);
        layout.Controls.Add(control, 1, row);
    }

    private sealed record TaskChoice(DesktopTaskSummary Task)
    {
        public override string ToString() => string.IsNullOrWhiteSpace(Task.Title) ? Task.Id : $"{Task.Title} — {Task.Id[..8]}";
    }
}
