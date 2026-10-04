using Joydex.App;
using Joydex.Contracts;
using Joydex.Core.Config;
using Joydex.Core.TaskAlerts;
using Joydex.Core.Voice;
using Joydex.Windows.TaskAlerts;

namespace Joydex.Tests;

public sealed class RuntimeTaskAlertsConnectionServicesTests
{
    [Fact]
    public void ProjectsRuntimeStateAndUsesCompleteDesiredSuppressions()
    {
        var firstRule = new TaskAlertSuppressionRule(
            TaskAlertSuppressionScope.Task,
            "task-one");
        var secondRule = new TaskAlertSuppressionRule(
            TaskAlertSuppressionScope.Workspace,
            @"D:\Dev\two");
        var preferences = Preferences(
            enabled: true,
            bank: 4,
            suppressions: [firstRule, secondRule],
            TaskAlertLedOutputMode.DirectHid);
        var now = DateTimeOffset.UnixEpoch.AddHours(1);
        var assignment = new TaskAlertAssignment(
            2,
            "task-one",
            "turn-one",
            TaskAlertState.Approval,
            now,
            Workspace: @"D:\Dev\one");
        var alerts = Alerts(
            enabled: true,
            bank: 3,
            assignments: [assignment],
            suppressions: [firstRule],
            ledOutput: preferences.LedOutput!,
            events:
            [
                new RuntimeTaskAlertEventTrace(
                    now,
                    CodexLifecycleEvent.ToolCompleted,
                    assignment.SessionId,
                    assignment.TurnId,
                    assignment.Slot,
                    assignment.State,
                    RuntimeTaskAlertEventResult.Updated,
                    assignment.Workspace),
            ]);
        using var services = new RuntimeTaskAlertsConnectionServices(
            new ImmediateSynchronizationContext());

        Assert.True(services.BeginConnection(
            7,
            new FakeSettingsWriter(),
            new FakeCommandRunner(),
            State(Snapshot(Settings(preferences), alerts))));

        var current = services.Current;
        Assert.True(current.IsConnected);
        Assert.False(current.IsStale);
        Assert.Equal(7, current.Generation);
        Assert.Equal(3, current.Snapshot.Bank);
        Assert.Equal(TaskAlertLedOutputMode.DirectHid, current.Snapshot.EffectiveLedOutput.Mode);
        Assert.Equal(assignment, Assert.Single(current.Snapshot.Assignments));
        Assert.Equal(TaskAlertEventResult.Updated, Assert.Single(current.Snapshot.RecentEvents!).Result);
        Assert.Equal([firstRule, secondRule], current.Snapshot.Suppressions);
    }

    [Fact]
    public void NewConnectionWithoutSnapshotDoesNotPresentThePreviousProjection()
    {
        var assignment = new TaskAlertAssignment(
            2,
            "old-task",
            "old-turn",
            TaskAlertState.Running,
            DateTimeOffset.UnixEpoch);
        using var services = new RuntimeTaskAlertsConnectionServices(
            new ImmediateSynchronizationContext());
        Assert.True(services.BeginConnection(
            1,
            new FakeSettingsWriter(),
            new FakeCommandRunner(),
            State(Snapshot(
                Settings(Preferences(bank: 5)),
                Alerts(bank: 5, assignments: [assignment])))));

        Assert.True(services.BeginConnection(
            2,
            new FakeSettingsWriter(),
            new FakeCommandRunner(),
            new RuntimeClientState(new ImmediateSynchronizationContext())));

        var current = services.Current;
        Assert.Equal(2, current.Generation);
        Assert.True(current.IsConnected);
        Assert.True(current.IsStale);
        Assert.Null(current.Settings);
        Assert.Empty(current.Snapshot.Assignments);
        Assert.NotEqual(5, current.Snapshot.Bank);
    }

    [Fact]
    public async Task SettingsChangesPatchOnlyCurrentDesiredTaskAlerts()
    {
        var existingTask = new TaskAlertSuppressionRule(
            TaskAlertSuppressionScope.Task,
            "existing-task");
        var existingWorkspace = new TaskAlertSuppressionRule(
            TaskAlertSuppressionScope.Workspace,
            @"D:\Dev\existing");
        var added = new TaskAlertSuppressionRule(
            TaskAlertSuppressionScope.Task,
            "added-task");
        var initialPreferences = Preferences(
            enabled: true,
            bank: 5,
            suppressions: [existingTask, existingWorkspace],
            TaskAlertLedOutputMode.DirectHid);
        var initialSettings = Settings(initialPreferences, revision: 12);
        var writer = new FakeSettingsWriter();
        writer.ApplyHandler = (baseRevision, patch, operationId, _) =>
        {
            var updated = initialSettings with
            {
                Revision = baseRevision + 1,
                Desired = initialSettings.Desired with { TaskAlerts = patch.TaskAlerts! },
            };
            return Task.FromResult(Result(
                RuntimeSettingsWriteOutcome.Applied,
                operationId,
                updated,
                "Applied."));
        };
        using var services = new RuntimeTaskAlertsConnectionServices(
            new ImmediateSynchronizationContext());
        Assert.True(services.BeginConnection(
            1,
            writer,
            new FakeCommandRunner(),
            State(Snapshot(initialSettings, Alerts()))));
        var operationId = Guid.NewGuid();

        var result = await services.AddSuppressionAsync(
            added.Scope,
            added.Value,
            operationId);

        Assert.True(result.Succeeded);
        var call = Assert.Single(writer.ApplyCalls);
        Assert.Equal(12, call.BaseRevision);
        Assert.Equal(operationId, call.OperationId);
        Assert.Null(call.Patch.Companion);
        Assert.Null(call.Patch.Voice);
        Assert.Null(call.Patch.PebbleIndex);
        var patched = Assert.IsType<TaskAlertPreferences>(call.Patch.TaskAlerts);
        Assert.True(patched.Enabled);
        Assert.Equal(5, patched.Bank);
        Assert.Equal(TaskAlertLedOutputMode.DirectHid, patched.LedOutput!.Mode);
        Assert.Equal(
            [existingTask, existingWorkspace, added],
            Assert.IsType<TaskAlertSuppressionRule[]>(patched.Suppressions));
        Assert.Same(initialSettings.Desired.Companion, services.Current.Settings!.Desired.Companion);
        Assert.Same(initialSettings.Desired.Voice, services.Current.Settings.Desired.Voice);
        Assert.Same(initialSettings.Desired.PebbleIndex, services.Current.Settings.Desired.PebbleIndex);
    }

    [Fact]
    public async Task ConflictRefreshesCheckedSettingsWithoutInventingEffectiveState()
    {
        var activeLed = TaskAlertLedOptions.CreateDefault();
        var requestedLed = activeLed with { Mode = TaskAlertLedOutputMode.DirectHid };
        var initialPreferences = Preferences(ledOutputMode: TaskAlertLedOutputMode.LinkTool);
        var initialSettings = Settings(initialPreferences, revision: 3);
        var conflictPreferences = initialPreferences with
        {
            Enabled = false,
            LedOutput = requestedLed,
        };
        var conflictSettings = Settings(conflictPreferences, revision: 4);
        var writer = new FakeSettingsWriter
        {
            ApplyHandler = (_, _, operationId, _) => Task.FromResult(Result(
                RuntimeSettingsWriteOutcome.Conflict,
                operationId,
                conflictSettings,
                "Another editor changed Task Alerts.")),
        };
        using var services = new RuntimeTaskAlertsConnectionServices(
            new ImmediateSynchronizationContext());
        Assert.True(services.BeginConnection(
            1,
            writer,
            new FakeCommandRunner(),
            State(Snapshot(
                initialSettings,
                Alerts(enabled: true, ledOutput: activeLed)))));

        var result = await services.SetLedOutputAsync(requestedLed, Guid.NewGuid());

        Assert.Equal(RuntimeTaskAlertsActionOutcome.Conflict, result.Outcome);
        Assert.True(services.Current.Snapshot.Enabled);
        Assert.Equal(
            TaskAlertLedOutputMode.LinkTool,
            services.Current.Snapshot.EffectiveLedOutput.Mode);
        Assert.False(services.Current.Settings!.Desired.TaskAlerts.Enabled);
        Assert.Equal(
            TaskAlertLedOutputMode.DirectHid,
            services.Current.Settings.Desired.TaskAlerts.LedOutput!.Mode);
    }

    [Fact]
    public async Task StaleCompletionCannotOverwriteANewerConnection()
    {
        var completion = new TaskCompletionSource<RuntimeSettingsWriteResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var oldWriter = new FakeSettingsWriter
        {
            ApplyHandler = (_, _, _, _) => completion.Task,
        };
        var initialSettings = Settings(Preferences(), revision: 1);
        var replacementSettings = Settings(
            Preferences(enabled: false, bank: 4),
            revision: 9);
        var recoveredSettings = replacementSettings with { Revision = 10 };
        var replacementWriter = new FakeSettingsWriter
        {
            RecoverHandler = (id, _) => Task.FromResult(Result(
                RuntimeSettingsWriteOutcome.Applied,
                id,
                recoveredSettings,
                "Recovered on the replacement connection.")),
        };
        using var services = new RuntimeTaskAlertsConnectionServices(
            new ImmediateSynchronizationContext());
        Assert.True(services.BeginConnection(
            1,
            oldWriter,
            new FakeCommandRunner(),
            State(Snapshot(initialSettings, Alerts()))));
        var operationId = Guid.NewGuid();
        var pending = services.SetEnabledAsync(false, operationId);

        Assert.True(services.BeginConnection(
            2,
            replacementWriter,
            new FakeCommandRunner(),
            State(Snapshot(replacementSettings, Alerts(enabled: false, bank: 4)))));
        var recovery = services.RecoverPendingOperationsAsync();
        Assert.False(recovery.IsCompleted);
        completion.SetResult(Result(
            RuntimeSettingsWriteOutcome.Applied,
            operationId,
            Settings(Preferences(enabled: false), revision: 2),
            "Late result."));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var recovered = Assert.Single(await recovery);
        Assert.Equal(RuntimeTaskAlertsActionOutcome.Applied, recovered.Outcome);
        Assert.Equal([operationId], replacementWriter.RecoverIds);
        Assert.Equal(2, services.Current.Generation);
        Assert.Equal(10, services.Current.Settings!.Revision);
        Assert.Equal(4, services.Current.Snapshot.Bank);
        Assert.False(services.ApplyStateChange(
            1,
            Change(Snapshot(initialSettings, Alerts(bank: 1)))));
        Assert.False(services.EndConnection(1, new IOException("late")));
    }

    [Fact]
    public async Task SettingsRecoveryUsesExactIdAndRetriesLookupAfterUncertain()
    {
        var operationId = Guid.NewGuid();
        var initialSettings = Settings(Preferences(), revision: 2);
        var firstWriter = new FakeSettingsWriter
        {
            ApplyHandler = (_, _, id, _) => Task.FromResult(Result(
                RuntimeSettingsWriteOutcome.Uncertain,
                id,
                initialSettings,
                "Reply and lookup were lost.")),
        };
        var recoveredSettings = Settings(Preferences(enabled: false), revision: 3);
        var recoveryResults = new Queue<RuntimeSettingsWriteResult>(
        [
            Result(
                RuntimeSettingsWriteOutcome.Uncertain,
                operationId,
                initialSettings,
                "Still unavailable."),
            Result(
                RuntimeSettingsWriteOutcome.Applied,
                operationId,
                recoveredSettings,
                "Recovered."),
        ]);
        var replacementWriter = new FakeSettingsWriter
        {
            RecoverHandler = (_, _) => Task.FromResult(recoveryResults.Dequeue()),
        };
        using var services = new RuntimeTaskAlertsConnectionServices(
            new ImmediateSynchronizationContext());
        Assert.True(services.BeginConnection(
            1,
            firstWriter,
            new FakeCommandRunner(),
            State(Snapshot(initialSettings, Alerts()))));

        var uncertain = await services.SetEnabledAsync(false, operationId);
        Assert.Equal(RuntimeTaskAlertsActionOutcome.Uncertain, uncertain.Outcome);
        Assert.True(services.BeginConnection(
            2,
            replacementWriter,
            new FakeCommandRunner(),
            State(Snapshot(initialSettings, Alerts()))));

        var firstRecovery = Assert.Single(await services.RecoverPendingOperationsAsync());
        var secondRecovery = Assert.Single(await services.RecoverPendingOperationsAsync());

        Assert.Equal(RuntimeTaskAlertsActionOutcome.Uncertain, firstRecovery.Outcome);
        Assert.Equal(RuntimeTaskAlertsActionOutcome.Applied, secondRecovery.Outcome);
        Assert.Equal([operationId, operationId], replacementWriter.RecoverIds);
        Assert.Empty(replacementWriter.ApplyCalls);
        Assert.Equal(3, services.Current.Settings!.Revision);
        Assert.Empty(await services.RecoverPendingOperationsAsync());
    }

    [Fact]
    public async Task RecoveryPolicyRechecksSameConnectionPendingOperationUntilTerminal()
    {
        var operationId = Guid.NewGuid();
        var initialSettings = Settings(Preferences(), revision: 2);
        var recoveredSettings = Settings(Preferences(enabled: false), revision: 3);
        var recoveryResults = new Queue<RuntimeSettingsWriteResult>(
        [
            Result(
                RuntimeSettingsWriteOutcome.Uncertain,
                operationId,
                initialSettings,
                "Still unavailable."),
            Result(
                RuntimeSettingsWriteOutcome.Running,
                operationId,
                initialSettings,
                "Still running."),
            Result(
                RuntimeSettingsWriteOutcome.Applied,
                operationId,
                recoveredSettings,
                "Recovered."),
        ]);
        var writer = new FakeSettingsWriter
        {
            ApplyHandler = (_, _, id, _) => Task.FromResult(Result(
                RuntimeSettingsWriteOutcome.Uncertain,
                id,
                initialSettings,
                "Reply was lost.")),
            RecoverHandler = (_, _) => Task.FromResult(recoveryResults.Dequeue()),
        };
        using var services = new RuntimeTaskAlertsConnectionServices(
            new ImmediateSynchronizationContext());
        Assert.True(services.BeginConnection(
            1,
            writer,
            new FakeCommandRunner(),
            State(Snapshot(initialSettings, Alerts()))));
        var uncertain = await services.SetEnabledAsync(false, operationId);
        Assert.Equal(RuntimeTaskAlertsActionOutcome.Uncertain, uncertain.Outcome);

        var results = await RuntimeTaskAlertsRecoveryPolicy
            .RecoverUntilSettledAsync(services);

        Assert.True(Assert.Single(results).Succeeded);
        Assert.Equal([operationId, operationId, operationId], writer.RecoverIds);
        Assert.Equal(3, services.Current.Settings!.Revision);
        Assert.Empty(await services.RecoverPendingOperationsAsync());
    }

    [Theory]
    [InlineData(RuntimeCommandKind.InspectTaskAlertHooks)]
    [InlineData(RuntimeCommandKind.InstallTaskAlertHooks)]
    [InlineData(RuntimeCommandKind.RemoveTaskAlertHooks)]
    public async Task HookActionsSendTheirTypedRuntimeCommand(RuntimeCommandKind commandKind)
    {
        var runner = new FakeCommandRunner
        {
            ExecuteHandler = (request, _) => Task.FromResult(new RuntimeCommandResult(
                request.OperationId,
                request.Kind,
                RuntimeCommandStatus.Completed,
                Payload: new RuntimeCommandPayload(
                    TaskAlertHooks: new RuntimeTaskAlertHookStatus(
                        RuntimeTaskAlertHookState.Installed)))),
        };
        using var services = new RuntimeTaskAlertsConnectionServices(
            new ImmediateSynchronizationContext());
        Assert.True(services.BeginConnection(
            1,
            new FakeSettingsWriter(),
            runner,
            State(Snapshot(Settings(Preferences()), Alerts()))));
        var operationId = Guid.NewGuid();

        var result = commandKind switch
        {
            RuntimeCommandKind.InspectTaskAlertHooks =>
                await services.InspectHooksAsync(operationId),
            RuntimeCommandKind.InstallTaskAlertHooks =>
                await services.InstallHooksAsync(operationId),
            RuntimeCommandKind.RemoveTaskAlertHooks =>
                await services.RemoveHooksAsync(operationId),
            _ => throw new InvalidOperationException("Unexpected hook command kind."),
        };

        Assert.True(result.Succeeded);
        var request = Assert.Single(runner.ExecuteRequests);
        Assert.Equal(operationId, request.OperationId);
        Assert.Equal(commandKind, request.Kind);
    }

    [Fact]
    public async Task HookCommandRecoveryUsesTypedCommandAndNeverResubmits()
    {
        var operationId = Guid.NewGuid();
        var firstRunner = new FakeCommandRunner
        {
            ExecuteHandler = (_, _) => Task.FromException<RuntimeCommandResult>(
                new IOException("reply lost")),
            LookupHandler = (id, _) => Task.FromResult(new RuntimeCommandOperationResult(
                id,
                RuntimeCommandOperationState.NotFound)),
        };
        var recoveryResults = new Queue<RuntimeCommandOperationResult>(
        [
            new RuntimeCommandOperationResult(
                operationId,
                RuntimeCommandOperationState.NotFound),
            new RuntimeCommandOperationResult(
                operationId,
                RuntimeCommandOperationState.Completed,
                new RuntimeCommandResult(
                    operationId,
                    RuntimeCommandKind.InstallTaskAlertHooks,
                    RuntimeCommandStatus.Completed,
                    Payload: new RuntimeCommandPayload(
                        TaskAlertHooks: new RuntimeTaskAlertHookStatus(
                            RuntimeTaskAlertHookState.Installed,
                            "Installed.")))),
        ]);
        var replacementRunner = new FakeCommandRunner
        {
            LookupHandler = (_, _) => Task.FromResult(recoveryResults.Dequeue()),
        };
        var settings = Settings(Preferences());
        var snapshot = Snapshot(settings, Alerts());
        using var services = new RuntimeTaskAlertsConnectionServices(
            new ImmediateSynchronizationContext());
        Assert.True(services.BeginConnection(
            1,
            new FakeSettingsWriter(),
            firstRunner,
            State(snapshot)));

        var uncertain = await services.InstallHooksAsync(operationId);
        Assert.Equal(RuntimeTaskAlertsActionOutcome.Uncertain, uncertain.Outcome);
        var submitted = Assert.Single(firstRunner.ExecuteRequests);
        Assert.Equal(operationId, submitted.OperationId);
        Assert.Equal(RuntimeCommandKind.InstallTaskAlertHooks, submitted.Kind);
        Assert.True(services.BeginConnection(
            2,
            new FakeSettingsWriter(),
            replacementRunner,
            State(snapshot)));

        var firstRecovery = Assert.Single(await services.RecoverPendingOperationsAsync());
        var secondRecovery = Assert.Single(await services.RecoverPendingOperationsAsync());

        Assert.Equal(RuntimeTaskAlertsActionOutcome.Uncertain, firstRecovery.Outcome);
        Assert.True(secondRecovery.Succeeded);
        Assert.Empty(replacementRunner.ExecuteRequests);
        Assert.Equal([operationId, operationId], replacementRunner.LookupIds);
        Assert.Equal(RuntimeTaskAlertHookState.Installed, services.Current.Hooks.State);
        Assert.Empty(await services.RecoverPendingOperationsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingHookCommandReleasesForExplicitRetryOnlyAfterEngineRestart(bool restarted)
    {
        var operationId = Guid.NewGuid();
        var firstRunner = new FakeCommandRunner
        {
            ExecuteHandler = (_, _) => Task.FromException<RuntimeCommandResult>(new IOException("reply lost")),
        };
        var replacementRunner = new FakeCommandRunner
        {
            ExecuteHandler = (request, _) => Task.FromResult(new RuntimeCommandResult(
                request.OperationId, request.Kind, RuntimeCommandStatus.Completed,
                Payload: new RuntimeCommandPayload(TaskAlertHooks:
                    new RuntimeTaskAlertHookStatus(RuntimeTaskAlertHookState.Installed)))),
        };
        var snapshot = Snapshot(Settings(Preferences()), Alerts());
        using var services = new RuntimeTaskAlertsConnectionServices(new ImmediateSynchronizationContext());
        Assert.True(services.BeginConnection(1, new FakeSettingsWriter(), firstRunner, State(snapshot)));
        Assert.Equal(RuntimeTaskAlertsActionOutcome.Uncertain,
            (await services.InstallHooksAsync(operationId)).Outcome);
        Assert.True(services.BeginConnection(2, new FakeSettingsWriter(), replacementRunner,
            State(restarted ? snapshot with { EngineEpoch = Guid.NewGuid() } : snapshot)));

        var recovered = Assert.Single(await services.RecoverPendingOperationsAsync());

        Assert.Empty(replacementRunner.ExecuteRequests);
        Assert.Equal(operationId, Assert.Single(replacementRunner.LookupIds));
        if (restarted)
        {
            Assert.Equal(RuntimeTaskAlertsActionOutcome.Failed, recovered.Outcome);
            Assert.Empty(await services.RecoverPendingOperationsAsync());
            var retryId = Guid.NewGuid();
            Assert.True((await services.InspectHooksAsync(retryId)).Succeeded);
            Assert.Equal(retryId, Assert.Single(replacementRunner.ExecuteRequests).OperationId);
        }
        else
        {
            Assert.Equal(RuntimeTaskAlertsActionOutcome.Uncertain, recovered.Outcome);
            await Assert.ThrowsAsync<InvalidOperationException>(() => services.InspectHooksAsync(Guid.NewGuid()));
            Assert.Empty(replacementRunner.ExecuteRequests);
        }
    }

    [Fact]
    public void StateNotificationsAreMarshaledAndDisposalDropsQueuedCallbacks()
    {
        var notifications = new QueuedSynchronizationContext();
        using var services = new RuntimeTaskAlertsConnectionServices(notifications);
        var delivered = new List<RuntimeTaskAlertsPresentationState>();
        services.Changed += (_, state) => delivered.Add(state);
        var initial = Snapshot(Settings(Preferences()), Alerts(bank: 2));
        Assert.True(services.BeginConnection(
            1,
            new FakeSettingsWriter(),
            new FakeCommandRunner(),
            State(initial)));
        Assert.True(services.ApplyStateChange(
            1,
            Change(Snapshot(Settings(Preferences()), Alerts(bank: 5)))));

        Assert.Empty(delivered);
        notifications.Drain();
        Assert.Equal([2, 5], delivered.Select(state => state.Snapshot.Bank));

        Assert.True(services.EndConnection(1, new IOException("closed")));
        services.Dispose();
        notifications.Drain();
        Assert.Equal(2, delivered.Count);
        Assert.False(services.BeginConnection(
            2,
            new FakeSettingsWriter(),
            new FakeCommandRunner(),
            State(initial)));
    }

    private static RuntimeClientState State(RuntimeSnapshot snapshot)
    {
        var state = new RuntimeClientState(new ImmediateSynchronizationContext());
        state.Initialize(new RuntimeAttachResult(
            "connection",
            RuntimeProtocol.MajorVersion,
            RuntimeProtocol.MinorVersion,
            RuntimeProtocol.Capabilities,
            RuntimeProtocol.MaximumMessageBytes,
            false,
            snapshot));
        return state;
    }

    private static RuntimeClientStateChange Change(RuntimeSnapshot snapshot) => new(
        RuntimeClientChangeKind.RuntimeEvent,
        new RuntimeClientConnectionState(
            snapshot,
            InputEventCursor: 0,
            IsInitialized: true,
            ResynchronizationRequired: false,
            IsDisconnected: false,
            DisconnectFailure: null));

    private static RuntimeSnapshot Snapshot(
        SettingsSnapshot settings,
        RuntimeTaskAlertSnapshot alerts) => new(
        Guid.NewGuid(),
        EventCursor: 0,
        new RuntimeIdentitySnapshot(
            123,
            RuntimeInstanceKind.Synthetic,
            "test-root",
            RuntimeGeneration: 1,
            []),
        settings,
        new RuntimeInputSnapshot([], []),
        new RuntimeUiSnapshot(TaskAlerts: alerts));

    private static SettingsSnapshot Settings(
        TaskAlertPreferences taskAlerts,
        long revision = 1)
    {
        var bundle = new SettingsBundle(
            CompanionConfig.CreateSafeDefault(),
            VoicePePreferences.Default,
            PebbleIndexPreferences.Default,
            taskAlerts);
        return new SettingsSnapshot(revision, bundle, bundle, [], []);
    }

    private static TaskAlertPreferences Preferences(
        bool enabled = true,
        int bank = 2,
        TaskAlertSuppressionRule[]? suppressions = null,
        TaskAlertLedOutputMode ledOutputMode = TaskAlertLedOutputMode.LinkTool) =>
        new(
            enabled,
            bank,
            suppressions ?? [],
            TaskAlertLedOptions.CreateDefault() with { Mode = ledOutputMode });

    private static RuntimeTaskAlertSnapshot Alerts(
        bool enabled = true,
        int bank = 2,
        TaskAlertAssignment[]? assignments = null,
        TaskAlertSuppressionRule[]? suppressions = null,
        TaskAlertLedOptions? ledOutput = null,
        RuntimeTaskAlertEventTrace[]? events = null) => new(
        enabled,
        assignments ?? [],
        DroppedEventCount: 4,
        bank,
        BankAutomaticallyDetected: true,
        events ?? [],
        suppressions ?? [],
        ledOutput ?? TaskAlertLedOptions.CreateDefault(),
        new RuntimeTaskAlertHookStatus(RuntimeTaskAlertHookState.NotInstalled));

    private static RuntimeSettingsWriteResult Result(
        RuntimeSettingsWriteOutcome outcome,
        Guid operationId,
        SettingsSnapshot snapshot,
        string detail) =>
        new(outcome, operationId, snapshot, ApplyResult: null, detail);

    private sealed class ImmediateSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => callback(state);
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _callbacks = new();

        public override void Post(SendOrPostCallback callback, object? state) =>
            _callbacks.Enqueue((callback, state));

        public void Drain()
        {
            while (_callbacks.TryDequeue(out var item))
            {
                item.Callback(item.State);
            }
        }
    }

    private sealed record ApplyCall(
        long BaseRevision,
        SettingsPatch Patch,
        Guid OperationId);

    private sealed class FakeSettingsWriter : IRuntimeSettingsWriter
    {
        public Func<long, SettingsPatch, Guid, CancellationToken, Task<RuntimeSettingsWriteResult>>
            ApplyHandler { get; set; } = (_, _, operationId, _) => Task.FromException<RuntimeSettingsWriteResult>(
                new InvalidOperationException($"Unexpected settings Apply {operationId:D}."));

        public Func<Guid, CancellationToken, Task<RuntimeSettingsWriteResult>> RecoverHandler { get; set; } =
            (operationId, _) => Task.FromException<RuntimeSettingsWriteResult>(
                new InvalidOperationException($"Unexpected settings recovery {operationId:D}."));

        public List<ApplyCall> ApplyCalls { get; } = [];

        public List<Guid> RecoverIds { get; } = [];

        public Task<RuntimeSettingsWriteResult> ApplyAsync(
            long baseRevision,
            SettingsPatch patch,
            Guid operationId,
            CancellationToken cancellationToken)
        {
            ApplyCalls.Add(new ApplyCall(baseRevision, patch, operationId));
            return ApplyHandler(baseRevision, patch, operationId, cancellationToken);
        }

        public Task<RuntimeSettingsWriteResult> RecoverAsync(
            Guid operationId,
            CancellationToken cancellationToken)
        {
            RecoverIds.Add(operationId);
            return RecoverHandler(operationId, cancellationToken);
        }
    }

    private sealed class FakeCommandRunner : IRuntimeCommandRunner
    {
        public Func<RuntimeCommandRequest, CancellationToken, Task<RuntimeCommandResult>>
            ExecuteHandler { get; set; } = (request, _) => Task.FromException<RuntimeCommandResult>(
                new InvalidOperationException($"Unexpected command {request.Kind}."));

        public Func<Guid, CancellationToken, Task<RuntimeCommandOperationResult>> LookupHandler { get; set; } =
            (operationId, _) => Task.FromResult(new RuntimeCommandOperationResult(
                operationId,
                RuntimeCommandOperationState.NotFound));

        public List<RuntimeCommandRequest> ExecuteRequests { get; } = [];

        public List<Guid> LookupIds { get; } = [];

        public Task<RuntimeCommandResult> ExecuteAsync(
            RuntimeCommandRequest request,
            CancellationToken cancellationToken)
        {
            ExecuteRequests.Add(request);
            return ExecuteHandler(request, cancellationToken);
        }

        public Task<RuntimeCommandOperationResult> GetOperationAsync(
            Guid operationId,
            CancellationToken cancellationToken)
        {
            LookupIds.Add(operationId);
            return LookupHandler(operationId, cancellationToken);
        }
    }
}
