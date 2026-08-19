using Joydex.Core.TaskAlerts;

namespace Joydex.App;

internal sealed class TaskAlertLedSettingsForm : ThemedForm
{
    private readonly ComboBox _mode = new()
    {
        AccessibleName = "Task alert LED output selection",
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 220,
    };
    private readonly Dictionary<TaskAlertState, Button> _taskColors = [];
    private readonly DataGridView _banks;
    private readonly CheckBox _alphaFirmware = new()
    {
        AutoSize = true,
        Text = "Use the controller profile color when Alpha is idle",
    };
    private readonly Button _alphaColor = new() { AutoSize = true, Text = "#000000" };

    public TaskAlertLedSettingsForm(TaskAlertLedOptions initial)
    {
        initial = initial.Normalize();
        Text = "Task-alert LED output";
        StartPosition = FormStartPosition.CenterParent;
        ShowIcon = false;
        MinimumSize = new Size(760, 560);
        Size = new Size(860, 640);

        _mode.Items.AddRange([
            new OutputChoice(TaskAlertLedOutputMode.LinkTool, "VIRPIL LinkTool"),
            new OutputChoice(TaskAlertLedOutputMode.DirectHid, "Direct USB"),
        ]);
        _mode.SelectedItem = _mode.Items.Cast<OutputChoice>().Single(choice => choice.Mode == initial.Mode);

        _banks = new DataGridView
        {
            AccessibleName = "Throttle bank LED colors",
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            Dock = DockStyle.Fill,
            ReadOnly = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
        };
        _banks.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Bank",
            HeaderText = "Bank",
            FillWeight = 45,
            MinimumWidth = 55,
        });
        for (var button = 1; button <= 6; button++)
        {
            _banks.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = $"B{button}",
                HeaderText = $"B{button}",
                FillWeight = 70,
                MinimumWidth = 82,
            });
        }

        for (var bank = 1; bank <= 5; bank++)
        {
            var values = initial.BankColors(bank).Select(color => color.ToHex()).Cast<object>().ToArray();
            var row = _banks.Rows[_banks.Rows.Add(new object[] { $"M{bank}" }.Concat(values).ToArray())];
            for (var column = 1; column <= 6; column++)
            {
                ApplyColorCell(row.Cells[column], Convert.ToString(row.Cells[column].Value)!);
            }
        }
        _banks.CellDoubleClick += (_, eventArgs) => EditBankColor(eventArgs.RowIndex, eventArgs.ColumnIndex);
        _banks.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.KeyCode == Keys.Enter && _banks.CurrentCell is { } cell)
            {
                EditBankColor(cell.RowIndex, cell.ColumnIndex);
                eventArgs.Handled = true;
            }
        };

        var palette = initial.TaskColors!;
        foreach (var (state, color) in new[]
        {
            (TaskAlertState.Running, palette.Running),
            (TaskAlertState.Approval, palette.Approval),
            (TaskAlertState.Completed, palette.Completed),
            (TaskAlertState.Fault, palette.Fault),
        })
        {
            var button = new Button { AutoSize = true, MinimumSize = new Size(105, 32) };
            SetButtonColor(button, color);
            button.Click += (_, _) => PickButtonColor(button);
            _taskColors[state] = button;
        }

        _alphaFirmware.Checked = initial.AlphaIdleColor() is null;
        SetButtonColor(_alphaColor, initial.AlphaIdleColor()?.ToHex() ?? "#000000");
        _alphaColor.Enabled = !_alphaFirmware.Checked;
        _alphaFirmware.CheckedChanged += (_, _) => _alphaColor.Enabled = !_alphaFirmware.Checked;
        _alphaColor.Click += (_, _) => PickButtonColor(_alphaColor);

        var root = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            RowCount = 5,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var modePanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        modePanel.Controls.Add(new Label
        {
            AutoSize = true,
            Margin = new Padding(0, 8, 12, 0),
            Text = "LED output",
        });
        modePanel.Controls.Add(_mode);
        modePanel.Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(12, 8, 0, 0),
            Text = "Direct USB requires LinkTool and VPC utilities to be closed.",
        });
        root.Controls.Add(modePanel, 0, 0);

        var palettePanel = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 8,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 14, 0, 12),
        };
        var paletteColumn = 0;
        foreach (var state in Enum.GetValues<TaskAlertState>())
        {
            palettePanel.Controls.Add(new Label
            {
                AutoSize = true,
                Margin = new Padding(paletteColumn == 0 ? 0 : 14, 8, 6, 0),
                Text = state.ToString(),
            }, paletteColumn++, 0);
            palettePanel.Controls.Add(_taskColors[state], paletteColumn++, 0);
        }
        root.Controls.Add(palettePanel, 0, 1);

        var banksGroup = new GroupBox
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            Text = "Throttle baseline colors — double-click a cell to change it",
        };
        banksGroup.Controls.Add(_banks);
        root.Controls.Add(banksGroup, 0, 2);

        var alphaPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 12, 0, 0),
            WrapContents = false,
        };
        alphaPanel.Controls.Add(new Label { AutoSize = true, Margin = new Padding(0, 8, 12, 0), Text = "Alpha idle" });
        alphaPanel.Controls.Add(_alphaFirmware);
        alphaPanel.Controls.Add(_alphaColor);
        root.Controls.Add(alphaPanel, 0, 3);

        var commands = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = new Padding(0, 14, 0, 0),
            WrapContents = false,
        };
        var save = new RoundedButton { Text = "Apply", Variant = ButtonVariant.Primary };
        save.Click += OnSave;
        commands.Controls.Add(save);
        commands.Controls.Add(new RoundedButton { DialogResult = DialogResult.Cancel, Text = "Cancel" });
        var defaults = new RoundedButton { Text = "Restore Joydex defaults" };
        defaults.Click += (_, _) => LoadOptions(TaskAlertLedOptions.CreateDefault() with
        {
            Mode = ((OutputChoice)_mode.SelectedItem!).Mode,
        });
        commands.Controls.Add(defaults);
        root.Controls.Add(commands, 0, 4);
        Controls.Add(root);
        ThemeService.Apply(this);
        LoadOptions(initial);
    }

    public TaskAlertLedOptions Options { get; private set; } = TaskAlertLedOptions.CreateDefault();

    private void OnSave(object? sender, EventArgs eventArgs)
    {
        var banks = new string[5][];
        for (var bank = 0; bank < 5; bank++)
        {
            banks[bank] = Enumerable.Range(1, 6)
                .Select(column => Convert.ToString(_banks.Rows[bank].Cells[column].Value)!)
                .ToArray();
        }

        Options = new TaskAlertLedOptions(
            Mode: ((OutputChoice)_mode.SelectedItem!).Mode,
            TaskColors: new TaskAlertLedPalette(
                _taskColors[TaskAlertState.Running].Text,
                _taskColors[TaskAlertState.Approval].Text,
                _taskColors[TaskAlertState.Completed].Text,
                _taskColors[TaskAlertState.Fault].Text),
            ThrottleBanks: new TaskAlertThrottleBanks(banks[0], banks[1], banks[2], banks[3], banks[4]),
            AlphaIdle: _alphaFirmware.Checked ? TaskAlertLedOptions.FirmwareIdle : _alphaColor.Text).Normalize();
        DialogResult = DialogResult.OK;
        Close();
    }

    private void LoadOptions(TaskAlertLedOptions options)
    {
        options = options.Normalize();
        var palette = options.TaskColors!;
        SetButtonColor(_taskColors[TaskAlertState.Running], palette.Running);
        SetButtonColor(_taskColors[TaskAlertState.Approval], palette.Approval);
        SetButtonColor(_taskColors[TaskAlertState.Completed], palette.Completed);
        SetButtonColor(_taskColors[TaskAlertState.Fault], palette.Fault);
        for (var bank = 1; bank <= 5; bank++)
        {
            var colors = options.BankColors(bank);
            for (var button = 1; button <= 6; button++)
            {
                ApplyColorCell(_banks.Rows[bank - 1].Cells[button], colors[button - 1].ToHex());
            }
        }

        _alphaFirmware.Checked = options.AlphaIdleColor() is null;
        SetButtonColor(_alphaColor, options.AlphaIdleColor()?.ToHex() ?? "#000000");
    }

    private void EditBankColor(int row, int column)
    {
        if (row < 0 || column < 1)
        {
            return;
        }

        var cell = _banks.Rows[row].Cells[column];
        if (PickColor(Convert.ToString(cell.Value)!, out var color))
        {
            ApplyColorCell(cell, color);
        }
    }

    private static void PickButtonColor(Button button)
    {
        if (PickColor(button.Text, out var color))
        {
            SetButtonColor(button, color);
        }
    }

    private static bool PickColor(string current, out string color)
    {
        var rgb = TaskAlertLedOptions.ParseColor(current);
        using var picker = new ColorDialog
        {
            Color = Color.FromArgb(rgb.Red, rgb.Green, rgb.Blue),
            FullOpen = true,
        };
        if (picker.ShowDialog() != DialogResult.OK)
        {
            color = current;
            return false;
        }

        color = $"#{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}";
        return true;
    }

    private static void SetButtonColor(Button button, string color)
    {
        var rgb = TaskAlertLedOptions.ParseColor(color);
        button.Text = color.ToUpperInvariant();
        button.BackColor = Color.FromArgb(rgb.Red, rgb.Green, rgb.Blue);
        button.ForeColor = PerceivedBrightness(rgb) < 128 ? Color.White : Color.Black;
    }

    private static void ApplyColorCell(DataGridViewCell cell, string color)
    {
        var rgb = TaskAlertLedOptions.ParseColor(color);
        cell.Value = color.ToUpperInvariant();
        cell.Style.BackColor = Color.FromArgb(rgb.Red, rgb.Green, rgb.Blue);
        cell.Style.ForeColor = PerceivedBrightness(rgb) < 128 ? Color.White : Color.Black;
        cell.Style.SelectionBackColor = cell.Style.BackColor;
        cell.Style.SelectionForeColor = cell.Style.ForeColor;
    }

    private static int PerceivedBrightness(TaskAlertRgbColor color) =>
        ((color.Red * 299) + (color.Green * 587) + (color.Blue * 114)) / 1000;

    private sealed record OutputChoice(TaskAlertLedOutputMode Mode, string Name)
    {
        public override string ToString() => Name;
    }
}
