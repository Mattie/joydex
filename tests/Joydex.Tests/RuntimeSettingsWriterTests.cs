using Joydex.App;
using Joydex.Contracts;
using Joydex.Core.Config;
using Joydex.Core.TaskAlerts;
using Joydex.Core.Voice;

namespace Joydex.Tests;

public sealed class RuntimeSettingsWriterTests
{
    [Fact]
    public async Task AppliesPreparedPatchWithCallerOperationIdentity()
    {
        var operationId = Guid.NewGuid();
        var initial = Snapshot(revision: 4);
        var applied = Snapshot(revision: 5);
        PrepareSettingsRequest? preparedRequest = null;
        ApplySettingsRequest? appliedRequest = null;
        var rpc = new FakeRpc
        {
            Prepare = (request, _) =>
            {
                preparedRequest = request;
                return Task.FromResult(Prepared(initial));
            },
            Apply = (request, _) =>
            {
                appliedRequest = request;
                return Task.FromResult(Applied(request.OperationId, applied));
            },
        };
        var writer = Writer(rpc, initial);
        var patch = new SettingsPatch(TaskAlerts: initial.Settings.Desired.TaskAlerts with { Bank = 7 });

        var result = await writer.ApplyAsync(4, patch, operationId, CancellationToken.None);

        Assert.Equal(RuntimeSettingsWriteOutcome.Applied, result.Outcome);
        Assert.Equal(operationId, result.OperationId);
        Assert.Equal(applied.Settings, result.Snapshot);
        Assert.Equal(4, preparedRequest?.BaseRevision);
        Assert.Same(patch, preparedRequest?.Patch);
        Assert.Equal(operationId, appliedRequest?.OperationId);
        Assert.Equal("prepared-token", appliedRequest?.PreparationToken);
        Assert.Empty(rpc.Lookups);
    }

    [Fact]
    public async Task LostApplyReplyRecoversExactOperationWithoutResubmitting()
    {
        var operationId = Guid.NewGuid();
        var initial = Snapshot(revision: 8);
        var applied = Snapshot(revision: 9);
        var rpc = new FakeRpc
        {
            Prepare = (_, _) => Task.FromResult(Prepared(initial)),
            Apply = (_, _) => throw new IOException("reply lost"),
            Lookup = (requestedOperationId, _) => Task.FromResult(new SettingsOperationResult(
                requestedOperationId,
                SettingsOperationState.Completed,
                Applied(requestedOperationId, applied))),
        };

        var result = await Writer(rpc, initial).ApplyAsync(
            8,
            new SettingsPatch(TaskAlerts: initial.Settings.Desired.TaskAlerts with { Bank = 2 }),
            operationId,
            CancellationToken.None);

        Assert.Equal(RuntimeSettingsWriteOutcome.Applied, result.Outcome);
        Assert.Equal(1, rpc.ApplyCount);
        Assert.Equal([operationId], rpc.Lookups);
    }

    [Fact]
    public async Task MissingOperationAfterLostReplyRemainsUncertain()
    {
        var operationId = Guid.NewGuid();
        var initial = Snapshot(revision: 11);
        var rpc = new FakeRpc
        {
            Prepare = (_, _) => Task.FromResult(Prepared(initial)),
            Apply = (_, _) => throw new TimeoutException("reply lost"),
            Lookup = (requestedOperationId, _) => Task.FromResult(new SettingsOperationResult(
                requestedOperationId,
                SettingsOperationState.NotFound)),
        };

        var result = await Writer(rpc, initial).ApplyAsync(
            11,
            new SettingsPatch(TaskAlerts: initial.Settings.Desired.TaskAlerts with { Bank = 3 }),
            operationId,
            CancellationToken.None);

        Assert.Equal(RuntimeSettingsWriteOutcome.Uncertain, result.Outcome);
        Assert.Contains("not resubmitted", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, rpc.ApplyCount);
        Assert.Equal([operationId], rpc.Lookups);
    }

    [Fact]
    public async Task RecoverReportsRunningOperationWithoutSubmittingAnything()
    {
        var operationId = Guid.NewGuid();
        var initial = Snapshot(revision: 2);
        var rpc = new FakeRpc
        {
            Lookup = (requestedOperationId, _) => Task.FromResult(new SettingsOperationResult(
                requestedOperationId,
                SettingsOperationState.Running)),
        };

        var result = await Writer(rpc, initial).RecoverAsync(operationId, CancellationToken.None);

        Assert.Equal(RuntimeSettingsWriteOutcome.Running, result.Outcome);
        Assert.Equal(0, rpc.PrepareCount);
        Assert.Equal(0, rpc.ApplyCount);
        Assert.Equal([operationId], rpc.Lookups);
    }

    [Fact]
    public async Task ConnectionChangeWhilePrepareIsInFlightCannotApplyToReplacementEditor()
    {
        var initial = Snapshot(revision: 1);
        var prepared = new TaskCompletionSource<PrepareSettingsResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var current = true;
        var rpc = new FakeRpc
        {
            Prepare = (_, _) => prepared.Task,
        };
        var writer = Writer(rpc, initial, () => current);
        var write = writer.ApplyAsync(
            1,
            new SettingsPatch(TaskAlerts: initial.Settings.Desired.TaskAlerts with { Bank = 4 }),
            Guid.NewGuid(),
            CancellationToken.None);

        current = false;
        prepared.SetResult(Prepared(initial));

        await Assert.ThrowsAsync<OperationCanceledException>(() => write);
        Assert.Equal(0, rpc.ApplyCount);
        Assert.Empty(rpc.Lookups);
    }

    [Fact]
    public async Task ConnectionChangeWhileApplyIsInFlightCannotReconcileIntoReplacementEditor()
    {
        var initial = Snapshot(revision: 1);
        var apply = new TaskCompletionSource<ApplySettingsResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var current = true;
        var operationId = Guid.NewGuid();
        var rpc = new FakeRpc
        {
            Prepare = (_, _) => Task.FromResult(Prepared(initial)),
            Apply = (_, _) => apply.Task,
        };
        var writer = Writer(rpc, initial, () => current);
        var write = writer.ApplyAsync(
            1,
            new SettingsPatch(TaskAlerts: initial.Settings.Desired.TaskAlerts with { Bank = 5 }),
            operationId,
            CancellationToken.None);

        current = false;
        apply.SetResult(Applied(operationId, Snapshot(revision: 2)));

        await Assert.ThrowsAsync<OperationCanceledException>(() => write);
        Assert.Empty(rpc.Lookups);
    }

    [Fact]
    public async Task EngineChangeBlocksFallbackSnapshotAfterFailedPrepare()
    {
        var initial = Snapshot(revision: 3);
        var replacement = Snapshot(revision: 4);
        var rpc = new FakeRpc
        {
            Prepare = (_, _) => throw new IOException("connection failed"),
        };
        var writer = new RuntimeSettingsWriter(
            rpc,
            initial.EngineEpoch,
            () => true,
            () => replacement);

        await Assert.ThrowsAsync<OperationCanceledException>(() => writer.ApplyAsync(
            3,
            new SettingsPatch(TaskAlerts: initial.Settings.Desired.TaskAlerts with { Bank = 6 }),
            Guid.NewGuid(),
            CancellationToken.None));
    }

    [Fact]
    public async Task AuthorityStateUnrecordedIsNeverReportedAsAConfirmedFailure()
    {
        var operationId = Guid.NewGuid();
        var initial = Snapshot(revision: 6);
        var rpc = new FakeRpc
        {
            Prepare = (_, _) => Task.FromResult(Prepared(initial)),
            Apply = (request, _) => Task.FromResult(new ApplySettingsResult(
                request.OperationId,
                SettingsApplyStatus.AuthorityStateUnrecorded,
                DesiredStateCommitted: false,
                CanCloseSettings: false,
                [],
                ["authority write outcome unknown"],
                initial.Settings)),
        };

        var result = await Writer(rpc, initial).ApplyAsync(
            6,
            new SettingsPatch(TaskAlerts: initial.Settings.Desired.TaskAlerts with { Bank = 8 }),
            operationId,
            CancellationToken.None);

        Assert.Equal(RuntimeSettingsWriteOutcome.Uncertain, result.Outcome);
        Assert.Equal(
            SettingsApplyStatus.AuthorityStateUnrecorded,
            Assert.IsType<ApplySettingsResult>(result.ApplyResult).Status);
    }

    private static RuntimeSettingsWriter Writer(
        FakeRpc rpc,
        RuntimeSnapshot snapshot,
        Func<bool>? isCurrent = null) => new(
            rpc,
            snapshot.EngineEpoch,
            isCurrent ?? (() => true),
            () => snapshot);

    private static PrepareSettingsResult Prepared(RuntimeSnapshot snapshot) => new(
        SettingsPrepareStatus.Prepared,
        "prepared-token",
        "payload-hash",
        DateTimeOffset.UtcNow.AddMinutes(1),
        [],
        [],
        snapshot.Settings);

    private static ApplySettingsResult Applied(Guid operationId, RuntimeSnapshot snapshot) => new(
        operationId,
        SettingsApplyStatus.Applied,
        DesiredStateCommitted: true,
        CanCloseSettings: true,
        [],
        [],
        snapshot.Settings);

    private static RuntimeSnapshot Snapshot(long revision)
    {
        var settings = new SettingsBundle(
            CompanionConfig.CreateSafeDefault(),
            VoicePePreferences.Default,
            PebbleIndexPreferences.Default,
            TaskAlertPreferences.Default);
        return new RuntimeSnapshot(
            Guid.NewGuid(),
            EventCursor: 0,
            new RuntimeIdentitySnapshot(1, RuntimeInstanceKind.Synthetic, "test-root", 1, []),
            new SettingsSnapshot(revision, settings, settings, [], []),
            new RuntimeInputSnapshot([], []),
            Ui: null,
            InputEventCursor: 0);
    }

    private sealed class FakeRpc : IRuntimeRpcServer
    {
        public Func<PrepareSettingsRequest, CancellationToken, Task<PrepareSettingsResult>> Prepare { get; init; } =
            (_, _) => throw new NotSupportedException();

        public Func<ApplySettingsRequest, CancellationToken, Task<ApplySettingsResult>> Apply { get; init; } =
            (_, _) => throw new NotSupportedException();

        public Func<Guid, CancellationToken, Task<SettingsOperationResult>> Lookup { get; init; } =
            (_, _) => throw new NotSupportedException();

        public int PrepareCount { get; private set; }

        public int ApplyCount { get; private set; }

        public List<Guid> Lookups { get; } = [];

        public Task<PrepareSettingsResult> PrepareSettingsAsync(
            PrepareSettingsRequest request,
            CancellationToken cancellationToken)
        {
            PrepareCount++;
            return Prepare(request, cancellationToken);
        }

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

        public Task<RuntimeCommandResult> ExecuteCommandAsync(
            RuntimeCommandRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RuntimeCommandOperationResult> GetCommandOperationAsync(
            Guid operationId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
