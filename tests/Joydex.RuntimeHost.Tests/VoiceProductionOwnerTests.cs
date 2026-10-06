using Joydex.Contracts;
using Joydex.Core.Config;
using Joydex.Core.Mapping;
using Joydex.Core.TaskAlerts;
using Joydex.Core.Voice;
using Joydex.Ipc;
using Joydex.RuntimeHost.Plugins;
using Joydex.RuntimeHost.Plugins.Voice;
using Joydex.RuntimeHost.Production;
using Joydex.Windows.Actions;
using Joydex.Windows.Voice;
using System.Collections.Concurrent;

namespace Joydex.RuntimeHost.Tests;

public sealed class VoiceProductionOwnerTests
{
    [Theory]
    [InlineData(VoicePeSessionMode.JoydexOwner, false, false, true)]
    [InlineData(VoicePeSessionMode.JoydexOwner, false, true, true)]
    [InlineData(VoicePeSessionMode.LastVoiceFallback, false, false, false)]
    [InlineData(VoicePeSessionMode.LastVoiceFallback, true, false, true)]
    public async Task WorkerReceivesHandoffBrokerIndependentlyOfMessaging(
        VoicePeSessionMode mode, bool messaging, bool dryRun, bool needsBroker)
    {
        var starter = new FakeBrokerStarter();
        const string pipeName = "Joydex.Test.Handoff";
        await using var broker = new ProductionDesktopBrokerManager(
            Paths().DesktopBridgeHost, pipeName, _ => { }, CancellationToken.None,
            starter, TimeSpan.FromSeconds(1));
        var generations = new FakeGenerationFactory(new FakeGeneration());
        var bundle = Bundle();
        bundle = bundle with
        {
            Companion = new CompanionConfig { Safety = new SafetyOptions { DryRun = dryRun } },
            Voice = bundle.Voice with { SessionMode = mode, DesktopTaskMessagingEnabled = messaging },
        };
        var owner = await VoiceProductionOwner.StartAsync(
            new FakeHost(), Paths(), broker, generations, bundle,
            static (_, _) => Task.CompletedTask, TimeProvider.System,
            CancellationToken.None, CancellationToken.None);
        try
        {
            var configuration = Assert.Single(generations.Configurations);
            Assert.Equal(needsBroker ? pipeName : "Joydex.DesktopBridge.unavailable",
                configuration.DesktopBridgePipeName);
            Assert.Equal(messaging, configuration.Preferences.DesktopTaskMessagingEnabled);
            Assert.Equal(dryRun, configuration.Safety.DryRun);
            Assert.Equal(needsBroker ? 1 : 0, starter.StartCount);
        }
        finally
        {
            await owner.DisposeAsync();
        }
        Assert.Equal(needsBroker, starter.Process.Completion.IsCompleted);
    }

    [Fact]
    public async Task CandidateRemainsQuiescentUntilCommittedAndPublishesLatestSnapshot()
    {
        var host = new FakeHost();
        var generation = new FakeGeneration();
        var factory = new FakeGenerationFactory(generation);
        await using var owner = await CreateAsync(host, factory);
        generation.Publish(WorkerSnapshot(1, 2, active: true));

        Assert.Empty(host.VoiceStates);
        Assert.False(generation.Active);
        Assert.Equal(BundledPluginLifecycleState.Starting, host.Health.Last().State);
        Assert.False(await generation.NavigateAsync(VoiceTaskId));
        Assert.False((await generation.ExecuteActionAsync()).Executed);

        owner.Commit();

        Assert.True(generation.Active);
        Assert.True(Assert.Single(host.VoiceStates).Snapshot.SessionActive);
        Assert.Equal(BundledPluginLifecycleState.Ready, host.Health.Last().State);
    }

    [Fact]
    public async Task WorkerCrashesStayContainedAndBackoffPersistsUntilStable()
    {
        var host = new FakeHost();
        var first = new FakeGeneration();
        var second = new FakeGeneration();
        var third = new FakeGeneration();
        var factory = new FakeGenerationFactory(first, second, third);
        var delays = new ControlledDelays();
        var time = new ManualTimeProvider();
        await using var owner = await CreateAsync(host, factory, delays.DelayAsync, time);
        first.SetSnapshot(WorkerSnapshot(1, 1, active: true));
        owner.Commit();

        first.Fail(new IOException("worker exited"));
        await WaitForAsync(() => delays.Count == 1);

        Assert.Equal([1], delays.Snapshot());
        Assert.False(host.VoiceStates.Last().Snapshot.SessionActive);
        Assert.True(host.VoiceStates.Last().Snapshot.Stale);
        Assert.Equal(1, host.IdleCount);
        Assert.False(owner.Completion.IsCompleted);

        delays.Release(0);
        await WaitForAsync(() => factory.StartCount == 2 && second.Active);
        second.Fail(new IOException("worker exited again"));
        await WaitForAsync(() => delays.Count == 2);

        Assert.Equal([1, 2], delays.Snapshot());
        Assert.False(owner.Completion.IsCompleted);
        delays.Release(1);
        await WaitForAsync(() => factory.StartCount == 3 && third.Active);
        time.Advance(TimeSpan.FromMinutes(1));
        third.Fail(new IOException("stable worker exited"));
        await WaitForAsync(() => delays.Count == 3);
        Assert.Equal([1, 2, 1], delays.Snapshot());
    }

    [Fact]
    public async Task UnconfirmedCrashCleanupBlocksVoiceWithoutReplacement()
    {
        var host = new FakeHost();
        var generation = new FakeGeneration
        {
            DisposeFailure = new VoiceOwnershipCleanupException(
                "cleanup uncertain",
                [new IOException("job not empty")]),
        };
        var factory = new FakeGenerationFactory(generation, new FakeGeneration());
        var owner = await CreateAsync(host, factory);
        owner.Commit();

        generation.Fail(new IOException("worker exited"));
        await WaitForAsync(() => host.Health.Last().State == BundledPluginLifecycleState.Blocked);

        Assert.Equal(1, factory.StartCount);
        Assert.False(owner.Completion.IsCompleted);
        await Assert.ThrowsAsync<VoiceOwnershipCleanupException>(
            () => owner.DisposeAsync().AsTask());
        Assert.Equal(1, factory.StartCount);
    }

    [Fact]
    public async Task CallbackAuthorityUsesCurrentCommittedPolicyAndRejectsStaleGeneration()
    {
        var host = new FakeHost();
        var first = new FakeGeneration();
        var second = new FakeGeneration();
        var factory = new FakeGenerationFactory(first, second);
        var delays = new ControlledDelays();
        await using var owner = await CreateAsync(host, factory, delays.DelayAsync);
        owner.Commit();

        Assert.False(await first.NavigateAsync(Guid.NewGuid().ToString("D")));
        Assert.True(await first.NavigateAsync(VoiceTaskId));
        _ = await first.ExecuteActionAsync();
        Assert.Equal([1], host.ExecutedPolicyVersions);
        var request = Assert.Single(host.ExecutedRequests);
        Assert.Equal("Voice PE wake", request.BindingName);
        Assert.Equal(CompanionConfig.AlwaysBank, request.Bank);
        Assert.Equal(0, request.Button);
        Assert.Equal("wake", request.Trigger);
        Assert.Equal(CodexAction.StartVoiceChat, request.Action);
        Assert.Equal("voice-pe", request.DeviceId);

        host.PolicyVersion = 2;
        owner.RefreshPolicy(Bundle());
        _ = await first.ExecuteActionAsync();
        Assert.Equal([1, 2], host.ExecutedPolicyVersions);

        first.Fail(new IOException("worker exited"));
        await WaitForAsync(() => delays.Count == 1);
        Assert.False(await first.NavigateAsync(VoiceTaskId));
        delays.Release(0);
        await WaitForAsync(() => second.Active);
        Assert.True(await second.NavigateAsync(VoiceTaskId));
    }

    [Fact]
    public async Task HiddenInactiveEdgeSignalsIdleOnceWhenLatestSnapshotIsActive()
    {
        var host = new FakeHost();
        var generation = new FakeGeneration();
        var factory = new FakeGenerationFactory(generation);
        await using var owner = await CreateAsync(host, factory);
        generation.SetSnapshot(WorkerSnapshot(1, 1, active: true));
        owner.Commit();

        generation.SignalIdle(sequence: 3);
        generation.Publish(WorkerSnapshot(1, 2, active: true));
        generation.SignalIdle(sequence: 2);
        generation.SignalIdle(sequence: 2);

        Assert.Equal(1, host.IdleCount);
        Assert.True(owner.IsSessionActive);
    }

    [Fact]
    public async Task InitialFailureRemainsFaultedAndNeverStartsSupervision()
    {
        var host = new FakeHost();
        var factory = new FakeGenerationFactory(new InvalidDataException("bad config"));

        await Assert.ThrowsAsync<InvalidDataException>(() => CreateAsync(host, factory));

        Assert.Equal(BundledPluginLifecycleState.Faulted, host.Health.Last().State);
        Assert.Equal(1, factory.StartCount);
    }

    private const string VoiceTaskId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

    private static Task<VoiceProductionOwner> CreateAsync(
        FakeHost host,
        FakeGenerationFactory factory,
        Func<int, CancellationToken, Task>? delay = null,
        TimeProvider? timeProvider = null) =>
        VoiceProductionOwner.StartForTestAsync(
            host,
            Paths(),
            factory,
            Bundle(),
            delay ?? (static (_, _) => Task.CompletedTask),
            timeProvider ?? TimeProvider.System,
            CancellationToken.None,
            CancellationToken.None);

    private static SettingsBundle Bundle() => new(
        CompanionConfig.CreateSafeDefault(),
        VoicePePreferences.Default with
        {
            Enabled = true,
            DeviceEndpoint = "http://127.0.0.1/",
            PinnedTaskId = VoiceTaskId,
            PinnedTaskLabel = "Voice",
        },
        PebbleIndexPreferences.Default,
        TaskAlertPreferences.Default);

    private static ProductionRuntimePaths Paths()
    {
        var root = Path.Combine(Path.GetTempPath(), "joydex-voice-owner-tests");
        return new ProductionRuntimePaths(
            Path.Combine(root, "companion.json"), root,
            Path.Combine(root, "voice.json"), Path.Combine(root, "active-voice.json"),
            Path.Combine(root, "pebble.json"), Path.Combine(root, "pebble.secret"),
            Path.Combine(root, "inbox"), Path.Combine(root, "alerts.json"),
            Path.Combine(root, "alert-state.json"), Path.Combine(root, "webview"),
            Path.Combine(root, "log"), Path.Combine(root, "links.json"),
            Path.Combine(root, "guardian.json"), Path.Combine(root, "broker.exe"),
            Path.Combine(root, "Joydex.App.exe"));
    }

    private static VoiceWorkerSnapshot WorkerSnapshot(long generation, long sequence, bool active) => new(
        generation,
        sequence,
        new RuntimeVoiceSnapshot(
            RuntimeVoiceSessionState.Armed,
            OwnerReady: true,
            SessionActive: active,
            HistoryAvailable: false,
            Stale: false,
            "Armed",
            null,
            ConversationVersion: sequence),
        []);

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline) { throw new TimeoutException(); }
            await Task.Delay(10);
        }
    }

    private sealed class FakeHost : IVoicePluginHostServices
    {
        public ConcurrentQueue<ProductionVoiceState> VoiceStates { get; } = [];
        public ConcurrentQueue<BundledPluginHealth> Health { get; } = [];
        public ConcurrentQueue<int> ExecutedPolicyVersions { get; } = [];
        public ConcurrentQueue<ActionRequest> ExecutedRequests { get; } = [];
        private int _policyVersion = 1;
        public int PolicyVersion
        {
            get => Volatile.Read(ref _policyVersion);
            set => Volatile.Write(ref _policyVersion, value);
        }
        private int _idleCount;
        public int IdleCount => Volatile.Read(ref _idleCount);

        public VoiceHostCommandPolicy CreateVoiceHostPolicy(SettingsBundle activeSettings)
        {
            var version = PolicyVersion;
            return new VoiceHostCommandPolicy(
                activeSettings.Voice.Normalize(),
                activeSettings.Companion.Safety,
                new FakeNavigator(),
                (request, _) =>
                {
                    ExecutedRequests.Enqueue(request);
                    ExecutedPolicyVersions.Enqueue(version);
                    return Task.FromResult(ActionExecutionResult.Success("started"));
                });
        }

        public void PublishVoice(ProductionVoiceState state, bool reset = false) => VoiceStates.Enqueue(state);
        public void PublishVoiceBecameIdle() => Interlocked.Increment(ref _idleCount);
        public void PublishVoiceHealth(VoiceProductionOwner owner, BundledPluginHealth health) => Health.Enqueue(health);
        public void ClearVoiceOwner(VoiceProductionOwner owner) { }
        public void WriteLog(string message) { }
    }

    private sealed class FakeNavigator : IPinnedVoiceTargetNavigator
    {
        public Task<bool> NavigateAsync(string taskId, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class FakeGenerationFactory(params object[] outcomes) : IVoiceWorkerGenerationFactory
    {
        private readonly Queue<object> _outcomes = new(outcomes);
        public List<VoiceWorkerGenerationConfiguration> Configurations { get; } = [];
        private int _startCount;
        public int StartCount => Volatile.Read(ref _startCount);

        public Task<IVoiceWorkerGeneration> StartAsync(
            VoiceWorkerGenerationConfiguration configuration,
            IVoiceWorkerHostCallbacks callbacks,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _startCount);
            Configurations.Add(configuration);
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = _outcomes.Dequeue();
            if (outcome is Exception exception)
            {
                return Task.FromException<IVoiceWorkerGeneration>(exception);
            }
            var generation = (FakeGeneration)outcome;
            generation.Bind(configuration.Generation, callbacks);
            return Task.FromResult<IVoiceWorkerGeneration>(generation);
        }
    }

    private sealed class FakeBrokerStarter : IProductionDesktopBrokerStarter
    {
        public FakeBrokerProcess Process { get; } = new();
        public int StartCount { get; private set; }

        public Task<IProductionDesktopBrokerProcess> StartAsync(
            string executablePath, string pipeName, Action<string> log, CancellationToken cancellationToken)
        {
            StartCount++;
            return Task.FromResult<IProductionDesktopBrokerProcess>(Process);
        }
    }

    private sealed class FakeBrokerProcess : IProductionDesktopBrokerProcess
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Completion => _completion.Task;
        public ValueTask DisposeAsync()
        {
            _completion.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeGeneration : IVoiceWorkerGeneration
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IVoiceWorkerHostCallbacks? _callbacks;
        public long Generation { get; private set; }
        private VoiceWorkerSnapshot _snapshot = WorkerSnapshot(1, 1, active: false);
        public VoiceWorkerSnapshot Snapshot => Volatile.Read(ref _snapshot);
        public Task Completion => _completion.Task;
        private int _active;
        public bool Active => Volatile.Read(ref _active) != 0;
        public Exception? DisposeFailure { get; init; }

        public void Bind(long generation, IVoiceWorkerHostCallbacks callbacks)
        {
            Generation = generation;
            _callbacks = callbacks;
            Volatile.Write(ref _snapshot, Snapshot with { Generation = generation });
        }

        public VoiceWorkerSnapshot ActivateCallbacks()
        {
            Volatile.Write(ref _active, 1);
            return Snapshot;
        }

        public void DeactivateCallbacks() => Volatile.Write(ref _active, 0);
        public Task EndSessionAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RefreshConversationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<RuntimeVoiceConversationPage> GetConversationPageAsync(string? token, CancellationToken ct) =>
            Task.FromResult(new RuntimeVoiceConversationPage([]));

        public void SetSnapshot(VoiceWorkerSnapshot snapshot) => Volatile.Write(ref _snapshot, snapshot);
        public void Publish(VoiceWorkerSnapshot snapshot)
        {
            Volatile.Write(ref _snapshot, snapshot);
            if (Active) { _callbacks!.PublishSnapshot(snapshot); }
        }

        public void Fail(Exception exception) => _completion.TrySetException(exception);
        public Task<bool> NavigateAsync(string taskId) =>
            _callbacks!.NavigateAsync(Generation, taskId, CancellationToken.None);
        public Task<ActionExecutionResult> ExecuteActionAsync() => _callbacks!.ExecuteActionAsync(
            Generation,
            new ActionRequest("untrusted", "x", 99, "x", CodexAction.NextTask, DateTimeOffset.MinValue),
            CancellationToken.None);
        public void SignalIdle(long sequence) => _callbacks!.VoiceBecameIdle(Generation, sequence);

        public ValueTask DisposeAsync() => DisposeFailure is null
            ? ValueTask.CompletedTask
            : ValueTask.FromException(DisposeFailure);
    }

    private sealed class ControlledDelays
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource> _waiters = [];
        private readonly List<int> _attempts = [];
        public int Count { get { lock (_gate) { return _attempts.Count; } } }
        public int[] Snapshot() { lock (_gate) { return [.. _attempts]; } }

        public Task DelayAsync(int attempt, CancellationToken cancellationToken)
        {
            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
            lock (_gate)
            {
                _attempts.Add(attempt);
                _waiters.Add(waiter);
            }
            return waiter.Task;
        }

        public void Release(int index)
        {
            TaskCompletionSource waiter;
            lock (_gate) { waiter = _waiters[index]; }
            waiter.TrySetResult();
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate) { return _now; }
        }

        public void Advance(TimeSpan duration)
        {
            lock (_gate) { _now += duration; }
        }
    }
}
