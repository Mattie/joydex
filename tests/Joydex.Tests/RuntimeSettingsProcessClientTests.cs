using System.Collections.Concurrent;
using System.Threading.Channels;
using Joydex.App;
using Joydex.Contracts;
using Joydex.Core.Config;
using Joydex.Core.TaskAlerts;
using Joydex.Core.Voice;
using Joydex.Ipc;

namespace Joydex.Tests;

public sealed class RuntimeSettingsProcessClientTests
{
    [Fact]
    public async Task SynchronousControlReadDoesNotBlockBootstrapConnection()
    {
        var fixture = new Fixture();
        var channel = new SynchronouslyBlockingReadStream();
        var connection = FakeConnection.Create(
            fixture.Ui,
            eventCursor: 3,
            onDispose: channel.ReleaseRead);
        fixture.Connector.Enqueue(connection);
        RuntimeSettingsProcessClient? client = null;
        var disposed = false;
        var construction = Task.Run(() => new RuntimeSettingsProcessClient(
            fixture.Bootstrap,
            channel,
            fixture.Ui,
            fixture.Connector));

        try
        {
            await channel.ReadEntered.WaitAsync(TimeSpan.FromSeconds(5));
            var attempt = await fixture.Connector.NextAttemptAsync();
            client = await construction.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("bootstrap-ticket", attempt.LaunchTicket);
            Assert.False(channel.ReadReleased);
            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            disposed = true;
        }
        finally
        {
            if (!disposed)
            {
                channel.ReleaseRead();
                client ??= await construction.WaitAsync(TimeSpan.FromSeconds(5));
                await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        Assert.True(channel.IsDisposed);
        Assert.Equal(1, channel.DisposeCount);
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task InitialConnectionFailureDoesNotWaitForBlockedControlRead()
    {
        var fixture = new Fixture();
        var channel = new SynchronouslyBlockingReadStream();
        var connectionFailure = new IOException("initial connection failed");
        var connect = fixture.Connector.EnqueueConnectHandoff();
        RuntimeSettingsProcessClient? client = null;
        var construction = Task.Run(() => new RuntimeSettingsProcessClient(
            fixture.Bootstrap,
            channel,
            fixture.Ui,
            fixture.Connector));

        try
        {
            await channel.ReadEntered.WaitAsync(TimeSpan.FromSeconds(5));
            _ = await fixture.Connector.NextAttemptAsync();
            client = await construction.WaitAsync(TimeSpan.FromSeconds(5));
            connect.SetException(connectionFailure);

            var completionFailure = await Assert.ThrowsAsync<IOException>(() =>
                client.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Same(connectionFailure, completionFailure);
            Assert.False(channel.ReadReleased);

            var disposalFailure = await Assert.ThrowsAsync<IOException>(() =>
                client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Same(connectionFailure, disposalFailure);
            Assert.True(channel.IsDisposed);
            Assert.Equal(1, channel.DisposeCount);
            Assert.False(channel.ReadReleased);
        }
        finally
        {
            connect.TrySetException(connectionFailure);
            channel.ReleaseRead();
            client ??= await construction.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (IOException exception) when (ReferenceEquals(exception, connectionFailure))
            {
            }
        }

        Assert.True(channel.IsDisposed);
        Assert.Equal(1, channel.DisposeCount);
    }

    [Fact]
    public async Task BootstrapConnectsOnceAndActivateIsPostedToTheUiContext()
    {
        var fixture = new Fixture();
        var connection = FakeConnection.Create(fixture.Ui, eventCursor: 4);
        fixture.Connector.Enqueue(connection);
        var connectionEvents = new List<RuntimeSettingsProcessConnectionChangedEventArgs>();
        var activations = 0;
        var activationUsedUi = false;
        await using var client = new RuntimeSettingsProcessClient(
            fixture.Bootstrap,
            fixture.Channel,
            fixture.Ui,
            fixture.Connector);
        client.ConnectionChanged += (_, change) => connectionEvents.Add(change);
        client.ActivateRequested += (_, _) =>
        {
            activations++;
            activationUsedUi = ReferenceEquals(SynchronizationContext.Current, fixture.Ui);
        };

        var bootstrapAttempt = await fixture.Connector.NextAttemptAsync();
        await fixture.Ui.WaitForPostsAsync(1);
        Assert.Equal("bootstrap-ticket", bootstrapAttempt.LaunchTicket);
        Assert.Equal(fixture.Endpoint.PipeName, bootstrapAttempt.Endpoint.PipeName);
        Assert.Equal(fixture.Endpoint.DataRootId, bootstrapAttempt.Endpoint.DataRootId);
        Assert.Equal(fixture.Endpoint.InstanceKind, bootstrapAttempt.Endpoint.InstanceKind);
        Assert.Equal(fixture.Endpoint.SessionId, bootstrapAttempt.Endpoint.SessionId);
        Assert.Null(bootstrapAttempt.ResumeCursor);
        Assert.Null(client.Current); // Fake handles intentionally omit the concrete transport object.

        fixture.Ui.Drain();
        var connected = Assert.Single(connectionEvents);
        Assert.True(connected.IsConnected);
        Assert.Equal(1, connected.Generation);
        Assert.Same(connection.State, connected.State);
        Assert.Same(connection.Rpc, connected.Rpc);
        Assert.Null(connected.Failure);

        await fixture.Channel.SendAsync(RuntimeSettingsChannel.CreateActivate());
        await fixture.Ui.WaitForPostsAsync(1);
        Assert.Equal(0, activations);
        fixture.Ui.Drain();
        Assert.Equal(1, activations);
        Assert.True(activationUsedUi);

        fixture.Channel.Complete();
        await client.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task FreshReconnectsDisposeInOrderPreserveCursorAndFenceOldCompletion()
    {
        var fixture = new Fixture();
        var epoch = Guid.NewGuid();
        var first = FakeConnection.Create(fixture.Ui, epoch, eventCursor: 7);
        var second = FakeConnection.Create(fixture.Ui, epoch, eventCursor: 9);
        var third = FakeConnection.Create(fixture.Ui, epoch, eventCursor: 11);
        fixture.Connector.Enqueue(first);
        fixture.Connector.Enqueue(second);
        fixture.Connector.Enqueue(third);
        var connectionEvents = new List<RuntimeSettingsProcessConnectionChangedEventArgs>();
        await using var client = new RuntimeSettingsProcessClient(
            fixture.Bootstrap,
            fixture.Channel,
            fixture.Ui,
            fixture.Connector);
        client.ConnectionChanged += (_, change) => connectionEvents.Add(change);
        _ = await fixture.Connector.NextAttemptAsync();
        await first.CompletionObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Ui.WaitForTotalPostsAsync(1);
        Assert.Equal(1, fixture.Ui.TotalPosts);
        fixture.Ui.Drain();
        var firstConnected = Assert.Single(connectionEvents);
        AssertConnected(firstConnected, generation: 1, first);

        await fixture.Channel.SendAsync(RuntimeSettingsChannel.CreateReconnect("ticket-two"));
        var secondAttempt = await fixture.Connector.NextAttemptAsync();
        await second.CompletionObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Ui.WaitForTotalPostsAsync(2);
        Assert.Equal(2, fixture.Ui.TotalPosts);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal("ticket-two", secondAttempt.LaunchTicket);
        Assert.Equal(new RuntimeClientResumeCursor(epoch, 7), secondAttempt.ResumeCursor);
        fixture.Ui.Drain();
        Assert.Collection(
            connectionEvents,
            change => AssertConnected(change, generation: 1, first),
            change => AssertConnected(change, generation: 3, second));

        first.FailCompletion(new IOException("late old completion"));
        Assert.Equal(2, fixture.Ui.TotalPosts);
        Assert.Equal(2, connectionEvents.Count);

        await fixture.Channel.SendAsync(RuntimeSettingsChannel.CreateReconnect("ticket-three"));
        var thirdAttempt = await fixture.Connector.NextAttemptAsync();
        await third.CompletionObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Ui.WaitForTotalPostsAsync(3);
        Assert.Equal(3, fixture.Ui.TotalPosts);
        Assert.Equal(1, second.DisposeCount);
        Assert.Equal("ticket-three", thirdAttempt.LaunchTicket);
        Assert.Equal(new RuntimeClientResumeCursor(epoch, 9), thirdAttempt.ResumeCursor);
        fixture.Ui.Drain();
        Assert.Collection(
            connectionEvents,
            change => AssertConnected(change, generation: 1, first),
            change => AssertConnected(change, generation: 3, second),
            change => AssertConnected(change, generation: 5, third));

        fixture.Channel.Complete();
        await client.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, fixture.Ui.TotalPosts);
        fixture.Ui.Drain();
        Assert.Equal(1, third.DisposeCount);
        Assert.Equal(3, fixture.Connector.AttemptCount);
        Assert.Collection(
            connectionEvents,
            change => AssertConnected(change, generation: 1, first),
            change => AssertConnected(change, generation: 3, second),
            change => AssertConnected(change, generation: 5, third),
            change =>
            {
                Assert.False(change.IsConnected);
                Assert.Equal(6, change.Generation);
                Assert.Same(third.State, change.State);
                Assert.Null(change.Connection);
                Assert.Null(change.Rpc);
                Assert.Null(change.Failure);
            });
    }

    [Fact]
    public async Task LiveDisconnectUsesSavedCursorAndInvalidReplacementClearsIt()
    {
        var fixture = new Fixture();
        var epoch = Guid.NewGuid();
        var first = FakeConnection.Create(fixture.Ui, epoch, eventCursor: 17);
        var second = FakeConnection.Create(fixture.Ui, epoch, eventCursor: 19);
        await second.State.RuntimeEventAsync(
            new RuntimeEvent(epoch, 21, RuntimeEventKind.SettingsChanged),
            CancellationToken.None);
        Assert.True(second.State.Current.ResynchronizationRequired);
        var third = FakeConnection.Create(fixture.Ui, epoch, eventCursor: 21);
        fixture.Connector.Enqueue(first);
        fixture.Connector.Enqueue(second);
        fixture.Connector.Enqueue(third);
        var changes = new List<RuntimeSettingsProcessConnectionChangedEventArgs>();
        await using var client = new RuntimeSettingsProcessClient(
            fixture.Bootstrap,
            fixture.Channel,
            fixture.Ui,
            fixture.Connector);
        client.ConnectionChanged += (_, change) => changes.Add(change);
        _ = await fixture.Connector.NextAttemptAsync();
        await fixture.Ui.WaitForTotalPostsAsync(1);
        fixture.Ui.Drain();

        var disconnectFailure = new IOException("runtime disconnected");
        first.FailCompletion(disconnectFailure);
        await first.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Ui.WaitForTotalPostsAsync(2);
        fixture.Ui.Drain();

        Assert.Null(client.Current);
        Assert.Equal(1, first.DisposeCount);
        var disconnected = Assert.Single(changes, change => !change.IsConnected);
        Assert.Same(first.State, disconnected.State);
        Assert.Same(disconnectFailure, disconnected.Failure);

        await fixture.Channel.SendAsync(RuntimeSettingsChannel.CreateReconnect("ticket-two"));
        var secondAttempt = await fixture.Connector.NextAttemptAsync();
        await fixture.Ui.WaitForTotalPostsAsync(3);
        Assert.Equal(new RuntimeClientResumeCursor(epoch, 17), secondAttempt.ResumeCursor);
        fixture.Ui.Drain();

        await fixture.Channel.SendAsync(RuntimeSettingsChannel.CreateReconnect("ticket-three"));
        var thirdAttempt = await fixture.Connector.NextAttemptAsync();
        await fixture.Ui.WaitForTotalPostsAsync(4);
        Assert.Null(thirdAttempt.ResumeCursor);

        fixture.Channel.Complete();
        await client.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, second.DisposeCount);
        Assert.Equal(1, third.DisposeCount);
    }

    [Fact]
    public async Task FailedReconnectLeavesControlChannelAliveForLaterFreshTicket()
    {
        var fixture = new Fixture();
        var epoch = Guid.NewGuid();
        var first = FakeConnection.Create(fixture.Ui, epoch, eventCursor: 23);
        var third = FakeConnection.Create(fixture.Ui, epoch, eventCursor: 29);
        var reconnectFailure = new IOException("runtime not ready");
        fixture.Connector.Enqueue(first);
        fixture.Connector.EnqueueFailure(reconnectFailure);
        fixture.Connector.Enqueue(third);
        var changes = new List<RuntimeSettingsProcessConnectionChangedEventArgs>();
        await using var client = new RuntimeSettingsProcessClient(
            fixture.Bootstrap,
            fixture.Channel,
            fixture.Ui,
            fixture.Connector);
        client.ConnectionChanged += (_, change) => changes.Add(change);
        _ = await fixture.Connector.NextAttemptAsync();
        await fixture.Ui.WaitForTotalPostsAsync(1);
        fixture.Ui.Drain();

        await fixture.Channel.SendAsync(RuntimeSettingsChannel.CreateReconnect("ticket-two"));
        var failedAttempt = await fixture.Connector.NextAttemptAsync();
        await fixture.Ui.WaitForTotalPostsAsync(2);
        fixture.Ui.Drain();
        Assert.Equal(new RuntimeClientResumeCursor(epoch, 23), failedAttempt.ResumeCursor);
        Assert.Equal(1, first.DisposeCount);
        Assert.False(client.Completion.IsCompleted);
        var failed = Assert.Single(changes, change => ReferenceEquals(change.Failure, reconnectFailure));
        Assert.False(failed.IsConnected);

        await fixture.Channel.SendAsync(RuntimeSettingsChannel.CreateReconnect("ticket-three"));
        var successfulAttempt = await fixture.Connector.NextAttemptAsync();
        await fixture.Ui.WaitForTotalPostsAsync(3);
        Assert.Equal("ticket-three", successfulAttempt.LaunchTicket);
        Assert.Equal(new RuntimeClientResumeCursor(epoch, 23), successfulAttempt.ResumeCursor);

        fixture.Channel.Complete();
        await client.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, third.DisposeCount);
        Assert.Equal(3, fixture.Connector.AttemptCount);
    }

    [Fact]
    public async Task RepeatedReconnectTicketIsNeverSubmittedAgain()
    {
        var fixture = new Fixture();
        fixture.Connector.Enqueue(FakeConnection.Create(fixture.Ui, eventCursor: 1));
        fixture.Connector.Enqueue(FakeConnection.Create(fixture.Ui, eventCursor: 2));
        var client = new RuntimeSettingsProcessClient(
            fixture.Bootstrap,
            fixture.Channel,
            fixture.Ui,
            fixture.Connector);
        _ = await fixture.Connector.NextAttemptAsync();

        await fixture.Channel.SendAsync(RuntimeSettingsChannel.CreateReconnect("ticket-two"));
        _ = await fixture.Connector.NextAttemptAsync();
        await fixture.Channel.SendAsync(RuntimeSettingsChannel.CreateReconnect("ticket-two"));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("repeated", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, fixture.Connector.AttemptCount);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task EofCancelsAndJoinsBlockedBootstrapConnect()
    {
        var fixture = new Fixture();
        fixture.Connector.EnqueueBlocked();
        await using var client = new RuntimeSettingsProcessClient(
            fixture.Bootstrap,
            fixture.Channel,
            fixture.Ui,
            fixture.Connector);
        var attempt = await fixture.Connector.NextAttemptAsync();

        fixture.Channel.Complete();

        await client.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await attempt.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(attempt.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task MalformedControlCancelsBlockedConnectAndFaultsCompletion()
    {
        var fixture = new Fixture();
        fixture.Connector.EnqueueBlocked();
        var client = new RuntimeSettingsProcessClient(
            fixture.Bootstrap,
            fixture.Channel,
            fixture.Ui,
            fixture.Connector);
        var attempt = await fixture.Connector.NextAttemptAsync();

        await fixture.Channel.SendRawAsync("{ malformed\n"u8.ToArray());

        await Assert.ThrowsAsync<InvalidDataException>(
            () => client.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        await attempt.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(attempt.CancellationToken.IsCancellationRequested);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task ControlQueueOverflowCancelsBlockedConnectAndFaultsCompletion()
    {
        var fixture = new Fixture();
        fixture.Connector.EnqueueBlocked();
        var client = new RuntimeSettingsProcessClient(
            fixture.Bootstrap,
            fixture.Channel,
            fixture.Ui,
            fixture.Connector);
        var attempt = await fixture.Connector.NextAttemptAsync();

        for (var index = 0; index < 33; index++)
        {
            await fixture.Channel.SendAsync(RuntimeSettingsChannel.CreateActivate());
        }

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("32-message control queue", exception.Message, StringComparison.Ordinal);
        await attempt.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(attempt.CancellationToken.IsCancellationRequested);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task ConcurrentDisposalCancelsConnectHandoffAndJoinsCleanupOnce()
    {
        var fixture = new Fixture();
        var connection = FakeConnection.Create(
            fixture.Ui,
            eventCursor: 1,
            blockDisposal: true);
        var handoff = fixture.Connector.EnqueueConnectHandoff();
        var connectionEvents = new List<RuntimeSettingsProcessConnectionChangedEventArgs>();
        var client = new RuntimeSettingsProcessClient(
            fixture.Bootstrap,
            fixture.Channel,
            fixture.Ui,
            fixture.Connector);
        client.ConnectionChanged += (_, change) => connectionEvents.Add(change);
        var attempt = await fixture.Connector.NextAttemptAsync();

        var first = client.DisposeAsync().AsTask();
        var second = client.DisposeAsync().AsTask();
        await attempt.CancellationRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        handoff.SetResult(connection);
        await connection.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        connection.ReleaseDisposal();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        await attempt.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, connection.DisposeCount);
        Assert.True(fixture.Channel.IsDisposed);
        Assert.Equal(1, fixture.Channel.DisposeCount);
        Assert.Equal(0, fixture.Ui.TotalPosts);
        Assert.Empty(connectionEvents);
    }

    private sealed class Fixture
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "joydex-settings-client-tests",
            Guid.NewGuid().ToString("N"));

        public Fixture()
        {
            var config = Path.Combine(_root, "chosen.json");
            Endpoint = RuntimeIpcEndpoint.CreateSynthetic(
                _root,
                config,
                $"Joydex.Settings.Client.{Guid.NewGuid():N}");
            Bootstrap = RuntimeSettingsChannel.CreateBootstrap(
                Endpoint,
                _root,
                config,
                "bootstrap-ticket");
        }

        public RuntimeIpcEndpoint Endpoint { get; }
        public RuntimeSettingsChannelMessage Bootstrap { get; }
        public TestControlStream Channel { get; } = new();
        public QueuedSynchronizationContext Ui { get; } = new();
        public FakeConnector Connector { get; } = new();
    }

    private sealed class FakeConnector : IRuntimeSettingsConnector
    {
        private readonly object _gate = new();
        private readonly Queue<ConnectPlan> _plans = [];
        private readonly Channel<ConnectAttempt> _attempts = Channel.CreateUnbounded<ConnectAttempt>();
        private int _attemptCount;

        public int AttemptCount => Volatile.Read(ref _attemptCount);

        public void Enqueue(IRuntimeSettingsProcessConnection connection)
        {
            var plan = NewPlan();
            plan.SetResult(connection);
            lock (_gate)
            {
                _plans.Enqueue(new ConnectPlan(plan, HonorCancellation: true));
            }
        }

        public void EnqueueBlocked()
        {
            lock (_gate)
            {
                _plans.Enqueue(new ConnectPlan(NewPlan(), HonorCancellation: true));
            }
        }

        public TaskCompletionSource<IRuntimeSettingsProcessConnection> EnqueueConnectHandoff()
        {
            var plan = NewPlan();
            lock (_gate)
            {
                _plans.Enqueue(new ConnectPlan(plan, HonorCancellation: false));
            }
            return plan;
        }

        public void EnqueueFailure(Exception failure)
        {
            var plan = NewPlan();
            plan.SetException(failure);
            lock (_gate)
            {
                _plans.Enqueue(new ConnectPlan(plan, HonorCancellation: true));
            }
        }

        public async Task<IRuntimeSettingsProcessConnection> ConnectAsync(
            RuntimeIpcEndpoint endpoint,
            string launchTicket,
            SynchronizationContext uiContext,
            RuntimeClientResumeCursor? resumeCursor,
            CancellationToken cancellationToken)
        {
            ConnectPlan plan;
            lock (_gate)
            {
                plan = _plans.Dequeue();
            }
            Interlocked.Increment(ref _attemptCount);
            var attempt = new ConnectAttempt(
                endpoint,
                launchTicket,
                resumeCursor,
                cancellationToken);
            _attempts.Writer.TryWrite(attempt);
            using var cancellationRegistration = cancellationToken.Register(
                static state => ((TaskCompletionSource)state!).TrySetResult(),
                attempt.CancellationRequested);
            try
            {
                return plan.HonorCancellation
                    ? await plan.Completion.Task.WaitAsync(cancellationToken)
                    : await plan.Completion.Task;
            }
            finally
            {
                attempt.Finished.TrySetResult();
            }
        }

        public async Task<ConnectAttempt> NextAttemptAsync() =>
            await _attempts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        private static TaskCompletionSource<IRuntimeSettingsProcessConnection> NewPlan() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private sealed record ConnectPlan(
            TaskCompletionSource<IRuntimeSettingsProcessConnection> Completion,
            bool HonorCancellation);
    }

    private sealed record ConnectAttempt(
        RuntimeIpcEndpoint Endpoint,
        string LaunchTicket,
        RuntimeClientResumeCursor? ResumeCursor,
        CancellationToken CancellationToken)
    {
        public TaskCompletionSource CancellationRequested { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Finished { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class FakeConnection : IRuntimeSettingsProcessConnection
    {
        // Inline continuations make FailCompletion a deterministic fence once CompletionObserved
        // confirms that the process client installed its completion continuation.
        private readonly TaskCompletionSource _completion = new();
        private readonly TaskCompletionSource _disposeRelease;
        private readonly Action? _onDispose;
        private Task? _disposal;
        private int _completionReads;

        private FakeConnection(
            RuntimeClientState state,
            bool blockDisposal,
            Action? onDispose)
        {
            State = state;
            _onDispose = onDispose;
            _disposeRelease = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!blockDisposal)
            {
                _disposeRelease.SetResult();
            }
        }

        public RuntimeClientConnection? Connection => null;
        public RuntimeClientState State { get; }
        public IRuntimeRpcServer Rpc { get; } = new FakeRpc();
        public Task Completion
        {
            get
            {
                if (Interlocked.Increment(ref _completionReads) >= 2)
                {
                    CompletionObserved.TrySetResult();
                }
                return _completion.Task;
            }
        }
        public int DisposeCount { get; private set; }
        public TaskCompletionSource CompletionObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposeEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public static FakeConnection Create(
            SynchronizationContext ui,
            long eventCursor,
            bool blockDisposal = false,
            Action? onDispose = null) =>
            Create(ui, Guid.NewGuid(), eventCursor, blockDisposal, onDispose);

        public static FakeConnection Create(
            SynchronizationContext ui,
            Guid engineEpoch,
            long eventCursor,
            bool blockDisposal = false,
            Action? onDispose = null)
        {
            var state = new RuntimeClientState(ImmediateSynchronizationContext.Instance);
            var settings = Settings();
            state.Initialize(new RuntimeAttachResult(
                "fake-settings",
                RuntimeProtocol.MajorVersion,
                RuntimeProtocol.MinorVersion,
                RuntimeProtocol.Capabilities,
                RuntimeProtocol.MaximumMessageBytes,
                ResynchronizationRequired: false,
                new RuntimeSnapshot(
                    engineEpoch,
                    eventCursor,
                    new RuntimeIdentitySnapshot(
                        Environment.ProcessId,
                        RuntimeInstanceKind.Synthetic,
                        "fake-root",
                        1,
                        []),
                    settings,
                    new RuntimeInputSnapshot([], []),
                    InputEventCursor: 0)));
            return new FakeConnection(state, blockDisposal, onDispose);
        }

        public void FailCompletion(Exception failure) => _completion.TrySetException(failure);

        public void ReleaseDisposal() => _disposeRelease.TrySetResult();

        public ValueTask DisposeAsync()
        {
            _disposal ??= DisposeCoreAsync();
            return new ValueTask(_disposal);
        }

        private async Task DisposeCoreAsync()
        {
            DisposeCount++;
            DisposeEntered.TrySetResult();
            _onDispose?.Invoke();
            await _disposeRelease.Task;
        }
    }

    private sealed class FakeRpc : IRuntimeRpcServer
    {
        public Task<RuntimeAttachResult> AttachAsync(
            RuntimeAttachRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RuntimeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PrepareSettingsResult> PrepareSettingsAsync(
            PrepareSettingsRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ApplySettingsResult> ApplySettingsAsync(
            ApplySettingsRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SettingsOperationResult> GetSettingsOperationAsync(
            Guid operationId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RuntimeInputSnapshot> RefreshInputSourcesAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RuntimeCaptureStartResult> BeginInputCaptureAsync(
            RuntimeCaptureRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RuntimeCaptureCommandResult> RenewInputCaptureAsync(
            Guid captureId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RuntimeCaptureCommandResult> CancelInputCaptureAsync(
            Guid captureId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RuntimeCaptureLookupResult> GetInputCaptureAsync(
            Guid captureId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RuntimeCommandResult> ExecuteCommandAsync(
            RuntimeCommandRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RuntimeCommandOperationResult> GetCommandOperationAsync(
            Guid operationId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = new();
        private readonly SemaphoreSlim _posted = new(0);
        private int _totalPosts;

        public int TotalPosts => Volatile.Read(ref _totalPosts);

        public override void Post(SendOrPostCallback d, object? state)
        {
            _callbacks.Enqueue((d, state));
            Interlocked.Increment(ref _totalPosts);
            _posted.Release();
        }

        public async Task WaitForPostsAsync(int count)
        {
            for (var index = 0; index < count; index++)
            {
                await _posted.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        public async Task WaitForTotalPostsAsync(int count)
        {
            while (Volatile.Read(ref _totalPosts) < count)
            {
                await _posted.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        public void Drain()
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                while (_callbacks.TryDequeue(out var callback))
                {
                    callback.Callback(callback.State);
                }
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }

    private sealed class ImmediateSynchronizationContext : SynchronizationContext
    {
        public static ImmediateSynchronizationContext Instance { get; } = new();

        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }

    private sealed class SynchronouslyBlockingReadStream : Stream
    {
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private readonly TaskCompletionSource _readEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;
        private int _disposeCount;

        public Task ReadEntered => _readEntered.Task;
        public bool ReadReleased => _release.IsSet;
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public override bool CanRead => !IsDisposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void ReleaseRead() => _release.Set();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            _readEntered.TrySetResult();
            _release.Wait();
            return ValueTask.FromResult(0);
        }

        public override ValueTask DisposeAsync()
        {
            Dispose(disposing: true);
            return ValueTask.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Increment(ref _disposeCount);
            }
            base.Dispose(disposing);
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class TestControlStream : Stream
    {
        private readonly Channel<byte[]> _writes = Channel.CreateUnbounded<byte[]>();
        private byte[]? _current;
        private int _offset;
        private int _disposed;
        private int _disposeCount;

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public override bool CanRead => !IsDisposed;
        public override bool CanSeek => false;
        public override bool CanWrite => !IsDisposed;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public Task SendAsync(RuntimeSettingsChannelMessage message) =>
            RuntimeSettingsChannel.WriteAsync(this, message).AsTask();

        public async Task SendRawAsync(byte[] bytes) =>
            await WriteAsync(bytes);

        public void Complete() => _writes.Writer.TryComplete();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            while (_current is null || _offset == _current.Length)
            {
                if (!await _writes.Reader.WaitToReadAsync(cancellationToken))
                {
                    return 0;
                }
                if (_writes.Reader.TryRead(out var next))
                {
                    _current = next;
                    _offset = 0;
                }
            }

            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_writes.Writer.TryWrite(buffer.ToArray()))
            {
                throw new ObjectDisposedException(nameof(TestControlStream));
            }
            return ValueTask.CompletedTask;
        }

        public override ValueTask DisposeAsync()
        {
            Dispose(disposing: true);
            return ValueTask.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Interlocked.Increment(ref _disposeCount);
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    _writes.Writer.TryComplete();
                }
            }
            base.Dispose(disposing);
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private static SettingsSnapshot Settings()
    {
        var bundle = new SettingsBundle(
            CompanionConfig.CreateSafeDefault(),
            new VoicePePreferences(),
            PebbleIndexPreferences.Default,
            TaskAlertPreferences.Default);
        return new SettingsSnapshot(1, bundle, bundle, [], []);
    }

    private static void AssertConnected(
        RuntimeSettingsProcessConnectionChangedEventArgs change,
        long generation,
        FakeConnection connection)
    {
        Assert.True(change.IsConnected);
        Assert.Equal(generation, change.Generation);
        Assert.Null(change.Connection);
        Assert.Same(connection.State, change.State);
        Assert.Same(connection.Rpc, change.Rpc);
        Assert.Null(change.Failure);
    }
}
