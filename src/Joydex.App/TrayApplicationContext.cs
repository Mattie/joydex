using System.Diagnostics;
using Joydex.Core.Config;
using Joydex.Core.Mapping;
using Joydex.Core.Runtime;
using Joydex.Core.TaskAlerts;
using Joydex.Windows.Actions;
using Joydex.Windows.Input;
using Joydex.Windows.Interop;
using Joydex.Windows.Runtime;
using Joydex.Windows.TaskAlerts;
using Microsoft.Win32;

namespace Joydex.App;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly string _configPath;
    private readonly string _windowStatePath;
    private readonly string _buttonMapStatePath;
    private readonly FileLog _log;
    private readonly CodexKeybindingService _keybindingService;
    private readonly CooperativeWindow _cooperativeWindow;
    private readonly Icon _appIcon;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _controllersMenu;
    private readonly ToolStripMenuItem _modeItem;
    private readonly ToolStripMenuItem _testControlsItem;
    private readonly ToolStripMenuItem _testingAdvancedMenu;
    private readonly ToolStripMenuItem _promptPickersItem;
    private readonly ToolStripMenuItem _configureItem;
    private readonly ToolStripMenuItem _taskAlertsItem;
    private readonly ToolStripMenuItem _taskAlertsStatusItem;
    private readonly SynchronizationContext _uiContext;
    private readonly TaskAlertCoordinator _taskAlerts;
    private readonly TaskAlertPipeServer _taskAlertPipe;
    private readonly VirpilShiftModeMonitor _shiftModeMonitor;
    private readonly LinkToolLedService _ledService;
    private readonly CodexHookManager _hookManager;
    private readonly string _hookRelayPath;
    private readonly string _linkToolProfilePath;
    private readonly GuardianController _guardian;
    private readonly DeviceChangeMonitor _deviceChangeMonitor;
    private readonly Queue<string> _recentActivity = new();
    private readonly Dictionary<string, CompanionWorker> _workers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _deviceStatuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ToolStripMenuItem> _controllerItems = new(StringComparer.OrdinalIgnoreCase);
    private CompanionConfig? _activeConfig;
    private DryRunActivityForm? _activityForm;
    private readonly Dictionary<string, ButtonMapForm> _buttonMapForms = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ToolStripMenuItem> _buttonMapItems = new(StringComparer.OrdinalIgnoreCase);
    private PromptPickerCoordinator? _promptPicker;
    private PromptPickerOverlayForm? _promptOverlay;
    private TaskAlertsForm? _taskAlertsForm;
    private bool _configuring;

    public TrayApplicationContext(string configPath)
    {
        _configPath = configPath;
        var existingCompanionInstall = ConfigPathResolver.HasExistingInstallation(
            configPath,
            CodexKeybindingService.DefaultProvisioningStatePath);
        var dataDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath))
            ?? throw new InvalidOperationException("The configuration path has no parent directory.");
        _windowStatePath = Path.Combine(dataDirectory, "configuration-window.json");
        _buttonMapStatePath = Path.Combine(dataDirectory, "button-map-window.json");
        _log = new FileLog(Path.Combine(dataDirectory, "joydex.log"));
        _keybindingService = CodexKeybindingService.CreateDefault(_log.Write, existingCompanionInstall);
        _keybindingService.InitializeAsync().GetAwaiter().GetResult();
        _cooperativeWindow = new CooperativeWindow("Joydex");
        _appIcon = AppIconFactory.Create();
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _taskAlerts = new TaskAlertCoordinator(
            Path.Combine(dataDirectory, "task-alerts.json"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Joydex",
                "task-alert-state.json"),
            _log.Write);
        _taskAlertPipe = new TaskAlertPipeServer(_taskAlerts, _log.Write);
        _shiftModeMonitor = new VirpilShiftModeMonitor(
            new VirpilShiftModeReader(),
            _taskAlerts.SetDetectedBank,
            _log.Write);
        var initialTaskAlerts = _taskAlerts.GetSnapshot();
        _linkToolProfilePath = Path.Combine(dataDirectory, "joydex-linktool.led.json");
        try
        {
            LinkToolProfileWriter.Write(_linkToolProfilePath);
            _log.Write($"Joydex LinkTool profile written to {_linkToolProfilePath}.");
        }
        catch (Exception exception)
        {
            _log.Write($"Could not write the Joydex LinkTool profile: {exception.Message}");
        }

        _ledService = new LinkToolLedService(
            new UdpLinkToolTelemetrySender(),
            new VpcConflictDetector(),
            _log.Write,
            initialTaskAlerts);
        _hookManager = new CodexHookManager(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex",
            "hooks.json"));
        _hookRelayPath = Path.Combine(AppContext.BaseDirectory, "Joydex.HookRelay.exe");
        _guardian = new GuardianController(
            Path.Combine(AppContext.BaseDirectory, "Joydex.Guardian.exe"),
            _log.Write);
        _deviceChangeMonitor = new DeviceChangeMonitor();
        _deviceChangeMonitor.DevicesChanged += OnDevicesChanged;

        _controllersMenu = new ToolStripMenuItem("Controllers: Starting…") { Enabled = false };
        _modeItem = new ToolStripMenuItem("Dry run", image: null, OnToggleDryRun)
        {
            CheckOnClick = false,
            Enabled = false,
        };
        _testControlsItem = new ToolStripMenuItem("Test controls…", image: null, OnTestControls);
        _configureItem = new ToolStripMenuItem("Configure…", image: null, OnConfigure);
        _promptPickersItem = new ToolStripMenuItem("Prompt pickers...", image: null, OnPromptPickers);
        _taskAlertsItem = new ToolStripMenuItem("Task alerts", image: null, OnToggleTaskAlerts)
        {
            CheckOnClick = false,
            Checked = _taskAlerts.GetSnapshot().Enabled,
        };
        _taskAlertsStatusItem = new ToolStripMenuItem("Task alerts status...", image: null, OnTaskAlertsStatus);
        var reloadItem = new ToolStripMenuItem("Reload configuration", image: null, OnReloadConfig);
        var openConfigItem = new ToolStripMenuItem("Open config JSON...", image: null, (_, _) => OpenPath(_configPath));
        var openLogItem = new ToolStripMenuItem("Open log", image: null, (_, _) => OpenPath(_log.Path));
        var exitItem = new ToolStripMenuItem("Exit", image: null, (_, _) => ExitThread());
        _testingAdvancedMenu = new ToolStripMenuItem("Testing / Advanced");
        _testingAdvancedMenu.DropDownItems.AddRange([
            _modeItem,
            _testControlsItem,
            new ToolStripSeparator(),
            _taskAlertsStatusItem,
            reloadItem,
            openConfigItem,
            openLogItem,
        ]);

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = new ContextMenuStrip
            {
                Items =
                {
                    _controllersMenu,
                    _taskAlertsItem,
                    new ToolStripSeparator(),
                    _configureItem,
                    _promptPickersItem,
                    new ToolStripSeparator(),
                    _testingAdvancedMenu,
                    new ToolStripSeparator(),
                    exitItem,
                },
            },
            Icon = _appIcon,
            Text = "Joydex",
            Visible = true,
        };
        _notifyIcon.DoubleClick += OnConfigure;

        _taskAlerts.Changed += OnTaskAlertsChanged;
        _ledService.StatusChanged += OnLedStatusChanged;
        _ledService.ProfileDirtyChanged += OnProfileDirtyChanged;
        if (initialTaskAlerts.Enabled && initialTaskAlerts.Assignments.Count > 0)
        {
            _guardian.Start();
            _guardian.SetRestoreRequired(true);
            _ledService.RestoreAndReplay(replay: true);
        }
        else
        {
            _ledService.Apply(initialTaskAlerts);
        }

        _shiftModeMonitor.Start();
        _taskAlertPipe.Start();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionEnding += OnSessionEnding;

        var firstRun = !File.Exists(_configPath);
        StartWorker(showFirstRunNotice: firstRun);
        if (firstRun)
        {
            _uiContext.Post(_ => OnConfigure(this, EventArgs.Empty), null);
        }
        else if (_activeConfig?.Safety.DryRun == true)
        {
            _uiContext.Post(_ => OnTestControls(this, EventArgs.Empty), null);
        }
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionEnding -= OnSessionEnding;
        _deviceChangeMonitor.DevicesChanged -= OnDevicesChanged;
        _deviceChangeMonitor.Dispose();
        _taskAlertsForm?.Close();
        _taskAlertsForm = null;
        _activityForm?.Close();
        _activityForm = null;
        foreach (var form in _buttonMapForms.Values)
        {
            form.SaveWindowState();
            form.Dispose();
        }
        _buttonMapForms.Clear();
        _promptOverlay?.Dispose();
        _promptOverlay = null;

        foreach (var worker in _workers.Values)
        {
            worker.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        _workers.Clear();

        _shiftModeMonitor.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _taskAlertPipe.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _taskAlerts.Changed -= OnTaskAlertsChanged;
        _ledService.StatusChanged -= OnLedStatusChanged;
        _ledService.ProfileDirtyChanged -= OnProfileDirtyChanged;
        _ledService.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (!_ledService.RestorePending)
        {
            _guardian.SignalCleanExit();
        }

        _guardian.Dispose();
        _taskAlerts.DisposeAsync().AsTask().GetAwaiter().GetResult();

        _keybindingService.DisposeAsync().AsTask().GetAwaiter().GetResult();

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _appIcon.Dispose();
        _cooperativeWindow.Dispose();
        base.ExitThreadCore();
    }

    private async void OnReloadConfig(object? sender, EventArgs eventArgs)
    {
        try
        {
            CloseActivityForm();
            HideAllButtonMaps();
            _promptPicker?.Dismiss();
            _recentActivity.Clear();
            await StopWorkersAsync();

            StartWorker(showFirstRunNotice: false);
            if (_activeConfig?.Safety.DryRun == true)
            {
                OnTestControls(this, EventArgs.Empty);
            }
        }
        catch (Exception exception)
        {
            HandleConfigurationError(exception);
        }
    }

    private async void OnToggleDryRun(object? sender, EventArgs eventArgs)
    {
        if (_configuring || _activeConfig is null)
        {
            return;
        }

        _modeItem.Enabled = false;
        try
        {
            var current = _activeConfig;
            var enableDryRun = !current.Safety.DryRun;
            var updated = new CompanionConfig
            {
                Device = current.Device,
                Devices = current.Devices,
                Polling = current.Polling,
                Safety = new SafetyOptions
                {
                    DryRun = enableDryRun,
                    RequireCodexForeground = current.Safety.RequireCodexForeground,
                    CodexProcessNames = current.Safety.CodexProcessNames,
                    SimulatorProcessNames = current.Safety.SimulatorProcessNames,
                },
                OpenWorkingDirectory = current.OpenWorkingDirectory,
                BankSelectors = current.BankSelectors,
                Bindings = current.Bindings,
                PromptPickers = current.PromptPickers,
            };

            ConfigStore.Save(_configPath, updated);
            CloseActivityForm();
            HideAllButtonMaps();
            _promptPicker?.Dismiss();
            _recentActivity.Clear();
            await StopWorkersAsync();

            StartWorker(showFirstRunNotice: false);
            if (enableDryRun && _activeConfig?.Safety.DryRun == true)
            {
                OnTestControls(this, EventArgs.Empty);
            }
        }
        catch (Exception exception)
        {
            HandleConfigurationError(exception);
        }
        finally
        {
            _modeItem.Enabled = _activeConfig is not null;
        }
    }

    private async void OnConfigure(object? sender, EventArgs eventArgs)
    {
        if (_configuring)
        {
            return;
        }

        _configuring = true;
        _configureItem.Enabled = false;
        _modeItem.Enabled = false;
        try
        {
            CloseActivityForm();
            HideAllButtonMaps();
            _promptPicker?.Dismiss();
            _recentActivity.Clear();
            await StopWorkersAsync();

            using var form = new ConfigurationForm(_configPath, _windowStatePath, _cooperativeWindow.Handle);
            var result = form.ShowDialog();
            StartWorker(showFirstRunNotice: false);
            if (result == DialogResult.OK)
            {
                _notifyIcon.ShowBalloonTip(
                    4000,
                    "Joydex configuration saved",
                    "The throttle mapping has been reloaded.",
                    ToolTipIcon.Info);

                if (_activeConfig?.Safety.DryRun == true)
                {
                    OnTestControls(this, EventArgs.Empty);
                }
            }
        }
        catch (Exception exception)
        {
            HandleConfigurationError(exception);
        }
        finally
        {
            if (_workers.Count == 0)
            {
                StartWorker(showFirstRunNotice: false);
            }

            _configuring = false;
            _configureItem.Enabled = true;
            _modeItem.Enabled = _activeConfig is not null;
        }
    }

    private void StartWorker(bool showFirstRunNotice)
    {
        try
        {
            var config = ConfigStore.LoadOrCreate(_configPath);
            DisposeStaleButtonMaps(_activeConfig, config);
            _activeConfig = config;
            _modeItem.Text = "Dry run";
            _modeItem.Checked = config.Safety.DryRun;
            _modeItem.Enabled = true;
            _modeItem.ForeColor = SystemColors.ControlText;
            _testControlsItem.Enabled = config.Safety.DryRun;
            _testingAdvancedMenu.Text = config.Safety.DryRun
                ? "Testing / Advanced (DRY RUN)"
                : "Testing / Advanced";
            ConfigureControllersMenu(config);
            foreach (var form in _buttonMapForms.Values)
            {
                form.UpdateConfig(config);
                form.UpdateTaskAlerts(_taskAlerts.GetSnapshot().Assignments);
            }

            var promptSubmitExecutor = new CodexActionExecutor(
                config.Safety,
                WriteActivity,
                _keybindingService,
                config.OpenWorkingDirectory);
            _promptPicker = new PromptPickerCoordinator(
                config,
                WriteActivity,
                _uiContext,
                submit: async (request, cancellationToken) =>
                {
                    await promptSubmitExecutor.ExecuteAsync(
                        new ActionRequest(
                            "Prompt picker submit",
                            CompanionConfig.AlwaysBank,
                            request.Button,
                            "press",
                            CodexAction.Submit,
                            DateTimeOffset.UtcNow,
                            DeviceId: request.DeviceId),
                        cancellationToken).ConfigureAwait(false);
                });
            _promptOverlay ??= new PromptPickerOverlayForm(() => _promptPicker?.CodexStillForeground() == true);
            _promptOverlay.DismissRequested -= OnPromptOverlayDismissRequested;
            _promptOverlay.DismissRequested += OnPromptOverlayDismissRequested;
            _promptPicker.Changed += (_, snapshot) => _promptOverlay.Apply(snapshot);

            var taskAlertNavigator = new TaskDeepLinkNavigator(config.Safety, WriteActivity);
            foreach (var device in config.Devices)
            {
                var source = new DirectInputJoystickSource(_cooperativeWindow.Handle);
                var executor = new CodexActionExecutor(
                    config.Safety,
                    WriteActivity,
                    _keybindingService,
                    config.OpenWorkingDirectory,
                    internalAction: OnInternalAction);
                var isCm3 = string.Equals(device.ButtonMapTemplate, "cm3", StringComparison.OrdinalIgnoreCase);
                var taskAlertInput = isCm3
                    ? new TaskAlertInputInterceptor(() => _taskAlerts.GetSnapshot().Assignments)
                    : null;
                var worker = new CompanionWorker(
                    config,
                    source,
                    executor,
                    WriteActivity,
                    taskAlertInputInterceptor: taskAlertInput,
                    taskAlertNavigator: isCm3 ? taskAlertNavigator : null,
                    acknowledgeTerminalTaskAlert: isCm3 ? _taskAlerts.AcknowledgeTerminal : null,
                    deviceId: device.Id,
                    promptPickerHandler: _promptPicker.HandleAsync,
                    buttonMapHandler: OnButtonMapVisibility);
                worker.StatusChanged += (_, status) => OnDeviceStatusChanged(device.Id, status);
                _workers[device.Id] = worker;
                worker.Start();
            }

            if (showFirstRunNotice)
            {
                _notifyIcon.ShowBalloonTip(
                    5000,
                    "Joydex is in dry-run mode",
                    "Use Test controls to see each mapped throttle press.",
                    ToolTipIcon.Info);
            }
        }
        catch (Exception exception)
        {
            HandleConfigurationError(exception);
        }
    }

    private void OnDeviceStatusChanged(string deviceId, string status)
    {
        _uiContext.Post(_ =>
        {
            if (_activeConfig?.Devices.Any(device =>
                    string.Equals(device.Id, deviceId, StringComparison.OrdinalIgnoreCase)) != true)
            {
                return;
            }

            _deviceStatuses[deviceId] = status;
            UpdateControllerItem(deviceId);
            UpdateControllerSummary();
        }, null);
    }

    private void OnTestControls(object? sender, EventArgs eventArgs)
    {
        if (_activeConfig?.Safety.DryRun != true)
        {
            MessageBox.Show(
                "Enable Dry run in Configure before testing controls.",
                "Test Joydex",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        if (_activityForm is { IsDisposed: false })
        {
            _activityForm.Show();
            _activityForm.BringToFront();
            _activityForm.Activate();
            return;
        }

        _activityForm = new DryRunActivityForm(_activeConfig);
        _activityForm.FormClosed += (_, _) => _activityForm = null;
        foreach (var message in _recentActivity)
        {
            _activityForm.Append(message);
        }

        _activityForm.SetConnectionStatus(_controllersMenu.Text ?? "Controllers: Starting...");
        _activityForm.Show();
    }

    private void OnShowControllerMap(object? sender, EventArgs eventArgs)
    {
        if (sender is not ToolStripMenuItem { Tag: string deviceId })
        {
            return;
        }

        ShowButtonMap(deviceId);
    }

    private void OnInternalAction(ActionRequest request)
    {
        _uiContext.Post(_ =>
        {
            if (string.Equals(request.Trigger, "release", StringComparison.OrdinalIgnoreCase))
            {
                HideButtonMap(request.DeviceId);
            }
            else
            {
                ShowButtonMap(request.DeviceId);
            }
        }, null);
    }

    private void ShowButtonMap(string deviceId)
    {
        if (_activeConfig is null)
        {
            return;
        }

        try
        {
            var device = _activeConfig.Devices.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, deviceId, StringComparison.OrdinalIgnoreCase));
            if (device?.ButtonMapTemplate is null)
            {
                return;
            }

            if (!_buttonMapForms.TryGetValue(deviceId, out var form) || form.IsDisposed)
            {
                var statePath = GetButtonMapStatePath(deviceId);
                form = new ButtonMapForm(_activeConfig, deviceId, statePath, _log.Write);
                form.UpdateTaskAlerts(_taskAlerts.GetSnapshot().Assignments);
                var capturedId = deviceId;
                form.VisibleChanged += (_, _) =>
                {
                    if (_buttonMapItems.TryGetValue(capturedId, out var item))
                    {
                        item.Checked = _buttonMapForms.TryGetValue(capturedId, out var current) && current.Visible;
                    }
                };
                _buttonMapForms[deviceId] = form;
            }

            form.UpdateConfig(_activeConfig);
            form.UpdateTaskAlerts(_taskAlerts.GetSnapshot().Assignments);
            form.ShowReference();
            if (_buttonMapItems.TryGetValue(deviceId, out var menuItem))
            {
                menuItem.Checked = true;
            }
        }
        catch (Exception exception)
        {
            _log.Write($"Could not show the button map: {exception.Message}");
            _notifyIcon.ShowBalloonTip(
                5000,
                "Joydex button map",
                exception.Message,
                ToolTipIcon.Error);
        }
    }

    private void HideButtonMap(string deviceId)
    {
        if (_buttonMapForms.TryGetValue(deviceId, out var form))
        {
            form.HideReference();
        }
        if (_buttonMapItems.TryGetValue(deviceId, out var item))
        {
            item.Checked = false;
        }
    }

    private string GetButtonMapStatePath(string deviceId)
    {
        var dataDirectory = Path.GetFullPath(Path.GetDirectoryName(_buttonMapStatePath)!);
        var statePath = Path.GetFullPath(Path.Combine(dataDirectory, $"button-map-{deviceId}-window.json"));
        var directoryPrefix = dataDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!statePath.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Device ID '{deviceId}' produced an unsafe button-map state path.");
        }

        return statePath;
    }

    private void HideAllButtonMaps()
    {
        foreach (var deviceId in _buttonMapForms.Keys.ToArray())
        {
            HideButtonMap(deviceId);
        }
    }

    private void ConfigureControllersMenu(CompanionConfig config)
    {
        _controllersMenu.DropDownItems.Clear();
        _controllerItems.Clear();
        _buttonMapItems.Clear();
        _deviceStatuses.Clear();

        _controllersMenu.DropDownItems.Add(new ToolStripMenuItem("Select a controller to show its map")
        {
            Enabled = false,
        });
        _controllersMenu.DropDownItems.Add(new ToolStripSeparator());

        foreach (var device in config.Devices)
        {
            const string initialStatus = "Starting...";
            var hasMap = device.ButtonMapTemplate is not null;
            var item = new ToolStripMenuItem(FormatControllerItem(device.DisplayName, initialStatus, hasMap))
            {
                Tag = device.Id,
                Enabled = hasMap,
                Checked = _buttonMapForms.TryGetValue(device.Id, out var form) && form.Visible,
            };
            if (hasMap)
            {
                item.Click += OnShowControllerMap;
            }
            _controllersMenu.DropDownItems.Add(item);
            _controllerItems[device.Id] = item;
            _deviceStatuses[device.Id] = initialStatus;
            if (hasMap)
            {
                _buttonMapItems[device.Id] = item;
            }
        }

        _controllersMenu.Enabled = config.Devices.Count > 0;
        UpdateControllerSummary();
    }

    private void UpdateControllerItem(string deviceId)
    {
        if (_activeConfig is null || !_controllerItems.TryGetValue(deviceId, out var item))
        {
            return;
        }

        var device = _activeConfig.Devices.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, deviceId, StringComparison.OrdinalIgnoreCase));
        if (device is null)
        {
            return;
        }

        _deviceStatuses.TryGetValue(deviceId, out var status);
        item.Text = FormatControllerItem(device.DisplayName, status, device.ButtonMapTemplate is not null);
    }

    private void UpdateControllerSummary()
    {
        var total = _activeConfig?.Devices.Count ?? 0;
        var statuses = _activeConfig?.Devices.Select(device =>
                _deviceStatuses.TryGetValue(device.Id, out var status) ? status : string.Empty)
            ?? [];
        _controllersMenu.Text = FormatControllerSummary(total, statuses);
        _notifyIcon.Text = TruncateTooltip($"Joydex - {_controllersMenu.Text}");
        _activityForm?.SetConnectionStatus(_controllersMenu.Text);
    }

    private void DisposeStaleButtonMaps(CompanionConfig? previous, CompanionConfig current)
    {
        foreach (var deviceId in _buttonMapForms.Keys.ToArray())
        {
            var oldDevice = previous?.Devices.FirstOrDefault(device =>
                string.Equals(device.Id, deviceId, StringComparison.OrdinalIgnoreCase));
            var newDevice = current.Devices.FirstOrDefault(device =>
                string.Equals(device.Id, deviceId, StringComparison.OrdinalIgnoreCase));
            var mustRecreate = oldDevice is null
                || newDevice is null
                || !string.Equals(oldDevice.ButtonMapTemplate, newDevice.ButtonMapTemplate, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(oldDevice.DisplayName, newDevice.DisplayName, StringComparison.Ordinal);
            if (!mustRecreate)
            {
                continue;
            }

            var form = _buttonMapForms[deviceId];
            form.SaveWindowState();
            form.Dispose();
            _buttonMapForms.Remove(deviceId);
        }
    }

    private void OnButtonMapVisibility(ButtonMapVisibilityRequest request) =>
        _uiContext.Post(_ =>
        {
            if (request.Visible) ShowButtonMap(request.DeviceId);
            else HideButtonMap(request.DeviceId);
        }, null);

    private void OnPromptOverlayDismissRequested(object? sender, EventArgs eventArgs) =>
        _promptPicker?.Dismiss();

    private async void OnPromptPickers(object? sender, EventArgs eventArgs)
    {
        if (_configuring)
        {
            return;
        }

        _configuring = true;
        _promptPickersItem.Enabled = false;
        try
        {
            _promptPicker?.Dismiss();
            await StopWorkersAsync();
            using var form = new PromptPickerEditorForm(_configPath, _cooperativeWindow.Handle);
            var result = form.ShowDialog();
            StartWorker(showFirstRunNotice: false);
            if (result == DialogResult.OK)
            {
                _notifyIcon.ShowBalloonTip(3500, "Joydex prompt pickers", "Prompt pickers and device maps were reloaded.", ToolTipIcon.Info);
            }
        }
        catch (Exception exception)
        {
            HandleConfigurationError(exception);
        }
        finally
        {
            if (_workers.Count == 0)
            {
                StartWorker(showFirstRunNotice: false);
            }
            _configuring = false;
            _promptPickersItem.Enabled = true;
        }
    }

    private async Task StopWorkersAsync()
    {
        foreach (var worker in _workers.Values)
        {
            await worker.DisposeAsync();
        }
        _workers.Clear();
    }

    private void WriteActivity(string message)
    {
        _log.Write(message);
        if (!IsActionActivity(message))
        {
            return;
        }

        _uiContext.Post(_ =>
        {
            _recentActivity.Enqueue(message);
            while (_recentActivity.Count > 200)
            {
                _recentActivity.Dequeue();
            }

            _activityForm?.Append(message);
        }, null);
    }

    private void CloseActivityForm()
    {
        _activityForm?.Close();
        _activityForm = null;
    }

    private void HandleConfigurationError(Exception exception)
    {
        _log.Write($"Configuration error: {exception.Message}");
        _activeConfig = null;
        _controllersMenu.Text = "Controllers unavailable";
        _controllersMenu.Enabled = false;
        _controllersMenu.DropDownItems.Clear();
        _controllerItems.Clear();
        _buttonMapItems.Clear();
        _deviceStatuses.Clear();
        _notifyIcon.Text = "Joydex - Configuration error";
        _modeItem.Text = "Dry run unavailable";
        _modeItem.Checked = false;
        _modeItem.Enabled = false;
        _modeItem.ForeColor = Color.DarkRed;
        _testControlsItem.Enabled = false;
        _testingAdvancedMenu.Text = "Testing / Advanced";
        _notifyIcon.ShowBalloonTip(
            7000,
            "Joydex configuration error",
            exception.Message,
            ToolTipIcon.Error);
    }

    private async void OnToggleTaskAlerts(object? sender, EventArgs eventArgs)
    {
        _taskAlertsItem.Enabled = false;
        try
        {
            await SetTaskAlertsEnabledAsync(!_taskAlerts.GetSnapshot().Enabled);
        }
        catch (Exception exception)
        {
            _log.Write($"Could not change task-alert state: {exception.Message}");
            _notifyIcon.ShowBalloonTip(5000, "Joydex task alerts", exception.Message, ToolTipIcon.Error);
        }
        finally
        {
            _taskAlertsItem.Enabled = true;
            _taskAlertsItem.Checked = _taskAlerts.GetSnapshot().Enabled;
        }
    }

    private void OnTaskAlertsStatus(object? sender, EventArgs eventArgs)
    {
        if (_taskAlertsForm is { IsDisposed: false })
        {
            _taskAlertsForm.Show();
            _taskAlertsForm.BringToFront();
            _taskAlertsForm.Activate();
            return;
        }

        _taskAlertsForm = new TaskAlertsForm(
            _taskAlerts,
            _hookManager,
            _hookRelayPath,
            _linkToolProfilePath,
            SetTaskAlertsEnabledAsync);
        _taskAlertsForm.FormClosed += (_, _) => _taskAlertsForm = null;
        _taskAlertsForm.Show();
    }

    private void OnTaskAlertsChanged(object? sender, TaskAlertSnapshot snapshot)
    {
        var assignmentSummary = snapshot.Assignments.Count == 0
            ? "none"
            : string.Join(
                ',',
                snapshot.Assignments.Select(assignment => $"S{assignment.Slot}={assignment.State}"));
        _log.Write(
            $"Task-alert snapshot enabled={snapshot.Enabled}; bank=M{snapshot.Bank}; assignments={assignmentSummary}; " +
            $"dropped={snapshot.DroppedEventCount}.");

        if (snapshot.Enabled && snapshot.Assignments.Count > 0)
        {
            _guardian.Start();
            _guardian.SetRestoreRequired(true);
        }

        _ledService.Apply(snapshot);
        _uiContext.Post(_ =>
        {
            _taskAlertsItem.Checked = snapshot.Enabled;
            foreach (var form in _buttonMapForms.Values)
            {
                form.UpdateTaskAlerts(snapshot.Assignments);
            }
        }, null);
    }

    private void OnProfileDirtyChanged(object? sender, bool dirty) =>
        _guardian.SetRestoreRequired(dirty);

    private void OnLedStatusChanged(object? sender, string status)
    {
        if (status.Contains("pending", StringComparison.OrdinalIgnoreCase)
            || status.Contains("inactive", StringComparison.OrdinalIgnoreCase))
        {
            _uiContext.Post(_ => _taskAlertsStatusItem.Text = $"Task alerts status... ({status})", null);
        }
        else
        {
            _uiContext.Post(_ => _taskAlertsStatusItem.Text = "Task alerts status...", null);
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs eventArgs)
    {
        if (eventArgs.Mode == PowerModes.Suspend)
        {
            _ledService.SetPaused(true);
        }
        else if (eventArgs.Mode == PowerModes.Resume)
        {
            _ledService.SetPaused(false);
            _ledService.Apply(_taskAlerts.GetSnapshot());
        }
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs eventArgs) =>
        _ledService.SetPaused(true);

    private void OnDevicesChanged(object? sender, EventArgs eventArgs)
    {
        _log.Write("Device-change notification; task-alert profile restore/replay requested.");
        _ledService.RestoreAndReplay(_taskAlerts.GetSnapshot().Enabled);
    }

    private Task SetTaskAlertsEnabledAsync(bool enabled)
    {
        if (_taskAlerts.GetSnapshot().Enabled == enabled)
        {
            return Task.CompletedTask;
        }

        if (!enabled)
        {
            _taskAlerts.SetEnabled(false);
            return Task.CompletedTask;
        }

        _taskAlerts.SetEnabled(true);
        return Task.CompletedTask;
    }

    private static void OpenPath(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    internal static bool IsConnectedDeviceStatus(string? status) =>
        !string.IsNullOrWhiteSpace(status)
        && status.StartsWith("Connected", StringComparison.OrdinalIgnoreCase);

    internal static string SummarizeDeviceStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return "Starting...";
        }

        var normalized = status.Trim();
        if (!IsConnectedDeviceStatus(normalized))
        {
            return normalized;
        }

        var productSeparator = normalized.IndexOf(':');
        return productSeparator >= 0 ? normalized[..productSeparator] : normalized;
    }

    internal static string FormatControllerItem(string displayName, string? status, bool hasMap)
    {
        var suffix = hasMap ? string.Empty : " (No map)";
        return $"{displayName}: {SummarizeDeviceStatus(status)}{suffix}";
    }

    internal static string FormatControllerSummary(int total, IEnumerable<string> statuses)
    {
        var connected = statuses.Count(IsConnectedDeviceStatus);
        return $"Controllers: {connected}/{total} Connected";
    }

    private static string TruncateTooltip(string value) => value.Length <= 63 ? value : value[..63];

    private static bool IsActionActivity(string message) =>
        message.StartsWith("INPUT ", StringComparison.Ordinal)
        || message.StartsWith("DRY RUN ", StringComparison.Ordinal)
        || message.StartsWith("BLOCKED ", StringComparison.Ordinal)
        || message.StartsWith("FAILED ", StringComparison.Ordinal)
        || message.StartsWith("EXECUTED ", StringComparison.Ordinal);
}
