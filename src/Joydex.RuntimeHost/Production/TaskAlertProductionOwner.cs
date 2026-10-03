using Joydex.App;
using Joydex.Contracts;
using Joydex.Core.TaskAlerts;
using Joydex.Virpil;
using Joydex.Windows.TaskAlerts;

namespace Joydex.RuntimeHost.Production;

internal sealed class TaskAlertProductionOwner : IProductionRuntimeOwner
{
    private readonly WindowsProductionRuntimeOwnerFactory _factory;
    private readonly ProductionWindowsStaHost _windowsSta;
    private readonly TaskAlertCoordinator _coordinator;
    private readonly TaskAlertPipeServer _pipe;
    private readonly VirpilShiftModeMonitor _shiftMonitor;
    private readonly ITaskAlertLedOutput _ledOutput;
    private readonly GuardianController _guardian;
    private readonly DeviceChangeMonitor _deviceChangeMonitor;
    private readonly TaskCompletionSource _ownedCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _completion;
    private int _disposed;

    private TaskAlertProductionOwner(
        WindowsProductionRuntimeOwnerFactory factory,
        ProductionWindowsStaHost windowsSta,
        TaskAlertCoordinator coordinator,
        TaskAlertPipeServer pipe,
        VirpilShiftModeMonitor shiftMonitor,
        ITaskAlertLedOutput ledOutput,
        GuardianController guardian,
        DeviceChangeMonitor deviceChangeMonitor)
    {
        _factory = factory;
        _windowsSta = windowsSta;
        _coordinator = coordinator;
        _pipe = pipe;
        _shiftMonitor = shiftMonitor;
        _ledOutput = ledOutput;
        _guardian = guardian;
        _deviceChangeMonitor = deviceChangeMonitor;
        _completion = Task.WhenAny(_ownedCompletion.Task, windowsSta.Completion).Unwrap();
    }

    public SettingsAggregateId Aggregate => SettingsAggregateId.TaskAlerts;

    public Task Completion => _completion;

    internal TaskAlertSnapshot Snapshot => _coordinator.GetSnapshot();

    public static TaskAlertProductionOwner Start(
        WindowsProductionRuntimeOwnerFactory factory,
        ProductionWindowsStaHost windowsSta,
        ProductionRuntimePaths paths,
        SettingsBundle activeSettings)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(windowsSta);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(activeSettings);

        TaskAlertCoordinator? coordinator = null;
        TaskAlertPipeServer? pipe = null;
        VirpilShiftModeMonitor? monitor = null;
        ITaskAlertLedOutput? led = null;
        GuardianController? guardian = null;
        DeviceChangeMonitor? deviceChangeMonitor = null;
        TaskAlertProductionOwner? owner = null;
        try
        {
            coordinator = new TaskAlertCoordinator(
                paths.TaskAlertPreferences,
                activeSettings.TaskAlerts,
                paths.TaskAlertState,
                factory.WriteLog);
            var voice = activeSettings.Voice.Normalize();
            coordinator.SetInternallySuppressedTaskIds(
                voice.SessionMode == Joydex.Core.Voice.VoicePeSessionMode.JoydexOwner
                && Joydex.Core.Voice.CodexTaskReference.TryParse(voice.DedicatedTaskId, out var taskId)
                    ? new[] { taskId }
                    : []);

            pipe = new TaskAlertPipeServer(coordinator, factory.WriteLog);
            monitor = new VirpilShiftModeMonitor(
                new VirpilShiftModeReader(),
                coordinator.SetDetectedBank,
                factory.WriteLog);
            var snapshot = coordinator.GetSnapshot();
            led = CreateLedOutput(snapshot, paths, factory.WriteLog);
            guardian = new GuardianController(
                Path.Combine(AppContext.BaseDirectory, "Joydex.Guardian.exe"),
                factory.WriteLog,
                paths.GuardianRecovery);
            deviceChangeMonitor = new DeviceChangeMonitor();

            owner = new TaskAlertProductionOwner(
                factory,
                windowsSta,
                coordinator,
                pipe,
                monitor,
                led,
                guardian,
                deviceChangeMonitor);
            coordinator.Changed += owner.OnChanged;
            led.ProfileDirtyChanged += owner.OnProfileDirtyChanged;
            deviceChangeMonitor.DevicesChanged += owner.OnDevicesChanged;
            owner.UpdateGuardian(snapshot);
            if (snapshot.Enabled && snapshot.Assignments.Count > 0)
            {
                guardian.Start();
                guardian.SetRestoreRequired(led.RestorePending);
                led.RestoreAndReplay(replay: true);
            }
            else
            {
                led.Apply(snapshot);
            }

            monitor.Start();
            pipe.Start();
            return owner;
        }
        catch (Exception startupFailure)
        {
            var cleanupFailures = new List<Exception>();
            if (owner is not null)
            {
                try { coordinator!.Changed -= owner.OnChanged; }
                catch (Exception exception) { cleanupFailures.Add(exception); }
                try { led!.ProfileDirtyChanged -= owner.OnProfileDirtyChanged; }
                catch (Exception exception) { cleanupFailures.Add(exception); }
                try { deviceChangeMonitor!.DevicesChanged -= owner.OnDevicesChanged; }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }
            try { monitor?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch (Exception exception) { cleanupFailures.Add(exception); }
            try { pipe?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch (Exception exception) { cleanupFailures.Add(exception); }
            try { led?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch (Exception exception) { cleanupFailures.Add(exception); }
            try { guardian?.SignalCleanExit(); }
            catch (Exception exception) { cleanupFailures.Add(exception); }
            try { guardian?.Dispose(); }
            catch (Exception exception) { cleanupFailures.Add(exception); }
            try { deviceChangeMonitor?.Dispose(); }
            catch (Exception exception) { cleanupFailures.Add(exception); }
            try { coordinator?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch (Exception exception) { cleanupFailures.Add(exception); }
            if (cleanupFailures.Count > 0)
            {
                cleanupFailures.Insert(0, startupFailure);
                throw new ProductionOwnershipCleanupException(
                    "Task-alert startup failed and cleanup was incomplete.",
                    cleanupFailures);
            }
            throw;
        }
    }

    internal bool AcknowledgeTerminal(int slot, string sessionId) =>
        _coordinator.AcknowledgeTerminal(slot, sessionId);

    internal void SetInternallySuppressedTaskIds(IEnumerable<string> taskIds) =>
        _coordinator.SetInternallySuppressedTaskIds(taskIds);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        return new ValueTask(_windowsSta.InvokeAsync(DisposeOnStaAsync));
    }

    private async Task DisposeOnStaAsync()
    {
        _factory.ClearTaskAlerts(this);
        _coordinator.Changed -= OnChanged;
        _ledOutput.ProfileDirtyChanged -= OnProfileDirtyChanged;
        _deviceChangeMonitor.DevicesChanged -= OnDevicesChanged;
        var failures = new List<Exception>();
        try
        {
            _deviceChangeMonitor.Dispose();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        await TryDisposeAsync(_shiftMonitor, failures);
        await TryDisposeAsync(_pipe, failures);
        try
        {
            _ledOutput.SetPaused(true);
            await _ledOutput.DisposeAsync();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        try
        {
            _guardian.SignalCleanExit();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        try
        {
            _guardian.Dispose();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        await TryDisposeAsync(_coordinator, failures);
        _ownedCompletion.TrySetResult();
        if (failures.Count > 0)
        {
            throw new AggregateException("Task-alert owner cleanup did not complete.", failures);
        }
    }

    private void OnChanged(object? sender, TaskAlertSnapshot snapshot)
    {
        UpdateGuardian(snapshot);
        if (snapshot.Enabled && snapshot.Assignments.Count > 0)
        {
            _guardian.Start();
            _guardian.SetRestoreRequired(_ledOutput.RestorePending);
        }
        _ledOutput.Apply(snapshot);
        _factory.PublishTaskAlerts(this, snapshot);
    }

    private void OnProfileDirtyChanged(object? sender, bool dirty) =>
        _guardian.SetRestoreRequired(dirty);

    private void OnDevicesChanged(object? sender, EventArgs eventArgs)
    {
        _factory.WriteLog("Device-change notification; task-alert profile restore/replay requested.");
        _ledOutput.RestoreAndReplay(Snapshot.Enabled);
    }

    private void UpdateGuardian(TaskAlertSnapshot snapshot)
    {
        try
        {
            _guardian.UpdateRecovery(snapshot);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.Text.Json.JsonException
            or NotSupportedException)
        {
            _factory.WriteLog("Could not update LED guardian recovery state: " + exception.Message);
        }
    }

    private static ITaskAlertLedOutput CreateLedOutput(
        TaskAlertSnapshot snapshot,
        ProductionRuntimePaths paths,
        Action<string> log)
    {
        var options = snapshot.EffectiveLedOutput;
        if (options.Mode == TaskAlertLedOutputMode.DirectHid)
        {
            return new DirectVirpilLedService(
                new VirpilHidTransportFactory(),
                new DirectVirpilConflictDetector(),
                log,
                snapshot,
                options);
        }

        try
        {
            LinkToolProfileWriter.Write(paths.LinkToolProfile, options);
            log($"Joydex LinkTool profile written to {paths.LinkToolProfile}.");
        }
        catch (Exception exception)
        {
            log("Could not write the Joydex LinkTool profile: " + exception.Message);
        }
        return new LinkToolLedService(
            new UdpLinkToolTelemetrySender(),
            new VpcConflictDetector(),
            log,
            snapshot);
    }

    private static async Task TryDisposeAsync(IAsyncDisposable disposable, List<Exception> failures)
    {
        try
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }
}
