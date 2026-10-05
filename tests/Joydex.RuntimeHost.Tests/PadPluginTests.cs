using Joydex.Core.Config;
using Joydex.Core.Mapping;
using Joydex.Core.TaskAlerts;
using Joydex.RuntimeHost.Plugins;
using Joydex.WirelessPanel;
using Joydex.Windows.Actions;
using Joydex.Windows.TaskAlerts;

namespace Joydex.RuntimeHost.Tests;

public sealed class PadPluginTests
{
    [Fact]
    public async Task RepeatedRestartsRetireCompletedObserversAndShutdownJoinsTheLastOne()
    {
        var instances = new FakeInstanceFactory();
        await using var plugin = CreatePlugin(new FakeHost(), () => Configuration("test"), instances);
        await plugin.RefreshSharedConfigurationAsync(new CompanionConfig(), default);
        for (var restart = 0; restart < 64; restart++)
        {
            await plugin.RestartAsync(default);
        }
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (plugin.TrackedObserverCount > 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(1);
        }
        Assert.Equal(1, plugin.TrackedObserverCount);
        Assert.False(instances.Instances[^1].Disposed);

        await plugin.DisposeAsync();

        Assert.All(instances.Instances, instance => Assert.True(instance.Disposed));
        deadline = DateTime.UtcNow.AddSeconds(10);
        while (plugin.TrackedObserverCount != 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(1);
        }
        Assert.Equal(0, plugin.TrackedObserverCount);
    }

    [Fact]
    public void CatalogRegistersCanonicalPadAndVoiceWorker()
    {
        Assert.Collection(
            BundledPluginCatalog.Registrations,
            registration =>
            {
                Assert.Equal(Joydex.Contracts.RuntimePluginIds.Pad, registration.Id);
                Assert.Equal(BundledPluginExecutionModel.InProcess, registration.Execution);
            },
            registration =>
            {
                Assert.Equal(Joydex.Contracts.RuntimePluginIds.VoicePe, registration.Id);
                Assert.Equal(BundledPluginExecutionModel.WorkerProcess, registration.Execution);
            });
    }

    [Fact]
    public async Task TypedDispatchInspectsFullCatalogAndRejectsUnknownPlugin()
    {
        var host = new FakeHost();
        var plugin = CreatePlugin(host, () => null, new FakeInstanceFactory());
        var commands = new PadPluginCommandHandler(plugin, host.WritePadLog);
        var inspect = new Joydex.Contracts.RuntimeCommandRequest(
            Guid.NewGuid(),
            Joydex.Contracts.RuntimeCommandKind.InspectPlugins);

        var inspected = await commands.ExecuteAsync(inspect, default);
        var unknown = await commands.ExecuteAsync(
            new Joydex.Contracts.RuntimeCommandRequest(
                Guid.NewGuid(),
                Joydex.Contracts.RuntimeCommandKind.RestartPlugin,
                new Joydex.Contracts.RuntimeCommandArguments(PluginId: "other.pad")),
            default);
        var voice = await commands.ExecuteAsync(
            new Joydex.Contracts.RuntimeCommandRequest(
                Guid.NewGuid(),
                Joydex.Contracts.RuntimeCommandKind.RestartPlugin,
                new Joydex.Contracts.RuntimeCommandArguments(
                    PluginId: Joydex.Contracts.RuntimePluginIds.VoicePe)),
            default);

        Assert.Equal(inspect.OperationId, inspected.OperationId);
        Assert.Equal(inspect.Kind, inspected.Kind);
        Assert.Equal(Joydex.Contracts.RuntimeCommandStatus.Completed, inspected.Status);
        var plugins = Assert.IsType<Joydex.Contracts.RuntimePluginSnapshot>(inspected.Payload?.Plugins);
        Assert.Equal(2, plugins.Registrations.Length);
        Assert.False(plugins.Registrations.Single(item =>
            item.Id == Joydex.Contracts.RuntimePluginIds.VoicePe).ExecutionInProcess);
        Assert.Equal(Joydex.Contracts.RuntimePluginState.Disabled, plugins.Health.Single(item =>
            item.PluginId == Joydex.Contracts.RuntimePluginIds.VoicePe).State);
        Assert.Equal(Joydex.Contracts.RuntimeCommandStatus.Rejected, unknown.Status);
        Assert.NotNull(unknown.Payload?.Plugins);
        Assert.Equal(Joydex.Contracts.RuntimeCommandStatus.Rejected, voice.Status);
        Assert.Contains("existing Voice controls", voice.Detail, StringComparison.Ordinal);
        await plugin.DisposeAsync();
    }

    [Fact]
    public async Task TypedDispatchRestartsAndReloadsWithCurrentPostCommandHealth()
    {
        var host = new FakeHost();
        var instances = new FakeInstanceFactory();
        var loaded = Configuration("first");
        var plugin = CreatePlugin(host, () => loaded, instances);
        var commands = new PadPluginCommandHandler(plugin, host.WritePadLog);
        await plugin.RefreshSharedConfigurationAsync(new CompanionConfig(), default);

        var restarted = await commands.ExecuteAsync(PluginRequest(
            Joydex.Contracts.RuntimeCommandKind.RestartPlugin), default);
        loaded = Configuration("second");
        var reloaded = await commands.ExecuteAsync(PluginRequest(
            Joydex.Contracts.RuntimeCommandKind.ReloadPluginConfiguration), default);

        Assert.Equal(Joydex.Contracts.RuntimeCommandStatus.Completed, restarted.Status);
        Assert.Equal(Joydex.Contracts.RuntimePluginState.Ready, PadHealth(restarted).State);
        Assert.Equal(Joydex.Contracts.RuntimeCommandStatus.Completed, reloaded.Status);
        Assert.Equal(Joydex.Contracts.RuntimePluginState.Ready, PadHealth(reloaded).State);
        Assert.Equal(3, instances.Starts.Count);
        await plugin.DisposeAsync();
    }

    [Fact]
    public async Task TypedRejectedReloadKeepsLiveReadyGenerationAndSanitizesResult()
    {
        var host = new FakeHost();
        var instances = new FakeInstanceFactory();
        var reject = false;
        var plugin = CreatePlugin(host, () => reject
            ? throw new InvalidDataException("leaked-secret")
            : Configuration("leaked-secret"), instances);
        var commands = new PadPluginCommandHandler(plugin, host.WritePadLog);
        await plugin.RefreshSharedConfigurationAsync(new CompanionConfig(), default);
        var running = Assert.Single(instances.Instances);
        reject = true;

        var result = await commands.ExecuteAsync(PluginRequest(
            Joydex.Contracts.RuntimeCommandKind.ReloadPluginConfiguration), default);

        Assert.Equal(Joydex.Contracts.RuntimeCommandStatus.Rejected, result.Status);
        Assert.Equal(Joydex.Contracts.RuntimePluginState.Ready, PadHealth(result).State);
        Assert.False(running.Disposed);
        Assert.DoesNotContain("leaked-secret", result.Detail ?? string.Empty, StringComparison.Ordinal);
        await plugin.DisposeAsync();
    }

    [Fact]
    public async Task TypedCleanupFailureReturnsFixedSanitizedFailureAndBlockedHealth()
    {
        var host = new FakeHost();
        var instances = new FakeInstanceFactory(new FakeInstance
        {
            DisposeFailure = new IOException("leaked-secret"),
        });
        var plugin = CreatePlugin(host, () => Configuration("secret"), instances);
        var commands = new PadPluginCommandHandler(plugin, host.WritePadLog);
        await plugin.RefreshSharedConfigurationAsync(new CompanionConfig(), default);

        var result = await commands.ExecuteAsync(PluginRequest(
            Joydex.Contracts.RuntimeCommandKind.RestartPlugin), default);

        Assert.Equal(Joydex.Contracts.RuntimeCommandStatus.Failed, result.Status);
        Assert.Equal(Joydex.Contracts.RuntimePluginState.Blocked, PadHealth(result).State);
        Assert.DoesNotContain("leaked-secret", result.Detail ?? string.Empty, StringComparison.Ordinal);
        await Assert.ThrowsAsync<PadPluginCleanupException>(async () => await plugin.DisposeAsync());
    }

    [Fact]
    public async Task TypedRestartThatLosesItsGenerationReturnsFailedWithFaultedHealth()
    {
        var host = new FakeHost();
        var instances = new FakeInstanceFactory(
            new FakeInstance(),
            new IOException("simulated replacement startup failure"));
        var plugin = CreatePlugin(host, () => Configuration("secret"), instances);
        var commands = new PadPluginCommandHandler(plugin, host.WritePadLog);
        await plugin.RefreshSharedConfigurationAsync(new CompanionConfig(), default);

        var result = await commands.ExecuteAsync(PluginRequest(
            Joydex.Contracts.RuntimeCommandKind.RestartPlugin), default);

        Assert.Equal(Joydex.Contracts.RuntimeCommandStatus.Failed, result.Status);
        Assert.Equal(Joydex.Contracts.RuntimePluginState.Faulted, PadHealth(result).State);
        Assert.Equal(2, instances.Starts.Count);
        await plugin.DisposeAsync();
    }

    [Fact]
    public async Task MissingConfigurationIsDisabledAndCompanionRefreshDoesNotReloadOrRestart()
    {
        var host = new FakeHost();
        var instances = new FakeInstanceFactory();
        var loadCount = 0;
        var plugin = CreatePlugin(host, () =>
        {
            loadCount++;
            return null;
        }, instances);

        await plugin.RefreshSharedConfigurationAsync(new CompanionConfig(), default);
        await plugin.RefreshSharedConfigurationAsync(new CompanionConfig(), default);

        Assert.Equal(1, loadCount);
        Assert.Empty(instances.Starts);
        Assert.Equal(BundledPluginLifecycleState.Disabled, plugin.Health.State);
        Assert.False(plugin.Health.CanRestart);
        Assert.True(plugin.Health.CanReload);
        await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.RestartAsync(default));
        await plugin.DisposeAsync();
    }

    [Fact]
    public async Task ExplicitDisabledConfigurationStartsOnlyAfterEnabledReload()
    {
        var host = new FakeHost();
        var instances = new FakeInstanceFactory();
        var configuration = Configuration("secret", enabled: false);
        var plugin = CreatePlugin(host, () => configuration, instances);

        await plugin.RefreshSharedConfigurationAsync(new CompanionConfig(), default);

        Assert.Empty(instances.Starts);
        Assert.Equal(BundledPluginLifecycleState.Disabled, plugin.Health.State);
        configuration = Configuration("secret", enabled: true);
        await plugin.ReloadAsync(default);

        Assert.Single(instances.Starts);
        Assert.Equal(BundledPluginLifecycleState.Ready, plugin.Health.State);
        await plugin.DisposeAsync();
    }

    [Fact]
    public async Task InitialInstanceFailureIsContainedAsFaultedHealth()
    {
        var host = new FakeHost();
        var instances = new FakeInstanceFactory(
            new IOException("simulated startup failure"));
        var plugin = CreatePlugin(host, () => Configuration("secret"), instances);

        await plugin.RefreshSharedConfigurationAsync(new CompanionConfig(), default);

        Assert.Equal(BundledPluginLifecycleState.Faulted, plugin.Health.State);
        Assert.True(plugin.Health.CanRestart);
        Assert.Single(instances.Starts);
        await plugin.DisposeAsync();
    }

    [Fact]
    public async Task CompanionRefreshAtomicallyUpdatesCommandPolicyWithoutRestartingPanel()
    {
        var host = new FakeHost { PolicyName = "first" };
        var instances = new FakeInstanceFactory();
        var plugin = CreatePlugin(host, () => Configuration("secret"), instances);
        await plugin.RefreshSharedConfigurationAsync(new CompanionConfig(), default);
        var start = Assert.Single(instances.Starts);

        await InvokePolicyAsync(start);
        host.PolicyName = "second";
        await plugin.RefreshSharedConfigurationAsync(new CompanionConfig(), default);
        await InvokePolicyAsync(start);

        Assert.Single(instances.Starts);
        Assert.Equal(["first", "first", "second", "second"], host.PolicyUses);
        await plugin.DisposeAsync();
    }

    [Fact]
    public async Task UnexpectedCompletionConfirmsCleanupBeforeInjectedRetryStartsReplacement()
    {
        var host = new FakeHost();
        var first = new FakeInstance();
        var replacement = new FakeInstance();
        var instances = new FakeInstanceFactory(first, replacement);
        var restart = new ManualRestartDelay();
        var plugin = CreatePlugin(host, () => Configuration("secret"), instances, restart.DelayAsync);
        await plugin.RefreshSharedConfigurationAsync(new CompanionConfig(), default);

        first.CompleteUnexpectedly();
        await restart.Scheduled.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(first.Disposed);
        Assert.Single(instances.Starts);
        restart.Release.TrySetResult();
        await instances.WaitForStartCountAsync(2).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(BundledPluginLifecycleState.Ready, plugin.Health.State);
        await plugin.DisposeAsync();
    }

    [Fact]
    public async Task UnexpectedCompletionWithUnconfirmedCleanupBlocksWithoutRetry()
    {
        var host = new FakeHost();
        var first = new FakeInstance
        {
            DisposeFailure = new IOException("simulated cleanup failure"),
        };
        var instances = new FakeInstanceFactory(first);
        var restart = new ManualRestartDelay();
        var plugin = CreatePlugin(host, () => Configuration("secret"), instances, restart.DelayAsync);
        await plugin.RefreshSharedConfigurationAsync(new CompanionConfig(), default);

        first.CompleteUnexpectedly();
        await first.DisposeAttempted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForHealthAsync(plugin, BundledPluginLifecycleState.Blocked);

        Assert.Equal(BundledPluginLifecycleState.Blocked, plugin.Health.State);
        Assert.False(restart.Scheduled.Task.IsCompleted);
        Assert.Single(instances.Starts);
        await Assert.ThrowsAsync<PadPluginCleanupException>(async () => await plugin.DisposeAsync());
    }

    [Fact]
    public async Task UnexpectedCompletionStopsAfterThreeFailedAutomaticRestarts()
    {
        var host = new FakeHost();
        var first = new FakeInstance();
        var instances = new FakeInstanceFactory(
            first,
            new IOException("retry 1"),
            new IOException("retry 2"),
            new IOException("retry 3"));
        var restart = new ManualRestartDelay();
        restart.Release.TrySetResult();
        var plugin = CreatePlugin(host, () => Configuration("secret"), instances, restart.DelayAsync);
        await plugin.RefreshSharedConfigurationAsync(new CompanionConfig(), default);

        first.CompleteUnexpectedly();
        await instances.WaitForStartCountAsync(4).WaitAsync(TimeSpan.FromSeconds(2));
        await WaitForHealthAsync(plugin, BundledPluginLifecycleState.Faulted);

        Assert.Equal(4, instances.Starts.Count);
        Assert.Equal("PAD exhausted its automatic restart attempts.", plugin.Health.Detail);
        await plugin.DisposeAsync();
    }

    [Fact]
    public async Task TaskSnapshotsReachOnlyTheActiveSubscribedGeneration()
    {
        var host = new FakeHost();
        var instance = new FakeInstance();
        var instances = new FakeInstanceFactory(instance);
        var plugin = CreatePlugin(host, () => Configuration("secret"), instances);
        await plugin.RefreshSharedConfigurationAsync(new CompanionConfig(), default);

        host.Publish(new TaskAlertSnapshot(true, [], 0));
        Assert.Equal(1, instance.AppliedSnapshots);
        await plugin.DisposeAsync();
        host.Publish(new TaskAlertSnapshot(true, [], 0));

        Assert.Equal(1, instance.AppliedSnapshots);
    }

    [Fact]
    public async Task UnconfirmedCleanupBlocksReplacementAndIsRetainedThroughShutdown()
    {
        var host = new FakeHost();
        var first = new FakeInstance
        {
            DisposeFailure = new IOException("simulated join failure"),
        };
        var instances = new FakeInstanceFactory(first);
        var plugin = CreatePlugin(host, () => Configuration("secret"), instances);
        await plugin.RefreshSharedConfigurationAsync(new CompanionConfig(), default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.RestartAsync(default));

        Assert.Equal(BundledPluginLifecycleState.Blocked, plugin.Health.State);
        Assert.False(plugin.Health.CanRestart);
        Assert.False(plugin.Health.CanReload);
        Assert.Single(instances.Starts);
        await Assert.ThrowsAsync<PadPluginCleanupException>(async () => await plugin.DisposeAsync());
        Assert.Equal(BundledPluginLifecycleState.Blocked, plugin.Health.State);
    }

    [Fact]
    public async Task RejectedReloadPreservesRunningGenerationAndSanitizedReadyHealth()
    {
        var host = new FakeHost();
        var instances = new FakeInstanceFactory();
        Exception? loadFailure = null;
        var plugin = CreatePlugin(host, () =>
        {
            if (loadFailure is not null)
            {
                throw loadFailure;
            }
            return Configuration("prior-secret");
        }, instances);
        await plugin.RefreshSharedConfigurationAsync(new CompanionConfig(), default);
        var running = instances.Instances.Single();
        loadFailure = new InvalidDataException("candidate contained prior-secret");

        await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.ReloadAsync(default));

        Assert.False(running.Disposed);
        Assert.Single(instances.Starts);
        Assert.Equal(BundledPluginLifecycleState.Ready, plugin.Health.State);
        Assert.DoesNotContain("prior-secret", plugin.Health.Detail, StringComparison.Ordinal);
        await plugin.DisposeAsync();
    }

    [Fact]
    public async Task PasswordChangeRestartsAndFailedCandidateRestoresPriorEffectiveConfiguration()
    {
        var host = new FakeHost();
        var initial = new FakeInstance();
        var restored = new FakeInstance();
        var instances = new FakeInstanceFactory(
            initial,
            new IOException("simulated candidate startup failure"),
            restored);
        var loaded = Configuration("old-secret");
        var plugin = CreatePlugin(host, () => loaded, instances);
        await plugin.RefreshSharedConfigurationAsync(new CompanionConfig(), default);
        loaded = Configuration("new-secret");

        await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.ReloadAsync(default));

        Assert.True(initial.Disposed);
        Assert.Equal(3, instances.Starts.Count);
        Assert.Equal("old-secret", instances.Starts[0].Configuration.Password);
        Assert.Equal("new-secret", instances.Starts[1].Configuration.Password);
        Assert.Equal("old-secret", instances.Starts[2].Configuration.Password);
        Assert.Equal(BundledPluginLifecycleState.Ready, plugin.Health.State);
        Assert.All(host.Logs, message =>
        {
            Assert.DoesNotContain("old-secret", message, StringComparison.Ordinal);
            Assert.DoesNotContain("new-secret", message, StringComparison.Ordinal);
        });
        await plugin.DisposeAsync();
    }

    [Fact]
    public async Task SuccessfulNullReloadAfterInitialLoadFailureTransitionsToDisabled()
    {
        var host = new FakeHost();
        var instances = new FakeInstanceFactory();
        var firstLoad = true;
        var plugin = CreatePlugin(host, () =>
        {
            if (firstLoad)
            {
                firstLoad = false;
                throw new InvalidDataException("simulated invalid document");
            }
            return null;
        }, instances);

        await plugin.RefreshSharedConfigurationAsync(new CompanionConfig(), default);
        Assert.Equal(BundledPluginLifecycleState.Blocked, plugin.Health.State);

        await plugin.ReloadAsync(default);

        Assert.Equal(BundledPluginLifecycleState.Disabled, plugin.Health.State);
        Assert.False(plugin.Health.CanRestart);
        Assert.True(plugin.Health.CanReload);
        await plugin.DisposeAsync();
    }

    private static PadPlugin CreatePlugin(
        FakeHost host,
        Func<WirelessPanelConfiguration?> load,
        FakeInstanceFactory instances,
        Func<int, CancellationToken, Task>? restartDelay = null) => new(
        host,
        load,
        instances,
        restartDelay ?? (static (_, _) => Task.CompletedTask),
        default);

    private static Joydex.Contracts.RuntimeCommandRequest PluginRequest(
        Joydex.Contracts.RuntimeCommandKind kind) => new(
        Guid.NewGuid(),
        kind,
        new Joydex.Contracts.RuntimeCommandArguments(
            PluginId: Joydex.Contracts.RuntimePluginIds.Pad));

    private static Joydex.Contracts.RuntimePluginHealth PadHealth(
        Joydex.Contracts.RuntimeCommandResult result) =>
        Assert.Single(
            Assert.IsType<Joydex.Contracts.RuntimePluginSnapshot>(result.Payload?.Plugins).Health,
            health => health.PluginId == Joydex.Contracts.RuntimePluginIds.Pad);

    private static WirelessPanelConfiguration Configuration(
        string password,
        bool enabled = true) =>
        WirelessPanelConfiguration.Create("http://panel.local/", "user", password, enabled);

    private static async Task WaitForHealthAsync(
        PadPlugin plugin,
        BundledPluginLifecycleState state)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (plugin.Health.State == state)
            {
                return;
            }
            await Task.Delay(1);
        }
        Assert.Equal(state, plugin.Health.State);
    }

    private static async Task InvokePolicyAsync(StartCall start)
    {
        await start.Navigator.NavigateAsync(
            new TaskAlertNavigationRequest(1, 1, 1, "session"),
            default);
        await start.ExecuteAction(
            new ActionRequest(
                "test",
                CompanionConfig.AlwaysBank,
                1,
                "press",
                CodexAction.Submit,
                DateTimeOffset.UtcNow),
            default);
    }

    internal sealed class FakeHost : IPadPluginHostServices
    {
        public event EventHandler<TaskAlertSnapshot>? TaskAlertsChanged;

        public string PolicyName { get; set; } = "policy";

        public List<string> PolicyUses { get; } = [];

        public List<string> Logs { get; } = [];

        public TaskAlertSnapshot GetTaskAlertSnapshot() => new(false, [], 0);

        public bool AcknowledgeTerminalTaskAlert(int slot, string sessionId) => true;

        public PadCommandPolicy CreatePadCommandPolicy(CompanionConfig config)
        {
            var name = PolicyName;
            return new PadCommandPolicy(
                new RecordingNavigator(() => PolicyUses.Add(name)),
                (_, _) =>
                {
                    PolicyUses.Add(name);
                    return Task.FromResult(ActionExecutionResult.Success("sent"));
                });
        }

        public void WritePadLog(string message) => Logs.Add(message);

        public void Publish(TaskAlertSnapshot snapshot) => TaskAlertsChanged?.Invoke(this, snapshot);
    }

    private sealed class RecordingNavigator(Action invoked) : ITaskAlertNavigator
    {
        public Task<bool> NavigateAsync(
            TaskAlertNavigationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            invoked();
            return Task.FromResult(true);
        }
    }

    internal sealed class FakeInstanceFactory
        : IPadPluginInstanceFactory
    {
        private readonly Queue<object> _outcomes;
        private readonly object _gate = new();
        private TaskCompletionSource _startChanged =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeInstanceFactory(params object[] outcomes)
        {
            _outcomes = new Queue<object>(outcomes);
        }

        public List<StartCall> Starts { get; } = [];

        public List<FakeInstance> Instances { get; } = [];

        public Task<IPadPluginInstance> StartAsync(
            WirelessPanelConfiguration configuration,
            TaskAlertSnapshot initialSnapshot,
            Func<TaskAlertSnapshot> getSnapshot,
            ITaskAlertNavigator navigator,
            Func<int, string, bool> acknowledgeTerminal,
            Func<ActionRequest, CancellationToken, Task<ActionExecutionResult>> executeAction,
            Action<string> log,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                Starts.Add(new StartCall(configuration, navigator, executeAction));
                var started = _startChanged;
                _startChanged = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                started.TrySetResult();

                var outcome = _outcomes.Count == 0
                    ? new FakeInstance()
                    : _outcomes.Dequeue();
                if (outcome is Exception failure)
                {
                    return Task.FromException<IPadPluginInstance>(failure);
                }
                var instance = Assert.IsType<FakeInstance>(outcome);
                Instances.Add(instance);
                return Task.FromResult<IPadPluginInstance>(instance);
            }
        }

        public Task WaitForStartCountAsync(int count)
            => WaitAsync(count);

        private async Task WaitAsync(int count)
        {
            while (true)
            {
                Task signal;
                lock (_gate)
                {
                    if (Starts.Count >= count)
                    {
                        return;
                    }
                    signal = _startChanged.Task;
                }
                await signal.ConfigureAwait(false);
            }
        }
    }

    internal sealed class FakeInstance : IPadPluginInstance
    {
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Completion => _completion.Task;

        public Exception? DisposeFailure { get; init; }

        public bool Disposed { get; private set; }

        public int AppliedSnapshots { get; private set; }

        public TaskCompletionSource DisposeAttempted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Apply(TaskAlertSnapshot snapshot) => AppliedSnapshots++;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            DisposeAttempted.TrySetResult();
            if (DisposeFailure is not null)
            {
                return ValueTask.FromException(DisposeFailure);
            }
            _completion.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public void CompleteUnexpectedly() => _completion.TrySetResult();
    }

    internal sealed class ManualRestartDelay
    {
        public TaskCompletionSource Scheduled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task DelayAsync(int attempt, CancellationToken cancellationToken)
        {
            Scheduled.TrySetResult();
            return Release.Task.WaitAsync(cancellationToken);
        }
    }

    internal sealed record StartCall(
        WirelessPanelConfiguration Configuration,
        ITaskAlertNavigator Navigator,
        Func<ActionRequest, CancellationToken, Task<ActionExecutionResult>> ExecuteAction);
}
