using System.Text.Json;
using System.Text.Json.Nodes;
using Joydex.App;
using Joydex.Contracts;
using Joydex.Core.Config;
using Joydex.Core.Input;
using Joydex.Core.TaskAlerts;
using Joydex.Core.Voice;

namespace Joydex.Tests;

public sealed class RuntimeClientStateTests
{
    [Fact]
    public async Task OversizedUiEventRequiresSnapshotAndResumesAtItsCursor()
    {
        var client = new RuntimeClientState(new ImmediateSynchronizationContext());
        var epoch = Guid.NewGuid();
        client.Initialize(Attach(Snapshot(epoch, eventCursor: 2, bank: 2)));
        await client.RuntimeEventAsync(new RuntimeEvent(epoch, 3, RuntimeEventKind.UiResynchronizationRequired), default);
        Assert.True(client.Current.ResynchronizationRequired);
        Assert.Equal(2, client.Current.Snapshot!.EventCursor);

        client.ApplySnapshot(Snapshot(epoch, eventCursor: 3, bank: 4));
        Assert.False(client.Current.ResynchronizationRequired);
        Assert.Equal(3, client.Current.Snapshot!.EventCursor);
        Assert.Equal(4, client.Current.Snapshot.Settings.Desired.TaskAlerts.Bank);
    }

    [Fact]
    public async Task AttachBuffersOnlyPostSnapshotEventsAndPublishesOneInitializedState()
    {
        var client = new RuntimeClientState(new ImmediateSynchronizationContext());
        var changes = new List<RuntimeClientStateChange>();
        client.Changed += (_, change) => changes.Add(change);
        var epoch = Guid.NewGuid();

        await client.RuntimeEventAsync(
            new RuntimeEvent(
                epoch,
                5,
                RuntimeEventKind.SettingsChanged,
                Settings: Settings(revision: 5, bank: 3)),
            CancellationToken.None);
        await client.RuntimeEventAsync(
            new RuntimeEvent(
                epoch,
                6,
                RuntimeEventKind.SettingsChanged,
                Settings: Settings(revision: 6, bank: 4)),
            CancellationToken.None);

        client.Initialize(Attach(Snapshot(epoch, eventCursor: 5, bank: 3)));

        Assert.Collection(
            changes,
            initialized => Assert.Equal(RuntimeClientChangeKind.Initialized, initialized.Kind),
            firstBuffered => Assert.Equal(5, firstBuffered.RuntimeEvent!.Sequence),
            secondBuffered => Assert.Equal(6, secondBuffered.RuntimeEvent!.Sequence));
        var finalSnapshot = Assert.IsType<RuntimeSnapshot>(changes[^1].State.Snapshot);
        Assert.Equal(6, finalSnapshot.EventCursor);
        Assert.Equal(4, finalSnapshot.Settings.Desired.TaskAlerts.Bank);
        Assert.False(changes[^1].State.ResynchronizationRequired);
    }

    [Fact]
    public async Task RuntimeEventGapRequiresSnapshotBeforeLaterEventsAreApplied()
    {
        var client = new RuntimeClientState(new ImmediateSynchronizationContext());
        var changes = new List<RuntimeClientStateChange>();
        client.Changed += (_, change) => changes.Add(change);
        var epoch = Guid.NewGuid();
        client.Initialize(Attach(Snapshot(epoch, eventCursor: 2, bank: 2)));

        await client.RuntimeEventAsync(
            new RuntimeEvent(
                epoch,
                4,
                RuntimeEventKind.SettingsChanged,
                Settings: Settings(revision: 4, bank: 4)),
            CancellationToken.None);
        await client.RuntimeEventAsync(
            new RuntimeEvent(
                epoch,
                3,
                RuntimeEventKind.SettingsChanged,
                Settings: Settings(revision: 3, bank: 3)),
            CancellationToken.None);

        Assert.True(client.Current.ResynchronizationRequired);
        Assert.Equal(2, client.Current.Snapshot!.EventCursor);
        Assert.Equal(RuntimeClientChangeKind.ResynchronizationRequired, changes[^1].Kind);

        client.ApplySnapshot(Snapshot(epoch, eventCursor: 4, bank: 4));
        await client.RuntimeEventAsync(
            new RuntimeEvent(
                epoch,
                5,
                RuntimeEventKind.SettingsChanged,
                Settings: Settings(revision: 5, bank: 5)),
            CancellationToken.None);

        Assert.False(client.Current.ResynchronizationRequired);
        Assert.Equal(5, client.Current.Snapshot!.EventCursor);
        Assert.Equal(5, client.Current.Snapshot.Settings.Desired.TaskAlerts.Bank);
    }

    [Fact]
    public async Task CaptureCallbacksUpdateOnlyTheConnectionOwnedActiveLeases()
    {
        var client = new RuntimeClientState(new ImmediateSynchronizationContext());
        var changes = new List<RuntimeClientStateChange>();
        client.Changed += (_, change) => changes.Add(change);
        var epoch = Guid.NewGuid();
        var captureId = Guid.NewGuid();
        client.Initialize(Attach(Snapshot(epoch, eventCursor: 1, bank: 2)));

        await client.RuntimeInputEventAsync(
            new RuntimeConnectionInputEvent(
                epoch,
                1,
                RuntimeConnectionInputEventKind.CaptureChanged,
                Capture: new RuntimeCaptureUpdate(new RuntimeCaptureLease(
                    captureId,
                    "controller",
                    "binding",
                    1,
                    DateTimeOffset.UtcNow.AddSeconds(10),
                    InputCaptureStatus.Active,
                    1))),
            CancellationToken.None);
        Assert.Equal(captureId, Assert.Single(client.Current.Snapshot!.Input.Captures).CaptureId);

        await client.RuntimeInputEventAsync(
            new RuntimeConnectionInputEvent(
                epoch,
                2,
                RuntimeConnectionInputEventKind.CaptureChanged,
                Capture: new RuntimeCaptureUpdate(
                    new RuntimeCaptureLease(
                        captureId,
                        "controller",
                        "binding",
                        1,
                        DateTimeOffset.UtcNow,
                        InputCaptureStatus.Completed,
                        2),
                    new JoystickEvent(JoystickEventKind.ButtonPressed, 0, 1))),
            CancellationToken.None);

        Assert.Empty(client.Current.Snapshot!.Input.Captures);
        var completion = Assert.Single(
            changes,
            change => change.InputEvent?.Capture?.Lease.Status == InputCaptureStatus.Completed);
        Assert.Equal(captureId, completion.InputEvent!.Capture!.Lease.CaptureId);
        Assert.Equal(2, client.Current.InputEventCursor);
    }

    [Fact]
    public async Task SnapshotRefreshPreservesTheInputCursorForTheSameConnection()
    {
        var client = new RuntimeClientState(new ImmediateSynchronizationContext());
        var epoch = Guid.NewGuid();
        client.Initialize(Attach(Snapshot(epoch, eventCursor: 1, bank: 2)));
        await client.RuntimeInputEventAsync(
            ObservationEvent(epoch, sequence: 1),
            CancellationToken.None);

        client.ApplySnapshot(Snapshot(
            epoch,
            eventCursor: 1,
            bank: 2,
            inputEventCursor: 1));
        await client.RuntimeInputEventAsync(
            ObservationEvent(epoch, sequence: 2),
            CancellationToken.None);

        Assert.False(client.Current.ResynchronizationRequired);
        Assert.Equal(2, client.Current.InputEventCursor);
    }

    [Fact]
    public async Task ResyncBuffersAndPostsOperationAndCaptureCompletionsAcrossSnapshotRpc()
    {
        var client = new RuntimeClientState(new ImmediateSynchronizationContext());
        var changes = new List<RuntimeClientStateChange>();
        client.Changed += (_, change) => changes.Add(change);
        var epoch = Guid.NewGuid();
        var captureId = Guid.NewGuid();
        client.Initialize(Attach(Snapshot(epoch, eventCursor: 1, bank: 2)));
        await client.RuntimeInputEventAsync(
            new RuntimeConnectionInputEvent(
                epoch,
                1,
                RuntimeConnectionInputEventKind.CaptureChanged,
                Capture: new RuntimeCaptureUpdate(new RuntimeCaptureLease(
                    captureId,
                    "controller",
                    "binding",
                    1,
                    DateTimeOffset.UtcNow.AddSeconds(10),
                    InputCaptureStatus.Active,
                    1))),
            CancellationToken.None);

        var operationId = Guid.NewGuid();
        var operation = new ApplySettingsResult(
            operationId,
            SettingsApplyStatus.Applied,
            true,
            true,
            [],
            [],
            Settings(3, bank: 3));
        await client.RuntimeEventAsync(
            new RuntimeEvent(
                epoch,
                3,
                RuntimeEventKind.OperationCompleted,
                Operation: operation),
            CancellationToken.None);
        await client.RuntimeInputEventAsync(
            new RuntimeConnectionInputEvent(
                epoch,
                2,
                RuntimeConnectionInputEventKind.CaptureChanged,
                Capture: new RuntimeCaptureUpdate(new RuntimeCaptureLease(
                    captureId,
                    "controller",
                    "binding",
                    1,
                    DateTimeOffset.UtcNow,
                    InputCaptureStatus.Completed,
                    2))),
            CancellationToken.None);

        Assert.True(client.Current.ResynchronizationRequired);
        client.ApplySnapshot(Snapshot(
            epoch,
            eventCursor: 3,
            bank: 3,
            inputEventCursor: 2));
        await client.RuntimeInputEventAsync(
            ObservationEvent(epoch, sequence: 3),
            CancellationToken.None);

        Assert.False(client.Current.ResynchronizationRequired);
        Assert.Equal(3, client.Current.InputEventCursor);
        Assert.Empty(client.Current.Snapshot!.Input.Captures);
        Assert.Contains(
            changes,
            change => change.RuntimeEvent?.Operation?.OperationId == operationId);
        Assert.Contains(
            changes,
            change => change.InputEvent?.Capture?.Lease is
            {
                CaptureId: var id,
                Status: InputCaptureStatus.Completed,
            } && id == captureId);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(256)]
    [InlineData(300)]
    public async Task ResyncOverflowPreservesCaptureCompletionAcrossRepeatedSnapshots(int completionSequence)
    {
        var client = new RuntimeClientState(new ImmediateSynchronizationContext());
        var changes = new List<RuntimeClientStateChange>();
        client.Changed += (_, change) => changes.Add(change);
        var epoch = Guid.NewGuid();
        var lease = new RuntimeCaptureLease(
            Guid.NewGuid(), "controller", "binding", 1,
            DateTimeOffset.UtcNow.AddSeconds(10), InputCaptureStatus.Active, 1);
        var initial = Snapshot(epoch, eventCursor: 1, bank: 2);
        client.Initialize(Attach(initial with { Input = initial.Input with { Captures = [lease] } }));
        await client.RuntimeEventAsync(
            new RuntimeEvent(epoch, 3, RuntimeEventKind.SettingsChanged, Settings: Settings(3, 2)), default);
        var capturedInput = new JoystickEvent(JoystickEventKind.ButtonPressed, 0, 7);
        for (var sequence = 1; sequence <= 600; sequence++)
        {
            await client.RuntimeInputEventAsync(
                sequence == completionSequence
                    ? new RuntimeConnectionInputEvent(
                        epoch, sequence, RuntimeConnectionInputEventKind.CaptureChanged,
                        Capture: new RuntimeCaptureUpdate(
                            lease with { Status = InputCaptureStatus.Completed, Revision = 2 }, capturedInput))
                    : ObservationEvent(epoch, sequence), default);
        }

        // The first in-flight snapshot predates the overflow; a second refresh is required.
        client.ApplySnapshot(initial with { Input = initial.Input with { Captures = [lease] } });
        Assert.True(client.Current.ResynchronizationRequired);
        client.ApplySnapshot(Snapshot(epoch, eventCursor: 3, bank: 2, inputEventCursor: 600));

        Assert.False(client.Current.ResynchronizationRequired);
        Assert.Empty(client.Current.Snapshot!.Input.Captures);
        var completion = Assert.Single(changes, change => change.InputEvent?.Capture is not null);
        Assert.Equal(lease.CaptureId, completion.InputEvent!.Capture!.Lease.CaptureId);
        Assert.Equal(InputCaptureStatus.Completed, completion.InputEvent.Capture.Lease.Status);
        Assert.Equal(capturedInput, completion.InputEvent.Capture.CapturedInput);
        await client.RuntimeInputEventAsync(ObservationEvent(epoch, 601), default);
        Assert.False(client.Current.ResynchronizationRequired);
        Assert.Equal(601, client.Current.InputEventCursor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResyncCoalescesCaptureRevisionsAndRejectsRetiredEpoch(bool replaceEpoch)
    {
        var client = new RuntimeClientState(new ImmediateSynchronizationContext());
        var captures = new List<RuntimeCaptureUpdate>();
        client.Changed += (_, change) =>
        {
            if (change.InputEvent?.Capture is { } capture)
            {
                captures.Add(capture);
            }
        };
        var epoch = Guid.NewGuid();
        client.Initialize(Attach(Snapshot(epoch, eventCursor: 1, bank: 2)));
        await client.RuntimeEventAsync(
            new RuntimeEvent(epoch, 3, RuntimeEventKind.SettingsChanged, Settings: Settings(3, 2)), default);
        var lease = new RuntimeCaptureLease(
            Guid.NewGuid(), "controller", "binding", 1,
            DateTimeOffset.UtcNow.AddSeconds(10), InputCaptureStatus.Active, 1);
        for (var revision = 1; revision <= 600; revision++)
        {
            await client.RuntimeInputEventAsync(new RuntimeConnectionInputEvent(
                epoch, revision, RuntimeConnectionInputEventKind.CaptureChanged,
                Capture: new RuntimeCaptureUpdate(lease with { Revision = revision })), default);
        }
        await client.RuntimeInputEventAsync(new RuntimeConnectionInputEvent(
            epoch, 601, RuntimeConnectionInputEventKind.CaptureChanged,
            Capture: new RuntimeCaptureUpdate(lease)), default);

        client.ApplySnapshot(Snapshot(
            replaceEpoch ? Guid.NewGuid() : epoch, eventCursor: 3, bank: 2, inputEventCursor: 601));

        Assert.False(client.Current.ResynchronizationRequired);
        if (replaceEpoch)
        {
            Assert.Empty(captures);
            Assert.Empty(client.Current.Snapshot!.Input.Captures);
        }
        else
        {
            Assert.Equal(600, Assert.Single(captures).Lease.Revision);
            Assert.Equal(600, Assert.Single(client.Current.Snapshot!.Input.Captures).Revision);
        }
    }

    [Fact]
    public async Task VoiceDeltaMergesTimelineAndResetRequestsAFullSnapshot()
    {
        var client = new RuntimeClientState(new ImmediateSynchronizationContext());
        var epoch = Guid.NewGuid();
        var snapshot = Snapshot(epoch, eventCursor: 1, bank: 2);
        var messaging = new RuntimeVoiceMessagingSnapshot(
            true,
            true,
            "Ready",
            [],
            null,
            "",
            []);
        snapshot = snapshot with
        {
            Ui = new RuntimeUiSnapshot(
                Voice: new RuntimeVoiceUiSnapshot(
                    VoiceSession(),
                    [],
                    messaging)),
        };
        client.Initialize(Attach(snapshot));
        var entry = new RuntimeVoiceTimelineEntry(
            "line-1",
            DateTimeOffset.UtcNow,
            RuntimeVoiceTimelineKind.Assistant,
            "Ready.");

        await client.RuntimeEventAsync(
            new RuntimeEvent(
                epoch,
                2,
                RuntimeEventKind.VoiceChanged,
                Voice: new RuntimeVoiceEvent(VoiceSession(), entry)),
            CancellationToken.None);

        Assert.Equal(entry, Assert.Single(client.Current.Snapshot!.Ui!.Voice!.Timeline));

        await client.RuntimeEventAsync(
            new RuntimeEvent(
                epoch,
                3,
                RuntimeEventKind.VoiceChanged,
                Voice: new RuntimeVoiceEvent(VoiceSession(), TimelineReset: true)),
            CancellationToken.None);

        Assert.True(client.Current.ResynchronizationRequired);
        Assert.Equal(2, client.Current.Snapshot!.EventCursor);
    }

    [Fact]
    public async Task CommandCompletionAndDisconnectArePostedWithoutMutatingTheSnapshot()
    {
        var client = new RuntimeClientState(new ImmediateSynchronizationContext());
        var changes = new List<RuntimeClientStateChange>();
        client.Changed += (_, change) => changes.Add(change);
        var snapshot = Snapshot(Guid.NewGuid(), eventCursor: 1, bank: 2);
        client.Initialize(Attach(snapshot));
        var result = new RuntimeCommandResult(
            Guid.NewGuid(),
            RuntimeCommandKind.ListDesktopTasks,
            RuntimeCommandStatus.Completed);

        await client.RuntimeCommandCompletedAsync(result, CancellationToken.None);
        var failure = new IOException("connection ended");
        client.MarkDisconnected(failure);

        Assert.Equal(result, changes[^2].CommandResult);
        Assert.Equal(RuntimeClientChangeKind.Disconnected, changes[^1].Kind);
        Assert.Same(failure, client.Current.DisconnectFailure);
        Assert.Same(snapshot, client.Current.Snapshot);
    }

    [Fact]
    public async Task LosingCallbacksBeforeAttachRequiresAFullSnapshotRefresh()
    {
        var client = new RuntimeClientState(new ImmediateSynchronizationContext());
        var epoch = Guid.NewGuid();
        for (var index = 1; index <= RuntimeProtocol.MaximumRetainedEvents + 1; index++)
        {
            await client.RuntimeEventAsync(
                new RuntimeEvent(
                    epoch,
                    index,
                    RuntimeEventKind.SettingsChanged,
                    Settings: Settings(index, bank: 2)),
                CancellationToken.None);
        }

        client.Initialize(Attach(Snapshot(epoch, eventCursor: 0, bank: 2)));

        Assert.True(client.Current.ResynchronizationRequired);
        Assert.Equal(0, client.Current.Snapshot!.EventCursor);
    }

    [Fact]
    public async Task ConcurrentCallbacksArePostedInTheirMutationOrder()
    {
        var context = new BlockingPostSynchronizationContext();
        var client = new RuntimeClientState(context);
        var sequences = new List<long>();
        client.Changed += (_, change) =>
        {
            if (change.RuntimeEvent is { } runtimeEvent)
            {
                sequences.Add(runtimeEvent.Sequence);
            }
        };
        var epoch = Guid.NewGuid();
        client.Initialize(Attach(Snapshot(epoch, eventCursor: 0, bank: 2)));
        context.BlockNextPost();

        var first = Task.Run(() => client.RuntimeEventAsync(
            new RuntimeEvent(
                epoch,
                1,
                RuntimeEventKind.SettingsChanged,
                Settings: Settings(1, bank: 3)),
            CancellationToken.None));
        await context.PostBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await client.RuntimeEventAsync(
            new RuntimeEvent(
                epoch,
                2,
                RuntimeEventKind.SettingsChanged,
                Settings: Settings(2, bank: 4)),
            CancellationToken.None);
        context.ReleasePost.TrySetResult();
        await first;

        Assert.Equal([1, 2], sequences);
    }

    [Fact]
    public async Task SnapshotReplayDoesNotDuplicateAnActivityAlreadyInTheSnapshot()
    {
        var client = new RuntimeClientState(new ImmediateSynchronizationContext());
        var epoch = Guid.NewGuid();
        var activity = new RuntimeActionActivity(42, DateTimeOffset.UtcNow, "Applied action");
        client.Initialize(Attach(Snapshot(epoch, eventCursor: 0, bank: 2)));
        await client.RuntimeEventAsync(
            new RuntimeEvent(
                epoch,
                2,
                RuntimeEventKind.ActionActivityAdded,
                ActionActivity: activity),
            CancellationToken.None);
        Assert.True(client.Current.ResynchronizationRequired);

        var replacement = Snapshot(epoch, eventCursor: 1, bank: 2) with
        {
            Ui = new RuntimeUiSnapshot(RecentActivity: [activity]),
        };
        client.ApplySnapshot(replacement);

        Assert.False(client.Current.ResynchronizationRequired);
        Assert.Equal(2, client.Current.Snapshot!.EventCursor);
        Assert.Equal(activity, Assert.Single(client.Current.Snapshot.Ui!.RecentActivity!));
    }

    [Fact]
    public void CurrentUiClientRejectsLegacyOrUnadvertisedReliableCursors()
    {
        var snapshot = Snapshot(Guid.NewGuid(), eventCursor: 3, bank: 2, inputEventCursor: 7);
        var legacyJson = JsonNode.Parse(JsonSerializer.Serialize(snapshot))!.AsObject();
        Assert.True(legacyJson.Remove(nameof(RuntimeSnapshot.InputEventCursor)));
        var legacySnapshot = legacyJson.Deserialize<RuntimeSnapshot>();
        Assert.NotNull(legacySnapshot);
        Assert.Equal(0, legacySnapshot.InputEventCursor);
        var legacyAttach = Attach(legacySnapshot) with
        {
            ProtocolMinor = 1,
            Capabilities = RuntimeProtocol.CapabilitiesForMinor(1),
        };
        var missingCapability = Attach(snapshot) with
        {
            Capabilities = RuntimeProtocol.CapabilitiesForMinor(2)
                .Where(capability => capability != RuntimeProtocol.ReliableCursorsCapability)
                .ToArray(),
        };

        Assert.Throws<InvalidDataException>(() =>
            RuntimeClientConnection.EnsureReliableStateProtocol(legacyAttach));
        Assert.Throws<InvalidDataException>(() =>
            RuntimeClientConnection.EnsureReliableStateProtocol(missingCapability));
        RuntimeClientConnection.EnsureReliableStateProtocol(Attach(snapshot));
    }

    private static RuntimeAttachResult Attach(RuntimeSnapshot snapshot) => new(
        "connection",
        RuntimeProtocol.MajorVersion,
        RuntimeProtocol.MinorVersion,
        RuntimeProtocol.Capabilities,
        RuntimeProtocol.MaximumMessageBytes,
        false,
        snapshot);

    private static RuntimeSnapshot Snapshot(
        Guid epoch,
        long eventCursor,
        int bank,
        long inputEventCursor = 0) => new(
        epoch,
        eventCursor,
        new RuntimeIdentitySnapshot(
            123,
            RuntimeInstanceKind.Synthetic,
            "root",
            1,
            []),
        Settings(revision: eventCursor, bank),
        new RuntimeInputSnapshot(
            [new RuntimeInputSource("controller", "Controller", null, null, null, 1, true)],
            []),
        new RuntimeUiSnapshot(),
        inputEventCursor);

    private static RuntimeConnectionInputEvent ObservationEvent(Guid epoch, long sequence) => new(
        epoch,
        sequence,
        RuntimeConnectionInputEventKind.InputObserved,
        Observation: new RuntimeInputObservation(
            sequence,
            "controller",
            1,
            new JoystickSnapshot(DateTimeOffset.UtcNow, [], [], []),
            []));

    private static SettingsSnapshot Settings(long revision, int bank)
    {
        var bundle = new SettingsBundle(
            CompanionConfig.CreateSafeDefault(),
            VoicePePreferences.Default,
            PebbleIndexPreferences.Default,
            TaskAlertPreferences.Default with { Bank = bank });
        return new SettingsSnapshot(revision, bundle, bundle, [], []);
    }

    private static RuntimeVoiceSnapshot VoiceSession() => new(
        RuntimeVoiceSessionState.Armed,
        true,
        false,
        true,
        false,
        "Ready",
        null,
        1);

    private sealed class ImmediateSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => callback(state);
    }

    private sealed class BlockingPostSynchronizationContext : SynchronizationContext
    {
        private int _blockNextPost;

        public TaskCompletionSource PostBlocked { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleasePost { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void BlockNextPost() => Interlocked.Exchange(ref _blockNextPost, 1);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            if (Interlocked.Exchange(ref _blockNextPost, 0) != 0)
            {
                PostBlocked.TrySetResult();
                ReleasePost.Task.GetAwaiter().GetResult();
            }
            callback(state);
        }
    }
}
