using Joydex.Core.T3;

namespace Joydex.App;

/// <summary>Explicit source configuration and reversible actions; opening the dialog performs no SSH work.</summary>
internal sealed class T3TaskSourceSettingsForm : Form
{
    private readonly RuntimeTaskAlertsConnectionServices _runtime;
    private readonly CheckBox _enabled = new() { Text = "Include T3 desktop tasks", AutoSize = true };
    private readonly TextBox _host = new() { Dock = DockStyle.Fill };
    private readonly TextBox _name = new() { Dock = DockStyle.Fill };
    private readonly TextBox _identity = new() { Dock = DockStyle.Fill, ReadOnly = true };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(660, 0), Dock = DockStyle.Fill };
    private readonly FlowLayoutPanel _actions = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly CancellationTokenSource _closing = new();
    private string _testedHost;
    private bool _busy;
    private Button? _save;

    public T3TaskSourceSettingsForm(RuntimeTaskAlertsConnectionServices runtime)
    {
        _runtime = runtime;
        var options = runtime.Current.Settings?.Desired.TaskAlerts.T3 ?? new();
        _enabled.Checked = options.Enabled;
        _host.Text = options.SshHost;
        _name.Text = options.DisplayName;
        _identity.Text = options.EnvironmentId;
        _testedHost = options.SshHost;
        Text = "T3 desktop tasks";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(730, 400);
        MinimumSize = new Size(650, 380);
        Font = JoydexTheme.UiFont;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, AutoScroll = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(_enabled, 0, 0); layout.SetColumnSpan(_enabled, 2);
        AddRow(layout, "SSH alias", _host, 1);
        AddRow(layout, "Display name", _name, 2);
        AddRow(layout, "Environment", _identity, 3);
        var test = new Button { Text = "Test connection", AutoSize = true };
        test.Click += async (_, _) => await RunAsync(async () =>
        {
            await RuntimeTaskAlertsRecoveryPolicy.ReconcileBeforeNewActionAsync(_runtime, _closing.Token);
            var result = await _runtime.TestT3ConnectionAsync(ReadOptions(discovery: true), _closing.Token);
            if (result.Success)
            {
                _identity.Text = result.EnvironmentId;
                _testedHost = _host.Text.Trim();
                _status.Text = $"Connected. {result.Tasks.Length} task records read. {result.ContextDiagnostic}";
            }
            else _status.Text = result.Reason ?? "Connection unavailable.";
        });
        var setup = new Button { Text = "Add missing shortcuts", AutoSize = true };
        setup.Click += async (_, _) => await ChangeShortcutsAsync(false);
        var remove = new Button { Text = "Remove Joydex shortcuts", AutoSize = true };
        remove.Click += async (_, _) => await ChangeShortcutsAsync(true);
        _actions.Controls.AddRange([test, setup, remove]);
        layout.Controls.Add(_actions, 0, 4); layout.SetColumnSpan(_actions, 2);
        _status.Text = runtime.Current.Snapshot.T3Status is { } status
            ? (status.Success ? "Connected. " : "Unavailable. ") + (status.Reason ?? status.ContextDiagnostic)
            : "Test the existing environment, then save to enable monitoring. Shortcut actions require verified desktop context.";
        layout.Controls.Add(_status, 0, 5); layout.SetColumnSpan(_status, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var save = new Button { Text = "Save", AutoSize = true };
        _save = save;
        save.Click += async (_, _) => await RunAsync(async () =>
        {
            var optionsToSave = ReadOptions();
            if (optionsToSave.Enabled) RequireTestedIdentity();
            await RuntimeTaskAlertsRecoveryPolicy.ReconcileBeforeNewActionAsync(_runtime, _closing.Token);
            var result = await _runtime.SetT3ConnectionAsync(optionsToSave, Guid.NewGuid(), _closing.Token);
            if (!result.Succeeded) { _status.Text = result.Detail; return; }
            DialogResult = DialogResult.OK;
            Close();
        });
        buttons.Controls.AddRange([cancel, save]);
        layout.Controls.Add(buttons, 0, 6); layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);
        CancelButton = cancel;
        FormClosed += (_, _) => _closing.Cancel();
    }

    private T3ConnectionOptions ReadOptions(bool discovery = false) => BuildOptions(
        _enabled.Checked, _host.Text, _name.Text, _identity.Text, discovery);

    internal static T3ConnectionOptions BuildOptions(bool enabled, string host, string displayName,
        string environmentId, bool discovery = false) => new T3ConnectionOptions
    {
        Enabled = !discovery && enabled, SshHost = host, DisplayName = displayName,
        EnvironmentId = discovery ? "" : environmentId
    }.Normalize();

    private void RequireTestedIdentity()
    {
        ValidateTestedIdentity(_host.Text, _identity.Text, _testedHost);
    }

    internal static void ValidateTestedIdentity(string host, string environmentId, string testedHost)
    {
        if (string.IsNullOrWhiteSpace(environmentId) || testedHost != host.Trim())
            throw new InvalidOperationException("Test this SSH alias before saving or changing shortcuts.");
    }

    private Task ChangeShortcutsAsync(bool remove) => RunAsync(async () =>
    {
        RequireTestedIdentity();
        await RuntimeTaskAlertsRecoveryPolicy.ReconcileBeforeNewActionAsync(_runtime, _closing.Token);
        var result = await _runtime.ChangeT3ShortcutsAsync(ReadOptions(), remove, _closing.Token);
        _status.Text = string.Join(Environment.NewLine, new[] { result.Reason ?? "Shortcut operation completed.", result.ContextDiagnostic }
            .Concat(result.Diagnostics));
    });

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        _actions.Enabled = false;
        if (_save is not null) _save.Enabled = false;
        _host.Enabled = _name.Enabled = _enabled.Enabled = false;
        try { await action(); }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (Exception error) { if (!IsDisposed) _status.Text = error.Message; }
        finally
        {
            _busy = false;
            if (!IsDisposed)
            {
                _actions.Enabled = true;
                if (_save is not null) _save.Enabled = true;
                _host.Enabled = _name.Enabled = _enabled.Enabled = true;
            }
        }
    }

    private static void AddRow(TableLayoutPanel layout, string label, Control control, int row)
    {
        layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 12, 8) }, 0, row);
        control.Margin = new Padding(0, 4, 0, 4);
        layout.Controls.Add(control, 1, row);
    }
}
