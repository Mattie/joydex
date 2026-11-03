using Joydex.Core.TaskAlerts;
using Joydex.Core.T3;
using Joydex.Windows.TaskAlerts;

namespace Joydex.Tests;

public sealed class T3DesktopTaskAlertMonitorTests
{
    private static readonly T3DesktopEnvironment Local = new("local-native", "Local desktop", true, null, true);
    private static readonly T3DesktopEnvironment Remote = new("remote-native", "Remote desktop", false, "remote", true, TransportKind: "ssh");

    [Fact]
    public async Task LocalAndRemoteShareThePoolAndFailureOnlyMarksItsOwnSourceUnavailable()
    {
        using var files = new Scratch();
        await using var coordinator = files.Coordinator();
        var catalog = new T3DesktopDiscoveryResult(true, true, [Local, Remote]);
        coordinator.SetT3DesktopEnvironments(catalog);
        var now = DateTimeOffset.UtcNow;
        Apply(coordinator, Local, now);
        Apply(coordinator, Remote, now);
        Assert.Equal(2, coordinator.GetSnapshot().Assignments.Count);
        coordinator.SetSourceUnavailable(new(TaskAlertBackend.RemoteT3, Remote.EnvironmentId), new() { ErrorClass = "Offline" });
        var snapshot = coordinator.GetSnapshot();
        Assert.False(snapshot.Assignments.Single(task => task.TaskIdentity.EnvironmentId == Local.EnvironmentId).SourceUnavailable);
        Assert.True(snapshot.Assignments.Single(task => task.TaskIdentity.EnvironmentId == Remote.EnvironmentId).SourceUnavailable);
        Assert.True(snapshot.T3Statuses!.Single(status => status.EnvironmentId == Local.EnvironmentId).Success);
        Assert.False(snapshot.T3Statuses!.Single(status => status.EnvironmentId == Remote.EnvironmentId).Success);
    }

    [Fact]
    public async Task FailedAndPartialCatalogsRetainSourcesButPositiveDisableStopsTheirUpdates()
    {
        using var files = new Scratch();
        await using var coordinator = files.Coordinator();
        coordinator.SetT3DesktopEnvironments(new(true, true, [Local, Remote]));
        var now = DateTimeOffset.UtcNow;
        Apply(coordinator, Local, now);
        Apply(coordinator, Remote, now);
        coordinator.SetT3DesktopEnvironments(new(false, false, [], "Catalog unavailable"));
        coordinator.SetT3DesktopEnvironments(new(true, false, [Local]));
        Apply(coordinator, Remote, now.AddSeconds(1), 2);
        Assert.False(coordinator.GetSnapshot().Assignments.Single(task => task.TaskIdentity.EnvironmentId == Remote.EnvironmentId).SourceUnavailable);
        coordinator.SetT3DesktopEnvironments(new(true, true, [Local, Remote with { Enabled = false }]));
        var retained = coordinator.GetSnapshot().Assignments.Single(task => task.TaskIdentity.EnvironmentId == Remote.EnvironmentId);
        Assert.True(retained.SourceUnavailable);
        Apply(coordinator, Remote, now.AddSeconds(2), 3);
        Assert.Equal(retained, coordinator.GetSnapshot().Assignments.Single(task => task.TaskIdentity.EnvironmentId == Remote.EnvironmentId));
        Assert.False(coordinator.GetSnapshot().T3Statuses!.Single(status => status.EnvironmentId == Remote.EnvironmentId).Enabled);
    }

    [Fact]
    public async Task UnsupportedAndUnknownRoutesHaveVisibleDiagnosticsWithoutInventedAssignments()
    {
        using var files = new Scratch();
        await using var coordinator = files.Coordinator();
        coordinator.SetT3DesktopEnvironments(new(true, false, [Local,
            new("", "Unknown local host", true, null, true, "Native identity unavailable", TransportKind: "wsl"),
            Remote with { UnsupportedReason = "Relay metadata is unavailable", TransportKind = "relay" }]));
        Assert.Empty(coordinator.GetSnapshot().Assignments);
        var statuses = coordinator.GetSnapshot().T3Statuses!;
        Assert.Contains(statuses, status => status.DisplayName == "Unknown local host" && status.ErrorClass == "UnsupportedRoute");
        Assert.Contains(statuses, status => status.EnvironmentId == Remote.EnvironmentId && status.TransportKind == "relay");
        Assert.Contains(statuses, status => status.TransportKind == "discovery");
    }

    [Fact]
    public async Task SlowRemoteDoesNotDelayLocalAndCompleteCatalogRemovalCancelsOnlyThatRead()
    {
        using var files = new Scratch();
        await using var coordinator = files.Coordinator();
        var catalog = new T3DesktopDiscoveryResult(true, true, [Remote, Local]);
        var remoteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remoteCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var monitor = new T3DesktopTaskAlertMonitor(coordinator, files.Options, _ => { },
            _ => Task.FromResult(catalog), async (environment, _, token) =>
            {
                if (environment == Remote)
                {
                    remoteEntered.TrySetResult();
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                    finally { remoteCanceled.TrySetResult(); }
                }
                return Result(environment, DateTimeOffset.UtcNow);
            });
        await monitor.RefreshOnceAsync();
        await remoteEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Until(() => coordinator.GetSnapshot().Assignments.Any(task => task.TaskIdentity.EnvironmentId == Local.EnvironmentId));
        catalog = new(true, true, [Local]);
        await monitor.RefreshOnceAsync();
        await remoteCanceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Single(coordinator.GetSnapshot().Assignments);
        Assert.False(coordinator.GetSnapshot().Assignments[0].SourceUnavailable);
    }

    [Fact]
    public async Task ExplicitLocalDisableStopsItsWorkerDespiteAnIncompleteRemoteCatalog()
    {
        using var files = new Scratch();
        await using var coordinator = files.Coordinator();
        var catalog = new T3DesktopDiscoveryResult(true, true, [Local, Remote]);
        var localEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var localCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var monitor = new T3DesktopTaskAlertMonitor(coordinator, files.Options, _ => { },
            _ => Task.FromResult(catalog), async (environment, _, token) =>
            {
                if (environment == Local)
                {
                    localEntered.TrySetResult();
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                    finally { localCanceled.TrySetResult(); }
                }
                return Result(environment, DateTimeOffset.UtcNow);
            });
        await monitor.RefreshOnceAsync();
        await localEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var now = DateTimeOffset.UtcNow;
        Apply(coordinator, Local, now);
        await Until(() => coordinator.GetSnapshot().Assignments.Any(task => task.TaskIdentity.EnvironmentId == Remote.EnvironmentId));
        catalog = new(true, false, [Local with { Enabled = false }], "Remote catalog unavailable");
        await monitor.RefreshOnceAsync();
        await localCanceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var snapshot = coordinator.GetSnapshot();
        Assert.True(snapshot.Assignments.Single(task => task.TaskIdentity.EnvironmentId == Local.EnvironmentId).SourceUnavailable);
        Assert.False(snapshot.Assignments.Single(task => task.TaskIdentity.EnvironmentId == Remote.EnvironmentId).SourceUnavailable);
        Assert.False(snapshot.T3Statuses!.Single(status => status.EnvironmentId == Local.EnvironmentId).Enabled);
        Assert.Contains(snapshot.T3Statuses!, status => status.TransportKind == "discovery");
    }

    [Fact]
    public async Task UnexpectedWorkerFailureIsVisibleAndTheNextRefreshRecoversThatSource()
    {
        using var files = new Scratch();
        await using var coordinator = files.Coordinator();
        var reads = 0;
        var log = new List<string>();
        await using var monitor = new T3DesktopTaskAlertMonitor(coordinator, files.Options, message => { lock (log) log.Add(message); },
            _ => Task.FromResult(new T3DesktopDiscoveryResult(true, true, [Local])), (environment, _, _) =>
            {
                if (Interlocked.Increment(ref reads) == 1) throw new OperationCanceledException("PRIVATE ERROR DETAIL");
                return Task.FromResult(Result(environment, DateTimeOffset.UtcNow));
            });
        await monitor.RefreshOnceAsync();
        await Until(() => coordinator.GetSnapshot().T3Statuses!.Any(status => status.ErrorClass == nameof(OperationCanceledException)));
        Assert.False(coordinator.GetSnapshot().T3Statuses!.Single().Success);
        await monitor.RefreshOnceAsync();
        await Until(() => coordinator.GetSnapshot().Assignments.Count == 1);
        Assert.True(coordinator.GetSnapshot().T3Statuses!.Single().Success);
        lock (log)
        {
            Assert.Contains(log, message => message.Contains(Local.EnvironmentId, StringComparison.Ordinal));
            Assert.DoesNotContain(log, message => message.Contains("PRIVATE ERROR DETAIL", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task RemovedStatusRemainsOnlyWhileItsAssignedTaskNeedsSourceContext()
    {
        using var files = new Scratch();
        await using var coordinator = files.Coordinator();
        coordinator.SetT3DesktopEnvironments(new(true, true, [Local, Remote]));
        Apply(coordinator, Remote, DateTimeOffset.UtcNow);
        coordinator.SetT3DesktopEnvironments(new(true, true, [Local]));
        Assert.Contains(coordinator.GetSnapshot().T3Statuses!, status => status.EnvironmentId == Remote.EnvironmentId);
        coordinator.AddSuppression(TaskAlertSuppressionScope.Task,
            TaskAlertIdentity.RemoteT3(Remote.EnvironmentId, "same-native-thread").PoolKey);
        Assert.DoesNotContain(coordinator.GetSnapshot().T3Statuses!, status => status.EnvironmentId == Remote.EnvironmentId);
        for (var index = 0; index < 100; index++)
            coordinator.SetT3DesktopEnvironments(new(true, true, [Local with { EnvironmentId = "catalog-" + index }]));
        Assert.Single(coordinator.GetSnapshot().T3Statuses!);
    }

    [Fact]
    public async Task OversizedCatalogIsAnExplicitErrorAndCannotSilentlyReplaceExistingSources()
    {
        using var files = new Scratch();
        await using var coordinator = files.Coordinator();
        coordinator.SetT3DesktopEnvironments(new(true, true, [Local]));
        coordinator.SetT3DesktopEnvironments(new(true, true,
            Enumerable.Range(0, 65).Select(index => Local with { EnvironmentId = "catalog-" + index }).ToArray()));
        Apply(coordinator, Local, DateTimeOffset.UtcNow);
        Assert.Equal(Local.EnvironmentId, Assert.Single(coordinator.GetSnapshot().Assignments).TaskIdentity.EnvironmentId);
        var statuses = coordinator.GetSnapshot().T3Statuses!;
        Assert.Equal(2, statuses.Count);
        Assert.Contains(statuses, status => status.TransportKind == "discovery" && status.Reason!.Contains("limit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ManualFallbackRequiresThePinnedEnvironmentToRemainEnabledInTheCatalog()
    {
        using var files = new Scratch();
        await using var coordinator = files.Coordinator();
        var catalog = new T3DesktopDiscoveryResult(true, true, [Remote with { SshHost = null, UnsupportedReason = "Relay unsupported", TransportKind = "relay" }]);
        var read = new TaskCompletionSource<T3DesktopEnvironment>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var monitor = new T3DesktopTaskAlertMonitor(coordinator,
            files.Options with { SshHost = "saved-alias", EnvironmentId = Remote.EnvironmentId }, _ => { },
            _ => Task.FromResult(catalog), (environment, _, _) => { read.TrySetResult(environment); return Task.FromResult(Result(environment, DateTimeOffset.UtcNow)); });
        await monitor.RefreshOnceAsync();
        var selected = await read.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("saved-alias", selected.SshHost);
        Assert.Equal("ssh", selected.TransportKind);
        Assert.Null(selected.UnsupportedReason);
        catalog = new(true, true, [Remote with { Enabled = false }]);
        await monitor.RefreshOnceAsync();
        Assert.False(coordinator.GetSnapshot().T3Statuses!.Single(status => status.EnvironmentId == Remote.EnvironmentId).Enabled);
    }

    [Fact]
    public async Task ShutdownCancelsEverySourceBeforeWaitingForGrantCleanup()
    {
        using var files = new Scratch();
        await using var coordinator = files.Coordinator();
        var localEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remoteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var localCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remoteCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = new T3DesktopTaskAlertMonitor(coordinator, files.Options, _ => { },
            _ => Task.FromResult(new T3DesktopDiscoveryResult(true, true, [Local, Remote])),
            async (environment, _, token) =>
            {
                (environment.IsLocal ? localEntered : remoteEntered).TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally
                {
                    (environment.IsLocal ? localCanceled : remoteCanceled).TrySetResult();
                    await releaseCleanup.Task;
                }
                return Result(environment, DateTimeOffset.UtcNow);
            });
        Task? stopping = null;
        try
        {
            await monitor.RefreshOnceAsync();
            await Task.WhenAll(localEntered.Task, remoteEntered.Task).WaitAsync(TimeSpan.FromSeconds(3));
            stopping = monitor.DisposeAsync().AsTask();
            await Task.WhenAll(localCanceled.Task, remoteCanceled.Task).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(stopping.IsCompleted);
        }
        finally
        {
            releaseCleanup.TrySetResult();
            await (stopping ?? monitor.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private static T3ReadResult Result(T3DesktopEnvironment environment, DateTimeOffset at) => new()
    {
        Success = true, EnvironmentId = environment.EnvironmentId, SourceEpoch = new string('A', 64), Sequence = 1,
        CapturedAt = at, IsComplete = true, TransportKind = environment.TransportKind,
        Tasks = [new() { Id = "same-native-thread", RunId = "run", Status = "running", StartedAt = at.AddSeconds(-1), ReadComplete = true }],
    };
    private static void Apply(TaskAlertCoordinator coordinator, T3DesktopEnvironment environment, DateTimeOffset at, long sequence = 1)
    {
        var result = Result(environment, at) with { Sequence = sequence };
        var source = new TaskAlertSourceIdentity(TaskAlertBackend.RemoteT3, environment.EnvironmentId);
        coordinator.ApplySourceSnapshot(new(source, 1, sequence, at, true, result.Tasks.Select(task => T3TaskAlertMonitor.Project(source, task)).ToArray()), result);
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private sealed class Scratch : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "joydex-multi-t3", Guid.NewGuid().ToString("N"));
        public T3ConnectionOptions Options { get; } = new() { Enabled = true, FollowDesktop = true };
        public TaskAlertCoordinator Coordinator() => new(Path.Combine(_root, "settings.json"), new TaskAlertPreferences(T3: Options), Path.Combine(_root, "state.json"));
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
}
