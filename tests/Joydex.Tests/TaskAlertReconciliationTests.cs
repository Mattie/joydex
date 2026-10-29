using System.Text.Json;
using Joydex.Core.TaskAlerts;
using Joydex.Windows.TaskAlerts;

namespace Joydex.Tests;

public sealed class TaskAlertReconciliationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly string Session = Guid.NewGuid().ToString("D");
    private static readonly string QuestionKey = new('A', 64);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ANewTurnInTheSameSecondAsThePreviousStopCanRecoverItsMissedStart(bool graceElapsed)
    {
        var pool = new TaskAlertPool();
        var second = DateTimeOffset.FromUnixTimeSeconds(Now.AddMinutes(-1).ToUnixTimeSeconds());
        pool.Apply(new(CodexLifecycleEvent.UserPromptSubmit, Session, "previous", second.AddMinutes(-1)));
        pool.Apply(new(CodexLifecycleEvent.Stop, Session, "previous", second.AddMilliseconds(700)));
        if (graceElapsed) pool.Advance(second.AddSeconds(3));
        Assert.Equal(second.AddMilliseconds(700), Assert.Single(pool.Assignments).UpdatedAt);
        Assert.True(pool.Reconcile(new(Session, "next", TaskAlertState.Running, second, null), Now));
        Assert.Equal("next", Assert.Single(pool.Assignments).TurnId);
        Assert.Equal(TaskAlertState.Running, Assert.Single(pool.Assignments).State);
    }

    [Fact]
    public async Task AConfirmedIgnoredWorkspaceRemovesALegacyAssignmentWithoutCwd()
    {
        using var scratch = new Scratch();
        await using var coordinator = new TaskAlertCoordinator(scratch.Preferences);
        coordinator.AddSuppression(TaskAlertSuppressionScope.Workspace, scratch.Root);
        coordinator.TryPublish(new(CodexLifecycleEvent.UserPromptSubmit, Session, "turn", Now));
        await UntilAsync(() => coordinator.GetSnapshot().Assignments.Count == 1);
        Assert.Equal(1, coordinator.Reconcile(coordinator.CaptureReconciliationCheckpoint(), new([
            new(Session, "turn", TaskAlertState.Running, Now.AddMinutes(-1), null, scratch.Root)])));
        Assert.Empty(coordinator.GetSnapshot().Assignments);
    }

    [Fact]
    public async Task PolicyChangesInvalidateAnInFlightScanAndUnrelatedHooksDoNot()
    {
        using var scratch = new Scratch();
        await using var coordinator = new TaskAlertCoordinator(scratch.Preferences);
        var observation = new TaskAlertDesktopObservation(Session, "turn", TaskAlertState.Running,
            Now.AddMinutes(-1), null, scratch.Root);
        var checkpoint = coordinator.CaptureReconciliationCheckpoint();
        coordinator.SetEnabled(false);
        coordinator.SetEnabled(true);
        Assert.Equal(0, coordinator.Reconcile(checkpoint, new([observation])));
        checkpoint = coordinator.CaptureReconciliationCheckpoint();
        coordinator.SetInternallySuppressedTaskIds([Session]);
        Assert.Equal(0, coordinator.Reconcile(checkpoint, new([observation])));
        coordinator.SetInternallySuppressedTaskIds([]);
        checkpoint = coordinator.CaptureReconciliationCheckpoint();
        coordinator.TryPublish(new(CodexLifecycleEvent.ToolCompleted, Guid.NewGuid().ToString(), "other", Now));
        Assert.Equal(1, coordinator.Reconcile(checkpoint, new([observation])));
    }

    [Fact]
    public async Task DismissedQuestionsStayDismissedAcrossLaterActiveObservations()
    {
        using var scratch = new Scratch();
        await using var coordinator = new TaskAlertCoordinator(scratch.Preferences);
        coordinator.TryPublish(new(CodexLifecycleEvent.AsyncUserInputRequest, Session, "turn", Now, QuestionKey));
        await UntilAsync(() => coordinator.GetSnapshot().Assignments.SingleOrDefault()?.State == TaskAlertState.Approval);
        var observation = new TaskAlertDesktopObservation(Session, "turn", TaskAlertState.Running,
            Now.AddMinutes(-1), null, scratch.Root);
        var checkpoint = coordinator.CaptureReconciliationCheckpoint();
        var assignment = Assert.Single(coordinator.GetSnapshot().Assignments);
        Assert.True(coordinator.DismissQuestions(assignment.Slot, Session, [QuestionKey]));
        Assert.Equal(0, coordinator.Reconcile(checkpoint, new([observation])));
        coordinator.Reconcile(coordinator.CaptureReconciliationCheckpoint(), new([observation]));
        Assert.Equal(TaskAlertState.Running, Assert.Single(coordinator.GetSnapshot().Assignments).State);
        Assert.Null(Assert.Single(coordinator.GetSnapshot().Assignments).PendingAsyncQuestions);
    }

    [Fact]
    public async Task UnavailableReadsDoNotEraseExistingState()
    {
        using var scratch = new Scratch();
        await using var coordinator = new TaskAlertCoordinator(scratch.Preferences);
        coordinator.TryPublish(new(CodexLifecycleEvent.UserPromptSubmit, Session, "turn", Now));
        await UntilAsync(() => coordinator.GetSnapshot().Assignments.Count == 1);
        await using var reconciler = new TaskAlertReconciler(coordinator,
            (_, _) => Task.FromException<TaskAlertDesktopSnapshot>(new IOException("fixture unavailable")), _ => { });
        await Assert.ThrowsAsync<IOException>(() => reconciler.ReconcileOnceAsync(default));
        Assert.Equal(TaskAlertState.Running, Assert.Single(coordinator.GetSnapshot().Assignments).State);
    }

    [Fact]
    public void LegacyHooksWithoutTurnIdentityRequireASpanningTurn()
    {
        var pool = new TaskAlertPool();
        pool.Apply(new(CodexLifecycleEvent.UserPromptSubmit, Session, null, Now.AddMinutes(-2)));
        Assert.False(pool.Reconcile(new(Session, "old", TaskAlertState.Fault,
            Now.AddMinutes(-4), Now.AddMinutes(-3)), Now));
        Assert.True(pool.Reconcile(new(Session, "current", TaskAlertState.Fault,
            Now.AddMinutes(-3), Now.AddMinutes(-1)), Now));
        Assert.Equal(TaskAlertState.Fault, Assert.Single(pool.Assignments).State);
    }

    [Fact]
    public async Task APostToolHookSuppliesIdentityEvenWhenTheStartWasLost()
    {
        using var scratch = new Scratch();
        await using var coordinator = new TaskAlertCoordinator(scratch.Preferences);
        coordinator.TryPublish(new(CodexLifecycleEvent.ToolCompleted, Session, "turn", Now));
        Assert.Contains(Session, coordinator.CaptureReconciliationCheckpoint().KnownTaskIds);
        await UntilAsync(() => coordinator.Reconcile(coordinator.CaptureReconciliationCheckpoint(), new([
            new(Session, "turn", TaskAlertState.Running, Now.AddMinutes(-1), null, scratch.Root)])) > 0);
        Assert.Equal(TaskAlertState.Running, Assert.Single(coordinator.GetSnapshot().Assignments).State);
    }

    [Fact]
    public async Task DesktopReaderIncludesKnownTasksOutsideCatalogAndExcludesRemoteAndCloudEntries()
    {
        var missing = Guid.NewGuid().ToString();
        var remote = Guid.NewGuid().ToString();
        var cloud = Guid.NewGuid().ToString();
        var reads = new List<string>();
        var result = await TaskAlertDesktopReader.ReadAsync([missing], (tool, arguments, _) =>
        {
            if (tool == "list_threads") return Task.FromResult(JsonSerializer.Serialize(new
            {
                threads = new[]
                {
                    new { id = Session, kind = "codex", hostId = "local", status = "active" },
                    new { id = remote, kind = "codex", hostId = "remote", status = "active" },
                    new { id = cloud, kind = "chatgpt", hostId = "local", status = "active" },
                },
            }));
            var id = arguments.GetProperty("threadId").GetString()!;
            reads.Add(id);
            Assert.Equal(1, arguments.GetProperty("turnLimit").GetInt32());
            Assert.False(arguments.GetProperty("includeOutputs").GetBoolean());
            using var document = Metadata("active", "inProgress", id);
            return Task.FromResult(document.RootElement.GetRawText());
        }, default);
        Assert.Equal(2, result.Tasks.Length);
        Assert.Contains(Session, reads);
        Assert.Contains(missing, reads);
        Assert.DoesNotContain(remote, reads);
        Assert.DoesNotContain(cloud, reads);
    }

    [Fact]
    public async Task OversizedWorkspaceMetadataKeepsTheFrameBoundedAndPreservesIgnoreKeys()
    {
        var ids = Enumerable.Range(0, 50).Select(_ => Guid.NewGuid().ToString()).ToArray();
        var result = await TaskAlertDesktopReader.ReadAsync([], (tool, arguments, _) =>
        {
            if (tool == "list_threads") return Task.FromResult(JsonSerializer.Serialize(new
            {
                threads = ids.Select(id => new { id, kind = "codex", hostId = "local", status = "active" }),
            }));
            using var document = Metadata("active", "inProgress", arguments.GetProperty("threadId").GetString(),
                @"C:\fixture\" + new string('\u732b', 2000));
            return Task.FromResult(document.RootElement.GetRawText());
        }, default);
        Assert.Equal(50, result.Tasks.Length);
        Assert.All(result.Tasks, item =>
        {
            Assert.Null(item.Workspace);
            Assert.True(TaskAlertSuppression.IsWorkspaceKey(item.WorkspaceKey));
        });
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(result).Length < Joydex.Core.Voice.DesktopTaskBridgeProtocol.MaximumFrameBytes);
    }

    [Fact]
    public void RecoversMissingLongRunningStartWithoutExpiringOrAdvancingHookWatermark()
    {
        var pool = new TaskAlertPool();
        var start = Now.AddHours(-20);
        Assert.True(pool.Reconcile(new(Session, "turn", TaskAlertState.Running, start, null), Now));
        Assert.Equal(start, Assert.Single(pool.Assignments).UpdatedAt);
        pool.Advance(Now);
        Assert.Single(pool.Assignments);
        var restored = new TaskAlertPool();
        restored.Restore(pool.CaptureState());
        restored.Advance(Now.AddHours(11));
        Assert.Single(restored.Assignments);
        Assert.True(restored.Apply(new(CodexLifecycleEvent.PermissionRequest, Session, "turn", Now.AddSeconds(-1), QuestionKey)));
        Assert.Equal(TaskAlertState.Approval, Assert.Single(restored.Assignments).State);
    }

    [Fact]
    public void TerminalObservationClearsStaleRunningButAnOlderTurnCannotOverwriteANewerHook()
    {
        var pool = new TaskAlertPool();
        pool.Apply(new(CodexLifecycleEvent.UserPromptSubmit, Session, "old", Now.AddMinutes(-2)));
        Assert.True(pool.Reconcile(new(Session, "old", TaskAlertState.Fault, Now.AddMinutes(-3), Now.AddMinutes(-1)), Now));
        Assert.Equal(TaskAlertState.Fault, Assert.Single(pool.Assignments).State);
        pool.Apply(new(CodexLifecycleEvent.UserPromptSubmit, Session, "new", Now));
        Assert.False(pool.Reconcile(new(Session, "old", TaskAlertState.Fault, Now.AddMinutes(-3), Now.AddMinutes(-1)), Now));
        Assert.Equal("new", Assert.Single(pool.Assignments).TurnId);
        Assert.False(new TaskAlertPool().Reconcile(new(Session, "old", TaskAlertState.Completed,
            Now.AddMinutes(-3), Now.AddMinutes(-1)), Now));
    }

    [Theory]
    [InlineData(TaskAlertState.Completed)]
    [InlineData(TaskAlertState.Fault)]
    public void ConfirmedTerminalTurnsRetireQuestionAttention(TaskAlertState terminal)
    {
        var pool = new TaskAlertPool { ClearQuestionsOnTurnEnd = true };
        pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, Session, "turn", Now.AddMinutes(-2), QuestionKey));
        pool.Reconcile(new(Session, "turn", terminal, Now.AddMinutes(-3), Now.AddMinutes(-1)), Now);
        var assignment = Assert.Single(pool.Assignments);
        Assert.Equal(terminal, assignment.State);
        Assert.Null(assignment.PendingAsyncQuestions);
        pool.Apply(new(CodexLifecycleEvent.UserPromptSubmit, Session, "next", Now));
        pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, Session, "next", Now, QuestionKey));
        pool.Reconcile(new(Session, "next", TaskAlertState.Fault, Now, Now.AddSeconds(1)), Now.AddSeconds(2));
        Assert.Equal(TaskAlertState.Fault, Assert.Single(pool.Assignments).State);
        Assert.Null(Assert.Single(pool.Assignments).PendingAsyncQuestions);
    }

    [Fact]
    public async Task AHookAcceptedDuringTheReadSupersedesTheOlderObservation()
    {
        using var scratch = new Scratch();
        await using var coordinator = new TaskAlertCoordinator(scratch.Preferences);
        coordinator.TryPublish(new(CodexLifecycleEvent.UserPromptSubmit, Session, "old", Now.AddMinutes(-2)));
        await UntilAsync(() => coordinator.GetSnapshot().Assignments.Count == 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource<TaskAlertDesktopSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reconciler = new TaskAlertReconciler(coordinator, (_, _) =>
        {
            started.TrySetResult();
            return reply.Task;
        }, _ => { });
        var scan = reconciler.ReconcileOnceAsync(default);
        await started.Task;
        coordinator.TryPublish(new(CodexLifecycleEvent.PermissionRequest, Session, "new", DateTimeOffset.UtcNow, QuestionKey));
        reply.SetResult(new([new(Session, "old", TaskAlertState.Fault, Now.AddMinutes(-3), Now.AddMinutes(-1))]));
        Assert.Equal(0, await scan);
        await UntilAsync(() => coordinator.GetSnapshot().Assignments.Single().TurnId == "new");
        Assert.Equal(TaskAlertState.Approval, coordinator.GetSnapshot().Assignments.Single().State);
    }

    [Fact]
    public async Task RecoveryHonorsIgnoresAndAcknowledgmentsAndDoesNotResurrectHistoricalTasks()
    {
        using var scratch = new Scratch();
        await using var coordinator = new TaskAlertCoordinator(scratch.Preferences);
        var workspace = Path.GetFullPath(scratch.Root);
        var active = new TaskAlertDesktopObservation(Session, "turn", TaskAlertState.Running, Now.AddMinutes(-2), null, workspace);
        coordinator.AddSuppression(TaskAlertSuppressionScope.Workspace, workspace);
        Assert.Equal(0, coordinator.Reconcile(coordinator.CaptureReconciliationCheckpoint(), new([active])));
        Assert.Empty(coordinator.GetSnapshot().Assignments);
        var rule = Assert.Single(coordinator.GetSnapshot().Suppressions!);
        coordinator.RemoveSuppression(rule);
        Assert.Equal(1, coordinator.Reconcile(coordinator.CaptureReconciliationCheckpoint(), new([active])));
        coordinator.Reconcile(coordinator.CaptureReconciliationCheckpoint(), new([active with
        {
            State = TaskAlertState.Fault, CompletedAt = Now.AddMinutes(-1),
        }]));
        var assignment = Assert.Single(coordinator.GetSnapshot().Assignments);
        Assert.True(coordinator.AcknowledgeTerminal(assignment.Slot, Session));
        Assert.Equal(0, coordinator.Reconcile(coordinator.CaptureReconciliationCheckpoint(), new([active with
        {
            TurnId = "even-older", StartedAt = Now.AddMinutes(-3),
        }])));
        Assert.Equal(0, coordinator.Reconcile(coordinator.CaptureReconciliationCheckpoint(), new([active])));
        Assert.Empty(coordinator.GetSnapshot().Assignments);
        Assert.Equal(0, coordinator.Reconcile(coordinator.CaptureReconciliationCheckpoint(), new([active with
        {
            State = TaskAlertState.Completed, CompletedAt = Now.AddMinutes(-1),
        }])));
    }

    [Fact]
    public async Task WorkerCancellationJoinsAnInFlightRead()
    {
        using var scratch = new Scratch();
        await using var coordinator = new TaskAlertCoordinator(scratch.Preferences);
        coordinator.TryPublish(new(CodexLifecycleEvent.UserPromptSubmit, Session, "turn", Now));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconciler = new TaskAlertReconciler(coordinator, async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new([]);
        }, _ => { });
        reconciler.Start();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await reconciler.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(reconciler.Completion.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData("notLoaded", "failed", TaskAlertState.Fault)]
    [InlineData("idle", "interrupted", TaskAlertState.Fault)]
    [InlineData("idle", "completed", TaskAlertState.Completed)]
    [InlineData("active", "inProgress", TaskAlertState.Running)]
    public void DesktopProjectionUsesExplicitLocalTurnMetadataOnly(string status, string turnStatus, TaskAlertState expected)
    {
        using var document = Metadata(status, turnStatus);
        var observation = TaskAlertDesktopReader.Parse(document.RootElement, Session);
        Assert.Equal(expected, observation!.State);
        Assert.DoesNotContain("Private fixture content", JsonSerializer.Serialize(observation));
    }

    [Theory]
    [InlineData("notLoaded", "inProgress")]
    [InlineData("active", "failed")]
    [InlineData("unknown", "unknown")]
    [InlineData("unknown", "failed")]
    public void AmbiguousDesktopMetadataDoesNotProduceATerminalOrRunningObservation(string status, string turnStatus)
    {
        using var document = Metadata(status, turnStatus);
        Assert.Null(TaskAlertDesktopReader.Parse(document.RootElement, Session));
    }

    private static JsonDocument Metadata(string status, string turnStatus, string? id = null, string? workspace = null) => JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        thread = new { id = id ?? Session, kind = "codex", hostId = "local", status, cwd = workspace ?? @"C:\fixture" },
        page = new { order = "newest_first" },
        turns = new[] { new { id = "turn", status = turnStatus, startedAt = Now.AddMinutes(-2).ToUnixTimeSeconds(),
            completedAt = Now.AddMinutes(-1).ToUnixTimeSeconds(), items = new[] { new { text = "Private fixture content" } } } },
    }));

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }

    private sealed class Scratch : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "joydex-reconciliation", Guid.NewGuid().ToString("N"));
        public string Preferences => Path.Combine(Root, "task-alerts.json");
        public Scratch() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
