using System.Collections.Concurrent;
using Joydex.Contracts;
using Joydex.Core.Voice;
using Joydex.Ipc;
using Joydex.RuntimeHost.Plugins;
using Joydex.RuntimeHost.Plugins.Pebble;
using Joydex.RuntimeHost.Production;

namespace Joydex.RuntimeHost.Tests;

public sealed class PebbleIndexProductionOwnerTests
{
    [Fact]
    public async Task CandidateIsQuiescentUntilCommitAndPublishesHighestStatus()
    {
        var host = new FakeHost();
        var generation = new FakeGeneration();
        var factory = new FakeGenerationFactory(generation);
        await using var owner = await CreateAsync(host, factory);
        generation.PublishDirect(Status(1, 2, running: true));

        Assert.Empty(host.Statuses);
        Assert.False(generation.Active);
        Assert.Equal(BundledPluginLifecycleState.Starting, host.Health.Last().State);

        owner.Commit();

        Assert.True(generation.Active);
        Assert.Equal(2, Assert.Single(host.Statuses).Sequence);
        Assert.Equal(BundledPluginLifecycleState.Ready, host.Health.Last().State);
    }

    [Fact]
    public async Task CrashIsContainedAndBackoffPersistsUntilStable()
    {
        var host = new FakeHost();
        var first = new FakeGeneration();
        var second = new FakeGeneration();
        var third = new FakeGeneration();
        var factory = new FakeGenerationFactory(first, second, third);
        var delays = new ControlledDelays();
        var time = new ManualTimeProvider();
        await using var owner = await CreateAsync(host, factory, delays.DelayAsync, time);
        owner.Commit();

        first.Fail(new IOException("worker exited"));
        await WaitForAsync(() => delays.Count == 1);
        Assert.Equal([1], delays.Snapshot());
        Assert.False(host.Statuses.Last().Running);
        Assert.False(owner.Completion.IsCompleted);

        delays.Release(0);
        await WaitForAsync(() => factory.StartCount == 2 && second.Active);
        second.Fail(new IOException("worker exited again"));
        await WaitForAsync(() => delays.Count == 2);
        Assert.Equal([1, 2], delays.Snapshot());

        delays.Release(1);
        await WaitForAsync(() => factory.StartCount == 3 && third.Active);
        time.Advance(TimeSpan.FromMinutes(1));
        third.Fail(new IOException("stable worker exited"));
        await WaitForAsync(() => delays.Count == 3);
        Assert.Equal([1, 2, 1], delays.Snapshot());
        Assert.False(owner.Completion.IsCompleted);
    }

    [Fact]
    public async Task CleanupUncertaintyBlocksPebbleAndIsRetainedForOwnerDispose()
    {
        var host = new FakeHost();
        var first = new FakeGeneration
        {
            DisposeFailure = new ProductionOwnershipCleanupException(
                "cleanup uncertain",
                [new IOException("job not empty")]),
        };
        var factory = new FakeGenerationFactory(first, new FakeGeneration());
        var owner = await CreateAsync(host, factory);
        owner.Commit();

        first.Fail(new IOException("worker exited"));
        await WaitForAsync(() => host.Health.Last().State == BundledPluginLifecycleState.Blocked);
        first.PublishDirect(Status(1, 20, running: true));

        Assert.False(host.Statuses.Last().Running);
        Assert.Equal(1, factory.StartCount);
        Assert.False(owner.Completion.IsCompleted);
        await Assert.ThrowsAsync<ProductionOwnershipCleanupException>(
            () => owner.DisposeAsync().AsTask());
        Assert.Equal(1, factory.StartCount);
    }

    [Fact]
    public async Task CommittedDisposePublishesUnavailableButCandidateDisposeIsSilent()
    {
        var candidateHost = new FakeHost();
        var candidate = await CreateAsync(candidateHost, new FakeGenerationFactory(new FakeGeneration()));
        await candidate.DisposeAsync();
        Assert.Empty(candidateHost.Statuses);

        var committedHost = new FakeHost();
        var committed = await CreateAsync(committedHost, new FakeGenerationFactory(new FakeGeneration()));
        committed.Commit();
        await committed.DisposeAsync();

        Assert.False(committedHost.Statuses.Last().Running);
        Assert.Equal("Pebble Index worker is stopped.", committedHost.Statuses.Last().Message);
    }

    [Fact]
    public async Task InitialFailureRemainsFaultedAndNeverStartsSupervision()
    {
        var host = new FakeHost();
        var factory = new FakeGenerationFactory(new InvalidDataException("bad config"));

        await Assert.ThrowsAsync<InvalidDataException>(() => CreateAsync(host, factory));

        Assert.Equal(BundledPluginLifecycleState.Faulted, host.Health.Last().State);
        Assert.Equal(1, factory.StartCount);
        Assert.Empty(host.Statuses);
    }

    private static Task<PebbleIndexProductionOwner> CreateAsync(
        FakeHost host,
        FakeGenerationFactory factory,
        Func<int, CancellationToken, Task>? delay = null,
        TimeProvider? timeProvider = null) =>
        PebbleIndexProductionOwner.StartForTestAsync(
            host,
            Paths(),
            factory,
            Preferences(),
            delay ?? (static (_, _) => Task.CompletedTask),
            timeProvider ?? TimeProvider.System,
            CancellationToken.None,
            CancellationToken.None);

    private static PebbleIndexPreferences Preferences() => PebbleIndexPreferences.Default with
    {
        Enabled = true,
        TargetTaskId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
        TargetHostId = "local",
        TargetTaskLabel = "Pebble",
    };

    private static ProductionRuntimePaths Paths()
    {
        var root = Path.Combine(Path.GetTempPath(), "joydex-pebble-owner-tests");
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

    private static PebbleWorkerStatus Status(long generation, long sequence, bool running) => new(
        generation,
        sequence,
        running,
        running ? "Pebble Index receiver is listening on loopback." : "Unavailable.",
        0);

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline) { throw new TimeoutException(); }
            await Task.Delay(10);
        }
    }

    private sealed class FakeHost : IPebblePluginHostServices
    {
        public ConcurrentQueue<PebbleWorkerStatus> Statuses { get; } = [];
        public ConcurrentQueue<BundledPluginHealth> Health { get; } = [];

        public void PublishPebble(PebbleWorkerStatus status) => Statuses.Enqueue(status);
        public void PublishPebbleHealth(
            PebbleIndexProductionOwner owner,
            BundledPluginHealth health) => Health.Enqueue(health);
        public void ClearPebbleOwner(PebbleIndexProductionOwner owner) { }
        public void WriteLog(string message) { }
    }

    private sealed class FakeGenerationFactory(params object[] outcomes) : IPebbleWorkerGenerationFactory
    {
        private readonly Queue<object> _outcomes = new(outcomes);
        private int _startCount;
        public int StartCount => Volatile.Read(ref _startCount);

        public Task<IPebbleWorkerGeneration> StartAsync(
            PebbleWorkerGenerationConfiguration configuration,
            IPebbleWorkerHostCallbacks callbacks,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _startCount);
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = _outcomes.Dequeue();
            if (outcome is Exception exception)
            {
                return Task.FromException<IPebbleWorkerGeneration>(exception);
            }
            var generation = (FakeGeneration)outcome;
            generation.Bind(configuration.Generation, callbacks);
            return Task.FromResult<IPebbleWorkerGeneration>(generation);
        }
    }

    private sealed class FakeGeneration : IPebbleWorkerGeneration
    {
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IPebbleWorkerHostCallbacks? _callbacks;
        private PebbleWorkerStatus _status = Status(1, 1, running: true);
        private int _active;

        public long Generation { get; private set; }
        public PebbleWorkerStatus Status => Volatile.Read(ref _status);
        public Task Completion => _completion.Task;
        public bool Active => Volatile.Read(ref _active) != 0;
        public Exception? DisposeFailure { get; init; }

        public void Bind(long generation, IPebbleWorkerHostCallbacks callbacks)
        {
            Generation = generation;
            _callbacks = callbacks;
            Volatile.Write(ref _status, Status with { Generation = generation });
        }

        public PebbleWorkerStatus ActivateCallbacks()
        {
            Volatile.Write(ref _active, 1);
            return Status;
        }

        public void DeactivateCallbacks() => Volatile.Write(ref _active, 0);

        public void PublishDirect(PebbleWorkerStatus status)
        {
            Volatile.Write(ref _status, status);
            _callbacks!.PublishStatus(status);
        }

        public void Fail(Exception exception) => _completion.TrySetException(exception);

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
