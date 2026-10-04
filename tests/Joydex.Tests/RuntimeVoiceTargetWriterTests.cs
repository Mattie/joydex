using Joydex.App;
using Joydex.Contracts;
using Joydex.Core.Config;
using Joydex.Core.TaskAlerts;
using Joydex.Core.Voice;

namespace Joydex.Tests;

public sealed class RuntimeVoiceTargetWriterTests
{
    [Fact]
    public async Task SelectionPatchesDesiredVoiceAndPreservesItsPendingFields()
    {
        var target = new RuntimeTaskReference(Guid.NewGuid().ToString("D"), "local");
        var activeVoice = VoicePePreferences.Default;
        var desiredVoice = activeVoice with { ConversationSpeakerGain = 4 };
        var initial = Settings(revision: 7, desiredVoice, activeVoice);
        PrepareSettingsRequest? prepareRequest = null;
        var rpc = new FakeRpc
        {
            Prepare = (request, _) =>
            {
                prepareRequest = request;
                return Task.FromResult(Prepared(initial));
            },
            Apply = (request, _) => Task.FromResult(new ApplySettingsResult(
                request.OperationId,
                SettingsApplyStatus.PendingIdle,
                DesiredStateCommitted: true,
                CanCloseSettings: true,
                [],
                [],
                Settings(
                    revision: 8,
                    desiredVoice with
                    {
                        VoiceTargetTaskId = target.TaskId,
                        VoiceTargetHostId = target.HostId,
                        VoiceTargetTaskLabel = "Selected task",
                    },
                    activeVoice))),
        };
        var writer = Writer(rpc, initial, target, "Selected task");

        var result = await writer.SelectAsync(target, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(RuntimeVoiceTargetSelectionStatus.SavedPendingIdle, result.Status);
        Assert.NotNull(prepareRequest);
        Assert.Equal(7, prepareRequest.BaseRevision);
        Assert.Equal(4, prepareRequest.Patch.Voice?.ConversationSpeakerGain);
        Assert.Equal(target.TaskId, prepareRequest.Patch.Voice?.VoiceTargetTaskId);
        Assert.Equal(target.HostId, prepareRequest.Patch.Voice?.VoiceTargetHostId);
        Assert.Equal("Selected task", prepareRequest.Patch.Voice?.VoiceTargetTaskLabel);
        Assert.Equal(1, rpc.ApplyCount);
    }

    [Fact]
    public async Task AppliedSelectionRequiresTheTargetInTheActiveSnapshot()
    {
        var target = new RuntimeTaskReference(Guid.NewGuid().ToString("D"), "local");
        var initial = Settings(12, VoicePePreferences.Default, VoicePePreferences.Default);
        var active = VoicePePreferences.Default with
        {
            VoiceTargetTaskId = target.TaskId,
            VoiceTargetHostId = target.HostId,
            VoiceTargetTaskLabel = "Active task",
        };
        var rpc = new FakeRpc
        {
            Prepare = (_, _) => Task.FromResult(Prepared(initial)),
            Apply = (request, _) => Task.FromResult(new ApplySettingsResult(
                request.OperationId,
                SettingsApplyStatus.Applied,
                DesiredStateCommitted: true,
                CanCloseSettings: true,
                [],
                [],
                Settings(13, active, active))),
        };

        var result = await Writer(rpc, initial, target, "Active task")
            .SelectAsync(target, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(RuntimeVoiceTargetSelectionStatus.Applied, result.Status);
    }

    [Fact]
    public async Task AppliedReplyWithoutActiveTargetIsReportedAsSavedPendingIdle()
    {
        var target = new RuntimeTaskReference(Guid.NewGuid().ToString("D"), "local");
        var original = VoicePePreferences.Default;
        var desired = original with
        {
            VoiceTargetTaskId = target.TaskId,
            VoiceTargetHostId = target.HostId,
            VoiceTargetTaskLabel = "Saved task",
        };
        var initial = Settings(12, original, original);
        var rpc = new FakeRpc
        {
            Prepare = (_, _) => Task.FromResult(Prepared(initial)),
            Apply = (request, _) => Task.FromResult(new ApplySettingsResult(
                request.OperationId,
                SettingsApplyStatus.Applied,
                DesiredStateCommitted: true,
                CanCloseSettings: true,
                [],
                [],
                Settings(13, desired, original))),
        };

        var result = await Writer(rpc, initial, target, "Saved task")
            .SelectAsync(target, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(RuntimeVoiceTargetSelectionStatus.SavedPendingIdle, result.Status);
    }

    [Fact]
    public async Task PreparationConflictDoesNotSubmitAnApply()
    {
        var target = new RuntimeTaskReference(Guid.NewGuid().ToString("D"), "local");
        var initial = Settings(3, VoicePePreferences.Default, VoicePePreferences.Default);
        var rpc = new FakeRpc
        {
            Prepare = (_, _) => Task.FromResult(new PrepareSettingsResult(
                SettingsPrepareStatus.Conflict,
                null,
                null,
                null,
                [],
                ["revision changed"],
                initial)),
        };

        var result = await Writer(rpc, initial, target, "Task")
            .SelectAsync(target, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(RuntimeVoiceTargetSelectionStatus.Conflict, result.Status);
        Assert.Contains("revision changed", result.Detail, StringComparison.Ordinal);
        Assert.Equal(0, rpc.ApplyCount);
    }

    [Fact]
    public async Task LostApplyReplyUsesTheExactCompletedOperation()
    {
        var target = new RuntimeTaskReference(Guid.NewGuid().ToString("D"), "local");
        var initial = Settings(5, VoicePePreferences.Default, VoicePePreferences.Default);
        var active = VoicePePreferences.Default with
        {
            VoiceTargetTaskId = target.TaskId,
            VoiceTargetHostId = target.HostId,
            VoiceTargetTaskLabel = "Recovered task",
        };
        Guid observedOperation = Guid.Empty;
        var rpc = new FakeRpc
        {
            Prepare = (_, _) => Task.FromResult(Prepared(initial)),
            Apply = (request, _) =>
            {
                observedOperation = request.OperationId;
                throw new IOException("reply lost");
            },
            Lookup = (operationId, _) => Task.FromResult(new SettingsOperationResult(
                operationId,
                SettingsOperationState.Completed,
                new ApplySettingsResult(
                    operationId,
                    SettingsApplyStatus.Applied,
                    DesiredStateCommitted: true,
                    CanCloseSettings: true,
                    [],
                    [],
                    Settings(6, active, active)))),
        };

        var result = await Writer(rpc, initial, target, "Recovered task")
            .SelectAsync(target, Guid.NewGuid(), CancellationToken.None);

        Assert.NotEqual(Guid.Empty, observedOperation);
        Assert.Equal([observedOperation], rpc.Lookups);
        Assert.Equal(1, rpc.ApplyCount);
        Assert.Equal(RuntimeVoiceTargetSelectionStatus.Applied, result.Status);
    }

    [Fact]
    public async Task MissingOperationAfterLostReplyIsUncertainAndNeverResubmitted()
    {
        var target = new RuntimeTaskReference(Guid.NewGuid().ToString("D"), "local");
        var initial = Settings(9, VoicePePreferences.Default, VoicePePreferences.Default);
        var rpc = new FakeRpc
        {
            Prepare = (_, _) => Task.FromResult(Prepared(initial)),
            Apply = (_, _) => throw new TimeoutException("reply lost"),
            Lookup = (operationId, _) => Task.FromResult(new SettingsOperationResult(
                operationId,
                SettingsOperationState.NotFound)),
        };

        var result = await Writer(rpc, initial, target, "Task")
            .SelectAsync(target, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(RuntimeVoiceTargetSelectionStatus.Uncertain, result.Status);
        Assert.Contains("not resubmitted", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, rpc.ApplyCount);
        Assert.Single(rpc.Lookups);
    }

    [Fact]
    public async Task MissingCurrentCatalogEntryPreservesTheDesiredLabel()
    {
        var target = new RuntimeTaskReference(Guid.NewGuid().ToString("D"), "local");
        var desired = VoicePePreferences.Default with { VoiceTargetTaskLabel = "Saved label" };
        var initial = Settings(14, desired, VoicePePreferences.Default);
        PrepareSettingsRequest? prepareRequest = null;
        var rpc = new FakeRpc
        {
            Prepare = (request, _) =>
            {
                prepareRequest = request;
                return Task.FromResult(new PrepareSettingsResult(
                    SettingsPrepareStatus.Conflict,
                    null,
                    null,
                    null,
                    [],
                    ["revision changed"],
                    initial));
            },
        };

        _ = await Writer(rpc, initial, target, targetLabel: null)
            .SelectAsync(target, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal("Saved label", prepareRequest?.Patch.Voice?.VoiceTargetTaskLabel);
    }

    private static RuntimeVoiceTargetWriter Writer(
        FakeRpc rpc,
        SettingsSnapshot settings,
        RuntimeTaskReference target,
        string? targetLabel)
    {
        var state = new RuntimeClientState(new ImmediateSynchronizationContext());
        RuntimeDesktopTask[] tasks = targetLabel is null
            ? []
            :
            [
                new RuntimeDesktopTask(
                    target.TaskId,
                    target.HostId,
                    targetLabel,
                    "idle",
                    ProjectId: null,
                    WorkingDirectory: null,
                    UpdatedAt: 1),
            ];
        var snapshot = new RuntimeSnapshot(
            Guid.NewGuid(),
            1,
            new RuntimeIdentitySnapshot(
                123,
                RuntimeInstanceKind.Synthetic,
                "test-root",
                1,
                []),
            settings,
            new RuntimeInputSnapshot([], []),
            new RuntimeUiSnapshot(Voice: new RuntimeVoiceUiSnapshot(
                new RuntimeVoiceSnapshot(
                    RuntimeVoiceSessionState.Armed,
                    OwnerReady: true,
                    SessionActive: false,
                    HistoryAvailable: true,
                    Stale: false,
                    "Ready",
                    Error: null,
                    ConversationVersion: 1),
                [],
                new RuntimeVoiceMessagingSnapshot(
                    Enabled: true,
                    BridgeAvailable: true,
                    "Ready",
                    tasks,
                    SelectedTask: null,
                    SelectedLabel: string.Empty,
                    []))),
            InputEventCursor: 0);
        state.Initialize(new RuntimeAttachResult(
            "settings-test",
            RuntimeProtocol.MajorVersion,
            RuntimeProtocol.MinorVersion,
            RuntimeProtocol.Capabilities,
            RuntimeProtocol.MaximumMessageBytes,
            ResynchronizationRequired: false,
            snapshot));
        return new RuntimeVoiceTargetWriter(rpc, state);
    }

    private static PrepareSettingsResult Prepared(SettingsSnapshot snapshot) => new(
        SettingsPrepareStatus.Prepared,
        "preparation-token",
        "payload-hash",
        DateTimeOffset.UtcNow.AddMinutes(1),
        [],
        [],
        snapshot);

    private static SettingsSnapshot Settings(
        long revision,
        VoicePePreferences desiredVoice,
        VoicePePreferences activeVoice)
    {
        var desired = new SettingsBundle(
            CompanionConfig.CreateSafeDefault(),
            desiredVoice,
            PebbleIndexPreferences.Default,
            TaskAlertPreferences.Default);
        var active = desired with { Voice = activeVoice };
        return new SettingsSnapshot(revision, desired, active, [], []);
    }

    private sealed class ImmediateSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => callback(state);
    }

    private sealed class FakeRpc : IRuntimeRpcServer
    {
        public Func<PrepareSettingsRequest, CancellationToken, Task<PrepareSettingsResult>> Prepare { get; init; } =
            (_, _) => throw new NotSupportedException();

        public Func<ApplySettingsRequest, CancellationToken, Task<ApplySettingsResult>> Apply { get; init; } =
            (_, _) => throw new NotSupportedException();

        public Func<Guid, CancellationToken, Task<SettingsOperationResult>> Lookup { get; init; } =
            (operationId, _) => Task.FromResult(new SettingsOperationResult(
                operationId,
                SettingsOperationState.NotFound));

        public int ApplyCount { get; private set; }

        public List<Guid> Lookups { get; } = [];

        public Task<PrepareSettingsResult> PrepareSettingsAsync(
            PrepareSettingsRequest request,
            CancellationToken cancellationToken) => Prepare(request, cancellationToken);

        public Task<ApplySettingsResult> ApplySettingsAsync(
            ApplySettingsRequest request,
            CancellationToken cancellationToken)
        {
            ApplyCount++;
            return Apply(request, cancellationToken);
        }

        public Task<SettingsOperationResult> GetSettingsOperationAsync(
            Guid operationId,
            CancellationToken cancellationToken)
        {
            Lookups.Add(operationId);
            return Lookup(operationId, cancellationToken);
        }

        public Task<RuntimeCommandResult> ExecuteCommandAsync(
            RuntimeCommandRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RuntimeCommandOperationResult> GetCommandOperationAsync(
            Guid operationId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RuntimeAttachResult> AttachAsync(
            RuntimeAttachRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RuntimeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

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
    }
}
