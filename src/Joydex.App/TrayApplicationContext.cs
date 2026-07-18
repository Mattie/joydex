using System.Diagnostics;
using Joydex.Core.Config;
using Joydex.Core.Mapping;
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
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _modeItem;
    private readonly ToolStripMenuItem _testControlsItem;
    private readonly ToolStripMenuItem _buttonMapItem;
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
    private CompanionWorker? _worker;
    private CompanionConfig? _activeConfig;
    private DryRunActivityForm? _activityForm;
    private ButtonMapForm? _buttonMapForm;
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
        _taskAlerts = new TaskAlertCoordinator(Path.Combine(dataDirectory, "task-alerts.json"));
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

        _statusItem = new ToolStripMenuItem("Starting…") { Enabled = false };
        _modeItem = new ToolStripMenuItem("Dry run", image: null, OnToggleDryRun)
        {
            CheckOnClick = false,
            Enabled = false,
        };
        _testControlsItem = new ToolStripMenuItem("Test controls…", image: null, OnTestControls);
        _configureItem = new ToolStripMenuItem("Configure…", image: null, OnConfigure);
        _buttonMapItem = new ToolStripMenuItem("Button map...", image: null, OnToggleButtonMap)
        {
            Enabled = false,
        };
        _taskAlertsItem = new ToolStripMenuItem("Task alerts", image: null, OnToggleTaskAlerts)
        {
            CheckOnClick = false,
            Checked = _taskAlerts.GetSnapshot().Enabled,
        };
        _taskAlertsStatusItem = new ToolStripMenuItem("Task alerts status...", image: null, OnTaskAlertsStatus);
        var reloadItem = new ToolStripMenuItem("Reload config", image: null, OnReloadConfig);
        var openConfigItem = new ToolStripMenuItem("Open config JSON (advanced)", image: null, (_, _) => OpenPath(_configPath));
        var openLogItem = new ToolStripMenuItem("Open log", image: null, (_, _) => OpenPath(_log.Path));
        var exitItem = new ToolStripMenuItem("Exit", image: null, (_, _) => ExitThread());

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = new ContextMenuStrip
            {
                Items =
                {
                    _statusItem,
                    _modeItem,
                    _taskAlertsItem,
                    new ToolStripSeparator(),
                    _testControlsItem,
                    _buttonMapItem,
                    _taskAlertsStatusItem,
                    _configureItem,
                    reloadItem,
                    openConfigItem,
                    openLogItem,
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
        _ledService.Apply(initialTaskAlerts);
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
        if (_buttonMapForm is not null)
        {
            _buttonMapForm.SaveWindowState();
            _buttonMapForm.Dispose();
            _buttonMapForm = null;
        }

        if (_worker is not null)
        {
            _worker.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _worker = null;
        }

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
            HideButtonMap();
            _recentActivity.Clear();
            if (_worker is not null)
            {
                await _worker.DisposeAsync();
                _worker = null;
            }

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
            };

            ConfigStore.Save(_configPath, updated);
            CloseActivityForm();
            HideButtonMap();
            _recentActivity.Clear();
            if (_worker is not null)
            {
                await _worker.DisposeAsync();
                _worker = null;
            }

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
            HideButtonMap();
            _recentActivity.Clear();
            if (_worker is not null)
            {
                await _worker.DisposeAsync();
                _worker = null;
            }

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
            if (_worker is null)
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
            _activeConfig = config;
            _modeItem.Text = "Dry run";
            _modeItem.Checked = config.Safety.DryRun;
            _modeItem.Enabled = true;
            _modeItem.ForeColor = config.Safety.DryRun ? Color.DarkGreen : Color.DarkRed;
            _testControlsItem.Enabled = config.Safety.DryRun;
            _buttonMapItem.Enabled = true;
            _buttonMapForm?.UpdateConfig(config);
            _buttonMapForm?.UpdateTaskAlerts(_taskAlerts.GetSnapshot().Assignments);

            var source = new DirectInputJoystickSource(_cooperativeWindow.Handle);
            var executor = new CodexActionExecutor(
                config.Safety,
                WriteActivity,
                _keybindingService,
                config.OpenWorkingDirectory,
                internalAction: OnInternalAction);
            var taskAlertInput = new TaskAlertInputInterceptor(() => _taskAlerts.GetSnapshot().Assignments);
            var taskAlertNavigator = new TaskDeepLinkNavigator(config.Safety, WriteActivity);
            _worker = new CompanionWorker(
                config,
                source,
                executor,
                WriteActivity,
                taskAlertInputInterceptor: taskAlertInput,
                taskAlertNavigator: taskAlertNavigator,
                acknowledgeTerminalTaskAlert: _taskAlerts.AcknowledgeTerminal);
            _worker.StatusChanged += OnStatusChanged;
            _worker.Start();

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

    private void OnStatusChanged(object? sender, string status)
    {
        _uiContext.Post(_ =>
        {
            _statusItem.Text = status;
            _notifyIcon.Text = TruncateTooltip($"Joydex — {status}");
            _activityForm?.SetConnectionStatus(status);
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

        _activityForm.SetConnectionStatus(_statusItem.Text ?? "Starting...");
        _activityForm.Show();
    }

    private void OnToggleButtonMap(object? sender, EventArgs eventArgs)
    {
        if (_buttonMapForm is { Visible: true })
        {
            HideButtonMap();
        }
        else
        {
            ShowButtonMap();
        }
    }

    private void OnInternalAction(ActionRequest request)
    {
        _uiContext.Post(_ =>
        {
            if (string.Equals(request.Trigger, "release", StringComparison.OrdinalIgnoreCase))
            {
                HideButtonMap();
            }
            else
            {
                ShowButtonMap();
            }
        }, null);
    }

    private void ShowButtonMap()
    {
        if (_activeConfig is null)
        {
            return;
        }

        try
        {
            if (_buttonMapForm is null || _buttonMapForm.IsDisposed)
            {
                _buttonMapForm = new ButtonMapForm(_activeConfig, _buttonMapStatePath, _log.Write);
                _buttonMapForm.UpdateTaskAlerts(_taskAlerts.GetSnapshot().Assignments);
                _buttonMapForm.VisibleChanged += (_, _) =>
                    _buttonMapItem.Checked = _buttonMapForm is { Visible: true };
            }
            else
            {
                _buttonMapForm.UpdateConfig(_activeConfig);
                _buttonMapForm.UpdateTaskAlerts(_taskAlerts.GetSnapshot().Assignments);
            }

            _buttonMapForm.ShowReference();
            _buttonMapItem.Checked = true;
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

    private void HideButtonMap()
    {
        _buttonMapForm?.HideReference();
        _buttonMapItem.Checked = false;
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
        _statusItem.Text = "Configuration error";
        _modeItem.Text = "Dry run unavailable";
        _modeItem.Checked = false;
        _modeItem.Enabled = false;
        _modeItem.ForeColor = Color.DarkRed;
        _testControlsItem.Enabled = false;
        _buttonMapItem.Enabled = false;
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
            _buttonMapForm?.UpdateTaskAlerts(snapshot.Assignments);
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

    private static string TruncateTooltip(string value) => value.Length <= 63 ? value : value[..63];

    private static bool IsActionActivity(string message) =>
        message.StartsWith("INPUT ", StringComparison.Ordinal)
        || message.StartsWith("DRY RUN ", StringComparison.Ordinal)
        || message.StartsWith("BLOCKED ", StringComparison.Ordinal)
        || message.StartsWith("FAILED ", StringComparison.Ordinal)
        || message.StartsWith("EXECUTED ", StringComparison.Ordinal);
}
