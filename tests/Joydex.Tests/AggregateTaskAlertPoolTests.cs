using System.Text.Json;
using Joydex.Core.TaskAlerts;

namespace Joydex.Tests;

public sealed class AggregateTaskAlertPoolTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TaskAlertSourceIdentity Source = new(TaskAlertBackend.RemoteT3, "server-a");

    private static TaskAlertSourceRequest Question(string name, int second = 1, string run = "run") =>
        new(TaskAlertIdentity.CreateRequestKey(TaskAlertIdentity.RemoteT3(Source.EnvironmentId, "thread"), run, name),
            run, Start.AddSeconds(second));
    private static TaskAlertSourceObservation Running(params TaskAlertSourceRequest[] questions) =>
        new("thread", "run", TaskAlertState.Running, Start, AsyncQuestions: questions);
    private static TaskAlertSourceSnapshot Snapshot(TaskAlertSourceObservation[] tasks, long sequence = 1,
        int second = 10, long generation = 1, bool complete = true) =>
        new(Source, generation, sequence, Start.AddSeconds(second), complete, tasks, Start.AddSeconds(second));

    [Fact]
    public void IdentitySeparatesBackendsEnvironmentsAndOrdinalNativeIdsThroughLegacyMaps()
    {
        var identities = new[] { TaskAlertIdentity.LocalCodex("thread"),
            TaskAlertIdentity.RemoteT3("server-a", "thread"), TaskAlertIdentity.RemoteT3("server-a", "Thread"),
            TaskAlertIdentity.RemoteT3("server-b", "thread"), TaskAlertIdentity.RemoteT3("Server-a", "thread") };
        Assert.Equal("thread", identities[0].PoolKey);
        Assert.Equal(identities.Length, identities.Select(identity => identity.PoolKey).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var identity in identities) Assert.Equal(identity, TaskAlertIdentity.FromPoolKey(identity.PoolKey));
        Assert.Throws<InvalidDataException>(() => TaskAlertIdentity.LocalCodex(identities[1].PoolKey).Validate());
        var pool = new TaskAlertPool();
        pool.Apply(new(CodexLifecycleEvent.UserPromptSubmit, "thread", "run", Start));
        pool.ApplySourceSnapshot(Snapshot([Running(), Running() with { NativeThreadId = "Thread" }]));
        Assert.Equal(3, pool.Assignments.Count);
        Assert.Equal([1, 2, 3], pool.Assignments.Select(item => item.Slot));
    }

    [Fact]
    public void SuppressionAcceptsOnlyCanonicalLongRemoteKeysAndKeepsNativeBound()
    {
        var identity = TaskAlertIdentity.RemoteT3(new string('\u3042', 512), new string('\u3044', 512));
        Assert.True(identity.PoolKey.Length > 512);
        Assert.Equal(identity.PoolKey, TaskAlertSuppression.NormalizeTaskId(identity.PoolKey));
        Assert.Null(TaskAlertSuppression.NormalizeTaskId(new string('a', 513)));
        Assert.Null(TaskAlertSuppression.NormalizeTaskId(identity.PoolKey.ToLowerInvariant()));
        Assert.Throws<InvalidDataException>(() => TaskAlertIdentity.RemoteT3("server", "\ud800").Validate());
    }

    [Fact]
    public void DismissalSurvivesRepeatedSnapshotsDiskRestartAndNewQuestionsRemainVisible()
    {
        var old = Question("old");
        var fresh = Question("new", 11);
        var pool = new TaskAlertPool();
        pool.ApplySourceSnapshot(Snapshot([Running(old)]));
        var selected = Assert.Single(pool.Assignments);
        pool.DismissQuestions(selected.Slot, selected.SessionId, selected.PendingAsyncQuestions!, Start.AddSeconds(10));
        var root = Path.Combine(Path.GetTempPath(), "joydex-core-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(root, "state.json");
            TaskAlertStateStore.Save(path, pool.CaptureState());
            var restored = new TaskAlertPool();
            restored.Restore(TaskAlertStateStore.Load(path));
            restored.ApplySourceSnapshot(Snapshot([Running(old, fresh)], second: 12));
            Assert.Equal([fresh.Key], Assert.Single(restored.Assignments).PendingAsyncQuestions!);
            Assert.Equal(TaskAlertState.Approval, Assert.Single(restored.Assignments).State);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void PositiveNewHumanTurnRetiresOnlyOlderQuestionsAndStaysRetiredAfterRestart()
    {
        var old = Question("old");
        var fresh = Question("new", 12);
        var pool = new TaskAlertPool();
        pool.ApplySourceSnapshot(Snapshot([Running(old)]));
        pool.ApplySourceSnapshot(Snapshot([Running(old, fresh) with { LatestHumanUserTurnAt = Start.AddSeconds(11) }], 2, 13));
        Assert.Equal([fresh.Key], Assert.Single(pool.Assignments).PendingAsyncQuestions!);
        var restored = new TaskAlertPool();
        restored.Restore(pool.CaptureState());
        restored.ApplySourceSnapshot(Snapshot([Running(old, fresh)], second: 14));
        Assert.Equal([fresh.Key], Assert.Single(restored.Assignments).PendingAsyncQuestions!);
    }

    [Fact]
    public void NewQuestionWithoutNativeTimeIsNotAssumedOlderThanKnownHumanTurn()
    {
        var question = Question("new") with { CreatedAt = null };
        var pool = new TaskAlertPool();
        pool.ApplySourceSnapshot(Snapshot([Running() with { LatestHumanUserTurnAt = Start.AddSeconds(5) }]));
        pool.ApplySourceSnapshot(Snapshot([Running(question)], 2, 11));
        Assert.Equal([question.Key], Assert.Single(pool.Assignments).PendingAsyncQuestions!);
    }

    [Fact]
    public void MissingCatalogPartialReadsAndUnknownLifecycleNeverEndOrClearQuestions()
    {
        var question = Question("question");
        var pool = new TaskAlertPool();
        pool.ApplySourceSnapshot(Snapshot([Running(question)]));
        pool.ApplySourceSnapshot(Snapshot([], 2, 11));
        pool.ApplySourceSnapshot(Snapshot([Running() with { State = TaskAlertState.Completed,
            CompletedAt = Start.AddSeconds(5), ReadComplete = false, RequestsComplete = true }], 3, 12, complete: false));
        pool.ApplySourceSnapshot(Snapshot([Running() with { State = null, RequestsComplete = true }], 4, 13));
        Assert.Equal(TaskAlertState.Approval, Assert.Single(pool.Assignments).State);
        Assert.Equal([question.Key], Assert.Single(pool.Assignments).PendingAsyncQuestions!);
    }

    [Fact]
    public void RequestAbsencePreservesAsyncButOnlyCompleteReadsRetireBlockingRequests()
    {
        var pool = new TaskAlertPool();
        var blocking = Question("permission");
        pool.ApplySourceSnapshot(Snapshot([Running() with { BlockingRequests = [blocking] }]));
        pool.ApplySourceSnapshot(Snapshot([Running() with { RequestsComplete = true, ReadComplete = false }], 2, 11));
        Assert.Equal(TaskAlertState.Approval, Assert.Single(pool.Assignments).State);
        pool.ApplySourceSnapshot(Snapshot([Running() with { RequestsComplete = true }], 3, 12));
        Assert.Equal(TaskAlertState.Running, Assert.Single(pool.Assignments).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompleteRunCanEstablishUnknownPartialAssignmentThenResolveItsRequestAndComplete(bool blocking)
    {
        var question = Question("partial-question");
        var pool = new TaskAlertPool();
        var partial = new TaskAlertSourceObservation("thread", null, null, null,
            ReadComplete: false, BlockingRequests: blocking ? [question] : [], AsyncQuestions: blocking ? [] : [question]);
        Assert.True(pool.ApplySourceSnapshot(Snapshot([partial], complete: false)));
        var assigned = Assert.Single(pool.Assignments);
        Assert.Equal(TaskAlertState.Approval, assigned.State);
        Assert.Null(assigned.TurnId);
        Assert.Null(assigned.TurnBoundaryAt);
        Assert.Equal(Start.AddSeconds(10), assigned.UpdatedAt);

        Assert.True(pool.ApplySourceSnapshot(Snapshot([Running() with { RequestsComplete = true,
            BlockingRequests = blocking ? [question] : [], AsyncQuestions = blocking ? [] : [question] }], 2, 12)));
        assigned = Assert.Single(pool.Assignments);
        Assert.Equal("run", assigned.TurnId);
        Assert.Equal(Start, assigned.TurnBoundaryAt);
        Assert.Equal(Start.AddSeconds(12), assigned.ObservedActiveAt);
        Assert.Equal(TaskAlertState.Approval, assigned.State);
        if (!blocking) Assert.Equal([question.Key], assigned.PendingAsyncQuestions!);

        Assert.True(pool.ApplySourceSnapshot(Snapshot([Running() with
            { RequestsComplete = true, ResolvedQuestions = blocking ? [] : [question] }], 3, 13)));
        Assert.Equal(TaskAlertState.Running, Assert.Single(pool.Assignments).State);
        Assert.Null(Assert.Single(pool.Assignments).PendingAsyncQuestions);
        Assert.True(pool.ApplySourceSnapshot(Snapshot([Running() with
            { State = TaskAlertState.Completed, CompletedAt = Start.AddSeconds(14), RequestsComplete = true }], 4, 15)));
        Assert.Equal(TaskAlertState.Completed, Assert.Single(pool.Assignments).State);
        Assert.Equal(Start, Assert.Single(pool.Assignments).TurnBoundaryAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EstablishingRunAfterPartialReadPreservesRestartedDismissalAndNewQuestions(bool partialStartHint)
    {
        var dismissed = Question("dismissed");
        var fresh = Question("fresh", 11);
        var pool = new TaskAlertPool();
        pool.ApplySourceSnapshot(Snapshot([new("thread", partialStartHint ? "unconfirmed-run" : null, null, partialStartHint ? Start.AddSeconds(9) : null,
            ReadComplete: false, AsyncQuestions: [dismissed])], complete: false));
        var assigned = Assert.Single(pool.Assignments);
        Assert.Null(assigned.TurnBoundaryAt);
        Assert.True(pool.DismissQuestions(assigned.Slot, assigned.SessionId, [dismissed.Key], Start.AddSeconds(10)));
        var restored = new TaskAlertPool();
        restored.Restore(pool.CaptureState());

        Assert.True(restored.ApplySourceSnapshot(Snapshot([Running(dismissed, fresh) with { RequestsComplete = true }], second: 12)));
        assigned = Assert.Single(restored.Assignments);
        Assert.Equal("run", assigned.TurnId);
        Assert.Equal(Start, assigned.TurnBoundaryAt);
        Assert.Equal(TaskAlertState.Approval, assigned.State);
        Assert.Equal([fresh.Key], assigned.PendingAsyncQuestions!);
        restored.ApplySourceSnapshot(Snapshot([Running(dismissed, fresh) with { RequestsComplete = true }], 2, 13));
        Assert.Equal([fresh.Key], Assert.Single(restored.Assignments).PendingAsyncQuestions!);
    }

    [Fact]
    public void EstablishedNativeRunStillRejectsOlderDifferentRunWithoutClearingAttention()
    {
        var question = Question("current-question", 21, "current");
        var current = Running(question) with { RunId = "current", StartedAt = Start.AddSeconds(20), RequestsComplete = true };
        var pool = new TaskAlertPool();
        pool.ApplySourceSnapshot(Snapshot([current], second: 22));
        var assigned = Assert.Single(pool.Assignments);

        var older = Running() with { RunId = "previous", StartedAt = Start.AddSeconds(10), RequestsComplete = true,
            ResolvedQuestions = [question] };
        Assert.False(pool.ApplySourceSnapshot(Snapshot([older], 2, 23)));
        Assert.False(pool.ApplySourceSnapshot(Snapshot([older with
            { State = TaskAlertState.Fault, CompletedAt = Start.AddSeconds(15) }], 3, 24)));
        var retained = Assert.Single(pool.Assignments);
        Assert.Equal(assigned.UpdatedAt, retained.UpdatedAt);
        Assert.Equal(assigned.ObservedActiveAt, retained.ObservedActiveAt);
        Assert.Equal(TaskAlertState.Approval, retained.State);
        Assert.Equal("current", retained.TurnId);
        Assert.Equal(Start.AddSeconds(20), retained.TurnBoundaryAt);
        Assert.Equal([question.Key], retained.PendingAsyncQuestions!);
    }

    [Fact]
    public void OnlyExactPositiveResolutionClearsAsyncQuestion()
    {
        var question = Question("question");
        var pool = new TaskAlertPool();
        pool.ApplySourceSnapshot(Snapshot([Running(question)]));
        pool.ApplySourceSnapshot(Snapshot([Running() with { ResolvedQuestions = [question with { RunId = "other" }] }], 2, 11));
        Assert.Equal(TaskAlertState.Approval, Assert.Single(pool.Assignments).State);
        pool.ApplySourceSnapshot(Snapshot([Running() with { ResolvedQuestions = [question], ReadComplete = false }], 3, 12));
        Assert.Equal(TaskAlertState.Approval, Assert.Single(pool.Assignments).State);
        pool.ApplySourceSnapshot(Snapshot([Running() with { ResolvedQuestions = [question] }], 4, 13));
        Assert.Equal(TaskAlertState.Running, Assert.Single(pool.Assignments).State);
        pool.ApplySourceSnapshot(Snapshot([Running(question)], 5, 14));
        Assert.Equal(TaskAlertState.Running, Assert.Single(pool.Assignments).State);
    }

    [Theory]
    [InlineData(false, TaskAlertState.Approval)]
    [InlineData(true, TaskAlertState.Completed)]
    public void CompletionUsesExistingQuestionPolicy(bool clear, TaskAlertState expected)
    {
        var pool = new TaskAlertPool { ClearQuestionsOnTurnEnd = clear };
        pool.ApplySourceSnapshot(Snapshot([Running(Question("question"))]));
        pool.ApplySourceSnapshot(Snapshot([Running() with { State = TaskAlertState.Completed, CompletedAt = Start.AddSeconds(11) }], 2, 12));
        Assert.Equal(expected, Assert.Single(pool.Assignments).State);
        pool.ApplySourceSnapshot(Snapshot([Running(Question("question"))], 3, 13));
        Assert.Equal(expected, Assert.Single(pool.Assignments).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewWaitingRunDoesNotInheritCompletedRunsTimer(bool restart)
    {
        var oldQuestion = Question("old-question");
        var permission = Question("permission", 21, "next");
        var pool = new TaskAlertPool();
        pool.ApplySourceSnapshot(Snapshot([Running(oldQuestion)]));
        pool.ApplySourceSnapshot(Snapshot([Running() with
            { State = TaskAlertState.Completed, CompletedAt = Start.AddSeconds(11) }], 2, 12));
        Assert.NotNull(Assert.Single(pool.Assignments).CompleteAfter);
        if (restart)
        {
            var saved = pool.CaptureState();
            pool = new TaskAlertPool();
            pool.Restore(saved);
        }

        var waiting = Running() with
        {
            RunId = "next", StartedAt = Start.AddSeconds(20), State = null,
            LatestHumanUserTurnAt = Start.AddSeconds(20), BlockingRequests = [permission],
            RequestsComplete = true,
        };
        pool.ApplySourceSnapshot(Snapshot([waiting], 3, 22));
        pool.Advance(Start.AddSeconds(23));
        var assigned = Assert.Single(pool.Assignments);
        Assert.Equal(TaskAlertState.Approval, assigned.State);
        Assert.Equal("next", assigned.TurnId);
        Assert.Null(assigned.PendingAsyncQuestions);
        Assert.Null(assigned.CompleteAfter);
        Assert.Equal(Start.AddSeconds(20), assigned.UpdatedAt);

        var restored = new TaskAlertPool();
        restored.Restore(pool.CaptureState());
        restored.Advance(Start.AddSeconds(24));
        Assert.Equal(TaskAlertState.Approval, Assert.Single(restored.Assignments).State);
        restored.ApplySourceSnapshot(Snapshot([waiting with { BlockingRequests = [] }], 4, 25));
        Assert.Equal(TaskAlertState.Running, Assert.Single(restored.Assignments).State);
        restored.ApplySourceSnapshot(Snapshot([waiting with { BlockingRequests = [],
            State = TaskAlertState.Completed, CompletedAt = Start.AddSeconds(26) }], 5, 27));
        Assert.Equal(TaskAlertState.Completed, Assert.Single(restored.Assignments).State);
    }

    [Fact]
    public void ConsecutiveCompleteSnapshotsRecognizeAFastRunWithoutBackfillingStartupHistory()
    {
        var pool = new TaskAlertPool();
        var history = Running() with { NativeThreadId = "historical-thread", RunId = "history", State = TaskAlertState.Completed, CompletedAt = Start.AddSeconds(3) };
        Assert.False(pool.ApplySourceSnapshot(Snapshot([history])));
        Assert.Empty(pool.Assignments);
        var fast = Running() with { RunId = "fast", State = TaskAlertState.Completed,
            StartedAt = Start.AddSeconds(11), CompletedAt = Start.AddSeconds(12) };
        Assert.True(pool.ApplySourceSnapshot(Snapshot([history, fast], 2, 13)));
        var assigned = Assert.Single(pool.Assignments);
        Assert.Equal("fast", assigned.TurnId);
        Assert.Equal(TaskAlertState.Completed, assigned.State);
        pool.AcknowledgeTerminal(assigned.Slot, assigned.SessionId, Start.AddSeconds(14));
        pool.ApplySourceSnapshot(Snapshot([fast], 3, 15));
        Assert.Empty(pool.Assignments);
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("unavailable")]
    [InlineData("generation")]
    [InlineData("restart")]
    [InlineData("disabled")]
    [InlineData("same-catalog-boundary")]
    public void FastCompletionRecoveryRequiresAnUninterruptedPositiveSourceBaseline(string interruption)
    {
        var pool = new TaskAlertPool();
        pool.ApplySourceSnapshot(Snapshot([]));
        if (interruption == "partial") pool.ApplySourceSnapshot(Snapshot([], 2, 11, complete: false));
        if (interruption == "unavailable") pool.SetSourceUnavailable(Source);
        if (interruption == "restart") { var state = pool.CaptureState(); pool = new(); pool.Restore(state); }
        if (interruption == "disabled") { pool.SetEnabled(false); pool.SetEnabled(true); }
        var fast = Running() with { RunId = "fast", State = TaskAlertState.Completed,
            StartedAt = Start.AddSeconds(12), CompletedAt = Start.AddSeconds(13) };
        pool.ApplySourceSnapshot(Snapshot([fast], 3, 14,
            interruption == "generation" ? 2 : 1) with
            { CatalogObservedAt = Start.AddSeconds(interruption == "same-catalog-boundary" ? 10 : 14) });
        Assert.Empty(pool.Assignments);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletionDuringPreviousDetailReadsUsesTheCatalogBoundary(bool sameAggregateSequence)
    {
        var pool = new TaskAlertPool();
        pool.ApplySourceSnapshot(Snapshot([], second: 30) with { CatalogObservedAt = Start.AddSeconds(10) });
        var fast = Running() with { RunId = "fast", State = TaskAlertState.Completed,
            StartedAt = Start.AddSeconds(20), CompletedAt = Start.AddSeconds(25) };
        pool.ApplySourceSnapshot(Snapshot([fast], sameAggregateSequence ? 1 : 2, 40) with { CatalogObservedAt = Start.AddSeconds(35) });
        Assert.Equal(TaskAlertState.Completed, Assert.Single(pool.Assignments).State);
    }

    [Theory]
    [InlineData("previous-missing")]
    [InlineData("current-missing")]
    [InlineData("backwards")]
    [InlineData("historical")]
    public void CompletionRecoveryRequiresKnownOrderedCatalogBoundaries(string gap)
    {
        var pool = new TaskAlertPool();
        pool.ApplySourceSnapshot(Snapshot([], second: 30) with
            { CatalogObservedAt = gap == "previous-missing" ? null : Start.AddSeconds(10) });
        var fast = Running() with { RunId = "fast", State = TaskAlertState.Completed,
            StartedAt = Start.AddSeconds(gap == "historical" ? 5 : 20), CompletedAt = Start.AddSeconds(25) };
        pool.ApplySourceSnapshot(Snapshot([fast], 2, 40) with
            { CatalogObservedAt = gap == "current-missing" ? null : Start.AddSeconds(gap == "backwards" ? 9 : 35) });
        Assert.Empty(pool.Assignments);
    }

    [Fact]
    public void BackwardsCatalogBoundaryCannotBecomeALowerHistoryRecoveryFloor()
    {
        var pool = new TaskAlertPool();
        var history = Running() with { State = TaskAlertState.Completed,
            StartedAt = Start.AddSeconds(9), CompletedAt = Start.AddSeconds(25) };
        pool.ApplySourceSnapshot(Snapshot([history], second: 30) with { CatalogObservedAt = Start.AddSeconds(10) });
        pool.ApplySourceSnapshot(Snapshot([history], 2, 40) with { CatalogObservedAt = Start.AddSeconds(8) });
        pool.ApplySourceSnapshot(Snapshot([history], 3, 50) with { CatalogObservedAt = Start.AddSeconds(35) });
        Assert.Empty(pool.Assignments);
        pool.ApplySourceSnapshot(Snapshot([history], 4, 60) with { CatalogObservedAt = Start.AddSeconds(55) });
        Assert.Empty(pool.Assignments);
    }

    [Fact]
    public void HistoricalTerminalDoesNotAllocateAndFaultRemainsFaultAfterStaleRequestReplay()
    {
        var pool = new TaskAlertPool();
        var fault = Running() with { State = TaskAlertState.Fault, CompletedAt = Start.AddSeconds(11) };
        pool.ApplySourceSnapshot(Snapshot([fault], second: 12));
        Assert.Empty(pool.Assignments);
        pool.ApplySourceSnapshot(Snapshot([Running(Question("question"))], 2, 13));
        pool.ApplySourceSnapshot(Snapshot([fault], 3, 14));
        Assert.Equal(TaskAlertState.Fault, Assert.Single(pool.Assignments).State);
        pool.ApplySourceSnapshot(Snapshot([Running(Question("question"))], 4, 15));
        Assert.Equal(TaskAlertState.Fault, Assert.Single(pool.Assignments).State);
    }

    [Fact]
    public void OldGenerationsSequencesAndCapturedTimesCannotOverwriteCurrentRun()
    {
        var pool = new TaskAlertPool();
        pool.ApplySourceSnapshot(Snapshot([Running()], 10, 10, 2));
        var fault = Running() with { State = TaskAlertState.Fault, CompletedAt = Start.AddSeconds(5) };
        Assert.False(pool.ApplySourceSnapshot(Snapshot([fault], 11, 11, 1)));
        Assert.False(pool.ApplySourceSnapshot(Snapshot([fault], 9, 12, 2)));
        Assert.False(pool.ApplySourceSnapshot(Snapshot([fault], 11, 9, 2)));
        Assert.True(pool.ApplySourceSnapshot(Snapshot([Running()], 1, 13, 3)));
        Assert.Equal(TaskAlertState.Running, Assert.Single(pool.Assignments).State);
    }

    [Fact]
    public void FreshReadWithUnchangedSequenceRenewsLeaseAndUnavailabilityDoesNot()
    {
        var pool = new TaskAlertPool();
        pool.ApplySourceSnapshot(Snapshot([Running()]));
        var renewed = Start.AddHours(11);
        pool.ApplySourceSnapshot(new(Source, 1, 1, renewed, true, [Running()]));
        Assert.Equal(renewed, Assert.Single(pool.Assignments).ObservedActiveAt);
        pool.SetSourceUnavailable(Source);
        Assert.True(Assert.Single(pool.Assignments).SourceUnavailable);
        pool.Advance(Start.AddHours(22));
        Assert.Single(pool.Assignments);
        pool.Advance(renewed.Add(TaskAlertPool.RunningLease));
        Assert.Empty(pool.Assignments);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PositivePendingEvidenceRenewsAttentionLeaseWhileWaitingOrCompleted(bool blocking)
    {
        var request = Question("still-pending");
        var observation = Running() with
        {
            State = blocking ? null : TaskAlertState.Completed,
            CompletedAt = blocking ? null : Start.AddSeconds(2),
            BlockingRequests = blocking ? [request] : [], AsyncQuestions = blocking ? [] : [request],
            RequestsComplete = true,
        };
        var pool = new TaskAlertPool();
        pool.ApplySourceSnapshot(Snapshot([observation]));
        var renewed = Start.AddHours(23);
        pool.ApplySourceSnapshot(new(Source, 1, 1, renewed, true, [observation]));
        Assert.Equal(renewed, Assert.Single(pool.Assignments).ObservedActiveAt);
        pool.Advance(Start.AddHours(25));
        Assert.Equal(TaskAlertState.Approval, Assert.Single(pool.Assignments).State);
        pool.SetSourceUnavailable(Source);
        pool.Advance(renewed.Add(TaskAlertPool.AttentionLease));
        Assert.Empty(pool.Assignments);
    }

    [Fact]
    public void PartialOrAbsentPendingEvidenceCannotRenewRetainedAttentionLease()
    {
        var question = Question("retained");
        var waiting = Running(question) with { State = null };
        var pool = new TaskAlertPool();
        pool.ApplySourceSnapshot(Snapshot([waiting]));
        var observed = Assert.Single(pool.Assignments).ObservedActiveAt;
        pool.ApplySourceSnapshot(new(Source, 1, 2, Start.AddHours(23), false, [waiting with { ReadComplete = false }]));
        pool.ApplySourceSnapshot(new(Source, 1, 3, Start.AddHours(23).AddMinutes(1), true, [waiting with { AsyncQuestions = [] }]));
        Assert.Equal(observed, Assert.Single(pool.Assignments).ObservedActiveAt);
        pool.Advance(Start.AddHours(25));
        Assert.Empty(pool.Assignments);
    }

    [Fact]
    public void TerminalAcknowledgementSurvivesRestartAndDoesNotHideNewRun()
    {
        var pool = new TaskAlertPool();
        pool.ApplySourceSnapshot(Snapshot([Running()]));
        var completed = Running() with { State = TaskAlertState.Completed, CompletedAt = Start.AddSeconds(11) };
        pool.ApplySourceSnapshot(Snapshot([completed], 2, 12));
        var task = Assert.Single(pool.Assignments);
        pool.AcknowledgeTerminal(task.Slot, task.SessionId, Start.AddSeconds(13));
        var restored = new TaskAlertPool();
        restored.Restore(pool.CaptureState());
        restored.ApplySourceSnapshot(Snapshot([Running()], second: 14));
        Assert.Empty(restored.Assignments);
        restored.ApplySourceSnapshot(Snapshot([Running() with { RunId = "next", StartedAt = Start.AddSeconds(15) }], 2, 16));
        Assert.Equal("next", Assert.Single(restored.Assignments).TurnId);
    }

    [Fact]
    public void SchemaOneMigratesWithoutMovingSlotsAndBackupIsExactAndCreatedBeforeReplacement()
    {
        var root = Path.Combine(Path.GetTempPath(), "joydex-core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "state.json");
        const string original = "{\"schemaVersion\":1,\"assignments\":[{\"slot\":1,\"sessionId\":\"legacy\",\"turnId\":\"turn\",\"state\":\"running\",\"updatedAt\":\"2026-10-01T12:00:00Z\",\"completeAfter\":null,\"correlatedAttention\":[],\"uncorrelatedAttentionCount\":0}]}";
        try
        {
            File.WriteAllText(path, original);
            var pool = new TaskAlertPool();
            pool.Restore(TaskAlertStateStore.Load(path));
            var legacy = Assert.Single(pool.Assignments);
            Assert.Equal(1, legacy.Slot);
            Assert.Equal(TaskAlertIdentity.LocalCodex("legacy"), legacy.TaskIdentity);
            Assert.False(File.Exists(TaskAlertStateStore.LegacyBackupPath(path)));
            TaskAlertStateStore.Save(path, pool.CaptureState());
            Assert.Equal(original, File.ReadAllText(TaskAlertStateStore.LegacyBackupPath(path)));
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(2, json.RootElement.GetProperty("schemaVersion").GetInt32());
            TaskAlertStateStore.Save(path, TaskAlertPoolState.Empty);
            Assert.Equal(original, File.ReadAllText(TaskAlertStateStore.LegacyBackupPath(path)));
        }
        finally { Directory.Delete(root, true); }
    }
}
