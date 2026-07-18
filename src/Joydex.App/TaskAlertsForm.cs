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
    private readonly Dictionary<int, CheckBox> _channels = [];
    private readonly ComboBox _bank;
    private readonly DataGridView _assignments;
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
        MinimumSize = new Size(760, 470);
        Size = new Size(900, 560);
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
            Text = "Alert channels:",
            AutoSize = true,
            Margin = new Padding(0, 5, 8, 0),
        });
        foreach (var channel in TaskAlertChannels.Selectable)
        {
            var checkBox = new CheckBox { Text = $"B{channel}", AutoSize = true };
            checkBox.CheckedChanged += OnChannelsChanged;
            _channels[channel] = checkBox;
            channelPanel.Controls.Add(checkBox);
        }
        channelPanel.Controls.Add(new Label
        {
            Text = "B3 and B6 stay profile-controlled for bank indication.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(14, 5, 0, 0),
        });
        channelPanel.Controls.Add(new Label
        {
            Text = "Current bank:",
            AutoSize = true,
            Margin = new Padding(20, 5, 6, 0),
        });
        _bank = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 64,
        };
        _bank.Items.AddRange(["M1", "M2", "M3", "M4", "M5"]);
        _bank.SelectedIndexChanged += (_, _) =>
        {
            if (!_updating && _bank.SelectedIndex >= 0)
            {
                _coordinator.SetBank(_bank.SelectedIndex + 1);
            }
        };
        channelPanel.Controls.Add(_bank);

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
        _assignments.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Channel", FillWeight = 45 });
        _assignments.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "State", FillWeight = 60 });
        _assignments.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Color", FillWeight = 55 });
        _assignments.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Session", FillWeight = 125 });
        _assignments.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Routing target", FillWeight = 210 });

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
        layout.Controls.Add(_assignments, 0, 2);
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
            foreach (var pair in _channels)
            {
                pair.Value.Checked = snapshot.Channels.Contains(pair.Key);
            }

            _bank.SelectedIndex = snapshot.Bank - 1;

            _assignments.Rows.Clear();
            foreach (var assignment in snapshot.Assignments)
            {
                var color = TaskAlertColors.Get(assignment.State);
                _assignments.Rows.Add(
                    $"B{assignment.Channel}",
                    assignment.State.ToString().ToLowerInvariant(),
                    $"{color.Red:X2} {color.Green:X2} {color.Blue:X2}",
                    assignment.SessionId,
                    TaskDeepLinkNavigator.BuildUri(assignment.SessionId));
            }
        }
        finally
        {
            _updating = false;
        }
    }

    private void OnChannelsChanged(object? sender, EventArgs eventArgs)
    {
        if (_updating)
        {
            return;
        }

        var selected = _channels.Where(pair => pair.Value.Checked).Select(pair => pair.Key).ToArray();
        if (selected.Length == 0)
        {
            _updating = true;
            ((CheckBox)sender!).Checked = true;
            _updating = false;
            return;
        }

        _coordinator.SetChannels(selected);
    }

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
