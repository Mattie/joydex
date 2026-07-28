using Joydex.Core.Mapping;
using Joydex.Core.TaskAlerts;
using Joydex.Windows.Actions;
using Joydex.Windows.TaskAlerts;
using Joydex.Windows.WirelessPanel;

namespace Joydex.Tests;

public sealed class EspHomePanelAdapterTests
{
    [Fact]
    public void ProjectsEveryHostStateAndClearsDisabledSnapshots()
    {
        var projected = EspHomePanelAdapter.Project(Snapshot(
            enabled: true,
            Assignment(1, TaskAlertState.Running, "running"),
            Assignment(2, TaskAlertState.Approval, "approval"),
            Assignment(3, TaskAlertState.Completed, "complete"),
            Assignment(4, TaskAlertState.Fault, "fault")));

        Assert.Equal(EspHomeTaskState.Running, projected.Task1);
        Assert.Equal(EspHomeTaskState.Attention, projected.Task2);
        Assert.Equal(EspHomeTaskState.Complete, projected.Task3);
        Assert.Equal(EspHomeTaskState.Attention, projected.Task4);
        Assert.Equal(
            EspHomePanelSnapshot.Empty,
            EspHomePanelAdapter.Project(Snapshot(
                enabled: false,
                Assignment(1, TaskAlertState.Running, "hidden"))));
    }

    [Fact]
    public async Task CardPressReadsCurrentAssignmentAndAcknowledgesSuccessfulTerminalNavigation()
    {
        var initial = Snapshot(
            enabled: true,
            Assignment(1, TaskAlertState.Running, "old-session"));
        var current = Snapshot(
            enabled: true,
            Assignment(1, TaskAlertState.Completed, "current-session"));
        var navigator = new RecordingNavigator(result: true);
        var acknowledgements = new List<(int Slot, string SessionId)>();
        await using var transport = new RecordingTransport();
        await using var adapter = CreateAdapter(
            transport,
            initial,
            () => current,
            navigator,
            (slot, sessionId) =>
            {
                acknowledgements.Add((slot, sessionId));
                return true;
            });
        adapter.Start();

        await transport.PressAsync(EspHomePanelButton.Task1);

        var request = Assert.Single(navigator.Requests);
        Assert.Equal(1, request.Slot);
        Assert.Equal(2, request.Bank);
        Assert.Equal(1, request.Button);
        Assert.Equal("current-session", request.SessionId);
        Assert.Equal([(1, "current-session")], acknowledgements);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task DisabledOrEmptySlotDoesNotNavigate(bool enabled, bool includeAssignment)
    {
        var assignments = includeAssignment
            ? new[] { Assignment(1, TaskAlertState.Running, "session") }
            : [];
        var snapshot = Snapshot(enabled, assignments);
        var navigator = new RecordingNavigator(result: true);
        await using var transport = new RecordingTransport();
        await using var adapter = CreateAdapter(
            transport,
            snapshot,
            () => snapshot,
            navigator);
        adapter.Start();

        await transport.PressAsync(EspHomePanelButton.Task1);

        Assert.Empty(navigator.Requests);
    }

    [Fact]
    public async Task FailedNavigationDoesNotAcknowledge()
    {
        var snapshot = Snapshot(
            enabled: true,
            Assignment(2, TaskAlertState.Approval, "approval-session"));
        var navigator = new RecordingNavigator(result: false);
        var acknowledged = false;
        await using var transport = new RecordingTransport();
        await using var adapter = CreateAdapter(
            transport,
            snapshot,
            () => snapshot,
            navigator,
            (_, _) =>
            {
                acknowledged = true;
                return true;
            });
        adapter.Start();

        await transport.PressAsync(EspHomePanelButton.Task2);

        Assert.False(acknowledged);
    }

    [Theory]
    [InlineData(TaskAlertState.Running)]
    [InlineData(TaskAlertState.Approval)]
    public async Task SuccessfulNonterminalNavigationDoesNotAcknowledge(
        TaskAlertState state)
    {
        var snapshot = Snapshot(
            enabled: true,
            Assignment(1, state, "active-session"));
        var acknowledgementCount = 0;
        await using var transport = new RecordingTransport();
        await using var adapter = CreateAdapter(
            transport,
            snapshot,
            () => snapshot,
            acknowledge: (_, _) =>
            {
                acknowledgementCount++;
                return true;
            });
        adapter.Start();

        await transport.PressAsync(EspHomePanelButton.Task1);

        Assert.Equal(0, acknowledgementCount);
    }

    [Fact]
    public async Task PlanModeUsesTheExistingSemanticAction()
    {
        var snapshot = Snapshot(enabled: true);
        var requests = new List<ActionRequest>();
        var results = new Queue<ActionExecutionResult>(
        [
            ActionExecutionResult.Success("sent"),
            ActionExecutionResult.Simulated("dry run"),
            ActionExecutionResult.Blocked("blocked"),
        ]);
        await using var transport = new RecordingTransport();
        await using var adapter = CreateAdapter(
            transport,
            snapshot,
            () => snapshot,
            executeAction: (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(results.Dequeue());
            });
        adapter.Start();

        await transport.PressAsync(EspHomePanelButton.PlanMode);
        await transport.PressAsync(EspHomePanelButton.PlanMode);
        await transport.PressAsync(EspHomePanelButton.PlanMode);

        Assert.All(requests, request =>
        {
            Assert.Equal("ESPHome panel Plan Mode", request.BindingName);
            Assert.Equal(CodexAction.TogglePlanMode, request.Action);
            Assert.Equal("press", request.Trigger);
            Assert.Equal("always", request.Bank);
            Assert.Equal("esphome-panel", request.DeviceId);
        });
    }

    [Fact]
    public async Task ActionExceptionIsLogged()
    {
        const string Sentinel = "PRIVATE_EXCEPTION_SENTINEL";
        var snapshot = Snapshot(enabled: true);
        var logs = new List<string>();
        await using var transport = new RecordingTransport();
        await using var adapter = CreateAdapter(
            transport,
            snapshot,
            () => snapshot,
            executeAction: (_, _) => throw new InvalidOperationException(Sentinel),
            log: logs.Add);
        adapter.Start();

        await transport.PressAsync(EspHomePanelButton.PlanMode);

        Assert.Contains(logs, value => value.Contains(Sentinel, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReconnectReadsFreshSnapshot()
    {
        var current = Snapshot(
            enabled: true,
            Assignment(1, TaskAlertState.Running, "one"));
        await using var transport = new RecordingTransport();
        await using var adapter = CreateAdapter(
            transport,
            current,
            () => current);
        adapter.Start();
        await transport.WaitForStateCountAsync(1);
        current = Snapshot(
            enabled: true,
            Assignment(4, TaskAlertState.Fault, "four"));

        await transport.ConnectAsync();
        await transport.WaitForStateCountAsync(2);

        Assert.Equal(
            new EspHomePanelSnapshot(
                EspHomeTaskState.Empty,
                EspHomeTaskState.Empty,
                EspHomeTaskState.Empty,
                EspHomeTaskState.Attention),
            transport.States.Last());
    }

    [Fact]
    public async Task ReconnectForcesAFullRefreshForTheSameProjection()
    {
        var current = Snapshot(
            enabled: true,
            Assignment(1, TaskAlertState.Running, "one"));
        await using var transport = new RecordingTransport();
        await using var adapter = CreateAdapter(
            transport,
            current,
            () => current);
        adapter.Start();
        await transport.WaitForStateCountAsync(1);

        await transport.ConnectAsync();
        await transport.WaitForStateCountAsync(2);

        Assert.Equal(2, transport.States.Count);
        Assert.Equal(transport.States[0], transport.States[1]);
    }

    [Fact]
    public async Task ReconnectQueuedDuringAPublishConvergesToTheLatestSnapshot()
    {
        var current = Snapshot(enabled: true);
        await using var transport = new RecordingTransport();
        await using var adapter = CreateAdapter(
            transport,
            current,
            () => current);
        adapter.Start();
        await transport.WaitForStateCountAsync(1);
        var blocked = transport.BlockNextState();

        adapter.Apply(Snapshot(
            enabled: true,
            Assignment(1, TaskAlertState.Running, "one")));
        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        current = Snapshot(
            enabled: true,
            Assignment(2, TaskAlertState.Approval, "two"));

        await transport.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        blocked.Release.SetResult();
        await transport.WaitForStateCountAsync(3);

        Assert.Equal(
            new EspHomePanelSnapshot(
                EspHomeTaskState.Empty,
                EspHomeTaskState.Attention,
                EspHomeTaskState.Empty,
                EspHomeTaskState.Empty),
            transport.States.Last());
    }

    [Fact]
    public async Task ApplyCoalescesPendingUpdatesToTheLatestSnapshot()
    {
        var initial = Snapshot(enabled: true);
        await using var transport = new RecordingTransport();
        await using var adapter = CreateAdapter(
            transport,
            initial,
            () => initial);
        adapter.Start();
        await transport.WaitForStateCountAsync(1);
        var blocked = transport.BlockNextState();

        adapter.Apply(Snapshot(
            enabled: true,
            Assignment(1, TaskAlertState.Running, "one")));
        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        adapter.Apply(Snapshot(
            enabled: true,
            Assignment(2, TaskAlertState.Approval, "two")));
        adapter.Apply(Snapshot(
            enabled: true,
            Assignment(4, TaskAlertState.Completed, "four")));
        blocked.Release.SetResult();
        await transport.WaitForStateCountAsync(3);

        Assert.DoesNotContain(
            new EspHomePanelSnapshot(
                EspHomeTaskState.Empty,
                EspHomeTaskState.Attention,
                EspHomeTaskState.Empty,
                EspHomeTaskState.Empty),
            transport.States);
        Assert.Equal(
            new EspHomePanelSnapshot(
                EspHomeTaskState.Empty,
                EspHomeTaskState.Empty,
                EspHomeTaskState.Empty,
                EspHomeTaskState.Complete),
            transport.States.Last());
    }

    [Fact]
    public async Task ApplySkipsSnapshotsWithTheSamePanelProjection()
    {
        var initial = Snapshot(
            enabled: true,
            Assignment(1, TaskAlertState.Running, "first-session"));
        await using var transport = new RecordingTransport();
        await using var adapter = CreateAdapter(
            transport,
            initial,
            () => initial);
        adapter.Start();
        await transport.WaitForStateCountAsync(1);

        adapter.Apply(Snapshot(
            enabled: true,
            Assignment(1, TaskAlertState.Running, "different-session")));
        await Task.Delay(100);

        Assert.Single(transport.States);

        adapter.Apply(Snapshot(
            enabled: true,
            Assignment(1, TaskAlertState.Approval, "different-session")));
        await transport.WaitForStateCountAsync(2);
        Assert.Equal(EspHomeTaskState.Attention, transport.States.Last().Task1);
    }

    [Fact]
    public async Task ApplyPublishesOnlyChangedTaskSlots()
    {
        var initial = Snapshot(
            enabled: true,
            Assignment(1, TaskAlertState.Running, "one"),
            Assignment(2, TaskAlertState.Approval, "two"),
            Assignment(3, TaskAlertState.Completed, "three"));
        await using var transport = new RecordingTransport();
        await using var adapter = CreateAdapter(
            transport,
            initial,
            () => initial);
        adapter.Start();
        await transport.WaitForStateCountAsync(1);

        adapter.Apply(Snapshot(
            enabled: true,
            Assignment(1, TaskAlertState.Running, "one"),
            Assignment(2, TaskAlertState.Completed, "two"),
            Assignment(3, TaskAlertState.Completed, "three")));
        await transport.WaitForStateCountAsync(2);

        var update = Assert.Single(Assert.Single(transport.StateUpdateBatches));
        Assert.Equal(new EspHomeTaskStateUpdate(2, EspHomeTaskState.Complete), update);
    }

    [Fact]
    public async Task FailedStateUpdateRetriesTheLatestCompleteSnapshot()
    {
        var snapshot = Snapshot(
            enabled: true,
            Assignment(1, TaskAlertState.Running, "one"),
            Assignment(3, TaskAlertState.Approval, "three"));
        var logs = new List<string>();
        await using var transport = new RecordingTransport();
        transport.FailNextState();
        await using var adapter = CreateAdapter(
            transport,
            snapshot,
            () => snapshot,
            log: logs.Add);
        adapter.Start();

        await transport.WaitForStateCountAsync(2, TimeSpan.FromSeconds(4));

        Assert.Equal(transport.States[0], transport.States[1]);
        Assert.Contains(
            logs,
            message => message.Contains("state update failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailedDeltaRetriesTheSameChangedSlots()
    {
        var initial = Snapshot(
            enabled: true,
            Assignment(1, TaskAlertState.Running, "one"));
        var logs = new List<string>();
        await using var transport = new RecordingTransport();
        await using var adapter = CreateAdapter(
            transport,
            initial,
            () => initial,
            log: logs.Add);
        adapter.Start();
        await transport.WaitForStateCountAsync(1);
        transport.FailNextState();

        adapter.Apply(Snapshot(
            enabled: true,
            Assignment(1, TaskAlertState.Approval, "one"),
            Assignment(4, TaskAlertState.Completed, "four")));
        await transport.WaitForStateCountAsync(3, TimeSpan.FromSeconds(4));

        Assert.Equal(2, transport.StateUpdateBatches.Count);
        Assert.Equal(transport.StateUpdateBatches[0], transport.StateUpdateBatches[1]);
        Assert.Equal(
            [
                new EspHomeTaskStateUpdate(1, EspHomeTaskState.Attention),
                new EspHomeTaskStateUpdate(4, EspHomeTaskState.Complete),
            ],
            transport.StateUpdateBatches[1]);
        Assert.Contains(
            logs,
            message => message.Contains("state update failed", StringComparison.Ordinal));
    }

    private static EspHomePanelAdapter CreateAdapter(
        IEspHomePanelTransport transport,
        TaskAlertSnapshot initial,
        Func<TaskAlertSnapshot> getSnapshot,
        ITaskAlertNavigator? navigator = null,
        Func<int, string, bool>? acknowledge = null,
        Func<ActionRequest, CancellationToken, Task<ActionExecutionResult>>? executeAction = null,
        Action<string>? log = null) =>
        new(
            transport,
            initial,
            getSnapshot,
            navigator ?? new RecordingNavigator(result: true),
            acknowledge ?? ((_, _) => true),
            executeAction ?? ((_, _) => Task.FromResult(ActionExecutionResult.Success("sent"))),
            log ?? (_ => { }),
            TimeSpan.Zero);

    private static TaskAlertSnapshot Snapshot(
        bool enabled,
        params TaskAlertAssignment[] assignments) =>
        new(enabled, assignments, DroppedEventCount: 0, Bank: 2);

    private static TaskAlertAssignment Assignment(
        int slot,
        TaskAlertState state,
        string sessionId) =>
        new(slot, sessionId, TurnId: null, state, DateTimeOffset.UtcNow);

    private sealed class RecordingNavigator(bool result) : ITaskAlertNavigator
    {
        public List<TaskAlertNavigationRequest> Requests { get; } = [];

        public Task<bool> NavigateAsync(
            TaskAlertNavigationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingTransport : IEspHomePanelTransport
    {
        private readonly object _sync = new();
        private readonly TaskCompletionSource _disposed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Func<EspHomePanelButton, CancellationToken, ValueTask>? _onPressed;
        private Func<CancellationToken, ValueTask>? _onConnected;
        private CancellationToken _runCancellation;
        private StateBlock? _nextStateBlock;
        private bool _failNextState;
        private EspHomePanelSnapshot _currentState = EspHomePanelSnapshot.Empty;

        public List<EspHomePanelSnapshot> States { get; } = [];

        public List<IReadOnlyList<EspHomeTaskStateUpdate>> StateUpdateBatches { get; } = [];

        public Task RunAsync(
            Func<EspHomePanelButton, CancellationToken, ValueTask> onPressed,
            Func<CancellationToken, ValueTask>? onConnected = null,
            CancellationToken cancellationToken = default)
        {
            _onPressed = onPressed;
            _onConnected = onConnected;
            _runCancellation = cancellationToken;
            return WaitForDisposalAsync(cancellationToken);
        }

        public async Task SetTaskStatesAsync(
            EspHomeTaskState task1,
            EspHomeTaskState task2,
            EspHomeTaskState task3,
            EspHomeTaskState task4,
            CancellationToken cancellationToken = default)
        {
            await RecordStateAsync(
                new EspHomePanelSnapshot(task1, task2, task3, task4),
                updates: null,
                cancellationToken);
        }

        public async Task SetTaskStateUpdatesAsync(
            IReadOnlyList<EspHomeTaskStateUpdate> updates,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(updates);
            await RecordStateAsync(
                completeState: null,
                updates,
                cancellationToken);
        }

        private async Task RecordStateAsync(
            EspHomePanelSnapshot? completeState,
            IReadOnlyList<EspHomeTaskStateUpdate>? updates,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StateBlock? stateBlock;
            lock (_sync)
            {
                var next = completeState ?? _currentState;
                if (updates is not null)
                {
                    foreach (var update in updates)
                    {
                        next = update.Slot switch
                        {
                            1 => next with { Task1 = update.State },
                            2 => next with { Task2 = update.State },
                            3 => next with { Task3 = update.State },
                            4 => next with { Task4 = update.State },
                            _ => throw new ArgumentOutOfRangeException(nameof(updates)),
                        };
                    }

                    StateUpdateBatches.Add(updates.ToArray());
                }

                _currentState = next;
                States.Add(next);
                stateBlock = _nextStateBlock;
                _nextStateBlock = null;
                if (_failNextState)
                {
                    _failNextState = false;
                    throw new HttpRequestException("Simulated partial state update.");
                }
            }

            if (stateBlock is not null)
            {
                stateBlock.Started.SetResult();
                await stateBlock.Release.Task.WaitAsync(cancellationToken);
            }
        }

        public async ValueTask PressAsync(EspHomePanelButton button)
        {
            var callback = _onPressed
                ?? throw new InvalidOperationException("The transport has not started.");
            await callback(button, _runCancellation);
        }

        public async ValueTask ConnectAsync()
        {
            var callback = _onConnected
                ?? throw new InvalidOperationException("The transport has not started.");
            await callback(_runCancellation);
        }

        public StateBlock BlockNextState()
        {
            lock (_sync)
            {
                return _nextStateBlock = new StateBlock();
            }
        }

        public void FailNextState()
        {
            lock (_sync)
            {
                _failNextState = true;
            }
        }

        public async Task WaitForStateCountAsync(
            int count,
            TimeSpan? timeout = null)
        {
            var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(2));
            while (DateTimeOffset.UtcNow < deadline)
            {
                lock (_sync)
                {
                    if (States.Count >= count)
                    {
                        return;
                    }
                }

                await Task.Delay(10);
            }

            throw new TimeoutException($"Expected {count} panel state updates.");
        }

        public ValueTask DisposeAsync()
        {
            _disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }

        private async Task WaitForDisposalAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _disposed.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private sealed class StateBlock
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
