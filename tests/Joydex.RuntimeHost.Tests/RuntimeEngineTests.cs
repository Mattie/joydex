using System.Collections.Concurrent;
using Joydex.Contracts;
using Joydex.Core.Input;
using Joydex.Core.Runtime;
using Joydex.Core.TaskAlerts;
using Joydex.RuntimeHost.Settings;

namespace Joydex.RuntimeHost.Tests;

public sealed class RuntimeEngineTests
{
    [Fact]
    public void NewSettingsCommandPreservesThePublishedShutdownOrdinal()
    {
        Assert.Equal(27, (int)RuntimeCommandKind.ShutdownRuntime);
        Assert.Equal(28, (int)RuntimeCommandKind.OpenSettings);
    }

    [Fact]
    public async Task DisconnectReleasesCaptureWithoutRestartingSyntheticOwners()
    {
        using var scratch = new ScratchDirectory();
        var releasedCaptures = new ConcurrentQueue<Guid>();
        await using var engine = await RuntimeEngine.StartAsync(Options(
            scratch.Root,
            "disconnect",
            new DefaultSettingsImpactPlanner(),
            new ImmediateSettingsActivator(),
            releaseCaptureObservation: releasedCaptures.Enqueue));
        using var disconnected = new CancellationTokenSource();
        var firstClient = new RecordingClient();
        await using var first = engine.CreateSession(
            "connection-one",
            RuntimeClientKind.Settings,
            firstClient,
            disconnected.Token);
        var attached = await AttachAsync(engine, first, RuntimeClientKind.Settings);
        var source = Assert.Single(attached.Snapshot.Input.Sources);
        var started = await first.BeginInputCaptureAsync(
            new RuntimeCaptureRequest(source.SourceId, "test", source.Generation),
            CancellationToken.None);
        Assert.True(started.Accepted, started.Error);

        disconnected.Cancel();
        await AssertEventuallyAsync(async () =>
        {
            try
            {
                _ = await first.GetSnapshotAsync(CancellationToken.None);
                return false;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        });

        var secondClient = new RecordingClient();
        await using var second = engine.CreateSession(
            "connection-two",
            RuntimeClientKind.Settings,
            secondClient,
            CancellationToken.None);
        var reattached = await AttachAsync(engine, second, RuntimeClientKind.Settings);

        Assert.Equal(attached.Snapshot.Identity.ProcessId, reattached.Snapshot.Identity.ProcessId);
        Assert.Equal(attached.Snapshot.Identity.RuntimeGeneration, reattached.Snapshot.Identity.RuntimeGeneration);
        Assert.Equal(
            source.Generation,
            Assert.Single(reattached.Snapshot.Input.Sources).Generation);
        Assert.Empty(reattached.Snapshot.Input.Captures);
        var ended = await second.GetInputCaptureAsync(
            started.Lease!.CaptureId,
            CancellationToken.None);
        Assert.Equal(RuntimeCaptureLookupStatus.NotFound, ended.Status);
        Assert.Equal(started.Lease.CaptureId, Assert.Single(releasedCaptures));
    }

    [Fact]
    public async Task CaptureBeginAndConnectionDisposalAreSerializedWithoutLeakingObservation()
    {
        using var scratch = new ScratchDirectory();
        using var observationStarted = new ManualResetEventSlim();
        using var allowObservation = new ManualResetEventSlim();
        var releaseCount = 0;
        await using var engine = await RuntimeEngine.StartAsync(Options(
            scratch.Root,
            "capture-disposal-race",
            new DefaultSettingsImpactPlanner(),
            new ImmediateSettingsActivator(),
            releaseCaptureObservation: _ => Interlocked.Increment(ref releaseCount),
            captureObservationStarted: _ =>
            {
                observationStarted.Set();
                Assert.True(allowObservation.Wait(TimeSpan.FromSeconds(2)));
            }));
        var session = engine.CreateSession(
            "racing-connection",
            RuntimeClientKind.Settings,
            new RecordingClient(),
            CancellationToken.None);
        var attached = await AttachAsync(engine, session, RuntimeClientKind.Settings);
        var source = Assert.Single(attached.Snapshot.Input.Sources);

        var beginning = Task.Run(() => session.BeginInputCaptureAsync(
            new RuntimeCaptureRequest(source.SourceId, "race", source.Generation),
            CancellationToken.None));
        Assert.True(observationStarted.Wait(TimeSpan.FromSeconds(2)));
        var disposing = Task.Run(async () => await session.DisposeAsync());
        Assert.True(SpinWait.SpinUntil(
            () => !session.CanBeginCapture(),
            TimeSpan.FromSeconds(2)));
        Assert.False(disposing.IsCompleted);

        allowObservation.Set();
        var started = await beginning;
        Assert.True(started.Accepted, started.Error);
        await disposing;
        Assert.Equal(1, Volatile.Read(ref releaseCount));

        await using var replacement = engine.CreateSession(
            "race-replacement",
            RuntimeClientKind.Settings,
            new RecordingClient(),
            CancellationToken.None);
        var replacementAttach = await AttachAsync(engine, replacement, RuntimeClientKind.Settings);
        Assert.Empty(replacementAttach.Snapshot.Input.Captures);
    }

    [Fact]
    public async Task CaptureEventsAndRawObservationsReachOnlyTheirOwningConnection()
    {
        using var scratch = new ScratchDirectory();
        await using var engine = await RuntimeEngine.StartSyntheticAsync(scratch.Root, "scope");
        var ownerClient = new RecordingClient();
        var observerClient = new RecordingClient();
        await using var owner = engine.CreateSession(
            "capture-owner",
            RuntimeClientKind.Settings,
            ownerClient,
            CancellationToken.None);
        await using var observer = engine.CreateSession(
            "other-settings",
            RuntimeClientKind.Settings,
            observerClient,
            CancellationToken.None);
        var ownerAttach = await AttachAsync(engine, owner, RuntimeClientKind.Settings);
        _ = await AttachAsync(engine, observer, RuntimeClientKind.Settings);
        var source = Assert.Single(ownerAttach.Snapshot.Input.Sources);

        var started = await owner.BeginInputCaptureAsync(
            new RuntimeCaptureRequest(source.SourceId, "binding", source.Generation),
            CancellationToken.None);
        Assert.True(started.Accepted, started.Error);
        await engine.PublishSyntheticInputAsync(Snapshot(buttonPressed: false), []);
        await engine.PublishSyntheticInputAsync(
            Snapshot(buttonPressed: true),
            [new JoystickEvent(JoystickEventKind.ButtonPressed, 0, 1)]);

        var completed = await ownerClient.CaptureCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(started.Lease!.CaptureId, completed.Capture!.Lease.CaptureId);
        Assert.Equal(InputCaptureStatus.Completed, completed.Capture.Lease.Status);
        Assert.Equal(0, completed.Capture.CapturedInput?.ControlIndex);
        Assert.Contains(
            ownerClient.InputEvents,
            item => item.Kind == RuntimeConnectionInputEventKind.InputObserved);
        Assert.Empty(observerClient.InputEvents);

        var ownerLookup = await owner.GetInputCaptureAsync(
            started.Lease.CaptureId,
            CancellationToken.None);
        Assert.Equal(RuntimeCaptureLookupStatus.Completed, ownerLookup.Status);
        var otherLookup = await observer.GetInputCaptureAsync(
            started.Lease.CaptureId,
            CancellationToken.None);
        Assert.Equal(RuntimeCaptureLookupStatus.NotFound, otherLookup.Status);
    }

    [Fact]
    public async Task FreshEngineReconcilesPreviouslyPendingDesiredVoiceSettings()
    {
        using var scratch = new ScratchDirectory();
        var firstActivator = new RecordingActivator();
        var firstOptions = Options(
            scratch.Root,
            "first",
            new VoicePendingPlanner(),
            firstActivator);
        await using (var firstEngine = await RuntimeEngine.StartAsync(firstOptions))
        {
            Assert.Equal(
                [
                    SettingsAggregateId.TaskAlerts,
                    SettingsAggregateId.Companion,
                    SettingsAggregateId.Voice,
                    SettingsAggregateId.PebbleIndex,
                ],
                firstActivator.Aggregates);
            var client = new RecordingClient();
            await using var session = firstEngine.CreateSession(
                "settings-one",
                RuntimeClientKind.Settings,
                client,
                CancellationToken.None);
            var attached = await AttachAsync(firstEngine, session, RuntimeClientKind.Settings);
            var prepared = await session.PrepareSettingsAsync(
                new PrepareSettingsRequest(
                    attached.Snapshot.Settings.Revision,
                    new SettingsPatch(Voice: attached.Snapshot.Settings.Desired.Voice with
                    {
                        PinnedTaskLabel = "pending voice",
                    })),
                CancellationToken.None);
            Assert.Equal(SettingsPrepareStatus.Prepared, prepared.Status);
            var applied = await session.ApplySettingsAsync(
                new ApplySettingsRequest(Guid.NewGuid(), prepared.PreparationToken!),
                CancellationToken.None);
            Assert.Equal(SettingsApplyStatus.PendingIdle, applied.Status);
            Assert.NotEqual(
                applied.Snapshot.Desired.Voice.PinnedTaskLabel,
                applied.Snapshot.Active.Voice.PinnedTaskLabel);
        }

        var secondActivator = new RecordingActivator();
        await using var secondEngine = await RuntimeEngine.StartAsync(Options(
            scratch.Root,
            "second",
            new DefaultSettingsImpactPlanner(),
            secondActivator));
        var secondClient = new RecordingClient();
        await using var second = secondEngine.CreateSession(
            "settings-two",
            RuntimeClientKind.Settings,
            secondClient,
            CancellationToken.None);
        var restarted = await AttachAsync(secondEngine, second, RuntimeClientKind.Settings);
        var voice = Assert.Single(
            restarted.Snapshot.Settings.Aggregates,
            item => item.Aggregate == SettingsAggregateId.Voice);

        Assert.Contains(SettingsAggregateId.Voice, secondActivator.Aggregates);
        Assert.Equal(SettingsActivationState.Applied, voice.Activation);
        Assert.Equal(voice.DesiredRevision, voice.ActiveRevision);
        Assert.Equal("pending voice", restarted.Snapshot.Settings.Active.Voice.PinnedTaskLabel);
    }

    [Fact]
    public async Task NonSensitiveCommandResultSurvivesReconnectButPebbleAccessDoesNot()
    {
        using var scratch = new ScratchDirectory();
        await using var engine = await RuntimeEngine.StartSyntheticAsync(scratch.Root, "commands");
        var firstClient = new RecordingClient();
        var first = engine.CreateSession(
            "command-one",
            RuntimeClientKind.Settings,
            firstClient,
            CancellationToken.None);
        _ = await AttachAsync(engine, first, RuntimeClientKind.Settings);
        var listId = Guid.NewGuid();
        var listRequest = new RuntimeCommandRequest(listId, RuntimeCommandKind.ListVoiceProjects);
        var listed = await first.ExecuteCommandAsync(listRequest, CancellationToken.None);
        Assert.Equal(RuntimeCommandStatus.Completed, listed.Status);
        var secretId = Guid.NewGuid();
        _ = await first.ExecuteCommandAsync(
            new RuntimeCommandRequest(secretId, RuntimeCommandKind.ReadPebbleIndexAccess),
            CancellationToken.None);
        await first.DisposeAsync();

        var secondClient = new RecordingClient();
        await using var second = engine.CreateSession(
            "command-two",
            RuntimeClientKind.Settings,
            secondClient,
            CancellationToken.None);
        _ = await AttachAsync(engine, second, RuntimeClientKind.Settings);
        var recovered = await second.GetCommandOperationAsync(listId, CancellationToken.None);
        var hidden = await second.GetCommandOperationAsync(secretId, CancellationToken.None);

        Assert.Equal(RuntimeCommandOperationState.Completed, recovered.State);
        Assert.Equal(RuntimeCommandOperationState.NotFound, hidden.State);
        Assert.Equal(listed, await second.ExecuteCommandAsync(listRequest, CancellationToken.None));
        var mismatch = await second.ExecuteCommandAsync(
            listRequest with
            {
                Arguments = new RuntimeCommandArguments(
                    CodexAppServerPath: Path.Combine(scratch.Root, "different.exe")),
            },
            CancellationToken.None);
        Assert.Equal(RuntimeCommandStatus.Rejected, mismatch.Status);
    }

    [Fact]
    public async Task EngineOwnedReloadAdoptsExternalSettingsAndReplaysWithoutForwarding()
    {
        using var scratch = new ScratchDirectory();
        var forwarded = new RecordingCommandHandler();
        await using var engine = await RuntimeEngine.StartAsync(Options(
            scratch.Root,
            "authority-reload",
            new DefaultSettingsImpactPlanner(),
            new ImmediateSettingsActivator(),
            forwarded));
        await using var session = engine.CreateSession(
            "authority-reload-client",
            RuntimeClientKind.Settings,
            new RecordingClient(),
            CancellationToken.None);
        _ = await AttachAsync(engine, session, RuntimeClientKind.Settings);
        var paths = RuntimeSettingsPaths.InDataRoot(scratch.Root);
        var taskAlerts = TaskAlertPreferencesStore.LoadOrCreate(paths.TaskAlerts);
        TaskAlertPreferencesStore.Save(paths.TaskAlerts, taskAlerts with { Bank = 4 });
        var operationId = Guid.NewGuid();
        var request = new RuntimeCommandRequest(
            operationId,
            RuntimeCommandKind.ReloadConfiguration);

        var adopted = await session.ExecuteCommandAsync(request, CancellationToken.None);
        TaskAlertPreferencesStore.Save(paths.TaskAlerts, taskAlerts with { Bank = 5 });
        var replayed = await session.ExecuteCommandAsync(request, CancellationToken.None);
        var current = await session.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(RuntimeCommandStatus.Completed, adopted.Status);
        Assert.Equal(4, adopted.Payload!.Settings!.Desired.TaskAlerts.Bank);
        Assert.Equal(adopted, replayed);
        Assert.Equal(4, current.Settings.Desired.TaskAlerts.Bank);
        Assert.Contains(
            current.Settings.ExternalCandidates,
            candidate => candidate.Aggregate == SettingsAggregateId.TaskAlerts);
        Assert.Empty(forwarded.Requests);
    }

    [Fact]
    public async Task AdoptExternalSettingsIsSelectedAndInvalidReloadIsAllOrNothing()
    {
        using var scratch = new ScratchDirectory();
        var forwarded = new RecordingCommandHandler();
        await using var engine = await RuntimeEngine.StartAsync(Options(
            scratch.Root,
            "authority-selected",
            new DefaultSettingsImpactPlanner(),
            new ImmediateSettingsActivator(),
            forwarded));
        await using var session = engine.CreateSession(
            "authority-selected-client",
            RuntimeClientKind.Settings,
            new RecordingClient(),
            CancellationToken.None);
        var attached = await AttachAsync(engine, session, RuntimeClientKind.Settings);
        var paths = RuntimeSettingsPaths.InDataRoot(scratch.Root);
        var taskAlerts = TaskAlertPreferencesStore.LoadOrCreate(paths.TaskAlerts);
        TaskAlertPreferencesStore.Save(paths.TaskAlerts, taskAlerts with { Bank = 4 });
        File.WriteAllText(paths.Voice, "{ invalid voice settings");

        var rejected = await session.ExecuteCommandAsync(
            new RuntimeCommandRequest(Guid.NewGuid(), RuntimeCommandKind.ReloadConfiguration),
            CancellationToken.None);
        var selected = await session.ExecuteCommandAsync(
            new RuntimeCommandRequest(
                Guid.NewGuid(),
                RuntimeCommandKind.AdoptExternalSettings,
                new RuntimeCommandArguments(
                    ExternalSettingsAggregates: [SettingsAggregateId.TaskAlerts])),
            CancellationToken.None);

        Assert.Equal(RuntimeCommandStatus.Rejected, rejected.Status);
        Assert.Equal(
            attached.Snapshot.Settings.Desired.TaskAlerts.Bank,
            rejected.Payload!.Settings!.Desired.TaskAlerts.Bank);
        Assert.Equal(RuntimeCommandStatus.Completed, selected.Status);
        Assert.Equal(4, selected.Payload!.Settings!.Desired.TaskAlerts.Bank);
        var remaining = Assert.Single(selected.Payload.Settings.ExternalCandidates);
        Assert.Equal(SettingsAggregateId.Voice, remaining.Aggregate);
        Assert.False(remaining.IsValid);
        Assert.Empty(forwarded.Requests);
    }

    [Fact]
    public async Task OnlyTrayShutdownReturnsAcceptedBeforeSignalingProcessLifetime()
    {
        using var scratch = new ScratchDirectory();
        var forwarded = new RecordingCommandHandler();
        await using var engine = await RuntimeEngine.StartAsync(Options(
            scratch.Root,
            "authority-shutdown",
            new DefaultSettingsImpactPlanner(),
            new ImmediateSettingsActivator(),
            forwarded));
        await using var settings = engine.CreateSession(
            "authority-settings-client",
            RuntimeClientKind.Settings,
            new RecordingClient(),
            CancellationToken.None);
        _ = await AttachAsync(engine, settings, RuntimeClientKind.Settings);
        await using var tray = engine.CreateSession(
            "authority-tray-client",
            RuntimeClientKind.Tray,
            new RecordingClient(),
            CancellationToken.None);
        _ = await AttachAsync(engine, tray, RuntimeClientKind.Tray);
        var request = new RuntimeCommandRequest(
            Guid.NewGuid(),
            RuntimeCommandKind.ShutdownRuntime);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            settings.ExecuteCommandAsync(request, CancellationToken.None));
        Assert.False(engine.ShutdownRequested.IsCompleted);

        var accepted = await tray.ExecuteCommandAsync(request, CancellationToken.None);

        Assert.Equal(RuntimeCommandStatus.Completed, accepted.Status);
        await engine.ShutdownRequested.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(forwarded.Requests);
    }

    [Fact]
    public async Task OnlyTrayCanOpenTheHostOwnedSettingsProcess()
    {
        using var scratch = new ScratchDirectory();
        var launcher = new RecordingSettingsProcessLauncher();
        await using var engine = await RuntimeEngine.StartAsync(Options(
            scratch.Root,
            "authority-open-settings",
            new DefaultSettingsImpactPlanner(),
            new ImmediateSettingsActivator(),
            settingsProcessLauncher: launcher));
        await using var settings = engine.CreateSession(
            "open-settings-client",
            RuntimeClientKind.Settings,
            new RecordingClient(),
            CancellationToken.None);
        _ = await AttachAsync(engine, settings, RuntimeClientKind.Settings);
        await using var tray = engine.CreateSession(
            "open-settings-tray",
            RuntimeClientKind.Tray,
            new RecordingClient(),
            CancellationToken.None);
        _ = await AttachAsync(engine, tray, RuntimeClientKind.Tray);
        var request = new RuntimeCommandRequest(
            Guid.NewGuid(),
            RuntimeCommandKind.OpenSettings);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            settings.ExecuteCommandAsync(request, CancellationToken.None));
        var opened = await tray.ExecuteCommandAsync(request, CancellationToken.None);

        Assert.Equal(RuntimeCommandStatus.Completed, opened.Status);
        Assert.Equal([request], launcher.Requests);
    }

    [Fact]
    public async Task SettingsLifecycleNotificationsBracketSuccessfulAttachAndConnectionCleanup()
    {
        using var scratch = new ScratchDirectory();
        var launcher = new RecordingSettingsProcessLauncher();
        await using var engine = await RuntimeEngine.StartAsync(Options(
            scratch.Root,
            "settings-lifecycle",
            new DefaultSettingsImpactPlanner(),
            new ImmediateSettingsActivator(),
            settingsProcessLauncher: launcher));
        var session = engine.CreateSession(
            "original-settings-connection",
            RuntimeClientKind.Settings,
            new RecordingClient(),
            CancellationToken.None);

        _ = await AttachAsync(engine, session, RuntimeClientKind.Settings);

        Assert.Equal(
            [("attached", "original-settings-connection")],
            launcher.LifecycleNotifications);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AttachAsync(engine, session, RuntimeClientKind.Settings));
        Assert.Single(launcher.LifecycleNotifications);

        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.Equal(
            [
                ("attached", "original-settings-connection"),
                ("disconnected", "original-settings-connection"),
            ],
            launcher.LifecycleNotifications);

        await using var replacement = engine.CreateSession(
            "original-settings-connection",
            RuntimeClientKind.Settings,
            new RecordingClient(),
            CancellationToken.None);
        _ = await AttachAsync(engine, replacement, RuntimeClientKind.Settings);
    }

    [Fact]
    public async Task FailedIncompleteAndNonSettingsAttachNeverNotifySettingsLifecycle()
    {
        using var scratch = new ScratchDirectory();
        var launcher = new RecordingSettingsProcessLauncher();
        await using var engine = await RuntimeEngine.StartAsync(Options(
            scratch.Root,
            "settings-lifecycle-rejections",
            new DefaultSettingsImpactPlanner(),
            new ImmediateSettingsActivator(),
            settingsProcessLauncher: launcher));

        var incomplete = engine.CreateSession(
            "incomplete-settings-connection",
            RuntimeClientKind.Settings,
            new RecordingClient(),
            CancellationToken.None);
        await incomplete.DisposeAsync();

        var unauthorized = engine.CreateSession(
            "unauthorized-settings-connection",
            RuntimeClientKind.Settings,
            new RecordingClient(),
            CancellationToken.None);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => unauthorized.AttachAsync(
            new RuntimeAttachRequest(
                RuntimeProtocol.MajorVersion,
                RuntimeProtocol.MinorVersion,
                RuntimeClientKind.Tray,
                RuntimeInstanceKind.Synthetic,
                engine.DataRootId,
                "consumed-by-transport"),
            CancellationToken.None));
        await unauthorized.DisposeAsync();

        var failed = engine.CreateSession(
            "failed-settings-connection",
            RuntimeClientKind.Settings,
            new RecordingClient(),
            CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failed.AttachAsync(
            new RuntimeAttachRequest(
                RuntimeProtocol.MajorVersion + 1,
                RuntimeProtocol.MinorVersion,
                RuntimeClientKind.Settings,
                RuntimeInstanceKind.Synthetic,
                engine.DataRootId,
                "consumed-by-transport"),
            CancellationToken.None));
        await failed.DisposeAsync();

        var tray = engine.CreateSession(
            "attached-tray-connection",
            RuntimeClientKind.Tray,
            new RecordingClient(),
            CancellationToken.None);
        _ = await AttachAsync(engine, tray, RuntimeClientKind.Tray);
        await tray.DisposeAsync();

        Assert.Empty(launcher.LifecycleNotifications);
    }

    [Fact]
    public async Task SnapshotCursorsStayBehindMutationsThatCompleteDuringTheStateRead()
    {
        using var scratch = new ScratchDirectory();
        var activator = new BlockingActivator();
        await using var engine = await RuntimeEngine.StartAsync(Options(
            scratch.Root,
            "snapshot-boundary",
            new DefaultSettingsImpactPlanner(),
            activator));
        var callbacks = new RecordingClient();
        await using var session = engine.CreateSession(
            "snapshot-boundary-client",
            RuntimeClientKind.Settings,
            callbacks,
            CancellationToken.None);
        var attached = await AttachAsync(engine, session, RuntimeClientKind.Settings);
        var prepared = await session.PrepareSettingsAsync(
            new PrepareSettingsRequest(
                attached.Snapshot.Settings.Revision,
                new SettingsPatch(
                    TaskAlerts: attached.Snapshot.Settings.Desired.TaskAlerts with { Bank = 3 })),
            CancellationToken.None);
        activator.Arm();
        var operationId = Guid.NewGuid();
        var apply = session.ApplySettingsAsync(
            new ApplySettingsRequest(operationId, prepared.PreparationToken!),
            CancellationToken.None);
        await activator.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var snapshotRead = session.GetSnapshotAsync(CancellationToken.None);
        var source = Assert.Single(attached.Snapshot.Input.Sources);
        var capture = await session.BeginInputCaptureAsync(
            new RuntimeCaptureRequest(source.SourceId, "snapshot boundary", source.Generation),
            CancellationToken.None);
        Assert.True(capture.Accepted, capture.Error);
        activator.Release.TrySetResult();

        var applied = await apply;
        var snapshot = await snapshotRead;
        Assert.Equal(3, snapshot.Settings.Desired.TaskAlerts.Bank);
        Assert.Contains(snapshot.Input.Captures, item => item.CaptureId == capture.Lease!.CaptureId);
        await AssertEventuallyAsync(() => Task.FromResult(
            callbacks.RuntimeEvents.Any(item => item.Operation?.OperationId == operationId)
            && callbacks.InputEvents.Any(item =>
                item.Capture?.Lease.CaptureId == capture.Lease!.CaptureId)));
        Assert.All(
            callbacks.RuntimeEvents.Where(item => item.Operation?.OperationId == operationId),
            item => Assert.True(item.Sequence > snapshot.EventCursor));
        Assert.All(
            callbacks.InputEvents.Where(item => item.Capture?.Lease.CaptureId == capture.Lease!.CaptureId),
            item => Assert.True(item.Sequence > snapshot.InputEventCursor));
        Assert.Equal(SettingsApplyStatus.Applied, applied.Status);
    }

    [Fact]
    public async Task AcceptedBackgroundCommandsRemainBoundedWhenCallersStopWaiting()
    {
        using var scratch = new ScratchDirectory();
        var handler = new BlockingCommandHandler();
        await using var engine = await RuntimeEngine.StartAsync(Options(
            scratch.Root,
            "bounded-commands",
            new DefaultSettingsImpactPlanner(),
            new ImmediateSettingsActivator(),
            handler));
        var client = new RecordingClient();
        await using var session = engine.CreateSession(
            "bounded-command-client",
            RuntimeClientKind.Settings,
            client,
            CancellationToken.None);
        _ = await AttachAsync(engine, session, RuntimeClientKind.Settings);

        var acceptedIds = new List<Guid>();
        for (var index = 0; index < RuntimeCommandExecutor.MaximumRunningOperations; index++)
        {
            using var caller = new CancellationTokenSource();
            var operationId = Guid.NewGuid();
            acceptedIds.Add(operationId);
            var waiting = session.ExecuteCommandAsync(
                new RuntimeCommandRequest(operationId, RuntimeCommandKind.ListVoiceProjects),
                caller.Token);
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }

        Assert.Equal(RuntimeCommandExecutor.MaximumRunningOperations, handler.Started);
        var rejected = await session.ExecuteCommandAsync(
            new RuntimeCommandRequest(Guid.NewGuid(), RuntimeCommandKind.ListVoiceProjects),
            CancellationToken.None);
        Assert.Equal(RuntimeCommandStatus.Rejected, rejected.Status);
        Assert.Equal(RuntimeCommandExecutor.MaximumRunningOperations, handler.Started);

        handler.Release();
        await AssertEventuallyAsync(async () =>
        {
            foreach (var operationId in acceptedIds)
            {
                if ((await session.GetCommandOperationAsync(operationId, CancellationToken.None)).State
                    != RuntimeCommandOperationState.Completed)
                {
                    return false;
                }
            }
            return true;
        });
    }

    [Fact]
    public async Task DirectSessionNegotiatesANewerMinorToTheCurrentRuntimeMinor()
    {
        using var scratch = new ScratchDirectory();
        await using var engine = await RuntimeEngine.StartSyntheticAsync(scratch.Root, "minor-version");
        var client = new RecordingClient();
        await using var session = engine.CreateSession(
            "newer-minor",
            RuntimeClientKind.HeadlessTest,
            client,
            CancellationToken.None);

        var attached = await session.AttachAsync(
            new RuntimeAttachRequest(
                RuntimeProtocol.MajorVersion,
                RuntimeProtocol.MinorVersion + 7,
                RuntimeClientKind.HeadlessTest,
                RuntimeInstanceKind.Synthetic,
                engine.DataRootId,
                "consumed-by-transport"),
            CancellationToken.None);

        Assert.Equal(RuntimeProtocol.MinorVersion, attached.ProtocolMinor);
        Assert.Contains(RuntimeProtocol.SettingsTransferCapability, attached.Capabilities);
    }

    [Fact]
    public void MinorTwoCapabilitiesAreGated()
    {
        Assert.DoesNotContain(
            RuntimeProtocol.SettingsTransferCapability,
            RuntimeProtocol.CapabilitiesForMinor(1));
        Assert.Contains(
            RuntimeProtocol.RuntimeUiCapability,
            RuntimeProtocol.CapabilitiesForMinor(1));
        Assert.Contains(
            RuntimeProtocol.SettingsTransferCapability,
            RuntimeProtocol.CapabilitiesForMinor(2));
        Assert.DoesNotContain(
            RuntimeProtocol.ReliableCursorsCapability,
            RuntimeProtocol.CapabilitiesForMinor(1));
        Assert.Contains(
            RuntimeProtocol.ReliableCursorsCapability,
            RuntimeProtocol.CapabilitiesForMinor(2));
    }

    [Fact]
    public async Task OlderMinorReceivesCursorBearingCompatibilityEvents()
    {
        using var scratch = new ScratchDirectory();
        TestRuntimeComposition? composition = null;
        await using var engine = await RuntimeEngine.StartAsync(Options(
            scratch.Root,
            "older-minor",
            new DefaultSettingsImpactPlanner(),
            new ImmediateSettingsActivator(),
            compositionCreated: value => composition = value));
        var client = new RecordingClient();
        await using var session = engine.CreateSession(
            "older-minor-client",
            RuntimeClientKind.Settings,
            client,
            CancellationToken.None);

        var attached = await session.AttachAsync(
            new RuntimeAttachRequest(
                RuntimeProtocol.MajorVersion,
                0,
                RuntimeClientKind.Settings,
                RuntimeInstanceKind.Synthetic,
                engine.DataRootId,
                "consumed-by-transport"),
            CancellationToken.None);
        Assert.Equal(0, attached.ProtocolMinor);
        Assert.DoesNotContain(RuntimeProtocol.RuntimeUiCapability, attached.Capabilities);
        Assert.Null(attached.Snapshot.Ui);

        composition!.PublishUi(new RuntimeUiEvent(
            RuntimeEventKind.PromptPickerChanged,
            promptPicker: new RuntimePromptPickerSnapshot(
                true,
                "picker",
                0)));
        await AssertEventuallyAsync(() => Task.FromResult(client.RuntimeEvents.Count == 1));
        var compatibilityEvent = Assert.Single(client.RuntimeEvents);
        Assert.Equal(RuntimeEventKind.RuntimeIdentityChanged, compatibilityEvent.Kind);
        Assert.NotNull(compatibilityEvent.Identity);
        Assert.True(compatibilityEvent.Sequence > attached.Snapshot.EventCursor);
        Assert.Null((await session.GetSnapshotAsync(CancellationToken.None)).Ui);
    }

    [Fact]
    public async Task InputDispatcherCoalescesObservationsAndKeepsCaptureCompletion()
    {
        var client = new BlockingInputClient();
        await using var dispatcher = new RuntimeConnectionDispatcher(client, Guid.NewGuid());
        dispatcher.EnqueueObservation(Observation(0));
        await client.FirstCallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for (var sequence = 1; sequence < 100; sequence++)
        {
            dispatcher.EnqueueObservation(Observation(sequence));
        }
        var capture = new RuntimeCaptureUpdate(new RuntimeCaptureLease(
            Guid.NewGuid(),
            "source",
            "binding",
            1,
            DateTimeOffset.UtcNow.AddSeconds(15),
            InputCaptureStatus.Completed,
            3));
        dispatcher.EnqueueCapture(capture);
        client.ReleaseFirstCallback.TrySetResult();

        await client.CaptureCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var delivered = client.Events.ToArray();
        Assert.InRange(
            delivered.Count(item => item.Kind == RuntimeConnectionInputEventKind.InputObserved),
            1,
            2);
        Assert.Contains(
            delivered,
            item => item.Observation?.Sequence == 99);
        Assert.Contains(
            delivered,
            item => item.Capture?.Lease.CaptureId == capture.Lease.CaptureId);
        Assert.Equal(
            delivered.Select(item => item.Sequence).Order().ToArray(),
            delivered.Select(item => item.Sequence).ToArray());
    }

    [Fact]
    public async Task SnapshotCursorDoesNotAcknowledgeAnInFlightInputCallback()
    {
        using var scratch = new ScratchDirectory();
        await using var engine = await RuntimeEngine.StartSyntheticAsync(scratch.Root, "input-cursor-ack");
        var client = new BlockingInputClient();
        await using var session = engine.CreateSession(
            "input-cursor-client",
            RuntimeClientKind.Settings,
            client,
            CancellationToken.None);
        var attached = await AttachAsync(engine, session, RuntimeClientKind.Settings);
        var source = Assert.Single(attached.Snapshot.Input.Sources);

        var capture = await session.BeginInputCaptureAsync(
            new RuntimeCaptureRequest(source.SourceId, "cursor acknowledgement", source.Generation),
            CancellationToken.None);
        Assert.True(capture.Accepted, capture.Error);
        await client.FirstCallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var whileBlocked = await session.GetSnapshotAsync(CancellationToken.None);
        Assert.Equal(0, whileBlocked.InputEventCursor);
        Assert.Contains(
            whileBlocked.Input.Captures,
            item => item.CaptureId == capture.Lease!.CaptureId);

        client.ReleaseFirstCallback.TrySetResult();
        await AssertEventuallyAsync(() => Task.FromResult(client.Events.Count > 0));
        var delivered = Assert.Single(client.Events);
        Assert.Equal(1, delivered.Sequence);
        await AssertEventuallyAsync(async () =>
            (await session.GetSnapshotAsync(CancellationToken.None)).InputEventCursor == 1);
    }

    [Fact]
    public async Task CallbackOverflowAbortsConnectionAndReleasesItsCapture()
    {
        using var scratch = new ScratchDirectory();
        await using var engine = await RuntimeEngine.StartSyntheticAsync(scratch.Root, "callback-overflow");
        using var disconnected = new CancellationTokenSource();
        var client = new BlockingInputClient();
        var aborted = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = engine.CreateSession(
            "overflowing-client",
            RuntimeClientKind.Settings,
            client,
            disconnected.Token,
            failure =>
            {
                aborted.TrySetResult(failure);
                disconnected.Cancel();
            });
        var attached = await AttachAsync(engine, session, RuntimeClientKind.Settings);
        var source = Assert.Single(attached.Snapshot.Input.Sources);
        var capture = await session.BeginInputCaptureAsync(
            new RuntimeCaptureRequest(source.SourceId, "overflow", source.Generation),
            CancellationToken.None);
        Assert.True(capture.Accepted, capture.Error);
        await client.FirstCallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        for (var index = 0; index <= RuntimeConnectionDispatcher.MaximumQueuedItems; index++)
        {
            _ = await session.ExecuteCommandAsync(
                new RuntimeCommandRequest(Guid.NewGuid(), RuntimeCommandKind.ListVoiceProjects),
                CancellationToken.None);
        }

        var failure = await aborted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsType<InvalidOperationException>(failure);
        await AssertEventuallyAsync(async () =>
        {
            try
            {
                _ = await session.GetSnapshotAsync(CancellationToken.None);
                return false;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        });

        var replacementClient = new RecordingClient();
        await using var replacement = engine.CreateSession(
            "replacement-client",
            RuntimeClientKind.Settings,
            replacementClient,
            CancellationToken.None);
        var replacementAttach = await AttachAsync(engine, replacement, RuntimeClientKind.Settings);
        Assert.Equal(engine.RuntimeGeneration, replacementAttach.Snapshot.Identity.RuntimeGeneration);
        Assert.Empty(replacementAttach.Snapshot.Input.Captures);
        Assert.Equal(
            RuntimeCaptureLookupStatus.NotFound,
            (await replacement.GetInputCaptureAsync(
                capture.Lease!.CaptureId,
                CancellationToken.None)).Status);
    }

    [Fact]
    public async Task CallbackFailureAbortsConnectionAndReleasesItsCapture()
    {
        using var scratch = new ScratchDirectory();
        var releaseCount = 0;
        await using var engine = await RuntimeEngine.StartAsync(Options(
            scratch.Root,
            "callback-failure",
            new DefaultSettingsImpactPlanner(),
            new ImmediateSettingsActivator(),
            releaseCaptureObservation: _ => Interlocked.Increment(ref releaseCount)));
        using var disconnected = new CancellationTokenSource();
        var aborted = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = engine.CreateSession(
            "failing-client",
            RuntimeClientKind.Settings,
            new FailingInputClient(),
            disconnected.Token,
            failure =>
            {
                aborted.TrySetResult(failure);
                disconnected.Cancel();
            });
        var attached = await AttachAsync(engine, session, RuntimeClientKind.Settings);
        var source = Assert.Single(attached.Snapshot.Input.Sources);
        var capture = await session.BeginInputCaptureAsync(
            new RuntimeCaptureRequest(source.SourceId, "failure", source.Generation),
            CancellationToken.None);
        Assert.True(capture.Accepted, capture.Error);

        var failure = await aborted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsType<InvalidOperationException>(failure);

        await using var replacement = engine.CreateSession(
            "callback-failure-replacement",
            RuntimeClientKind.Settings,
            new RecordingClient(),
            CancellationToken.None);
        var replacementAttach = await AttachAsync(engine, replacement, RuntimeClientKind.Settings);
        Assert.Empty(replacementAttach.Snapshot.Input.Captures);
        Assert.Equal(
            RuntimeCaptureLookupStatus.NotFound,
            (await replacement.GetInputCaptureAsync(
                capture.Lease!.CaptureId,
                CancellationToken.None)).Status);
        Assert.Equal(1, Volatile.Read(ref releaseCount));
    }

    [Fact]
    public async Task OwnershipFailureOccursBeforeCreatingTheDataRoot()
    {
        using var scratch = new ScratchDirectory(create: false);
        var options = new RuntimeEngineOptions(
            RuntimeInstanceKind.Production,
            scratch.Root,
            "production-root",
            RuntimeSettingsPaths.InDataRoot(scratch.Root),
            new FailingOwnershipLeaseFactory(),
            (host, _) => new TestRuntimeComposition(
                host,
                "must-not-start",
                new ImmediateSettingsActivator()),
            new DefaultSettingsImpactPlanner(),
            TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(() => RuntimeEngine.StartAsync(options));
        Assert.False(Directory.Exists(scratch.Root));
    }

    [Fact]
    public async Task LegacyOwnershipLeaseReleasesItsInjectedMutexWithoutTouchingDisposedReadinessState()
    {
        var mutexName = $@"Local\Joydex.Tests.{Guid.NewGuid():N}";
        var factory = new LegacyJoydexOwnershipLeaseFactory(mutexName);
        var first = await Task.Run(factory.Acquire).WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Task.Run(factory.Acquire).WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            await Task.Run(first.Dispose).WaitAsync(TimeSpan.FromSeconds(2));
        }

        var replacement = await Task.Run(factory.Acquire).WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Run(replacement.Dispose).WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static RuntimeEngineOptions Options(
        string root,
        string name,
        ISettingsImpactPlanner planner,
        ISettingsActivator activator,
        IRuntimeCommandHandler? commandHandler = null,
        Action<Guid>? releaseCaptureObservation = null,
        Action<string>? captureObservationStarted = null,
        Action<TestRuntimeComposition>? compositionCreated = null,
        IRuntimeSettingsProcessLauncher? settingsProcessLauncher = null)
    {
        return new RuntimeEngineOptions(
            RuntimeInstanceKind.Synthetic,
            root,
            Path.GetFullPath(root),
            RuntimeSettingsPaths.InDataRoot(root),
            new SyntheticRuntimeOwnershipLeaseFactory(),
            (host, _) =>
            {
                var composition = new TestRuntimeComposition(
                    host,
                    name,
                    activator,
                    commandHandler,
                    releaseCaptureObservation,
                    captureObservationStarted);
                compositionCreated?.Invoke(composition);
                return composition;
            },
            planner,
            TimeProvider.System,
            settingsProcessLauncher);
    }

    private static Task<RuntimeAttachResult> AttachAsync(
        RuntimeEngine engine,
        RuntimeRpcSession session,
        RuntimeClientKind kind) => session.AttachAsync(
        new RuntimeAttachRequest(
            RuntimeProtocol.MajorVersion,
            RuntimeProtocol.MinorVersion,
            kind,
            RuntimeInstanceKind.Synthetic,
            engine.DataRootId,
            "consumed-by-transport"),
        CancellationToken.None);

    private static JoystickSnapshot Snapshot(bool buttonPressed) => new(
        DateTimeOffset.UtcNow,
        [buttonPressed],
        [],
        []);

    private static RuntimeInputObservation Observation(long sequence) => new(
        sequence,
        "source",
        1,
        Snapshot(buttonPressed: false),
        []);

    private static async Task AssertEventuallyAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!await condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class RecordingClient : IRuntimeRpcClient
    {
        public ConcurrentQueue<RuntimeEvent> RuntimeEvents { get; } = new();
        public ConcurrentQueue<RuntimeConnectionInputEvent> InputEvents { get; } = new();
        public ConcurrentQueue<RuntimeCommandResult> CommandResults { get; } = new();
        public TaskCompletionSource<RuntimeConnectionInputEvent> CaptureCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RuntimeEventAsync(RuntimeEvent runtimeEvent, CancellationToken cancellationToken)
        {
            RuntimeEvents.Enqueue(runtimeEvent);
            return Task.CompletedTask;
        }

        public Task RuntimeInputEventAsync(
            RuntimeConnectionInputEvent inputEvent,
            CancellationToken cancellationToken)
        {
            InputEvents.Enqueue(inputEvent);
            if (inputEvent.Capture?.Lease.Status == InputCaptureStatus.Completed)
            {
                CaptureCompleted.TrySetResult(inputEvent);
            }
            return Task.CompletedTask;
        }

        public Task RuntimeCommandCompletedAsync(
            RuntimeCommandResult result,
            CancellationToken cancellationToken)
        {
            CommandResults.Enqueue(result);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSettingsProcessLauncher : IRuntimeSettingsProcessLauncher
    {
        public List<RuntimeCommandRequest> Requests { get; } = [];
        public List<(string Kind, string ConnectionId)> LifecycleNotifications { get; } = [];

        public Task<RuntimeCommandResult> OpenAsync(
            RuntimeCommandRequest request,
            CancellationToken runtimeCancellationToken)
        {
            runtimeCancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(new RuntimeCommandResult(
                request.OperationId,
                request.Kind,
                RuntimeCommandStatus.Completed,
                "Settings process opened."));
        }

        public void OnSettingsAttached(string connectionId) =>
            LifecycleNotifications.Add(("attached", connectionId));

        public void OnSettingsDisconnected(string connectionId) =>
            LifecycleNotifications.Add(("disconnected", connectionId));
    }

    private sealed class BlockingInputClient : IRuntimeRpcClient
    {
        private int _inputCallbacks;
        public ConcurrentQueue<RuntimeConnectionInputEvent> Events { get; } = new();
        public TaskCompletionSource FirstCallbackEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstCallback { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CaptureCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RuntimeEventAsync(RuntimeEvent runtimeEvent, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public async Task RuntimeInputEventAsync(
            RuntimeConnectionInputEvent inputEvent,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _inputCallbacks) == 1)
            {
                FirstCallbackEntered.TrySetResult();
                await ReleaseFirstCallback.Task.WaitAsync(cancellationToken);
            }
            Events.Enqueue(inputEvent);
            if (inputEvent.Capture?.Lease.Status == InputCaptureStatus.Completed)
            {
                CaptureCompleted.TrySetResult();
            }
        }

        public Task RuntimeCommandCompletedAsync(
            RuntimeCommandResult result,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FailingInputClient : IRuntimeRpcClient
    {
        public Task RuntimeEventAsync(RuntimeEvent runtimeEvent, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RuntimeInputEventAsync(
            RuntimeConnectionInputEvent inputEvent,
            CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException("Synthetic callback failure."));

        public Task RuntimeCommandCompletedAsync(
            RuntimeCommandResult result,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingActivator : ISettingsActivator
    {
        public List<SettingsAggregateId> Aggregates { get; } = [];

        public Task<SettingsActivationResult> ActivateAsync(
            SettingsAggregateId aggregate,
            SettingsBundle activationCandidate,
            long desiredRevision,
            CancellationToken runtimeCancellationToken)
        {
            runtimeCancellationToken.ThrowIfCancellationRequested();
            Aggregates.Add(aggregate);
            return Task.FromResult(new SettingsActivationResult(SettingsActivationState.Applied));
        }
    }

    private sealed class BlockingActivator : ISettingsActivator
    {
        private int _armed;

        public TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public async Task<SettingsActivationResult> ActivateAsync(
            SettingsAggregateId aggregate,
            SettingsBundle activationCandidate,
            long desiredRevision,
            CancellationToken runtimeCancellationToken)
        {
            if (Volatile.Read(ref _armed) == 0)
            {
                return new SettingsActivationResult(SettingsActivationState.Applied);
            }
            Entered.TrySetResult();
            await Release.Task.WaitAsync(runtimeCancellationToken);
            return new SettingsActivationResult(SettingsActivationState.Applied);
        }
    }

    private sealed class TestRuntimeComposition(
        RuntimeInputHost host,
        string instanceName,
        ISettingsActivator activator,
        IRuntimeCommandHandler? commandHandler = null,
        Action<Guid>? releaseCaptureObservation = null,
        Action<string>? captureObservationStarted = null) : IRuntimeComposition
    {
        private readonly SyntheticRuntimeInputCatalog _inputs = new(host, instanceName);
        private readonly IRuntimeCommandHandler _commands =
            commandHandler ?? new SyntheticRuntimeCommandHandler();

        public bool VoiceSessionActive => false;

        public event EventHandler? ActivationBoundaryAvailable;
        public event EventHandler<RuntimeUiEvent>? UiChanged;

        public RuntimeUiSnapshot GetUiSnapshot() => new();

        public void PublishUi(RuntimeUiEvent runtimeUiEvent) =>
            UiChanged?.Invoke(this, runtimeUiEvent);

        public void SignalActivationBoundary() =>
            ActivationBoundaryAvailable?.Invoke(this, EventArgs.Empty);

        public RuntimeInputSource[] Refresh(SettingsBundle activeSettings) =>
            _inputs.Refresh(activeSettings);

        public bool ObserveForCapture(Guid captureId, string sourceId)
        {
            var observed = _inputs.ObserveForCapture(captureId, sourceId);
            captureObservationStarted?.Invoke(sourceId);
            return observed;
        }

        public void ReleaseCaptureObservation(Guid captureId)
        {
            _inputs.ReleaseCaptureObservation(captureId);
            releaseCaptureObservation?.Invoke(captureId);
        }

        public Task<SettingsActivationResult> ActivateAsync(
            SettingsAggregateId aggregate,
            SettingsBundle activationCandidate,
            long desiredRevision,
            CancellationToken runtimeCancellationToken) =>
            activator.ActivateAsync(
                aggregate,
                activationCandidate,
                desiredRevision,
                runtimeCancellationToken);

        public Task<RuntimeCommandResult> ExecuteAsync(
            RuntimeCommandRequest request,
            CancellationToken runtimeCancellationToken) =>
            _commands.ExecuteAsync(request, runtimeCancellationToken);

        public ValueTask DisposeAsync() => _inputs.DisposeAsync();
    }

    private sealed class BlockingCommandHandler : IRuntimeCommandHandler
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;

        public int Started => Volatile.Read(ref _started);

        public async Task<RuntimeCommandResult> ExecuteAsync(
            RuntimeCommandRequest request,
            CancellationToken runtimeCancellationToken)
        {
            Interlocked.Increment(ref _started);
            await _release.Task.WaitAsync(runtimeCancellationToken);
            return new RuntimeCommandResult(
                request.OperationId,
                request.Kind,
                RuntimeCommandStatus.Completed);
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class RecordingCommandHandler : IRuntimeCommandHandler
    {
        public ConcurrentQueue<RuntimeCommandRequest> Requests { get; } = new();

        public Task<RuntimeCommandResult> ExecuteAsync(
            RuntimeCommandRequest request,
            CancellationToken runtimeCancellationToken)
        {
            runtimeCancellationToken.ThrowIfCancellationRequested();
            Requests.Enqueue(request);
            return Task.FromResult(new RuntimeCommandResult(
                request.OperationId,
                request.Kind,
                RuntimeCommandStatus.Completed));
        }
    }

    private sealed class VoicePendingPlanner : ISettingsImpactPlanner
    {
        public SettingsEffect[] Plan(
            SettingsBundle active,
            SettingsBundle candidate,
            IReadOnlyList<SettingsAggregateId> changedAggregates) => changedAggregates
            .Select(aggregate => new SettingsEffect(
                aggregate,
                aggregate == SettingsAggregateId.Voice
                    ? SettingsEffectKind.PendingIdle
                    : SettingsEffectKind.ApplyLive,
                "Synthetic pending call."))
            .ToArray();
    }

    private sealed class ScratchDirectory : IDisposable
    {
        public ScratchDirectory(bool create = true)
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "joydex-runtime-engine-tests",
                Guid.NewGuid().ToString("N"));
            if (create)
            {
                Directory.CreateDirectory(Root);
            }
        }

        public string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class FailingOwnershipLeaseFactory : IRuntimeOwnershipLeaseFactory
    {
        public IRuntimeOwnershipLease Acquire() =>
            throw new InvalidOperationException("Synthetic ownership conflict.");
    }
}
