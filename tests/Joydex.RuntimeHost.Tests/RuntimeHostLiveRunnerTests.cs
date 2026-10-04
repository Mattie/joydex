using System.Collections.Concurrent;
using Joydex.Contracts;
using Joydex.Ipc;

namespace Joydex.RuntimeHost.Tests;

public sealed class RuntimeHostLiveRunnerTests
{
    [Fact]
    public async Task EarlyTrayCancellationReleasesAdmissionAndLaterTrayUsesSameStartingEngine()
    {
        var components = new FakeComponents(CreateProductionPolicy());
        var runner = new RuntimeHostLiveRunner(components);
        var run = runner.RunAsync(components.Policy, CancellationToken.None);
        await components.EngineStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            ["guard", "listener", "rendezvous", "settings", "engine-start", "engine-claim-guard"],
            components.Log.Take(6));

        using var earlyConnection = new CancellationTokenSource();
        var earlyAttach = components.Listener.ConnectAsync(
            Context(components.Policy, "early"),
            RuntimeClientKind.Tray,
            earlyConnection.Token);
        await components.Rendezvous.FirstClaim.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, components.Rendezvous.ClaimCount);
        Assert.Equal(0, components.Engine.SessionCount);

        earlyConnection.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => earlyAttach.AsTask());
        Assert.Equal(1, components.Rendezvous.ReleasedCount);
        Assert.False(run.IsCompleted);

        components.CompleteEngineStartup();
        var replacement = await components.Listener.ConnectAsync(
            Context(components.Policy, "replacement"),
            RuntimeClientKind.Tray,
            CancellationToken.None);

        Assert.IsType<RuntimeHostTrayLeaseRpcServer>(replacement);
        Assert.Equal(2, components.Rendezvous.ClaimCount);
        Assert.Equal(1, components.Engine.SessionCount);
        Assert.Equal(
            ["claim:early", "release:early", "claim:replacement", "session:replacement"],
            components.Log.Where(entry =>
                entry.StartsWith("claim:", StringComparison.Ordinal)
                || entry.StartsWith("release:", StringComparison.Ordinal)
                || entry.StartsWith("session:", StringComparison.Ordinal)));

        await ((IAsyncDisposable)replacement).DisposeAsync();
        Assert.Equal(2, components.Rendezvous.ReleasedCount);
        components.Engine.RequestShutdown();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            [
                "rendezvous-dispose",
                "listener-dispose",
                "engine-dispose",
                "guard-dispose",
                "settings-dispose",
                "transfer-dispose",
            ],
            components.Log.TakeLast(6));
    }

    [Fact]
    public async Task FreshProductionInstallOpensSettingsAfterTheEngineIsReady()
    {
        var policy = CreateProductionPolicy() with { ExistingCompanionInstall = false };
        var components = new FakeComponents(policy);
        var runner = new RuntimeHostLiveRunner(components);
        var run = runner.RunAsync(policy, CancellationToken.None);
        await components.EngineStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(components.SettingsLauncher.Opened.Task.IsCompleted);
        components.CompleteEngineStartup();
        var request = await components.SettingsLauncher.Opened.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeCommandKind.OpenSettings, request.Kind);
        components.Engine.RequestShutdown();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ExistingProductionInstallLeavesSettingsClosedAtStartup()
    {
        var policy = CreateProductionPolicy() with { ExistingCompanionInstall = true };
        var components = new FakeComponents(policy);
        var runner = new RuntimeHostLiveRunner(components);
        var run = runner.RunAsync(policy, CancellationToken.None);
        await components.EngineStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        components.CompleteEngineStartup();
        components.Engine.RequestShutdown();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(components.SettingsLauncher.Opened.Task.IsCompleted);
    }

    [Fact]
    public async Task ShutdownCancelsRuntimeWorkBeforeDrainingListenerConnections()
    {
        var components = new FakeComponents(CreateProductionPolicy());
        var run = new RuntimeHostLiveRunner(components).RunAsync(components.Policy, CancellationToken.None);
        await components.EngineStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pendingActivationStopped = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = components.EngineLifetime.Register(() => pendingActivationStopped.TrySetResult());
        components.Listener.BeforeDispose = () => pendingActivationStopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        components.CompleteEngineStartup();

        components.Engine.RequestShutdown();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(pendingActivationStopped.Task.IsCompletedSuccessfully);
        Assert.Contains("engine-dispose", components.Log);
    }

    [Fact]
    public async Task UnexpectedListenerCompletionIsTerminalAndCleansUpInOrder()
    {
        var components = new FakeComponents(CreateProductionPolicy());
        var runner = new RuntimeHostLiveRunner(components);
        var run = runner.RunAsync(components.Policy, CancellationToken.None);
        await components.EngineStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        components.CompleteEngineStartup();
        components.Listener.StopUnexpectedly();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => run.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains("runtime RPC listener", exception.Message, StringComparison.Ordinal);
        Assert.Equal(
            [
                "rendezvous-dispose",
                "listener-dispose",
                "engine-dispose",
                "guard-dispose",
                "settings-dispose",
                "transfer-dispose",
            ],
            components.Log.TakeLast(6));
    }

    [Fact]
    public async Task UnexpectedListenerDuringStartupStopsListenersBeforeCancellingEngineInitialization()
    {
        var components = new FakeComponents(CreateProductionPolicy());
        var runner = new RuntimeHostLiveRunner(components);
        var run = runner.RunAsync(components.Policy, CancellationToken.None);
        await components.EngineStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        components.Listener.StopUnexpectedly();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => run.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains("runtime RPC listener", exception.Message, StringComparison.Ordinal);
        Assert.True(components.EngineStartupCancelled.Task.IsCompletedSuccessfully);
        Assert.Equal(
            [
                "rendezvous-dispose",
                "listener-dispose",
                "guard-dispose",
                "settings-dispose",
                "transfer-dispose",
            ],
            components.Log.TakeLast(5));
    }

    [Fact]
    public async Task UnexpectedCompositionCompletionIsTerminal()
    {
        var components = new FakeComponents(CreateProductionPolicy());
        var runner = new RuntimeHostLiveRunner(components);
        var run = runner.RunAsync(components.Policy, CancellationToken.None);
        await components.EngineStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        components.CompleteEngineStartup();
        components.Engine.StopCompositionUnexpectedly();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => run.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains("runtime composition", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnexpectedRendezvousCompletionIsTerminal()
    {
        var components = new FakeComponents(CreateProductionPolicy());
        var runner = new RuntimeHostLiveRunner(components);
        var run = runner.RunAsync(components.Policy, CancellationToken.None);
        await components.EngineStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        components.CompleteEngineStartup();
        components.Rendezvous.StopUnexpectedly();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => run.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains("tray bootstrap rendezvous", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TrayAttachFailureDisposesSessionAndAdmission()
    {
        var session = new FakeRpcServer { FailAttach = true };
        var lease = new CountingDisposable();
        await using var server = new RuntimeHostTrayLeaseRpcServer(session, lease);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            server.AttachAsync(null!, CancellationToken.None));

        Assert.Equal(1, session.DisposeCount);
        Assert.Equal(1, lease.DisposeCount);
    }

    [Fact]
    public void TransferredOwnershipIsClaimedOnceAndReleasedByItsFinalOwner()
    {
        var original = new CountingOwnershipLease();
        using var transfer = new TransferredRuntimeOwnershipLeaseFactory(original);

        var claimed = transfer.Acquire();
        Assert.Throws<InvalidOperationException>(() => transfer.Acquire());
        transfer.Dispose();
        Assert.Equal(0, original.DisposeCount);

        claimed.Dispose();
        Assert.Equal(1, original.DisposeCount);
    }

    [Fact]
    public void UnclaimedTransferredOwnershipIsReleasedByFactoryDisposal()
    {
        var original = new CountingOwnershipLease();
        var transfer = new TransferredRuntimeOwnershipLeaseFactory(original);

        transfer.Dispose();

        Assert.Equal(1, original.DisposeCount);
    }

    private static RuntimeHostLiveLaunchPolicy CreateProductionPolicy()
    {
        var root = Path.Combine(Path.GetTempPath(), "joydex-live-runner-tests");
        return RuntimeHostLiveLaunchPolicy.Create(
            RuntimeHostLaunchMode.Production,
            Path.Combine(root, "chosen-name.json"),
            pipeName: null,
            instanceName: null,
            deploymentDirectory: root);
    }

    private static RuntimeIpcConnectionContext Context(
        RuntimeHostLiveLaunchPolicy policy,
        string connectionId) =>
        new(
            connectionId,
            policy.Endpoint,
            new RuntimeIpcPeer(
                Environment.ProcessId,
                DateTimeOffset.UtcNow,
                policy.Endpoint.SessionId,
                "test-user",
                IsElevated: false));

    private sealed class FakeComponents(RuntimeHostLiveLaunchPolicy policy) :
        IRuntimeHostComponentFactory
    {
        private readonly TaskCompletionSource<IRuntimeHostEngine> _engineStartup =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IRuntimeOwnershipLease? _engineLease;

        public RuntimeHostLiveLaunchPolicy Policy { get; } = policy;
        public ConcurrentQueue<string> Log { get; } = new();
        public TaskCompletionSource EngineStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource EngineStartupCancelled { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public FakeRuntimeListener Listener { get; private set; } = null!;
        public FakeRendezvous Rendezvous { get; private set; } = null!;
        public FakeEngine Engine { get; } = new();
        public FakeSettingsLauncher SettingsLauncher { get; } = new();
        public CancellationToken EngineLifetime { get; private set; }

        public IRuntimeOwnershipLeaseFactory PrepareOwnership(RuntimeHostLiveLaunchPolicy _)
        {
            Log.Enqueue("guard");
            return new FakeOwnershipTransfer(Log);
        }

        public IRuntimeHostRuntimeListener StartRuntimeListener(
            RuntimeHostLiveLaunchPolicy _,
            RuntimeRpcServerFactory connectionFactory)
        {
            Log.Enqueue("listener");
            Listener = new FakeRuntimeListener(connectionFactory, Log);
            return Listener;
        }

        public IRuntimeHostRendezvous StartRendezvous(
            RuntimeHostLiveLaunchPolicy _,
            IRuntimeHostRuntimeListener __)
        {
            Log.Enqueue("rendezvous");
            Rendezvous = new FakeRendezvous(Log);
            return Rendezvous;
        }

        public RuntimeHostSettingsProcessOwner CreateSettingsProcessOwner(
            RuntimeHostLiveLaunchPolicy _,
            IRuntimeHostRuntimeListener __)
        {
            Log.Enqueue("settings");
            return new RuntimeHostSettingsProcessOwner(
                SettingsLauncher,
                new RecordingAsyncDisposable(Log, "settings-dispose"));
        }

        public Task<IRuntimeHostEngine> StartEngineAsync(
            RuntimeHostLiveLaunchPolicy _,
            IRuntimeOwnershipLeaseFactory ownershipLeaseFactory,
            IRuntimeSettingsProcessLauncher __,
            CancellationToken startupCancellationToken)
        {
            Log.Enqueue("engine-start");
            _engineLease = ownershipLeaseFactory.Acquire();
            Log.Enqueue("engine-claim-guard");
            Engine.SetOwnership(_engineLease, Log);
            EngineLifetime = startupCancellationToken;
            EngineStarted.TrySetResult();
            startupCancellationToken.Register(() =>
            {
                EngineStartupCancelled.TrySetResult();
                if (_engineStartup.TrySetCanceled(startupCancellationToken))
                {
                    Interlocked.Exchange(ref _engineLease, null)?.Dispose();
                }
            });
            return _engineStartup.Task;
        }

        public void CompleteEngineStartup() => _engineStartup.TrySetResult(Engine);
    }

    private sealed class FakeOwnershipTransfer(ConcurrentQueue<string> log) :
        IRuntimeOwnershipLeaseFactory,
        IDisposable
    {
        private readonly CountingOwnershipLease _lease = new(log, "guard-dispose");
        private int _claimed;

        public IRuntimeOwnershipLease Acquire()
        {
            if (Interlocked.Exchange(ref _claimed, 1) != 0)
            {
                throw new InvalidOperationException("Already claimed.");
            }
            return _lease;
        }

        public void Dispose() => log.Enqueue("transfer-dispose");
    }

    private sealed class FakeRuntimeListener(
        RuntimeRpcServerFactory connectionFactory,
        ConcurrentQueue<string> log) : IRuntimeHostRuntimeListener
    {
        private readonly TaskCompletionSource _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Completion => _completion.Task;
        public Func<Task>? BeforeDispose { get; set; }

        public ValueTask<IRuntimeRpcServer> ConnectAsync(
            RuntimeIpcConnectionContext context,
            RuntimeClientKind kind,
            CancellationToken connectionLifetime) =>
            connectionFactory(
                context,
                kind,
                new FakeRpcClient(),
                static _ => { },
                connectionLifetime);

        public void StopUnexpectedly() => _completion.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            log.Enqueue("listener-dispose");
            if (BeforeDispose is { } beforeDispose)
            {
                await beforeDispose();
            }
            _completion.TrySetResult();
        }
    }

    private sealed class FakeRendezvous(ConcurrentQueue<string> log) : IRuntimeHostRendezvous
    {
        private readonly TaskCompletionSource _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _claimCount;
        private int _releasedCount;

        public Task Completion => _completion.Task;
        public TaskCompletionSource FirstClaim { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public int ClaimCount => Volatile.Read(ref _claimCount);
        public int ReleasedCount => Volatile.Read(ref _releasedCount);

        public IDisposable ClaimTrayConnection(
            RuntimeIpcConnectionContext context,
            RuntimeClientKind authorizedKind,
            CancellationToken physicalConnectionLifetime)
        {
            Assert.Equal(RuntimeClientKind.Tray, authorizedKind);
            Interlocked.Increment(ref _claimCount);
            log.Enqueue($"claim:{context.ConnectionId}");
            FirstClaim.TrySetResult();
            return new CallbackDisposable(() =>
            {
                Interlocked.Increment(ref _releasedCount);
                log.Enqueue($"release:{context.ConnectionId}");
            });
        }

        public void StopUnexpectedly() => _completion.TrySetResult();

        public ValueTask DisposeAsync()
        {
            log.Enqueue("rendezvous-dispose");
            _completion.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeEngine : IRuntimeHostEngine
    {
        private readonly TaskCompletionSource _shutdown = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _composition = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private IRuntimeOwnershipLease? _ownership;
        private ConcurrentQueue<string>? _log;
        private int _sessionCount;

        public Task ShutdownRequested => _shutdown.Task;
        public Task CompositionCompletion => _composition.Task;
        public int SessionCount => Volatile.Read(ref _sessionCount);

        public void SetOwnership(
            IRuntimeOwnershipLease ownership,
            ConcurrentQueue<string> log)
        {
            _ownership = ownership;
            _log = log;
        }

        public IRuntimeRpcServer CreateSession(
            string connectionId,
            RuntimeClientKind authorizedClientKind,
            IRuntimeRpcClient client,
            CancellationToken connectionCancellationToken,
            Action<Exception?> abortConnection)
        {
            Interlocked.Increment(ref _sessionCount);
            _log!.Enqueue($"session:{connectionId}");
            return new FakeRpcServer();
        }

        public void RequestShutdown() => _shutdown.TrySetResult();

        public void StopCompositionUnexpectedly() => _composition.TrySetResult();

        public ValueTask DisposeAsync()
        {
            _log!.Enqueue("engine-dispose");
            Interlocked.Exchange(ref _ownership, null)?.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeSettingsLauncher : IRuntimeSettingsProcessLauncher
    {
        public TaskCompletionSource<RuntimeCommandRequest> Opened { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<RuntimeCommandResult> OpenAsync(
            RuntimeCommandRequest request,
            CancellationToken runtimeCancellationToken)
        {
            runtimeCancellationToken.ThrowIfCancellationRequested();
            Opened.TrySetResult(request);
            return Task.FromResult(new RuntimeCommandResult(
                request.OperationId,
                request.Kind,
                RuntimeCommandStatus.Completed));
        }
    }

    private sealed class FakeRpcClient : IRuntimeRpcClient
    {
        public Task RuntimeEventAsync(RuntimeEvent runtimeEvent, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RuntimeInputEventAsync(
            RuntimeConnectionInputEvent inputEvent,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RuntimeCommandCompletedAsync(
            RuntimeCommandResult result,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FakeRpcServer : IRuntimeRpcServer, IAsyncDisposable
    {
        public bool FailAttach { get; init; }
        public int DisposeCount { get; private set; }

        public Task<RuntimeAttachResult> AttachAsync(
            RuntimeAttachRequest request,
            CancellationToken cancellationToken) =>
            FailAttach
                ? Task.FromException<RuntimeAttachResult>(
                    new InvalidOperationException("Synthetic attach failure."))
                : throw new NotSupportedException();

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

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingOwnershipLease(
        ConcurrentQueue<string>? log = null,
        string? entry = null) : IRuntimeOwnershipLease
    {
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
            if (entry is not null)
            {
                log!.Enqueue(entry);
            }
        }
    }

    private sealed class CountingDisposable : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }

    private sealed class CallbackDisposable(Action callback) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                callback();
            }
        }
    }

    private sealed class RecordingAsyncDisposable(
        ConcurrentQueue<string> log,
        string entry) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            log.Enqueue(entry);
            return ValueTask.CompletedTask;
        }
    }
}
