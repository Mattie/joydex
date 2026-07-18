using System.Diagnostics;
using Joydex.Core.TaskAlerts;
using Joydex.Windows.TaskAlerts;

namespace Joydex.App;

internal sealed class TaskAlertsForm : Form
{
    private readonly TaskAlertCoordinator _coordinator;
    private readonly CodexHookManager _hooks;
    private readonly string _relayPath;
    private readonly string _linkToolProfilePath;
    private readonly Func<bool, Task> _setEnabled;
    private readonly CheckBox _enabled;
    private readonly Label _bank;
    private readonly Label _dropped;
    private readonly Label _telemetry;
    private readonly DataGridView _assignments;
    private readonly DataGridView _events;
    private readonly Label _hookStatus;
    private bool _updating;

    public TaskAlertsForm(
        TaskAlertCoordinator coordinator,
        CodexHookManager hooks,
        string relayPath,
        string linkToolProfilePath,
        Func<bool, Task> setEnabled)
    {
        _coordinator = coordinator;
        _hooks = hooks;
        _relayPath = relayPath;
        _linkToolProfilePath = linkToolProfilePath;
        _setEnabled = setEnabled;

        Text = "Joydex Task Alerts";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9F);
        MinimumSize = new Size(820, 520);
        Size = new Size(1080, 680);
        ShowIcon = false;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 4,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _enabled = new CheckBox
        {
            Text = "Task alerts",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
        };
        _enabled.CheckedChanged += async (_, _) =>
        {
            if (!_updating)
            {
                _enabled.Enabled = false;
                try
                {
                    await _setEnabled(_enabled.Checked);
                }
                catch (Exception exception)
                {
                    MessageBox.Show(
                        exception.Message,
                        "Joydex task alerts",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                finally
                {
                    _enabled.Enabled = true;
                    UpdateSnapshot(_coordinator.GetSnapshot());
                }
            }
        };

        var channelPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 8, 0, 8),
        };
        channelPanel.Controls.Add(new Label
        {
            Text = "Primary: M2-M4 B1, B2, B4, B5",
            AutoSize = true,
            Margin = new Padding(0, 5, 14, 0),
        });
        channelPanel.Controls.Add(new Label
        {
            Text = "Overflow: M1 B1-B6",
            AutoSize = true,
            Margin = new Padding(0, 5, 14, 0),
        });
        channelPanel.Controls.Add(new Label
        {
            Text = "M5: commands only",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 5, 0, 0),
        });
        channelPanel.Controls.Add(new Label
        {
            Text = "Current bank:",
            AutoSize = true,
            Margin = new Padding(20, 5, 6, 0),
        });
        _bank = new Label
        {
            AutoSize = true,
            Margin = new Padding(0, 5, 0, 0),
        };
        channelPanel.Controls.Add(_bank);
        channelPanel.Controls.Add(new Label
        {
            Text = "Dropped events:",
            AutoSize = true,
            Margin = new Padding(20, 5, 6, 0),
        });
        _dropped = new Label
        {
            AutoSize = true,
            Margin = new Padding(0, 5, 0, 0),
        };
        channelPanel.Controls.Add(_dropped);
        channelPanel.Controls.Add(new Label
        {
            Text = "Telemetry:",
            AutoSize = true,
            Margin = new Padding(20, 5, 6, 0),
        });
        _telemetry = new Label
        {
            AutoSize = true,
            Font = new Font("Consolas", 8.5F),
            Margin = new Padding(0, 5, 0, 0),
        };
        channelPanel.Controls.Add(_telemetry);

        _assignments = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AutoGenerateColumns = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ReadOnly = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        };
        _assignments.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Slot", FillWeight = 45 });
        _assignments.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Control", FillWeight = 70 });
        _assignments.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "State", FillWeight = 60 });
        _assignments.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Color", FillWeight = 55 });
        _assignments.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Session", FillWeight = 125 });
        _assignments.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Routing target", FillWeight = 210 });

        _events = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AutoGenerateColumns = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ReadOnly = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        };
        _events.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Received", FillWeight = 80 });
        _events.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Event", FillWeight = 95 });
        _events.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Result", FillWeight = 70 });
        _events.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Slot", FillWeight = 42 });
        _events.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "State", FillWeight = 60 });
        _events.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Session", FillWeight = 150 });
        _events.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Turn", FillWeight = 130 });

        var viewer = new TabControl { Dock = DockStyle.Fill };
        var currentTab = new TabPage("Current state");
        currentTab.Controls.Add(_assignments);
        var eventsTab = new TabPage("Event stream");
        eventsTab.Controls.Add(_events);
        viewer.TabPages.Add(currentTab);
        viewer.TabPages.Add(eventsTab);

        var hooksPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 8, 0, 0),
        };
        _hookStatus = new Label { AutoSize = true, Margin = new Padding(0, 6, 12, 0) };
        var installHooks = new Button { Text = "Install / Repair hooks", AutoSize = true };
        installHooks.Click += OnInstallHooks;
        var removeHooks = new Button { Text = "Remove hooks", AutoSize = true };
        removeHooks.Click += OnRemoveHooks;
        hooksPanel.Controls.AddRange([_hookStatus, installHooks, removeHooks]);
        hooksPanel.Controls.Add(new Label
        {
            Text = $"LED profile: {Path.GetFileName(_linkToolProfilePath)}",
            AutoSize = true,
            Margin = new Padding(20, 6, 8, 0),
        });
        var showLedProfile = new Button { Text = "Show LED profile", AutoSize = true };
        showLedProfile.Click += (_, _) => ShowLinkToolProfile();
        hooksPanel.Controls.Add(showLedProfile);

        layout.Controls.Add(_enabled, 0, 0);
        layout.Controls.Add(channelPanel, 0, 1);
        layout.Controls.Add(viewer, 0, 2);
        layout.Controls.Add(hooksPanel, 0, 3);
        Controls.Add(layout);

        _coordinator.Changed += OnCoordinatorChanged;
        UpdateSnapshot(_coordinator.GetSnapshot());
        UpdateHookStatus();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _coordinator.Changed -= OnCoordinatorChanged;
        }

        base.Dispose(disposing);
    }

    private void OnCoordinatorChanged(object? sender, TaskAlertSnapshot snapshot)
    {
        if (IsHandleCreated)
        {
            BeginInvoke(() => UpdateSnapshot(snapshot));
        }
    }

    private void UpdateSnapshot(TaskAlertSnapshot snapshot)
    {
        _updating = true;
        try
        {
            _enabled.Checked = snapshot.Enabled;
            _bank.Text = snapshot.BankAutomaticallyDetected
                ? $"M{snapshot.Bank} (automatic)"
                : $"M{snapshot.Bank} (fallback)";
            _dropped.Text = snapshot.DroppedEventCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var telemetry = LinkToolTelemetryState.From(snapshot);
            _telemetry.Text = $"P=[{telemetry.JoydexPrimaryB1State},{telemetry.JoydexPrimaryB2State}," +
                $"{telemetry.JoydexPrimaryB4State},{telemetry.JoydexPrimaryB5State}] " +
                $"O=[{telemetry.JoydexOverflowB1State},{telemetry.JoydexOverflowB2State}," +
                $"{telemetry.JoydexOverflowB3State},{telemetry.JoydexOverflowB4State}," +
                $"{telemetry.JoydexOverflowB5State},{telemetry.JoydexOverflowB6State}] " +
                $"Alpha={telemetry.JoydexAlphaState}";

            _assignments.Rows.Clear();
            foreach (var assignment in snapshot.Assignments)
            {
                var color = TaskAlertColors.Get(assignment.State);
                var page = TaskAlertSlots.Page(assignment.Slot);
                _assignments.Rows.Add(
                    page == TaskAlertPage.Primary
                        ? $"P{TaskAlertSlots.PageIndex(assignment.Slot)}"
                        : $"O{TaskAlertSlots.PageIndex(assignment.Slot)}",
                    page == TaskAlertPage.Primary
                        ? $"M2-M4 B{TaskAlertSlots.Button(assignment.Slot)}"
                        : $"M1 B{TaskAlertSlots.Button(assignment.Slot)}",
                    assignment.State.ToString().ToLowerInvariant(),
                    $"{color.Red:X2} {color.Green:X2} {color.Blue:X2}",
                    assignment.SessionId,
                    TaskDeepLinkNavigator.BuildUri(assignment.SessionId));
            }

            _events.Rows.Clear();
            foreach (var trace in (snapshot.RecentEvents ?? []).Reverse())
            {
                _events.Rows.Add(
                    trace.ReceivedAt.ToLocalTime().ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture),
                    trace.Event,
                    trace.Result,
                    trace.Slot is { } slot ? DisplaySlot(slot) : "—",
                    trace.State?.ToString().ToLowerInvariant() ?? "—",
                    trace.SessionId,
                    trace.TurnId ?? "—");
            }
        }
        finally
        {
            _updating = false;
        }
    }

    private static string DisplaySlot(int slot) => TaskAlertSlots.Page(slot) == TaskAlertPage.Primary
        ? $"P{TaskAlertSlots.PageIndex(slot)}"
        : $"O{TaskAlertSlots.PageIndex(slot)}";

    private void OnInstallHooks(object? sender, EventArgs eventArgs)
    {
        try
        {
            _hooks.InstallOrRepair(_relayPath);
            UpdateHookStatus();
            MessageBox.Show(
                "The three Joydex handlers were merged into hooks.json. Codex may ask you to trust them on first use.",
                "Joydex hooks",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Joydex hooks", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnRemoveHooks(object? sender, EventArgs eventArgs)
    {
        try
        {
            _hooks.Remove();
            UpdateHookStatus();
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Joydex hooks", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateHookStatus()
    {
        if (!File.Exists(_relayPath))
        {
            _hookStatus.Text = "Hooks: relay not packaged";
            return;
        }

        _hookStatus.Text = _hooks.Inspect(_relayPath) switch
        {
            JoydexHookState.Installed => "Hooks: installed",
            JoydexHookState.RepairNeeded => "Hooks: repair needed",
            _ => "Hooks: not installed",
        };
    }

    private void ShowLinkToolProfile()
    {
        if (!File.Exists(_linkToolProfilePath))
        {
            MessageBox.Show(
                "The LinkTool profile has not been generated yet. Restart Joydex with both VIRPIL devices connected.",
                "Joydex LinkTool profile",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo(
            "explorer.exe",
            $"/select,\"{_linkToolProfilePath}\"")
        {
            UseShellExecute = true,
        });
    }
}
