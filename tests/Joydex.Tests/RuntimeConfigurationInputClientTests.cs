using Joydex.App;
using Joydex.Contracts;
using Joydex.Core.Config;
using Joydex.Core.Input;
using Joydex.Core.TaskAlerts;
using Joydex.Core.Voice;

namespace Joydex.Tests;

public sealed class RuntimeConfigurationInputClientTests
{
    [Fact]
    public async Task MapsInitialAndRefreshedSourcesWithoutOpeningHardware()
    {
        var instance = Guid.NewGuid();
        var product = Guid.NewGuid();
        var initial = Source(
            "stick-a",
            "Stick A",
            instance.ToString("D"),
            product.ToString("D"),
            configuredDeviceId: "configured-a",
            generation: 7,
            connected: true);
        var refreshed = Source(
            "synthetic",
            "Synthetic",
            hardwareId: "synthetic-id",
            productId: "synthetic",
            configuredDeviceId: null,
            generation: null,
            connected: false);
        var rpc = new FakeRuntimeRpcServer
        {
            RefreshHandler = _ => Task.FromResult(new RuntimeInputSnapshot([refreshed], [])),
        };
        var state = State(Snapshot([initial]));
        await using var client = new RuntimeConfigurationInputClient();
        var connections = new List<RuntimeConfigurationConnectionChangedEventArgs>();
        client.ConnectionChanged += (_, change) => connections.Add(change);

        Assert.True(client.BeginConnection(4, rpc, state));

        Assert.True(client.IsConnected);
        var mapped = Assert.Single(client.Sources);
        Assert.Equal("stick-a", mapped.Source.SourceId);
        Assert.Equal("Stick A", mapped.Source.DisplayName);
        Assert.Equal(instance, mapped.InstanceGuid);
        Assert.Equal(product, mapped.ProductGuid);
        Assert.Equal(instance.ToString("D"), mapped.Selector.InstanceGuid);
        Assert.Equal(product.ToString("D"), mapped.Selector.ProductGuid);
        Assert.Equal("configured-a", mapped.ConfiguredDeviceId);
        var sourceState = Assert.IsType<InputSourceState>(client.GetSourceState("STICK-A"));
        Assert.Equal(7, sourceState.Generation);
        Assert.True(sourceState.Connected);
        var connected = Assert.Single(connections);
        Assert.Equal(4, connected.Generation);
        Assert.True(connected.IsConnected);

        var values = await client.RefreshSourcesAsync();

        mapped = Assert.Single(values);
        Assert.Equal(Guid.Empty, mapped.InstanceGuid);
        Assert.Equal(Guid.Empty, mapped.ProductGuid);
        Assert.Equal("synthetic-id", mapped.Selector.InstanceGuid);
        Assert.Equal("synthetic", mapped.Selector.ProductGuid);
        Assert.False(client.GetSourceState("synthetic")!.Connected);
        Assert.Equal(1, rpc.RefreshCalls);
    }

    [Fact]
    public async Task MapsCurrentGenerationObservationsAndIgnoresStaleChanges()
    {
        var epoch = Guid.NewGuid();
        var source = Source("stick-a", "Stick A", generation: 11, connected: true);
        var snapshot = Snapshot([source], epoch: epoch, inputCursor: 1);
        await using var client = new RuntimeConfigurationInputClient();
        Assert.True(client.BeginConnection(2, new FakeRuntimeRpcServer(), State(snapshot)));
        var observations = new List<InputObservation>();
        client.InputObserved += (_, change) => observations.Add(change.Observation);
        var input = new RuntimeInputObservation(
            17,
            source.SourceId,
            SourceGeneration: 12,
            new JoystickSnapshot(DateTimeOffset.UnixEpoch, [true], [2], [3]),
            [new JoystickEvent(JoystickEventKind.ButtonPressed, 0, 1)]);
        var change = Change(
            snapshot,
            new RuntimeConnectionInputEvent(
                epoch,
                2,
                RuntimeConnectionInputEventKind.InputObserved,
                Observation: input));

        Assert.False(client.ApplyStateChange(1, change));
        Assert.True(client.ApplyStateChange(2, change));

        var observed = Assert.Single(observations);
        Assert.Equal(17, observed.Sequence);
        Assert.Equal("Stick A", observed.Source.Descriptor.DisplayName);
        Assert.Equal(12, observed.Source.Generation);
        Assert.Equal(input.Snapshot, observed.Snapshot);
        Assert.Equal(input.Events, observed.Events);
    }

    [Fact]
    public async Task StaleRefreshCompletionCannotReplaceANewerConnectionProjection()
    {
        var refresh = new TaskCompletionSource<RuntimeInputSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var oldRpc = new FakeRuntimeRpcServer { RefreshHandler = _ => refresh.Task };
        var newSource = Source("new-source", "New source", generation: 2, connected: true);
        await using var client = new RuntimeConfigurationInputClient();
        Assert.True(client.BeginConnection(
            1,
            oldRpc,
            State(Snapshot([Source("old-source", "Old source")]))));
        var staleRefresh = client.RefreshSourcesAsync();
        Assert.True(client.BeginConnection(2, new FakeRuntimeRpcServer(), State(Snapshot([newSource]))));

        refresh.SetResult(new RuntimeInputSnapshot(
            [Source("stale-result", "Stale result")],
            []));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => staleRefresh);
        Assert.Equal("new-source", Assert.Single(client.Sources).Source.SourceId);
        Assert.False(client.BeginConnection(1, new FakeRuntimeRpcServer(), State(Snapshot([]))));
    }

    [Fact]
    public async Task TerminalCallbackBeforeBeginAcknowledgementIsReplayedByCaptureId()
    {
        var captureId = Guid.NewGuid();
        var epoch = Guid.NewGuid();
        var snapshot = Snapshot(
            [Source("stick-a", "Stick A", generation: 9, connected: true)],
            epoch: epoch);
        var state = State(snapshot);
        await using var client = new RuntimeConfigurationInputClient();
        InputCaptureChangedEventArgs? replayed = null;
        client.CaptureChanged += (_, change) => replayed = change;
        var rpc = new FakeRuntimeRpcServer();
        rpc.BeginHandler = (request, _) =>
        {
            var terminal = new RuntimeCaptureUpdate(
                Lease(
                    captureId,
                    request.SourceId,
                    request.Purpose,
                    sourceGeneration: 9,
                    InputCaptureStatus.Completed,
                    revision: 2),
                new JoystickEvent(JoystickEventKind.ButtonPressed, 3, 4),
                "Captured.");
            Assert.True(client.ApplyStateChange(
                1,
                Change(
                    snapshot,
                    new RuntimeConnectionInputEvent(
                        epoch,
                        1,
                        RuntimeConnectionInputEventKind.CaptureChanged,
                        Capture: terminal))));
            return Task.FromResult(new RuntimeCaptureStartResult(
                true,
                Lease(
                    captureId,
                    request.SourceId,
                    request.Purpose,
                    sourceGeneration: 9,
                    InputCaptureStatus.Pending,
                    revision: 0),
                null));
        };
        Assert.True(client.BeginConnection(1, rpc, state));

        var result = await client.BeginCaptureAsync(
            "stick-a",
            "Capture binding",
            expectedGeneration: 9);

        Assert.True(result.Accepted);
        Assert.Equal(InputCaptureStatus.Completed, result.Lease!.Status);
        Assert.Equal("runtime-settings:1", result.Lease.ConnectionId);
        Assert.Equal(9, result.Lease.SourceGeneration);
        Assert.NotNull(replayed);
        Assert.Equal(captureId, replayed.Lease.CaptureId);
        Assert.Equal(InputCaptureStatus.Completed, replayed.Lease.Status);
        Assert.Equal(4, replayed.CapturedInput!.DisplayIndex);
        Assert.Equal("Captured.", replayed.Detail);
        Assert.Equal(9, Assert.Single(rpc.BeginRequests).ExpectedGeneration);
    }

    [Fact]
    public async Task LookupReconcilesACompletionAndDeduplicatesItsLaterCallback()
    {
        var captureId = Guid.NewGuid();
        var epoch = Guid.NewGuid();
        var active = Lease(
            captureId,
            "stick-a",
            "Binding",
            sourceGeneration: 3,
            InputCaptureStatus.Active,
            revision: 1);
        var completed = new RuntimeCaptureUpdate(
            active with
            {
                Status = InputCaptureStatus.Completed,
                Revision = 2,
            },
            new JoystickEvent(JoystickEventKind.ButtonPressed, 5, 6),
            "Captured.");
        var rpc = new FakeRuntimeRpcServer
        {
            LookupHandler = (_, _) => Task.FromResult(new RuntimeCaptureLookupResult(
                RuntimeCaptureLookupStatus.Completed,
                completed)),
        };
        var snapshot = Snapshot([], [active], epoch);
        await using var client = new RuntimeConfigurationInputClient();
        Assert.True(client.BeginConnection(1, rpc, State(snapshot)));
        var callbacks = new List<InputCaptureChangedEventArgs>();
        client.CaptureChanged += (_, change) => callbacks.Add(change);

        var found = await client.GetCaptureAsync(captureId);
        Assert.NotNull(found);
        Assert.Equal(InputCaptureStatus.Completed, found.Lease.Status);
        Assert.Equal(6, found.CapturedInput!.DisplayIndex);
        Assert.True(client.ApplyStateChange(
            1,
            Change(
                snapshot,
                new RuntimeConnectionInputEvent(
                    epoch,
                    1,
                    RuntimeConnectionInputEventKind.CaptureChanged,
                    Capture: completed))));

        Assert.Empty(callbacks);
        Assert.Equal(1, rpc.LookupCalls);
    }

    [Fact]
    public async Task CaptureStartWithLostReplyIsRejectedWithoutRetry()
    {
        var rpc = new FakeRuntimeRpcServer
        {
            BeginHandler = (_, _) => Task.FromException<RuntimeCaptureStartResult>(
                new IOException("reply lost")),
        };
        await using var client = new RuntimeConfigurationInputClient();
        Assert.True(client.BeginConnection(1, rpc, State(Snapshot([]))));

        var result = await client.BeginCaptureAsync("stick-a", "Binding", expectedGeneration: 8);

        Assert.False(result.Accepted);
        Assert.Contains("was not retried", result.Error, StringComparison.Ordinal);
        Assert.Single(rpc.BeginRequests);
        Assert.Equal(0, rpc.LookupCalls);
    }

    [Fact]
    public async Task RenewAndCancelConsultCaptureStateAfterAmbiguousReplies()
    {
        var captureId = Guid.NewGuid();
        var active = Lease(
            captureId,
            "stick-a",
            "Binding",
            sourceGeneration: 4,
            InputCaptureStatus.Active,
            revision: 1);
        var renewed = active with
        {
            ExpiresAt = active.ExpiresAt.AddSeconds(10),
            Revision = 2,
        };
        var cancelled = renewed with
        {
            Status = InputCaptureStatus.Cancelled,
            Revision = 3,
        };
        var capturedInput = new JoystickEvent(JoystickEventKind.ButtonPressed, 2, 5);
        var renewedUpdate = new RuntimeCaptureUpdate(
            renewed,
            Detail: "Renewed after a lost reply.");
        var cancelledUpdate = new RuntimeCaptureUpdate(
            cancelled,
            capturedInput,
            "Cancelled after a lost reply.");
        var lookups = new Queue<RuntimeCaptureLookupResult>(
        [
            new RuntimeCaptureLookupResult(
                RuntimeCaptureLookupStatus.Active,
                renewedUpdate),
            new RuntimeCaptureLookupResult(
                RuntimeCaptureLookupStatus.Completed,
                cancelledUpdate),
        ]);
        var rpc = new FakeRuntimeRpcServer
        {
            RenewHandler = (_, _) => Task.FromException<RuntimeCaptureCommandResult>(
                new IOException("renew reply lost")),
            CancelHandler = (_, _) => Task.FromException<RuntimeCaptureCommandResult>(
                new TimeoutException("cancel reply lost")),
            LookupHandler = (_, _) => Task.FromResult(lookups.Dequeue()),
        };
        var epoch = Guid.NewGuid();
        var snapshot = Snapshot([], [active], epoch);
        await using var client = new RuntimeConfigurationInputClient();
        var captureChanges = new List<InputCaptureChangedEventArgs>();
        client.CaptureChanged += (_, change) => captureChanges.Add(change);
        Assert.True(client.BeginConnection(1, rpc, State(snapshot)));

        var renewal = await client.RenewCaptureAsync(captureId);
        var cancellation = await client.CancelCaptureAsync(captureId);

        Assert.NotNull(renewal);
        Assert.Equal(2, renewal.Revision);
        Assert.True(cancellation);
        Assert.Collection(
            captureChanges,
            change =>
            {
                Assert.Equal(InputCaptureStatus.Active, change.Lease.Status);
                Assert.Equal(2, change.Lease.Revision);
                Assert.Null(change.CapturedInput);
                Assert.Equal("Renewed after a lost reply.", change.Detail);
            },
            change =>
            {
                Assert.Equal(InputCaptureStatus.Cancelled, change.Lease.Status);
                Assert.Equal(3, change.Lease.Revision);
                Assert.Equal(capturedInput, change.CapturedInput);
                Assert.Equal("Cancelled after a lost reply.", change.Detail);
            });
        Assert.True(client.ApplyStateChange(
            1,
            Change(
                snapshot,
                new RuntimeConnectionInputEvent(
                    epoch,
                    1,
                    RuntimeConnectionInputEventKind.CaptureChanged,
                    Capture: cancelledUpdate))));
        Assert.Equal(2, captureChanges.Count);
        Assert.Equal(1, rpc.RenewCalls);
        Assert.Equal(1, rpc.CancelCalls);
        Assert.Equal(2, rpc.LookupCalls);
    }

    [Fact]
    public async Task NewConnectionEndsOnlyOldLocalCapturesAndRejectsLateOldEvents()
    {
        var oldCaptureId = Guid.NewGuid();
        var oldEpoch = Guid.NewGuid();
        var oldLease = Lease(
            oldCaptureId,
            "old-source",
            "Binding",
            sourceGeneration: 1,
            InputCaptureStatus.Active,
            revision: 1);
        var oldSnapshot = Snapshot(
            [Source("old-source", "Old source")],
            [oldLease],
            oldEpoch);
        var newSnapshot = Snapshot([Source("new-source", "New source")]);
        await using var client = new RuntimeConfigurationInputClient();
        var connectionChanges = new List<RuntimeConfigurationConnectionChangedEventArgs>();
        var captureChanges = new List<InputCaptureChangedEventArgs>();
        client.ConnectionChanged += (_, change) => connectionChanges.Add(change);
        client.CaptureChanged += (_, change) => captureChanges.Add(change);
        Assert.True(client.BeginConnection(1, new FakeRuntimeRpcServer(), State(oldSnapshot)));

        Assert.True(client.BeginConnection(2, new FakeRuntimeRpcServer(), State(newSnapshot)));
        Assert.False(client.ApplyStateChange(
            1,
            Change(
                oldSnapshot,
                new RuntimeConnectionInputEvent(
                    oldEpoch,
                    1,
                    RuntimeConnectionInputEventKind.CaptureChanged,
                    Capture: new RuntimeCaptureUpdate(oldLease with
                    {
                        Status = InputCaptureStatus.Completed,
                        Revision = 2,
                    })))));
        Assert.False(client.EndConnection(1, new IOException("late")));

        var disconnected = Assert.Single(captureChanges);
        Assert.Equal(oldCaptureId, disconnected.Lease.CaptureId);
        Assert.Equal(InputCaptureStatus.ClientDisconnected, disconnected.Lease.Status);
        Assert.Collection(
            connectionChanges,
            change =>
            {
                Assert.Equal(1, change.Generation);
                Assert.True(change.IsConnected);
            },
            change =>
            {
                Assert.Equal(1, change.Generation);
                Assert.False(change.IsConnected);
            },
            change =>
            {
                Assert.Equal(2, change.Generation);
                Assert.True(change.IsConnected);
            });
        Assert.Equal("new-source", Assert.Single(client.Sources).Source.SourceId);
    }

    [Fact]
    public async Task DisconnectCleansCaptureExactlyOnceAndMethodsAvoidRpc()
    {
        var captureId = Guid.NewGuid();
        var lease = Lease(
            captureId,
            "stick-a",
            "Binding",
            sourceGeneration: 5,
            InputCaptureStatus.Active,
            revision: 1);
        var rpc = new FakeRuntimeRpcServer();
        await using var client = new RuntimeConfigurationInputClient();
        var captureChanges = new List<InputCaptureChangedEventArgs>();
        var connectionChanges = new List<RuntimeConfigurationConnectionChangedEventArgs>();
        client.CaptureChanged += (_, change) => captureChanges.Add(change);
        client.ConnectionChanged += (_, change) => connectionChanges.Add(change);
        Assert.True(client.BeginConnection(7, rpc, State(Snapshot([], [lease]))));
        var failure = new IOException("connection ended");

        Assert.True(client.EndConnection(7, failure));
        Assert.False(client.EndConnection(7, failure));
        Assert.False(client.IsConnected);
        Assert.Equal(
            InputCaptureStatus.ClientDisconnected,
            Assert.Single(captureChanges).Lease.Status);
        var ended = Assert.Single(connectionChanges, change => !change.IsConnected);
        Assert.Same(failure, ended.Failure);

        var begin = await client.BeginCaptureAsync("stick-a", "Binding");
        Assert.False(begin.Accepted);
        Assert.False(await client.CancelCaptureAsync(captureId));
        Assert.Null(await client.GetCaptureAsync(captureId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RenewCaptureAsync(captureId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RefreshSourcesAsync());
        Assert.Equal(0, rpc.TotalInputCalls);
    }

    [Fact]
    public async Task DisposeCancelsOnlyCurrentActiveLeasesAndIsIdempotent()
    {
        var activeId = Guid.NewGuid();
        var completedId = Guid.NewGuid();
        var active = Lease(
            activeId,
            "stick-a",
            "Binding",
            1,
            InputCaptureStatus.Active,
            revision: 1);
        var completed = Lease(
            completedId,
            "stick-b",
            "Binding",
            2,
            InputCaptureStatus.Completed,
            revision: 2);
        var rpc = new FakeRuntimeRpcServer
        {
            CancelHandler = (captureId, _) => Task.FromResult(new RuntimeCaptureCommandResult(
                true,
                active with
                {
                    CaptureId = captureId,
                    Status = InputCaptureStatus.Cancelled,
                    Revision = 2,
                },
                null)),
        };
        var client = new RuntimeConfigurationInputClient();
        var captures = new List<InputCaptureChangedEventArgs>();
        var connections = new List<RuntimeConfigurationConnectionChangedEventArgs>();
        client.CaptureChanged += (_, change) => captures.Add(change);
        client.ConnectionChanged += (_, change) => connections.Add(change);
        Assert.True(client.BeginConnection(
            1,
            rpc,
            State(Snapshot([], [active, completed]))));

        await client.DisposeAsync();
        await client.DisposeAsync();

        Assert.Equal([activeId], rpc.CancelIds);
        Assert.Equal(
            InputCaptureStatus.ClientDisconnected,
            Assert.Single(captures).Lease.Status);
        Assert.Single(connections, change => !change.IsConnected);
        Assert.False(client.IsConnected);
        Assert.False(await client.CancelCaptureAsync(activeId));
        Assert.Equal(1, rpc.CancelCalls);
    }

    [Fact]
    public async Task StaleBeginCompletionCannotInstallAnOldCapture()
    {
        var captureId = Guid.NewGuid();
        var completion = new TaskCompletionSource<RuntimeCaptureStartResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var oldRpc = new FakeRuntimeRpcServer { BeginHandler = (_, _) => completion.Task };
        await using var client = new RuntimeConfigurationInputClient();
        Assert.True(client.BeginConnection(1, oldRpc, State(Snapshot([]))));
        var pending = client.BeginCaptureAsync("old-source", "Binding");
        Assert.True(client.BeginConnection(2, new FakeRuntimeRpcServer(), State(Snapshot([]))));

        completion.SetResult(new RuntimeCaptureStartResult(
            true,
            Lease(
                captureId,
                "old-source",
                "Binding",
                sourceGeneration: 1,
                InputCaptureStatus.Pending,
                revision: 0),
            null));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(await client.CancelCaptureAsync(captureId));
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

    private static RuntimeClientStateChange Change(
        RuntimeSnapshot snapshot,
        RuntimeConnectionInputEvent inputEvent) =>
        new(
            RuntimeClientChangeKind.InputEvent,
            new RuntimeClientConnectionState(
                snapshot,
                inputEvent.Sequence,
                IsInitialized: true,
                ResynchronizationRequired: false,
                IsDisconnected: false,
                DisconnectFailure: null),
            InputEvent: inputEvent);

    private static RuntimeSnapshot Snapshot(
        RuntimeInputSource[] sources,
        RuntimeCaptureLease[]? captures = null,
        Guid? epoch = null,
        long inputCursor = 0) =>
        new(
            epoch ?? Guid.NewGuid(),
            EventCursor: 0,
            new RuntimeIdentitySnapshot(
                123,
                RuntimeInstanceKind.Synthetic,
                "test-root",
                RuntimeGeneration: 1,
                []),
            Settings(),
            new RuntimeInputSnapshot(sources, captures ?? []),
            new RuntimeUiSnapshot(),
            inputCursor);

    private static SettingsSnapshot Settings()
    {
        var bundle = new SettingsBundle(
            CompanionConfig.CreateSafeDefault(),
            VoicePePreferences.Default,
            PebbleIndexPreferences.Default,
            TaskAlertPreferences.Default);
        return new SettingsSnapshot(0, bundle, bundle, [], []);
    }

    private static RuntimeInputSource Source(
        string sourceId,
        string displayName,
        string? hardwareId = null,
        string? productId = null,
        string? configuredDeviceId = null,
        long? generation = 1,
        bool connected = true) =>
        new(
            sourceId,
            displayName,
            hardwareId,
            productId,
            configuredDeviceId,
            generation,
            connected);

    private static RuntimeCaptureLease Lease(
        Guid captureId,
        string sourceId,
        string purpose,
        long? sourceGeneration,
        InputCaptureStatus status,
        long revision) =>
        new(
            captureId,
            sourceId,
            purpose,
            sourceGeneration,
            DateTimeOffset.UnixEpoch.AddMinutes(10 + revision),
            status,
            revision);

    private sealed class ImmediateSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => callback(state);
    }

    private sealed class FakeRuntimeRpcServer : IRuntimeRpcServer
    {
        public Func<CancellationToken, Task<RuntimeInputSnapshot>> RefreshHandler { get; init; } =
            _ => Task.FromResult(new RuntimeInputSnapshot([], []));

        public Func<RuntimeCaptureRequest, CancellationToken, Task<RuntimeCaptureStartResult>> BeginHandler { get; set; } =
            (_, _) => Task.FromResult(new RuntimeCaptureStartResult(
                false,
                null,
                "Capture unavailable."));

        public Func<Guid, CancellationToken, Task<RuntimeCaptureCommandResult>> RenewHandler { get; init; } =
            (_, _) => Task.FromResult(new RuntimeCaptureCommandResult(
                false,
                null,
                "Capture unavailable."));

        public Func<Guid, CancellationToken, Task<RuntimeCaptureCommandResult>> CancelHandler { get; init; } =
            (_, _) => Task.FromResult(new RuntimeCaptureCommandResult(
                false,
                null,
                "Capture unavailable."));

        public Func<Guid, CancellationToken, Task<RuntimeCaptureLookupResult>> LookupHandler { get; init; } =
            (_, _) => Task.FromResult(new RuntimeCaptureLookupResult(RuntimeCaptureLookupStatus.NotFound));

        public int RefreshCalls { get; private set; }

        public int RenewCalls { get; private set; }

        public int CancelCalls { get; private set; }

        public int LookupCalls { get; private set; }

        public int TotalInputCalls =>
            RefreshCalls + BeginRequests.Count + RenewCalls + CancelCalls + LookupCalls;

        public List<RuntimeCaptureRequest> BeginRequests { get; } = [];

        public List<Guid> CancelIds { get; } = [];

        public Task<RuntimeInputSnapshot> RefreshInputSourcesAsync(CancellationToken cancellationToken)
        {
            RefreshCalls++;
            return RefreshHandler(cancellationToken);
        }

        public Task<RuntimeCaptureStartResult> BeginInputCaptureAsync(
            RuntimeCaptureRequest request,
            CancellationToken cancellationToken)
        {
            BeginRequests.Add(request);
            return BeginHandler(request, cancellationToken);
        }

        public Task<RuntimeCaptureCommandResult> RenewInputCaptureAsync(
            Guid captureId,
            CancellationToken cancellationToken)
        {
            RenewCalls++;
            return RenewHandler(captureId, cancellationToken);
        }

        public Task<RuntimeCaptureCommandResult> CancelInputCaptureAsync(
            Guid captureId,
            CancellationToken cancellationToken)
        {
            CancelCalls++;
            CancelIds.Add(captureId);
            return CancelHandler(captureId, cancellationToken);
        }

        public Task<RuntimeCaptureLookupResult> GetInputCaptureAsync(
            Guid captureId,
            CancellationToken cancellationToken)
        {
            LookupCalls++;
            return LookupHandler(captureId, cancellationToken);
        }

        public Task<RuntimeAttachResult> AttachAsync(
            RuntimeAttachRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RuntimeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PrepareSettingsResult> PrepareSettingsAsync(
            PrepareSettingsRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ApplySettingsResult> ApplySettingsAsync(
            ApplySettingsRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SettingsOperationResult> GetSettingsOperationAsync(
            Guid operationId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RuntimeCommandResult> ExecuteCommandAsync(
            RuntimeCommandRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RuntimeCommandOperationResult> GetCommandOperationAsync(
            Guid operationId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
