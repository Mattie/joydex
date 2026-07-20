using Joydex.Core.Config;
using Joydex.Core.Input;
using Joydex.Windows.Input;

namespace Joydex.App;

internal sealed class PromptPickerEditorForm : Form
{
    private readonly string _configPath;
    private readonly IntPtr _cooperativeWindowHandle;
    private readonly CompanionConfig _original;
    private readonly List<MutableDevice> _devices;
    private readonly List<MutablePicker> _pickers;
    private readonly ComboBox _pickerCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly TextBox _pickerName = new() { Width = 260 };
    private readonly ListBox _promptList = new() { Dock = DockStyle.Fill };
    private readonly TextBox _promptText = new() { AcceptsReturn = true, Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly CheckBox _submitAfterInsertCheckBox = new() { AutoSize = true, Text = "After this prompt: run Codex Submit" };
    private readonly CheckBox _includeExitOptionCheckBox = new() { AutoSize = true, Text = "Add [Exit / Nevermind] as the last item" };
    private readonly Label _defaultLabel = new() { AutoSize = true };
    private readonly Dictionary<string, ComboBox> _controlDevices = [];
    private readonly Dictionary<string, ComboBox> _controlBanks = [];
    private readonly Dictionary<string, NumericUpDown> _controlButtons = [];
    private readonly DataGridView _deviceGrid = new();
    private readonly DirectInputJoystickSource _captureSource;
    private readonly System.Windows.Forms.Timer _captureTimer = new() { Interval = 16 };
    private Action<int>? _captureTarget;
    private DateTimeOffset _captureReadyAt;
    private int _currentPickerIndex = -1;
    private int _currentPromptIndex = -1;
    private bool _refreshingPromptSelection;
    internal Control EmbeddedPickerPage { get; }

    public PromptPickerEditorForm(string configPath, IntPtr cooperativeWindowHandle, bool pickerOnly = false)
    {
        _configPath = configPath;
        _cooperativeWindowHandle = cooperativeWindowHandle;
        _original = CompanionConfigNormalizer.Normalize(ConfigStore.LoadOrCreate(configPath));
        _captureSource = new DirectInputJoystickSource(cooperativeWindowHandle);
        _devices = BuildDevices(_original, _captureSource.EnumerateDevices());
        _pickers = _original.PromptPickers.Select(MutablePicker.FromConfig).ToList();

        Text = "Joydex Prompt Pickers and Device Maps";
        Font = new Font("Segoe UI", 9F);
        StartPosition = FormStartPosition.CenterScreen;
        var pickerPage = BuildPickerPage();
        EmbeddedPickerPage = pickerPage;
        if (pickerOnly)
        {
            ShowInTaskbar = false;
        }
        else
        {
            MinimumSize = new Size(980, 720);
            Size = new Size(1120, 820);

            var tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(new TabPage("Prompt pickers") { Controls = { pickerPage } });
            tabs.TabPages.Add(new TabPage("Device maps") { Controls = { BuildDevicePage() } });
            var footer = BuildFooter();
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(tabs, 0, 0);
            root.Controls.Add(footer, 0, 1);
            Controls.Add(root);
        }

        _pickerCombo.SelectedIndexChanged += (_, _) => SelectPicker(_pickerCombo.SelectedIndex);
        _pickerName.TextChanged += (_, _) =>
        {
            if (_currentPickerIndex >= 0 && _currentPickerIndex < _pickers.Count)
            {
                _pickers[_currentPickerIndex].Name = _pickerName.Text.Trim();
                _pickerCombo.Refresh();
            }
        };
        _promptList.SelectedIndexChanged += (_, _) =>
        {
            if (_refreshingPromptSelection)
            {
                return;
            }

            CommitPromptText();
            _currentPromptIndex = _promptList.SelectedIndex;
            _refreshingPromptSelection = true;
            _promptText.Text = _currentPromptIndex >= 0
                ? CurrentPicker().Prompts[_currentPromptIndex]
                : string.Empty;
            _submitAfterInsertCheckBox.Checked = _currentPromptIndex >= 0
                && CurrentPicker().SubmitAfterInsert[_currentPromptIndex];
            _refreshingPromptSelection = false;
            UpdateDefaultLabel();
        };
        _submitAfterInsertCheckBox.CheckedChanged += (_, _) =>
        {
            if (!_refreshingPromptSelection
                && _currentPickerIndex >= 0
                && _currentPromptIndex >= 0)
            {
                CommitPromptText();
                CurrentPicker().SubmitAfterInsert[_currentPromptIndex] = _submitAfterInsertCheckBox.Checked;
                RefreshPromptList(_currentPromptIndex);
            }
        };
        _captureTimer.Tick += OnCaptureTick;
        FormClosed += (_, _) =>
        {
            _captureTimer.Dispose();
            _captureSource.Dispose();
        };

        RefreshPickerCombo();
        if (_pickers.Count > 0)
        {
            _pickerCombo.SelectedIndex = 0;
        }

        if (!pickerOnly)
        {
            PopulateDeviceGrid();
        }
    }

    internal IReadOnlyList<PromptPickerConfig> GetPromptPickers()
    {
        CommitPromptText();
        CommitPicker();
        return _pickers.Select(picker => picker.ToConfig()).ToList();
    }

    private Control BuildPickerPage()
    {
        var page = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 2,
            RowCount = 4,
        };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 240));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var pickerBar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        pickerBar.Controls.AddRange([
            new Label { AutoSize = true, Text = "Picker", Margin = new Padding(0, 8, 4, 0) },
            _pickerCombo,
            Button("Add", (_, _) => AddPicker()),
            Button("Remove", (_, _) => RemovePicker()),
            Button("Move up", (_, _) => MovePicker(-1)),
            Button("Move down", (_, _) => MovePicker(1)),
        ]);
        page.Controls.Add(pickerBar, 0, 0);
        page.SetColumnSpan(pickerBar, 2);

        var settings = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5, Padding = new Padding(0, 8, 0, 8) };
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        settings.Controls.Add(new Label { AutoSize = true, Text = "Name", Anchor = AnchorStyles.Left }, 0, 0);
        settings.Controls.Add(_pickerName, 1, 0);
        AddControlRow(settings, 1, "Up", "up");
        AddControlRow(settings, 2, "Down", "down");
        AddControlRow(settings, 3, "Insert", "insert");
        settings.Controls.Add(new Label { AutoSize = true, Text = "Cancel option", Anchor = AnchorStyles.Left }, 0, 4);
        settings.Controls.Add(_includeExitOptionCheckBox, 1, 4);
        page.Controls.Add(settings, 0, 1);
        page.SetColumnSpan(settings, 2);

        var listPanel = new Panel { Dock = DockStyle.Fill };
        listPanel.Controls.Add(_promptList);
        var listButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom };
        listButtons.Controls.AddRange([
            Button("Add", (_, _) => AddPrompt()),
            Button("Delete", (_, _) => DeletePrompt()),
            Button("Up", (_, _) => MovePrompt(-1)),
            Button("Down", (_, _) => MovePrompt(1)),
            Button("Set default", (_, _) => SetDefault()),
        ]);
        listPanel.Controls.Add(listButtons);
        page.Controls.Add(listPanel, 0, 2);

        var editPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 0, 0) };
        editPanel.Controls.Add(_promptText);
        var update = Button("Update selected prompt", (_, _) => UpdatePrompt());
        var promptActions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom, WrapContents = false };
        promptActions.Controls.Add(_submitAfterInsertCheckBox);
        promptActions.Controls.Add(update);
        editPanel.Controls.Add(promptActions);
        page.Controls.Add(editPanel, 1, 2);
        page.Controls.Add(_defaultLabel, 0, 3);
        page.SetColumnSpan(_defaultLabel, 2);
        return page;
    }

    private void AddControlRow(TableLayoutPanel panel, int row, string label, string role)
    {
        var device = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 270 };
        foreach (var candidate in _devices)
        {
            device.Items.Add(candidate);
        }

        var bank = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
        var button = new NumericUpDown { Minimum = 1, Maximum = 128, Width = 70 };
        var bar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        bar.Controls.AddRange([
            device,
            new Label { AutoSize = true, Text = "Bank", Margin = new Padding(10, 8, 4, 0) },
            bank,
            new Label { AutoSize = true, Text = "Button", Margin = new Padding(10, 8, 4, 0) },
            button,
            Button("Capture", (_, _) => BeginCapture(device, value => button.Value = value)),
        ]);
        panel.Controls.Add(new Label { AutoSize = true, Text = label, Anchor = AnchorStyles.Left }, 0, row);
        panel.Controls.Add(bar, 1, row);
        _controlDevices[role] = device;
        _controlBanks[role] = bank;
        _controlButtons[role] = button;
        device.SelectedIndexChanged += (_, _) => RefreshControlBanks(role, CompanionConfig.AlwaysBank);
    }

    private Control BuildDevicePage()
    {
        _deviceGrid.Dock = DockStyle.Fill;
        _deviceGrid.AllowUserToAddRows = false;
        _deviceGrid.AllowUserToDeleteRows = false;
        _deviceGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _deviceGrid.RowHeadersVisible = false;
        _deviceGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _deviceGrid.Columns.Add("Id", "ID");
        _deviceGrid.Columns.Add("Name", "Device");
        var template = new DataGridViewComboBoxColumn { Name = "Template", HeaderText = "Button map" };
        template.Items.AddRange("", "cm3", "alpha-warbrd");
        _deviceGrid.Columns.Add(template);
        _deviceGrid.Columns.Add(new DataGridViewComboBoxColumn
        {
            Name = "MapHoldDevice",
            HeaderText = "Hold source",
            DataSource = _devices.Select(device => device.Id).ToArray(),
        });
        _deviceGrid.Columns.Add("MapHold", "Hold-to-show button");

        var page = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, Padding = new Padding(12) };
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "Each map can use a hold control from any configured controller.",
        }, 0, 0);
        page.Controls.Add(_deviceGrid, 0, 1);
        var controls = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        controls.Controls.Add(Button("Capture map hold", (_, _) => CaptureMapHold()));
        controls.Controls.Add(Button("Clear map hold", (_, _) =>
        {
            if (_deviceGrid.CurrentRow is not null)
            {
                _deviceGrid.CurrentRow.Cells["MapHold"].Value = null;
            }
        }));
        page.Controls.Add(controls, 0, 2);
        return page;
    }

    private Control BuildFooter()
    {
        var footer = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8),
        };
        footer.Controls.Add(Button("Save", OnSave));
        footer.Controls.Add(Button("Cancel", (_, _) => { DialogResult = DialogResult.Cancel; Close(); }));
        return footer;
    }

    private void SelectPicker(int index)
    {
        CommitPromptText();
        CommitPicker();
        _currentPickerIndex = index;
        _currentPromptIndex = -1;
        if (index < 0 || index >= _pickers.Count)
        {
            return;
        }

        var picker = _pickers[index];
        _pickerName.Text = picker.Name;
        _includeExitOptionCheckBox.Checked = picker.IncludeExitOption;
        SetControl("up", picker.Up);
        SetControl("down", picker.Down);
        SetControl("insert", picker.Insert);
        RefreshPromptList(picker.DefaultIndex);
    }

    private void CommitPicker()
    {
        if (_currentPickerIndex < 0 || _currentPickerIndex >= _pickers.Count)
        {
            return;
        }

        var picker = _pickers[_currentPickerIndex];
        picker.Name = _pickerName.Text.Trim();
        picker.IncludeExitOption = _includeExitOptionCheckBox.Checked;
        picker.Up = ReadControl("up");
        picker.Down = ReadControl("down");
        picker.Insert = ReadControl("insert");
    }

    private void AddPicker()
    {
        if (_pickers.Count >= 3)
        {
            MessageBox.Show(DialogOwner, "Joydex supports up to three prompt pickers.", Text);
            return;
        }

        CommitPromptText();
        CommitPicker();
        var primary = _devices[0].Id;
        var index = Enumerable.Range(1, 3).First(candidate => !_pickers.Any(picker =>
            string.Equals(picker.Id, $"picker-{candidate}", StringComparison.OrdinalIgnoreCase)));
        _pickers.Add(new MutablePicker
        {
            Id = $"picker-{index}",
            Name = $"Prompt picker {index}",
            Prompts = ["New prompt"],
            SubmitAfterInsert = [false],
            Up = new MutableControl(primary, CompanionConfig.AlwaysBank, 1),
            Down = new MutableControl(primary, CompanionConfig.AlwaysBank, 2),
            Insert = new MutableControl(primary, CompanionConfig.AlwaysBank, 3),
        });
        RefreshPickerCombo();
        _pickerCombo.SelectedIndex = _pickers.Count - 1;
    }

    private void RemovePicker()
    {
        if (_pickers.Count <= 1 || _pickerCombo.SelectedIndex < 0)
        {
            MessageBox.Show(DialogOwner, "At least one prompt picker is required.", Text);
            return;
        }

        CommitPromptText();
        _pickers.RemoveAt(_pickerCombo.SelectedIndex);
        _currentPickerIndex = -1;
        _currentPromptIndex = -1;
        RefreshPickerCombo();
        _pickerCombo.SelectedIndex = 0;
    }

    private void MovePicker(int delta)
    {
        var from = _pickerCombo.SelectedIndex;
        var to = from + delta;
        if (from < 0 || to < 0 || to >= _pickers.Count)
        {
            return;
        }

        CommitPromptText();
        CommitPicker();
        (_pickers[from], _pickers[to]) = (_pickers[to], _pickers[from]);
        _currentPickerIndex = -1;
        _currentPromptIndex = -1;
        RefreshPickerCombo();
        _pickerCombo.SelectedIndex = to;
    }

    private void AddPrompt()
    {
        var picker = CurrentPicker();
        var text = string.IsNullOrWhiteSpace(_promptText.Text) ? "New prompt" : _promptText.Text;
        picker.Prompts.Add(text);
        picker.SubmitAfterInsert.Add(false);
        RefreshPromptList(picker.Prompts.Count - 1);
    }

    private void UpdatePrompt()
    {
        CommitPromptText();
        RefreshPromptList(_currentPromptIndex);
    }

    private void DeletePrompt()
    {
        CommitPromptText();
        var picker = CurrentPicker();
        var index = _promptList.SelectedIndex;
        if (index < 0 || picker.Prompts.Count <= 1)
        {
            return;
        }

        picker.Prompts.RemoveAt(index);
        picker.SubmitAfterInsert.RemoveAt(index);
        if (picker.DefaultIndex == index)
        {
            picker.DefaultIndex = Math.Min(index, picker.Prompts.Count - 1);
        }
        else if (picker.DefaultIndex > index)
        {
            picker.DefaultIndex--;
        }
        RefreshPromptList(Math.Min(index, picker.Prompts.Count - 1));
    }

    private void MovePrompt(int delta)
    {
        CommitPromptText();
        var picker = CurrentPicker();
        var from = _promptList.SelectedIndex;
        var to = from + delta;
        if (from < 0 || to < 0 || to >= picker.Prompts.Count)
        {
            return;
        }

        (picker.Prompts[from], picker.Prompts[to]) = (picker.Prompts[to], picker.Prompts[from]);
        (picker.SubmitAfterInsert[from], picker.SubmitAfterInsert[to]) =
            (picker.SubmitAfterInsert[to], picker.SubmitAfterInsert[from]);
        if (picker.DefaultIndex == from) picker.DefaultIndex = to;
        else if (picker.DefaultIndex == to) picker.DefaultIndex = from;
        RefreshPromptList(to);
    }

    private void SetDefault()
    {
        CommitPromptText();
        if (_promptList.SelectedIndex >= 0)
        {
            CurrentPicker().DefaultIndex = _promptList.SelectedIndex;
            UpdateDefaultLabel();
        }
    }

    private void RefreshPromptList(int selected)
    {
        var picker = CurrentPicker();
        _refreshingPromptSelection = true;
        _promptList.Items.Clear();
        for (var index = 0; index < picker.Prompts.Count; index++)
        {
            var marker = index == picker.DefaultIndex ? "★ " : string.Empty;
            var submitMarker = picker.SubmitAfterInsert[index] ? " [+ Submit]" : string.Empty;
            _promptList.Items.Add(marker + picker.Prompts[index].Replace("\r\n", " ↵ ").Replace('\n', ' ') + submitMarker);
        }

        if (_promptList.Items.Count > 0)
        {
            _promptList.SelectedIndex = Math.Clamp(selected, 0, _promptList.Items.Count - 1);
            _currentPromptIndex = _promptList.SelectedIndex;
            _promptText.Text = picker.Prompts[_currentPromptIndex];
            _submitAfterInsertCheckBox.Checked = picker.SubmitAfterInsert[_currentPromptIndex];
        }
        else
        {
            _currentPromptIndex = -1;
            _promptText.Clear();
            _submitAfterInsertCheckBox.Checked = false;
        }
        _refreshingPromptSelection = false;
        UpdateDefaultLabel();
    }

    private void CommitPromptText()
    {
        if (_currentPickerIndex < 0
            || _currentPickerIndex >= _pickers.Count
            || _currentPromptIndex < 0
            || _currentPromptIndex >= _pickers[_currentPickerIndex].Prompts.Count
            || string.IsNullOrWhiteSpace(_promptText.Text))
        {
            return;
        }

        _pickers[_currentPickerIndex].Prompts[_currentPromptIndex] = _promptText.Text;
    }

    private void UpdateDefaultLabel()
    {
        if (_currentPickerIndex >= 0)
        {
            _defaultLabel.Text = $"Default entry: {CurrentPicker().DefaultIndex + 1} of {CurrentPicker().Prompts.Count}";
        }
    }

    private void RefreshPickerCombo()
    {
        var selected = _pickerCombo.SelectedIndex;
        _pickerCombo.Items.Clear();
        foreach (var picker in _pickers)
        {
            _pickerCombo.Items.Add(picker);
        }
        if (_pickerCombo.Items.Count > 0 && selected >= 0)
        {
            _pickerCombo.SelectedIndex = Math.Min(selected, _pickerCombo.Items.Count - 1);
        }
    }

    private void SetControl(string role, MutableControl control)
    {
        var combo = _controlDevices[role];
        combo.SelectedItem = _devices.FirstOrDefault(device =>
            string.Equals(device.Id, control.DeviceId, StringComparison.OrdinalIgnoreCase)) ?? _devices[0];
        RefreshControlBanks(role, control.Bank);
        _controlButtons[role].Value = Math.Clamp(control.Button, 1, 128);
    }

    private MutableControl ReadControl(string role) => new(
        ((MutableDevice)_controlDevices[role].SelectedItem!).Id,
        Convert.ToString(_controlBanks[role].SelectedItem) ?? CompanionConfig.AlwaysBank,
        (int)_controlButtons[role].Value);

    private void RefreshControlBanks(string role, string preferredBank)
    {
        var bank = _controlBanks[role];
        bank.Items.Clear();
        bank.Items.Add(CompanionConfig.AlwaysBank);
        if (_controlDevices[role].SelectedItem is MutableDevice device)
        {
            foreach (var name in device.BankSelectors.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                bank.Items.Add(name);
            }
        }

        bank.SelectedItem = bank.Items.Cast<string>().FirstOrDefault(name =>
            string.Equals(name, preferredBank, StringComparison.OrdinalIgnoreCase))
            ?? CompanionConfig.AlwaysBank;
    }

    private void BeginCapture(ComboBox deviceCombo, Action<int> target)
    {
        if (deviceCombo.SelectedItem is not MutableDevice device)
        {
            return;
        }

        _captureSource.Disconnect();
        if (!_captureSource.TryConnect(device.Selector, out var message))
        {
            MessageBox.Show(DialogOwner, message, "Capture control");
            return;
        }

        _captureSource.TryRead(out _, out _);
        _captureTarget = target;
        _captureReadyAt = DateTimeOffset.UtcNow.AddMilliseconds(_original.Polling.ConnectWarmupMs);
        _captureTimer.Start();
    }

    private void OnCaptureTick(object? sender, EventArgs eventArgs)
    {
        if (_captureTarget is null || DateTimeOffset.UtcNow < _captureReadyAt)
        {
            return;
        }

        if (!_captureSource.TryRead(out _, out _))
        {
            return;
        }

        var pressed = _captureSource.LatestBufferedButtonEvents.FirstOrDefault(input =>
            input.Kind == JoystickEventKind.ButtonPressed);
        if (pressed is null)
        {
            return;
        }

        _captureTarget(pressed.DisplayIndex);
        _captureTarget = null;
        _captureTimer.Stop();
        _captureSource.Disconnect();
    }

    private void PopulateDeviceGrid()
    {
        _deviceGrid.Rows.Clear();
        foreach (var device in _devices)
        {
            var index = _deviceGrid.Rows.Add(
                device.Id,
                device.DisplayName,
                device.Template ?? string.Empty,
                device.MapHold?.DeviceId ?? device.Id,
                device.MapHold?.Button);
            _deviceGrid.Rows[index].Tag = device;
        }
    }

    private void CaptureMapHold()
    {
        if (_deviceGrid.CurrentRow is not { } row)
        {
            return;
        }

        var sourceId = Convert.ToString(row.Cells["MapHoldDevice"].Value);
        var source = _devices.FirstOrDefault(device =>
            string.Equals(device.Id, sourceId, StringComparison.OrdinalIgnoreCase));
        if (source is not null)
        {
            BeginCaptureForDevice(source, value => row.Cells["MapHold"].Value = value);
        }
    }

    private void BeginCaptureForDevice(MutableDevice device, Action<int> target)
    {
        _captureSource.Disconnect();
        if (!_captureSource.TryConnect(device.Selector, out var message))
        {
            MessageBox.Show(DialogOwner, message, "Capture control");
            return;
        }

        _captureSource.TryRead(out _, out _);
        _captureTarget = target;
        _captureReadyAt = DateTimeOffset.UtcNow.AddMilliseconds(_original.Polling.ConnectWarmupMs);
        _captureTimer.Start();
    }

    private void OnSave(object? sender, EventArgs eventArgs)
    {
        CommitPromptText();
        CommitPicker();
        foreach (DataGridViewRow row in _deviceGrid.Rows)
        {
            if (row.Tag is not MutableDevice device) continue;
            device.Template = Convert.ToString(row.Cells["Template"].Value);
            var sourceId = Convert.ToString(row.Cells["MapHoldDevice"].Value) ?? string.Empty;
            device.MapHold = int.TryParse(Convert.ToString(row.Cells["MapHold"].Value), out var hold)
                ? new MutableControl(sourceId, CompanionConfig.AlwaysBank, hold)
                : null;
        }

        var config = new CompanionConfig
        {
            Device = _devices[0].Selector,
            Devices = _devices.Select(device => device.ToConfig()).ToList(),
            Polling = _original.Polling,
            Safety = _original.Safety,
            OpenWorkingDirectory = _original.OpenWorkingDirectory,
            BankSelectors = _devices[0].BankSelectors,
            Bindings = _original.Bindings,
            PromptPickers = _pickers.Select(picker => picker.ToConfig()).ToList(),
        };
        var errors = ConfigValidator.Validate(config);
        if (errors.Count > 0)
        {
            MessageBox.Show(DialogOwner, string.Join(Environment.NewLine, errors.Select(error => $"- {error}")), "Fix prompt picker settings");
            return;
        }

        ConfigStore.Save(_configPath, config);
        DialogResult = DialogResult.OK;
        Close();
    }

    private MutablePicker CurrentPicker() => _pickers[Math.Max(0, _currentPickerIndex)];

    private IWin32Window DialogOwner => EmbeddedPickerPage.FindForm() ?? this;

    private static Button Button(string text, EventHandler handler)
    {
        var button = new Button { AutoSize = true, Text = text };
        button.Click += handler;
        return button;
    }

    private static List<MutableDevice> BuildDevices(
        CompanionConfig config,
        IReadOnlyList<DirectInputDeviceInfo> attached)
    {
        var devices = config.Devices.Select(MutableDevice.FromConfig).ToList();
        foreach (var info in attached)
        {
            if (devices.Any(device => string.Equals(
                    device.Selector.InstanceGuid,
                    info.InstanceGuid.ToString(),
                    StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var selector = new DeviceSelector
            {
                ProductNameContains = info.ProductName,
                InstanceGuid = info.InstanceGuid.ToString(),
                ProductGuid = info.ProductGuid.ToString(),
            };
            var baseId = info.ProductName.Contains("WarBRD", StringComparison.OrdinalIgnoreCase)
                ? "alpha-warbrd"
                : $"device-{devices.Count + 1}";
            var id = baseId;
            for (var suffix = 2; devices.Any(device => string.Equals(device.Id, id, StringComparison.OrdinalIgnoreCase)); suffix++)
            {
                id = $"{baseId}-{suffix}";
            }

            devices.Add(new MutableDevice
            {
                Id = id,
                DisplayName = info.ProductName,
                Selector = selector,
                Template = CompanionConfigNormalizer.InferTemplate(selector),
            });
        }

        return devices;
    }

    private sealed class MutableDevice
    {
        public required string Id { get; init; }
        public required string DisplayName { get; init; }
        public required DeviceSelector Selector { get; init; }
        public Dictionary<string, int> BankSelectors { get; init; } = [];
        public string? Template { get; set; }
        public MutableControl? MapHold { get; set; }
        public override string ToString() => DisplayName;
        public DeviceProfile ToConfig() => new()
        {
            Id = Id,
            DisplayName = DisplayName,
            Selector = Selector,
            BankSelectors = BankSelectors,
            ButtonMapTemplate = string.IsNullOrWhiteSpace(Template) ? null : Template,
            ButtonMapHoldControl = MapHold is null
                ? null
                : new DeviceControlReference
                {
                    DeviceId = MapHold.DeviceId,
                    Bank = MapHold.Bank,
                    Button = MapHold.Button,
                },
        };
        public static MutableDevice FromConfig(DeviceProfile device) => new()
        {
            Id = device.Id,
            DisplayName = device.DisplayName,
            Selector = device.Selector,
            BankSelectors = new Dictionary<string, int>(device.BankSelectors, StringComparer.OrdinalIgnoreCase),
            Template = device.ButtonMapTemplate,
            MapHold = device.ButtonMapHoldControl is null
                ? null
                : new MutableControl(
                    device.ButtonMapHoldControl.DeviceId,
                    device.ButtonMapHoldControl.Bank,
                    device.ButtonMapHoldControl.Button),
        };
    }

    private sealed record MutableControl(string DeviceId, string Bank, int Button);

    private sealed class MutablePicker
    {
        public required string Id { get; init; }
        public required string Name { get; set; }
        public required List<string> Prompts { get; init; }
        public required List<bool> SubmitAfterInsert { get; init; }
        public bool IncludeExitOption { get; set; }
        public int DefaultIndex { get; set; }
        public required MutableControl Up { get; set; }
        public required MutableControl Down { get; set; }
        public required MutableControl Insert { get; set; }
        public override string ToString() => Name;
        public PromptPickerConfig ToConfig() => new()
        {
            Id = Id,
            Name = Name,
            Prompts = [.. Prompts],
            SubmitAfterInsert = [.. SubmitAfterInsert],
            IncludeExitOption = IncludeExitOption,
            DefaultPromptIndex = DefaultIndex,
            Controls = new PromptPickerControls
            {
                Up = new DeviceControlReference { DeviceId = Up.DeviceId, Bank = Up.Bank, Button = Up.Button },
                Down = new DeviceControlReference { DeviceId = Down.DeviceId, Bank = Down.Bank, Button = Down.Button },
                Insert = new DeviceControlReference { DeviceId = Insert.DeviceId, Bank = Insert.Bank, Button = Insert.Button },
            },
        };
        public static MutablePicker FromConfig(PromptPickerConfig picker) => new()
        {
            Id = picker.Id,
            Name = picker.Name,
            Prompts = [.. picker.Prompts],
            SubmitAfterInsert = [.. picker.SubmitAfterInsert],
            IncludeExitOption = picker.IncludeExitOption,
            DefaultIndex = picker.DefaultPromptIndex,
            Up = new MutableControl(picker.Controls.Up.DeviceId, picker.Controls.Up.Bank, picker.Controls.Up.Button),
            Down = new MutableControl(picker.Controls.Down.DeviceId, picker.Controls.Down.Bank, picker.Controls.Down.Button),
            Insert = new MutableControl(picker.Controls.Insert.DeviceId, picker.Controls.Insert.Bank, picker.Controls.Insert.Button),
        };
    }
}
