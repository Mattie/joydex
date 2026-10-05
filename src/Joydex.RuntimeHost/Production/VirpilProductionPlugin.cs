using Joydex.App;
using Joydex.Core.TaskAlerts;
using Joydex.RuntimeHost.Plugins;
using Joydex.Virpil;
using Joydex.Windows.TaskAlerts;

namespace Joydex.RuntimeHost.Production;

/// <summary>Owns the process-local VIRPIL read/write and Guardian lifetime as one aggregate.</summary>
internal sealed class VirpilProductionPlugin : IAsyncDisposable
{
    private readonly ITaskAlertLedOutput _led;
    private readonly IAsyncDisposable _shift;
    private readonly IDisposable _devices;
    private readonly IVirpilGuardian _guardian;
    private readonly Action _unsubscribeDevices;
    private readonly Action<string> _log;
    private readonly long _generation;
    private readonly object _gate = new();
    private Task? _disposeTask;
    private BundledPluginLifecycleState _state = BundledPluginLifecycleState.Ready;
    private string _detail = "VIRPIL owner is running; physical output is not verified.";

    internal VirpilProductionPlugin(ITaskAlertLedOutput led, IAsyncDisposable shift,
        IDisposable devices, IVirpilGuardian guardian, Action unsubscribeDevices,
        Action<string> log, long generation)
    {
        _led = led;
        _shift = shift;
        _devices = devices;
        _guardian = guardian;
        _unsubscribeDevices = unsubscribeDevices;
        _log = log;
        _generation = generation;
        _led.ProfileDirtyChanged += OnDirty;
    }

    internal BundledPluginHealth HealthSnapshot
    {
        get { lock (_gate) return new("joydex.virpil", _state, _generation, _detail, false, false); }
    }

    internal static VirpilProductionPlugin Start(TaskAlertCoordinator coordinator,
        ProductionRuntimePaths paths, Action<string> log, long generation)
    {
        var shift = new VirpilShiftModeMonitor(new VirpilShiftModeReader(), coordinator.SetDetectedBank, log);
        ITaskAlertLedOutput? led = null;
        GuardianController? guardian = null;
        DeviceChangeMonitor? devices = null;
        VirpilProductionPlugin? plugin = null;
        try
        {
            var snapshot = coordinator.GetSnapshot();
            led = CreateLedOutput(snapshot, paths, log);
            guardian = new GuardianController(Path.Combine(AppContext.BaseDirectory, "Joydex.Guardian.exe"), log, paths.GuardianRecovery);
            devices = new DeviceChangeMonitor();
            EventHandler changed = (_, _) => plugin!.Replay(coordinator.GetSnapshot().Enabled);
            plugin = new VirpilProductionPlugin(led, shift, devices, guardian,
                () => devices.DevicesChanged -= changed, log, generation);
            devices.DevicesChanged += changed;
            plugin.Apply(snapshot);
            if (snapshot.Enabled && snapshot.Assignments.Count > 0) led.RestoreAndReplay(true);
            shift.Start();
            return plugin;
        }
        catch (Exception startupFailure)
        {
            var failures = new List<Exception> { startupFailure };
            if (plugin is not null)
            {
                try { plugin.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch (Exception exception) { failures.Add(exception); }
            }
            else
            {
                try { shift.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch (Exception exception) { failures.Add(exception); }
                try { devices?.Dispose(); }
                catch (Exception exception) { failures.Add(exception); }
                try { led?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch (Exception exception) { failures.Add(exception); }
                try { guardian?.Dispose(); }
                catch (Exception exception) { failures.Add(exception); }
            }
            if (failures.Count > 1) throw new ProductionOwnershipCleanupException("VIRPIL startup cleanup is unconfirmed.", failures);
            throw;
        }
    }

    internal void Apply(TaskAlertSnapshot snapshot)
    {
        try { _guardian.UpdateRecovery(snapshot); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException or NotSupportedException)
        { _log("Could not update LED guardian recovery state: " + exception.Message); }
        if (snapshot.Enabled && snapshot.Assignments.Count > 0)
        {
            _guardian.Start();
            _guardian.SetRestoreRequired(_led.RestorePending);
        }
        _led.Apply(snapshot);
    }

    internal void SetPaused(bool paused, TaskAlertSnapshot snapshot)
    {
        _led.SetPaused(paused);
        if (!paused)
        {
            Apply(snapshot);
            Replay(snapshot.Enabled);
        }
    }

    private void Replay(bool enabled) => _led.RestoreAndReplay(enabled);
    private void OnDirty(object? sender, bool dirty) => _guardian.SetRestoreRequired(dirty);

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        _state = BundledPluginLifecycleState.Stopping;
        var failures = new List<Exception>();
        try { _unsubscribeDevices(); _devices.Dispose(); }
        catch (Exception exception) { failures.Add(exception); }
        try { await _shift.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { failures.Add(exception); }
        var restored = false;
        try
        {
            _led.SetPaused(true);
            await _led.DisposeAsync().ConfigureAwait(false);
            if (_led.RestorePending)
                throw new IOException("VIRPIL baseline restoration is unconfirmed.");
            restored = true;
        }
        catch (Exception exception) { failures.Add(exception); }
        _led.ProfileDirtyChanged -= OnDirty;
        // The recovery record and Guardian stay armed when physical cleanup is uncertain.
        if (restored && failures.Count == 0)
        {
            try { _guardian.SignalCleanExit(); }
            catch (Exception exception) { failures.Add(exception); }
        }
        try { _guardian.Dispose(); }
        catch (Exception exception) { failures.Add(exception); }
        lock (_gate)
        {
            _state = failures.Count == 0 ? BundledPluginLifecycleState.Stopped : BundledPluginLifecycleState.Blocked;
            _detail = failures.Count == 0 ? "VIRPIL owner stopped." : "VIRPIL cleanup is unconfirmed; replacement requires process exit and Guardian recovery.";
        }
        if (failures.Count != 0) throw new AggregateException(_detail, failures);
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

}
