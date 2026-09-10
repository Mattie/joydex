using System.Diagnostics;
using Joydex.Core.Config;
using Joydex.Core.Mapping;
using Joydex.Core.Runtime;
using Joydex.Core.TaskAlerts;
using Joydex.Core.Voice;
using Joydex.WirelessPanel;
using Joydex.Windows.Actions;
using Joydex.Windows.Input;
using Joydex.Windows.Interop;
using Joydex.Windows.Runtime;
using Joydex.Windows.TaskAlerts;
using Joydex.Windows.Voice;
using Joydex.Virpil;
using Joydex.Windows.WirelessPanel;
using Microsoft.Win32;

namespace Joydex.App;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly string _configPath;
    private readonly string _windowStatePath;
    private readonly string _buttonMapStatePath;
    private readonly string _roomVoiceWindowStatePath;
    private readonly string _voicePePreferencesPath;
    private readonly string _pebbleIndexPreferencesPath;
    private readonly string _pebbleIndexSecretPath;
    private readonly string _pebbleIndexInboxDirectory;
    private readonly string _voiceWebViewDataDirectory;
    private VoicePePreferences _voicePePreferences = VoicePePreferences.Default;
    private PebbleIndexPreferences _pebbleIndexPreferences = PebbleIndexPreferences.Default;
    private string? _voicePePreferencesError;
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
    private readonly ToolStripMenuItem _startAtLoginItem;
    private readonly ToolStripMenuItem _startLinkToolAtLoginItem;
    private readonly ToolStripMenuItem _taskAlertsItem;
    private readonly ToolStripMenuItem _taskAlertsStatusItem;
    private readonly ToolStripMenuItem _voicePeItem;
    private readonly SynchronizationContext _uiContext;
    private readonly TaskAlertCoordinator _taskAlerts;
    private readonly TaskAlertPipeServer _taskAlertPipe;
    private readonly VirpilShiftModeMonitor _shiftModeMonitor;
    private ITaskAlertLedOutput _ledService;
    private readonly IVirpilHidTransportFactory _virpilTransportFactory = new VirpilHidTransportFactory();
    private readonly SemaphoreSlim _ledSwitch = new(1, 1);
    private readonly object _ledOutputSync = new();
    private readonly CodexHookManager _hookManager;
    private readonly string _hookRelayPath;
    private readonly string _linkToolProfilePath;
    private readonly GuardianController _guardian;
    private readonly DeviceChangeMonitor _deviceChangeMonitor;
    private readonly LoginStartupRegistration _joydexLoginStartup;
    private readonly LoginStartupRegistration? _linkToolLoginStartup;
    private readonly Queue<string> _recentActivity = new();
    private readonly Dictionary<string, CompanionWorker> _workers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _deviceStatuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ToolStripMenuItem> _controllerItems = new(StringComparer.OrdinalIgnoreCase);
    private CompanionConfig? _activeConfig;
    private EspHomePanelAdapter? _wirelessPanelAdapter;
    private VoicePeBridgeRuntime? _voicePeRuntime;
    private readonly CancellableRuntimeCoordinator<PebbleIndexReceiverRuntime> _pebbleIndexCoordinator = new();
    private PebbleIndexReceiverStatus _pebbleIndexStatus = new(false, "Receiver is off.");
    private DesktopTaskBridgeBrokerProcess? _desktopTaskBroker;
    private Task<DesktopTaskBridgeBrokerProcess>? _desktopTaskBrokerStartup;
    private readonly string _desktopTaskBridgePipeName =
        DesktopTaskBridgeProtocol.PipeName + "." + Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _desktopTaskBrokerCancellation = new();
    private int _desktopTaskBrokerRestartAttempt;
    private readonly RoomVoiceConversationModel _roomVoiceConversation = new();
    private RoomVoiceForm? _roomVoiceForm;
    private Task<VoicePeBridgeRuntime>? _voicePeRuntimeStartup;
    private CancellationTokenSource? _voicePeRuntimeCancellation;
    private CancellationTokenSource? _voicePeStartupRetryCancellation;
    private int _voicePeOwnerRestartAttempt;
    private PinnedVoiceCoordinator? _voiceCoordinator;
    private DryRunActivityForm? _activityForm;
    private readonly Dictionary<string, ButtonMapForm> _buttonMapForms = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ToolStripMenuItem> _buttonMapItems = new(StringComparer.OrdinalIgnoreCase);
    private PromptPickerCoordinator? _promptPicker;
    private PromptPickerOverlayForm? _promptOverlay;
    private TaskAlertsForm? _taskAlertsForm;
    private bool _taskAlertsShowPending;
    private bool _configuring;
    private bool _guardianRecoveryReady;
    private bool _exitStarted;
    private bool _exitCompleted;

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
        _roomVoiceWindowStatePath = Path.Combine(dataDirectory, "room-voice-window.json");
        _voicePePreferencesPath = Path.Combine(dataDirectory, "voice-pe.json");
        _pebbleIndexPreferencesPath = Path.Combine(dataDirectory, "pebble-index.json");
        _pebbleIndexSecretPath = Path.Combine(dataDirectory, "pebble-index.secret");
        _pebbleIndexInboxDirectory = Path.Combine(dataDirectory, "pebble-index", "inbox");
        _voiceWebViewDataDirectory = Path.Combine(dataDirectory, "webview2-voice");
        _log = new FileLog(Path.Combine(dataDirectory, "joydex.log"));
        _voicePePreferences = LoadRoomVoicePreferences(
            _voicePePreferencesPath,
            _log.Write,
            out _voicePePreferencesError);
        _pebbleIndexPreferences = LoadPebbleIndexPreferences(
            _pebbleIndexPreferencesPath,
            _log.Write,
            out var pebbleIndexPreferencesError);
        _pebbleIndexStatus = PebbleIndexReceiverRuntime.ReadStoredStatus(
            false,
            pebbleIndexPreferencesError is null
                ? "Receiver is off."
                : "Pebble Index settings need attention: " + pebbleIndexPreferencesError,
            _pebbleIndexInboxDirectory);
        if (_voicePePreferencesError is not null)
        {
            _roomVoiceConversation.SetRuntimeState(
                VoicePeSessionState.Error,
                ownerReady: false,
                sessionActive: false,
                "Room Voice settings need attention.",
                _voicePePreferencesError);
        }
        _joydexLoginStartup = new LoginStartupRegistration(
            Environment.ProcessPath ?? Application.ExecutablePath,
            "Joydex",
            ["--config", Path.GetFullPath(_configPath)]);
        var linkToolExecutablePath = VirpilLinkToolLocator.FindInstalledPath();
        _linkToolLoginStartup = linkToolExecutablePath is null
            ? null
            : new LoginStartupRegistration(
                linkToolExecutablePath,
                "Joydex.VirpilLinkTool");
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
        ConfigureRoomVoiceTaskAlertExclusion(_voicePePreferences);
        _shiftModeMonitor = new VirpilShiftModeMonitor(
            new VirpilShiftModeReader(),
            _taskAlerts.SetDetectedBank,
            _log.Write);
        var initialTaskAlerts = _taskAlerts.GetSnapshot();
        _linkToolProfilePath = Path.Combine(dataDirectory, "joydex-linktool.led.json");
        if (initialTaskAlerts.EffectiveLedOutput.Mode == TaskAlertLedOutputMode.LinkTool)
        {
            try
            {
                LinkToolProfileWriter.Write(_linkToolProfilePath, initialTaskAlerts.EffectiveLedOutput);
                _log.Write($"Joydex LinkTool profile written to {_linkToolProfilePath}.");
            }
            catch (Exception exception)
            {
                _log.Write($"Could not write the Joydex LinkTool profile: {exception.Message}");
            }
        }

        _ledService = CreateLedOutput(initialTaskAlerts, initialTaskAlerts.EffectiveLedOutput);
        _hookManager = new CodexHookManager(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex",
            "hooks.json"));
        _hookRelayPath = Path.Combine(AppContext.BaseDirectory, "Joydex.HookRelay.exe");
        _guardian = new GuardianController(
            Path.Combine(AppContext.BaseDirectory, "Joydex.Guardian.exe"),
            _log.Write,
            Path.Combine(dataDirectory, "led-guardian-recovery.json"));
        _guardianRecoveryReady = TryUpdateGuardianRecovery(initialTaskAlerts);
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
        _startAtLoginItem = new ToolStripMenuItem(
            "Start Joydex when I sign in",
            image: null,
            OnToggleStartAtLogin)
        {
            CheckOnClick = false,
        };
        InitializeLoginStartupItem(_startAtLoginItem, _joydexLoginStartup, "Joydex");
        _startLinkToolAtLoginItem = new ToolStripMenuItem(
            "Start VIRPIL LinkTool when I sign in",
            image: null,
            OnToggleStartLinkToolAtLogin)
        {
            CheckOnClick = false,
        };
        if (_linkToolLoginStartup is null)
        {
            _startLinkToolAtLoginItem.Text += " (not installed)";
            _startLinkToolAtLoginItem.Enabled = false;
            _log.Write("VIRPIL LinkTool was not found in a standard install location.");
        }
        else
        {
            InitializeLoginStartupItem(
                _startLinkToolAtLoginItem,
                _linkToolLoginStartup,
                "VIRPIL LinkTool");
        }
        UpdateLedModeUi(initialTaskAlerts.EffectiveLedOutput.Mode);
        _taskAlertsItem = new ToolStripMenuItem("Task alerts", image: null, OnToggleTaskAlerts)
        {
            CheckOnClick = false,
            Checked = _taskAlerts.GetSnapshot().Enabled,
        };
        _taskAlertsStatusItem = new ToolStripMenuItem("Task alerts / ignored tasks...", image: null, OnTaskAlertsStatus);
        _voicePeItem = new ToolStripMenuItem("Room Voice", image: null, OnToggleRoomVoice)
        {
            CheckOnClick = false,
        };
        var reloadItem = new ToolStripMenuItem("Reload configuration", image: null, OnReloadConfig);
        var openConfigItem = new ToolStripMenuItem("Open config JSON...", image: null, (_, _) => OpenPath(_configPath));
        var openLogItem = new ToolStripMenuItem("Open log", image: null, (_, _) => OpenPath(_log.Path));
        var exitItem = new ToolStripMenuItem("Exit", image: null, (_, _) => BeginExit());
        _testingAdvancedMenu = new ToolStripMenuItem("Advanced");
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
                    _voicePeItem,
                    new ToolStripSeparator(),
                    _configureItem,
                    _promptPickersItem,
                    _startAtLoginItem,
                    _startLinkToolAtLoginItem,
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
        _roomVoiceConversation.RuntimeStateChanged += OnRoomVoiceRuntimeStateChanged;
        _ledService.StatusChanged += OnLedStatusChanged;
        _ledService.ProfileDirtyChanged += OnProfileDirtyChanged;
        if (initialTaskAlerts.Enabled && initialTaskAlerts.Assignments.Count > 0)
        {
            _guardian.Start();
            _guardian.SetRestoreRequired(_guardianRecoveryReady);
            _ledService.RestoreAndReplay(replay: true);
        }
        else
        {
            _ledService.Apply(initialTaskAlerts);
        }

        _shiftModeMonitor.Start();
        _taskAlertPipe.Start();
        if (DesktopTaskBrokerNeeded) StartDesktopTaskBroker();
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
        if (!_exitCompleted)
        {
            BeginExit();
            return;
        }

        base.ExitThreadCore();
    }

    private void BeginExit()
    {
        if (_exitStarted)
        {
            return;
        }

        _exitStarted = true;
        if (_notifyIcon.ContextMenuStrip is not null)
        {
            _notifyIcon.ContextMenuStrip.Enabled = false;
        }
        _ = ShutdownAndExitAsync();
    }

    private async Task ShutdownAndExitAsync()
    {
        try
        {
            await ShutdownAsync();
        }
        catch (Exception exception)
        {
            _log.Write($"Joydex shutdown did not complete cleanly: {exception.Message}");
        }
        finally
        {
            _exitCompleted = true;
            ExitThread();
        }
    }

    private async Task ShutdownAsync()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionEnding -= OnSessionEnding;
        _deviceChangeMonitor.DevicesChanged -= OnDevicesChanged;
        _deviceChangeMonitor.Dispose();
        if (_taskAlertsShowPending)
        {
            Application.Idle -= OnApplicationIdleShowTaskAlerts;
            _taskAlertsShowPending = false;
        }
        _taskAlertsForm?.Close();
        _taskAlertsForm = null;
        _roomVoiceForm?.CloseWorkspace();
        _roomVoiceForm?.Dispose();
        _roomVoiceForm = null;
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

        await StopWorkersAsync();

        await StopPebbleIndexReceiverAsync().ConfigureAwait(false);

        _desktopTaskBrokerCancellation.Cancel();
        var brokerStartup = _desktopTaskBrokerStartup;
        if (brokerStartup is not null)
        {
            try { _desktopTaskBroker ??= await brokerStartup.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception exception) { _log.Write($"Desktop Task Bridge broker shutdown observed an error: {exception.Message}"); }
        }
        if (_desktopTaskBroker is not null) await _desktopTaskBroker.DisposeAsync().ConfigureAwait(false);
        _desktopTaskBrokerCancellation.Dispose();

        await _shiftModeMonitor.DisposeAsync();
        await _taskAlertPipe.DisposeAsync();
        _taskAlerts.Changed -= OnTaskAlertsChanged;
        _roomVoiceConversation.RuntimeStateChanged -= OnRoomVoiceRuntimeStateChanged;
        _ledService.StatusChanged -= OnLedStatusChanged;
        _ledService.ProfileDirtyChanged -= OnProfileDirtyChanged;
        await _ledService.DisposeAsync();
        if (!_ledService.RestorePending)
        {
            _guardian.SignalCleanExit();
        }

        _guardian.Dispose();
        _ledSwitch.Dispose();
        await _taskAlerts.DisposeAsync();

        await _keybindingService.DisposeAsync();

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _appIcon.Dispose();
        _cooperativeWindow.Dispose();
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

    private void InitializeLoginStartupItem(
        ToolStripMenuItem item,
        LoginStartupRegistration registration,
        string displayName)
    {
        try
        {
            item.Checked = registration.IsEnabled;
        }
        catch (Exception exception)
        {
            item.Enabled = false;
            _log.Write($"Could not read the {displayName} login startup setting: {exception.Message}");
        }
    }

    private void OnToggleStartAtLogin(object? sender, EventArgs eventArgs) =>
        ToggleLoginStartup(
            _startAtLoginItem,
            _joydexLoginStartup,
            "Joydex",
            "Joydex will start after you sign in to Windows.",
            "Joydex will no longer start automatically after sign-in.");

    private void OnToggleStartLinkToolAtLogin(object? sender, EventArgs eventArgs)
    {
        if (_linkToolLoginStartup is null)
        {
            return;
        }

        ToggleLoginStartup(
            _startLinkToolAtLoginItem,
            _linkToolLoginStartup,
            "VIRPIL LinkTool",
            "VIRPIL LinkTool will start after you sign in to Windows.",
            "VIRPIL LinkTool will no longer start automatically after sign-in.");
    }

    private void ToggleLoginStartup(
        ToolStripMenuItem item,
        LoginStartupRegistration registration,
        string displayName,
        string enabledMessage,
        string disabledMessage)
    {
        item.Enabled = false;
        try
        {
            var enable = !registration.IsEnabled;
            registration.SetEnabled(enable);
            item.Checked = enable;
            _notifyIcon.ShowBalloonTip(
                3500,
                $"{displayName} login startup",
                enable ? enabledMessage : disabledMessage,
                ToolTipIcon.Info);
        }
        catch (Exception exception)
        {
            _log.Write($"Could not change the {displayName} login startup setting: {exception.Message}");
            _notifyIcon.ShowBalloonTip(
                5000,
                $"{displayName} login startup",
                exception.Message,
                ToolTipIcon.Error);
        }
        finally
        {
            try
            {
                item.Checked = registration.IsEnabled;
                item.Enabled = true;
            }
            catch (Exception exception)
            {
                item.Enabled = false;
                _log.Write($"Could not refresh the {displayName} login startup setting: {exception.Message}");
            }
        }
    }

    private async void OnConfigure(object? sender, EventArgs eventArgs) =>
        await ShowConfigurationAsync(initialPage: null);

    private async void OnVoicePeSettings(object? sender, EventArgs eventArgs) =>
        await ShowConfigurationAsync("Room Voice");

    private async Task ShowConfigurationAsync(string? initialPage)
    {
        if (_configuring)
        {
            return;
        }

        var activeVoiceSession = _roomVoiceConversation.GetSnapshot().SessionActive;
        if (activeVoiceSession
            && MessageBox.Show(
                _roomVoiceForm,
                "Room Voice is active. End the session and open Configuration?",
                "End session and configure",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        _configuring = true;
        _configureItem.Enabled = false;
        _modeItem.Enabled = false;
        StartDesktopTaskBroker();
        try
        {
            if (activeVoiceSession && _voicePeRuntime is not null)
            {
                await _voicePeRuntime.StopSessionAsync().ConfigureAwait(true);
            }

            var originalVoicePreferences = LoadRoomVoicePreferences(
                _voicePePreferencesPath,
                _log.Write,
                out var preferencesError);
            _voicePePreferences = originalVoicePreferences;
            var originalPebbleIndexPreferences = LoadPebbleIndexPreferences(
                _pebbleIndexPreferencesPath,
                _log.Write,
                out var pebbleIndexPreferencesError);
            _pebbleIndexPreferences = originalPebbleIndexPreferences;
            _voicePePreferencesError = preferencesError;
            if (preferencesError is not null)
            {
                MessageBox.Show(
                    _roomVoiceForm,
                    "Room Voice settings could not be read. Disabled defaults are shown; saving will replace the invalid Room Voice settings file."
                    + Environment.NewLine + Environment.NewLine + preferencesError,
                    "Room Voice settings need attention",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            if (pebbleIndexPreferencesError is not null)
            {
                MessageBox.Show(
                    _roomVoiceForm,
                    "Pebble Index settings could not be read. Disabled defaults are shown; saving will replace the invalid Pebble Index settings file."
                    + Environment.NewLine + Environment.NewLine + pebbleIndexPreferencesError,
                    "Pebble Index settings need attention",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            CloseActivityForm();
            HideAllButtonMaps();
            _promptPicker?.Dismiss();
            _recentActivity.Clear();
            _roomVoiceConversation.SetRuntimeState(
                VoicePeSessionState.Armed,
                ownerReady: false,
                sessionActive: false,
                "Room Voice is paused while Configuration is open.",
                stale: true);
            await StopWorkersAsync();

            using var roomVoiceSettings = new RoomVoiceSettingsControl(
                originalVoicePreferences,
                async candidate =>
                {
                    var config = _activeConfig
                        ?? ConfigStore.LoadOrCreate(_configPath);
                    var navigator = new PinnedVoiceTargetNavigator(config.Safety, WriteActivity);
                    return await navigator
                        .NavigateAsync(candidate.PinnedTaskId, CancellationToken.None)
                        .ConfigureAwait(true);
                },
                async (endpoint, cancellationToken) =>
                {
                    using var client = new EspHomeVoicePeTuningClient(endpoint);
                    return await client.GetAsync(cancellationToken).ConfigureAwait(true);
                },
                async (endpoint, tuning, cancellationToken) =>
                {
                    using var client = new EspHomeVoicePeTuningClient(endpoint);
                    return await client.SetAsync(tuning, cancellationToken).ConfigureAwait(true);
                },
                async (appServerPath, cancellationToken) =>
                {
                    var workspace = new CodexVoiceWorkspaceService(appServerPath, _log.Write);
                    return await workspace.ListProjectRootsAsync(cancellationToken).ConfigureAwait(true);
                },
                async (appServerPath, request, cancellationToken) =>
                {
                    var workspace = new CodexVoiceWorkspaceService(appServerPath, _log.Write);
                    return await workspace.ProvisionAsync(request, cancellationToken).ConfigureAwait(true);
                });
            using var pebbleIndexSettings = new PebbleIndexSettingsControl(
                originalPebbleIndexPreferences,
                _pebbleIndexSecretPath,
                _pebbleIndexInboxDirectory,
                async (candidateSourceTaskId, cancellationToken) =>
                {
                    var sourceTaskId = ResolvePebbleIndexSourceTaskId(
                        candidateSourceTaskId,
                        originalPebbleIndexPreferences,
                        originalVoicePreferences);
                    var bridge = await GetDesktopTaskBridgeClientAsync(cancellationToken).ConfigureAwait(true);
                    return await bridge
                        .ListTasksAsync(sourceTaskId, cancellationToken: cancellationToken)
                        .ConfigureAwait(true);
                },
                _pebbleIndexStatus);
            using var form = new ConfigurationForm(
                _configPath,
                _windowStatePath,
                _cooperativeWindow.Handle,
                roomVoiceSettings: roomVoiceSettings,
                pebbleIndexSettings: pebbleIndexSettings);
            if (initialPage is not null)
            {
                form.SelectPage(initialPage);
            }

            var result = form.ShowDialog();
            if (result == DialogResult.OK && form.RoomVoicePreferences is { } savedVoicePreferences)
            {
                VoicePePreferencesStore.Save(_voicePePreferencesPath, savedVoicePreferences);
                _voicePePreferences = savedVoicePreferences;
                _voicePePreferencesError = null;
                ConfigureRoomVoiceTaskAlertExclusion(savedVoicePreferences);
            }
            if (result == DialogResult.OK && form.PebbleIndexPreferences is { } savedPebbleIndexPreferences)
            {
                PebbleIndexPreferencesStore.Save(_pebbleIndexPreferencesPath, savedPebbleIndexPreferences);
                _pebbleIndexPreferences = savedPebbleIndexPreferences.Normalize();
                await StopPebbleIndexReceiverAsync().ConfigureAwait(true);
            }

            StartWorker(showFirstRunNotice: false);
            if (result == DialogResult.OK)
            {
                _notifyIcon.ShowBalloonTip(
                    4000,
                    "Joydex configuration saved",
                    "Joydex reloaded the saved settings.",
                    ToolTipIcon.Info);

                if (_activeConfig?.Safety.DryRun == true)
                {
                    OnTestControls(this, EventArgs.Empty);
                }
            }
        }
        catch (Exception exception)
        {
            _log.Write($"Configuration window error: {exception}");
            _notifyIcon.ShowBalloonTip(
                5000,
                "Joydex settings error",
                "Settings could not be opened or saved. Joydex kept its active configuration; see the log for details.",
                ToolTipIcon.Error);
        }
        finally
        {
            if (_workers.Count == 0)
            {
                StartWorker(showFirstRunNotice: false);
            }

            _configuring = false;
            await StopDesktopTaskBrokerIfUnusedAsync().ConfigureAwait(true);
            _configureItem.Enabled = true;
            _modeItem.Enabled = _activeConfig is not null;
            RefreshVoicePeMenu();
        }
    }

    private void OnToggleRoomVoice(object? sender, EventArgs eventArgs)
    {
        var form = EnsureRoomVoiceForm();
        if (form.Visible)
        {
            form.HideWorkspace();
        }
        else
        {
            form.ShowWorkspace();
        }
        RefreshVoicePeMenu();
    }

    private RoomVoiceForm EnsureRoomVoiceForm()
    {
        if (_roomVoiceForm is { IsDisposed: false } existing)
        {
            return existing;
        }

        _roomVoiceForm = new RoomVoiceForm(
            _roomVoiceConversation,
            _roomVoiceWindowStatePath,
            RefreshRoomVoiceConversationAsync,
            EndRoomVoiceSessionAsync,
            RestartRoomVoiceAsync,
            () => OnVoicePeSettings(this, EventArgs.Empty),
            visible =>
            {
                _voicePeItem.Checked = visible;
                RefreshVoicePeMenu();
            },
            new RoomVoiceTaskMessagingCallbacks(
                RefreshRoomVoiceTaskMessagingAsync,
                SaveRoomVoiceTargetAsync,
                RetryRoomVoiceDraftAsync,
                RetargetRoomVoiceDraftAsync,
                DiscardRoomVoiceDraft,
                LoadRoomVoiceDrafts));
        return _roomVoiceForm;
    }

    private async Task<RoomVoiceTaskMessagingSnapshot> RefreshRoomVoiceTaskMessagingAsync(
        CancellationToken cancellationToken)
    {
        var preferences = _voicePePreferences.Normalize();
        var drafts = LoadRoomVoiceDrafts();
        if (!preferences.DesktopTaskMessagingEnabled)
        {
            return new RoomVoiceTaskMessagingSnapshot(
                Enabled: false,
                BridgeAvailable: false,
                "Desktop task messaging is off.",
                [],
                preferences.VoiceTargetTaskId,
                preferences.VoiceTargetHostId,
                preferences.VoiceTargetTaskLabel,
                drafts);
        }

        try
        {
            var bridge = await GetDesktopTaskBridgeClientAsync(cancellationToken).ConfigureAwait(true);
            var catalog = await bridge.ListTasksAsync(
                    preferences.DedicatedTaskId,
                    preferences.DedicatedTaskId,
                    cancellationToken)
                .ConfigureAwait(true);
            return new RoomVoiceTaskMessagingSnapshot(
                Enabled: true,
                BridgeAvailable: true,
                "Desktop bridge connected.",
                catalog.Tasks,
                preferences.VoiceTargetTaskId,
                preferences.VoiceTargetHostId,
                preferences.VoiceTargetTaskLabel,
                drafts);
        }
        catch (Exception exception) when (exception is IOException
            or TimeoutException
            or InvalidDataException
            or InvalidOperationException)
        {
            return new RoomVoiceTaskMessagingSnapshot(
                Enabled: true,
                BridgeAvailable: false,
                "Desktop bridge unavailable: " + exception.Message,
                [],
                preferences.VoiceTargetTaskId,
                preferences.VoiceTargetHostId,
                preferences.VoiceTargetTaskLabel,
                drafts);
        }
    }

    private Task SaveRoomVoiceTargetAsync(
        DesktopTaskSummary target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var updated = _voicePePreferences with
        {
            VoiceTargetTaskId = target.Id,
            VoiceTargetHostId = target.HostId,
            VoiceTargetTaskLabel = target.Title,
        };
        VoicePePreferencesStore.Save(_voicePePreferencesPath, updated);
        _voicePePreferences = updated.Normalize();
        return Task.CompletedTask;
    }

    private async Task<string> RetryRoomVoiceDraftAsync(
        VoiceTaskOutboxDraft draft,
        CancellationToken cancellationToken)
    {
        var preferences = _voicePePreferences.Normalize();
        var outbox = CreateVoiceTaskOutbox(preferences)
            ?? throw new InvalidOperationException("The Voice Agent Workspace is unavailable.");
        try
        {
            var bridge = await GetDesktopTaskBridgeClientAsync(cancellationToken).ConfigureAwait(true);
            var catalog = await bridge.ListTasksAsync(
                    preferences.DedicatedTaskId,
                    preferences.DedicatedTaskId,
                    cancellationToken)
                .ConfigureAwait(true);
            var target = catalog.Tasks.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, draft.TargetTaskId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.HostId, draft.TargetHostId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"Target task '{draft.TargetTitle}' is unavailable. Retarget the draft before retrying.");
            var result = await bridge.SendMessageAsync(
                    preferences.DedicatedTaskId,
                    target,
                    draft.Message,
                    cancellationToken)
                .ConfigureAwait(true);
            outbox.Remove(draft.Id);
            return result.Queued ? "Queued to the running task." : "Delivered.";
        }
        catch (Exception exception)
        {
            outbox.RecordFailedAttempt(draft, exception.Message);
            throw;
        }
    }

    private Task RetargetRoomVoiceDraftAsync(
        VoiceTaskOutboxDraft draft,
        DesktopTaskSummary target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var outbox = CreateVoiceTaskOutbox(_voicePePreferences)
            ?? throw new InvalidOperationException("The Voice Agent Workspace is unavailable.");
        outbox.Retarget(draft, target);
        return Task.CompletedTask;
    }

    private void DiscardRoomVoiceDraft(VoiceTaskOutboxDraft draft)
    {
        var outbox = CreateVoiceTaskOutbox(_voicePePreferences)
            ?? throw new InvalidOperationException("The Voice Agent Workspace is unavailable.");
        outbox.Remove(draft.Id);
    }

    private IReadOnlyList<VoiceTaskOutboxDraft> LoadRoomVoiceDrafts()
    {
        try
        {
            return CreateVoiceTaskOutbox(_voicePePreferences)?.Load() ?? [];
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or ArgumentException)
        {
            _log.Write($"Could not read Room Voice pending-message metadata: {exception.Message}");
            return [];
        }
    }

    private static VoiceTaskOutbox? CreateVoiceTaskOutbox(VoicePePreferences preferences) =>
        string.IsNullOrWhiteSpace(preferences.AgentWorkspacePath)
            ? null
            : new VoiceTaskOutbox(preferences.AgentWorkspacePath);

    private async Task RefreshRoomVoiceConversationAsync()
    {
        var runtime = _voicePeRuntime
            ?? throw new InvalidOperationException("Room Voice is still connecting.");
        await runtime.RefreshConversationAsync().ConfigureAwait(true);
    }

    private async Task EndRoomVoiceSessionAsync()
    {
        var runtime = _voicePeRuntime
            ?? throw new InvalidOperationException("Room Voice is not running.");
        if (runtime.IsSessionActive)
        {
            await runtime.StopSessionAsync().ConfigureAwait(true);
            return;
        }

        if (_roomVoiceConversation.GetSnapshot().SessionActive)
        {
            await RestartRoomVoiceAsync().ConfigureAwait(true);
        }
    }

    private async Task RestartRoomVoiceAsync()
    {
        _roomVoiceConversation.SetRuntimeState(
            VoicePeSessionState.Starting,
            ownerReady: false,
            sessionActive: false,
            "Restarting Room Voice…",
            stale: true);
        await StopVoicePeBridgeAsync().ConfigureAwait(true);
        StartVoicePeBridge();
    }

    private void RefreshVoicePeMenu()
    {
        try
        {
            if (_voicePePreferencesError is not null)
            {
                _voicePeItem.Text = "Room Voice — Needs attention";
                _voicePeItem.Checked = _roomVoiceForm?.Visible == true;
                return;
            }

            var conversation = _roomVoiceConversation.GetSnapshot();
            _voicePeItem.Checked = _roomVoiceForm?.Visible == true;
            _voicePeItem.Text = FormatRoomVoiceMenuText(
                _voicePePreferences,
                conversation,
                _voicePeRuntime?.OwnerReady == true,
                _voicePeRuntimeStartup is not null);
        }
        catch (Exception exception)
        {
            _voicePeItem.Text = "Room Voice — Needs attention";
            _voicePeItem.Checked = _roomVoiceForm?.Visible == true;
            _log.Write($"Voice PE settings are unavailable: {exception.Message}");
        }
    }

    private void OnRoomVoiceRuntimeStateChanged(object? sender, EventArgs eventArgs) =>
        _uiContext.Post(_ => RefreshVoicePeMenu(), null);

    internal static string FormatRoomVoiceMenuText(
        VoicePePreferences preferences,
        RoomVoiceConversationSnapshot conversation,
        bool ownerReady,
        bool startupActive)
    {
        if (!preferences.Enabled)
        {
            return "Room Voice — Disabled";
        }
        if (startupActive || conversation.SessionState == VoicePeSessionState.Starting)
        {
            return "Room Voice — Connecting";
        }
        if (conversation.SessionState == VoicePeSessionState.Listening)
        {
            return "Room Voice — Listening";
        }
        if (conversation.SessionState == VoicePeSessionState.Muted)
        {
            return "Room Voice — Muted";
        }
        if (conversation.SessionState == VoicePeSessionState.Error
            || (preferences.SessionMode == VoicePeSessionMode.JoydexOwner && !ownerReady))
        {
            return "Room Voice — Needs attention";
        }

        return "Room Voice";
    }

    private void ConfigureRoomVoiceTaskAlertExclusion(VoicePePreferences? preferences = null)
    {
        try
        {
            preferences ??= _voicePePreferences;
            var taskIds = preferences.SessionMode == VoicePeSessionMode.JoydexOwner
                && CodexTaskReference.TryParse(preferences.DedicatedTaskId, out var taskId)
                    ? new[] { taskId }
                    : [];
            _taskAlerts.SetInternallySuppressedTaskIds(taskIds);
        }
        catch (Exception exception)
        {
            _log.Write($"Could not exclude the Dedicated Voice Task from task alerts: {exception.Message}");
        }
    }

    private async void StartDesktopTaskBroker()
    {
        if (!DesktopTaskBrokerNeeded || _desktopTaskBrokerCancellation.IsCancellationRequested) return;
        if (_desktopTaskBroker is not null || _desktopTaskBrokerStartup is not null) return;
        Task<DesktopTaskBridgeBrokerProcess>? startup = null;
        DesktopTaskBridgeBrokerProcess? broker = null;
        try
        {
            startup = DesktopTaskBridgeBrokerProcess.StartAsync(
                Path.Combine(AppContext.BaseDirectory, "Joydex.DesktopBridgeHost.exe"),
                _desktopTaskBridgePipeName,
                _log.Write,
                _desktopTaskBrokerCancellation.Token);
            _desktopTaskBrokerStartup = startup;
            broker = await startup.ConfigureAwait(true);
            if (Interlocked.CompareExchange(ref _desktopTaskBroker, broker, null) is not null)
            {
                await broker.DisposeAsync().ConfigureAwait(true);
                return;
            }
            _ = MonitorDesktopTaskBrokerAsync(broker, startup);
            _ = ResetDesktopTaskBrokerBackoffAfterStabilityAsync(broker);
            if (_pebbleIndexPreferences.Enabled) StartPebbleIndexReceiver();
        }
        catch (OperationCanceledException) when (_desktopTaskBrokerCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _log.Write($"Desktop Task Bridge broker worker is unavailable: {exception.Message}");
            ScheduleDesktopTaskBrokerRestart();
        }
        finally
        {
            if (broker is null && startup is not null)
                _ = Interlocked.CompareExchange(ref _desktopTaskBrokerStartup, null, startup);
        }
    }

    private async Task<DesktopTaskBridgeBrokerProcess> GetDesktopTaskBrokerAsync(CancellationToken cancellationToken)
    {
        StartDesktopTaskBroker();
        var startup = Volatile.Read(ref _desktopTaskBrokerStartup)
            ?? throw new InvalidOperationException("The Desktop Task Bridge is not enabled.");
        return await startup.WaitAsync(cancellationToken).ConfigureAwait(true);
    }

    private async Task<DesktopTaskBridgeClient> GetDesktopTaskBridgeClientAsync(CancellationToken cancellationToken)
    {
        var broker = await GetDesktopTaskBrokerAsync(cancellationToken).ConfigureAwait(true);
        return new DesktopTaskBridgeClient(broker.PipeName);
    }

    private async Task MonitorDesktopTaskBrokerAsync(
        DesktopTaskBridgeBrokerProcess broker,
        Task<DesktopTaskBridgeBrokerProcess> startup)
    {
        try
        {
            await broker.Completion.ConfigureAwait(false);
            if (!_desktopTaskBrokerCancellation.IsCancellationRequested)
                _log.Write("Desktop Task Bridge broker worker exited; scheduling a restart.");
        }
        catch (Exception exception)
        {
            if (!_desktopTaskBrokerCancellation.IsCancellationRequested)
                _log.Write($"Desktop Task Bridge broker monitor failed: {exception.Message}");
        }
        finally
        {
            var owned = ReferenceEquals(Interlocked.CompareExchange(ref _desktopTaskBroker, null, broker), broker);
            _ = Interlocked.CompareExchange(ref _desktopTaskBrokerStartup, null, startup);
            await broker.DisposeAsync().ConfigureAwait(false);
            if (owned) ScheduleDesktopTaskBrokerRestart();
        }
    }

    private void ScheduleDesktopTaskBrokerRestart()
    {
        if (!DesktopTaskBrokerNeeded || _desktopTaskBrokerCancellation.IsCancellationRequested) return;
        var attempt = Interlocked.Increment(ref _desktopTaskBrokerRestartAttempt);
        var seconds = Math.Min(30, 1 << Math.Min(attempt - 1, 4));
        _ = RestartDesktopTaskBrokerAfterDelayAsync(TimeSpan.FromSeconds(seconds));
    }

    private async Task RestartDesktopTaskBrokerAfterDelayAsync(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, _desktopTaskBrokerCancellation.Token).ConfigureAwait(false);
            if (DesktopTaskBrokerNeeded)
                _uiContext.Post(_ => StartDesktopTaskBroker(), null);
        }
        catch (OperationCanceledException) when (_desktopTaskBrokerCancellation.IsCancellationRequested) { }
    }

    private async Task ResetDesktopTaskBrokerBackoffAfterStabilityAsync(DesktopTaskBridgeBrokerProcess broker)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), _desktopTaskBrokerCancellation.Token).ConfigureAwait(false);
            if (ReferenceEquals(Volatile.Read(ref _desktopTaskBroker), broker))
                Interlocked.Exchange(ref _desktopTaskBrokerRestartAttempt, 0);
        }
        catch (OperationCanceledException) when (_desktopTaskBrokerCancellation.IsCancellationRequested) { }
    }

    private async Task StopDesktopTaskBrokerIfUnusedAsync()
    {
        if (DesktopTaskBrokerNeeded) return;
        var startup = Volatile.Read(ref _desktopTaskBrokerStartup);
        if (startup is null) return;
        try
        {
            var broker = await startup.ConfigureAwait(true);
            Interlocked.CompareExchange(ref _desktopTaskBroker, null, broker);
            _ = Interlocked.CompareExchange(ref _desktopTaskBrokerStartup, null, startup);
            await broker.DisposeAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _ = Interlocked.CompareExchange(ref _desktopTaskBrokerStartup, null, startup);
            _log.Write($"Desktop Task Bridge broker stop observed an error: {exception.Message}");
        }
    }

    private bool DesktopTaskBrokerNeeded => ShouldStartDesktopTaskBroker(
        _configuring,
        _voicePePreferences,
        _pebbleIndexPreferences);

    internal static bool ShouldStartDesktopTaskBroker(
        bool configuring,
        VoicePePreferences voicePreferences,
        PebbleIndexPreferences pebbleIndexPreferences)
    {
        ArgumentNullException.ThrowIfNull(voicePreferences);
        ArgumentNullException.ThrowIfNull(pebbleIndexPreferences);
        return configuring
            || voicePreferences.DesktopTaskMessagingEnabled
            || pebbleIndexPreferences.Enabled;
    }

    private void StartPebbleIndexReceiver()
    {
        if (_exitStarted || _desktopTaskBrokerCancellation.IsCancellationRequested) return;
        PebbleIndexPreferences preferences;
        try
        {
            preferences = PebbleIndexPreferencesStore.LoadOrCreate(_pebbleIndexPreferencesPath);
            _pebbleIndexPreferences = preferences;
        }
        catch (Exception exception)
        {
            ReportPebbleIndexUnavailable(exception);
            return;
        }
        if (!preferences.Enabled)
        {
            _pebbleIndexStatus = PebbleIndexReceiverRuntime.ReadStoredStatus(
                false,
                "Receiver is off.",
                _pebbleIndexInboxDirectory);
            return;
        }

        _ = _pebbleIndexCoordinator.Start(
            async cancellationToken =>
            {
                var broker = await GetDesktopTaskBrokerAsync(cancellationToken).ConfigureAwait(false);
                return await PebbleIndexReceiverRuntime.StartAsync(
                    preferences,
                    _pebbleIndexSecretPath,
                    _pebbleIndexInboxDirectory,
                    broker.PipeName,
                    status => _uiContext.Post(_ => _pebbleIndexStatus = status, null),
                    _log.Write,
                    cancellationToken).ConfigureAwait(false);
            },
            ReportPebbleIndexUnavailable);
    }

    private void ReportPebbleIndexUnavailable(Exception exception)
    {
        try
        {
            _pebbleIndexStatus = PebbleIndexReceiverRuntime.ReadStoredStatus(
                false,
                "Receiver unavailable: " + exception.Message,
                _pebbleIndexInboxDirectory);
            _log.Write(_pebbleIndexStatus.Message);
        }
        catch (Exception statusException)
        {
            _log.Write($"Pebble Index receiver and recovery status are unavailable: {statusException.Message}");
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
                ? "Advanced (DRY RUN)"
                : "Advanced";
            ConfigureControllersMenu(config);
            foreach (var form in _buttonMapForms.Values)
            {
                form.UpdateConfig(config);
                var initialTaskAlertSnapshot = _taskAlerts.GetSnapshot();
                form.UpdateTaskAlerts(initialTaskAlertSnapshot.Assignments, initialTaskAlertSnapshot.EffectiveLedOutput);
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
            var voiceNavigator = new PinnedVoiceTargetNavigator(config.Safety, WriteActivity);
            var voiceExecutor = new CodexActionExecutor(
                config.Safety,
                WriteActivity,
                _keybindingService,
                config.OpenWorkingDirectory);
            _voiceCoordinator = new PinnedVoiceCoordinator(
                config.Safety,
                WriteActivity,
                voiceNavigator,
                voiceExecutor.ExecuteAsync);
            RefreshVoicePeMenu();
            StartVoicePeBridge();
            StartPebbleIndexReceiver();
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

            StartWirelessPanel(config);

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
                var initialTaskAlertSnapshot = _taskAlerts.GetSnapshot();
                form.UpdateTaskAlerts(initialTaskAlertSnapshot.Assignments, initialTaskAlertSnapshot.EffectiveLedOutput);
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
            var taskAlertSnapshot = _taskAlerts.GetSnapshot();
            form.UpdateTaskAlerts(taskAlertSnapshot.Assignments, taskAlertSnapshot.EffectiveLedOutput);
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
        await StopVoicePeBridgeAsync().ConfigureAwait(false);

        _voiceCoordinator = null;
        var wirelessPanelAdapter = Interlocked.Exchange(ref _wirelessPanelAdapter, null);
        if (wirelessPanelAdapter is not null)
        {
            try
            {
                await wirelessPanelAdapter.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _log.Write($"Could not stop the ESPHome panel adapter: {exception.Message}");
            }
        }

        foreach (var worker in _workers.Values)
        {
            await worker.DisposeAsync().ConfigureAwait(false);
        }
        _workers.Clear();
    }

    private async Task StopPebbleIndexReceiverAsync()
    {
        await _pebbleIndexCoordinator.StopAsync(exception =>
            _log.Write($"Could not stop the Pebble Index receiver: {exception.Message}")).ConfigureAwait(false);
    }

    private async Task StopVoicePeBridgeAsync()
    {
        CancelVoicePeStartupRetry();
        var cancellation = Interlocked.Exchange(ref _voicePeRuntimeCancellation, null);
        cancellation?.Cancel();
        var startup = Interlocked.Exchange(ref _voicePeRuntimeStartup, null);
        if (startup is not null)
        {
            try
            {
                var completedStartup = await startup.ConfigureAwait(false);
                if (!ReferenceEquals(completedStartup, Volatile.Read(ref _voicePeRuntime)))
                {
                    await completedStartup.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellation?.IsCancellationRequested == true)
            {
            }
            catch (Exception exception)
            {
                _log.Write($"Voice PE bridge startup ended with an error: {exception.Message}");
            }
        }

        cancellation?.Dispose();
        var runtime = Interlocked.Exchange(ref _voicePeRuntime, null);
        if (runtime is not null)
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async void StartVoicePeBridge()
    {
        CancelVoicePeStartupRetry();
        CancellationTokenSource? cancellation = null;
        Task<VoicePeBridgeRuntime>? startup = null;
        VoicePeBridgeRuntime? unpublishedRuntime = null;
        var retryOwnerStartup = false;
        try
        {
            var preferences = LoadRoomVoicePreferences(
                _voicePePreferencesPath,
                _log.Write,
                out var preferencesError);
            _voicePePreferences = preferences;
            _voicePePreferencesError = preferencesError;
            if (preferencesError is not null)
            {
                throw new InvalidDataException(preferencesError);
            }
            if (!preferences.Enabled)
            {
                _log.Write("Voice PE bridge is disabled.");
                _roomVoiceConversation.SetFallbackState(enabled: false);
                RefreshVoicePeMenu();
                return;
            }

            retryOwnerStartup = preferences.SessionMode == VoicePeSessionMode.JoydexOwner;

            var coordinator = _voiceCoordinator;
            var config = _activeConfig;
            if (coordinator is null || config is null)
            {
                throw new InvalidDataException("The enabled Voice PE bridge has no active Joydex coordinator.");
            }

            cancellation = new CancellationTokenSource();
            var startupToken = cancellation.Token;
            if (Interlocked.CompareExchange(ref _voicePeRuntimeCancellation, cancellation, null) is not null)
            {
                cancellation.Dispose();
                _log.Write("Voice PE bridge startup is already active.");
                return;
            }

            if (preferences.DesktopTaskMessagingEnabled)
                await GetDesktopTaskBrokerAsync(startupToken).ConfigureAwait(true);
            startup = VoicePeBridgeRuntime.StartAsync(
                preferences,
                config.Safety,
                coordinator,
                _uiContext,
                _voiceWebViewDataDirectory,
                _roomVoiceConversation,
                WriteActivity,
                _voicePePreferencesPath,
                Path.Combine(AppContext.BaseDirectory, "Joydex.DesktopBridgeHost.exe"),
                _desktopTaskBridgePipeName,
                startupToken);
            Volatile.Write(ref _voicePeRuntimeStartup, startup);
            unpublishedRuntime = await startup.ConfigureAwait(true);
            startupToken.ThrowIfCancellationRequested();
            var runtime = unpublishedRuntime;
            var replaced = Interlocked.Exchange(ref _voicePeRuntime, runtime);
            unpublishedRuntime = null;
            if (replaced is not null)
            {
                await replaced.DisposeAsync().ConfigureAwait(true);
            }

            if (runtime.Mode == VoicePeSessionMode.JoydexOwner)
            {
                _ = MonitorVoicePeOwnerAsync(runtime, startupToken);
                _ = ResetVoicePeOwnerRestartBackoffAfterStabilityAsync(runtime, startupToken);
            }

            RefreshVoicePeMenu();
        }
        catch (OperationCanceledException) when (cancellation?.IsCancellationRequested == true)
        {
        }
        catch (Exception exception)
        {
            _log.Write($"Voice PE bridge is unavailable: {exception.Message}");
            _roomVoiceConversation.SetRuntimeState(
                VoicePeSessionState.Error,
                ownerReady: false,
                sessionActive: false,
                "Room Voice needs attention.",
                exception.Message,
                stale: true);
            RefreshVoicePeMenu();
            _notifyIcon.ShowBalloonTip(
                5000,
                "Joydex Voice PE bridge unavailable",
                exception.Message,
                ToolTipIcon.Warning);
            if (retryOwnerStartup && IsTransientOwnerStartupFailure(exception))
            {
                ScheduleVoicePeStartupRetry(exception);
            }
        }
        finally
        {
            if (unpublishedRuntime is not null)
            {
                await unpublishedRuntime.DisposeAsync().ConfigureAwait(true);
            }

            if (ReferenceEquals(Volatile.Read(ref _voicePeRuntimeStartup), startup))
            {
                _ = Interlocked.Exchange(ref _voicePeRuntimeStartup, null);
            }

            if (_voicePeRuntime is null
                && cancellation is not null
                && ReferenceEquals(Volatile.Read(ref _voicePeRuntimeCancellation), cancellation))
            {
                _ = Interlocked.Exchange(ref _voicePeRuntimeCancellation, null);
                cancellation.Dispose();
            }
        }
    }

    private void ScheduleVoicePeStartupRetry(Exception failure)
    {
        var cancellation = new CancellationTokenSource();
        if (Interlocked.CompareExchange(
                ref _voicePeStartupRetryCancellation,
                cancellation,
                null) is not null)
        {
            cancellation.Dispose();
            return;
        }

        var attempt = Interlocked.Increment(ref _voicePeOwnerRestartAttempt);
        var delay = TimeSpan.FromSeconds(Math.Min(30, 2 * Math.Pow(2, Math.Min(attempt - 1, 4))));
        _roomVoiceConversation.SetRuntimeState(
            VoicePeSessionState.Starting,
            ownerReady: false,
            sessionActive: false,
            $"Room Voice is reconnecting in {delay.TotalSeconds:0} seconds.",
            failure.Message,
            stale: true);
        _log.Write(
            $"Dedicated Voice Task owner startup will retry after {failure.GetType().Name} "
            + $"in {delay.TotalSeconds:0} seconds.");
        _ = RetryVoicePeStartupAsync(cancellation, delay);
    }

    private async Task RetryVoicePeStartupAsync(
        CancellationTokenSource cancellation,
        TimeSpan delay)
    {
        var cancellationToken = cancellation.Token;
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        _uiContext.Post(
            _ =>
            {
                if (cancellationToken.IsCancellationRequested
                    || !ReferenceEquals(
                        Interlocked.CompareExchange(
                            ref _voicePeStartupRetryCancellation,
                            null,
                            cancellation),
                        cancellation))
                {
                    return;
                }

                cancellation.Dispose();
                StartVoicePeBridge();
            },
            null);
    }

    private void CancelVoicePeStartupRetry()
    {
        var cancellation = Interlocked.Exchange(ref _voicePeStartupRetryCancellation, null);
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
    }

    internal static bool IsTransientOwnerStartupFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception.Message.Contains("failed to load configuration", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("failed to load bootstrap configuration", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return exception switch
        {
            CodexDedicatedVoiceCompatibilityException => false,
            InvalidDataException => false,
            FileNotFoundException => false,
            UnauthorizedAccessException => false,
            ArgumentException => false,
            _ => true,
        };
    }

    private async Task MonitorVoicePeOwnerAsync(
        VoicePeBridgeRuntime runtime,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            await runtime.OwnerCompletion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        if (cancellationToken.IsCancellationRequested
            || !ReferenceEquals(Volatile.Read(ref _voicePeRuntime), runtime))
        {
            return;
        }

        var attempt = Interlocked.Increment(ref _voicePeOwnerRestartAttempt);
        var delay = TimeSpan.FromSeconds(Math.Min(30, 2 * Math.Pow(2, Math.Min(attempt - 1, 4))));
        _roomVoiceConversation.SetRuntimeState(
            VoicePeSessionState.Starting,
            ownerReady: false,
            sessionActive: false,
            $"Room Voice is reconnecting in {delay.TotalSeconds:0} seconds.",
            failure?.Message,
            stale: true);
        _log.Write(
            $"Dedicated Voice Task owner stopped{(failure is null ? "." : $": {failure.Message}")} "
            + $"Joydex will restart the Voice PE bridge in {delay.TotalSeconds:0} seconds.");
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        _uiContext.Post(
            async _ =>
            {
                try
                {
                    if (cancellationToken.IsCancellationRequested
                        || !ReferenceEquals(Volatile.Read(ref _voicePeRuntime), runtime))
                    {
                        return;
                    }

                    await StopVoicePeBridgeAsync().ConfigureAwait(true);
                    StartVoicePeBridge();
                }
                catch (Exception exception)
                {
                    _log.Write($"Could not restart the Voice PE bridge: {exception.Message}");
                    _notifyIcon.ShowBalloonTip(
                        5000,
                        "Joydex Voice PE restart failed",
                        exception.Message,
                        ToolTipIcon.Warning);
                }
            },
            null);
    }

    private async Task ResetVoicePeOwnerRestartBackoffAfterStabilityAsync(
        VoicePeBridgeRuntime runtime,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
            if (runtime.OwnerReady
                && ReferenceEquals(Volatile.Read(ref _voicePeRuntime), runtime))
            {
                Interlocked.Exchange(ref _voicePeOwnerRestartAttempt, 0);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void StartWirelessPanel(CompanionConfig config)
    {
        EspHomePanelAdapter? adapter = null;
        try
        {
            var panelConfiguration = new WirelessPanelConfigurationStore().Load();
            if (panelConfiguration is null)
            {
                _log.Write("ESPHome panel is not configured.");
                return;
            }

            if (!panelConfiguration.Enabled)
            {
                _log.Write("ESPHome panel is configured and disabled.");
                return;
            }

            var navigator = new TaskDeepLinkNavigator(config.Safety, WriteActivity);
            var executor = new CodexActionExecutor(
                config.Safety,
                WriteActivity,
                _keybindingService,
                config.OpenWorkingDirectory,
                internalAction: OnInternalAction);
            var transport = new EspHomePanelTransport(
                panelConfiguration.Endpoint,
                panelConfiguration.Username,
                panelConfiguration.Password,
                _log.Write);
            var initialSnapshot = _taskAlerts.GetSnapshot();
            adapter = new EspHomePanelAdapter(
                transport,
                initialSnapshot,
                _taskAlerts.GetSnapshot,
                navigator,
                _taskAlerts.AcknowledgeTerminal,
                executor.ExecuteAsync,
                _log.Write);
            Volatile.Write(ref _wirelessPanelAdapter, adapter);
            adapter.Start();
            _log.Write(
                $"ESPHome panel adapter started for {panelConfiguration.Endpoint.Host}:" +
                $"{panelConfiguration.Endpoint.Port}.");
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _wirelessPanelAdapter, null);
            if (adapter is not null)
            {
                try
                {
                    adapter.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch (Exception disposeException)
                {
                    _log.Write(
                        $"Could not clean up the ESPHome panel adapter: {disposeException.Message}");
                }
            }

            _log.Write($"ESPHome panel is unavailable: {exception.Message}");
        }
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
        _testingAdvancedMenu.Text = "Advanced";
        _notifyIcon.ShowBalloonTip(
            7000,
            "Joydex configuration error",
            exception.Message,
            ToolTipIcon.Error);
    }

    internal static VoicePePreferences LoadRoomVoicePreferences(
        string path,
        Action<string> log,
        out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(log);
        try
        {
            error = null;
            return VoicePePreferencesStore.LoadOrCreate(path);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or System.Text.Json.JsonException)
        {
            error = exception.Message;
            log($"Room Voice settings are unavailable; normal Joydex features will continue: {exception.Message}");
            return VoicePePreferences.Default;
        }
    }

    internal static PebbleIndexPreferences LoadPebbleIndexPreferences(
        string path,
        Action<string> log,
        out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(log);
        try
        {
            error = null;
            return PebbleIndexPreferencesStore.LoadOrCreate(path);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or System.Text.Json.JsonException)
        {
            error = exception.Message;
            log($"Pebble Index settings are unavailable; normal Joydex features will continue: {exception.Message}");
            return PebbleIndexPreferences.Default;
        }
    }

    internal static string ResolvePebbleIndexSourceTaskId(
        string? candidateSourceTaskId,
        PebbleIndexPreferences pebbleIndexPreferences,
        VoicePePreferences voicePreferences)
    {
        ArgumentNullException.ThrowIfNull(pebbleIndexPreferences);
        ArgumentNullException.ThrowIfNull(voicePreferences);
        foreach (var candidate in new[]
        {
            candidateSourceTaskId,
            pebbleIndexPreferences.TargetTaskId,
            voicePreferences.DedicatedTaskId,
        })
        {
            if (CodexTaskReference.TryParse(candidate, out var sourceTaskId)) return sourceTaskId;
        }
        throw new InvalidOperationException(
            "Paste an existing Codex task ID before refreshing the Desktop task list.");
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
        if (_taskAlertsShowPending)
        {
            return;
        }

        // With a nested ToolStrip menu, the root Closed event occurs before this Click
        // handler. Application.Idle runs after the remaining dismissal/focus messages.
        _taskAlertsShowPending = true;
        Application.Idle += OnApplicationIdleShowTaskAlerts;
    }

    private void OnApplicationIdleShowTaskAlerts(object? sender, EventArgs eventArgs)
    {
        Application.Idle -= OnApplicationIdleShowTaskAlerts;
        _taskAlertsShowPending = false;
        _ = ShowTaskAlertsStatus();
    }

    private bool ShowTaskAlertsStatus()
    {
        try
        {
            var form = _taskAlertsForm;
            if (form is null || form.IsDisposed || form.Disposing)
            {
                form = new TaskAlertsForm(
                    _taskAlerts,
                    _hookManager,
                    _hookRelayPath,
                    _linkToolProfilePath,
                    SetTaskAlertsEnabledAsync,
                    SetLedOutputAsync);
                var createdForm = form;
                form.FormClosed += (_, _) =>
                {
                    if (ReferenceEquals(_taskAlertsForm, createdForm))
                    {
                        _taskAlertsForm = null;
                    }
                };
                _taskAlertsForm = form;
            }

            ShowAndActivate(form);
            var nativeVisible = IsFormNativelyVisible(form);
            _log.Write(
                $"Task-alert status shown visible={form.Visible}; nativeVisible={nativeVisible}; " +
                $"state={form.WindowState}; " +
                $"bounds={form.Bounds}; screen={Screen.FromControl(form).DeviceName}.");
            return nativeVisible;
        }
        catch (Exception exception)
        {
            _log.Write($"Could not show task-alert status: {exception}");
            _taskAlertsForm?.Dispose();
            _taskAlertsForm = null;
            _notifyIcon.ShowBalloonTip(
                5000,
                "Joydex task alerts",
                "The status window could not be opened. See the Joydex log for details.",
                ToolTipIcon.Error);
            return false;
        }
    }

    private static void ShowAndActivate(Form form)
    {
        RestoreVisibleWindowBounds(form);
        if (form.WindowState == FormWindowState.Minimized)
        {
            form.WindowState = FormWindowState.Normal;
        }

        if (!form.Visible)
        {
            form.Show();
        }

        EnsureNativeWindowVisible(form);

        form.BringToFront();
        form.Activate();
        _ = SetForegroundWindow(form.Handle);

        // Recheck on the next UI turn as foreground activation can settle after Show.
        form.BeginInvoke(() =>
        {
            if (form.IsDisposed || form.Disposing)
            {
                return;
            }

            if (form.WindowState == FormWindowState.Minimized)
            {
                form.WindowState = FormWindowState.Normal;
            }

            RestoreVisibleWindowBounds(form);
            if (!form.Visible)
            {
                form.Show();
            }

            EnsureNativeWindowVisible(form);

            form.BringToFront();
            form.Activate();
            _ = SetForegroundWindow(form.Handle);
        });
    }

    internal static bool IsFormNativelyVisible(Form? form) => form is
    {
        IsDisposed: false,
        Disposing: false,
        IsHandleCreated: true,
    } && IsWindowVisible(form.Handle);

    internal static void EnsureNativeWindowVisible(Form form)
    {
        if (form.IsHandleCreated && !IsWindowVisible(form.Handle))
        {
            _ = ShowWindow(form.Handle, 5); // SW_SHOW
        }
    }

    private static void RestoreVisibleWindowBounds(Form form)
    {
        var bounds = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0
            || Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(bounds)))
        {
            return;
        }

        var workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        var size = new Size(
            Math.Min(Math.Max(form.MinimumSize.Width, bounds.Width), workingArea.Width),
            Math.Min(Math.Max(form.MinimumSize.Height, bounds.Height), workingArea.Height));
        form.StartPosition = FormStartPosition.Manual;
        form.Bounds = new Rectangle(
            workingArea.Left + Math.Max(0, (workingArea.Width - size.Width) / 2),
            workingArea.Top + Math.Max(0, (workingArea.Height - size.Height) / 2),
            size.Width,
            size.Height);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

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

        _guardianRecoveryReady = TryUpdateGuardianRecovery(snapshot);
        if (snapshot.Enabled && snapshot.Assignments.Count > 0)
        {
            _guardian.Start();
            _guardian.SetRestoreRequired(_guardianRecoveryReady);
        }

        UseLedOutput(output => output.Apply(snapshot));
        Volatile.Read(ref _wirelessPanelAdapter)?.Apply(snapshot);
        _uiContext.Post(_ =>
        {
            _taskAlertsItem.Checked = snapshot.Enabled;
            foreach (var form in _buttonMapForms.Values)
            {
                form.UpdateTaskAlerts(snapshot.Assignments, snapshot.EffectiveLedOutput);
            }
        }, null);
    }

    private void OnProfileDirtyChanged(object? sender, bool dirty) =>
        _guardian.SetRestoreRequired(dirty && _guardianRecoveryReady);

    private void OnLedStatusChanged(object? sender, string status)
    {
        if (status.Contains("pending", StringComparison.OrdinalIgnoreCase)
            || status.Contains("inactive", StringComparison.OrdinalIgnoreCase))
        {
            _uiContext.Post(
                _ => _taskAlertsStatusItem.Text = $"Task alerts / ignored tasks... ({status})",
                null);
        }
        else
        {
            _uiContext.Post(
                _ => _taskAlertsStatusItem.Text = "Task alerts / ignored tasks...",
                null);
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs eventArgs)
    {
        if (eventArgs.Mode == PowerModes.Suspend)
        {
            UseLedOutput(output => output.SetPaused(true));
        }
        else if (eventArgs.Mode == PowerModes.Resume)
        {
            UseLedOutput(output =>
            {
                output.SetPaused(false);
                output.Apply(_taskAlerts.GetSnapshot());
            });
        }
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs eventArgs) =>
        UseLedOutput(output => output.SetPaused(true));

    private void OnDevicesChanged(object? sender, EventArgs eventArgs)
    {
        _log.Write("Device-change notification; task-alert profile restore/replay requested.");
        UseLedOutput(output => output.RestoreAndReplay(_taskAlerts.GetSnapshot().Enabled));
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

    private ITaskAlertLedOutput CreateLedOutput(
        TaskAlertSnapshot snapshot,
        TaskAlertLedOptions options) => options.Mode switch
        {
            TaskAlertLedOutputMode.DirectHid => new DirectVirpilLedService(
                _virpilTransportFactory,
                new DirectVirpilConflictDetector(),
                _log.Write,
                snapshot,
                options),
            _ => new LinkToolLedService(
                new UdpLinkToolTelemetrySender(),
                new VpcConflictDetector(),
                _log.Write,
                snapshot),
        };

    private async Task SetLedOutputAsync(TaskAlertLedOptions options)
    {
        options = options.Normalize();
        await _ledSwitch.WaitAsync().ConfigureAwait(true);
        try
        {
            var linkToolStartupDisabled = false;
            if (options.Mode == TaskAlertLedOutputMode.DirectHid)
            {
                var conflicts = new DirectVirpilConflictDetector();
                if (conflicts.HasConflict())
                {
                    throw new InvalidOperationException(
                        "Close VIRPIL LinkTool and all VPC utilities before enabling Direct USB LED output.");
                }

                if (!_virpilTransportFactory.IsAvailable(VirpilDevices.Throttle)
                    || !_virpilTransportFactory.IsAvailable(VirpilDevices.Alpha))
                {
                    throw new InvalidOperationException(
                        "Both the CM3 throttle and Constellation Alpha HID feature collections must be connected before enabling Direct USB LED output.");
                }

                if (_linkToolLoginStartup?.IsEnabled == true)
                {
                    _linkToolLoginStartup.SetEnabled(false);
                    _startLinkToolAtLoginItem.Checked = false;
                    linkToolStartupDisabled = true;
                }

            }
            else
            {
                LinkToolProfileWriter.Write(_linkToolProfilePath, options);
            }

            var previousSnapshot = _taskAlerts.GetSnapshot();
            var snapshot = previousSnapshot with { LedOutput = options };
            var next = CreateLedOutput(snapshot, options);
            var previous = BeginLedOutputHandoff(next);
            await DisposeReplacedLedOutputAsync(previous).ConfigureAwait(true);
            CompleteLedOutputHandoff(next);
            try
            {
                _taskAlerts.SetLedOutput(options);
            }
            catch
            {
                var rollback = CreateLedOutput(previousSnapshot, previousSnapshot.EffectiveLedOutput);
                var failed = BeginLedOutputHandoff(rollback);
                await DisposeReplacedLedOutputAsync(failed).ConfigureAwait(true);
                CompleteLedOutputHandoff(rollback);
                _guardianRecoveryReady = TryUpdateGuardianRecovery(previousSnapshot);
                UseLedOutput(output => output.Apply(previousSnapshot));
                UpdateLedModeUi(previousSnapshot.EffectiveLedOutput.Mode);
                if (linkToolStartupDisabled && _linkToolLoginStartup is not null)
                {
                    try
                    {
                        _linkToolLoginStartup.SetEnabled(true);
                        _startLinkToolAtLoginItem.Checked = true;
                    }
                    catch (Exception exception)
                    {
                        _log.Write($"Could not restore LinkTool login startup after LED mode rollback: {exception.Message}");
                    }
                }

                throw;
            }

            _guardianRecoveryReady = TryUpdateGuardianRecovery(_taskAlerts.GetSnapshot());
            UseLedOutput(output => output.Apply(_taskAlerts.GetSnapshot()));
            UpdateLedModeUi(options.Mode);
        }
        finally
        {
            _ledSwitch.Release();
        }
    }

    private void UpdateLedModeUi(TaskAlertLedOutputMode mode)
    {
        var linkToolMode = mode == TaskAlertLedOutputMode.LinkTool;
        _startLinkToolAtLoginItem.Visible = linkToolMode;
        _startLinkToolAtLoginItem.Enabled = linkToolMode && _linkToolLoginStartup is not null;
    }

    private ITaskAlertLedOutput BeginLedOutputHandoff(ITaskAlertLedOutput next)
    {
        ArgumentNullException.ThrowIfNull(next);
        next.SetPaused(true);
        lock (_ledOutputSync)
        {
            var previous = _ledService;
            previous.StatusChanged -= OnLedStatusChanged;
            previous.ProfileDirtyChanged -= OnProfileDirtyChanged;
            previous.SetPaused(true);
            _ledService = next;
            next.StatusChanged += OnLedStatusChanged;
            next.ProfileDirtyChanged += OnProfileDirtyChanged;
            return previous;
        }
    }

    private void CompleteLedOutputHandoff(ITaskAlertLedOutput output)
    {
        lock (_ledOutputSync)
        {
            if (!ReferenceEquals(_ledService, output))
            {
                return;
            }

            output.SetPaused(false);
            output.RestoreAndReplay(replay: true);
        }
    }

    private async Task DisposeReplacedLedOutputAsync(ITaskAlertLedOutput output)
    {
        try
        {
            await output.DisposeAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _log.Write($"Could not finish disposing the previous task-alert LED output: {exception.Message}");
        }
    }

    private void UseLedOutput(Action<ITaskAlertLedOutput> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_ledOutputSync)
        {
            operation(_ledService);
        }
    }

    private bool TryUpdateGuardianRecovery(TaskAlertSnapshot snapshot)
    {
        try
        {
            _guardian.UpdateRecovery(snapshot);
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.Text.Json.JsonException
            or NotSupportedException)
        {
            _log.Write($"Could not update LED guardian recovery state: {exception.Message}");
            return false;
        }
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
