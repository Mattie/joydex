using Joydex.Core.Config;
using Joydex.Core.Input;
using Joydex.Core.Mapping;
using Joydex.Windows.Input;

namespace Joydex.App;

internal sealed class ConfigurationForm : ThemedForm
{
    private static readonly Size PreferredMinimumSize = new(760, 560);
    private readonly string _configPath;
    private readonly string _windowStatePath;
    private readonly IntPtr _cooperativeWindowHandle;
    private readonly CompanionConfig _originalConfig;
    private readonly DirectInputJoystickSource _source;
    private readonly InputEventDetector _detector;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly bool _documentationMode;
    private readonly ComboBox _deviceCombo = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _connectionLabel = new() { AutoSize = true, Text = "Looking for controller..." };
    private readonly Label _inputLabel = new() { AutoSize = true, Text = "Held buttons: none" };
    private readonly StatusLabel _captureLabel = new() { Tag = ThemeTone.Subtle, Text = "Select a row and choose Capture." };
    private readonly ModernDataGridView _bankGrid = CreateGrid();
    private readonly ModernDataGridView _bindingGrid = CreateGrid();
    private readonly ModernDataGridView _buttonMapGrid = CreateGrid();
    private readonly BorderedTextBox _bindingFilter = new();
    private readonly Label _bindingCountLabel = new() { AutoSize = true, Tag = ThemeTone.Faint };
    private readonly Dictionary<BindingCluster, RoundedButton> _bindingClusterButtons = [];
    private BindingCluster _bindingCluster = BindingCluster.All;
    private readonly CheckBox _dryRunCheckBox = new() { AutoSize = true, Text = "Dry run (log actions without sending them)" };
    private readonly TextBox _simulatorProcessesTextBox = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _openTargetCombo = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly RoundedButton _captureBankButton = new() { Text = "Capture selector" };
    private readonly RoundedButton _captureBindingButton = new() { Text = "Capture action button" };
    private readonly RoundedButton _captureMapHoldButton = new() { Text = "Capture hold-to-show" };
    private readonly RoundedButton _cancelCaptureButton = new() { Text = "Cancel capture", Visible = false };
    private readonly RoundedButton _loadDefaultsButton = new() { Text = "Load Codex Micro defaults" };
    private readonly Panel _pageHost = new() { Dock = DockStyle.Fill };
    private readonly List<NavigationPage> _navigationPages = [];
    private CaptureTarget? _captureTarget;
    private JoystickSnapshot? _lastSnapshot;
    private DateTimeOffset _captureReadyAt;
    private DateTimeOffset _nextReconnectAt;
    private string? _loadWarning;
    private DirectInputDeviceInfo? _captureReturnDevice;
    private PromptPickerEditorForm? _promptPickerEditor;

    public ConfigurationForm(
        string configPath,
        string windowStatePath,
        IntPtr cooperativeWindowHandle,
        bool documentationMode = false)
    {
        _configPath = configPath;
        _windowStatePath = windowStatePath;
        _cooperativeWindowHandle = cooperativeWindowHandle;
        _documentationMode = documentationMode;
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

        Text = "Configure Joydex";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = PreferredMinimumSize;
        Size = ClampToCurrentScreen(new Size(1500, 1000));
        ShowIcon = false;

        RestoreWindowState();

        BuildLayout();
        PopulateFromConfig();

        Shown += (_, _) =>
        {
            if (_documentationMode)
            {
                _connectionLabel.Text = "Connected to VPC Throttle MT-50CM3 (documentation sample).";
                return;
            }

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
        _promptPickerEditor?.Close();
        _promptPickerEditor?.Dispose();
        base.OnFormClosed(eventArgs);
    }

    protected override void OnThemeApplied()
    {
        base.OnThemeApplied();
        foreach (DataGridViewRow row in _bindingGrid.Rows)
        {
            UpdateWheelNotchesCell(row);
        }

        _captureLabel.ForeColor = JoydexTheme.TextSub;
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
        var effectiveMinimum = new Size(
            Math.Min(PreferredMinimumSize.Width, Math.Max(1, workingArea.Width)),
            Math.Min(PreferredMinimumSize.Height, Math.Max(1, workingArea.Height)));
        MinimumSize = effectiveMinimum;

        return new Size(
            Math.Clamp(requestedSize.Width, effectiveMinimum.Width, Math.Max(effectiveMinimum.Width, workingArea.Width)),
            Math.Clamp(requestedSize.Height, effectiveMinimum.Height, Math.Max(effectiveMinimum.Height, workingArea.Height)));
    }

    private int ScaleLogical(int value) => (value * DeviceDpi + 48) / 96;

    private void BuildLayout()
    {
        var main = new TableLayoutPanel
        {
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 12, 12, 0),
            RowCount = 2,
        };
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLogical(196)));
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var sidebar = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 12, 0),
            Padding = new Padding(8, 10, 8, 8),
        };
        var sidebarLayout = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            RowCount = 1,
        };
        sidebarLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var navigation = new TableLayoutPanel
        {
            AccessibleName = "Configuration pages",
            AccessibleRole = AccessibleRole.PageTabList,
            AutoSize = true,
            ColumnCount = 1,
            Dock = DockStyle.Top,
            RowCount = 0,
        };
        sidebarLayout.Controls.Add(navigation, 0, 0);
        sidebar.Controls.Add(sidebarLayout);

        _pageHost.Padding = new Padding(8, 0, 0, 0);
        AddNavigationPage("Bindings", BuildBindingsPage(), navigation);
        AddNavigationPage("Prompt Pickers", BuildPromptPickersPage(), navigation);
        AddNavigationPage("Button Maps", BuildButtonMapsPage(), navigation);
        AddNavigationPage("General", BuildGeneralPage(), navigation);
        ShowPage(0, focusNavigation: false);

        main.Controls.Add(sidebar, 0, 0);
        main.SetRowSpan(sidebar, 2);
        main.Controls.Add(_pageHost, 1, 0);
        main.Controls.Add(BuildFooter(), 1, 1);
        Controls.Add(main);
        ThemeService.Apply(this);
    }

    internal void SelectTabForDocumentation(string tabText)
    {
        var index = _navigationPages.FindIndex(candidate =>
            string.Equals(candidate.Title, tabText, StringComparison.Ordinal));
        if (index >= 0)
        {
            ShowPage(index, focusNavigation: false);
        }
    }

    internal void ExerciseBindingGridEditingForDocumentation()
    {
        var comboColumns = new[] { "BindingDevice", "BindingTrigger", "BindingAction" };
        var rowCount = Math.Min(10, _bindingGrid.Rows.Count);
        for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            foreach (var columnName in comboColumns)
            {
                _bindingGrid.CurrentCell = _bindingGrid.Rows[rowIndex].Cells[columnName];
                _bindingGrid.BeginEdit(selectAll: false);
                Application.DoEvents();
                _bindingGrid.EndEdit();
            }
        }

        _bindingGrid.CurrentCell = null;
        _bindingGrid.Focus();
        if (_bindingGrid.Rows.Count > 0)
        {
            _bindingGrid.FirstDisplayedScrollingRowIndex = 0;
        }

        Application.DoEvents();
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if ((keyData & Keys.Control) == Keys.Control && (keyData & Keys.KeyCode) == Keys.Tab)
        {
            var current = Math.Max(0, _navigationPages.FindIndex(page => page.Button.Selected));
            var direction = (keyData & Keys.Shift) == Keys.Shift ? -1 : 1;
            var next = (current + direction + _navigationPages.Count) % _navigationPages.Count;
            ShowPage(next, focusNavigation: true);
            return true;
        }

        return base.ProcessCmdKey(ref message, keyData);
    }

    private static Panel CreatePage(Padding? padding = null) => new()
    {
        Dock = DockStyle.Fill,
        Padding = padding ?? new Padding(0, 0, 0, 12),
    };

    private void AddNavigationPage(string title, Control page, TableLayoutPanel navigation)
    {
        var index = _navigationPages.Count;
        var button = new NavButton
        {
            AccessibleName = title,
            Dock = DockStyle.Top,
            Height = ScaleLogical(36),
            Glyph = title switch
            {
                "Bindings" => NavGlyph.Bindings,
                "Prompt Pickers" => NavGlyph.PromptPickers,
                "Button Maps" => NavGlyph.ButtonMaps,
                "General" => NavGlyph.General,
                _ => NavGlyph.None,
            },
            Text = title,
        };
        button.Click += (_, _) => ShowPage(index, focusNavigation: false);
        navigation.RowCount++;
        navigation.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLogical(40)));
        navigation.Controls.Add(button, 0, index);

        page.Visible = false;
        _pageHost.Controls.Add(page);
        _navigationPages.Add(new NavigationPage(title, button, page));
    }

    private void ShowPage(int index, bool focusNavigation)
    {
        if (index < 0 || index >= _navigationPages.Count)
        {
            return;
        }

        for (var candidateIndex = 0; candidateIndex < _navigationPages.Count; candidateIndex++)
        {
            var candidate = _navigationPages[candidateIndex];
            var selected = candidateIndex == index;
            candidate.Button.Selected = selected;
            candidate.Button.TabStop = selected;
            candidate.Page.Visible = selected;
            if (selected)
            {
                candidate.Page.BringToFront();
            }
        }

        if (focusNavigation)
        {
            _navigationPages[index].Button.Focus();
        }
    }

    private Control BuildBindingsPage()
    {
        var page = CreatePage();
        _loadDefaultsButton.Click += (_, _) => LoadStarterProfile();
        page.Controls.Add(BuildBindingGroup());
        return page;
    }

    private Control BuildPromptPickersPage()
    {
        var page = CreatePage(new Padding(0));
        _promptPickerEditor = new PromptPickerEditorForm(_configPath, _cooperativeWindowHandle, pickerOnly: true);
        page.Controls.Add(_promptPickerEditor.EmbeddedPickerPage);
        return page;
    }

    private Control BuildButtonMapsPage()
    {
        var page = CreatePage();
        var layout = new TableLayoutPanel { ColumnCount = 1, Dock = DockStyle.Fill, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            Padding = new Padding(0, 0, 0, 8),
            Text = "Choose a map template and optionally capture a physical button that shows the map while held.",
        }, 0, 0);
        layout.Controls.Add(BuildButtonMapGroup(), 0, 1);
        page.Controls.Add(layout);
        return page;
    }

    private Control BuildGeneralPage()
    {
        var page = CreatePage();
        page.AutoScroll = true;
        var layout = new TableLayoutPanel { ColumnCount = 1, Dock = DockStyle.Fill, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 210));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 230));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(BuildDeviceGroup(), 0, 0);
        layout.Controls.Add(BuildSafetyGroup(), 0, 1);

        var bankGroup = BuildBankGroup();
        bankGroup.Visible = _originalConfig.BankSelectors.Count > 0;
        var advanced = new RoundedButton
        {
            Margin = new Padding(0, 12, 0, 6),
            Text = bankGroup.Visible ? "▼ Advanced" : "▶ Advanced",
            Variant = ButtonVariant.Ghost,
        };
        advanced.Click += (_, _) =>
        {
            bankGroup.Visible = !bankGroup.Visible;
            advanced.Text = bankGroup.Visible ? "▼ Advanced" : "▶ Advanced";
        };
        var advancedBar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        advancedBar.Controls.Add(advanced);
        advancedBar.Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Tag = ThemeTone.Subtle,
            Margin = new Padding(12, 20, 0, 0),
            Text = "Software banks are only needed when one controller reuses the same logical buttons in several hardware modes.",
        });
        layout.Controls.Add(advancedBar, 0, 2);
        layout.Controls.Add(bankGroup, 0, 3);
        page.Controls.Add(layout);
        return page;
    }

    private Control BuildDeviceGroup()
    {
        var layout = new TableLayoutPanel
        {
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            RowCount = 3,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Anchor = AnchorStyles.Left, AutoSize = true, Text = "Device" }, 0, 0);
        layout.Controls.Add(_deviceCombo, 1, 0);
        layout.Controls.Add(new Label { Anchor = AnchorStyles.Left, AutoSize = true, Text = "Connection" }, 0, 1);
        layout.Controls.Add(_connectionLabel, 1, 1);
        layout.Controls.Add(new Label { Anchor = AnchorStyles.Left, AutoSize = true, Text = "Live input" }, 0, 2);
        layout.Controls.Add(_inputLabel, 1, 2);
        return BuildCard("Controller", layout);
    }

    private Control BuildBankGroup()
    {
        _bankGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            FillWeight = 70,
            HeaderText = "Bank name",
            MinimumWidth = 180,
            Name = "BankName",
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _bankGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            FillWeight = 30,
            HeaderText = "Selector button",
            MinimumWidth = 130,
            Name = "SelectorButton",
            ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });

        var add = new RoundedButton { Text = "Add bank" };
        add.Click += (_, _) =>
        {
            CancelCapture();
            var index = _bankGrid.Rows.Add($"bank-{_bankGrid.Rows.Count + 1}", null);
            _bankGrid.CurrentCell = _bankGrid.Rows[index].Cells[0];
        };
        var remove = new RoundedButton { Text = "Remove bank" };
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
            MinimumWidth = 180,
            Name = "BindingName",
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _bindingGrid.Columns.Add(new DataGridViewComboBoxColumn
        {
            DataSource = _originalConfig.Devices.Select(device => device.Id).ToArray(),
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
            DisplayStyleForCurrentCellOnly = true,
            FillWeight = 15,
            FlatStyle = FlatStyle.Flat,
            HeaderText = "Device",
            MinimumWidth = 100,
            Name = "BindingDevice",
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _bindingGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            FillWeight = 18,
            HeaderText = "Bank",
            MinimumWidth = 100,
            Name = "BindingBank",
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _bindingGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            FillWeight = 10,
            HeaderText = "Button",
            MinimumWidth = 70,
            Name = "BindingButton",
            ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _bindingGrid.Columns.Add(new DataGridViewComboBoxColumn
        {
            DataSource = new[] { "press", "release" },
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
            DisplayStyleForCurrentCellOnly = true,
            FillWeight = 10,
            FlatStyle = FlatStyle.Flat,
            HeaderText = "When",
            MinimumWidth = 80,
            Name = "BindingTrigger",
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _bindingGrid.Columns.Add(new DataGridViewComboBoxColumn
        {
            DataSource = CodexActionCatalog.SupportedIds.ToArray(),
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
            DisplayStyleForCurrentCellOnly = true,
            FillWeight = 23,
            FlatStyle = FlatStyle.Flat,
            HeaderText = "Codex action",
            MinimumWidth = 150,
            Name = "BindingAction",
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _bindingGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            FillWeight = 15,
            HeaderText = "Wheel notches",
            MinimumWidth = 110,
            Name = "BindingWheelNotches",
            SortMode = DataGridViewColumnSortMode.NotSortable,
            ToolTipText = "Mouse-wheel notches per encoder detent; used only by scroll actions.",
        });

        _bindingGrid.CellPainting += OnBindingGridCellPainting;

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

        var add = new RoundedButton { Text = "+ Add binding", Variant = ButtonVariant.Primary };
        add.Click += (_, _) =>
        {
            CancelCapture();
            var bank = _bankGrid.Rows.Cast<DataGridViewRow>()
                .Select(row => Convert.ToString(row.Cells["BankName"].Value))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
            var index = _bindingGrid.Rows.Add(
                $"binding-{_bindingGrid.Rows.Count + 1}",
                _originalConfig.Devices[0].Id,
                bank,
                null,
                "press",
                "new-task",
            null);
            UpdateWheelNotchesCell(_bindingGrid.Rows[index]);
            ApplyBindingFilter();
            if (_bindingGrid.Rows[index].Visible)
            {
                _bindingGrid.CurrentCell = _bindingGrid.Rows[index].Cells[0];
            }
        };
        var remove = new RoundedButton { Text = "Remove" };
        remove.Click += (_, _) =>
        {
            RemoveCurrentRow(_bindingGrid);
            ApplyBindingFilter();
        };
        _captureBindingButton.Text = "Capture action";
        _captureBindingButton.Click += (_, _) =>
        {
            if (SelectBindingRowDeviceForCapture())
            {
                BeginCapture(_bindingGrid, "BindingButton", "Press the controller button for this Codex action.");
            }
        };
        _loadDefaultsButton.Text = "Load defaults";

        _bindingFilter.Editor.AccessibleName = "Filter bindings";
        _bindingFilter.PlaceholderText = "Filter bindings...";
        _bindingFilter.Editor.TextChanged += (_, _) => ApplyBindingFilter();
        _bindingFilter.Margin = new Padding(0, 0, 8, 4);
        _bindingCountLabel.Margin = new Padding(0, 8, 12, 0);

        var toolbar = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = new Padding(0, 0, 0, 4),
            WrapContents = true,
        };
        toolbar.Controls.AddRange([
            _bindingFilter,
            _bindingCountLabel,
            _captureBindingButton,
            _loadDefaultsButton,
            remove,
            add,
        ]);

        var clusters = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = new Padding(0, 0, 0, 4),
            WrapContents = true,
        };
        foreach (var cluster in Enum.GetValues<BindingCluster>())
        {
            var capturedCluster = cluster;
            var chip = new RoundedButton
            {
                AccessibleName = $"Show {FormatBindingCluster(cluster)} bindings",
                CornerRadius = JoydexTheme.CompactControlHeight / 2,
                MinimumSize = new Size(0, JoydexTheme.CompactControlHeight),
                Padding = new Padding(10, 4, 10, 4),
                Variant = cluster == BindingCluster.All ? ButtonVariant.Primary : ButtonVariant.Secondary,
            };
            chip.Click += (_, _) =>
            {
                _bindingCluster = capturedCluster;
                ApplyBindingFilter();
            };
            _bindingClusterButtons[cluster] = chip;
            clusters.Controls.Add(chip);
        }

        var helper = new Label
        {
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
            Tag = ThemeTone.Faint,
            Text = "Start with the Codex Micro layout for the CM3, then adjust any row you want.",
        };
        var gridCard = new CardPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(1),
        };
        gridCard.Controls.Add(_bindingGrid);

        var layout = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            RowCount = 4,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(toolbar, 0, 0);
        layout.Controls.Add(clusters, 0, 1);
        layout.Controls.Add(helper, 0, 2);
        layout.Controls.Add(gridCard, 0, 3);
        return layout;
    }

    private void ApplyBindingFilter()
    {
        var filter = _bindingFilter.Editor.Text.Trim();
        _bindingGrid.EndEdit();
        _bindingGrid.CurrentCell = null;
        var visible = 0;
        foreach (DataGridViewRow row in _bindingGrid.Rows)
        {
            var matchesText = filter.Length == 0
                || row.Cells.Cast<DataGridViewCell>()
                    .Select(cell => Convert.ToString(cell.Value))
                    .Any(value => value?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true);
            var matches = matchesText && MatchesBindingCluster(row, _bindingCluster);
            row.Visible = matches;
            if (matches)
            {
                visible++;
            }
        }

        _bindingCountLabel.Text = filter.Length == 0
            ? $"{visible} bindings"
            : $"{visible} of {_bindingGrid.Rows.Count} bindings";

        foreach (var (cluster, button) in _bindingClusterButtons)
        {
            var count = _bindingGrid.Rows.Cast<DataGridViewRow>().Count(row => MatchesBindingCluster(row, cluster));
            button.Text = $"{FormatBindingCluster(cluster)} · {count}";
            button.Variant = cluster == _bindingCluster ? ButtonVariant.Primary : ButtonVariant.Secondary;
        }
    }

    private static bool MatchesBindingCluster(DataGridViewRow row, BindingCluster cluster)
    {
        if (cluster == BindingCluster.All)
        {
            return true;
        }

        var label = Convert.ToString(row.Cells["BindingName"].Value)?.Trim() ?? string.Empty;
        var action = Convert.ToString(row.Cells["BindingAction"].Value)?.Trim() ?? string.Empty;
        return cluster switch
        {
            BindingCluster.Encoders => label.StartsWith("E1", StringComparison.OrdinalIgnoreCase)
                || label.StartsWith("E2", StringComparison.OrdinalIgnoreCase),
            BindingCluster.Modules => label.Length >= 2
                && char.ToUpperInvariant(label[0]) == 'M'
                && char.IsDigit(label[1]),
            BindingCluster.StickAndHats => label.StartsWith("Joystick", StringComparison.OrdinalIgnoreCase)
                || label.StartsWith("T5", StringComparison.OrdinalIgnoreCase)
                || label.StartsWith("T7", StringComparison.OrdinalIgnoreCase),
            BindingCluster.Talk => label.Contains("talk", StringComparison.OrdinalIgnoreCase)
                || string.Equals(action, "push-to-talk", StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
    }

    private static string FormatBindingCluster(BindingCluster cluster) => cluster switch
    {
        BindingCluster.All => "All",
        BindingCluster.Encoders => "Encoders",
        BindingCluster.Modules => "Modules",
        BindingCluster.StickAndHats => "Stick & hats",
        BindingCluster.Talk => "Talk",
        _ => cluster.ToString(),
    };

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
        cell.Style.BackColor = isScrollAction ? JoydexTheme.Surface : JoydexTheme.GroupBg;
        cell.Style.ForeColor = isScrollAction ? JoydexTheme.Text : JoydexTheme.TextFaint;

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

    private void OnBindingGridCellPainting(object? sender, DataGridViewCellPaintingEventArgs eventArgs)
    {
        if (eventArgs.RowIndex < 0 || eventArgs.ColumnIndex < 0)
        {
            return;
        }

        if (eventArgs.Graphics is not { } graphics)
        {
            return;
        }

        var column = _bindingGrid.Columns[eventArgs.ColumnIndex];
        if (column.Name == "BindingTrigger")
        {
            var current = _bindingGrid.CurrentCell;
            if (_bindingGrid.IsCurrentCellInEditMode
                && current is not null
                && current.RowIndex == eventArgs.RowIndex
                && current.ColumnIndex == eventArgs.ColumnIndex)
            {
                return;
            }

            var selected = (eventArgs.State & DataGridViewElementStates.Selected) != 0;
            var cellStyle = eventArgs.CellStyle ?? _bindingGrid.DefaultCellStyle;
            using (var background = new SolidBrush(
                       selected
                           ? cellStyle.SelectionBackColor
                           : cellStyle.BackColor))
            {
                graphics.FillRectangle(background, eventArgs.CellBounds);
            }

            var value = Convert.ToString(eventArgs.FormattedValue) ?? string.Empty;
            var warning = string.Equals(value, "release", StringComparison.OrdinalIgnoreCase);
            var textSize = TextRenderer.MeasureText(
                value,
                JoydexTheme.MonoFont,
                Size.Empty,
                TextFormatFlags.NoPadding);
            var horizontalInset = ScaleLogical(8);
            var reservedArrowWidth = ScaleLogical(26);
            var pillHeight = Math.Min(
                eventArgs.CellBounds.Height - ScaleLogical(6),
                textSize.Height + ScaleLogical(8));
            var pillBounds = new Rectangle(
                eventArgs.CellBounds.Left + horizontalInset,
                eventArgs.CellBounds.Top + ((eventArgs.CellBounds.Height - pillHeight) / 2),
                Math.Max(
                    ScaleLogical(8),
                    Math.Min(
                        eventArgs.CellBounds.Width - reservedArrowWidth,
                        textSize.Width + ScaleLogical(16))),
                pillHeight);
            using (var brush = new SolidBrush(warning ? JoydexTheme.TagWarnBg : JoydexTheme.TagBg))
            {
                ThemeDrawing.FillRoundedRectangle(graphics, brush, pillBounds, pillHeight / 2);
            }

            TextRenderer.DrawText(
                graphics,
                value,
                JoydexTheme.MonoFont,
                pillBounds,
                warning ? JoydexTheme.TagWarnText : JoydexTheme.TagText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            DrawComboAffordance(eventArgs);

            eventArgs.Handled = true;
            return;
        }
    }

    private void DrawComboAffordance(DataGridViewCellPaintingEventArgs eventArgs)
    {
        if (eventArgs.Graphics is not { } graphics)
        {
            return;
        }

        var arrowWidth = ScaleLogical(24);
        TextRenderer.DrawText(
            graphics,
            "\u25BE",
            JoydexTheme.SectionFont,
            new Rectangle(
                eventArgs.CellBounds.Right - arrowWidth,
                eventArgs.CellBounds.Top,
                arrowWidth,
                eventArgs.CellBounds.Height),
            JoydexTheme.TextFaint,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private Control BuildButtonMapGroup()
    {
        _buttonMapGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            FillWeight = 14,
            HeaderText = "Device ID",
            MinimumWidth = 100,
            Name = "MapDeviceId",
            ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _buttonMapGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            FillWeight = 28,
            HeaderText = "Controller",
            MinimumWidth = 180,
            Name = "MapDeviceName",
            ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        var template = new DataGridViewComboBoxColumn
        {
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
            DisplayStyleForCurrentCellOnly = true,
            FillWeight = 16,
            FlatStyle = FlatStyle.Flat,
            HeaderText = "Map template",
            MinimumWidth = 120,
            Name = "MapTemplate",
            SortMode = DataGridViewColumnSortMode.NotSortable,
        };
        template.Items.AddRange("", "cm3", "alpha-warbrd");
        _buttonMapGrid.Columns.Add(template);
        _buttonMapGrid.Columns.Add(new DataGridViewComboBoxColumn
        {
            DataSource = _originalConfig.Devices.Select(device => device.Id).ToArray(),
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
            DisplayStyleForCurrentCellOnly = true,
            FillWeight = 22,
            FlatStyle = FlatStyle.Flat,
            HeaderText = "Hold source",
            MinimumWidth = 120,
            Name = "MapHoldDevice",
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        _buttonMapGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            FillWeight = 20,
            HeaderText = "Hold-to-show button",
            MinimumWidth = 150,
            Name = "MapHold",
            ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });

        _captureMapHoldButton.Click += (_, _) =>
        {
            if (_buttonMapGrid.CurrentRow is null)
            {
                MessageBox.Show(this, "Select a controller row first.", "Capture map control");
                return;
            }

            var sourceDeviceId = Convert.ToString(_buttonMapGrid.CurrentRow.Cells["MapHoldDevice"].Value);
            if (SelectConfiguredDeviceForCapture(sourceDeviceId))
            {
                BeginCapture(
                    _buttonMapGrid,
                    "MapHold",
                    "Move the physical control that should hold the selected button map open.");
            }
        };
        var clear = new RoundedButton { Text = "Clear hold control" };
        clear.Click += (_, _) =>
        {
            CancelCapture();
            if (_buttonMapGrid.CurrentRow is not null)
            {
                _buttonMapGrid.CurrentRow.Cells["MapHold"].Value = null;
            }
        };

        return BuildGridGroup("Button maps", _buttonMapGrid, _captureMapHoldButton, clear);
    }

    private Control BuildSafetyGroup()
    {
        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Dock = DockStyle.Top,
            RowCount = 4,
        };
        for (var row = 0; row < 4; row++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
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
            Tag = ThemeTone.Subtle,
            Text = "Comma-separated process names, for example: DCS, FlightSimulator. Keyboard actions always require Codex in the foreground.",
        };
        layout.Controls.Add(hint, 0, 3);
        layout.SetColumnSpan(hint, 2);
        return BuildCard("Safety and open", layout);
    }

    private Control BuildFooter()
    {
        var save = new RoundedButton { Text = "Save and close", Variant = ButtonVariant.Primary };
        save.Click += OnSave;
        var cancel = new RoundedButton { DialogResult = DialogResult.Cancel, Text = "Cancel" };
        _cancelCaptureButton.Click += (_, _) => CancelCapture();
        var captureStatus = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 8, 0, 0),
            WrapContents = false,
        };
        captureStatus.Controls.Add(new StatusDot());
        _captureLabel.Margin = new Padding(0, 7, 10, 0);
        captureStatus.Controls.Add(_captureLabel);
        captureStatus.Controls.Add(_cancelCaptureButton);

        var commands = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 8, 12, 0),
            WrapContents = false,
        };
        commands.Controls.Add(save);
        commands.Controls.Add(cancel);

        var footer = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            Dock = DockStyle.Fill,
        };
        footer.Paint += (_, eventArgs) =>
        {
            using var pen = new Pen(JoydexTheme.Border);
            eventArgs.Graphics.DrawLine(pen, 0, 0, footer.ClientSize.Width, 0);
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(captureStatus, 0, 0);
        footer.Controls.Add(commands, 1, 0);
        AcceptButton = save;
        CancelButton = cancel;
        return footer;
    }

    private static Control BuildGridGroup(string title, DataGridView grid, params RoundedButton[] buttons)
    {
        var layout = new TableLayoutPanel { ColumnCount = 1, Dock = DockStyle.Fill, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(grid, 0, 0);

        var commands = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 4, 0, 0),
            WrapContents = true,
        };
        commands.Controls.AddRange(buttons);
        layout.Controls.Add(commands, 0, 1);
        return BuildCard(title, layout);
    }

    private static CardPanel BuildCard(string title, Control content)
    {
        var card = new CardPanel { Dock = DockStyle.Fill };
        var layout = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            RowCount = 2,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            Font = JoydexTheme.SectionFont,
            Margin = new Padding(0, 0, 0, 10),
            Text = title.ToUpperInvariant(),
        }, 0, 0);
        layout.Controls.Add(content, 0, 1);
        card.Controls.Add(layout);
        return card;
    }

    private static ModernDataGridView CreateGrid() => new()
    {
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BackgroundColor = Color.White,
        BorderStyle = BorderStyle.None,
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
                binding.DeviceId ?? _originalConfig.Devices[0].Id,
                binding.Bank,
                binding.Button,
                binding.Trigger,
                binding.Action,
                binding.WheelNotches);
            UpdateWheelNotchesCell(_bindingGrid.Rows[rowIndex]);
        }
        ApplyBindingFilter();

        var cm3MapRow = _buttonMapGrid.Rows.Cast<DataGridViewRow>().FirstOrDefault(row =>
            string.Equals(
                Convert.ToString(row.Cells["MapDeviceId"].Value),
                CompanionConfigNormalizer.PrimaryDeviceId,
                StringComparison.OrdinalIgnoreCase));
        if (cm3MapRow is not null)
        {
            cm3MapRow.Cells["MapHoldDevice"].Value = CompanionConfigNormalizer.PrimaryDeviceId;
            cm3MapRow.Cells["MapHold"].Value = 36;
        }

        var profileName = dialProfile == Cm3ModeDialProfile.FiveWayShift
            ? "CM3 5-way shift mode"
            : "CM3 standard mode buttons";
        _captureLabel.Text = $"Loaded {profileName}. Save to use this device layout.";
    }

    private void PopulateFromConfig()
    {
        IReadOnlyList<DirectInputDeviceInfo> devices;
        if (_documentationMode)
        {
            devices =
            [
                new DirectInputDeviceInfo(
                    "VPC Throttle MT-50CM3",
                    "VPC Throttle MT-50CM3",
                    Guid.Empty,
                    Guid.Empty),
            ];
        }
        else
        {
            try
            {
                devices = _source.EnumerateDevices();
            }
            catch (Exception exception)
            {
                devices = [];
                _connectionLabel.Text = $"Could not enumerate devices: {exception.Message}";
            }
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
                binding.DeviceId ?? _originalConfig.Devices[0].Id,
                binding.Bank,
                binding.Button,
                binding.Trigger,
                binding.Action,
                binding.WheelNotches);
            UpdateWheelNotchesCell(_bindingGrid.Rows[rowIndex]);
        }
        ApplyBindingFilter();

        foreach (var profile in _originalConfig.Devices)
        {
            _buttonMapGrid.Rows.Add(
                profile.Id,
                profile.DisplayName,
                profile.ButtonMapTemplate ?? string.Empty,
                profile.ButtonMapHoldControl?.DeviceId ?? profile.Id,
                profile.ButtonMapHoldControl?.Button);
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

    private bool SelectBindingRowDeviceForCapture()
    {
        if (_bindingGrid.CurrentRow is null)
        {
            MessageBox.Show(this, "Select a row first.", "Capture control", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        var deviceId = Convert.ToString(_bindingGrid.CurrentRow.Cells["BindingDevice"].Value);
        return SelectConfiguredDeviceForCapture(deviceId);
    }

    private bool SelectConfiguredDeviceForCapture(string? deviceId)
    {
        var profile = _originalConfig.Devices.FirstOrDefault(device =>
            string.Equals(device.Id, deviceId, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            MessageBox.Show(this, $"Controller profile '{deviceId}' is not configured.", "Capture control");
            return false;
        }

        var attached = _deviceCombo.Items.Cast<DirectInputDeviceInfo>().FirstOrDefault(device =>
            Guid.TryParse(profile.Selector.InstanceGuid, out var instanceGuid)
                ? device.InstanceGuid == instanceGuid
                : device.ProductName.Contains(profile.Selector.ProductNameContains, StringComparison.OrdinalIgnoreCase));
        if (attached is null)
        {
            MessageBox.Show(this, $"{profile.DisplayName} is not attached.", "Capture control");
            return false;
        }

        if (ReferenceEquals(_deviceCombo.SelectedItem, attached))
        {
            ConnectSelectedDevice();
        }
        else
        {
            var returnDevice = _deviceCombo.SelectedItem as DirectInputDeviceInfo;
            _deviceCombo.SelectedItem = attached;
            _captureReturnDevice = returnDevice;
        }

        return true;
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
            _connectionLabel.Text = $"Controller disconnected: {error ?? "unknown DirectInput error"}";
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
                RestoreDeviceAfterCapture();
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
            RestoreDeviceAfterCapture();
        }
    }

    private void RestoreDeviceAfterCapture()
    {
        var returnDevice = _captureReturnDevice;
        _captureReturnDevice = null;
        if (returnDevice is not null && !ReferenceEquals(_deviceCombo.SelectedItem, returnDevice))
        {
            _deviceCombo.SelectedItem = returnDevice;
        }
    }

    private void UpdateCaptureButtons()
    {
        _captureBankButton.Enabled = _captureTarget is null;
        _captureBindingButton.Enabled = _captureTarget is null;
        _captureMapHoldButton.Enabled = _captureTarget is null;
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
        CancelCapture();
        _bankGrid.EndEdit();
        _bindingGrid.EndEdit();
        _buttonMapGrid.EndEdit();

        var bankSelectors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var bindings = new List<ButtonBinding>();
        var buttonMaps = new Dictionary<string, (string? Template, DeviceControlReference? Hold)>(StringComparer.OrdinalIgnoreCase);
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
            var deviceId = Convert.ToString(row.Cells["BindingDevice"].Value)?.Trim()
                ?? _originalConfig.Devices[0].Id;
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
                DeviceId = deviceId,
                Bank = bank,
                Button = button,
                Trigger = trigger,
                Action = action,
                WheelNotches = wheelNotches,
            });
        }

        foreach (DataGridViewRow row in _buttonMapGrid.Rows)
        {
            var deviceId = Convert.ToString(row.Cells["MapDeviceId"].Value)?.Trim() ?? string.Empty;
            var template = Convert.ToString(row.Cells["MapTemplate"].Value)?.Trim();
            var holdDeviceId = Convert.ToString(row.Cells["MapHoldDevice"].Value)?.Trim() ?? string.Empty;
            var holdText = Convert.ToString(row.Cells["MapHold"].Value)?.Trim();
            DeviceControlReference? hold = null;
            if (!string.IsNullOrWhiteSpace(holdText))
            {
                if (int.TryParse(holdText, out var parsedHold))
                {
                    hold = new DeviceControlReference
                    {
                        DeviceId = holdDeviceId,
                        Bank = CompanionConfig.AlwaysBank,
                        Button = parsedHold,
                    };
                }
                else
                {
                    parseErrors.Add($"Button map for '{deviceId}' needs a captured hold-to-show button.");
                }
            }

            buttonMaps[deviceId] = (string.IsNullOrWhiteSpace(template) ? null : template, hold);
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
            Devices = _originalConfig.Devices.Select((profile, index) =>
            {
                var map = buttonMaps.TryGetValue(profile.Id, out var configuredMap)
                    ? configuredMap
                    : (Template: profile.ButtonMapTemplate, Hold: profile.ButtonMapHoldControl);
                return new DeviceProfile
                {
                    Id = profile.Id,
                    DisplayName = profile.DisplayName,
                    Selector = index == 0 ? device : profile.Selector,
                    BankSelectors = index == 0 ? bankSelectors : profile.BankSelectors,
                    ButtonMapTemplate = map.Template,
                    ButtonMapHoldControl = map.Hold,
                };
            }).ToList(),
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
            PromptPickers = _promptPickerEditor?.GetPromptPickers().ToList()
                ?? _originalConfig.PromptPickers,
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

    private enum BindingCluster
    {
        All,
        Encoders,
        Modules,
        StickAndHats,
        Talk,
    }

    private sealed record CaptureTarget(DataGridView Grid, int RowIndex, string ColumnName);

    private sealed record NavigationPage(string Title, NavButton Button, Control Page);
}
