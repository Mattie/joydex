using Joydex.App;
using Joydex.Contracts;
using Joydex.Core.Config;
using Joydex.Core.TaskAlerts;
using Joydex.Core.Voice;

namespace Joydex.Tests;

public sealed class RuntimeSettingsDraftControllerTests
{
    [Fact]
    public async Task ApplyKeepsOperationIdentityBeforeDispatchAndPatchesOnlyChangedAggregate()
    {
        var initial = Snapshot(revision: 12);
        var candidate = initial.Settings.Desired with
        {
            Companion = Config(dryRun: false),
        };
        var pending = new TaskCompletionSource<RuntimeSettingsWriteResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new RecordingWriter
        {
            Apply = (_, _, _, _) => pending.Task,
        };
        var controller = new RuntimeSettingsDraftController(writer, initial);

        var apply = controller.ApplyAsync(candidate, CancellationToken.None);

        var call = Assert.Single(writer.Applies);
        Assert.Equal(12, call.BaseRevision);
        Assert.NotNull(call.Patch.Companion);
        Assert.Null(call.Patch.Voice);
        Assert.Null(call.Patch.PebbleIndex);
        Assert.Null(call.Patch.TaskAlerts);
        Assert.Equal(call.OperationId, controller.Current.PendingOperationId);
        Assert.True(RuntimeSettingsDraftController.SettingsEqual(
            candidate,
            controller.Current.DraftSettings));

        pending.SetResult(Result(
            RuntimeSettingsWriteOutcome.Applied,
            call.OperationId,
            Settings(revision: 13, candidate)));
        _ = await apply;
        Assert.Null(controller.Current.PendingOperationId);
        Assert.False(controller.Current.IsDirty);
    }

    [Fact]
    public async Task ConnectionFailureRetainsExactOperationForRecoveryOnReplacementEpoch()
    {
        var initial = Snapshot(revision: 20);
        var candidate = initial.Settings.Desired with
        {
            PebbleIndex = initial.Settings.Desired.PebbleIndex with { Enabled = true },
        };
        var firstWriter = new RecordingWriter
        {
            Apply = (_, _, _, _) => throw new OperationCanceledException("connection changed"),
        };
        var controller = new RuntimeSettingsDraftController(firstWriter, initial);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            controller.ApplyAsync(candidate, CancellationToken.None));
        var operationId = Assert.IsType<Guid>(controller.Current.PendingOperationId);

        var committed = Settings(revision: 21, candidate);
        var replacementWriter = new RecordingWriter
        {
            Recover = (requestedOperationId, _) => Task.FromResult(Result(
                RuntimeSettingsWriteOutcome.Applied,
                requestedOperationId,
                committed)),
        };
        controller.BeginConnection(
            replacementWriter,
            Snapshot(revision: 21, settings: committed));

        var recovered = await controller.RecoverAsync(CancellationToken.None);

        Assert.Equal(RuntimeSettingsWriteOutcome.Applied, recovered?.Outcome);
        Assert.Equal([operationId], replacementWriter.Recoveries);
        Assert.Empty(replacementWriter.Applies);
        Assert.False(controller.Current.IsDirty);
        Assert.Equal(21, controller.Current.BaseRevision);
    }

    [Fact]
    public async Task ChangedDraftAfterLostApplyReplyRequiresRecoveryThenExplicitApply()
    {
        var initial = Snapshot(revision: 30);
        var candidateA = initial.Settings.Desired with
        {
            PebbleIndex = initial.Settings.Desired.PebbleIndex with { Enabled = true },
        };
        var firstWriter = new RecordingWriter
        {
            Apply = (_, _, _, _) => throw new IOException("reply lost"),
        };
        var controller = new RuntimeSettingsDraftController(firstWriter, initial);

        await Assert.ThrowsAsync<IOException>(() =>
            controller.ApplyAsync(candidateA, CancellationToken.None));
        var oldOperationId = Assert.IsType<Guid>(controller.Current.PendingOperationId);
        Assert.True(RuntimeSettingsDraftController.SettingsEqual(
            candidateA,
            Assert.IsType<SettingsBundle>(controller.Current.PendingSettings)));

        var candidateB = candidateA with
        {
            Voice = candidateA.Voice with { ConversationSpeakerGain = 4 },
        };
        controller.UpdateDraft(candidateB);
        var authoritativeA = Settings(revision: 31, candidateA);
        var replacementWriter = new RecordingWriter
        {
            Recover = (requestedOperationId, _) => Task.FromResult(Result(
                RuntimeSettingsWriteOutcome.Applied,
                requestedOperationId,
                authoritativeA)),
            Apply = (revision, _, operationId, _) => Task.FromResult(Result(
                RuntimeSettingsWriteOutcome.Applied,
                operationId,
                Settings(revision + 1, candidateB))),
        };
        controller.BeginConnection(
            replacementWriter,
            Snapshot(revision: 31, settings: authoritativeA));

        var review = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.ApplyAsync(candidateB, CancellationToken.None));

        Assert.Contains("choose Apply again", review.Message, StringComparison.Ordinal);
        Assert.Equal([oldOperationId], replacementWriter.Recoveries);
        Assert.Empty(replacementWriter.Applies);
        Assert.Null(controller.Current.PendingOperationId);
        Assert.Equal(31, controller.Current.BaseRevision);
        Assert.True(RuntimeSettingsDraftController.SettingsEqual(
            candidateA,
            controller.Current.BaseSettings));
        Assert.True(RuntimeSettingsDraftController.SettingsEqual(
            candidateB,
            controller.Current.DraftSettings));
        Assert.True(controller.Current.IsDirty);
        Assert.False(controller.Current.RequiresRebase);

        var applied = await controller.ApplyAsync(candidateB, CancellationToken.None);

        Assert.Equal(RuntimeSettingsWriteOutcome.Applied, applied.Outcome);
        var applyB = Assert.Single(replacementWriter.Applies);
        Assert.Equal(31, applyB.BaseRevision);
        Assert.NotEqual(oldOperationId, applyB.OperationId);
        Assert.Null(applyB.Patch.Companion);
        Assert.NotNull(applyB.Patch.Voice);
        Assert.Null(applyB.Patch.PebbleIndex);
        Assert.Null(applyB.Patch.TaskAlerts);
        Assert.Equal([oldOperationId], replacementWriter.Recoveries);
        Assert.Null(controller.Current.PendingOperationId);
        Assert.False(controller.Current.IsDirty);
    }

    [Fact]
    public async Task ChangedAuthorityRequiresExplicitRebaseBeforePreservedDraftCanApply()
    {
        var initial = Snapshot(revision: 4);
        var candidate = initial.Settings.Desired with
        {
            Voice = initial.Settings.Desired.Voice with { ConversationSpeakerGain = 4 },
        };
        var controller = new RuntimeSettingsDraftController(new RecordingWriter(), initial);
        controller.UpdateDraft(candidate);
        var latestBundle = initial.Settings.Desired with
        {
            TaskAlerts = initial.Settings.Desired.TaskAlerts with { Bank = 5 },
        };
        var latest = Snapshot(revision: 5, settings: Settings(5, latestBundle));
        var replacementWriter = new RecordingWriter();

        controller.BeginConnection(replacementWriter, latest);
        var blocked = await controller.ApplyAsync(candidate, CancellationToken.None);

        Assert.Equal(RuntimeSettingsWriteOutcome.Conflict, blocked.Outcome);
        Assert.True(controller.Current.RequiresRebase);
        Assert.Empty(replacementWriter.Applies);

        controller.RebaseToLatest();
        replacementWriter.Apply = (revision, _, operationId, _) => Task.FromResult(Result(
            RuntimeSettingsWriteOutcome.Applied,
            operationId,
            Settings(revision + 1, latestBundle with { Voice = candidate.Voice })));
        var applied = await controller.ApplyAsync(
            controller.Current.DraftSettings,
            CancellationToken.None);

        Assert.Equal(RuntimeSettingsWriteOutcome.Applied, applied.Outcome);
        var call = Assert.Single(replacementWriter.Applies);
        Assert.Equal(5, call.BaseRevision);
        Assert.Null(call.Patch.TaskAlerts);
        Assert.NotNull(call.Patch.Voice);
    }

    [Fact]
    public async Task ChangedAuthorityRequiresReviewEvenBeforeTheEditorProjectsItsFirstChange()
    {
        var initial = Snapshot(revision: 8);
        var controller = new RuntimeSettingsDraftController(new RecordingWriter(), initial);
        var latestBundle = initial.Settings.Desired with
        {
            TaskAlerts = initial.Settings.Desired.TaskAlerts with { Bank = 4 },
        };
        var replacementWriter = new RecordingWriter();

        controller.BeginConnection(
            replacementWriter,
            Snapshot(revision: 9, settings: Settings(9, latestBundle)));
        var blocked = await controller.ApplyAsync(
            initial.Settings.Desired,
            CancellationToken.None);

        Assert.Equal(RuntimeSettingsWriteOutcome.Conflict, blocked.Outcome);
        Assert.True(controller.Current.RequiresRebase);
        Assert.Empty(replacementWriter.Applies);
    }

    [Fact]
    public async Task AuthorityConflictPreservesDraftAndRequiresRebaseWhenRevisionAdvanced()
    {
        var initial = Snapshot(revision: 4);
        var candidate = initial.Settings.Desired with
        {
            Voice = initial.Settings.Desired.Voice with { ConversationSpeakerGain = 4 },
        };
        var latestBundle = initial.Settings.Desired with
        {
            TaskAlerts = initial.Settings.Desired.TaskAlerts with { Bank = 5 },
        };
        var writer = new RecordingWriter
        {
            Apply = (_, _, operationId, _) => Task.FromResult(Result(
                RuntimeSettingsWriteOutcome.Conflict,
                operationId,
                Settings(5, latestBundle),
                "changed elsewhere")),
        };
        var controller = new RuntimeSettingsDraftController(writer, initial);

        var result = await controller.ApplyAsync(candidate, CancellationToken.None);

        Assert.Equal(RuntimeSettingsWriteOutcome.Conflict, result.Outcome);
        Assert.Equal(4, controller.Current.BaseRevision);
        Assert.Null(controller.Current.PendingOperationId);
        Assert.True(controller.Current.IsDirty);
        Assert.True(controller.Current.RequiresRebase);
        Assert.Equal(4, controller.Current.DraftSettings.Voice.ConversationSpeakerGain);
    }

    [Fact]
    public async Task CommittedDesiredStateBecomesTheBaseWhenActivationFails()
    {
        var initial = Snapshot(revision: 4);
        var candidate = initial.Settings.Desired with
        {
            Voice = initial.Settings.Desired.Voice with { ConversationSpeakerGain = 4 },
        };
        var committed = new SettingsSnapshot(
            5,
            candidate,
            initial.Settings.Active,
            [],
            []);
        var writer = new RecordingWriter
        {
            Apply = (_, _, operationId, _) => Task.FromResult(new RuntimeSettingsWriteResult(
                RuntimeSettingsWriteOutcome.Failed,
                operationId,
                committed,
                new ApplySettingsResult(
                    operationId,
                    SettingsApplyStatus.ActivationFailed,
                    DesiredStateCommitted: true,
                    CanCloseSettings: false,
                    [],
                    ["port busy"],
                    committed),
                "The settings were saved, but activation failed.")),
        };
        var controller = new RuntimeSettingsDraftController(writer, initial);

        var result = await controller.ApplyAsync(candidate, CancellationToken.None);

        Assert.Equal(RuntimeSettingsWriteOutcome.Failed, result.Outcome);
        Assert.Equal(5, controller.Current.BaseRevision);
        Assert.Equal(candidate, controller.Current.BaseSettings);
        Assert.Equal(candidate, controller.Current.DraftSettings);
        Assert.False(controller.Current.IsDirty);
        Assert.False(controller.Current.RequiresRebase);
        Assert.Null(controller.Current.PendingOperationId);
        Assert.Contains("activation failed", controller.Current.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiscardDraftAdoptsTheLatestAuthoritativeSettings()
    {
        var initial = Snapshot(revision: 2);
        var controller = new RuntimeSettingsDraftController(new RecordingWriter(), initial);
        var latestBundle = initial.Settings.Desired with
        {
            TaskAlerts = initial.Settings.Desired.TaskAlerts with { Bank = 6 },
        };
        controller.UpdateDraft(initial.Settings.Desired with
        {
            Voice = initial.Settings.Desired.Voice with { ConversationSpeakerGain = 3 },
        });
        controller.BeginConnection(
            new RecordingWriter(),
            Snapshot(3, Settings(3, latestBundle)));

        controller.DiscardDraft();

        Assert.False(controller.Current.IsDirty);
        Assert.False(controller.Current.RequiresRebase);
        Assert.Equal(3, controller.Current.BaseRevision);
        Assert.Equal(6, controller.Current.DraftSettings.TaskAlerts.Bank);
    }

    private static RuntimeSnapshot Snapshot(long revision, SettingsSnapshot? settings = null) => new(
        Guid.NewGuid(),
        0,
        new RuntimeIdentitySnapshot(1, RuntimeInstanceKind.Synthetic, "test-root", 1, []),
        settings ?? Settings(revision, Bundle()),
        new RuntimeInputSnapshot([], []));

    private static SettingsSnapshot Settings(long revision, SettingsBundle desired) => new(
        revision,
        desired,
        desired,
        [],
        []);

    private static SettingsBundle Bundle() => new(
        Config(dryRun: true),
        VoicePePreferences.Default,
        PebbleIndexPreferences.Default,
        TaskAlertPreferences.Default);

    private static CompanionConfig Config(bool dryRun) => new()
    {
        Safety = new SafetyOptions { DryRun = dryRun },
    };

    private static RuntimeSettingsWriteResult Result(
        RuntimeSettingsWriteOutcome outcome,
        Guid operationId,
        SettingsSnapshot snapshot,
        string detail = "done") => new(
        outcome,
        operationId,
        snapshot,
        ApplyResult: null,
        detail);

    private sealed class RecordingWriter : IRuntimeSettingsWriter
    {
        public Func<long, SettingsPatch, Guid, CancellationToken, Task<RuntimeSettingsWriteResult>> Apply { get; set; } =
            (_, _, _, _) => throw new NotSupportedException();

        public Func<Guid, CancellationToken, Task<RuntimeSettingsWriteResult>> Recover { get; init; } =
            (_, _) => throw new NotSupportedException();

        public List<ApplyCall> Applies { get; } = [];

        public List<Guid> Recoveries { get; } = [];

        public Task<RuntimeSettingsWriteResult> ApplyAsync(
            long baseRevision,
            SettingsPatch patch,
            Guid operationId,
            CancellationToken cancellationToken)
        {
            Applies.Add(new ApplyCall(baseRevision, patch, operationId));
            return Apply(baseRevision, patch, operationId, cancellationToken);
        }

        public Task<RuntimeSettingsWriteResult> RecoverAsync(
            Guid operationId,
            CancellationToken cancellationToken)
        {
            Recoveries.Add(operationId);
            return Recover(operationId, cancellationToken);
        }
    }

    private sealed record ApplyCall(long BaseRevision, SettingsPatch Patch, Guid OperationId);
}
