using Joydex.App;
using Joydex.Contracts;
using Joydex.Core.Config;
using Joydex.Core.Runtime;
using Joydex.Core.TaskAlerts;
using Joydex.Core.Voice;
using Joydex.Ipc;
using Joydex.RuntimeHost.Plugins;
using Joydex.Virpil;
using Joydex.WirelessPanel;
using Joydex.Windows.Actions;
using Joydex.Windows.Input;
using Joydex.Windows.TaskAlerts;
using Joydex.Windows.Voice;

namespace Joydex.RuntimeHost.Production;

internal sealed partial class WindowsProductionRuntimeOwnerFactory :
    IProductionRuntimeOwnerFactory,
    IPadPluginHostServices,
    IVoicePluginHostServices,
    IPebblePluginHostServices
{
    private readonly object _stateGate = new();
    private readonly RuntimeInputHost _inputHost;
    private readonly CancellationToken _runtimeCancellationToken;
    private readonly ProductionRuntimePaths _paths;
    private readonly bool _existingCompanionInstall;
    private readonly FileLog _log;
    private readonly ProductionWindowsStaHost _windowsSta;
    private readonly IJoystickSourceFactory _inputSources;
    private readonly IForegroundProcessGuard _foreground = new ForegroundProcessGuard();
    private readonly IInputSender _inputSender = new WindowsInputSender();
    private readonly InjectedKeyStateOwner _injectedKeys;
    private readonly ProductionDesktopBrokerManager _desktopBroker;
    private readonly PadPlugin _pad;
    private readonly PadPluginCommandHandler _padCommands;
    private readonly Task _completion;
    private readonly ProductionRuntimeUiProjector _ui = new();
    private CodexKeybindingService? _keybindings;
    private TaskAlertProductionOwner? _taskAlerts;
    private VoiceProductionOwner? _voiceOwner;
    private PebbleIndexProductionOwner? _pebbleOwner;
    private CompanionProductionOwner? _directInputOwner;
    private TaskAlertProductionOwner? _virpilHealthOwner;
    private long _nextDeviceGeneration;
    private BundledPluginHealth _voiceHealth = new(
        BundledPluginCatalog.VoiceId,
        BundledPluginLifecycleState.Disabled,
        0,
        "Room Voice is disabled.",
        CanRestart: false,
        CanReload: false);
    private BundledPluginHealth _pebbleHealth = new(
        BundledPluginCatalog.PebbleId,
        BundledPluginLifecycleState.Disabled,
        0,
        "Pebble Index is disabled.",
        CanRestart: false,
        CanReload: false);
    private bool _disposed;

    public WindowsProductionRuntimeOwnerFactory(
        RuntimeInputHost inputHost,
        ProductionRuntimePaths paths,
        bool existingCompanionInstall,
        CancellationToken runtimeCancellationToken)
    {
        _inputHost = inputHost ?? throw new ArgumentNullException(nameof(inputHost));
        _runtimeCancellationToken = runtimeCancellationToken;
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _existingCompanionInstall = existingCompanionInstall;
        _log = new FileLog(paths.Log);
        _injectedKeys = new InjectedKeyStateOwner(_inputSender);
        _desktopBroker = new ProductionDesktopBrokerManager(
            paths.DesktopBridgeHost,
            "Joydex.DesktopBridge." + Guid.NewGuid().ToString("N"),
            WriteLog,
            runtimeCancellationToken);
        try
        {
            _windowsSta = ProductionWindowsStaHost.StartAsync(runtimeCancellationToken)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception startupFailure)
        {
            try
            {
                _desktopBroker.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception cleanupFailure)
            {
                throw new ProductionOwnershipCleanupException(
                    "The production Windows boundary failed to start and factory cleanup was incomplete.",
                    [startupFailure, cleanupFailure]);
            }
            throw;
        }
        _inputSources = _windowsSta.InputSources;
        _completion = Task.WhenAny(
                _windowsSta.Completion,
                _desktopBroker.TerminalCompletion)
            .Unwrap();
        _pad = new PadPlugin(
            this,
            static () => new WirelessPanelConfigurationStore().Load(),
            EspHomePadPluginInstanceFactory.Instance,
            static (attempt, cancellationToken) => Task.Delay(
                TimeSpan.FromSeconds(1 << Math.Min(attempt - 1, 2)),
                cancellationToken),
            runtimeCancellationToken);
        _padCommands = new PadPluginCommandHandler(
            _pad,
            WriteLog,
            GetVoiceHealth,
            GetPebbleHealth,
            GetDirectInputHealth,
            GetVirpilHealth);
    }

    public event Action? VoiceBecameIdle;

    public event EventHandler<RuntimeUiEvent>? UiChanged
    {
        add => _ui.Changed += value;
        remove => _ui.Changed -= value;
    }

    internal event EventHandler<TaskAlertSnapshot>? TaskAlertsChanged;

    public Task Completion => _completion;

    public VirpilSettingsTransition? PrepareTaskAlertTransition(SettingsBundle previous, SettingsBundle candidate) =>
        VirpilSettingsTransition.Prepare(
            previous.TaskAlerts.Normalize().LedOutput ?? TaskAlertLedOptions.CreateDefault(),
            candidate.TaskAlerts.Normalize().LedOutput ?? TaskAlertLedOptions.CreateDefault(),
            static () =>
            {
                if (new DirectVirpilConflictDetector().HasConflict())
                {
                    throw new InvalidOperationException(
                        "Close VIRPIL LinkTool and all VPC utilities before enabling Direct USB LED output.");
                }
                var transport = new VirpilHidTransportFactory();
                if (!transport.IsAvailable(VirpilDevices.Throttle) || !transport.IsAvailable(VirpilDevices.Alpha))
                {
                    throw new InvalidOperationException(
                        "Both the CM3 throttle and Constellation Alpha must be connected before enabling Direct USB LED output.");
                }
            },
            new RegistryLoginStartupStore("Joydex.VirpilLinkTool"),
            _paths.LinkToolProfile);

    internal IReadOnlyList<BundledPluginRegistration> Catalog =>
        BundledPluginCatalog.Registrations;

    internal BundledPluginHealth GetPluginHealth(string pluginId)
    {
        _ = BundledPluginCatalog.GetRequired(pluginId);
        return pluginId switch
        {
            BundledPluginCatalog.PadId => _pad.Health,
            BundledPluginCatalog.VoiceId => GetVoiceHealth(),
            BundledPluginCatalog.PebbleId => GetPebbleHealth(),
            BundledPluginCatalog.DirectInputId => GetDirectInputHealth(),
            BundledPluginCatalog.VirpilId => GetVirpilHealth(),
            _ => throw new ArgumentOutOfRangeException(nameof(pluginId)),
        };
    }

    public async Task RefreshPluginsAsync(
        SettingsBundle activeSettings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activeSettings);
        ThrowIfDisposed();
        try
        {
            await _pad.RefreshSharedConfigurationAsync(activeSettings.Companion, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            WriteLog(
                $"{BundledPluginCatalog.PadId}: shared policy refresh failed "
                + $"({exception.GetType().Name}).");
        }
        VoiceProductionOwner? voice;
        lock (_stateGate) { voice = _voiceOwner; }
        voice?.RefreshPolicy(activeSettings);
    }

    internal Task RestartPluginAsync(string pluginId, CancellationToken cancellationToken)
    {
        _ = BundledPluginCatalog.GetRequired(pluginId);
        if (!string.Equals(pluginId, BundledPluginCatalog.PadId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "This plugin is managed by its existing runtime controls.");
        }
        ThrowIfDisposed();
        return _pad.RestartAsync(cancellationToken);
    }

    internal Task ReloadPluginAsync(string pluginId, CancellationToken cancellationToken)
    {
        _ = BundledPluginCatalog.GetRequired(pluginId);
        if (!string.Equals(pluginId, BundledPluginCatalog.PadId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "This plugin is managed by its existing runtime controls.");
        }
        ThrowIfDisposed();
        return _pad.ReloadAsync(cancellationToken);
    }

    public RuntimeUiSnapshot GetUiSnapshot() => _ui.GetSnapshot();

    public async Task<IProductionRuntimeOwner?> CreateAsync(
        SettingsAggregateId aggregate,
        SettingsBundle activeSettings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activeSettings);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return aggregate switch
        {
            SettingsAggregateId.TaskAlerts => CreateTaskAlerts(activeSettings),
            SettingsAggregateId.Companion => await CreateCompanionAsync(activeSettings, cancellationToken)
                .ConfigureAwait(false),
            SettingsAggregateId.Voice => await CreateVoiceAsync(activeSettings, cancellationToken)
                .ConfigureAwait(false),
            SettingsAggregateId.PebbleIndex => await CreatePebbleIndexAsync(activeSettings, cancellationToken)
                .ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(aggregate)),
        };
    }

    public Task<RuntimeCommandResult> ExecuteAsync(
        RuntimeCommandRequest request,
        SettingsBundle activeSettings,
        CancellationToken cancellationToken) => request.Kind is
            RuntimeCommandKind.InspectPlugins
            or RuntimeCommandKind.RestartPlugin
            or RuntimeCommandKind.ReloadPluginConfiguration
                ? _padCommands.ExecuteAsync(request, cancellationToken)
                : ExecuteCommandAsync(request, activeSettings, cancellationToken);

    public void ReportFailure(SettingsAggregateId aggregate, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        WriteLog($"The {aggregate} runtime owner reported an error: {exception.Message}");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var failures = new List<Exception>();
        try
        {
            RestoreActiveVoicePreferences(preferences: null);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        try
        {
            await _pad.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        try
        {
            await _desktopBroker.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (_keybindings is not null)
        {
            try
            {
                await _keybindings.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        try
        {
            _injectedKeys.ReleaseAll();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            await _windowsSta.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        if (failures.Count > 0)
        {
            throw new AggregateException("Production adapter cleanup did not complete.", failures);
        }
    }

    internal TaskAlertSnapshot GetTaskAlertSnapshot()
    {
        lock (_stateGate)
        {
            return _taskAlerts?.Snapshot
                ?? new TaskAlertSnapshot(false, [], 0);
        }
    }

    internal bool AcknowledgeTerminalTaskAlert(int slot, string sessionId)
    {
        lock (_stateGate)
        {
            return _taskAlerts?.AcknowledgeTerminal(slot, sessionId) == true;
        }
    }

    event EventHandler<TaskAlertSnapshot>? IPadPluginHostServices.TaskAlertsChanged
    {
        add => TaskAlertsChanged += value;
        remove => TaskAlertsChanged -= value;
    }

    TaskAlertSnapshot IPadPluginHostServices.GetTaskAlertSnapshot() =>
        GetTaskAlertSnapshot();

    bool IPadPluginHostServices.AcknowledgeTerminalTaskAlert(int slot, string sessionId) =>
        AcknowledgeTerminalTaskAlert(slot, sessionId);

    PadCommandPolicy IPadPluginHostServices.CreatePadCommandPolicy(CompanionConfig config)
    {
        var navigator = new TaskDeepLinkNavigator(config.Safety, WriteLog);
        var executor = CreateActionExecutor(config);
        return new PadCommandPolicy(navigator, executor.ExecuteAsync);
    }

    void IPadPluginHostServices.WritePadLog(string message) => WriteLog(message);

    internal CodexActionExecutor CreateActionExecutor(
        CompanionConfig config,
        Action<Joydex.Core.Mapping.ActionRequest>? internalAction = null) => new(
        config.Safety,
        WriteActivity,
        GetKeybindings(),
        config.OpenWorkingDirectory,
        _foreground,
        _inputSender,
        internalAction: internalAction,
        injectedKeyStateOwner: _injectedKeys);

    VoiceHostCommandPolicy IVoicePluginHostServices.CreateVoiceHostPolicy(
        SettingsBundle activeSettings)
    {
        var navigator = new PinnedVoiceTargetNavigator(
            activeSettings.Companion.Safety,
            WriteLog);
        var executor = CreateActionExecutor(activeSettings.Companion);
        return new VoiceHostCommandPolicy(
            activeSettings.Voice.Normalize(),
            activeSettings.Companion.Safety,
            navigator,
            executor.ExecuteAsync);
    }

    void IVoicePluginHostServices.PublishVoice(ProductionVoiceState state, bool reset) =>
        PublishVoice(state, reset);

    void IVoicePluginHostServices.PublishVoiceBecameIdle() => PublishVoiceBecameIdle();

    void IVoicePluginHostServices.PublishVoiceHealth(
        VoiceProductionOwner owner,
        BundledPluginHealth health) => PublishVoiceHealth(owner, health);

    void IVoicePluginHostServices.ClearVoiceOwner(VoiceProductionOwner owner) =>
        ClearVoiceOwner(owner);

    void IVoicePluginHostServices.WriteLog(string message) => WriteLog(message);

    void IPebblePluginHostServices.PublishPebble(PebbleWorkerStatus status) =>
        PublishPebbleIndexStatus(status);

    void IPebblePluginHostServices.PublishPebbleHealth(
        PebbleIndexProductionOwner owner,
        BundledPluginHealth health) => PublishPebbleHealth(owner, health);

    void IPebblePluginHostServices.ClearPebbleOwner(PebbleIndexProductionOwner owner) =>
        ClearPebbleOwner(owner);

    void IPebblePluginHostServices.WriteLog(string message) => WriteLog(message);

    internal void PublishTaskAlerts(TaskAlertProductionOwner owner, TaskAlertSnapshot snapshot)
    {
        lock (_stateGate)
        {
            if (!ReferenceEquals(_taskAlerts, owner))
            {
                return;
            }
        }

        if (TaskAlertsChanged is { } handlers)
        {
            foreach (EventHandler<TaskAlertSnapshot> handler in handlers.GetInvocationList())
            {
                try { handler(this, snapshot); }
                catch (Exception exception) { WriteLog("Could not update a task-alert presentation adapter: " + exception.Message); }
            }
        }
        _ui.PublishTaskAlerts(snapshot, InspectTaskAlertHooks());
    }

    internal void ClearTaskAlerts(TaskAlertProductionOwner owner)
    {
        lock (_stateGate)
        {
            if (ReferenceEquals(_taskAlerts, owner))
            {
                _taskAlerts = null;
            }
        }
    }

    internal void PublishVoiceBecameIdle() => VoiceBecameIdle?.Invoke();

    internal void PublishPebbleIndexStatus(PebbleWorkerStatus status)
    {
        WriteLog(status.Message);
        _ui.PublishPebble(status, _paths.PebbleIndexInbox);
    }

    internal void PublishPromptPicker(PromptPickerSnapshot snapshot) =>
        _ui.PublishPromptPicker(snapshot);

    internal void OnPromptPickerChanged(object? sender, PromptPickerSnapshot snapshot) =>
        PublishPromptPicker(snapshot);

    internal void PublishButtonMap(string deviceId, bool visible) =>
        _ui.PublishButtonMap(deviceId, visible);

    internal void PublishController(
        string deviceId,
        string displayName,
        string status,
        bool hasButtonMap) =>
        _ui.PublishController(deviceId, displayName, status, hasButtonMap);

    internal void PublishVoice(ProductionVoiceState state, bool reset = false) =>
        _ui.PublishVoice(state, reset);

    internal void PublishVoiceHealth(VoiceProductionOwner owner, BundledPluginHealth health)
    {
        lock (_stateGate)
        {
            if (_voiceOwner is null || ReferenceEquals(_voiceOwner, owner))
            {
                _voiceHealth = health;
            }
        }
    }

    internal void ClearVoiceOwner(VoiceProductionOwner owner)
    {
        lock (_stateGate)
        {
            if (ReferenceEquals(_voiceOwner, owner))
            {
                _voiceOwner = null;
            }
        }
    }

    internal void PublishPebbleHealth(
        PebbleIndexProductionOwner owner,
        BundledPluginHealth health)
    {
        lock (_stateGate)
        {
            if (_pebbleOwner is null || ReferenceEquals(_pebbleOwner, owner))
            {
                _pebbleHealth = health;
            }
        }
    }

    internal void ClearPebbleOwner(PebbleIndexProductionOwner owner)
    {
        lock (_stateGate)
        {
            if (ReferenceEquals(_pebbleOwner, owner))
            {
                _pebbleOwner = null;
            }
        }
    }

    internal void PublishDesktopTasks(IReadOnlyList<DesktopTaskSummary> tasks) =>
        _ui.PublishDesktopTasks(tasks);

    public void RefreshVoiceMessaging(VoicePePreferences preferences)
    {
        var normalized = SetActiveVoicePreferences(preferences);
        _ui.RefreshVoiceMessaging(normalized);
    }

    public void CommitVoiceActivation(VoicePePreferences preferences)
    {
        var normalized = preferences.Normalize();
        VoiceProductionOwner? owner;
        lock (_stateGate) { owner = _voiceOwner; }
        if (normalized.Enabled)
        {
            if (owner is null || !owner.MatchesPreferences(normalized))
            {
                return;
            }
            ApplyVoiceTaskAlertExclusion(normalized);
            owner.Commit();
        }
        else
        {
            ApplyVoiceTaskAlertExclusion(normalized);
        }
        _ui.RefreshVoiceMessaging(normalized);
        if (!normalized.Enabled)
        {
            lock (_stateGate)
            {
                _voiceHealth = new BundledPluginHealth(
                    BundledPluginCatalog.VoiceId,
                    BundledPluginLifecycleState.Disabled,
                    _voiceHealth.Generation,
                    "Room Voice is disabled.",
                    CanRestart: false,
                    CanReload: false);
            }
        }
    }

    public void CommitPebbleActivation(PebbleIndexPreferences preferences)
    {
        var normalized = preferences.Normalize();
        PebbleIndexProductionOwner? owner;
        lock (_stateGate) { owner = _pebbleOwner; }
        if (normalized.Enabled)
        {
            if (owner is null || !owner.MatchesPreferences(normalized))
            {
                return;
            }
            owner.Commit();
        }
        else
        {
            lock (_stateGate)
            {
                _pebbleHealth = new BundledPluginHealth(
                    BundledPluginCatalog.PebbleId,
                    BundledPluginLifecycleState.Disabled,
                    _pebbleHealth.Generation,
                    "Pebble Index is disabled.",
                    CanRestart: false,
                    CanReload: false);
            }
        }
    }

    internal void WriteActivity(string message)
    {
        WriteLog(message);
        _ui.PublishActivity(message);
    }

    internal RuntimeTaskAlertHookStatus InspectTaskAlertHooks()
    {
        try
        {
            var state = CreateTaskAlertHookManager().Inspect(TaskAlertHookRelayPath());
            return new RuntimeTaskAlertHookStatus(state switch
            {
                JoydexHookState.NotInstalled => RuntimeTaskAlertHookState.NotInstalled,
                JoydexHookState.Installed => RuntimeTaskAlertHookState.Installed,
                JoydexHookState.RepairNeeded => RuntimeTaskAlertHookState.RepairNeeded,
                _ => throw new ArgumentOutOfRangeException(nameof(state)),
            });
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.Text.Json.JsonException
            or InvalidDataException)
        {
            return new RuntimeTaskAlertHookStatus(
                RuntimeTaskAlertHookState.RepairNeeded,
                "Task-alert hooks could not be inspected: " + exception.Message);
        }
    }

    internal void WriteLog(string message)
    {
        try
        {
            _log.Write(message);
        }
        catch
        {
            // Runtime ownership and cleanup remain authoritative when diagnostics are unavailable.
        }
    }

    private TaskAlertProductionOwner CreateTaskAlerts(SettingsBundle activeSettings)
    {
        var owner = _windowsSta.InvokeAsync(() => TaskAlertProductionOwner.Start(
                this,
                _windowsSta,
                _paths,
                activeSettings,
                Interlocked.Increment(ref _nextDeviceGeneration)))
            .GetAwaiter()
            .GetResult();
        lock (_stateGate)
        {
            _taskAlerts = owner;
            _virpilHealthOwner = owner;
        }
        PublishTaskAlerts(owner, owner.Snapshot);
        return owner;
    }

    private async Task<CompanionProductionOwner> CreateCompanionAsync(
        SettingsBundle activeSettings,
        CancellationToken cancellationToken)
    {
        await EnsureKeybindingsAsync(cancellationToken).ConfigureAwait(false);
        // The previous owner has drained; none of its retained device/UI state belongs
        // to the replacement, including when settings roll back to an earlier owner.
        _ui.ResetCompanion();
        var owner = await _windowsSta.InvokeAsync(() => CompanionProductionOwner.StartAsync(
                this,
                _windowsSta,
                _inputHost,
                _inputSources,
                activeSettings,
                Interlocked.Increment(ref _nextDeviceGeneration)),
                cancellationToken)
            .ConfigureAwait(false);
        lock (_stateGate) { _directInputOwner = owner; }
        return owner;
    }

    private async Task<IProductionRuntimeOwner?> CreateVoiceAsync(
        SettingsBundle activeSettings,
        CancellationToken cancellationToken)
    {
        var preferences = activeSettings.Voice.Normalize();
        var previousPreferences = GetActiveVoicePreferences();
        _ = SetActiveVoicePreferences(preferences);
        VoiceProductionOwner? owner = null;
        try
        {
            if (!preferences.Enabled)
            {
                _ui.ClearVoice();
                return null;
            }

            await EnsureKeybindingsAsync(cancellationToken).ConfigureAwait(false);
            owner = await VoiceProductionOwner.StartAsync(
                    this,
                    _paths,
                    _desktopBroker,
                    activeSettings,
                    _runtimeCancellationToken,
                    cancellationToken)
                .ConfigureAwait(false);
            lock (_stateGate)
            {
                _voiceOwner = owner;
                _voiceHealth = owner.HealthSnapshot;
            }
            return owner;
        }
        catch (Exception startupFailure)
        {
            var cleanupFailures = new List<Exception>();
            if (owner is not null)
            {
                try
                {
                    await owner.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    cleanupFailures.Add(exception);
                }
            }
            try
            {
                RestoreActiveVoicePreferences(previousPreferences);
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }

            if (cleanupFailures.Count > 0)
            {
                throw new ProductionOwnershipCleanupException(
                    "Room Voice startup failed and active-preferences cleanup was incomplete.",
                    [startupFailure, .. cleanupFailures]);
            }
            throw;
        }
    }

    private VoicePePreferences? GetActiveVoicePreferences()
        => _ui.GetActiveVoicePreferences();

    private VoicePePreferences SetActiveVoicePreferences(VoicePePreferences preferences)
    {
        var normalized = preferences.Normalize();
        VoicePePreferencesStore.Save(_paths.VoiceActivePreferences, normalized);
        _ui.SetActiveVoicePreferences(normalized);
        return normalized;
    }

    private void RestoreActiveVoicePreferences(VoicePePreferences? preferences)
    {
        if (preferences is null)
        {
            File.Delete(_paths.VoiceActivePreferences);
        }
        else
        {
            VoicePePreferencesStore.Save(_paths.VoiceActivePreferences, preferences);
        }
        _ui.SetActiveVoicePreferences(preferences);
    }

    private async Task<IProductionRuntimeOwner?> CreatePebbleIndexAsync(
        SettingsBundle activeSettings,
        CancellationToken cancellationToken)
    {
        var preferences = activeSettings.PebbleIndex.Normalize();
        if (!preferences.Enabled)
        {
            return null;
        }

        var owner = await PebbleIndexProductionOwner.StartAsync(
                this,
                _paths,
                _desktopBroker,
                preferences,
                _runtimeCancellationToken,
                cancellationToken)
            .ConfigureAwait(false);
        lock (_stateGate)
        {
            _pebbleOwner = owner;
            _pebbleHealth = owner.HealthSnapshot;
        }
        return owner;
    }

    private CodexKeybindingService GetKeybindings() => _keybindings
        ?? throw new InvalidOperationException("The Codex keybinding service has not been initialized.");

    private async Task EnsureKeybindingsAsync(CancellationToken cancellationToken)
    {
        if (_keybindings is not null)
        {
            return;
        }

        var candidate = CodexKeybindingService.CreateDefault(WriteLog, _existingCompanionInstall);
        try
        {
            await candidate.InitializeAsync(cancellationToken).ConfigureAwait(false);
            _keybindings = candidate;
        }
        catch (Exception startupFailure)
        {
            try
            {
                await candidate.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                throw new ProductionOwnershipCleanupException(
                    "Codex keybinding startup failed and cleanup was incomplete.",
                    [startupFailure, cleanupFailure]);
            }
            throw;
        }
    }

    private void ApplyVoiceTaskAlertExclusion(VoicePePreferences preferences)
    {
        var taskIds = preferences.Normalize().SessionMode == VoicePeSessionMode.JoydexOwner
            && CodexTaskReference.TryParse(preferences.DedicatedTaskId, out var taskId)
                ? new[] { taskId }
                : [];
        lock (_stateGate)
        {
            _taskAlerts?.SetInternallySuppressedTaskIds(taskIds);
        }
    }

    private BundledPluginHealth GetVoiceHealth()
    {
        lock (_stateGate) { return _voiceHealth; }
    }

    private BundledPluginHealth GetPebbleHealth()
    {
        lock (_stateGate) { return _pebbleHealth; }
    }

    private BundledPluginHealth GetDirectInputHealth()
    {
        lock (_stateGate)
        {
            return _directInputOwner?.HealthSnapshot ?? new(
                BundledPluginCatalog.DirectInputId, BundledPluginLifecycleState.Stopped,
                0, "Controller acquisition has not started.", false, false);
        }
    }

    private BundledPluginHealth GetVirpilHealth()
    {
        lock (_stateGate)
        {
            return _virpilHealthOwner?.HealthSnapshot ?? new(
                BundledPluginCatalog.VirpilId, BundledPluginLifecycleState.Stopped,
                0, "VIRPIL hardware ownership has not started.", false, false);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
