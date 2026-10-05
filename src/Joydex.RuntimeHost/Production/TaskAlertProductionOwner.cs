using Joydex.App;
using Joydex.Contracts;
using Joydex.Core.TaskAlerts;
using Joydex.Virpil;
using Joydex.Windows.TaskAlerts;
using Microsoft.Win32;

namespace Joydex.RuntimeHost.Production;

internal sealed class TaskAlertProductionOwner : IProductionRuntimeOwner
{
    private readonly WindowsProductionRuntimeOwnerFactory _factory;
    private readonly ProductionWindowsStaHost _windowsSta;
    private readonly TaskAlertCoordinator _coordinator;
    private readonly TaskAlertPipeServer _pipe;
    private readonly VirpilProductionPlugin _virpil;
    private readonly TaskCompletionSource _ownedCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _completion;
    private int _disposed;
    private Task? _disposeTask;
    private readonly object _disposeGate = new();

    private TaskAlertProductionOwner(
        WindowsProductionRuntimeOwnerFactory factory,
        ProductionWindowsStaHost windowsSta,
        TaskAlertCoordinator coordinator,
        TaskAlertPipeServer pipe,
        VirpilProductionPlugin virpil)
    {
        _factory = factory;
        _windowsSta = windowsSta;
        _coordinator = coordinator;
        _pipe = pipe;
        _virpil = virpil;
        _completion = Task.WhenAny(_ownedCompletion.Task, windowsSta.Completion).Unwrap();
    }

    public SettingsAggregateId Aggregate => SettingsAggregateId.TaskAlerts;

    public Task Completion => _completion;

    internal Joydex.RuntimeHost.Plugins.BundledPluginHealth HealthSnapshot => _virpil.HealthSnapshot;

    internal TaskAlertSnapshot Snapshot => _coordinator.GetSnapshot();

    public static TaskAlertProductionOwner Start(
        WindowsProductionRuntimeOwnerFactory factory,
        ProductionWindowsStaHost windowsSta,
        ProductionRuntimePaths paths,
        SettingsBundle activeSettings,
        long generation = 1)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(windowsSta);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(activeSettings);

        TaskAlertCoordinator? coordinator = null;
        TaskAlertPipeServer? pipe = null;
        VirpilProductionPlugin? virpil = null;
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
            virpil = VirpilProductionPlugin.Start(coordinator, paths, factory.WriteLog, generation);
            owner = new TaskAlertProductionOwner(factory, windowsSta, coordinator, pipe, virpil);
            coordinator.Changed += owner.OnChanged;
            SystemEvents.PowerModeChanged += owner.OnPowerModeChanged;
            SystemEvents.SessionEnding += owner.OnSessionEnding;
            pipe.Start();
            return owner;
        }
        catch (Exception startupFailure)
        {
            var cleanupFailures = new List<Exception>();
            if (owner is not null)
            {
                Interlocked.Exchange(ref owner._disposed, 1);
                try { SystemEvents.PowerModeChanged -= owner.OnPowerModeChanged; }
                catch (Exception exception) { cleanupFailures.Add(exception); }
                try { SystemEvents.SessionEnding -= owner.OnSessionEnding; }
                catch (Exception exception) { cleanupFailures.Add(exception); }
                try { coordinator!.Changed -= owner.OnChanged; }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }
            try { pipe?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch (Exception exception) { cleanupFailures.Add(exception); }
            try { virpil?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
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
        lock (_disposeGate)
            return new ValueTask(_disposeTask ??= _windowsSta.InvokeAsync(DisposeOnStaAsync));
    }

    private async Task DisposeOnStaAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionEnding -= OnSessionEnding;
        _factory.ClearTaskAlerts(this);
        _coordinator.Changed -= OnChanged;
        var failures = new List<Exception>();
        await TryDisposeAsync(_pipe, failures);
        await TryDisposeAsync(_virpil, failures);
        await TryDisposeAsync(_coordinator, failures);
        _ownedCompletion.TrySetResult();
        if (failures.Count > 0) throw new AggregateException("Task-alert owner cleanup did not complete.", failures);
    }

    private void OnChanged(object? sender, TaskAlertSnapshot snapshot)
    {
        _virpil.Apply(snapshot);
        _factory.PublishTaskAlerts(this, snapshot);
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs eventArgs)
    {
        if (eventArgs.Mode == PowerModes.Suspend)
        {
            SetPowerPaused(true);
        }
        else if (eventArgs.Mode == PowerModes.Resume)
        {
            SetPowerPaused(false);
        }
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs eventArgs) => SetPowerPaused(true);

    private void SetPowerPaused(bool paused)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }
        try
        {
            _windowsSta.Invoke(() =>
            {
                // A system event may already be queued when this owner is replaced.
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }
                _virpil.SetPaused(paused, Snapshot);
            });
        }
        catch (Exception exception)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                _ownedCompletion.TrySetException(exception);
            }
        }
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
