using Joydex.Core.Config;
using Joydex.Core.Mapping;
using Joydex.Core.TaskAlerts;
using Joydex.Windows.Actions;
using Joydex.Windows.TaskAlerts;

namespace Joydex.Windows.WirelessPanel;

/// <summary>
/// Projects Joydex task alerts onto the fixed ESPHome screen and routes its five touch targets
/// through the existing task navigator and semantic action executor.
/// </summary>
public sealed class EspHomePanelAdapter : IAsyncDisposable
{
    private static readonly TimeSpan DefaultStateRetryDelay = TimeSpan.FromSeconds(2);

    private readonly IEspHomePanelTransport _transport;
    private readonly Func<TaskAlertSnapshot> _getSnapshot;
    private readonly ITaskAlertNavigator _navigator;
    private readonly Func<int, string, bool> _acknowledgeTerminal;
    private readonly Func<ActionRequest, CancellationToken, Task<ActionExecutionResult>> _executeAction;
    private readonly Action<string> _log;
    private readonly TimeSpan _stateRetryDelay;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SemaphoreSlim _publishSignal = new(0, 1);
    private readonly object _lifecycleGate = new();
    private TaskAlertSnapshot _latestSnapshot;
    private Task? _eventLoopObserverTask;
    private Task? _publisherTask;
    private EspHomePanelSnapshot? _lastPublishedSnapshot;
    private long _requestedReconnectGeneration;
    private long _completedReconnectGeneration;
    private bool _started;
    private bool _disposed;

    /// <summary>Creates the host-side policy adapter for one ESPHome panel.</summary>
    public EspHomePanelAdapter(
        IEspHomePanelTransport transport,
        TaskAlertSnapshot initialSnapshot,
        Func<TaskAlertSnapshot> getSnapshot,
        ITaskAlertNavigator navigator,
        Func<int, string, bool> acknowledgeTerminal,
        Func<ActionRequest, CancellationToken, Task<ActionExecutionResult>> executeAction,
        Action<string> log)
        : this(
            transport,
            initialSnapshot,
            getSnapshot,
            navigator,
            acknowledgeTerminal,
            executeAction,
            log,
            DefaultStateRetryDelay)
    {
    }

    internal EspHomePanelAdapter(
        IEspHomePanelTransport transport,
        TaskAlertSnapshot initialSnapshot,
        Func<TaskAlertSnapshot> getSnapshot,
        ITaskAlertNavigator navigator,
        Func<int, string, bool> acknowledgeTerminal,
        Func<ActionRequest, CancellationToken, Task<ActionExecutionResult>> executeAction,
        Action<string> log,
        TimeSpan stateRetryDelay)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _latestSnapshot = initialSnapshot ?? throw new ArgumentNullException(nameof(initialSnapshot));
        _getSnapshot = getSnapshot ?? throw new ArgumentNullException(nameof(getSnapshot));
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        _acknowledgeTerminal = acknowledgeTerminal ?? throw new ArgumentNullException(nameof(acknowledgeTerminal));
        _executeAction = executeAction ?? throw new ArgumentNullException(nameof(executeAction));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        if (stateRetryDelay < TimeSpan.Zero || stateRetryDelay > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(stateRetryDelay));
        }

        _stateRetryDelay = stateRetryDelay;
    }

    /// <summary>Starts the outbound event loop and latest-state publisher.</summary>
    public void Start()
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                throw new InvalidOperationException("The ESPHome panel adapter is already running.");
            }

            _started = true;
            _publisherTask = PublishLoopAsync(_cancellation.Token);
            _eventLoopObserverTask = ObserveEventLoopAsync(
                _transport.RunAsync(
                    HandlePressedAsync,
                    QueueReconnectSnapshotAsync,
                    _cancellation.Token));
        }

        SignalPublisher();
    }

    /// <summary>
    /// Replaces the latest desired host state. This method is synchronous and never performs
    /// network I/O, so it is safe to call from <see cref="TaskAlertCoordinator.Changed"/>.
    /// </summary>
    public void Apply(TaskAlertSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }

            _latestSnapshot = snapshot;
        }

        SignalPublisher();
    }

    /// <summary>Stops all panel work and releases the transport.</summary>
    public async ValueTask DisposeAsync()
    {
        Task? publisherTask;
        Task? eventLoopObserverTask;
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cancellation.Cancel();
            publisherTask = _publisherTask;
            eventLoopObserverTask = _eventLoopObserverTask;
        }

        SignalPublisher();
        if (publisherTask is not null)
        {
            await publisherTask.ConfigureAwait(false);
        }

        if (eventLoopObserverTask is not null)
        {
            await eventLoopObserverTask.ConfigureAwait(false);
        }

        await _transport.DisposeAsync().ConfigureAwait(false);
        _publishSignal.Dispose();
        _cancellation.Dispose();
        GC.SuppressFinalize(this);
    }

    internal static EspHomePanelSnapshot Project(TaskAlertSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.Enabled)
        {
            return EspHomePanelSnapshot.Empty;
        }

        return new EspHomePanelSnapshot(
            ProjectSlot(snapshot, 1),
            ProjectSlot(snapshot, 2),
            ProjectSlot(snapshot, 3),
            ProjectSlot(snapshot, 4));
    }

    private static EspHomeTaskState ProjectSlot(TaskAlertSnapshot snapshot, int slot)
    {
        var assignment = snapshot.Assignments.FirstOrDefault(candidate => candidate.Slot == slot);
        return assignment?.State switch
        {
            null => EspHomeTaskState.Empty,
            TaskAlertState.Running => EspHomeTaskState.Running,
            TaskAlertState.Approval => EspHomeTaskState.Attention,
            TaskAlertState.Completed => EspHomeTaskState.Complete,
            TaskAlertState.Fault => EspHomeTaskState.Attention,
            _ => throw new ArgumentOutOfRangeException(nameof(snapshot), assignment.State, null),
        };
    }

    private async Task PublishLoopAsync(CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        var failureReported = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _publishSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            while (_publishSignal.Wait(0))
            {
            }

            try
            {
                await PublishLatestSnapshotAsync(cancellationToken).ConfigureAwait(false);
                consecutiveFailures = 0;
                if (failureReported)
                {
                    _log("ESPHome panel state updates resumed.");
                    failureReported = false;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                consecutiveFailures++;
                if (!failureReported)
                {
                    _log($"ESPHome panel state update failed: {exception.Message}");
                    failureReported = true;
                }

                var multiplier = 1 << Math.Min(consecutiveFailures - 1, 4);
                var retryDelay = TimeSpan.FromMilliseconds(
                    Math.Min(
                        TimeSpan.FromSeconds(30).TotalMilliseconds,
                        _stateRetryDelay.TotalMilliseconds * multiplier));
                try
                {
                    await Task.Delay(retryDelay, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                // A multi-select REST update is serialized but cannot be atomic. Retry against the
                // last confirmed snapshot so a partial write converges without new task data.
                SignalPublisher();
            }
        }
    }

    private async Task PublishLatestSnapshotAsync(CancellationToken cancellationToken)
    {
        TaskAlertSnapshot snapshot;
        long reconnectGeneration;
        lock (_lifecycleGate)
        {
            snapshot = _latestSnapshot;
            reconnectGeneration = _requestedReconnectGeneration;
        }

        await PublishSnapshotAsync(
                snapshot,
                force: reconnectGeneration != _completedReconnectGeneration,
                cancellationToken)
            .ConfigureAwait(false);
        _completedReconnectGeneration = reconnectGeneration;
    }

    private ValueTask QueueReconnectSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = _getSnapshot();
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _latestSnapshot = snapshot;
            _requestedReconnectGeneration++;
        }

        SignalPublisher();
        return ValueTask.CompletedTask;
    }

    private async Task PublishSnapshotAsync(
        TaskAlertSnapshot snapshot,
        bool force,
        CancellationToken cancellationToken)
    {
        var projected = Project(snapshot);
        if (!force && _lastPublishedSnapshot == projected)
        {
            return;
        }

        if (force || _lastPublishedSnapshot is not { } previous)
        {
            await _transport.SetTaskStatesAsync(
                    projected.Task1,
                    projected.Task2,
                    projected.Task3,
                    projected.Task4,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            var updates = GetChangedTaskStates(previous, projected);
            await _transport
                .SetTaskStateUpdatesAsync(updates, cancellationToken)
                .ConfigureAwait(false);
        }

        _lastPublishedSnapshot = projected;
    }

    private static IReadOnlyList<EspHomeTaskStateUpdate> GetChangedTaskStates(
        EspHomePanelSnapshot previous,
        EspHomePanelSnapshot current)
    {
        var updates = new List<EspHomeTaskStateUpdate>(4);
        AddIfChanged(1, previous.Task1, current.Task1);
        AddIfChanged(2, previous.Task2, current.Task2);
        AddIfChanged(3, previous.Task3, current.Task3);
        AddIfChanged(4, previous.Task4, current.Task4);
        return updates;

        void AddIfChanged(int slot, EspHomeTaskState oldState, EspHomeTaskState newState)
        {
            if (oldState != newState)
            {
                updates.Add(new EspHomeTaskStateUpdate(slot, newState));
            }
        }
    }

    private async ValueTask HandlePressedAsync(
        EspHomePanelButton button,
        CancellationToken cancellationToken)
    {
        try
        {
            switch (button)
            {
                case EspHomePanelButton.Task1:
                    await OpenCurrentSlotAsync(1, cancellationToken).ConfigureAwait(false);
                    break;
                case EspHomePanelButton.Task2:
                    await OpenCurrentSlotAsync(2, cancellationToken).ConfigureAwait(false);
                    break;
                case EspHomePanelButton.Task3:
                    await OpenCurrentSlotAsync(3, cancellationToken).ConfigureAwait(false);
                    break;
                case EspHomePanelButton.Task4:
                    await OpenCurrentSlotAsync(4, cancellationToken).ConfigureAwait(false);
                    break;
                case EspHomePanelButton.PlanMode:
                    await TogglePlanModeAsync(cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            _log($"ESPHome panel action failed: {exception.Message}");
        }
    }

    private async Task OpenCurrentSlotAsync(
        int slot,
        CancellationToken cancellationToken)
    {
        var snapshot = _getSnapshot();
        if (!snapshot.Enabled)
        {
            return;
        }

        var assignment = snapshot.Assignments.FirstOrDefault(candidate => candidate.Slot == slot);
        if (assignment is null)
        {
            return;
        }

        var banks = TaskAlertSlots.Banks(slot);
        var bank = banks.Contains(snapshot.Bank) ? snapshot.Bank : banks[0];
        var navigation = new TaskAlertNavigationRequest(
            slot,
            bank,
            TaskAlertSlots.Button(slot),
            assignment.SessionId);
        if (!await _navigator.NavigateAsync(navigation, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (assignment.State is TaskAlertState.Completed or TaskAlertState.Fault
            && !_acknowledgeTerminal(slot, assignment.SessionId))
        {
            _log($"ESPHome panel opened slot {slot}; its terminal assignment changed before acknowledgement.");
        }

    }

    private async Task TogglePlanModeAsync(CancellationToken cancellationToken)
    {
        var request = new ActionRequest(
            "ESPHome panel Plan Mode",
            CompanionConfig.AlwaysBank,
            (int)EspHomePanelButton.PlanMode,
            "press",
            CodexAction.TogglePlanMode,
            DateTimeOffset.UtcNow,
            DeviceId: "esphome-panel");
        await _executeAction(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task ObserveEventLoopAsync(Task eventLoopTask)
    {
        try
        {
            await eventLoopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _log($"ESPHome panel event loop stopped: {exception.Message}");
        }
    }

    private void SignalPublisher()
    {
        try
        {
            _publishSignal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

internal readonly record struct EspHomePanelSnapshot(
    EspHomeTaskState Task1,
    EspHomeTaskState Task2,
    EspHomeTaskState Task3,
    EspHomeTaskState Task4)
{
    public static EspHomePanelSnapshot Empty { get; } = new(
        EspHomeTaskState.Empty,
        EspHomeTaskState.Empty,
        EspHomeTaskState.Empty,
        EspHomeTaskState.Empty);
}
