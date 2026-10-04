using Joydex.Contracts;
using Joydex.Core.Config;
using Joydex.Core.Input;
using Joydex.Core.Mapping;
using Joydex.Core.TaskAlerts;
using Joydex.Core.Voice;
using Joydex.Windows.Runtime;

namespace Joydex.App;

internal sealed class ConfigurationDraftEventArgs(
    CompanionConfig companion,
    VoicePePreferences? voice,
    PebbleIndexPreferences? pebbleIndex) : EventArgs
{
    public CompanionConfig Companion { get; } = companion;

    public VoicePePreferences? Voice { get; } = voice;

    public PebbleIndexPreferences? PebbleIndex { get; } = pebbleIndex;
}

internal sealed class ConfigurationForm : ThemedForm
{
    private static readonly Size PreferredMinimumSize = new(760, 560);
    private readonly string _configPath;
    private readonly string _windowStatePath;
    private readonly IntPtr _cooperativeWindowHandle;
    private CompanionConfig _originalConfig;
    private readonly IConfigurationInputClient? _inputClient;
    private readonly IConfigurationInputSession? _inputSession;
    private readonly bool _documentationMode;
    private readonly bool _demoMode;
    private readonly RoomVoiceSettingsControl? _roomVoiceSettings;
    private readonly PebbleIndexSettingsControl? _pebbleIndexSettings;
    private readonly Func<CompanionConfig, VoicePePreferences?, PebbleIndexPreferences?, bool> _saveConfiguration;
    private readonly Func<CompanionConfig, VoicePePreferences?, PebbleIndexPreferences?, CancellationToken, Task<RuntimeSettingsWriteResult>>? _applyConfiguration;
    private SettingsBundle _authoritativeSettings;
    private SettingsBundle? _lastProjectedDraft;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly InputObservationCoalescer _inputObservations = new();
    private readonly ComboBox _deviceCombo = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _connectionLabel = new() { AutoSize = true, Text = "Looking for controller..." };
    private readonly Label _inputLabel = new() { AutoSize = true, Text = "Held buttons: none" };
    private readonly StatusLabel _captureLabel = new() { Tag = ThemeTone.Subtle, Text = "Select a row and choose Capture." };
    private readonly StatusLabel _runtimeStatus = new()
    {
        AccessibleName = "Settings apply status",
        Tag = ThemeTone.Subtle,
        Text = "Changes are applied by the Joydex runtime.",
        Visible = false,
    };
    private readonly ModernDataGridView _bankGrid = CreateGrid();
    private readonly ModernDataGridView _bindingGrid = CreateGrid();
    private readonly ModernDataGridView _buttonMapGrid = CreateGrid();
    private readonly BorderedTextBox _bindingFilter = new();
    private readonly Label _bindingCountLabel = new() { AutoSize = true, Tag = ThemeTone.Faint };
    private readonly Dictionary<BindingCluster, RoundedButton> _bindingClusterButtons = [];
    private BindingCluster _bindingCluster = BindingCluster.All;
    private readonly CheckBox _dryRunCheckBox = new()
    {
        AutoSize = true,
        Name = "ConfigurationDryRun",
        Text = "Dry run (log actions without sending them)",
    };
    private readonly TextBox _simulatorProcessesTextBox = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _openTargetCombo = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly RoundedButton _captureBankButton = new() { Text = "Capture selector" };
    private readonly RoundedButton _captureBindingButton = new() { Text = "Capture action button" };
    private readonly RoundedButton _captureMapHoldButton = new() { Text = "Capture hold-to-show" };
    private readonly RoundedButton _cancelCaptureButton = new() { Text = "Cancel capture", Visible = false };
    private readonly RoundedButton _loadDefaultsButton = new() { Text = "Load Codex Micro defaults" };
    private readonly Panel _pageHost = new() { Dock = DockStyle.Fill };
    private Control? _navigation;
    private RoundedButton? _applyButton;
    private RoundedButton? _saveButton;
    private RoundedButton? _reviewLatestButton;
    private RoundedButton? _discardDraftButton;
    private RoundedButton? _cancelButton;
    private readonly List<NavigationPage> _navigationPages = [];
    private IReadOnlyList<RuntimeInputSourceCatalogEntry> _availableSources = [];
    private CaptureTarget? _captureTarget;
    private InputCaptureLease? _captureLease;
    private CancellationTokenSource? _captureStartCancellation;
    private Task? _captureStartTask;
    private JoystickSnapshot? _lastSnapshot;
    private string? _loadWarning;
    private RuntimeInputSourceCatalogEntry? _captureReturnDevice;
    private PromptPickerEditorForm? _promptPickerEditor;
    private bool _closingAfterCaptureCancellation;
    private bool _closeCancellationStarted;
    private bool _configControlsInitialized;
    private bool _runtimeConnected = true;
    private bool _settingsOperationActive;
    private string? _draftAttention;
    private string? _aggregateAttention;

    public ConfigurationForm(
        string configPath,
        string windowStatePath,
        IntPtr cooperativeWindowHandle,
        bool documentationMode = false,
        RoomVoiceSettingsControl? roomVoiceSettings = null,
        PebbleIndexSettingsControl? pebbleIndexSettings = null,
        Func<CompanionConfig, VoicePePreferences?, PebbleIndexPreferences?, bool>? saveConfiguration = null,
        CompanionConfig? initialConfig = null,
        IConfigurationInputClient? inputClient = null,
        bool demoMode = false,
        IConfigurationInputSession? inputSession = null,
        Func<CompanionConfig, VoicePePreferences?, PebbleIndexPreferences?, CancellationToken, Task<RuntimeSettingsWriteResult>>? applyConfiguration = null,
        SettingsBundle? initialSettings = null)
    {
        _configPath = configPath;
        _windowStatePath = windowStatePath;
        _cooperativeWindowHandle = cooperativeWindowHandle;
        _documentationMode = documentationMode;
        _demoMode = demoMode;
        _roomVoiceSettings = roomVoiceSettings;
        _pebbleIndexSettings = pebbleIndexSettings;
        _inputClient = inputClient;
        _inputSession = inputSession ?? inputClient as IConfigurationInputSession;
        _applyConfiguration = applyConfiguration;
        _saveConfiguration = saveConfiguration ?? ((config, _, _) =>
        {
            ConfigStore.Save(_configPath, config);
            return true;
        });
        if (initialConfig is not null)
        {
            _originalConfig = CompanionConfigNormalizer.Normalize(initialConfig);
        }
        else try
        {
            _originalConfig = ConfigStore.LoadOrCreate(configPath);
        }
        catch (Exception exception)
        {
            _originalConfig = CompanionConfig.CreateSafeDefault();
            _loadWarning = $"The existing configuration could not be loaded. Saving will replace it with the values shown here. {exception.Message}";
        }

        _authoritativeSettings = initialSettings ?? new SettingsBundle(
            _originalConfig,
            roomVoiceSettings?.ReadPreferences() ?? VoicePePreferences.Default,
            pebbleIndexSettings?.ReadPreferences() ?? PebbleIndexPreferences.Default,
            TaskAlertPreferences.Default);

        Text = _demoMode ? "Joydex — Demo / dry-run inspector" : "Configure Joydex";
        Icon = ConfigurationIconFactory.Create();
        StartPosition = FormStartPosition.CenterScreen;
        SetLogicalMinimumSize(PreferredMinimumSize);
        Size = new Size(1500, 1000);
        ShowIcon = true;

        RestoreWindowState();

        if (!_documentationMode && _inputSession is not null)
        {
            _inputSession.InputObserved += OnInputObserved;
            _inputSession.CaptureChanged += OnCaptureChanged;
            if (_inputClient is not null)
            {
                try
                {
                    _availableSources = _inputClient.RefreshSources();
                }
                catch (Exception exception)
                {
                    _connectionLabel.Text = $"Could not enumerate devices: {exception.Message}";
                }
            }
        }

        BuildLayout();
        PopulateFromConfig();
        if (_demoMode)
        {
            _dryRunCheckBox.Checked = true;
            _dryRunCheckBox.Enabled = false;
            _dryRunCheckBox.Text = "Dry run (locked for this demo)";
        }

        Shown += async (_, _) =>
        {
            if (_documentationMode)
            {
                _connectionLabel.Text = "Connected to VPC Throttle MT-50CM3 (documentation sample).";
                return;
            }

            if (_inputClient is null)
            {
                await RefreshInputSourcesAsync().ConfigureAwait(true);
            }
            RefreshSelectedDeviceStatus();
            if (_loadWarning is not null)
            {
                MessageBox.Show(this, _loadWarning, "Configuration recovery", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
    }

    protected override void OnFormClosing(FormClosingEventArgs eventArgs)
    {
        if (_settingsOperationActive)
        {
            eventArgs.Cancel = true;
            base.OnFormClosing(eventArgs);
            return;
        }

        if (!_closingAfterCaptureCancellation && CaptureIsInProgress())
        {
            eventArgs.Cancel = true;
            base.OnFormClosing(eventArgs);
            if (!_closeCancellationStarted)
            {
                _closeCancellationStarted = true;
                _ = CancelCaptureAndCloseAsync();
            }
            return;
        }

        var restoredSize = WindowState == FormWindowState.Normal ? Size : RestoreBounds.Size;
        ConfigurationWindowStateStore.Save(
            _windowStatePath,
            new ConfigurationWindowState(
                restoredSize.Width,
                restoredSize.Height,
                WindowState == FormWindowState.Maximized,
                DeviceDpi));

        base.OnFormClosing(eventArgs);
    }

    protected override void OnFormClosed(FormClosedEventArgs eventArgs)
    {
        _captureStartCancellation?.Cancel();
        _captureStartCancellation?.Dispose();
        _captureStartCancellation = null;
        _inputObservations.SelectSource(null);
        _promptPickerEditor?.Close();
        _promptPickerEditor?.Dispose();
        if (_inputSession is not null)
        {
            _inputSession.InputObserved -= OnInputObserved;
            _inputSession.CaptureChanged -= OnCaptureChanged;
        }
        _inputClient?.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
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

        var sourceDpi = state.Dpi > 0 ? state.Dpi : DpiUtilities.SystemDpi;
        Size = DpiUtilities.ScaleBetween(
            new Size(state.Width, state.Height),
            sourceDpi,
            DpiUtilities.LogicalDpi);
        if (state.Maximized)
        {
            WindowState = FormWindowState.Maximized;
        }
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
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 196));
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var sidebar = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 12, 0),
            Padding = new Padding(8, 10, 8, 8),
        };
        _navigation = sidebar;
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
        if (_roomVoiceSettings is not null)
        {
            AddNavigationPage("Room Voice", BuildRoomVoicePage(), navigation);
        }
        if (_pebbleIndexSettings is not null)
        {
            AddNavigationPage("Pebble Index", BuildPebbleIndexPage(), navigation);
        }
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

    internal void SelectPage(string pageTitle) => SelectTabForDocumentation(pageTitle);

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
            Height = 36,
            Glyph = title switch
            {
                "Bindings" => NavGlyph.Bindings,
                "Prompt Pickers" => NavGlyph.PromptPickers,
                "Button Maps" => NavGlyph.ButtonMaps,
                "Room Voice" => NavGlyph.RoomVoice,
                "Pebble Index" => NavGlyph.RoomVoice,
                "General" => NavGlyph.General,
                _ => NavGlyph.None,
            },
            Text = title,
        };
        button.Click += (_, _) => ShowPage(index, focusNavigation: false);
        navigation.RowCount++;
        navigation.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
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
        _loadDefaultsButton.Click += async (_, _) =>
            await LoadStarterProfileAsync().ConfigureAwait(true);
        page.Controls.Add(BuildBindingGroup());
        return page;
    }

    private Control BuildPromptPickersPage()
    {
        var page = CreatePage(new Padding(0));
        _promptPickerEditor = new PromptPickerEditorForm(
            _configPath,
            _cooperativeWindowHandle,
            pickerOnly: true,
            initialConfig: _originalConfig,
            inputClient: _inputClient,
            inputSession: _inputSession);
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

    private Control BuildRoomVoicePage()
    {
        var page = CreatePage(new Padding(0));
        if (_roomVoiceSettings is not null)
        {
            page.Controls.Add(_roomVoiceSettings);
        }
        return page;
    }

    private Control BuildPebbleIndexPage()
    {
        var page = CreatePage(new Padding(0));
        if (_pebbleIndexSettings is not null) page.Controls.Add(_pebbleIndexSettings);
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
        add.Click += async (_, _) =>
        {
            await CancelCaptureAsync().ConfigureAwait(true);
            var index = _bankGrid.Rows.Add($"bank-{_bankGrid.Rows.Count + 1}", null);
            _bankGrid.CurrentCell = _bankGrid.Rows[index].Cells[0];
        };
        var remove = new RoundedButton { Text = "Remove bank" };
        remove.Click += async (_, _) =>
            await RemoveCurrentRowAsync(_bankGrid).ConfigureAwait(true);
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
        var add = new RoundedButton { Text = "+ Add binding", Variant = ButtonVariant.Primary };
        add.Click += async (_, _) =>
        {
            await CancelCaptureAsync().ConfigureAwait(true);
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
        remove.Click += async (_, _) =>
        {
            await RemoveCurrentRowAsync(_bindingGrid).ConfigureAwait(true);
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
                || string.Equals(action, "push-to-talk", StringComparison.OrdinalIgnoreCase)
                || string.Equals(action, "in-app-push-to-talk", StringComparison.OrdinalIgnoreCase)
                || string.Equals(action, "voice-chat", StringComparison.OrdinalIgnoreCase)
                || string.Equals(action, "end-voice-chat", StringComparison.OrdinalIgnoreCase)
                || string.Equals(action, "toggle-voice-mic", StringComparison.OrdinalIgnoreCase),
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
            var monoFont = JoydexTheme.FontFor(_bindingGrid, JoydexTheme.MonoFont);
            var textSize = TextRenderer.MeasureText(
                value,
                monoFont,
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
                monoFont,
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
            JoydexTheme.FontFor(_bindingGrid, JoydexTheme.SectionFont),
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
        clear.Click += async (_, _) =>
        {
            var row = _buttonMapGrid.CurrentRow;
            await CancelCaptureAsync().ConfigureAwait(true);
            if (row is not null && _buttonMapGrid.Rows.Contains(row))
            {
                row.Cells["MapHold"].Value = null;
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
        _saveButton = new RoundedButton { Text = "Save and close", Variant = ButtonVariant.Primary };
        _saveButton.Click += OnSave;
        _applyButton = new RoundedButton
        {
            Text = "Apply",
            Variant = ButtonVariant.Secondary,
            Visible = _applyConfiguration is not null,
        };
        _applyButton.Click += OnApply;
        _reviewLatestButton = new RoundedButton
        {
            Text = "Review latest",
            Variant = ButtonVariant.Secondary,
            Visible = false,
        };
        _reviewLatestButton.Click += async (_, _) =>
        {
            var draft = await ProjectCurrentDraftAsync().ConfigureAwait(true);
            if (draft is not null)
            {
                ReviewLatestRequested?.Invoke(
                    this,
                    new ConfigurationDraftEventArgs(
                        draft.Companion,
                        draft.Voice,
                        draft.PebbleIndex));
            }
        };
        _discardDraftButton = new RoundedButton
        {
            Text = "Discard draft",
            Variant = ButtonVariant.Ghost,
            Visible = false,
        };
        _discardDraftButton.Click += (_, _) =>
        {
            if (MessageBox.Show(
                    this,
                    "Discard every unsaved change in this settings window and load the latest saved settings?",
                    "Discard settings draft",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) == DialogResult.Yes)
            {
                DiscardDraftRequested?.Invoke(this, EventArgs.Empty);
            }
        };
        _cancelButton = new RoundedButton
        {
            Name = "ConfigurationCancel",
            Text = "Cancel",
        };
        _cancelButton.Click += async (_, _) =>
        {
            await CancelCaptureAsync().ConfigureAwait(true);
            DialogResult = DialogResult.Cancel;
            Close();
        };
        _cancelCaptureButton.Click += async (_, _) =>
            await CancelCaptureAsync().ConfigureAwait(true);
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
        _runtimeStatus.Margin = new Padding(12, 7, 10, 0);
        captureStatus.Controls.Add(_runtimeStatus);

        var commands = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 8, 12, 0),
            WrapContents = false,
        };
        commands.Controls.Add(_saveButton);
        commands.Controls.Add(_applyButton);
        commands.Controls.Add(_reviewLatestButton);
        commands.Controls.Add(_discardDraftButton);
        commands.Controls.Add(_cancelButton);

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
        AcceptButton = _saveButton;
        CancelButton = _cancelButton;
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

    private async Task LoadStarterProfileAsync()
    {
        await CancelCaptureAsync().ConfigureAwait(true);
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
        if (_documentationMode)
        {
            var selector = new DeviceSelector
            {
                ProductNameContains = "VPC Throttle MT-50CM3",
                InstanceGuid = Guid.Empty.ToString(),
                ProductGuid = Guid.Empty.ToString(),
            };
            _availableSources =
            [
                new RuntimeInputSourceCatalogEntry(
                    new InputSourceDescriptor("documentation-cm3", "VPC Throttle MT-50CM3", Guid.Empty.ToString()),
                    selector,
                    Guid.Empty,
                    Guid.Empty,
                    CompanionConfigNormalizer.PrimaryDeviceId),
            ];
        }

        if (!_configControlsInitialized)
        {
            _configControlsInitialized = true;
            _deviceCombo.Format += (_, eventArgs) =>
            {
                if (eventArgs.ListItem is RuntimeInputSourceCatalogEntry source)
                {
                    eventArgs.Value = source.Source.DisplayName;
                }
            };
            _deviceCombo.SelectedIndexChanged += async (_, _) =>
            {
                await CancelCaptureAsync().ConfigureAwait(true);
                RefreshSelectedDeviceStatus();
            };
        }

        _deviceCombo.BeginUpdate();
        _deviceCombo.Items.Clear();
        foreach (var device in _availableSources)
        {
            _deviceCombo.Items.Add(device);
        }

        var selectedDevice = _availableSources.FirstOrDefault(device =>
            Matches(device, _originalConfig.Device));
        if (selectedDevice is not null)
        {
            _deviceCombo.SelectedItem = selectedDevice;
        }
        else if (_deviceCombo.Items.Count > 0)
        {
            _deviceCombo.SelectedIndex = 0;
        }
        _deviceCombo.EndUpdate();

        _bankGrid.Rows.Clear();
        foreach (var (bank, button) in _originalConfig.BankSelectors)
        {
            _bankGrid.Rows.Add(bank, button);
        }

        _bindingGrid.Rows.Clear();
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

        _buttonMapGrid.Rows.Clear();
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

    internal void ApplyAuthoritativeSettings(SettingsBundle settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ApplySettingsProjection(settings, settings);
    }

    internal void ApplyDraftState(RuntimeSettingsDraftState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ApplySettingsProjection(state.BaseSettings, state.DraftSettings);
        ShowDraftState(state);
    }

    private void ApplySettingsProjection(SettingsBundle authoritative, SettingsBundle visible)
    {
        _authoritativeSettings = authoritative;
        _originalConfig = CompanionConfigNormalizer.Normalize(visible.Companion);
        RoomVoicePreferences = visible.Voice.Normalize();
        PebbleIndexPreferences = visible.PebbleIndex.Normalize();
        _promptPickerEditor?.ReplaceConfiguration(_originalConfig);
        _roomVoiceSettings?.ApplyPreferences(RoomVoicePreferences);
        _pebbleIndexSettings?.ApplyPreferences(PebbleIndexPreferences);
        PopulateFromConfig();
        if (_demoMode)
        {
            _dryRunCheckBox.Checked = true;
        }
    }

    private void RefreshSelectedDeviceStatus()
    {
        _inputLabel.Text = "Held buttons: none";
        if (_deviceCombo.SelectedItem is not RuntimeInputSourceCatalogEntry selected)
        {
            _inputObservations.SelectSource(null);
            _connectionLabel.Text = "No DirectInput game controller is available.";
            return;
        }

        _inputObservations.SelectSource(selected.Source.SourceId);
        var state = _inputSession?.GetSourceState(selected.Source.SourceId);
        _connectionLabel.Text = state?.Connected == true
            ? $"Observed by Joydex: {selected.Source.DisplayName}."
            : $"Available: {selected.Source.DisplayName}. Capture will ask Joydex to observe this controller.";
    }

    private async Task RefreshInputSourcesAsync()
    {
        if (_documentationMode || _inputSession is null)
        {
            return;
        }

        try
        {
            var sources = await _inputSession.RefreshSourcesAsync(_lifetime.Token)
                .ConfigureAwait(true);
            var selectedId = (_deviceCombo.SelectedItem as RuntimeInputSourceCatalogEntry)?.Source.SourceId;
            _availableSources = sources;
            _deviceCombo.BeginUpdate();
            _deviceCombo.Items.Clear();
            foreach (var source in sources)
            {
                _deviceCombo.Items.Add(source);
            }
            _deviceCombo.SelectedItem = sources.FirstOrDefault(source =>
                string.Equals(source.Source.SourceId, selectedId, StringComparison.OrdinalIgnoreCase))
                ?? sources.FirstOrDefault(source => Matches(source, _originalConfig.Device));
            if (_deviceCombo.SelectedIndex < 0 && _deviceCombo.Items.Count > 0)
            {
                _deviceCombo.SelectedIndex = 0;
            }
            _deviceCombo.EndUpdate();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _connectionLabel.Text = $"Could not enumerate devices: {exception.Message}";
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

        var attached = _deviceCombo.Items.Cast<RuntimeInputSourceCatalogEntry>().FirstOrDefault(device =>
            Matches(device, profile.Selector));
        if (attached is null)
        {
            MessageBox.Show(this, $"{profile.DisplayName} is not attached.", "Capture control");
            return false;
        }

        if (ReferenceEquals(_deviceCombo.SelectedItem, attached))
        {
            RefreshSelectedDeviceStatus();
        }
        else
        {
            var returnDevice = _deviceCombo.SelectedItem as RuntimeInputSourceCatalogEntry;
            _deviceCombo.SelectedItem = attached;
            _captureReturnDevice = returnDevice;
        }

        return true;
    }

    private void OnInputObserved(object? sender, InputObservationEventArgs eventArgs)
    {
        if (IsDisposed || Disposing || !IsHandleCreated)
        {
            return;
        }

        if (_inputObservations.TryQueue(eventArgs.Observation))
        {
            if (!RunOnUiThread(DrainInputObservation))
            {
                _inputObservations.CancelScheduledDispatch();
            }
        }
    }

    private void DrainInputObservation()
    {
        if (_inputObservations.TakePending() is { } observation)
        {
            ApplyObservation(observation);
        }
    }

    private void ApplyObservation(InputObservation observation)
    {
        if (_deviceCombo.SelectedItem is not RuntimeInputSourceCatalogEntry selected
            || !string.Equals(
                selected.Source.SourceId,
                observation.Source.Descriptor.SourceId,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _connectionLabel.Text = $"Observed by Joydex: {observation.Source.Descriptor.DisplayName}.";
        _lastSnapshot = observation.Snapshot;
        var heldButtons = observation.Snapshot.Buttons
            .Select((pressed, index) => (pressed, button: index + 1))
            .Where(item => item.pressed)
            .Select(item => item.button)
            .Take(16)
            .ToArray();
        _inputLabel.Text = heldButtons.Length == 0
            ? "Held buttons: none"
            : $"Held buttons: {string.Join(", ", heldButtons)}";

        if (_captureTarget is null)
        {
            var pressed = observation.Events.LastOrDefault(input =>
                input.Kind == JoystickEventKind.ButtonPressed);
            if (pressed is not null)
            {
                _captureLabel.Text = $"Last pressed: button {pressed.DisplayIndex}.";
            }
        }
    }

    private void OnCaptureChanged(object? sender, InputCaptureChangedEventArgs eventArgs) =>
        _ = RunOnUiThread(() => ApplyCaptureChange(eventArgs));

    private void ApplyCaptureChange(InputCaptureChangedEventArgs eventArgs)
    {
        var current = _captureLease;
        if (current is null
            || current.CaptureId != eventArgs.Lease.CaptureId
            || eventArgs.Lease.Revision < current.Revision)
        {
            return;
        }

        _captureLease = eventArgs.Lease;
        if (eventArgs.Lease.Status == InputCaptureStatus.Active)
        {
            _captureLabel.Text = "Listening for a fresh controller button press…";
            return;
        }
        if (eventArgs.Lease.Status == InputCaptureStatus.Pending)
        {
            _captureLabel.Text = "Preparing the selected controller for capture…";
            return;
        }

        var target = _captureTarget;
        var capturedInput = eventArgs.CapturedInput;
        var completed = eventArgs.Lease.Status == InputCaptureStatus.Completed
            && capturedInput?.Kind == JoystickEventKind.ButtonPressed
            && target is not null
            && target.RowIndex >= 0
            && target.RowIndex < target.Grid.Rows.Count;
        _captureTarget = null;
        _captureLease = null;
        if (completed)
        {
            target!.Grid.Rows[target.RowIndex].Cells[target.ColumnName].Value = capturedInput!.DisplayIndex;
            target.Grid.CurrentCell = target.Grid.Rows[target.RowIndex].Cells[target.ColumnName];
            _captureLabel.Text = $"Captured button {capturedInput.DisplayIndex}.";
        }
        else
        {
            _captureLabel.Text = eventArgs.Detail ?? CaptureStatusText(eventArgs.Lease.Status);
        }
        UpdateCaptureButtons();
        RestoreDeviceAfterCapture();
    }

    private void BeginCapture(DataGridView grid, string columnName, string instruction)
    {
        if (CaptureIsInProgress())
        {
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _captureStartCancellation = cancellation;
        var task = BeginCaptureCoreAsync(grid, columnName, instruction, cancellation.Token);
        _captureStartTask = task;
        _ = ObserveCaptureStartAsync(task, cancellation);
    }

    private async Task ObserveCaptureStartAsync(
        Task task,
        CancellationTokenSource cancellation)
    {
        try
        {
            await task.ConfigureAwait(true);
        }
        finally
        {
            if (ReferenceEquals(_captureStartTask, task))
            {
                _captureStartTask = null;
            }
            if (ReferenceEquals(_captureStartCancellation, cancellation))
            {
                _captureStartCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private async Task BeginCaptureCoreAsync(
        DataGridView grid,
        string columnName,
        string instruction,
        CancellationToken cancellationToken)
    {
        if (grid.CurrentRow is null)
        {
            MessageBox.Show(this, "Select a row first.", "Capture control", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_inputSession is null
            || _deviceCombo.SelectedItem is not RuntimeInputSourceCatalogEntry source)
        {
            MessageBox.Show(this, "No Joydex input source is available.", "Capture control");
            return;
        }

        _captureTarget = new CaptureTarget(grid, grid.CurrentRow.Index, columnName);
        var state = _inputSession.GetSourceState(source.Source.SourceId);
        InputCaptureStartResult result;
        try
        {
            result = await _inputSession.BeginCaptureAsync(
                    source.Source.SourceId,
                    instruction,
                    state?.Connected == true ? state.Generation : null,
                    cancellationToken)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _captureTarget = null;
            UpdateCaptureButtons();
            RestoreDeviceAfterCapture();
            return;
        }
        catch (Exception exception)
        {
            _captureTarget = null;
            _captureLabel.Text = "Capture could not start: " + exception.Message;
            UpdateCaptureButtons();
            RestoreDeviceAfterCapture();
            return;
        }
        if (!result.Accepted || result.Lease is null)
        {
            _captureTarget = null;
            _captureLabel.Text = result.Error ?? "Capture could not start.";
            UpdateCaptureButtons();
            RestoreDeviceAfterCapture();
            return;
        }

        _captureLease = result.Lease;
        _captureLabel.Text = "Preparing the selected controller for capture…";
        UpdateCaptureButtons();
        if (result.Lease.Status is not (InputCaptureStatus.Pending or InputCaptureStatus.Active))
        {
            InputCaptureChangedEventArgs? terminal;
            try
            {
                terminal = await _inputSession.GetCaptureAsync(
                        result.Lease.CaptureId,
                        cancellationToken)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _captureTarget = null;
                _captureLease = null;
                UpdateCaptureButtons();
                RestoreDeviceAfterCapture();
                return;
            }
            catch (Exception exception)
            {
                _captureTarget = null;
                _captureLease = null;
                _captureLabel.Text = "Capture status could not be confirmed: " + exception.Message;
                UpdateCaptureButtons();
                RestoreDeviceAfterCapture();
                return;
            }
            if (terminal is not null)
            {
                ApplyCaptureChange(terminal);
            }
            else
            {
                _captureTarget = null;
                _captureLease = null;
                _captureLabel.Text = CaptureStatusText(result.Lease.Status);
                UpdateCaptureButtons();
                RestoreDeviceAfterCapture();
            }
        }
    }

    private async Task CancelCaptureAsync()
    {
        _captureStartCancellation?.Cancel();
        var captureStart = _captureStartTask;
        if (captureStart is not null)
        {
            try
            {
                await captureStart.ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                _captureLabel.Text = "Capture stopped: " + exception.Message;
            }
        }

        var capture = _captureLease;
        var target = _captureTarget;
        _captureLease = null;
        _captureTarget = null;
        if (capture is not null)
        {
            if (_inputSession is not null)
            {
                try
                {
                    _ = await _inputSession.CancelCaptureAsync(
                            capture.CaptureId,
                            CancellationToken.None)
                        .ConfigureAwait(true);
                }
                catch (Exception exception)
                {
                    _captureLabel.Text = "Capture cancellation could not be confirmed: " + exception.Message;
                }
            }
        }
        if (capture is not null || target is not null)
        {
            _captureLabel.Text = "Capture cancelled.";
        }
        UpdateCaptureButtons();
        RestoreDeviceAfterCapture();
    }

    private bool CaptureIsInProgress() =>
        _captureTarget is not null
        || _captureLease is not null
        || _captureStartTask is { IsCompleted: false }
        || _promptPickerEditor?.CaptureIsInProgress == true;

    private async Task CancelCaptureAndCloseAsync()
    {
        await QuiesceCapturesAsync().ConfigureAwait(true);
        if (IsDisposed || Disposing)
        {
            return;
        }

        _closingAfterCaptureCancellation = true;
        Close();
    }

    internal async Task QuiesceCapturesAsync()
    {
        await CancelCaptureAsync().ConfigureAwait(true);
        if (_promptPickerEditor is not null)
        {
            await _promptPickerEditor.QuiesceCaptureAsync().ConfigureAwait(true);
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
        var canStart = _captureTarget is null
            && !_settingsOperationActive
            && (_applyConfiguration is null || _runtimeConnected);
        _captureBankButton.Enabled = canStart;
        _captureBindingButton.Enabled = canStart;
        _captureMapHoldButton.Enabled = canStart;
        _cancelCaptureButton.Visible = _captureTarget is not null;
    }

    private bool RunOnUiThread(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated)
        {
            return false;
        }

        if (!InvokeRequired)
        {
            action();
            return true;
        }

        try
        {
            BeginInvoke(() =>
            {
                if (!IsDisposed && !Disposing)
                {
                    action();
                }
            });
            return true;
        }
        catch (InvalidOperationException) when (IsDisposed || Disposing || !IsHandleCreated)
        {
            return false;
        }
    }

    private static string CaptureStatusText(InputCaptureStatus status) => status switch
    {
        InputCaptureStatus.Cancelled => "Capture cancelled.",
        InputCaptureStatus.TimedOut => "Capture timed out.",
        InputCaptureStatus.ClientDisconnected => "Capture window disconnected.",
        InputCaptureStatus.SourceDisconnected => "The selected controller disconnected.",
        InputCaptureStatus.GenerationChanged => "The selected controller reconnected. Start capture again.",
        InputCaptureStatus.Failed => "Capture failed.",
        _ => "Capture ended.",
    };

    private static bool Matches(RuntimeInputSourceCatalogEntry source, DeviceSelector selector)
    {
        if (Guid.TryParse(selector.InstanceGuid, out var instanceGuid))
        {
            return source.InstanceGuid == instanceGuid;
        }

        return string.IsNullOrWhiteSpace(selector.ProductNameContains)
            || source.Source.DisplayName.Contains(
                selector.ProductNameContains,
                StringComparison.OrdinalIgnoreCase);
    }

    private async Task RemoveCurrentRowAsync(DataGridView grid)
    {
        var row = grid.CurrentRow;
        await CancelCaptureAsync().ConfigureAwait(true);
        if (row is not null && grid.Rows.Contains(row))
        {
            grid.Rows.Remove(row);
        }
    }

    private async void OnSave(object? sender, EventArgs eventArgs) =>
        await SubmitAsync(closeAfterSuccess: true).ConfigureAwait(true);

    private async void OnApply(object? sender, EventArgs eventArgs) =>
        await SubmitAsync(closeAfterSuccess: false).ConfigureAwait(true);

    internal async Task<SettingsBundle?> ProjectCurrentDraftAsync()
    {
        _lastProjectedDraft = null;
        await SubmitAsync(closeAfterSuccess: false, projectOnly: true).ConfigureAwait(true);
        return _lastProjectedDraft;
    }

    private async Task SubmitAsync(bool closeAfterSuccess, bool projectOnly = false)
    {
        await QuiesceCapturesAsync().ConfigureAwait(true);
        IReadOnlyList<PromptPickerConfig>? promptPickers = null;
        if (_promptPickerEditor is not null)
        {
            promptPickers = await _promptPickerEditor.ProjectPromptPickersAsync()
                .ConfigureAwait(true);
        }
        _bankGrid.EndEdit();
        _bindingGrid.EndEdit();
        _buttonMapGrid.EndEdit();

        var roomVoicePreferences = _roomVoiceSettings?.ReadPreferences();
        var roomVoiceErrors = roomVoicePreferences?.Validate() ?? [];
        if (roomVoiceErrors.Count > 0)
        {
            SelectPage("Room Voice");
            MessageBox.Show(
                this,
                string.Join(Environment.NewLine, roomVoiceErrors.Select(error => $"- {error}")),
                "Room Voice settings need attention",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }
        var pebbleIndexPreferences = _pebbleIndexSettings?.ReadPreferences();
        var pebbleIndexErrors = pebbleIndexPreferences?.Validate() ?? [];
        if (pebbleIndexErrors.Count > 0)
        {
            SelectPage("Pebble Index");
            MessageBox.Show(this,
                string.Join(Environment.NewLine, pebbleIndexErrors.Select(error => $"- {error}")),
                "Pebble Index settings need attention", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

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

        var device = _deviceCombo.SelectedItem is RuntimeInputSourceCatalogEntry selected
            ? selected.Selector
            : _originalConfig.Device;
        var simulatorProcesses = _simulatorProcessesTextBox.Text
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(process => !string.IsNullOrWhiteSpace(process))
            .Select(process => process!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var devices = _originalConfig.Devices.Select((profile, index) =>
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
        }).ToList();
        var effectivePromptPickers = promptPickers?.ToList()
            ?? _originalConfig.PromptPickers;
        if (_promptPickerEditor is not null)
        {
            devices = MergePromptPickerDevices(
                devices,
                _promptPickerEditor.GetDeviceProfiles(),
                effectivePromptPickers);
        }

        var config = new CompanionConfig
        {
            Device = device,
            Devices = devices,
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
            PromptPickers = effectivePromptPickers,
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

        _lastProjectedDraft = _authoritativeSettings with
        {
            Companion = config,
            Voice = roomVoicePreferences ?? _authoritativeSettings.Voice,
            PebbleIndex = pebbleIndexPreferences ?? _authoritativeSettings.PebbleIndex,
        };
        if (projectOnly)
        {
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
            if (_applyConfiguration is null)
            {
                if (!_saveConfiguration(config, roomVoicePreferences, pebbleIndexPreferences))
                {
                    return;
                }
                RoomVoicePreferences = roomVoicePreferences;
                PebbleIndexPreferences = pebbleIndexPreferences;
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            SetSettingsOperationActive(active: true);
            var result = await _applyConfiguration(
                    config,
                    roomVoicePreferences,
                    pebbleIndexPreferences,
                    _lifetime.Token)
                .ConfigureAwait(true);
            ShowRuntimeResult(result);
            if (result.Outcome is RuntimeSettingsWriteOutcome.NoChanges
                or RuntimeSettingsWriteOutcome.Applied
                or RuntimeSettingsWriteOutcome.PendingIdle)
            {
                ApplyAuthoritativeSettings(result.Snapshot.Desired);
                if (closeAfterSuccess)
                {
                    DialogResult = DialogResult.OK;
                    Close();
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (_applyConfiguration is null)
            {
                MessageBox.Show(this, exception.Message, "Could not save configuration", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            else
            {
                _runtimeStatus.Text = "The runtime connection changed. Choose Check status after it reconnects. "
                    + exception.Message;
                _runtimeStatus.Visible = true;
                if (_applyButton is not null)
                {
                    _applyButton.Text = "Check status";
                }
            }
        }
        finally
        {
            if (!IsDisposed && !Disposing)
            {
                SetSettingsOperationActive(active: false);
            }
        }
    }

    private void SetSettingsOperationActive(bool active)
    {
        _settingsOperationActive = active;
        UpdateRuntimeEditingState();
        if (active)
        {
            _runtimeStatus.Text = "Applying settings…";
            _runtimeStatus.Visible = true;
            _reviewLatestButton!.Visible = false;
            _discardDraftButton!.Visible = false;
        }
    }

    internal void SetRuntimeConnectionAvailable(bool connected, string? detail = null)
    {
        if (_applyConfiguration is null)
        {
            return;
        }

        _runtimeConnected = connected;
        _runtimeStatus.Text = connected
            ? detail ?? "Connected to the Joydex runtime."
            : detail ?? "The Joydex runtime connection is unavailable. Your draft is still open.";
        _runtimeStatus.Visible = true;
        UpdateRuntimeEditingState();
    }

    internal void ShowDraftState(RuntimeSettingsDraftState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _draftAttention = state.RequiresRebase || state.PendingOperationId is not null
            ? state.Detail ?? (state.RequiresRebase
                ? "Settings changed while this draft was open. Review the latest settings before applying."
                : "The settings operation is still running. Choose Check status to reconcile it.")
            : null;
        RenderSettingsAttention();
        if (_reviewLatestButton is not null)
        {
            _reviewLatestButton.Visible = state.RequiresRebase;
        }
        if (_discardDraftButton is not null)
        {
            _discardDraftButton.Visible = state.RequiresRebase;
        }
        if (_applyButton is not null)
        {
            _applyButton.Text = state.PendingOperationId is null ? "Apply" : "Check status";
        }
        UpdateRuntimeEditingState();
    }

    internal void ShowRuntimeSettingsStatus(SettingsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var attention = snapshot.Aggregates
            .Where(aggregate => aggregate.Activation != SettingsActivationState.Applied
                || aggregate.DesiredRevision != aggregate.ActiveRevision)
            .Select(aggregate => aggregate.Activation switch
            {
                SettingsActivationState.PendingIdle =>
                    $"{aggregate.Aggregate}: saved and waiting for the current runtime owner to become idle."
                    + FormatAggregateDetail(aggregate.Detail),
                SettingsActivationState.Failed =>
                    $"{aggregate.Aggregate}: saved settings are not active."
                    + FormatAggregateDetail(aggregate.Detail),
                _ =>
                    $"{aggregate.Aggregate}: saved revision {aggregate.DesiredRevision} differs from active revision {aggregate.ActiveRevision}."
                    + FormatAggregateDetail(aggregate.Detail),
            })
            .ToArray();
        _aggregateAttention = attention.Length == 0
            ? null
            : string.Join(Environment.NewLine, attention);
        RenderSettingsAttention();
    }

    private void RenderSettingsAttention()
    {
        var attention = new[] { _draftAttention, _aggregateAttention }
            .Where(detail => !string.IsNullOrWhiteSpace(detail))
            .ToArray();
        _runtimeStatus.Text = string.Join(Environment.NewLine, attention!);
        _runtimeStatus.Visible = attention.Length > 0;
    }

    private static string FormatAggregateDetail(string? detail) =>
        string.IsNullOrWhiteSpace(detail) ? string.Empty : " " + detail.Trim();

    private void UpdateRuntimeEditingState()
    {
        var editable = !_settingsOperationActive;
        var canContactRuntime = editable
            && (_applyConfiguration is null || _runtimeConnected);
        if (_navigation is not null)
        {
            _navigation.Enabled = editable;
        }
        _pageHost.Enabled = editable;
        if (_applyButton is not null)
        {
            _applyButton.Enabled = canContactRuntime;
        }
        if (_saveButton is not null)
        {
            _saveButton.Enabled = canContactRuntime;
        }
        if (_reviewLatestButton is not null)
        {
            _reviewLatestButton.Enabled = canContactRuntime;
        }
        if (_discardDraftButton is not null)
        {
            _discardDraftButton.Enabled = canContactRuntime;
        }
        UpdateCaptureButtons();
    }

    private void ShowRuntimeResult(RuntimeSettingsWriteResult result)
    {
        _runtimeStatus.Text = result.Detail;
        _runtimeStatus.Visible = true;
        var conflict = result.Outcome == RuntimeSettingsWriteOutcome.Conflict;
        _reviewLatestButton!.Visible = conflict;
        _discardDraftButton!.Visible = conflict;
        if (_applyButton is not null)
        {
            _applyButton.Text = result.Outcome is RuntimeSettingsWriteOutcome.Running
                or RuntimeSettingsWriteOutcome.Uncertain
                ? "Check status"
                : "Apply";
        }
    }

    internal VoicePePreferences? RoomVoicePreferences { get; private set; }
    internal PebbleIndexPreferences? PebbleIndexPreferences { get; private set; }

    internal event EventHandler<ConfigurationDraftEventArgs>? ReviewLatestRequested;

    internal event EventHandler? DiscardDraftRequested;

    internal static List<DeviceProfile> MergePromptPickerDevices(
        IReadOnlyList<DeviceProfile> configuredDevices,
        IReadOnlyList<DeviceProfile> editorDevices,
        IReadOnlyList<PromptPickerConfig> promptPickers)
    {
        var referencedDeviceIds = promptPickers
            .SelectMany(picker => new[]
            {
                picker.Controls.Up.DeviceId,
                picker.Controls.Down.DeviceId,
                picker.Controls.Insert.DeviceId,
            })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var merged = configuredDevices.ToList();
        foreach (var device in editorDevices)
        {
            if (referencedDeviceIds.Contains(device.Id)
                && !merged.Any(existing => string.Equals(
                    existing.Id,
                    device.Id,
                    StringComparison.OrdinalIgnoreCase)))
            {
                merged.Add(device);
            }
        }

        return merged;
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
