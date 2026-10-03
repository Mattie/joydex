extern alias RuntimeHost;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Joydex.Contracts;
using Joydex.Core.Config;
using Joydex.Core.TaskAlerts;
using Joydex.Core.Voice;
using RuntimeHost::Joydex.RuntimeHost;
using RuntimeHost::Joydex.RuntimeHost.Settings;

namespace Joydex.Tests;

public sealed class RuntimeSettingsCoordinatorTests
{
    [Fact]
    public async Task ApplyCommitsNormalizedSettingsAndReplaysTheBoundOperation()
    {
        using var store = new TemporarySettingsStore();
        await using var coordinator = store.CreateCoordinator();
        using var client = await AttachAsync(coordinator, "settings-1");

        var prepared = await coordinator.PrepareAsync(
            client.ConnectionId,
            new PrepareSettingsRequest(
                client.State.Snapshot.Revision,
                new SettingsPatch(TaskAlerts: client.State.Snapshot.Desired.TaskAlerts with { Bank = 99 })),
            CancellationToken.None);

        Assert.Equal(SettingsPrepareStatus.Prepared, prepared.Status);
        Assert.NotNull(prepared.PreparationToken);
        Assert.NotNull(prepared.PayloadHash);
        Assert.Single(prepared.Effects);

        var operationId = Guid.NewGuid();
        var request = new ApplySettingsRequest(operationId, prepared.PreparationToken!);
        var applied = await coordinator.ApplyAsync(client.ConnectionId, request, CancellationToken.None);

        Assert.Equal(SettingsApplyStatus.Applied, applied.Status);
        Assert.True(applied.DesiredStateCommitted);
        Assert.True(applied.CanCloseSettings);
        Assert.Equal(2, applied.Snapshot.Revision);
        Assert.Equal(5, applied.Snapshot.Desired.TaskAlerts.Bank);
        Assert.Equal(5, applied.Snapshot.Active.TaskAlerts.Bank);
        var aggregate = Assert.Single(applied.Aggregates);
        Assert.Equal(SettingsPersistenceStatus.Committed, aggregate.Persistence);
        Assert.Equal(SettingsActivationState.Applied, aggregate.Activation);

        var replay = await coordinator.ApplyAsync(client.ConnectionId, request, CancellationToken.None);
        AssertSameOutcome(applied, replay);

        var operation = await coordinator.GetOperationAsync(
            client.ConnectionId,
            operationId,
            CancellationToken.None);
        Assert.Equal(SettingsOperationState.Completed, operation.State);
        AssertSameOutcome(applied, operation.Result!);
        Assert.Equal(5, TaskAlertPreferencesStore.LoadOrCreate(store.Paths.TaskAlerts).Bank);
    }

    [Fact]
    public async Task CompletedOutcomeReplaysWithTheCurrentSnapshotAfterLaterApplyAndRestart()
    {
        using var store = new TemporarySettingsStore();
        ApplySettingsResult firstResult;
        ApplySettingsRequest firstRequest;
        await using (var coordinator = store.CreateCoordinator())
        using (var client = await AttachAsync(coordinator, "settings-1"))
        {
            var first = await PrepareTaskAlertBankAsync(coordinator, client, 3);
            firstRequest = new ApplySettingsRequest(Guid.NewGuid(), first.PreparationToken);
            firstResult = await coordinator.ApplyAsync(
                client.ConnectionId,
                firstRequest,
                CancellationToken.None);
            var second = await coordinator.PrepareAsync(
                client.ConnectionId,
                new PrepareSettingsRequest(
                    firstResult.Snapshot.Revision,
                    new SettingsPatch(TaskAlerts: firstResult.Snapshot.Desired.TaskAlerts with { Bank = 4 })),
                CancellationToken.None);
            Assert.Equal(SettingsPrepareStatus.Prepared, second.Status);
            var secondResult = await coordinator.ApplyAsync(
                client.ConnectionId,
                new ApplySettingsRequest(Guid.NewGuid(), second.PreparationToken!),
                CancellationToken.None);

            var replay = await coordinator.ApplyAsync(
                client.ConnectionId,
                firstRequest,
                CancellationToken.None);
            AssertSameOutcome(firstResult, replay);
            Assert.Equal(secondResult.Snapshot.Revision, replay.Snapshot.Revision);
            Assert.Equal(4, replay.Snapshot.Desired.TaskAlerts.Bank);
        }

        await using var recovered = store.CreateCoordinator();
        using var recoveredClient = await AttachAsync(recovered, "settings-2");
        var lookup = await recovered.GetOperationAsync(
            recoveredClient.ConnectionId,
            firstRequest.OperationId,
            CancellationToken.None);
        Assert.Equal(SettingsOperationState.Completed, lookup.State);
        AssertSameOutcome(firstResult, lookup.Result!);
        Assert.Equal(recoveredClient.State.Snapshot.Revision, lookup.Result!.Snapshot.Revision);
        Assert.Equal(4, lookup.Result.Snapshot.Desired.TaskAlerts.Bank);
    }

    [Fact]
    public async Task BoundedReplayRetainsNewestSameRevisionCompletionAcrossRestart()
    {
        using var store = new TemporarySettingsStore();
        var coordinator = store.CreateCoordinator();
        var client = await AttachAsync(coordinator, "settings-1");
        var preparations = new List<PreparedClientChange>();
        for (var index = 0; index < 65; index++)
        {
            preparations.Add(await PrepareTaskAlertBankAsync(coordinator, client, 4));
        }

        var oldestOperationId = new Guid(1_000, 0, 0, new byte[8]);
        var applied = await coordinator.ApplyAsync(
            client.ConnectionId,
            new ApplySettingsRequest(oldestOperationId, preparations[0].PreparationToken),
            CancellationToken.None);
        Assert.Equal(SettingsApplyStatus.Applied, applied.Status);

        var conflictOperationIds = new List<Guid>();
        ApplySettingsResult? newest = null;
        for (var index = 1; index < preparations.Count; index++)
        {
            var operationId = index == preparations.Count - 1
                ? new Guid(1, 0, 0, new byte[8])
                : new Guid(1_000 + index, 0, 0, new byte[8]);
            conflictOperationIds.Add(operationId);
            newest = await coordinator.ApplyAsync(
                client.ConnectionId,
                new ApplySettingsRequest(operationId, preparations[index].PreparationToken),
                CancellationToken.None);
            Assert.Equal(SettingsApplyStatus.Conflict, newest.Status);
        }

        var newestOperationId = conflictOperationIds[^1];
        Assert.Equal(newestOperationId, newest?.OperationId);
        Assert.Equal(
            SettingsOperationState.Completed,
            (await coordinator.GetOperationAsync(
                client.ConnectionId,
                newestOperationId,
                CancellationToken.None)).State);
        Assert.Equal(
            SettingsOperationState.NotFound,
            (await coordinator.GetOperationAsync(
                client.ConnectionId,
                oldestOperationId,
                CancellationToken.None)).State);

        client.Dispose();
        await coordinator.DisposeAsync();

        await using var recovered = store.CreateCoordinator();
        using var recoveredClient = await AttachAsync(recovered, "settings-2");
        Assert.Equal(
            SettingsOperationState.Completed,
            (await recovered.GetOperationAsync(
                recoveredClient.ConnectionId,
                newestOperationId,
                CancellationToken.None)).State);
        Assert.Equal(
            SettingsOperationState.NotFound,
            (await recovered.GetOperationAsync(
                recoveredClient.ConnectionId,
                oldestOperationId,
                CancellationToken.None)).State);

        var afterRestart = await PrepareTaskAlertBankAsync(recovered, recoveredClient, 3);
        var afterRestartOperationId = new Guid(2, 0, 0, new byte[8]);
        var afterRestartResult = await recovered.ApplyAsync(
            recoveredClient.ConnectionId,
            new ApplySettingsRequest(afterRestartOperationId, afterRestart.PreparationToken),
            CancellationToken.None);
        Assert.Equal(SettingsApplyStatus.Applied, afterRestartResult.Status);
        Assert.Equal(
            SettingsOperationState.NotFound,
            (await recovered.GetOperationAsync(
                recoveredClient.ConnectionId,
                conflictOperationIds[0],
                CancellationToken.None)).State);
        Assert.Equal(
            SettingsOperationState.Completed,
            (await recovered.GetOperationAsync(
                recoveredClient.ConnectionId,
                newestOperationId,
                CancellationToken.None)).State);
        Assert.Equal(
            SettingsOperationState.Completed,
            (await recovered.GetOperationAsync(
                recoveredClient.ConnectionId,
                afterRestartOperationId,
                CancellationToken.None)).State);
    }

    [Fact]
    public async Task LegacyNestedSchemaOneOutcomeFailsClosed()
    {
        using var store = new TemporarySettingsStore();
        ApplySettingsResult applied;
        await using (var coordinator = store.CreateCoordinator())
        using (var client = await AttachAsync(coordinator, "settings-1"))
        {
            var prepared = await PrepareTaskAlertBankAsync(coordinator, client, 3);
            applied = await coordinator.ApplyAsync(
                client.ConnectionId,
                new ApplySettingsRequest(Guid.NewGuid(), prepared.PreparationToken),
                CancellationToken.None);
            Assert.Equal(SettingsApplyStatus.Applied, applied.Status);
        }

        var journal = JsonNode.Parse(await File.ReadAllTextAsync(store.Paths.Journal))!.AsObject();
        var compactOperation = journal["completedOperations"]!.AsArray()[0]!.AsObject();
        journal["schemaVersion"] = 1;
        journal.Remove("selectionIdentity");
        journal.Remove("lastCompletionSequence");
        journal["completedOperations"] = new JsonArray(new JsonObject
        {
            ["operationId"] = compactOperation["operationId"]!.DeepClone(),
            ["tokenHash"] = compactOperation["tokenHash"]!.DeepClone(),
            ["payloadHash"] = compactOperation["payloadHash"]!.DeepClone(),
            ["result"] = JsonSerializer.SerializeToNode(applied, LegacyJournalJsonOptions),
        });
        await File.WriteAllTextAsync(
            store.Paths.Journal,
            journal.ToJsonString(LegacyJournalJsonOptions));

        var error = Assert.Throws<InvalidDataException>(() => store.CreateCoordinator());
        Assert.Contains("not valid JSON", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyRejectsAnOperationIdReusedWithAnotherPreparedToken()
    {
        using var store = new TemporarySettingsStore();
        await using var coordinator = store.CreateCoordinator();
        using var client = await AttachAsync(coordinator, "settings-1");

        var first = await PrepareTaskAlertBankAsync(coordinator, client, 3);
        var operationId = Guid.NewGuid();
        var applied = await coordinator.ApplyAsync(
            client.ConnectionId,
            new ApplySettingsRequest(operationId, first.PreparationToken!),
            CancellationToken.None);
        Assert.Equal(SettingsApplyStatus.Applied, applied.Status);

        var second = await coordinator.PrepareAsync(
            client.ConnectionId,
            new PrepareSettingsRequest(
                applied.Snapshot.Revision,
                new SettingsPatch(TaskAlerts: applied.Snapshot.Desired.TaskAlerts with { Bank = 4 })),
            CancellationToken.None);
        Assert.Equal(SettingsPrepareStatus.Prepared, second.Status);

        var rejected = await coordinator.ApplyAsync(
            client.ConnectionId,
            new ApplySettingsRequest(operationId, second.PreparationToken!),
            CancellationToken.None);

        Assert.Equal(SettingsApplyStatus.Rejected, rejected.Status);
        Assert.False(rejected.DesiredStateCommitted);
        Assert.Equal(3, rejected.Snapshot.Desired.TaskAlerts.Bank);
        Assert.Equal(3, TaskAlertPreferencesStore.LoadOrCreate(store.Paths.TaskAlerts).Bank);
    }

    [Fact]
    public async Task ExternalEditAfterPrepareConflictsWithoutOverwritingTheFile()
    {
        using var store = new TemporarySettingsStore();
        await using var coordinator = store.CreateCoordinator();
        using var client = await AttachAsync(coordinator, "settings-1");
        var prepared = await PrepareTaskAlertBankAsync(coordinator, client, 4);

        TaskAlertPreferencesStore.Save(
            store.Paths.TaskAlerts,
            client.State.Snapshot.Desired.TaskAlerts with { Bank = 5 });
        var externalBytes = File.ReadAllBytes(store.Paths.TaskAlerts);

        var result = await coordinator.ApplyAsync(
            client.ConnectionId,
            new ApplySettingsRequest(Guid.NewGuid(), prepared.PreparationToken!),
            CancellationToken.None);

        Assert.Equal(SettingsApplyStatus.Conflict, result.Status);
        Assert.False(result.DesiredStateCommitted);
        Assert.False(result.CanCloseSettings);
        Assert.Equal(client.State.Snapshot.Desired.TaskAlerts.Bank, result.Snapshot.Desired.TaskAlerts.Bank);
        Assert.Equal(externalBytes, File.ReadAllBytes(store.Paths.TaskAlerts));
        var candidate = Assert.Single(result.Snapshot.ExternalCandidates);
        Assert.Equal(SettingsAggregateId.TaskAlerts, candidate.Aggregate);
        Assert.True(candidate.IsValid);
    }

    [Fact]
    public async Task InvalidExternalEditBecomesAConflictCandidateInsteadOfBreakingApply()
    {
        using var store = new TemporarySettingsStore();
        await using var coordinator = store.CreateCoordinator();
        using var client = await AttachAsync(coordinator, "settings-1");
        var prepared = await PrepareTaskAlertBankAsync(coordinator, client, 4);
        await File.WriteAllTextAsync(store.Paths.TaskAlerts, "{ invalid json");
        var externalBytes = File.ReadAllBytes(store.Paths.TaskAlerts);

        var result = await coordinator.ApplyAsync(
            client.ConnectionId,
            new ApplySettingsRequest(Guid.NewGuid(), prepared.PreparationToken!),
            CancellationToken.None);

        Assert.Equal(SettingsApplyStatus.Conflict, result.Status);
        Assert.Equal(externalBytes, File.ReadAllBytes(store.Paths.TaskAlerts));
        var candidate = Assert.Single(result.Snapshot.ExternalCandidates);
        Assert.False(candidate.IsValid);
        Assert.False(string.IsNullOrWhiteSpace(candidate.Detail));
    }

    [Fact]
    public async Task MalformedNestedClientValueIsRejectedWithoutChangingState()
    {
        using var store = new TemporarySettingsStore();
        await using var coordinator = store.CreateCoordinator();
        using var client = await AttachAsync(coordinator, "settings-1");
        var malformed = client.State.Snapshot.Desired.Voice with { PinnedTaskLabel = null! };

        var result = await coordinator.PrepareAsync(
            client.ConnectionId,
            new PrepareSettingsRequest(
                client.State.Snapshot.Revision,
                new SettingsPatch(Voice: malformed)),
            CancellationToken.None);

        Assert.Equal(SettingsPrepareStatus.Rejected, result.Status);
        Assert.Equal(1, result.Snapshot.Revision);
        Assert.Empty(result.Snapshot.ExternalCandidates);
        Assert.Equal(
            client.State.Snapshot.Desired.Voice,
            result.Snapshot.Desired.Voice);
    }

    [Fact]
    public async Task ExternalEditRacingApplyIsCaughtByTheFinalFingerprintCheck()
    {
        using var store = new TemporarySettingsStore();
        var io = new EditAfterReadsDocumentIo();
        await using var coordinator = store.CreateCoordinator(io: io);
        using var client = await AttachAsync(coordinator, "settings-1");
        var prepared = await PrepareTaskAlertBankAsync(coordinator, client, 4);
        io.EditAfterReads(9, () => TaskAlertPreferencesStore.Save(
            store.Paths.TaskAlerts,
            client.State.Snapshot.Desired.TaskAlerts with { Bank = 5 }));

        var result = await coordinator.ApplyAsync(
            client.ConnectionId,
            new ApplySettingsRequest(Guid.NewGuid(), prepared.PreparationToken!),
            CancellationToken.None);

        Assert.Equal(SettingsApplyStatus.Conflict, result.Status);
        Assert.False(result.DesiredStateCommitted);
        Assert.Equal(5, TaskAlertPreferencesStore.LoadOrCreate(store.Paths.TaskAlerts).Bank);
        Assert.Equal(2, result.Snapshot.Desired.TaskAlerts.Bank);
        Assert.Contains(result.Errors, error => error.Contains("commit snapshot", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StaleClientCannotPrepareOverANewerCommittedRevision()
    {
        using var store = new TemporarySettingsStore();
        await using var coordinator = store.CreateCoordinator();
        using var first = await AttachAsync(coordinator, "settings-1");
        using var stale = await AttachAsync(coordinator, "settings-2");
        var prepared = await PrepareTaskAlertBankAsync(coordinator, first, 3);
        var applied = await coordinator.ApplyAsync(
            first.ConnectionId,
            new ApplySettingsRequest(Guid.NewGuid(), prepared.PreparationToken!),
            CancellationToken.None);
        Assert.Equal(SettingsApplyStatus.Applied, applied.Status);

        var conflict = await coordinator.PrepareAsync(
            stale.ConnectionId,
            new PrepareSettingsRequest(
                stale.State.Snapshot.Revision,
                new SettingsPatch(TaskAlerts: stale.State.Snapshot.Desired.TaskAlerts with { Bank = 4 })),
            CancellationToken.None);

        Assert.Equal(SettingsPrepareStatus.Conflict, conflict.Status);
        Assert.Equal(2, conflict.Snapshot.Revision);
        Assert.Equal(3, conflict.Snapshot.Desired.TaskAlerts.Bank);
        Assert.Equal(3, TaskAlertPreferencesStore.LoadOrCreate(store.Paths.TaskAlerts).Bank);
    }

    [Fact]
    public async Task ConflictCompletionJournalFailureBlocksMutationAndIsAbsentAfterRestart()
    {
        using var store = new TemporarySettingsStore();
        var io = new FailOnceDocumentIo();
        var coordinator = store.CreateCoordinator(io: io);
        var first = await AttachAsync(coordinator, "settings-1");
        var stale = await AttachAsync(coordinator, "settings-2");
        var stalePrepared = await PrepareTaskAlertBankAsync(coordinator, stale, 4);
        var currentPrepared = await PrepareTaskAlertBankAsync(coordinator, first, 3);
        var current = await coordinator.ApplyAsync(
            first.ConnectionId,
            new ApplySettingsRequest(Guid.NewGuid(), currentPrepared.PreparationToken),
            CancellationToken.None);
        Assert.Equal(SettingsApplyStatus.Applied, current.Status);
        var conflictOperationId = Guid.NewGuid();
        io.FailNextReplace(store.Paths.Journal);

        var conflict = await coordinator.ApplyAsync(
            stale.ConnectionId,
            new ApplySettingsRequest(conflictOperationId, stalePrepared.PreparationToken),
            CancellationToken.None);

        Assert.Equal(SettingsApplyStatus.AuthorityStateUnrecorded, conflict.Status);
        Assert.False(conflict.DesiredStateCommitted);
        Assert.False(conflict.CanCloseSettings);
        Assert.Equal(SettingsPersistenceStatus.NotAttempted, Assert.Single(conflict.Aggregates).Persistence);
        Assert.Equal(3, conflict.Snapshot.Desired.TaskAlerts.Bank);
        Assert.Equal(3, conflict.Snapshot.Active.TaskAlerts.Bank);
        Assert.Contains(conflict.Errors, error => error.Contains("could not be recorded", StringComparison.OrdinalIgnoreCase));

        var replay = await coordinator.ApplyAsync(
            stale.ConnectionId,
            new ApplySettingsRequest(conflictOperationId, stalePrepared.PreparationToken),
            CancellationToken.None);
        AssertSameOutcome(conflict, replay);
        var blocked = await coordinator.PrepareAsync(
            first.ConnectionId,
            new PrepareSettingsRequest(
                conflict.Snapshot.Revision,
                new SettingsPatch(TaskAlerts: conflict.Snapshot.Desired.TaskAlerts with { Bank = 5 })),
            CancellationToken.None);
        Assert.Equal(SettingsPrepareStatus.Rejected, blocked.Status);

        first.Dispose();
        stale.Dispose();
        await coordinator.DisposeAsync();

        await using var recovered = store.CreateCoordinator();
        using var recoveredClient = await AttachAsync(recovered, "settings-3");
        Assert.Equal(3, recoveredClient.State.Snapshot.Desired.TaskAlerts.Bank);
        Assert.Equal(3, recoveredClient.State.Snapshot.Active.TaskAlerts.Bank);
        var lookup = await recovered.GetOperationAsync(
            recoveredClient.ConnectionId,
            conflictOperationId,
            CancellationToken.None);
        Assert.Equal(SettingsOperationState.NotFound, lookup.State);
    }

    [Fact]
    public async Task HandledPersistenceFailureRestoresEveryPriorFileByte()
    {
        using var store = new TemporarySettingsStore();
        var io = new FailOnceDocumentIo();
        await using var coordinator = store.CreateCoordinator(io: io);
        using var client = await AttachAsync(coordinator, "settings-1");
        var previousVoice = File.ReadAllBytes(store.Paths.Voice);
        var previousTaskAlerts = File.ReadAllBytes(store.Paths.TaskAlerts);
        var prepared = await coordinator.PrepareAsync(
            client.ConnectionId,
            new PrepareSettingsRequest(
                client.State.Snapshot.Revision,
                new SettingsPatch(
                    Voice: client.State.Snapshot.Desired.Voice with { PinnedTaskLabel = "changed" },
                    TaskAlerts: client.State.Snapshot.Desired.TaskAlerts with { Bank = 4 })),
            CancellationToken.None);
        Assert.Equal(SettingsPrepareStatus.Prepared, prepared.Status);
        io.FailNextReplace(store.Paths.TaskAlerts);

        var operationId = Guid.NewGuid();
        var result = await coordinator.ApplyAsync(
            client.ConnectionId,
            new ApplySettingsRequest(operationId, prepared.PreparationToken!),
            CancellationToken.None);

        Assert.Equal(SettingsApplyStatus.FailedRolledBack, result.Status);
        Assert.False(result.DesiredStateCommitted);
        Assert.Equal(1, result.Snapshot.Revision);
        Assert.Equal(previousVoice, File.ReadAllBytes(store.Paths.Voice));
        Assert.Equal(previousTaskAlerts, File.ReadAllBytes(store.Paths.TaskAlerts));

        var replay = await coordinator.ApplyAsync(
            client.ConnectionId,
            new ApplySettingsRequest(operationId, prepared.PreparationToken!),
            CancellationToken.None);
        AssertSameOutcome(result, replay);
    }

    [Fact]
    public async Task RolledBackCompletionJournalFailureBlocksMutationAndRecoversOutcomeOnRestart()
    {
        using var store = new TemporarySettingsStore();
        var io = new FailOnceDocumentIo();
        var coordinator = store.CreateCoordinator(io: io);
        var client = await AttachAsync(coordinator, "settings-1");
        var priorBytes = File.ReadAllBytes(store.Paths.TaskAlerts);
        var prepared = await PrepareTaskAlertBankAsync(coordinator, client, 4);
        var operationId = Guid.NewGuid();
        io.FailNextReplace(store.Paths.TaskAlerts);
        io.FailNextReplace(store.Paths.Journal);

        var result = await coordinator.ApplyAsync(
            client.ConnectionId,
            new ApplySettingsRequest(operationId, prepared.PreparationToken),
            CancellationToken.None);

        Assert.Equal(SettingsApplyStatus.AuthorityStateUnrecorded, result.Status);
        Assert.False(result.DesiredStateCommitted);
        Assert.False(result.CanCloseSettings);
        Assert.Equal(SettingsPersistenceStatus.RolledBack, Assert.Single(result.Aggregates).Persistence);
        Assert.Equal(1, result.Snapshot.Revision);
        Assert.Equal(2, result.Snapshot.Desired.TaskAlerts.Bank);
        Assert.Equal(2, result.Snapshot.Active.TaskAlerts.Bank);
        Assert.Equal(priorBytes, File.ReadAllBytes(store.Paths.TaskAlerts));
        Assert.Contains(result.Errors, error => error.Contains("could not be recorded", StringComparison.OrdinalIgnoreCase));

        var blocked = await coordinator.PrepareAsync(
            client.ConnectionId,
            new PrepareSettingsRequest(
                result.Snapshot.Revision,
                new SettingsPatch(TaskAlerts: result.Snapshot.Desired.TaskAlerts with { Bank = 3 })),
            CancellationToken.None);
        Assert.Equal(SettingsPrepareStatus.Rejected, blocked.Status);

        client.Dispose();
        await coordinator.DisposeAsync();

        await using var recovered = store.CreateCoordinator();
        using var recoveredClient = await AttachAsync(recovered, "settings-2");
        Assert.Equal(1, recoveredClient.State.Snapshot.Revision);
        Assert.Equal(2, recoveredClient.State.Snapshot.Desired.TaskAlerts.Bank);
        Assert.Equal(2, recoveredClient.State.Snapshot.Active.TaskAlerts.Bank);
        Assert.Equal(priorBytes, File.ReadAllBytes(store.Paths.TaskAlerts));
        var recoveredOperation = await recovered.GetOperationAsync(
            recoveredClient.ConnectionId,
            operationId,
            CancellationToken.None);
        Assert.Equal(SettingsOperationState.Completed, recoveredOperation.State);
        Assert.Equal(SettingsApplyStatus.FailedRolledBack, recoveredOperation.Result?.Status);
        Assert.False(recoveredOperation.Result?.DesiredStateCommitted);
    }

    [Fact]
    public async Task FailureToPersistTheDesiredCommitMarkerRollsBackFilesAndMemory()
    {
        using var store = new TemporarySettingsStore();
        var io = new FailOnceDocumentIo();
        await using var coordinator = store.CreateCoordinator(io: io);
        using var client = await AttachAsync(coordinator, "settings-1");
        var priorBytes = File.ReadAllBytes(store.Paths.TaskAlerts);
        var prepared = await PrepareTaskAlertBankAsync(coordinator, client, 4);
        io.FailReplace(store.Paths.Journal, occurrence: 2);

        var result = await coordinator.ApplyAsync(
            client.ConnectionId,
            new ApplySettingsRequest(Guid.NewGuid(), prepared.PreparationToken!),
            CancellationToken.None);

        Assert.Equal(SettingsApplyStatus.FailedRolledBack, result.Status);
        Assert.False(result.DesiredStateCommitted);
        Assert.Equal(1, result.Snapshot.Revision);
        Assert.Equal(2, result.Snapshot.Desired.TaskAlerts.Bank);
        Assert.Equal(2, result.Snapshot.Active.TaskAlerts.Bank);
        Assert.Equal(priorBytes, File.ReadAllBytes(store.Paths.TaskAlerts));
    }

    [Fact]
    public async Task FinalApplyJournalFailureReportsLiveStateBlocksMutationAndRecoversOnRestart()
    {
        using var store = new TemporarySettingsStore();
        var io = new FailOnceDocumentIo();
        var coordinator = store.CreateCoordinator(io: io);
        var client = await AttachAsync(coordinator, "settings-1");
        var prepared = await PrepareTaskAlertBankAsync(coordinator, client, 4);
        var operationId = Guid.NewGuid();
        io.FailReplace(store.Paths.Journal, occurrence: 3);

        var result = await coordinator.ApplyAsync(
            client.ConnectionId,
            new ApplySettingsRequest(operationId, prepared.PreparationToken),
            CancellationToken.None);

        Assert.Equal(SettingsApplyStatus.AuthorityStateUnrecorded, result.Status);
        Assert.True(result.DesiredStateCommitted);
        Assert.False(result.CanCloseSettings);
        Assert.Equal(2, result.Snapshot.Revision);
        Assert.Equal(4, result.Snapshot.Desired.TaskAlerts.Bank);
        Assert.Equal(4, result.Snapshot.Active.TaskAlerts.Bank);
        var aggregate = Assert.Single(result.Aggregates);
        Assert.Equal(SettingsPersistenceStatus.Committed, aggregate.Persistence);
        Assert.Equal(SettingsActivationState.Applied, aggregate.Activation);
        Assert.Contains(result.Errors, error => error.Contains("could not be recorded", StringComparison.OrdinalIgnoreCase));

        var blocked = await coordinator.PrepareAsync(
            client.ConnectionId,
            new PrepareSettingsRequest(
                result.Snapshot.Revision,
                new SettingsPatch(TaskAlerts: result.Snapshot.Desired.TaskAlerts with { Bank = 3 })),
            CancellationToken.None);
        Assert.Equal(SettingsPrepareStatus.Rejected, blocked.Status);
        Assert.Contains(blocked.Errors, error => error.Contains("could not be recorded", StringComparison.OrdinalIgnoreCase));

        client.Dispose();
        await coordinator.DisposeAsync();

        await using var recovered = store.CreateCoordinator();
        using var recoveredClient = await AttachAsync(recovered, "settings-2");
        Assert.Equal(2, recoveredClient.State.Snapshot.Revision);
        Assert.Equal(4, recoveredClient.State.Snapshot.Desired.TaskAlerts.Bank);
        Assert.Equal(2, recoveredClient.State.Snapshot.Active.TaskAlerts.Bank);
        var recoveredState = Assert.Single(
            recoveredClient.State.Snapshot.Aggregates,
            item => item.Aggregate == SettingsAggregateId.TaskAlerts);
        Assert.Equal(SettingsActivationState.PendingIdle, recoveredState.Activation);

        var recoveredOperation = await recovered.GetOperationAsync(
            recoveredClient.ConnectionId,
            operationId,
            CancellationToken.None);
        Assert.Equal(SettingsApplyStatus.PendingIdle, recoveredOperation.Result?.Status);
        Assert.True(recoveredOperation.Result?.DesiredStateCommitted);

        var activated = await recovered.ActivateDesiredAsync(
            SettingsAggregateId.TaskAlerts,
            CancellationToken.None);
        Assert.Equal(SettingsActivationState.Applied, activated.State);
        var active = await recovered.GetSnapshotAsync(recoveredClient.ConnectionId, CancellationToken.None);
        Assert.Equal(4, active.Active.TaskAlerts.Bank);
    }

    [Fact]
    public async Task RestartRollsBackAnInterruptedDocumentWriteAndRecordsTheOperation()
    {
        using var store = new TemporarySettingsStore();
        var observer = new InterruptingObserver(SettingsTransactionCheckpoint.DocumentWritten);
        var coordinator = store.CreateCoordinator(observer: observer);
        using var firstClient = await AttachAsync(coordinator, "settings-1");
        var previousVoice = File.ReadAllBytes(store.Paths.Voice);
        var previousTaskAlerts = File.ReadAllBytes(store.Paths.TaskAlerts);
        var prepared = await coordinator.PrepareAsync(
            firstClient.ConnectionId,
            new PrepareSettingsRequest(
                firstClient.State.Snapshot.Revision,
                new SettingsPatch(
                    Voice: firstClient.State.Snapshot.Desired.Voice with { PinnedTaskLabel = "changed" },
                    TaskAlerts: firstClient.State.Snapshot.Desired.TaskAlerts with { Bank = 4 })),
            CancellationToken.None);
        var operationId = Guid.NewGuid();

        await Assert.ThrowsAsync<SettingsTransactionInterruptedException>(() => coordinator.ApplyAsync(
            firstClient.ConnectionId,
            new ApplySettingsRequest(operationId, prepared.PreparationToken!),
            CancellationToken.None));
        firstClient.Dispose();
        await coordinator.DisposeAsync();

        await using var recovered = store.CreateCoordinator();
        using var recoveredClient = await AttachAsync(recovered, "settings-2");
        Assert.Equal(previousVoice, File.ReadAllBytes(store.Paths.Voice));
        Assert.Equal(previousTaskAlerts, File.ReadAllBytes(store.Paths.TaskAlerts));
        Assert.Equal(1, recoveredClient.State.Snapshot.Revision);

        var operation = await recovered.GetOperationAsync(
            recoveredClient.ConnectionId,
            operationId,
            CancellationToken.None);
        Assert.Equal(SettingsOperationState.Completed, operation.State);
        Assert.Equal(SettingsApplyStatus.FailedRolledBack, operation.Result?.Status);
        Assert.False(operation.Result?.DesiredStateCommitted);

        var replay = await recovered.ApplyAsync(
            recoveredClient.ConnectionId,
            new ApplySettingsRequest(operationId, prepared.PreparationToken!),
            CancellationToken.None);
        Assert.Equal(SettingsApplyStatus.FailedRolledBack, replay.Status);
    }

    [Fact]
    public async Task RestartPreservesDesiredStateCommittedBeforeActivation()
    {
        using var store = new TemporarySettingsStore();
        var observer = new InterruptingObserver(SettingsTransactionCheckpoint.DesiredCommitted);
        var coordinator = store.CreateCoordinator(observer: observer);
        using var firstClient = await AttachAsync(coordinator, "settings-1");
        var prepared = await PrepareTaskAlertBankAsync(coordinator, firstClient, 4);
        var operationId = Guid.NewGuid();

        await Assert.ThrowsAsync<SettingsTransactionInterruptedException>(() => coordinator.ApplyAsync(
            firstClient.ConnectionId,
            new ApplySettingsRequest(operationId, prepared.PreparationToken!),
            CancellationToken.None));
        firstClient.Dispose();
        await coordinator.DisposeAsync();

        await using var recovered = store.CreateCoordinator();
        using var recoveredClient = await AttachAsync(recovered, "settings-2");
        var snapshot = recoveredClient.State.Snapshot;
        Assert.Equal(2, snapshot.Revision);
        Assert.Equal(4, snapshot.Desired.TaskAlerts.Bank);
        Assert.Equal(2, snapshot.Active.TaskAlerts.Bank);
        var state = Assert.Single(snapshot.Aggregates, item => item.Aggregate == SettingsAggregateId.TaskAlerts);
        Assert.Equal(2, state.DesiredRevision);
        Assert.Equal(1, state.ActiveRevision);
        Assert.Equal(SettingsActivationState.PendingIdle, state.Activation);

        var operation = await recovered.GetOperationAsync(
            recoveredClient.ConnectionId,
            operationId,
            CancellationToken.None);
        Assert.Equal(SettingsApplyStatus.PendingIdle, operation.Result?.Status);
        Assert.True(operation.Result?.DesiredStateCommitted);
        Assert.True(operation.Result?.CanCloseSettings);

        var replay = await recovered.ApplyAsync(
            recoveredClient.ConnectionId,
            new ApplySettingsRequest(operationId, prepared.PreparationToken!),
            CancellationToken.None);
        Assert.Equal(SettingsApplyStatus.PendingIdle, replay.Status);
    }

    [Fact]
    public async Task ActivationFailureKeepsDurableDesiredAndPriorActiveSettingsDistinct()
    {
        using var store = new TemporarySettingsStore();
        await using var coordinator = store.CreateCoordinator(
            activator: new FixedActivator(SettingsActivationState.Failed, "synthetic activation failure"));
        using var client = await AttachAsync(coordinator, "settings-1");
        var prepared = await PrepareTaskAlertBankAsync(coordinator, client, 4);

        var result = await coordinator.ApplyAsync(
            client.ConnectionId,
            new ApplySettingsRequest(Guid.NewGuid(), prepared.PreparationToken!),
            CancellationToken.None);

        Assert.Equal(SettingsApplyStatus.ActivationFailed, result.Status);
        Assert.True(result.DesiredStateCommitted);
        Assert.False(result.CanCloseSettings);
        Assert.Equal(4, result.Snapshot.Desired.TaskAlerts.Bank);
        Assert.Equal(2, result.Snapshot.Active.TaskAlerts.Bank);
        var aggregate = Assert.Single(result.Aggregates);
        Assert.Equal(SettingsPersistenceStatus.Committed, aggregate.Persistence);
        Assert.Equal(SettingsActivationState.Failed, aggregate.Activation);
    }

    [Fact]
    public async Task PendingIdleIsDurableAndAllowsTheSettingsClientToClose()
    {
        using var store = new TemporarySettingsStore();
        var activator = new CountingActivator();
        await using var coordinator = store.CreateCoordinator(
            planner: new FixedImpactPlanner(SettingsEffectKind.PendingIdle),
            activator: activator);
        using var client = await AttachAsync(coordinator, "settings-1");
        var prepared = await PrepareTaskAlertBankAsync(coordinator, client, 4);

        var result = await coordinator.ApplyAsync(
            client.ConnectionId,
            new ApplySettingsRequest(Guid.NewGuid(), prepared.PreparationToken!),
            CancellationToken.None);

        Assert.Equal(SettingsApplyStatus.PendingIdle, result.Status);
        Assert.True(result.DesiredStateCommitted);
        Assert.True(result.CanCloseSettings);
        Assert.Equal(4, result.Snapshot.Desired.TaskAlerts.Bank);
        Assert.Equal(2, result.Snapshot.Active.TaskAlerts.Bank);
        Assert.Equal(0, activator.CallCount);

        var activated = await coordinator.ActivateDesiredAsync(
            SettingsAggregateId.TaskAlerts,
            CancellationToken.None);
        Assert.Equal(SettingsActivationState.Applied, activated.State);
        Assert.Equal(1, activator.CallCount);
        var recovered = await coordinator.GetSnapshotAsync(client.ConnectionId, CancellationToken.None);
        Assert.Equal(4, recovered.Active.TaskAlerts.Bank);
        var state = Assert.Single(recovered.Aggregates, item => item.Aggregate == SettingsAggregateId.TaskAlerts);
        Assert.Equal(SettingsActivationState.Applied, state.Activation);
        Assert.Equal(state.DesiredRevision, state.ActiveRevision);
    }

    [Fact]
    public async Task ActivateDesiredJournalFailureReportsLiveStateBlocksMutationAndRecoversOnRestart()
    {
        using var store = new TemporarySettingsStore();
        var io = new FailOnceDocumentIo();
        var activator = new CountingActivator();
        var coordinator = store.CreateCoordinator(
            planner: new FixedImpactPlanner(SettingsEffectKind.PendingIdle),
            activator: activator,
            io: io);
        var client = await AttachAsync(coordinator, "settings-1");
        var prepared = await PrepareTaskAlertBankAsync(coordinator, client, 4);
        var applied = await coordinator.ApplyAsync(
            client.ConnectionId,
            new ApplySettingsRequest(Guid.NewGuid(), prepared.PreparationToken),
            CancellationToken.None);
        Assert.Equal(SettingsApplyStatus.PendingIdle, applied.Status);
        io.FailNextReplace(store.Paths.Journal);

        var activation = await coordinator.ActivateDesiredAsync(
            SettingsAggregateId.TaskAlerts,
            CancellationToken.None);

        Assert.Equal(SettingsActivationState.Applied, activation.State);
        Assert.Contains("could not be recorded", activation.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, activator.CallCount);
        var live = await coordinator.GetSnapshotAsync(client.ConnectionId, CancellationToken.None);
        Assert.Equal(4, live.Desired.TaskAlerts.Bank);
        Assert.Equal(4, live.Active.TaskAlerts.Bank);
        var liveState = Assert.Single(
            live.Aggregates,
            item => item.Aggregate == SettingsAggregateId.TaskAlerts);
        Assert.Equal(SettingsActivationState.Applied, liveState.Activation);
        Assert.Equal(liveState.DesiredRevision, liveState.ActiveRevision);

        var blocked = await coordinator.PrepareAsync(
            client.ConnectionId,
            new PrepareSettingsRequest(
                live.Revision,
                new SettingsPatch(TaskAlerts: live.Desired.TaskAlerts with { Bank = 3 })),
            CancellationToken.None);
        Assert.Equal(SettingsPrepareStatus.Rejected, blocked.Status);
        Assert.Contains(blocked.Errors, error => error.Contains("could not be recorded", StringComparison.OrdinalIgnoreCase));

        client.Dispose();
        await coordinator.DisposeAsync();

        var recoveredActivator = new CountingActivator();
        await using var recovered = store.CreateCoordinator(
            planner: new FixedImpactPlanner(SettingsEffectKind.PendingIdle),
            activator: recoveredActivator);
        using var recoveredClient = await AttachAsync(recovered, "settings-2");
        Assert.Equal(4, recoveredClient.State.Snapshot.Desired.TaskAlerts.Bank);
        Assert.Equal(2, recoveredClient.State.Snapshot.Active.TaskAlerts.Bank);
        var recoveredState = Assert.Single(
            recoveredClient.State.Snapshot.Aggregates,
            item => item.Aggregate == SettingsAggregateId.TaskAlerts);
        Assert.Equal(SettingsActivationState.PendingIdle, recoveredState.Activation);

        var recoveredActivation = await recovered.ActivateDesiredAsync(
            SettingsAggregateId.TaskAlerts,
            CancellationToken.None);
        Assert.Equal(SettingsActivationState.Applied, recoveredActivation.State);
        Assert.Equal(1, recoveredActivator.CallCount);
        var recoveredSnapshot = await recovered.GetSnapshotAsync(
            recoveredClient.ConnectionId,
            CancellationToken.None);
        Assert.Equal(4, recoveredSnapshot.Active.TaskAlerts.Bank);
    }

    [Fact]
    public async Task ReconcileStartupPropagatesPostActivationJournalFailure()
    {
        using var store = new TemporarySettingsStore();
        var io = new FailOnceDocumentIo();
        var activator = new CountingActivator();
        io.FailReplace(store.Paths.Journal, occurrence: 2);
        await using var coordinator = store.CreateCoordinator(activator: activator, io: io);

        var exception = await Assert.ThrowsAsync<IOException>(
            () => coordinator.ReconcileStartupAsync(CancellationToken.None));

        Assert.Contains("Synthetic document write failure", exception.Message, StringComparison.Ordinal);
        Assert.Equal(Enum.GetValues<SettingsAggregateId>().Length, activator.CallCount);
    }

    [Fact]
    public async Task UnrelatedLiveActivationCannotAdoptAPendingVoiceRevision()
    {
        using var store = new TemporarySettingsStore();
        var activator = new RecordingActivator();
        await using var coordinator = store.CreateCoordinator(
            planner: new VoicePendingImpactPlanner(),
            activator: activator);
        using var client = await AttachAsync(coordinator, "settings-1");
        var originalVoice = client.State.Snapshot.Active.Voice;

        var voicePrepared = await coordinator.PrepareAsync(
            client.ConnectionId,
            new PrepareSettingsRequest(
                client.State.Snapshot.Revision,
                new SettingsPatch(Voice: originalVoice with { PinnedTaskLabel = "pending voice v2" })),
            CancellationToken.None);
        var voiceResult = await coordinator.ApplyAsync(
            client.ConnectionId,
            new ApplySettingsRequest(Guid.NewGuid(), voicePrepared.PreparationToken!),
            CancellationToken.None);
        Assert.Equal(SettingsApplyStatus.PendingIdle, voiceResult.Status);
        Assert.Empty(activator.Candidates);

        var taskPrepared = await coordinator.PrepareAsync(
            client.ConnectionId,
            new PrepareSettingsRequest(
                voiceResult.Snapshot.Revision,
                new SettingsPatch(TaskAlerts: voiceResult.Snapshot.Desired.TaskAlerts with { Bank = 4 })),
            CancellationToken.None);
        var taskResult = await coordinator.ApplyAsync(
            client.ConnectionId,
            new ApplySettingsRequest(Guid.NewGuid(), taskPrepared.PreparationToken!),
            CancellationToken.None);

        Assert.Equal(SettingsApplyStatus.Applied, taskResult.Status);
        var candidate = Assert.Single(activator.Candidates);
        Assert.Equal(SettingsAggregateId.TaskAlerts, candidate.Aggregate);
        Assert.Equal(originalVoice, candidate.Bundle.Voice);
        Assert.Equal(4, candidate.Bundle.TaskAlerts.Bank);
        Assert.Equal("pending voice v2", taskResult.Snapshot.Desired.Voice.PinnedTaskLabel);
        Assert.Equal(originalVoice, taskResult.Snapshot.Active.Voice);
        var voiceState = Assert.Single(
            taskResult.Snapshot.Aggregates,
            item => item.Aggregate == SettingsAggregateId.Voice);
        Assert.Equal(SettingsActivationState.PendingIdle, voiceState.Activation);
    }

    [Fact]
    public async Task ExternalAdoptionRestoresMemoryWhenItsCommitMarkerCannotBeSaved()
    {
        using var store = new TemporarySettingsStore();
        var io = new FailOnceDocumentIo();
        await using var coordinator = store.CreateCoordinator(io: io);
        using var client = await AttachAsync(coordinator, "settings-1");
        TaskAlertPreferencesStore.Save(
            store.Paths.TaskAlerts,
            client.State.Snapshot.Desired.TaskAlerts with { Bank = 4 });
        io.FailReplace(store.Paths.Journal, occurrence: 2);

        var result = await coordinator.AdoptExternalAsync(
            client.ConnectionId,
            [SettingsAggregateId.TaskAlerts],
            CancellationToken.None);

        Assert.Equal(RuntimeCommandStatus.Failed, result.Status);
        Assert.Equal(2, result.Snapshot.Revision);
        Assert.Equal(2, result.Snapshot.Desired.TaskAlerts.Bank);
        Assert.Equal(2, result.Snapshot.Active.TaskAlerts.Bank);
        var external = Assert.Single(result.Snapshot.ExternalCandidates);
        Assert.Equal(SettingsAggregateId.TaskAlerts, external.Aggregate);
        Assert.Equal(4, TaskAlertPreferencesStore.LoadOrCreate(store.Paths.TaskAlerts).Bank);

        var blocked = await coordinator.PrepareAsync(
            client.ConnectionId,
            new PrepareSettingsRequest(
                result.Snapshot.Revision,
                new SettingsPatch(TaskAlerts: result.Snapshot.Desired.TaskAlerts with { Bank = 3 })),
            CancellationToken.None);
        Assert.Equal(SettingsPrepareStatus.Rejected, blocked.Status);
        Assert.Contains(blocked.Errors, error => error.Contains("journal", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PostActivationJournalFailureReportsTruthAndBlocksFurtherMutation()
    {
        using var store = new TemporarySettingsStore();
        var io = new FailOnceDocumentIo();
        await using var coordinator = store.CreateCoordinator(io: io);
        using var client = await AttachAsync(coordinator, "settings-1");
        TaskAlertPreferencesStore.Save(
            store.Paths.TaskAlerts,
            client.State.Snapshot.Desired.TaskAlerts with { Bank = 4 });
        io.FailReplace(store.Paths.Journal, occurrence: 3);

        var result = await coordinator.AdoptExternalAsync(
            client.ConnectionId,
            [SettingsAggregateId.TaskAlerts],
            CancellationToken.None);

        Assert.Equal(RuntimeCommandStatus.Failed, result.Status);
        Assert.Equal(3, result.Snapshot.Revision);
        Assert.Equal(4, result.Snapshot.Desired.TaskAlerts.Bank);
        Assert.Equal(4, result.Snapshot.Active.TaskAlerts.Bank);
        Assert.Empty(result.Snapshot.ExternalCandidates);
        var state = Assert.Single(
            result.Snapshot.Aggregates,
            item => item.Aggregate == SettingsAggregateId.TaskAlerts);
        Assert.Equal(SettingsActivationState.Applied, state.Activation);
        Assert.Equal(state.DesiredRevision, state.ActiveRevision);

        var blocked = await coordinator.AdoptExternalAsync(
            client.ConnectionId,
            requestedAggregates: null,
            CancellationToken.None);
        Assert.Equal(RuntimeCommandStatus.Failed, blocked.Status);
        Assert.Equal(result.Snapshot.Revision, blocked.Snapshot.Revision);
        Assert.Equal(
            SettingsCanonicalizer.HashBundle(result.Snapshot.Desired),
            SettingsCanonicalizer.HashBundle(blocked.Snapshot.Desired));
        Assert.Equal(
            SettingsCanonicalizer.HashBundle(result.Snapshot.Active),
            SettingsCanonicalizer.HashBundle(blocked.Snapshot.Active));
    }

    [Fact]
    public async Task ExternalCompanionAdoptionPreservesBomDecodingFromCapturedBytes()
    {
        using var store = new TemporarySettingsStore();
        await using var coordinator = store.CreateCoordinator();
        using var client = await AttachAsync(coordinator, "settings-1");
        var json = await File.ReadAllTextAsync(store.Paths.Companion);
        await File.WriteAllTextAsync(store.Paths.Companion, json, Encoding.Unicode);

        var result = await coordinator.AdoptExternalAsync(
            client.ConnectionId,
            [SettingsAggregateId.Companion],
            CancellationToken.None);

        Assert.Equal(RuntimeCommandStatus.Completed, result.Status);
        Assert.Empty(result.Snapshot.ExternalCandidates);
        Assert.Equal(
            SettingsCanonicalizer.HashBundle(client.State.Snapshot.Desired),
            SettingsCanonicalizer.HashBundle(result.Snapshot.Desired));
        var stable = await coordinator.GetSnapshotAsync(client.ConnectionId, CancellationToken.None);
        Assert.Equal(result.Snapshot.Revision, stable.Revision);
        Assert.Empty(stable.ExternalCandidates);
    }

    [Fact]
    public async Task ExternalAdoptionRejectsAConcurrentChangeInAnUnselectedFile()
    {
        using var store = new TemporarySettingsStore();
        var io = new EditAfterReadsDocumentIo();
        await using var coordinator = store.CreateCoordinator(io: io);
        using var client = await AttachAsync(coordinator, "settings-1");
        TaskAlertPreferencesStore.Save(
            store.Paths.TaskAlerts,
            client.State.Snapshot.Desired.TaskAlerts with { Bank = 4 });
        var discovered = await coordinator.GetSnapshotAsync(client.ConnectionId, CancellationToken.None);
        Assert.Single(discovered.ExternalCandidates);
        io.EditAfterReads(6, () => VoicePePreferencesStore.Save(
            store.Paths.Voice,
            discovered.Desired.Voice with { PinnedTaskLabel = "concurrent edit" }));

        var result = await coordinator.AdoptExternalAsync(
            client.ConnectionId,
            [SettingsAggregateId.TaskAlerts],
            CancellationToken.None);

        Assert.Equal(RuntimeCommandStatus.Rejected, result.Status);
        Assert.Equal(2, result.Snapshot.Desired.TaskAlerts.Bank);
        Assert.Contains(
            result.Snapshot.ExternalCandidates,
            candidate => candidate.Aggregate == SettingsAggregateId.TaskAlerts && candidate.IsValid);
        Assert.Contains(
            result.Snapshot.ExternalCandidates,
            candidate => candidate.Aggregate == SettingsAggregateId.Voice && candidate.IsValid);
    }

    [Fact]
    public async Task SharedJournalRebasesCleanlyForAnotherCompanionSelection()
    {
        using var store = new TemporarySettingsStore();
        var firstPaths = RuntimeSettingsPaths.ForConfiguration(Path.Combine(store.Root, "first.json"));
        var secondPaths = RuntimeSettingsPaths.ForConfiguration(Path.Combine(store.Root, "second.json"));
        Assert.Equal(firstPaths.Journal, secondPaths.Journal);

        var operationId = Guid.NewGuid();
        await using (var first = CreateCoordinator(firstPaths))
        using (var firstClient = await AttachAsync(first, "settings-first"))
        {
            var prepared = await PrepareTaskAlertBankAsync(first, firstClient, 4);
            var result = await first.ApplyAsync(
                firstClient.ConnectionId,
                new ApplySettingsRequest(operationId, prepared.PreparationToken),
                CancellationToken.None);
            Assert.Equal(SettingsApplyStatus.Applied, result.Status);
        }

        await using var second = CreateCoordinator(secondPaths);
        using var secondClient = await AttachAsync(second, "settings-second");
        var lookup = await second.GetOperationAsync(
            secondClient.ConnectionId,
            operationId,
            CancellationToken.None);
        Assert.Equal(SettingsOperationState.NotFound, lookup.State);
    }

    [Fact]
    public async Task PendingCommitForOneCompanionSelectionCannotRecoverAgainstAnother()
    {
        using var store = new TemporarySettingsStore();
        var firstPaths = RuntimeSettingsPaths.ForConfiguration(Path.Combine(store.Root, "first.json"));
        var secondPaths = RuntimeSettingsPaths.ForConfiguration(Path.Combine(store.Root, "second.json"));
        await using (var secondSeed = CreateCoordinator(secondPaths))
        {
        }
        var secondBytes = File.ReadAllBytes(secondPaths.Companion);
        var taskAlertBytes = File.ReadAllBytes(secondPaths.TaskAlerts);

        var observer = new InterruptingObserver(SettingsTransactionCheckpoint.DocumentWritten);
        var first = RuntimeSettingsCoordinator.Create(
            firstPaths,
            new DefaultSettingsImpactPlanner(),
            new ImmediateSettingsActivator(),
            TimeProvider.System,
            CancellationToken.None,
            new SettingsDocumentIo(),
            observer,
            new RuntimeEventHub(Guid.NewGuid()));
        using var firstClient = await AttachAsync(first, "settings-first");
        var candidate = WithLargePrompt(firstClient.State.Snapshot.Desired.Companion, 1);
        var prepared = await first.PrepareAsync(
            firstClient.ConnectionId,
            new PrepareSettingsRequest(
                firstClient.State.Snapshot.Revision,
                new SettingsPatch(
                    Companion: candidate,
                    TaskAlerts: firstClient.State.Snapshot.Desired.TaskAlerts with { Bank = 4 })),
            CancellationToken.None);
        Assert.Equal(SettingsPrepareStatus.Prepared, prepared.Status);
        await Assert.ThrowsAsync<SettingsTransactionInterruptedException>(() => first.ApplyAsync(
            firstClient.ConnectionId,
            new ApplySettingsRequest(Guid.NewGuid(), prepared.PreparationToken!),
            CancellationToken.None));
        await first.DisposeAsync();

        var error = Assert.Throws<InvalidDataException>(() => CreateCoordinator(secondPaths));
        Assert.Contains("another companion configuration", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(secondBytes, File.ReadAllBytes(secondPaths.Companion));
        Assert.Equal(taskAlertBytes, File.ReadAllBytes(secondPaths.TaskAlerts));
    }

    [Fact]
    public async Task RepeatedLargeValidAppliesKeepJournalBoundedAndRecoverLatestState()
    {
        using var store = new TemporarySettingsStore();
        await using (var coordinator = store.CreateCoordinator())
        using (var client = await AttachAsync(coordinator, "settings-1"))
        {
            var snapshot = client.State.Snapshot;
            for (var index = 0; index < 20; index++)
            {
                var candidate = WithLargePrompt(snapshot.Desired.Companion, index);
                var prepared = await coordinator.PrepareAsync(
                    client.ConnectionId,
                    new PrepareSettingsRequest(
                        snapshot.Revision,
                        new SettingsPatch(Companion: candidate)),
                    CancellationToken.None);
                Assert.Equal(SettingsPrepareStatus.Prepared, prepared.Status);
                var applied = await coordinator.ApplyAsync(
                    client.ConnectionId,
                    new ApplySettingsRequest(Guid.NewGuid(), prepared.PreparationToken!),
                    CancellationToken.None);
                Assert.Equal(SettingsApplyStatus.Applied, applied.Status);
                snapshot = applied.Snapshot;
            }
        }

        var companionBytes = new FileInfo(store.Paths.Companion).Length;
        Assert.True(new FileInfo(store.Paths.Journal).Length < companionBytes * 4);
        await using var recovered = store.CreateCoordinator();
        using var recoveredClient = await AttachAsync(recovered, "settings-2");
        Assert.Equal(20, recoveredClient.State.Snapshot.Revision - 1);
        Assert.EndsWith(
            "-19",
            recoveredClient.State.Snapshot.Desired.Companion.PromptPickers[0].Prompts[0],
            StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeEventsRequireAnEpochCursorPairAndSurviveAFaultySubscriber()
    {
        var epoch = Guid.NewGuid();
        var hub = new RuntimeEventHub(epoch, capacity: 2);
        using var faulty = hub.Subscribe("faulty", null, null, _ => throw new IOException("disconnected"));
        var observed = new List<long>();
        using var healthy = hub.Subscribe("healthy", null, null, item => observed.Add(item.Sequence));

        hub.Publish(RuntimeEventKind.RuntimeIdentityChanged);
        hub.Publish(RuntimeEventKind.RuntimeIdentityChanged);
        hub.Publish(RuntimeEventKind.RuntimeIdentityChanged);

        Assert.Equal([1L, 2L, 3L], observed);
        using var replay = hub.Subscribe("replay", epoch, 1, _ => { });
        Assert.False(replay.ResynchronizationRequired);
        Assert.Equal([2L, 3L], replay.Replay.Select(item => item.Sequence));
        using var gap = hub.Subscribe("gap", epoch, 0, _ => { });
        Assert.True(gap.ResynchronizationRequired);
        Assert.Empty(gap.Replay);
        using var cursorOnly = hub.Subscribe("cursor-only", null, 3, _ => { });
        Assert.True(cursorOnly.ResynchronizationRequired);
        using var epochOnly = hub.Subscribe("epoch-only", epoch, null, _ => { });
        Assert.True(epochOnly.ResynchronizationRequired);
    }

    private static async Task<PreparedClientChange> PrepareTaskAlertBankAsync(
        RuntimeSettingsCoordinator coordinator,
        AttachedClient client,
        int bank)
    {
        var prepared = await coordinator.PrepareAsync(
            client.ConnectionId,
            new PrepareSettingsRequest(
                client.State.Snapshot.Revision,
                new SettingsPatch(TaskAlerts: client.State.Snapshot.Desired.TaskAlerts with { Bank = bank })),
            CancellationToken.None);
        Assert.Equal(SettingsPrepareStatus.Prepared, prepared.Status);
        return new PreparedClientChange(prepared.PreparationToken!);
    }

    private static void AssertSameOutcome(ApplySettingsResult expected, ApplySettingsResult actual)
    {
        Assert.Equal(expected.OperationId, actual.OperationId);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.DesiredStateCommitted, actual.DesiredStateCommitted);
        Assert.Equal(expected.CanCloseSettings, actual.CanCloseSettings);
        Assert.Equal(expected.Aggregates, actual.Aggregates);
        Assert.Equal(expected.Errors, actual.Errors);
    }

    private static RuntimeSettingsCoordinator CreateCoordinator(RuntimeSettingsPaths paths) =>
        RuntimeSettingsCoordinator.Create(
            paths,
            new DefaultSettingsImpactPlanner(),
            new ImmediateSettingsActivator(),
            TimeProvider.System,
            CancellationToken.None,
            new SettingsDocumentIo(),
            SettingsTransactionObserver.Instance,
            new RuntimeEventHub(Guid.NewGuid()));

    private static CompanionConfig WithLargePrompt(CompanionConfig basis, int suffix)
    {
        var picker = basis.PromptPickers[0];
        return new CompanionConfig
        {
            Device = basis.Device,
            Devices = basis.Devices,
            Polling = basis.Polling,
            Safety = basis.Safety,
            OpenWorkingDirectory = basis.OpenWorkingDirectory,
            BankSelectors = basis.BankSelectors,
            Bindings = basis.Bindings,
            PromptPickers =
            [
                new PromptPickerConfig
                {
                    Id = picker.Id,
                    Name = picker.Name,
                    Prompts = [new string('p', 256 * 1024) + $"-{suffix}"],
                    SubmitAfterInsert = [false],
                    IncludeExitOption = picker.IncludeExitOption,
                    DefaultPromptIndex = 0,
                    Controls = picker.Controls,
                },
            ],
        };
    }

    private static async Task<AttachedClient> AttachAsync(
        RuntimeSettingsCoordinator coordinator,
        string connectionId)
    {
        var state = await coordinator.AttachAsync(
            connectionId,
            previousEpoch: null,
            afterSequence: null,
            _ => { },
            CancellationToken.None);
        return new AttachedClient(connectionId, state);
    }

    private static readonly JsonSerializerOptions LegacyJournalJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private sealed record PreparedClientChange(string PreparationToken);

    private sealed class AttachedClient(string connectionId, SettingsAttachState state) : IDisposable
    {
        public string ConnectionId { get; } = connectionId;
        public SettingsAttachState State { get; } = state;

        public void Dispose() => State.Subscription.Dispose();
    }

    private sealed class TemporarySettingsStore : IDisposable
    {
        public TemporarySettingsStore()
        {
            Root = Path.Combine(Path.GetTempPath(), "joydex-runtime-settings-tests", Guid.NewGuid().ToString("N"));
            Paths = new RuntimeSettingsPaths(
                Path.Combine(Root, "companion.json"),
                Path.Combine(Root, "voice.json"),
                Path.Combine(Root, "pebble-index.json"),
                Path.Combine(Root, "task-alerts.json"),
                Path.Combine(Root, "runtime-settings-journal.json"));
        }

        public string Root { get; }
        public RuntimeSettingsPaths Paths { get; }

        public RuntimeSettingsCoordinator CreateCoordinator(
            ISettingsImpactPlanner? planner = null,
            ISettingsActivator? activator = null,
            ISettingsDocumentIo? io = null,
            ISettingsTransactionObserver? observer = null) => RuntimeSettingsCoordinator.Create(
                Paths,
                planner ?? new DefaultSettingsImpactPlanner(),
                activator ?? new ImmediateSettingsActivator(),
                TimeProvider.System,
                CancellationToken.None,
                io ?? new SettingsDocumentIo(),
                observer ?? SettingsTransactionObserver.Instance,
                new RuntimeEventHub(Guid.NewGuid()));

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class FailOnceDocumentIo : ISettingsDocumentIo
    {
        private readonly SettingsDocumentIo _inner = new();
        private readonly List<ReplaceFailure> _failures = [];

        public void FailNextReplace(string path) => FailReplace(path, occurrence: 1);

        public void FailReplace(string path, int occurrence)
        {
            if (occurrence <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(occurrence));
            }
            _failures.Add(new ReplaceFailure(Path.GetFullPath(path), occurrence));
        }

        public bool Exists(string path) => _inner.Exists(path);
        public byte[] Read(string path) => _inner.Read(path);
        public T Read<T>(string path, Func<Stream, T> read) => _inner.Read(path, read);

        public void Replace(string path, ReadOnlySpan<byte> content)
        {
            if (ShouldFail(path))
            {
                throw new IOException("Synthetic document write failure.");
            }
            _inner.Replace(path, content);
        }

        public void Replace(string path, Action<Stream> write)
        {
            if (ShouldFail(path))
            {
                throw new IOException("Synthetic document write failure.");
            }
            _inner.Replace(path, write);
        }

        public void Delete(string path) => _inner.Delete(path);

        private bool ShouldFail(string path)
        {
            if (_failures.Count == 0
                || !string.Equals(
                    Path.GetFullPath(path),
                    _failures[0].Path,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            _failures[0].RemainingReplaces--;
            if (_failures[0].RemainingReplaces != 0)
            {
                return false;
            }

            _failures.RemoveAt(0);
            return true;
        }

        private sealed record ReplaceFailure(string Path, int InitialRemainingReplaces)
        {
            public int RemainingReplaces { get; set; } = InitialRemainingReplaces;
        }
    }

    private sealed class EditAfterReadsDocumentIo : ISettingsDocumentIo
    {
        private readonly SettingsDocumentIo _inner = new();
        private int _remainingReads = -1;
        private Action? _edit;

        public void EditAfterReads(int reads, Action edit)
        {
            if (reads <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(reads));
            }
            _remainingReads = reads;
            _edit = edit;
        }

        public bool Exists(string path) => _inner.Exists(path);

        public byte[] Read(string path)
        {
            if (_remainingReads > 0 && --_remainingReads == 0)
            {
                var edit = _edit;
                _edit = null;
                edit?.Invoke();
            }
            return _inner.Read(path);
        }

        public T Read<T>(string path, Func<Stream, T> read)
        {
            using var stream = new MemoryStream(Read(path), writable: false);
            return read(stream);
        }

        public void Replace(string path, ReadOnlySpan<byte> content) => _inner.Replace(path, content);
        public void Replace(string path, Action<Stream> write) => _inner.Replace(path, write);
        public void Delete(string path) => _inner.Delete(path);
    }

    private sealed class InterruptingObserver(SettingsTransactionCheckpoint checkpoint)
        : ISettingsTransactionObserver
    {
        public void OnCheckpoint(
            SettingsTransactionCheckpoint current,
            SettingsAggregateId? aggregate = null)
        {
            if (current == checkpoint)
            {
                throw new SettingsTransactionInterruptedException($"Interrupted after {current}.");
            }
        }
    }

    private sealed class FixedActivator(SettingsActivationState state, string? detail) : ISettingsActivator
    {
        public Task<SettingsActivationResult> ActivateAsync(
            SettingsAggregateId aggregate,
            SettingsBundle desired,
            long desiredRevision,
            CancellationToken runtimeCancellationToken) =>
            Task.FromResult(new SettingsActivationResult(state, detail));
    }

    private sealed class CountingActivator : ISettingsActivator
    {
        public int CallCount { get; private set; }

        public Task<SettingsActivationResult> ActivateAsync(
            SettingsAggregateId aggregate,
            SettingsBundle desired,
            long desiredRevision,
            CancellationToken runtimeCancellationToken)
        {
            CallCount++;
            return Task.FromResult(new SettingsActivationResult(SettingsActivationState.Applied));
        }
    }

    private sealed class RecordingActivator : ISettingsActivator
    {
        public List<(SettingsAggregateId Aggregate, SettingsBundle Bundle)> Candidates { get; } = [];

        public Task<SettingsActivationResult> ActivateAsync(
            SettingsAggregateId aggregate,
            SettingsBundle activationCandidate,
            long desiredRevision,
            CancellationToken runtimeCancellationToken)
        {
            Candidates.Add((aggregate, activationCandidate));
            return Task.FromResult(new SettingsActivationResult(SettingsActivationState.Applied));
        }
    }

    private sealed class FixedImpactPlanner(SettingsEffectKind kind) : ISettingsImpactPlanner
    {
        public SettingsEffect[] Plan(
            SettingsBundle active,
            SettingsBundle candidate,
            IReadOnlyList<SettingsAggregateId> changedAggregates) =>
            changedAggregates.Select(aggregate => new SettingsEffect(
                aggregate,
                kind,
                "Synthetic effect."))
                .ToArray();
    }

    private sealed class VoicePendingImpactPlanner : ISettingsImpactPlanner
    {
        public SettingsEffect[] Plan(
            SettingsBundle active,
            SettingsBundle candidate,
            IReadOnlyList<SettingsAggregateId> changedAggregates) =>
            changedAggregates.Select(aggregate => new SettingsEffect(
                aggregate,
                aggregate == SettingsAggregateId.Voice
                    ? SettingsEffectKind.PendingIdle
                    : SettingsEffectKind.ApplyLive,
                "Synthetic effect."))
                .ToArray();
    }
}
