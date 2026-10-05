using Joydex.App;
using Joydex.Contracts;
using Joydex.Core.Config;
using Joydex.Core.Runtime;
using Joydex.Core.TaskAlerts;
using Joydex.Core.Voice;
using Joydex.RuntimeHost.Plugins;
using Joydex.WirelessPanel;
using Joydex.Windows.Actions;
using Joydex.Windows.Input;
using Joydex.Windows.TaskAlerts;

namespace Joydex.RuntimeHost.Production;

internal sealed partial class WindowsProductionRuntimeOwnerFactory :
    IProductionRuntimeOwnerFactory,
    IPadPluginHostServices
{
    private readonly object _stateGate = new();
    private readonly RuntimeInputHost _inputHost;
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
    private bool _disposed;

    public WindowsProductionRuntimeOwnerFactory(
        RuntimeInputHost inputHost,
        ProductionRuntimePaths paths,
        bool existingCompanionInstall,
        CancellationToken runtimeCancellationToken)
    {
        _inputHost = inputHost ?? throw new ArgumentNullException(nameof(inputHost));
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
        _padCommands = new PadPluginCommandHandler(_pad, WriteLog);
    }

    public event Action? VoiceBecameIdle;

    public event EventHandler<RuntimeUiEvent>? UiChanged
    {
        add => _ui.Changed += value;
        remove => _ui.Changed -= value;
    }

    internal event EventHandler<TaskAlertSnapshot>? TaskAlertsChanged;

    public Task Completion => _completion;

    internal IReadOnlyList<BundledPluginRegistration> Catalog =>
        BundledPluginCatalog.Registrations;

    internal BundledPluginHealth GetPluginHealth(string pluginId)
    {
        _ = BundledPluginCatalog.GetRequired(pluginId);
        return _pad.Health;
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
    }

    internal Task RestartPluginAsync(string pluginId, CancellationToken cancellationToken)
    {
        _ = BundledPluginCatalog.GetRequired(pluginId);
        ThrowIfDisposed();
        return _pad.RestartAsync(cancellationToken);
    }

    internal Task ReloadPluginAsync(string pluginId, CancellationToken cancellationToken)
    {
        _ = BundledPluginCatalog.GetRequired(pluginId);
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

    internal void PublishPebbleIndexStatus(PebbleIndexReceiverStatus status)
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

    internal void PublishDesktopTasks(IReadOnlyList<DesktopTaskSummary> tasks) =>
        _ui.PublishDesktopTasks(tasks);

    public void RefreshVoiceMessaging(VoicePePreferences preferences)
    {
        var normalized = SetActiveVoicePreferences(preferences);
        _ui.RefreshVoiceMessaging(normalized);
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
                activeSettings))
            .GetAwaiter()
            .GetResult();
        lock (_stateGate)
        {
            _taskAlerts = owner;
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
        return await _windowsSta.InvokeAsync(() => CompanionProductionOwner.StartAsync(
                this,
                _windowsSta,
                _inputHost,
                _inputSources,
                activeSettings),
                cancellationToken)
            .ConfigureAwait(false);
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
                ApplyVoiceTaskAlertExclusion(preferences);
                _ui.ClearVoice();
                return null;
            }

            await EnsureKeybindingsAsync(cancellationToken).ConfigureAwait(false);
            owner = await VoiceProductionOwner.StartAsync(
                    this,
                    _paths,
                    _desktopBroker,
                    activeSettings,
                    cancellationToken)
                .ConfigureAwait(false);
            ApplyVoiceTaskAlertExclusion(preferences);
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
        if (!activeSettings.PebbleIndex.Normalize().Enabled)
        {
            return null;
        }

        return await PebbleIndexProductionOwner.StartAsync(
                this,
                _paths,
                _desktopBroker,
                activeSettings.PebbleIndex,
                cancellationToken)
            .ConfigureAwait(false);
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

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
