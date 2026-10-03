using Joydex.Core.Config;
using Joydex.Core.Input;
using Joydex.Core.Runtime;
using Joydex.Core.TaskAlerts;
using Joydex.Windows.Actions;
using Joydex.Windows.Input;
using Joydex.Windows.TaskAlerts;

namespace Joydex.Windows.Runtime;

public sealed class CompanionWorker(
    CompanionConfig config,
    IJoystickSource source,
    CodexActionExecutor executor,
    Action<string> log,
    IInjectedKeyStateLifecycle? keyStateLifecycle = null,
    TaskAlertInputInterceptor? taskAlertInputInterceptor = null,
    ITaskAlertNavigator? taskAlertNavigator = null,
    Func<int, string, bool>? acknowledgeTerminalTaskAlert = null,
    string? deviceId = null,
    Func<PromptPickerRequest, CancellationToken, Task>? promptPickerHandler = null,
    Action<ButtonMapVisibilityRequest>? buttonMapHandler = null,
    RuntimeInputHost? inputHost = null) : IAsyncDisposable
{
    private const int ShutdownCleanupAttempts = 3;
    private const int ShutdownCleanupRetryDelayMs = 100;
    private readonly CompanionConfig _normalizedConfig = CompanionConfigNormalizer.Normalize(config);
    private readonly CompanionEngine _engine = new(config, taskAlertInputInterceptor, deviceId);
    private readonly DeviceSelector _deviceSelector = CompanionConfigNormalizer.Normalize(config).Devices
        .First(device => string.Equals(
            device.Id,
            deviceId ?? CompanionConfigNormalizer.Normalize(config).Devices[0].Id,
            StringComparison.OrdinalIgnoreCase))
        .Selector;
    private readonly string _deviceId = deviceId ?? CompanionConfigNormalizer.Normalize(config).Devices[0].Id;
    private readonly IInjectedKeyStateLifecycle _keyStateLifecycle = keyStateLifecycle ?? executor;
    private readonly RuntimeInputHost _inputHost = inputHost ?? new RuntimeInputHost();
    private readonly bool _ownsInputHost = inputHost is null;
    private CancellationTokenSource? _cancellation;
    private Task? _runTask;
    private InputSourceSession? _inputSession;
    private readonly HashSet<InputSourceSession> _keyCleanupBacklog = [];

    public event EventHandler<string>? StatusChanged;

    public void Start()
    {
        if (_runTask is not null)
        {
            return;
        }

        try
        {
            _keyStateLifecycle.ClearInjectedKeyState();
        }
        catch (Exception exception)
        {
            log($"Could not clear stale push-to-talk keys during startup: {exception.Message}");
        }

        _cancellation = new CancellationTokenSource();
        _runTask = RunGuardedAsync(_cancellation.Token);
    }

    public async Task StopAsync()
    {
        if (_runTask is null || _cancellation is null)
        {
            return;
        }

        _cancellation.Cancel();
        try
        {
            await _runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _runTask = null;
        _cancellation.Dispose();
        _cancellation = null;
        source.Disconnect();
        _engine.Reset();
        SetStatus("Stopped");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        source.Dispose();
        if (_ownsInputHost)
        {
            _inputHost.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    private async Task RunGuardedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            try
            {
                log($"Companion stopped unexpectedly: {exception}");
            }
            catch
            {
                // The worker must remain observable through its tray status even if the log is unavailable.
            }

            SetStatus("Companion stopped; see log");
        }
        finally
        {
            ReleaseCurrentSource("worker shutdown");
            await RetryKeyCleanupBeforeShutdownAsync().ConfigureAwait(false);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (source.ConnectedDevice is null)
            {
                if (!RetryKeyCleanupBacklog("controller reconnect"))
                {
                    SetStatus("Waiting for injected-key cleanup");
                    await Task.Delay(config.Polling.ReconnectIntervalMs, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (source.TryConnect(_deviceSelector, out var connectionMessage))
                {
                    _engine.Reset();
                    var connected = source.ConnectedDevice!;
                    InputSourceSession session = default;
                    session = _inputHost.ConnectSource(
                        new InputSourceDescriptor(
                            _deviceId,
                            connected.ProductName,
                            connected.InstanceGuid.ToString("D")),
                        () => PrepareForCapture(session));
                    _inputSession = session;
                    log(connectionMessage);
                    SetStatus(config.Safety.DryRun
                        ? $"Connected (dry run): {source.ConnectedDevice?.ProductName}"
                        : $"Connected: {source.ConnectedDevice?.ProductName}");
                    await Task.Delay(config.Polling.ConnectWarmupMs, cancellationToken).ConfigureAwait(false);

                    if (source.TryRead(out var baseline, out _) && baseline is not null)
                    {
                        await ProcessSnapshotAsync(baseline, [], session, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    continue;
                }
                else
                {
                    SetStatus("Waiting for controller");
                    await Task.Delay(config.Polling.ReconnectIntervalMs, cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }

            if (!source.TryRead(out var snapshot, out var readError) || snapshot is null)
            {
                if (!string.IsNullOrWhiteSpace(readError))
                {
                    log($"DirectInput disconnected: {readError}");
                }

                ReleaseCurrentSource("controller disconnect");
                RetryKeyCleanupBacklog("controller disconnect");
                _engine.Reset();
                SetStatus("Controller disconnected");
                await Task.Delay(config.Polling.ReconnectIntervalMs, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var currentSession = _inputSession;
            if (currentSession is null)
            {
                throw new InvalidOperationException("The connected controller has no runtime input session.");
            }

            await ProcessSnapshotAsync(
                    snapshot,
                    source.LatestBufferedButtonEvents,
                    currentSession.Value,
                    cancellationToken)
                .ConfigureAwait(false);

            await Task.Delay(config.Polling.PollIntervalMs, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task<EngineResult> ProcessSnapshotAsync(
        JoystickSnapshot snapshot,
        IReadOnlyList<JoystickEvent> bufferedButtonEvents,
        InputSourceSession session,
        CancellationToken cancellationToken)
    {
        var observedEvents = _engine.Observe(snapshot, bufferedButtonEvents);
        return _inputHost.RouteAsync(
            session,
            snapshot,
            observedEvents,
            async routedInput =>
            {
                var result = _engine.ProcessRouted(routedInput.Snapshot, routedInput.Events);
                await DispatchAsync(result, session, cancellationToken).ConfigureAwait(false);
                return result;
            });
    }

    private async Task DispatchAsync(
        EngineResult result,
        InputSourceSession session,
        CancellationToken cancellationToken)
    {
            if (config.Safety.DryRun)
            {
                foreach (var inputEvent in result.InputEvents)
                {
                    var trigger = inputEvent.Kind switch
                    {
                        JoystickEventKind.ButtonPressed => "press",
                        JoystickEventKind.ButtonReleased => "release",
                        _ => null,
                    };
                    if (trigger is not null)
                    {
                        log($"INPUT {trigger} from controller/button {inputEvent.DisplayIndex}");
                    }
                }
            }

            if (promptPickerHandler is not null)
            {
                foreach (var request in result.PromptPickerRequests)
                {
                    try
                    {
                        await promptPickerHandler(request, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        log($"Prompt picker {request.PickerId} {request.Gesture} failed: {exception.Message}");
                        SetStatus("Prompt picker failed; see log");
                    }
                }
            }

            if (buttonMapHandler is not null)
            {
                foreach (var request in result.ButtonMapVisibilityRequests)
                {
                    buttonMapHandler(request);
                }
            }

            if (taskAlertNavigator is not null)
            {
                foreach (var navigation in result.TaskAlertNavigationRequests)
                {
                    var navigated = await taskAlertNavigator
                        .NavigateAsync(navigation, cancellationToken)
                        .ConfigureAwait(false);
                    if (navigated)
                    {
                        acknowledgeTerminalTaskAlert?.Invoke(navigation.Slot, navigation.SessionId);
                    }
                }
            }

            foreach (var request in result.ActionRequests)
            {
                try
                {
                    await executor.ExecuteAsync(
                            request with { SourceGeneration = session.Generation },
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    log($"Action {request.BindingName} failed: {exception.Message}");
                    SetStatus("Action failed; see log");
                }
            }
    }

    private void PrepareForCapture(InputSourceSession session)
    {
        _keyStateLifecycle.ReleaseHeldKeys(session);
        _engine.ResetDispatchState();
        if (buttonMapHandler is not null)
        {
            foreach (var device in _normalizedConfig.Devices.Where(device =>
                         string.Equals(
                             device.ButtonMapHoldControl?.DeviceId,
                             _deviceId,
                             StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    buttonMapHandler(new ButtonMapVisibilityRequest(device.Id, false));
                }
                catch (Exception exception)
                {
                    try
                    {
                        log($"Could not hide button map {device.Id} during capture: {exception.Message}");
                    }
                    catch
                    {
                        // A map callback and diagnostics cannot prevent safe capture preparation.
                    }
                }
            }
        }
    }

    private void ReleaseCurrentSource(string reason)
    {
        if (_inputSession is not { } session)
        {
            return;
        }

        _inputHost.DisconnectSource(session);
        try
        {
            _keyStateLifecycle.ReleaseHeldKeys(session);
        }
        catch (Exception exception)
        {
            _keyCleanupBacklog.Add(session);
            try
            {
                log($"Could not release injected keys during {reason}: {exception.Message}");
            }
            catch
            {
                // Cleanup must not fault the worker if the log has also become unavailable.
            }
        }
        _inputSession = null;
    }

    private bool RetryKeyCleanupBacklog(string reason)
    {
        foreach (var session in _keyCleanupBacklog.ToArray())
        {
            try
            {
                _keyStateLifecycle.ReleaseHeldKeys(session);
                _keyCleanupBacklog.Remove(session);
            }
            catch (Exception exception)
            {
                try
                {
                    log($"Injected-key cleanup is still pending during {reason}: {exception.Message}");
                }
                catch
                {
                    // Cleanup retries continue even if diagnostics are unavailable.
                }
            }
        }
        return _keyCleanupBacklog.Count == 0;
    }

    private async Task RetryKeyCleanupBeforeShutdownAsync()
    {
        for (var attempt = 1; attempt <= ShutdownCleanupAttempts; attempt++)
        {
            var reason = attempt == 1 ? "worker shutdown" : "worker shutdown retry";
            if (RetryKeyCleanupBacklog(reason))
            {
                return;
            }

            if (attempt < ShutdownCleanupAttempts)
            {
                await Task.Delay(ShutdownCleanupRetryDelayMs).ConfigureAwait(false);
            }
        }
    }

    private void SetStatus(string status) => StatusChanged?.Invoke(this, status);
}
