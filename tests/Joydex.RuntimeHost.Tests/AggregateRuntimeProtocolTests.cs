using System.Collections.Concurrent;
using Joydex.Contracts;
using Joydex.Core.Runtime;
using Joydex.Core.T3;
using Joydex.Core.TaskAlerts;
using Joydex.RuntimeHost.Settings;

namespace Joydex.RuntimeHost.Tests;

public sealed class AggregateRuntimeProtocolTests
{
    private static readonly TaskAlertIdentity Remote = TaskAlertIdentity.RemoteT3("environment", "thread");

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task AttachFreshReplayAndLiveEventsHonorNegotiatedTaskCapabilities(int minor)
    {
        using var fixture = new Fixture();
        await using var engine = await fixture.StartAsync();
        fixture.Composition.PublishAlerts();
        var replaySequence = engine.EventCursor;
        var client = new Client();
        await using var session = engine.CreateSession("projection", RuntimeClientKind.Settings, client, CancellationToken.None);
        var attached = await Attach(engine, session, minor, previousEpoch: engine.EngineEpoch, afterSequence: 0);
        Assert.Equal(minor >= 4, attached.Capabilities.Contains(RuntimeProtocol.AggregateTasksCapability));
        Assert.Equal(minor >= 1, attached.Capabilities.Contains(RuntimeProtocol.RuntimeUiCapability));
        CheckSnapshot(attached.Snapshot, minor);
        CheckSnapshot(await session.GetSnapshotAsync(CancellationToken.None), minor);
        var replay = await client.EventAt(replaySequence);
        CheckEvent(replay, minor);
        fixture.Composition.PublishAlerts();
        var live = await client.EventAt(engine.EventCursor);
        CheckEvent(live, minor);
        Assert.True(live.Sequence > replay.Sequence);
    }

    [Theory]
    [InlineData(RuntimeCommandKind.TestT3Connection)]
    [InlineData(RuntimeCommandKind.SetupT3Shortcuts)]
    [InlineData(RuntimeCommandKind.RemoveT3Shortcuts)]
    public async Task LegacyPeerCannotExecuteT3CommandsButCurrentPeerCan(RuntimeCommandKind kind)
    {
        using var fixture = new Fixture();
        await using var engine = await fixture.StartAsync();
        await using var old = engine.CreateSession("old-command", RuntimeClientKind.Settings, new Client(), CancellationToken.None);
        await Attach(engine, old, 3);
        var request = new RuntimeCommandRequest(Guid.NewGuid(), kind,
            new(T3Connection: new() { SshHost = "server", EnvironmentId = "environment" }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => old.ExecuteCommandAsync(request, CancellationToken.None));
        Assert.Contains("does not support aggregate T3", error.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Composition.Commands);
        await using var current = engine.CreateSession("current-command", RuntimeClientKind.Settings, new Client(), CancellationToken.None);
        await Attach(engine, current, 4);
        var result = await current.ExecuteCommandAsync(request with { OperationId = Guid.NewGuid() }, CancellationToken.None);
        Assert.Equal(RuntimeCommandStatus.Completed, result.Status);
        Assert.Equal(kind, Assert.Single(fixture.Composition.Commands).Kind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task LegacyPeerCannotNavigateOrDismissRemoteIdentities(int shape)
    {
        using var fixture = new Fixture();
        await using var engine = await fixture.StartAsync();
        await using var session = engine.CreateSession("old-task", RuntimeClientKind.Settings, new Client(), CancellationToken.None);
        await Attach(engine, session, 3);
        var request = shape switch
        {
            0 => new RuntimeCommandRequest(Guid.NewGuid(), RuntimeCommandKind.NavigateToDesktopTask,
                new(Task: new("thread", "environment", Remote))),
            1 => new RuntimeCommandRequest(Guid.NewGuid(), RuntimeCommandKind.NavigateToDesktopTask,
                new(Task: new(Remote.PoolKey, "local"))),
            _ => new RuntimeCommandRequest(Guid.NewGuid(), RuntimeCommandKind.DismissTaskAlertQuestions,
                new(TaskAlertQuestions: new(1, Remote.PoolKey, [new string('A', 64)]))),
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteCommandAsync(request, CancellationToken.None));
        Assert.Empty(fixture.Composition.Commands);
    }

    [Fact]
    public async Task CurrentPeerCanNavigateAndDismissTypedRemoteTasks()
    {
        using var fixture = new Fixture();
        await using var engine = await fixture.StartAsync();
        await using var session = engine.CreateSession("current-task", RuntimeClientKind.Settings, new Client(), CancellationToken.None);
        await Attach(engine, session, 4);
        var navigate = await session.ExecuteCommandAsync(new(Guid.NewGuid(), RuntimeCommandKind.NavigateToDesktopTask,
            new(Task: new(Remote.NativeThreadId, Remote.EnvironmentId, Remote))), CancellationToken.None);
        var dismiss = await session.ExecuteCommandAsync(new(Guid.NewGuid(), RuntimeCommandKind.DismissTaskAlertQuestions,
            new(TaskAlertQuestions: new(1, Remote.PoolKey, [new string('A', 64)]))), CancellationToken.None);
        Assert.Equal(RuntimeCommandStatus.Completed, navigate.Status);
        Assert.Equal(RuntimeCommandStatus.Completed, dismiss.Status);
        Assert.Equal(Remote, fixture.Composition.Commands.First().Arguments!.Task!.Identity);
        Assert.Equal(2, fixture.Composition.Commands.Count);
    }

    [Fact]
    public async Task LegacyPeerCannotSmuggleNewT3ArgumentsThroughAnOlderCommand()
    {
        using var fixture = new Fixture();
        await using var engine = await fixture.StartAsync();
        await using var session = engine.CreateSession("smuggled-argument", RuntimeClientKind.Settings, new Client(), CancellationToken.None);
        await Attach(engine, session, 3);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteCommandAsync(
            new(Guid.NewGuid(), RuntimeCommandKind.InspectTaskAlertHooks,
                new(T3Connection: new() { SshHost = "server", EnvironmentId = "environment" })), CancellationToken.None));
        Assert.Empty(fixture.Composition.Commands);
    }

    [Fact]
    public async Task LegacyPeerKeepsLocalCodexNavigationAndDismissal()
    {
        using var fixture = new Fixture();
        await using var engine = await fixture.StartAsync();
        await using var session = engine.CreateSession("old-local", RuntimeClientKind.Settings, new Client(), CancellationToken.None);
        await Attach(engine, session, 3);
        var native = Guid.NewGuid().ToString("D");
        var navigate = await session.ExecuteCommandAsync(new(Guid.NewGuid(), RuntimeCommandKind.NavigateToDesktopTask,
            new(Task: new(native, "local"))), CancellationToken.None);
        var dismiss = await session.ExecuteCommandAsync(new(Guid.NewGuid(), RuntimeCommandKind.DismissTaskAlertQuestions,
            new(TaskAlertQuestions: new(1, native, [new string('A', 64)]))), CancellationToken.None);
        Assert.Equal(RuntimeCommandStatus.Completed, navigate.Status);
        Assert.Equal(RuntimeCommandStatus.Completed, dismiss.Status);
        Assert.Equal(2, fixture.Composition.Commands.Count);
    }

    [Theory]
    [InlineData(true, "server", "environment", "")]
    [InlineData(false, "server", "environment", "")]
    [InlineData(false, "", "", "saved source")]
    public async Task LegacyTaskSettingsCannotOverwriteEnabledOrSavedT3Configuration(bool enabled, string host, string environment, string display)
    {
        using var fixture = new Fixture();
        var original = new T3ConnectionOptions { Enabled = enabled, SshHost = host, EnvironmentId = environment, DisplayName = display };
        fixture.SeedT3(original);
        await using var engine = await fixture.StartAsync();
        await using var session = engine.CreateSession("legacy-settings", RuntimeClientKind.Settings, new Client(), CancellationToken.None);
        var attached = await Attach(engine, session, 3);
        var snapshot = attached.Snapshot.Settings;
        var patch = snapshot.Desired.TaskAlerts with { Bank = 3, T3 = null };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.PrepareSettingsAsync(
            new(snapshot.Revision, new(TaskAlerts: patch)), CancellationToken.None));
        Assert.Contains("cannot update Task Alerts while T3 is configured", error.Message, StringComparison.Ordinal);
        var after = await session.GetSnapshotAsync(CancellationToken.None);
        Assert.Equal(snapshot.Revision, after.Settings.Revision);
        Assert.Equal(original, after.Settings.Desired.TaskAlerts.T3);
        Assert.Equal(snapshot.Desired.TaskAlerts.Bank, after.Settings.Desired.TaskAlerts.Bank);
    }

    [Fact]
    public async Task LegacyPeerCannotIntroduceT3ConfigurationThroughSettings()
    {
        using var fixture = new Fixture();
        await using var engine = await fixture.StartAsync();
        await using var session = engine.CreateSession("old-configure", RuntimeClientKind.Settings, new Client(), CancellationToken.None);
        var attached = await Attach(engine, session, 3);
        var preferences = attached.Snapshot.Settings.Desired.TaskAlerts with
            { T3 = new() { Enabled = true, SshHost = "server", EnvironmentId = "environment" } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.PrepareSettingsAsync(
            new(attached.Snapshot.Settings.Revision, new(TaskAlerts: preferences)), CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task LegacyCodexOnlySettingsAndCurrentAggregateSettingsRemainWritable(int minor)
    {
        using var fixture = new Fixture();
        if (minor == 4) fixture.SeedT3(new() { Enabled = true, SshHost = "server", EnvironmentId = "environment" });
        await using var engine = await fixture.StartAsync();
        await using var session = engine.CreateSession("writable", RuntimeClientKind.Settings, new Client(), CancellationToken.None);
        var attached = await Attach(engine, session, minor);
        var preferences = attached.Snapshot.Settings.Desired.TaskAlerts with { Bank = 3 };
        var prepared = await session.PrepareSettingsAsync(new(attached.Snapshot.Settings.Revision, new(TaskAlerts: preferences)), CancellationToken.None);
        Assert.Equal(SettingsPrepareStatus.Prepared, prepared.Status);
        var result = await session.ApplySettingsAsync(new(Guid.NewGuid(), prepared.PreparationToken!), CancellationToken.None);
        Assert.True(result.DesiredStateCommitted);
        Assert.Equal(3, result.Snapshot.Desired.TaskAlerts.Bank);
        Assert.Equal(preferences.T3, result.Snapshot.Desired.TaskAlerts.T3);
    }

    [Fact]
    public async Task LegacyPreparationCannotOverwriteT3ConfiguredBeforeItsApply()
    {
        using var fixture = new Fixture();
        await using var engine = await fixture.StartAsync();
        await using var old = engine.CreateSession("prepared-old", RuntimeClientKind.Settings, new Client(), CancellationToken.None);
        await using var current = engine.CreateSession("configure-new", RuntimeClientKind.Settings, new Client(), CancellationToken.None);
        var oldAttached = await Attach(engine, old, 3);
        var newAttached = await Attach(engine, current, 4);
        var oldPreferences = oldAttached.Snapshot.Settings.Desired.TaskAlerts with { Bank = 3, T3 = null };
        var oldPrepared = await old.PrepareSettingsAsync(new(oldAttached.Snapshot.Settings.Revision,
            new(TaskAlerts: oldPreferences)), CancellationToken.None);
        Assert.Equal(SettingsPrepareStatus.Prepared, oldPrepared.Status);
        var t3 = new T3ConnectionOptions { Enabled = true, SshHost = "server", EnvironmentId = "environment" };
        var newPrepared = await current.PrepareSettingsAsync(new(newAttached.Snapshot.Settings.Revision,
            new(TaskAlerts: newAttached.Snapshot.Settings.Desired.TaskAlerts with { T3 = t3 })), CancellationToken.None);
        var configured = await current.ApplySettingsAsync(new(Guid.NewGuid(), newPrepared.PreparationToken!), CancellationToken.None);
        Assert.True(configured.DesiredStateCommitted);
        var stale = await old.ApplySettingsAsync(new(Guid.NewGuid(), oldPrepared.PreparationToken!), CancellationToken.None);
        Assert.Equal(SettingsApplyStatus.Conflict, stale.Status);
        Assert.False(stale.DesiredStateCommitted);
        Assert.Equal(t3, stale.Snapshot.Desired.TaskAlerts.T3);
    }

    private static void CheckSnapshot(RuntimeSnapshot snapshot, int minor)
    {
        if (minor == 0) { Assert.Null(snapshot.Ui); return; }
        CheckAlerts(snapshot.Ui!.TaskAlerts!, minor);
    }
    private static void CheckEvent(RuntimeEvent value, int minor)
    {
        if (minor == 0)
        {
            Assert.Equal(RuntimeEventKind.RuntimeIdentityChanged, value.Kind);
            Assert.NotNull(value.Identity); Assert.Null(value.TaskAlerts);
        }
        else { Assert.Equal(RuntimeEventKind.TaskAlertsChanged, value.Kind); CheckAlerts(value.TaskAlerts!, minor); }
    }
    private static void CheckAlerts(RuntimeTaskAlertSnapshot alerts, int minor)
    {
        Assert.Equal(minor >= 4 ? 2 : 1, alerts.Assignments.Length);
        Assert.Contains(alerts.Assignments, task => task.SessionId == "local-thread");
        Assert.Equal(minor >= 4, alerts.Assignments.Any(task => task.TaskIdentity.Backend == TaskAlertBackend.RemoteT3));
        Assert.Equal(minor >= 4, alerts.T3Status is not null);
    }
    private static Task<RuntimeAttachResult> Attach(RuntimeEngine engine, RuntimeRpcSession session, int minor,
        Guid? previousEpoch = null, long? afterSequence = null) => session.AttachAsync(new(RuntimeProtocol.MajorVersion,
        minor, RuntimeClientKind.Settings, RuntimeInstanceKind.Synthetic, engine.DataRootId, "consumed-by-transport",
        previousEpoch, afterSequence), CancellationToken.None);

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "joydex-protocol-tests", Guid.NewGuid().ToString("N"));
        public Composition Composition { get; private set; } = null!;
        public void SeedT3(T3ConnectionOptions value) => TaskAlertPreferencesStore.Save(
            RuntimeSettingsPaths.InDataRoot(_root).TaskAlerts, TaskAlertPreferences.Default with { T3 = value });
        public Task<RuntimeEngine> StartAsync() => RuntimeEngine.StartAsync(new(RuntimeInstanceKind.Synthetic,
            _root, Path.GetFullPath(_root), RuntimeSettingsPaths.InDataRoot(_root), new SyntheticRuntimeOwnershipLeaseFactory(),
            (host, _) => Composition = new(host), new DefaultSettingsImpactPlanner(), TimeProvider.System));
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
    private sealed class Composition(RuntimeInputHost host) : IRuntimeComposition
    {
        private readonly SyntheticRuntimeComposition _inner = new(host, "protocol");
        public ConcurrentQueue<RuntimeCommandRequest> Commands { get; } = new();
        public bool VoiceSessionActive => false;
        public event EventHandler? ActivationBoundaryAvailable { add { } remove { } }
        public event EventHandler<RuntimeUiEvent>? UiChanged;
        public RuntimeUiSnapshot GetUiSnapshot() => new(TaskAlerts: Alerts());
        public void PublishAlerts() => UiChanged?.Invoke(this, new(RuntimeEventKind.TaskAlertsChanged, taskAlerts: Alerts()));
        private static RuntimeTaskAlertSnapshot Alerts() => new(true,
            [new(1, "local-thread", "run", TaskAlertState.Running, DateTimeOffset.UtcNow),
                new(2, Remote.PoolKey, "run", TaskAlertState.Running, DateTimeOffset.UtcNow, Identity: Remote)],
            0, 2, false, [], [], TaskAlertLedOptions.CreateDefault(), new(RuntimeTaskAlertHookState.NotInstalled),
            new() { Success = true, EnvironmentId = "environment" });
        public RuntimeInputSource[] Refresh(SettingsBundle activeSettings) => _inner.Refresh(activeSettings);
        public bool ObserveForCapture(Guid captureId, string sourceId) => _inner.ObserveForCapture(captureId, sourceId);
        public void ReleaseCaptureObservation(Guid captureId) => _inner.ReleaseCaptureObservation(captureId);
        public Task<SettingsActivationResult> ActivateAsync(SettingsAggregateId aggregate, SettingsBundle candidate,
            long revision, CancellationToken token) => _inner.ActivateAsync(aggregate, candidate, revision, token);
        public Task<RuntimeCommandResult> ExecuteAsync(RuntimeCommandRequest request, CancellationToken token)
        {
            Commands.Enqueue(request);
            return Task.FromResult(new RuntimeCommandResult(request.OperationId, request.Kind, RuntimeCommandStatus.Completed));
        }
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
    private sealed class Client : IRuntimeRpcClient
    {
        private readonly ConcurrentQueue<RuntimeEvent> _events = new();
        public Task RuntimeEventAsync(RuntimeEvent value, CancellationToken token) { _events.Enqueue(value); return Task.CompletedTask; }
        public Task RuntimeInputEventAsync(RuntimeConnectionInputEvent value, CancellationToken token) => Task.CompletedTask;
        public Task RuntimeCommandCompletedAsync(RuntimeCommandResult value, CancellationToken token) => Task.CompletedTask;
        public async Task<RuntimeEvent> EventAt(long sequence)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                var value = _events.FirstOrDefault(item => item.Sequence == sequence);
                if (value is not null) return value;
                await Task.Delay(10, timeout.Token);
            }
        }
    }
}
