using VirpilCodexPad.Core.Config;
using VirpilCodexPad.Core.Input;
using VirpilCodexPad.Core.Mapping;
using VirpilCodexPad.Windows.Input;

namespace VirpilCodexPad.App;

internal sealed class ConfigurationForm : Form
{
    private readonly string _configPath;
    private readonly string _windowStatePath;
    private readonly CompanionConfig _originalConfig;
    private readonly DirectInputJoystickSource _source;
    private readonly InputEventDetector _detector;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly ComboBox _deviceCombo = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _connectionLabel = new() { AutoSize = true, Text = "Looking for throttle..." };
    private readonly Label _inputLabel = new() { AutoSize = true, Text = "Held buttons: none" };
    private readonly Label _captureLabel = new() { AutoSize = true, ForeColor = Color.DarkBlue, Text = "Select a row and choose Capture." };
    private readonly DataGridView _bankGrid = CreateGrid();
    private readonly DataGridView _bindingGrid = CreateGrid();
    private readonly CheckBox _dryRunCheckBox = new() { AutoSize = true, Text = "Dry run (log actions without sending them)" };
    private readonly TextBox _simulatorProcessesTextBox = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _openTargetCombo = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button _captureBankButton = new() { AutoSize = true, Text = "Capture selector" };
    private readonly Button _captureBindingButton = new() { AutoSize = true, Text = "Capture action button" };
    private readonly Button _cancelCaptureButton = new() { AutoSize = true, Text = "Cancel capture", Visible = false };
    private readonly Button _loadDefaultsButton = new() { AutoSize = true, Text = "Load Codex Micro defaults" };
    private CaptureTarget? _captureTarget;
    private JoystickSnapshot? _lastSnapshot;
    private DateTimeOffset _captureReadyAt;
    private DateTimeOffset _nextReconnectAt;
    private string? _loadWarning;

    public ConfigurationForm(string configPath, string windowStatePath, IntPtr cooperativeWindowHandle)
    {
        _configPath = configPath;
        _windowStatePath = windowStatePath;
        try
        {
            _originalConfig = ConfigStore.LoadOrCreate(configPath);
        }
        catch (Exception exception)
        {
            _originalConfig = CompanionConfig.CreateSafeDefault();
            _loadWarning = $"The existing configuration could not be loaded. Saving will replace it with the values shown here. {exception.Message}";
        }

        _source = new DirectInputJoystickSource(cooperativeWindowHandle);
        _detector = new InputEventDetector(_originalConfig.Polling.AxisTraceThreshold);
        _pollTimer = new System.Windows.Forms.Timer { Interval = _originalConfig.Polling.PollIntervalMs };
        _pollTimer.Tick += OnPoll;

        Text = "Configure Virpil Codex Pad";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9F);
        MinimumSize = new Size(960, 720);
        Size = ClampToCurrentScreen(new Size(1500, 1000));
        ShowIcon = false;

        RestoreWindowState();

        BuildLayout();
        PopulateFromConfig();

        Shown += (_, _) =>
        {
            ConnectSelectedDevice();
            _pollTimer.Start();
            if (_loadWarning is not null)
            {
                MessageBox.Show(this, _loadWarning, "Configuration recovery", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
    }

    protected override void OnFormClosing(FormClosingEventArgs eventArgs)
    {
        var restoredSize = WindowState == FormWindowState.Normal ? Size : RestoreBounds.Size;
        ConfigurationWindowStateStore.Save(
            _windowStatePath,
            new ConfigurationWindowState(
                restoredSize.Width,
                restoredSize.Height,
                WindowState == FormWindowState.Maximized));

        base.OnFormClosing(eventArgs);
    }

    protected override void OnFormClosed(FormClosedEventArgs eventArgs)
    {
        _pollTimer.Stop();
        _pollTimer.Dispose();
        _source.Dispose();
        base.OnFormClosed(eventArgs);
    }

    private void RestoreWindowState()
    {
        var state = ConfigurationWindowStateStore.Load(_windowStatePath);
        if (state is null)
        {
            return;
        }

        Size = ClampToCurrentScreen(new Size(state.Width, state.Height));
        if (state.Maximized)
        {
            WindowState = FormWindowState.Maximized;
        }
    }

    private Size ClampToCurrentScreen(Size requestedSize)
    {
        var workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        var maximumWidth = Math.Max(MinimumSize.Width, workingArea.Width);
        var maximumHeight = Math.Max(MinimumSize.Height, workingArea.Height);

        return new Size(
            Math.Clamp(requestedSize.Width, MinimumSize.Width, maximumWidth),
            Math.Clamp(requestedSize.Height, MinimumSize.Height, maximumHeight));
    }

    private void BuildLayout()
    {
        var main = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            RowCount = 6,
        };
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 66));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 140));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

        var introduction = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 0, 0, 8),
            WrapContents = true,
        };
        introduction.Controls.Add(new Label
        {
            Anchor = AnchorStyles.Left,
            AutoSize = true,
            Margin = new Padding(0, 7, 12, 0),
            Text = "Start with the Codex Micro layout for the CM3, then adjust any row you want.",
        });
        _loadDefaultsButton.Click += (_, _) => LoadStarterProfile();
        introduction.Controls.Add(_loadDefaultsButton);
        main.Controls.Add(introduction, 0, 0);
        main.Controls.Add(BuildDeviceGroup(), 0, 1);
        main.Controls.Add(BuildBankGroup(), 0, 2);
        main.Controls.Add(BuildBindingGroup(), 0, 3);
        main.Controls.Add(BuildSafetyGroup(), 0, 4);
        main.Controls.Add(BuildFooter(), 0, 5);
        Controls.Add(main);
    }

    private Control BuildDeviceGroup()
    {
        var group = new GroupBox { Dock = DockStyle.Fill, Text = "Throttle" };
        var layout = new TableLayoutPanel
        {
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            Padding = new Padding(8, 4, 8, 4),
            RowCount = 4,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Anchor = AnchorStyles.Left, AutoSize = true, Text = "Device" }, 0, 0);
        layout.Controls.Add(_deviceCombo, 1, 0);
        layout.Controls.Add(new Label { Anchor = AnchorStyles.Left, AutoSize = true, Text = "Connection" }, 0, 1);
        layout.Controls.Add(_connectionLabel, 1, 1);
        layout.Controls.Add(new Label { Anchor = AnchorStyles.Left, AutoSize = true, Text = "Live input" }, 0, 2);
        layout.Controls.Add(_inputLabel, 1, 2);
        layout.Controls.Add(new Label { Anchor = AnchorStyles.Left, AutoSize = true, Text = "Capture" }, 0, 3);
        var captureStatus = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        captureStatus.Controls.Add(_captureLabel);
        _cancelCaptureButton.Click += (_, _) => CancelCapture();
        captureStatus.Controls.Add(_cancelCaptureButton);
        layout.Controls.Add(captureStatus, 1, 3);
        group.Controls.Add(layout);
        return group;
    }

    private Control BuildBankGroup()
    {
        _bankGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            FillWeight = 70,
            HeaderText = "Bank name",
            Name = "BankName",
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _bankGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            FillWeight = 30,
            HeaderText = "Selector button",
            Name = "SelectorButton",
            ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });

        var add = new Button { AutoSize = true, Text = "Add bank" };
        add.Click += (_, _) =>
        {
            CancelCapture();
            var index = _bankGrid.Rows.Add($"bank-{_bankGrid.Rows.Count + 1}", null);
            _bankGrid.CurrentCell = _bankGrid.Rows[index].Cells[0];
        };
        var remove = new Button { AutoSize = true, Text = "Remove bank" };
        remove.Click += (_, _) => RemoveCurrentRow(_bankGrid);
        _captureBankButton.Click += (_, _) => BeginCapture(
            _bankGrid,
            "SelectorButton",
            "Starting from an adjacent dial position, move directly into this bank position.");

        return BuildGridGroup("Banks", _bankGrid, add, remove, _captureBankButton);
    }

    private Control BuildBindingGroup()
    {
        _bindingGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            FillWeight = 24,
            HeaderText = "Label",
            Name = "BindingName",
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _bindingGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            FillWeight = 18,
            HeaderText = "Bank",
            Name = "BindingBank",
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _bindingGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            FillWeight = 10,
            HeaderText = "Button",
            Name = "BindingButton",
            ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _bindingGrid.Columns.Add(new DataGridViewComboBoxColumn
        {
            DataSource = new[] { "press", "release" },
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
            FillWeight = 10,
            FlatStyle = FlatStyle.Flat,
            HeaderText = "When",
            Name = "BindingTrigger",
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _bindingGrid.Columns.Add(new DataGridViewComboBoxColumn
        {
            DataSource = CodexActionCatalog.SupportedIds.ToArray(),
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
            FillWeight = 23,
            FlatStyle = FlatStyle.Flat,
            HeaderText = "Codex action",
            Name = "BindingAction",
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _bindingGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            FillWeight = 15,
            HeaderText = "Wheel notches",
            Name = "BindingWheelNotches",
            SortMode = DataGridViewColumnSortMode.NotSortable,
            ToolTipText = "Mouse-wheel notches per encoder detent; used only by scroll actions.",
        });

        _bindingGrid.CellBeginEdit += (_, eventArgs) =>
        {
            if (eventArgs.ColumnIndex == _bindingGrid.Columns["BindingWheelNotches"].Index
                && !IsScrollAction(_bindingGrid.Rows[eventArgs.RowIndex]))
            {
                eventArgs.Cancel = true;
            }
        };
        _bindingGrid.CellValueChanged += (_, eventArgs) =>
        {
            if (eventArgs.RowIndex >= 0
                && eventArgs.ColumnIndex == _bindingGrid.Columns["BindingAction"].Index)
            {
                UpdateWheelNotchesCell(_bindingGrid.Rows[eventArgs.RowIndex]);
            }
        };
        _bindingGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_bindingGrid.IsCurrentCellDirty
                && _bindingGrid.CurrentCell?.OwningColumn.Name == "BindingAction")
            {
                _bindingGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };

        var add = new Button { AutoSize = true, Text = "Add binding" };
        add.Click += (_, _) =>
        {
            CancelCapture();
            var bank = _bankGrid.Rows.Cast<DataGridViewRow>()
                .Select(row => Convert.ToString(row.Cells["BankName"].Value))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
            var index = _bindingGrid.Rows.Add(
                $"binding-{_bindingGrid.Rows.Count + 1}",
                bank,
                null,
                "press",
                "new-task",
                null);
            UpdateWheelNotchesCell(_bindingGrid.Rows[index]);
            _bindingGrid.CurrentCell = _bindingGrid.Rows[index].Cells[0];
        };
        var remove = new Button { AutoSize = true, Text = "Remove binding" };
        remove.Click += (_, _) => RemoveCurrentRow(_bindingGrid);
        _captureBindingButton.Click += (_, _) => BeginCapture(_bindingGrid, "BindingButton", "Press the throttle button for this Codex action.");

        return BuildGridGroup("Bindings", _bindingGrid, add, remove, _captureBindingButton);
    }

    private static bool IsScrollAction(DataGridViewRow row)
    {
        var action = Convert.ToString(row.Cells["BindingAction"].Value);
        return string.Equals(action, "scroll-up", StringComparison.OrdinalIgnoreCase)
            || string.Equals(action, "scroll-down", StringComparison.OrdinalIgnoreCase);
    }

    private static void UpdateWheelNotchesCell(DataGridViewRow row)
    {
        var cell = row.Cells["BindingWheelNotches"];
        var isScrollAction = IsScrollAction(row);
        cell.ReadOnly = !isScrollAction;
        cell.Style.BackColor = isScrollAction ? SystemColors.Window : SystemColors.Control;
        cell.Style.ForeColor = isScrollAction ? SystemColors.WindowText : SystemColors.GrayText;

        if (isScrollAction)
        {
            if (!int.TryParse(Convert.ToString(cell.Value), out _))
            {
                cell.Value = ButtonBinding.DefaultWheelNotches;
            }
        }
        else
        {
            cell.Value = null;
        }
    }

    private Control BuildSafetyGroup()
    {
        var group = new GroupBox { Dock = DockStyle.Fill, Text = "Safety and Open" };
        var layout = new TableLayoutPanel
        {
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            Padding = new Padding(8, 4, 8, 4),
            RowCount = 4,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(_dryRunCheckBox, 0, 0);
        layout.SetColumnSpan(_dryRunCheckBox, 2);
        layout.Controls.Add(new Label { Anchor = AnchorStyles.Left, AutoSize = true, Text = "Block when these apps run" }, 0, 1);
        layout.Controls.Add(_simulatorProcessesTextBox, 1, 1);
        layout.Controls.Add(new Label { Anchor = AnchorStyles.Left, AutoSize = true, Text = "Open working directory in" }, 0, 2);
        _openTargetCombo.Items.AddRange(
            [OpenWorkingDirectoryOptions.VisualStudioCodeTarget, OpenWorkingDirectoryOptions.FileExplorerTarget]);
        layout.Controls.Add(_openTargetCombo, 1, 2);
        var hint = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = SystemColors.GrayText,
            Text = "Comma-separated process names, for example: DCS, FlightSimulator. Keyboard actions always require Codex in the foreground.",
        };
        layout.Controls.Add(hint, 0, 3);
        layout.SetColumnSpan(hint, 2);
        group.Controls.Add(layout);
        return group;
    }

    private Control BuildFooter()
    {
        var save = new Button { AutoSize = true, Text = "Save and close" };
        save.Click += OnSave;
        var cancel = new Button { AutoSize = true, DialogResult = DialogResult.Cancel, Text = "Cancel" };
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 8, 0, 0),
            WrapContents = false,
        };
        footer.Controls.Add(save);
        footer.Controls.Add(cancel);
        AcceptButton = save;
        CancelButton = cancel;
        return footer;
    }

    private static Control BuildGridGroup(string title, DataGridView grid, params Button[] buttons)
    {
        var group = new GroupBox { Dock = DockStyle.Fill, Text = title };
        var layout = new TableLayoutPanel { ColumnCount = 1, Dock = DockStyle.Fill, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(grid, 0, 0);

        var commands = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 4, 0, 0),
            WrapContents = false,
        };
        commands.Controls.AddRange(buttons);
        layout.Controls.Add(commands, 0, 1);
        group.Controls.Add(layout);
        return group;
    }

    private static DataGridView CreateGrid() => new()
    {
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BackgroundColor = SystemColors.Window,
        BorderStyle = BorderStyle.Fixed3D,
        Dock = DockStyle.Fill,
        MultiSelect = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
    };

    private void LoadStarterProfile()
    {
        CancelCapture();
        if ((_bankGrid.Rows.Count > 0 || _bindingGrid.Rows.Count > 0)
            && MessageBox.Show(
                this,
                "Replace the rows currently shown with the CM3 Codex Micro starter layout?",
                "Load Codex Micro defaults",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1) != DialogResult.Yes)
        {
            return;
        }

        var dialProfile = CodexMicroStarterProfile.DetectDialProfile(_lastSnapshot);
        var profile = CodexMicroStarterProfile.Create(dialProfile);

        _bankGrid.Rows.Clear();
        foreach (var (bank, button) in profile.BankSelectors)
        {
            _bankGrid.Rows.Add(bank, button);
        }

        _bindingGrid.Rows.Clear();
        foreach (var binding in profile.Bindings)
        {
            var rowIndex = _bindingGrid.Rows.Add(
                binding.Name,
                binding.Bank,
                binding.Button,
                binding.Trigger,
                binding.Action,
                binding.WheelNotches);
            UpdateWheelNotchesCell(_bindingGrid.Rows[rowIndex]);
        }

        var profileName = dialProfile == Cm3ModeDialProfile.FiveWayShift
            ? "CM3 5-way shift mode"
            : "CM3 standard mode buttons";
        _captureLabel.Text = $"Loaded {profileName}. Save to use this device layout.";
    }

    private void PopulateFromConfig()
    {
        IReadOnlyList<DirectInputDeviceInfo> devices;
        try
        {
            devices = _source.EnumerateDevices();
        }
        catch (Exception exception)
        {
            devices = [];
            _connectionLabel.Text = $"Could not enumerate devices: {exception.Message}";
        }

        _deviceCombo.DisplayMember = nameof(DirectInputDeviceInfo.ProductName);
        foreach (var device in devices)
        {
            _deviceCombo.Items.Add(device);
        }

        var selectedDevice = devices.FirstOrDefault(device =>
            Guid.TryParse(_originalConfig.Device.InstanceGuid, out var configuredGuid)
                ? device.InstanceGuid == configuredGuid
                : device.ProductName.Contains(_originalConfig.Device.ProductNameContains, StringComparison.OrdinalIgnoreCase));
        if (selectedDevice is not null)
        {
            _deviceCombo.SelectedItem = selectedDevice;
        }
        else if (_deviceCombo.Items.Count > 0)
        {
            _deviceCombo.SelectedIndex = 0;
        }

        _deviceCombo.SelectedIndexChanged += (_, _) => ConnectSelectedDevice();

        foreach (var (bank, button) in _originalConfig.BankSelectors)
        {
            _bankGrid.Rows.Add(bank, button);
        }

        foreach (var binding in _originalConfig.Bindings)
        {
            var rowIndex = _bindingGrid.Rows.Add(
                binding.Name,
                binding.Bank,
                binding.Button,
                binding.Trigger,
                binding.Action,
                binding.WheelNotches);
            UpdateWheelNotchesCell(_bindingGrid.Rows[rowIndex]);
        }

        _dryRunCheckBox.Checked = _originalConfig.Safety.DryRun;
        _simulatorProcessesTextBox.Text = string.Join(", ", _originalConfig.Safety.SimulatorProcessNames);
        _openTargetCombo.SelectedItem = _originalConfig.OpenWorkingDirectory.Target;
        if (_openTargetCombo.SelectedIndex < 0)
        {
            _openTargetCombo.SelectedItem = OpenWorkingDirectoryOptions.VisualStudioCodeTarget;
        }
    }

    private void ConnectSelectedDevice()
    {
        CancelCapture();
        _source.Disconnect();
        _detector.Reset();
        _inputLabel.Text = "Held buttons: none";

        if (_deviceCombo.SelectedItem is not DirectInputDeviceInfo selected)
        {
            _connectionLabel.Text = "No DirectInput game controller is available.";
            return;
        }

        var selector = new DeviceSelector
        {
            ProductNameContains = selected.ProductName,
            InstanceGuid = selected.InstanceGuid.ToString(),
            ProductGuid = selected.ProductGuid.ToString(),
        };
        if (_source.TryConnect(selector, out var message))
        {
            _connectionLabel.Text = message;
            _captureReadyAt = DateTimeOffset.UtcNow.AddMilliseconds(_originalConfig.Polling.ConnectWarmupMs);
        }
        else
        {
            _connectionLabel.Text = message;
            _nextReconnectAt = DateTimeOffset.UtcNow.AddMilliseconds(_originalConfig.Polling.ReconnectIntervalMs);
        }
    }

    private void OnPoll(object? sender, EventArgs eventArgs)
    {
        if (_source.ConnectedDevice is null)
        {
            if (DateTimeOffset.UtcNow >= _nextReconnectAt)
            {
                ConnectSelectedDevice();
            }

            return;
        }

        if (!_source.TryRead(out var snapshot, out var error) || snapshot is null)
        {
            _connectionLabel.Text = $"Throttle disconnected: {error ?? "unknown DirectInput error"}";
            _nextReconnectAt = DateTimeOffset.UtcNow.AddMilliseconds(_originalConfig.Polling.ReconnectIntervalMs);
            return;
        }

        _lastSnapshot = snapshot;

        var heldButtons = snapshot.Buttons
            .Select((pressed, index) => (pressed, button: index + 1))
            .Where(item => item.pressed)
            .Select(item => item.button)
            .Take(16)
            .ToArray();
        _inputLabel.Text = heldButtons.Length == 0
            ? "Held buttons: none"
            : $"Held buttons: {string.Join(", ", heldButtons)}";

        if (snapshot.Timestamp < _captureReadyAt)
        {
            _detector.Reset();
            return;
        }

        var bufferedEvents = _source.LatestBufferedButtonEvents;
        var events = bufferedEvents
            .Concat(_detector.Detect(snapshot).Where(detected => !bufferedEvents.Any(buffered => buffered == detected)))
            .ToArray();
        foreach (var inputEvent in events)
        {
            if (inputEvent.Kind != JoystickEventKind.ButtonPressed)
            {
                continue;
            }

            if (_captureTarget is { } target
                && target.RowIndex >= 0
                && target.RowIndex < target.Grid.Rows.Count)
            {
                target.Grid.Rows[target.RowIndex].Cells[target.ColumnName].Value = inputEvent.DisplayIndex;
                target.Grid.CurrentCell = target.Grid.Rows[target.RowIndex].Cells[target.ColumnName];
                _captureLabel.Text = $"Captured button {inputEvent.DisplayIndex}.";
                _captureTarget = null;
                UpdateCaptureButtons();
            }
            else
            {
                _captureLabel.Text = $"Last pressed: button {inputEvent.DisplayIndex}.";
            }
        }
    }

    private void BeginCapture(DataGridView grid, string columnName, string instruction)
    {
        if (grid.CurrentRow is null)
        {
            MessageBox.Show(this, "Select a row first.", "Capture control", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _captureTarget = new CaptureTarget(grid, grid.CurrentRow.Index, columnName);
        _captureLabel.Text = instruction;
        UpdateCaptureButtons();
    }

    private void CancelCapture()
    {
        if (_captureTarget is not null)
        {
            _captureTarget = null;
            _captureLabel.Text = "Capture cancelled.";
            UpdateCaptureButtons();
        }
    }

    private void UpdateCaptureButtons()
    {
        _captureBankButton.Enabled = _captureTarget is null;
        _captureBindingButton.Enabled = _captureTarget is null;
        _cancelCaptureButton.Visible = _captureTarget is not null;
    }

    private void RemoveCurrentRow(DataGridView grid)
    {
        CancelCapture();
        if (grid.CurrentRow is not null)
        {
            grid.Rows.Remove(grid.CurrentRow);
        }
    }

    private void OnSave(object? sender, EventArgs eventArgs)
    {
        _bankGrid.EndEdit();
        _bindingGrid.EndEdit();

        var bankSelectors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var bindings = new List<ButtonBinding>();
        var parseErrors = new List<string>();

        foreach (DataGridViewRow row in _bankGrid.Rows)
        {
            var name = Convert.ToString(row.Cells["BankName"].Value)?.Trim() ?? string.Empty;
            if (!int.TryParse(Convert.ToString(row.Cells["SelectorButton"].Value), out var button))
            {
                parseErrors.Add($"Bank '{name}' needs a captured selector button.");
                button = 0;
            }

            if (!bankSelectors.TryAdd(name, button))
            {
                parseErrors.Add($"Bank name '{name}' is duplicated.");
            }
        }

        foreach (DataGridViewRow row in _bindingGrid.Rows)
        {
            var name = Convert.ToString(row.Cells["BindingName"].Value)?.Trim() ?? string.Empty;
            var bank = Convert.ToString(row.Cells["BindingBank"].Value)?.Trim() ?? string.Empty;
            var trigger = Convert.ToString(row.Cells["BindingTrigger"].Value)?.Trim() ?? string.Empty;
            var action = Convert.ToString(row.Cells["BindingAction"].Value)?.Trim() ?? string.Empty;
            var wheelNotches = ButtonBinding.DefaultWheelNotches;
            if (!int.TryParse(Convert.ToString(row.Cells["BindingButton"].Value), out var button))
            {
                parseErrors.Add($"Binding '{name}' needs a captured action button.");
                button = 0;
            }

            if (IsScrollAction(row)
                && (!int.TryParse(Convert.ToString(row.Cells["BindingWheelNotches"].Value), out wheelNotches)
                    || wheelNotches < ButtonBinding.DefaultWheelNotches
                    || wheelNotches > ButtonBinding.MaximumWheelNotches))
            {
                parseErrors.Add(
                    $"Binding '{name}' needs wheel notches between "
                    + $"{ButtonBinding.DefaultWheelNotches} and {ButtonBinding.MaximumWheelNotches}.");
                wheelNotches = ButtonBinding.DefaultWheelNotches;
            }

            bindings.Add(new ButtonBinding
            {
                Name = name,
                Bank = bank,
                Button = button,
                Trigger = trigger,
                Action = action,
                WheelNotches = wheelNotches,
            });
        }

        var device = _deviceCombo.SelectedItem is DirectInputDeviceInfo selected
            ? new DeviceSelector
            {
                ProductNameContains = selected.ProductName,
                InstanceGuid = selected.InstanceGuid.ToString(),
                ProductGuid = selected.ProductGuid.ToString(),
            }
            : _originalConfig.Device;
        var simulatorProcesses = _simulatorProcessesTextBox.Text
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(process => !string.IsNullOrWhiteSpace(process))
            .Select(process => process!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var config = new CompanionConfig
        {
            Device = device,
            Polling = _originalConfig.Polling,
            Safety = new SafetyOptions
            {
                DryRun = _dryRunCheckBox.Checked,
                RequireCodexForeground = true,
                CodexProcessNames = _originalConfig.Safety.CodexProcessNames,
                SimulatorProcessNames = simulatorProcesses,
            },
            OpenWorkingDirectory = new OpenWorkingDirectoryOptions
            {
                Target = Convert.ToString(_openTargetCombo.SelectedItem)
                    ?? OpenWorkingDirectoryOptions.VisualStudioCodeTarget,
            },
            BankSelectors = bankSelectors,
            Bindings = bindings,
        };

        var errors = parseErrors.Concat(ConfigValidator.Validate(config)).Distinct().ToArray();
        if (errors.Length > 0)
        {
            MessageBox.Show(
                this,
                string.Join(Environment.NewLine, errors.Select(error => $"- {error}")),
                "Please finish the configuration",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        if (_originalConfig.Safety.DryRun
            && !_dryRunCheckBox.Checked
            && MessageBox.Show(
                this,
                "Live mode will send the configured Codex shortcuts when the foreground safety checks pass. Enable live mode?",
                "Enable live actions",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        try
        {
            ConfigStore.Save(_configPath, config);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Could not save configuration", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private sealed record CaptureTarget(DataGridView Grid, int RowIndex, string ColumnName);
}
