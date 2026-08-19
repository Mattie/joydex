using System.Diagnostics;
using Joydex.Core.TaskAlerts;
using Joydex.Windows.TaskAlerts;

namespace Joydex.App;

internal sealed class TaskAlertsForm : ThemedForm
{
    private static readonly Size PreferredMinimumSize = new(820, 640);
    private static readonly Size PreferredWindowSize = new(1100, 720);
    private readonly TaskAlertCoordinator _coordinator;
    private readonly CodexHookManager _hooks;
    private readonly string _relayPath;
    private readonly string _linkToolProfilePath;
    private readonly Func<bool, Task> _setEnabled;
    private readonly Func<TaskAlertLedOptions, Task> _setLedOutput;
    private readonly CheckBox _enabled;
    private readonly Label _bank;
    private readonly Label _dropped;
    private readonly Label _telemetry;
    private readonly RoundedButton _telemetryToggle;
    private readonly ToolTip _toolTips = new();
    private readonly ModernDataGridView _assignments;
    private readonly ModernDataGridView _events;
    private readonly Label _selectedSource;
    private readonly RoundedButton _ignoreSelected;
    private readonly RoundedButton _ignoredSources;
    private readonly Label _hookStatus;
    private readonly Label _ledOutput;
    private readonly Label _ledProfile;
    private readonly RoundedButton _showLedProfile;
    private readonly PageTabButton _currentStateNav;
    private readonly PageTabButton _eventStreamNav;
    private readonly Panel _currentStatePage;
    private readonly Panel _eventStreamPage;
    private readonly object _snapshotSync = new();
    private TaskAlertSnapshot? _latestSnapshot;
    private bool _snapshotUpdateScheduled;
    private bool _eventStreamSelected;
    private bool _updating;
    private bool _keepInitialSelectionForDocumentation;
    private ContextMenuStrip? _ignoreMenu;

    public TaskAlertsForm(
        TaskAlertCoordinator coordinator,
        CodexHookManager hooks,
        string relayPath,
        string linkToolProfilePath,
        Func<bool, Task> setEnabled,
        Func<TaskAlertLedOptions, Task>? setLedOutput = null)
    {
        _coordinator = coordinator;
        _hooks = hooks;
        _relayPath = relayPath;
        _linkToolProfilePath = linkToolProfilePath;
        _setEnabled = setEnabled;
        _setLedOutput = setLedOutput ?? (options =>
        {
            _coordinator.SetLedOutput(options);
            return Task.CompletedTask;
        });

        Text = "Joydex Task Alerts";
        StartPosition = FormStartPosition.CenterScreen;
        SetLogicalMinimumSize(PreferredMinimumSize);
        Size = PreferredWindowSize;
        ShowIcon = false;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 3,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _enabled = new CheckBox
        {
            Text = "Task alerts",
            AutoSize = true,
            Font = JoydexTheme.UiSemiboldFont,
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

        var summaryCard = new CardPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 8),
        };
        var summaryLayout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Dock = DockStyle.Top,
            RowCount = 3,
        };
        summaryLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        summaryLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        summaryLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        summaryLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var summaryHeader = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 5,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            RowCount = 1,
        };
        summaryHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        summaryHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        summaryHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        summaryHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        summaryHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        summaryHeader.Controls.Add(_enabled, 0, 0);
        summaryHeader.Controls.Add(new Label
        {
            Text = "Current bank:",
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(12, 7, 6, 0),
        }, 1, 0);
        _bank = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Font = JoydexTheme.UiSemiboldFont,
            Margin = new Padding(0, 7, 0, 0),
        };
        summaryHeader.Controls.Add(_bank, 2, 0);
        summaryHeader.Controls.Add(new Label
        {
            Text = "Dropped events:",
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(18, 7, 6, 0),
        }, 3, 0);
        _dropped = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Font = JoydexTheme.MonoFont,
            Margin = new Padding(0, 7, 0, 0),
        };
        summaryHeader.Controls.Add(_dropped, 4, 0);
        summaryLayout.Controls.Add(summaryHeader, 0, 0);

        var channelLayout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            RowCount = 1,
        };
        channelLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
        channelLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        channelLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));
        channelLayout.Controls.Add(SummaryLabel("Primary: M2-M4 B1, B2, B4, B5"), 0, 0);
        channelLayout.Controls.Add(SummaryLabel("Overflow: M1 B1-B6"), 1, 0);
        channelLayout.Controls.Add(SummaryLabel("M5: commands only", ThemeTone.Subtle), 2, 0);
        summaryLayout.Controls.Add(channelLayout, 0, 1);

        var diagnosticsLayout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            RowCount = 1,
        };
        diagnosticsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        diagnosticsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _telemetryToggle = new RoundedButton
        {
            AccessibleName = "Show telemetry diagnostics",
            Margin = new Padding(0, 2, 10, 0),
            Text = "Show telemetry",
            Variant = ButtonVariant.Ghost,
        };
        _telemetry = new Label
        {
            AccessibleName = "Task alert telemetry diagnostics",
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Font = JoydexTheme.MonoFont,
            Margin = new Padding(0, 9, 0, 0),
            Tag = ThemeTone.Subtle,
            TextAlign = ContentAlignment.TopLeft,
            Visible = false,
        };
        _telemetryToggle.Click += (_, _) =>
        {
            _telemetry.Visible = !_telemetry.Visible;
            _telemetryToggle.Text = _telemetry.Visible ? "Hide telemetry" : "Show telemetry";
            _telemetryToggle.AccessibleName = _telemetryToggle.Text + " diagnostics";
        };
        diagnosticsLayout.Controls.Add(_telemetryToggle, 0, 0);
        diagnosticsLayout.Controls.Add(_telemetry, 1, 0);
        summaryLayout.Controls.Add(diagnosticsLayout, 0, 2);
        summaryCard.Controls.Add(summaryLayout);
        root.Controls.Add(summaryCard, 0, 0);

        _assignments = new ModernDataGridView
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
            AccessibleName = "Current task-alert assignments",
        };
        _assignments.Columns.Add(new DataGridViewTextBoxColumn { Name = "Slot", HeaderText = "Slot", FillWeight = 45, MinimumWidth = 60 });
        _assignments.Columns.Add(new DataGridViewTextBoxColumn { Name = "Control", HeaderText = "Control", FillWeight = 70, MinimumWidth = 110 });
        _assignments.Columns.Add(new DataGridViewTextBoxColumn { Name = "State", HeaderText = "State", FillWeight = 60, MinimumWidth = 100 });
        _assignments.Columns.Add(new DataGridViewTextBoxColumn { Name = "Color", HeaderText = "Color", FillWeight = 55, MinimumWidth = 95 });
        _assignments.Columns.Add(new DataGridViewTextBoxColumn { Name = "Session", HeaderText = "Task ID", FillWeight = 125, MinimumWidth = 230 });
        _assignments.Columns.Add(new DataGridViewTextBoxColumn { Name = "Workspace", HeaderText = "Workspace", FillWeight = 90, MinimumWidth = 150 });
        _assignments.Columns.Add(new DataGridViewTextBoxColumn { Name = "RoutingTarget", HeaderText = "Routing target", FillWeight = 210, MinimumWidth = 320 });

        _events = new ModernDataGridView
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
            AccessibleName = "Task-alert event stream",
        };
        _events.Columns.Add(new DataGridViewTextBoxColumn { Name = "Received", HeaderText = "Received", FillWeight = 80, MinimumWidth = 105 });
        _events.Columns.Add(new DataGridViewTextBoxColumn { Name = "Event", HeaderText = "Event", FillWeight = 95, MinimumWidth = 140 });
        _events.Columns.Add(new DataGridViewTextBoxColumn { Name = "Result", HeaderText = "Result", FillWeight = 70, MinimumWidth = 100 });
        _events.Columns.Add(new DataGridViewTextBoxColumn { Name = "Slot", HeaderText = "Slot", FillWeight = 42, MinimumWidth = 60 });
        _events.Columns.Add(new DataGridViewTextBoxColumn { Name = "State", HeaderText = "State", FillWeight = 60, MinimumWidth = 90 });
        _events.Columns.Add(new DataGridViewTextBoxColumn { Name = "Session", HeaderText = "Task ID", FillWeight = 150, MinimumWidth = 230 });
        _events.Columns.Add(new DataGridViewTextBoxColumn { Name = "Workspace", HeaderText = "Workspace", FillWeight = 100, MinimumWidth = 160 });
        _events.Columns.Add(new DataGridViewTextBoxColumn { Name = "Turn", HeaderText = "Turn", FillWeight = 130, MinimumWidth = 190 });
        _assignments.SelectionChanged += (_, _) => UpdateSuppressionActions();
        _events.SelectionChanged += (_, _) => UpdateSuppressionActions();

        var viewerCard = new CardPanel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };
        var viewerLayout = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            RowCount = 3,
        };
        viewerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        viewerLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        viewerLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        viewerLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var pageSelector = new FlowLayoutPanel
        {
            AccessibleName = "Task alert pages",
            AccessibleRole = AccessibleRole.PageTabList,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 6),
            WrapContents = false,
        };
        _currentStateNav = new PageTabButton
        {
            AccessibleName = "Current state page",
            AutoSize = true,
            Selected = true,
            Text = "Current state",
        };
        _eventStreamNav = new PageTabButton
        {
            AccessibleName = "Event stream page",
            AutoSize = true,
            TabStop = false,
            Text = "Event stream",
        };
        _currentStateNav.Click += (_, _) => SelectViewerPage(eventStream: false);
        _eventStreamNav.Click += (_, _) => SelectViewerPage(eventStream: true);
        pageSelector.Controls.Add(_currentStateNav);
        pageSelector.Controls.Add(_eventStreamNav);
        viewerLayout.Controls.Add(pageSelector, 0, 0);

        var actionBar = new TableLayoutPanel
        {
            AccessibleName = "Selected task alert actions",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 10),
            RowCount = 1,
        };
        actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _selectedSource = new Label
        {
            AccessibleName = "Selected task alert source",
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 12, 0),
            MinimumSize = new Size(0, JoydexTheme.StandardControlHeight),
            Tag = ThemeTone.Subtle,
            Text = "Select a row to manage its signaling.",
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _ignoreSelected = new RoundedButton
        {
            AccessibleDescription = "Opens a menu for choosing task or workspace scope.",
            AccessibleName = "Choose ignore scope for selected Codex task source",
            Enabled = false,
            Margin = new Padding(0, 0, 8, 0),
            Text = "Ignore selected ▾",
        };
        _ignoreSelected.Click += (_, _) => ShowIgnoreMenu();
        _ignoredSources = new RoundedButton
        {
            AccessibleName = "Manage ignored Codex task status sources",
            Text = "Ignored sources (0)",
        };
        _ignoredSources.Click += (_, _) => ShowIgnoredSources();
        actionBar.Controls.Add(_selectedSource, 0, 0);
        actionBar.Controls.Add(_ignoreSelected, 1, 0);
        actionBar.Controls.Add(_ignoredSources, 2, 0);
        viewerLayout.Controls.Add(actionBar, 0, 1);

        var pageHost = new Panel
        {
            AccessibleName = "Task alert details",
            AccessibleRole = AccessibleRole.Pane,
            Dock = DockStyle.Fill,
        };
        _currentStatePage = new Panel
        {
            AccessibleName = "Current state",
            AccessibleRole = AccessibleRole.Pane,
            Dock = DockStyle.Fill,
        };
        _currentStatePage.Controls.Add(_assignments);
        _eventStreamPage = new Panel
        {
            AccessibleName = "Event stream",
            AccessibleRole = AccessibleRole.Pane,
            Dock = DockStyle.Fill,
            Visible = false,
        };
        _eventStreamPage.Controls.Add(_events);
        pageHost.Controls.Add(_eventStreamPage);
        pageHost.Controls.Add(_currentStatePage);
        viewerLayout.Controls.Add(pageHost, 0, 2);
        viewerCard.Controls.Add(viewerLayout);
        root.Controls.Add(viewerCard, 0, 1);

        var hooksCard = new CardPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 8, 0, 0),
        };
        var hooksLayout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Dock = DockStyle.Top,
            RowCount = 3,
        };
        hooksLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        hooksLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        hooksLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        hooksLayout.Controls.Add(new Label
        {
            AutoSize = true,
            Font = JoydexTheme.SectionFont,
            Margin = new Padding(0, 0, 0, 8),
            Text = "INTEGRATION",
        }, 0, 0);
        var hooksPanel = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            RowCount = 2,
        };
        hooksPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        hooksPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        hooksPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        hooksPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        hooksPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        hooksPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _hookStatus = new Label { AutoSize = true, Margin = new Padding(0, 6, 12, 0) };
        var installHooks = new RoundedButton
        {
            AccessibleName = "Install or repair hooks",
            Text = "Install / repair",
        };
        installHooks.Click += OnInstallHooks;
        var removeHooks = new RoundedButton { Text = "Remove hooks" };
        removeHooks.Click += OnRemoveHooks;
        _ledProfile = new Label
        {
            Text = $"LED profile: {Path.GetFileName(_linkToolProfilePath)}",
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 12, 8, 0),
            TextAlign = ContentAlignment.TopLeft,
        };
        _showLedProfile = new RoundedButton { Text = "Show LED profile" };
        _showLedProfile.Click += (_, _) => ShowLinkToolProfile();
        _toolTips.SetToolTip(_ledProfile, _ledProfile.Text);
        _ledOutput = new Label
        {
            AccessibleName = "Task alert LED output mode",
            AutoSize = true,
            Margin = new Padding(0, 18, 8, 0),
        };
        var configureLeds = new RoundedButton
        {
            AccessibleName = "Configure task alert LED output and colors",
            Text = "Configure LEDs...",
        };
        configureLeds.Click += OnConfigureLeds;
        hooksPanel.Controls.Add(_hookStatus, 0, 0);
        hooksPanel.Controls.Add(installHooks, 1, 0);
        hooksPanel.Controls.Add(removeHooks, 2, 0);
        hooksPanel.Controls.Add(_ledProfile, 0, 1);
        hooksPanel.SetColumnSpan(_ledProfile, 2);
        hooksPanel.Controls.Add(_showLedProfile, 2, 1);
        hooksPanel.Controls.Add(_ledOutput, 0, 2);
        hooksPanel.SetColumnSpan(_ledOutput, 2);
        hooksPanel.Controls.Add(configureLeds, 2, 2);
        hooksLayout.Controls.Add(hooksPanel, 0, 1);
        hooksCard.Controls.Add(hooksLayout);
        root.Controls.Add(hooksCard, 0, 2);
        Controls.Add(root);

        UpdateHookStatus();
        TaskAlertSnapshot initialSnapshot;
        lock (_snapshotSync)
        {
            _coordinator.Changed += OnCoordinatorChanged;
            initialSnapshot = _coordinator.GetSnapshot();
            _latestSnapshot = initialSnapshot;
        }

        try
        {
            UpdateSnapshot(initialSnapshot);
        }
        catch
        {
            _coordinator.Changed -= OnCoordinatorChanged;
            throw;
        }
    }

    private static Label SummaryLabel(string text, ThemeTone tone = ThemeTone.Default) => new()
    {
        AutoEllipsis = true,
        Dock = DockStyle.Fill,
        Margin = Padding.Empty,
        Tag = tone,
        Text = text,
        TextAlign = ContentAlignment.MiddleLeft,
    };

    internal void SetSnapshotForDocumentation(TaskAlertSnapshot snapshot)
    {
        lock (_snapshotSync)
        {
            _latestSnapshot = snapshot;
        }

        UpdateSnapshot(snapshot);
    }

    internal void SelectAssignmentForDocumentation(string sessionId)
    {
        var row = _assignments.Rows
            .Cast<DataGridViewRow>()
            .FirstOrDefault(candidate => candidate.Tag is SuppressionTarget target
                && string.Equals(target.SessionId, sessionId, StringComparison.Ordinal));
        if (row is null)
        {
            throw new ArgumentException("The requested documentation task is not present.", nameof(sessionId));
        }

        _assignments.ClearSelection();
        row.Selected = true;
        _assignments.CurrentCell = row.Cells[0];
        _keepInitialSelectionForDocumentation = true;
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData is (Keys.Control | Keys.Tab) or (Keys.Control | Keys.Shift | Keys.Tab))
        {
            SelectViewerPage(!_eventStreamSelected);
            (_eventStreamSelected ? _eventStreamNav : _currentStateNav).Focus();
            return true;
        }

        return base.ProcessCmdKey(ref message, keyData);
    }

    private void SelectViewerPage(bool eventStream)
    {
        _eventStreamSelected = eventStream;
        _currentStateNav.Selected = !eventStream;
        _currentStateNav.TabStop = !eventStream;
        _eventStreamNav.Selected = eventStream;
        _eventStreamNav.TabStop = eventStream;
        _currentStatePage.Visible = !eventStream;
        _eventStreamPage.Visible = eventStream;
        (eventStream ? _eventStreamPage : _currentStatePage).BringToFront();
        UpdateSuppressionActions();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _coordinator.Changed -= OnCoordinatorChanged;
            _toolTips.Dispose();
            _ignoreMenu?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OnCoordinatorChanged(object? sender, TaskAlertSnapshot snapshot)
    {
        lock (_snapshotSync)
        {
            _latestSnapshot = snapshot;
        }

        if (IsDisposed || Disposing || !IsHandleCreated)
        {
            return;
        }

        lock (_snapshotSync)
        {
            if (_snapshotUpdateScheduled)
            {
                return;
            }

            _snapshotUpdateScheduled = true;
        }

        try
        {
            BeginInvoke(() =>
            {
                TaskAlertSnapshot? latest;
                lock (_snapshotSync)
                {
                    _snapshotUpdateScheduled = false;
                    latest = _latestSnapshot;
                }

                if (!IsDisposed && !Disposing && Visible && latest is not null)
                {
                    UpdateSnapshot(latest);
                }
            });
        }
        catch (InvalidOperationException) when (IsDisposed || Disposing || !IsHandleCreated)
        {
            lock (_snapshotSync)
            {
                _snapshotUpdateScheduled = false;
            }

            // The form handle was torn down between the pre-check and BeginInvoke.
        }
    }

    protected override void OnVisibleChanged(EventArgs eventArgs)
    {
        base.OnVisibleChanged(eventArgs);
        TaskAlertSnapshot? latest;
        lock (_snapshotSync)
        {
            latest = _latestSnapshot;
        }

        if (Visible && latest is not null && !IsDisposed && !Disposing)
        {
            UpdateSnapshot(latest);
        }
    }

    protected override void OnShown(EventArgs eventArgs)
    {
        base.OnShown(eventArgs);
        if (_keepInitialSelectionForDocumentation)
        {
            return;
        }

        _assignments.ClearSelection();
        _assignments.CurrentCell = null;
        _events.ClearSelection();
        _events.CurrentCell = null;
        UpdateSuppressionActions();
        BeginInvoke(() =>
        {
            _assignments.ClearSelection();
            _assignments.CurrentCell = null;
            _events.ClearSelection();
            _events.CurrentCell = null;
            UpdateSuppressionActions();
        });
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
            var ledOptions = snapshot.EffectiveLedOutput;
            _ledOutput.Text = ledOptions.Mode == TaskAlertLedOutputMode.DirectHid
                ? "LED output: Direct USB"
                : "LED output: VIRPIL LinkTool";
            var linkToolMode = ledOptions.Mode == TaskAlertLedOutputMode.LinkTool;
            _ledProfile.Enabled = linkToolMode;
            _showLedProfile.Enabled = linkToolMode;
            var telemetry = LinkToolTelemetryState.From(snapshot);
            _telemetry.Text = $"P=[{telemetry.JoydexPrimaryB1State},{telemetry.JoydexPrimaryB2State}," +
                $"{telemetry.JoydexPrimaryB4State},{telemetry.JoydexPrimaryB5State}] " +
                $"O=[{telemetry.JoydexOverflowB1State},{telemetry.JoydexOverflowB2State}," +
                $"{telemetry.JoydexOverflowB3State},{telemetry.JoydexOverflowB4State}," +
                $"{telemetry.JoydexOverflowB5State},{telemetry.JoydexOverflowB6State}] " +
                $"Alpha={telemetry.JoydexAlphaState}";
            _toolTips.SetToolTip(_telemetry, _telemetry.Text);

            var assignmentSelection = GetSelectedTarget(_assignments);
            var eventSelection = GetSelectedTarget(_events);
            _assignments.Rows.Clear();
            foreach (var assignment in snapshot.Assignments)
            {
                var color = TaskAlertColors.Get(assignment.State, ledOptions);
                var page = TaskAlertSlots.Page(assignment.Slot);
                var rowIndex = _assignments.Rows.Add(
                    page == TaskAlertPage.Primary
                        ? $"P{TaskAlertSlots.PageIndex(assignment.Slot)}"
                        : $"O{TaskAlertSlots.PageIndex(assignment.Slot)}",
                    page == TaskAlertPage.Primary
                        ? $"M2-M4 B{TaskAlertSlots.Button(assignment.Slot)}"
                        : $"M1 B{TaskAlertSlots.Button(assignment.Slot)}",
                    assignment.State.ToString().ToLowerInvariant(),
                    $"{color.Red:X2} {color.Green:X2} {color.Blue:X2}",
                    assignment.SessionId,
                    DisplayWorkspace(assignment.Workspace),
                    TaskDeepLinkNavigator.BuildUri(assignment.SessionId));
                _assignments.Rows[rowIndex].Tag = new SuppressionTarget(
                    assignment.SessionId,
                    assignment.Workspace);
                _assignments.Rows[rowIndex].Cells["Session"].ToolTipText = assignment.SessionId;
                _assignments.Rows[rowIndex].Cells["Workspace"].ToolTipText =
                    assignment.Workspace ?? "Workspace metadata will appear after the next event.";
                _assignments.Rows[rowIndex].Cells["RoutingTarget"].ToolTipText =
                    TaskDeepLinkNavigator.BuildUri(assignment.SessionId);
            }

            _events.Rows.Clear();
            foreach (var trace in (snapshot.RecentEvents ?? []).Reverse())
            {
                var rowIndex = _events.Rows.Add(
                    trace.ReceivedAt.ToLocalTime().ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture),
                    trace.Event,
                    trace.Result,
                    trace.Slot is { } slot ? DisplaySlot(slot) : "—",
                    trace.State?.ToString().ToLowerInvariant() ?? "—",
                    trace.SessionId,
                    DisplayWorkspace(trace.Workspace),
                    trace.TurnId ?? "—");
                _events.Rows[rowIndex].Tag = new SuppressionTarget(trace.SessionId, trace.Workspace);
                _events.Rows[rowIndex].Cells["Session"].ToolTipText = trace.SessionId;
                _events.Rows[rowIndex].Cells["Workspace"].ToolTipText =
                    trace.Workspace ?? "Workspace metadata was not provided by this event.";
                _events.Rows[rowIndex].Cells["Turn"].ToolTipText = trace.TurnId ?? "—";
            }

            RestoreSelection(_assignments, assignmentSelection);
            RestoreSelection(_events, eventSelection);

            var suppressionCount = snapshot.Suppressions?.Count ?? 0;
            _ignoredSources.Text = $"Ignored sources ({suppressionCount})";
            _ignoredSources.AccessibleName = suppressionCount == 1
                ? "Manage 1 ignored Codex task status source"
                : $"Manage {suppressionCount} ignored Codex task status sources";
            UpdateSuppressionActions();
        }
        finally
        {
            _updating = false;
        }
    }

    private void UpdateSuppressionActions()
    {
        var target = GetSelectedTarget();
        _ignoreSelected.Enabled = target is not null;
        if (target is null)
        {
            _selectedSource.Text = "Select a row to manage its signaling.";
            _toolTips.SetToolTip(_selectedSource, null);
            return;
        }

        var workspace = DisplayWorkspace(target.Workspace);
        _selectedSource.Text = $"Selected: {workspace} / {CompactTaskId(target.SessionId)}";
        _toolTips.SetToolTip(
            _selectedSource,
            string.IsNullOrWhiteSpace(target.Workspace)
                ? target.SessionId
                : $"{target.Workspace}\n{target.SessionId}");
    }

    private SuppressionTarget? GetSelectedTarget()
    {
        var grid = _eventStreamSelected ? _events : _assignments;
        return GetSelectedTarget(grid);
    }

    private static SuppressionTarget? GetSelectedTarget(DataGridView grid)
    {
        return grid.SelectedRows.Count == 1
            ? grid.SelectedRows[0].Tag as SuppressionTarget
            : null;
    }

    private static void RestoreSelection(DataGridView grid, SuppressionTarget? target)
    {
        grid.ClearSelection();
        grid.CurrentCell = null;
        if (target is null)
        {
            return;
        }

        var row = grid.Rows
            .Cast<DataGridViewRow>()
            .FirstOrDefault(candidate => candidate.Tag is SuppressionTarget candidateTarget
                && string.Equals(
                    candidateTarget.SessionId,
                    target.SessionId,
                    StringComparison.Ordinal));
        if (row is null)
        {
            return;
        }

        row.Selected = true;
        grid.CurrentCell = row.Cells[0];
    }

    private void ShowIgnoreMenu()
    {
        var target = GetSelectedTarget();
        if (target is null)
        {
            return;
        }

        _ignoreMenu?.Dispose();
        _ignoreMenu = new ContextMenuStrip
        {
            AccessibleName = "Ignore selected task status source",
        };
        var taskItem = new ToolStripMenuItem($"This task · {CompactTaskId(target.SessionId)}")
        {
            ToolTipText = target.SessionId,
        };
        taskItem.Click += (_, _) => IgnoreSelectedTask(target);
        var workspaceAvailable = !string.IsNullOrWhiteSpace(target.Workspace);
        var workspaceItem = new ToolStripMenuItem(workspaceAvailable
            ? $"This workspace · {DisplayWorkspace(target.Workspace)}"
            : "This workspace (unavailable)")
        {
            Enabled = workspaceAvailable,
            ToolTipText = workspaceAvailable
                ? target.Workspace
                : "This lifecycle event did not include workspace metadata.",
        };
        workspaceItem.Click += (_, _) => IgnoreSelectedWorkspace(target);
        _ignoreMenu.Items.Add(taskItem);
        _ignoreMenu.Items.Add(workspaceItem);
        _ignoreMenu.Show(_ignoreSelected, new Point(0, _ignoreSelected.Height));
    }

    private void IgnoreSelectedTask(SuppressionTarget target)
    {
        if (MessageBox.Show(
                $"Ignore this exact Codex task?\n\n{target.SessionId}\n\n" +
                "The rule stays active after Joydex restarts until you re-enable it.",
                "Ignore Codex task",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        TryChangeSuppression(() => _coordinator.AddSuppression(
            TaskAlertSuppressionScope.Task,
            target.SessionId));
    }

    private void IgnoreSelectedWorkspace(SuppressionTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.Workspace))
        {
            return;
        }

        if (MessageBox.Show(
                $"Ignore every Codex task launched from this exact workspace?\n\n{target.Workspace}\n\n" +
                "This also covers future task IDs and stays active after Joydex restarts.",
                "Ignore Codex workspace",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        TryChangeSuppression(() => _coordinator.AddSuppression(
            TaskAlertSuppressionScope.Workspace,
            target.Workspace));
    }

    private void ShowIgnoredSources()
    {
        using var form = new IgnoredTaskSourcesForm(_coordinator);
        _ = form.ShowDialog(this);
        UpdateSnapshot(_coordinator.GetSnapshot());
    }

    private static void TryChangeSuppression(Func<bool> change)
    {
        try
        {
            _ = change();
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

    private static string CompactTaskId(string sessionId) => sessionId.Length <= 20
        ? sessionId
        : $"{sessionId[..12]}...";

    private static string DisplayWorkspace(string? workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace))
        {
            return "—";
        }

        var name = Path.GetFileName(workspace.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? workspace : name;
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
        var status = InspectHookStatus(_hooks, _relayPath);
        _hookStatus.Text = status.Text;
        _toolTips.SetToolTip(_hookStatus, status.Error ?? string.Empty);
    }

    internal static (string Text, string? Error) InspectHookStatus(CodexHookManager hooks, string relayPath)
    {
        if (!File.Exists(relayPath))
        {
            return ("Hooks: relay not packaged", null);
        }

        try
        {
            return (hooks.Inspect(relayPath) switch
            {
                JoydexHookState.Installed => "Hooks: installed",
                JoydexHookState.RepairNeeded => "Hooks: repair needed",
                _ => "Hooks: not installed",
            }, null);
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException
            or UnauthorizedAccessException
            or System.Text.Json.JsonException)
        {
            return ("Hooks: status unavailable", exception.Message);
        }
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

    private async void OnConfigureLeds(object? sender, EventArgs eventArgs)
    {
        using var form = new TaskAlertLedSettingsForm(_coordinator.GetLedOutput());
        if (form.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        if (form.Options.Mode == TaskAlertLedOutputMode.DirectHid
            && _coordinator.GetLedOutput().Mode != TaskAlertLedOutputMode.DirectHid
            && MessageBox.Show(
                this,
                "Direct USB will take temporary host control of the CM3 and Alpha LEDs. " +
                "Close LinkTool and VPC utilities first. Enable it now?",
                "Enable Direct USB LEDs",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        try
        {
            Enabled = false;
            await _setLedOutput(form.Options);
            UpdateSnapshot(_coordinator.GetSnapshot());
            if (form.Options.Mode == TaskAlertLedOutputMode.LinkTool)
            {
                MessageBox.Show(
                    this,
                    "The Joydex LinkTool profile was regenerated. Reload it in LinkTool to apply color changes.",
                    "LinkTool profile updated",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Could not change LED output",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            Enabled = true;
        }
    }

    private sealed record SuppressionTarget(string SessionId, string? Workspace);
}
