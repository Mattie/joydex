using Joydex.Core.TaskAlerts;

namespace Joydex.Tests;

public sealed class TaskAlertPoolTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly string AttentionKeyA = new('A', 64);
    private static readonly string AttentionKeyB = new('B', 64);

    [Fact]
    public void AllocatesLowestFreeSlotAndUpdatesExistingSession()
    {
        var pool = new TaskAlertPool();

        Assert.True(pool.Apply(Event(CodexLifecycleEvent.UserPromptSubmit, "session-a", Start)));
        Assert.True(pool.Apply(Event(CodexLifecycleEvent.PermissionRequest, "session-b", Start.AddSeconds(1))));
        Assert.True(pool.Apply(Event(CodexLifecycleEvent.PermissionRequest, "session-a", Start.AddSeconds(2))));

        Assert.Collection(
            pool.Assignments,
            first =>
            {
                Assert.Equal(1, first.Slot);
                Assert.Equal("session-a", first.SessionId);
                Assert.Equal(TaskAlertState.Approval, first.State);
            },
            second => Assert.Equal(2, second.Slot));
    }

    [Theory]
    [InlineData(CodexLifecycleEvent.PermissionRequest)]
    [InlineData(CodexLifecycleEvent.UserInputRequest)]
    public void MatchingToolCompletionClearsAttention(CodexLifecycleEvent attentionEvent)
    {
        var pool = new TaskAlertPool();
        pool.Apply(Event(CodexLifecycleEvent.UserPromptSubmit, "session-a", Start));
        pool.Apply(Event(attentionEvent, "session-a", Start.AddSeconds(1), AttentionKeyA));

        Assert.True(pool.Apply(Event(
            CodexLifecycleEvent.ToolCompleted,
            "session-a",
            Start.AddSeconds(2),
            AttentionKeyA)));

        Assert.Equal(TaskAlertState.Running, Assert.Single(pool.Assignments).State);
    }

    [Fact]
    public void UnrelatedParallelToolCompletionLeavesAttentionActive()
    {
        var pool = new TaskAlertPool();
        pool.Apply(Event(
            CodexLifecycleEvent.PermissionRequest,
            "session-a",
            Start,
            AttentionKeyA));

        Assert.False(pool.Apply(Event(
            CodexLifecycleEvent.ToolCompleted,
            "session-a",
            Start.AddSeconds(1),
            AttentionKeyB)));
        Assert.Equal(TaskAlertState.Approval, Assert.Single(pool.Assignments).State);
    }

    [Fact]
    public void AttentionClearsOnlyAfterEveryParallelToolCompletes()
    {
        var pool = new TaskAlertPool();
        pool.Apply(Event(CodexLifecycleEvent.PermissionRequest, "session-a", Start, AttentionKeyA));
        pool.Apply(Event(
            CodexLifecycleEvent.PermissionRequest,
            "session-a",
            Start.AddSeconds(1),
            AttentionKeyB));

        pool.Apply(Event(
            CodexLifecycleEvent.ToolCompleted,
            "session-a",
            Start.AddSeconds(2),
            AttentionKeyA));
        Assert.Equal(TaskAlertState.Approval, Assert.Single(pool.Assignments).State);

        pool.Apply(Event(
            CodexLifecycleEvent.ToolCompleted,
            "session-a",
            Start.AddSeconds(3),
            AttentionKeyB));
        Assert.Equal(TaskAlertState.Running, Assert.Single(pool.Assignments).State);
    }

    [Fact]
    public void DuplicateAttentionKeysRequireMatchingCompletionCount()
    {
        var pool = new TaskAlertPool();
        pool.Apply(Event(CodexLifecycleEvent.PermissionRequest, "session-a", Start, AttentionKeyA));
        pool.Apply(Event(
            CodexLifecycleEvent.PermissionRequest,
            "session-a",
            Start.AddSeconds(1),
            AttentionKeyA));

        pool.Apply(Event(
            CodexLifecycleEvent.ToolCompleted,
            "session-a",
            Start.AddSeconds(2),
            AttentionKeyA));
        Assert.Equal(TaskAlertState.Approval, Assert.Single(pool.Assignments).State);

        pool.Apply(Event(
            CodexLifecycleEvent.ToolCompleted,
            "session-a",
            Start.AddSeconds(3),
            AttentionKeyA));
        Assert.Equal(TaskAlertState.Running, Assert.Single(pool.Assignments).State);
    }

    [Fact]
    public void UncorrelatedAttentionKeepsCurrentFallbackBehavior()
    {
        var pool = new TaskAlertPool();
        pool.Apply(Event(CodexLifecycleEvent.PermissionRequest, "session-a", Start));

        Assert.False(pool.Apply(Event(
            CodexLifecycleEvent.ToolCompleted,
            "session-a",
            Start.AddSeconds(1),
            AttentionKeyA)));
        Assert.Equal(TaskAlertState.Approval, Assert.Single(pool.Assignments).State);
    }

    [Fact]
    public void InvalidCorrelationKeyUsesContentFreeFallbackState()
    {
        var pool = new TaskAlertPool();
        pool.Apply(Event(
            CodexLifecycleEvent.PermissionRequest,
            "session-a",
            Start,
            "raw-private-value"));

        var stored = Assert.Single(pool.CaptureState().Assignments);
        Assert.Empty(stored.CorrelatedAttention);
        Assert.Equal(1, stored.UncorrelatedAttentionCount);
    }

    [Fact]
    public void DropsOverflowWithoutBacklogAndAllowsLaterRetry()
    {
        var pool = new TaskAlertPool();
        foreach (var index in Enumerable.Range(1, 10))
        {
            pool.Apply(Event(CodexLifecycleEvent.UserPromptSubmit, $"session-{index}", Start));
        }

        Assert.Equal(Enumerable.Range(1, 10), pool.Assignments.Select(assignment => assignment.Slot));
        Assert.False(pool.Apply(Event(CodexLifecycleEvent.UserPromptSubmit, "session-11", Start)));
        Assert.Equal(1, pool.DroppedEventCount);
        pool.Apply(Event(CodexLifecycleEvent.Fault, "session-1", Start.AddMilliseconds(500)));
        Assert.True(pool.AcknowledgeTerminal(1, "session-1"));

        Assert.True(pool.Apply(Event(CodexLifecycleEvent.PermissionRequest, "session-11", Start.AddSeconds(1))));
        Assert.Equal("session-11", pool.Assignments.Single(assignment => assignment.Slot == 1).SessionId);
        Assert.Equal("session-5", pool.Assignments.Single(assignment => assignment.Slot == 5).SessionId);
    }

    [Theory]
    [InlineData(CodexLifecycleEvent.UserPromptSubmit, TaskAlertState.Running)]
    [InlineData(CodexLifecycleEvent.PermissionRequest, TaskAlertState.Approval)]
    public void NavigationDoesNotAcknowledgeNonTerminalAssignment(
        CodexLifecycleEvent lifecycleEvent,
        TaskAlertState expectedState)
    {
        var pool = new TaskAlertPool();
        pool.Apply(Event(lifecycleEvent, "session-a", Start));

        Assert.False(pool.AcknowledgeTerminal(1, "session-a"));

        var assignment = Assert.Single(pool.Assignments);
        Assert.Equal("session-a", assignment.SessionId);
        Assert.Equal(expectedState, assignment.State);
    }

    [Theory]
    [InlineData(CodexLifecycleEvent.Stop, TaskAlertState.Completed)]
    [InlineData(CodexLifecycleEvent.Fault, TaskAlertState.Fault)]
    public void NavigationAcknowledgesTerminalAssignment(
        CodexLifecycleEvent lifecycleEvent,
        TaskAlertState expectedState)
    {
        var pool = new TaskAlertPool();
        pool.Apply(Event(CodexLifecycleEvent.UserPromptSubmit, "session-a", Start));
        pool.Apply(Event(lifecycleEvent, "session-a", Start.AddSeconds(1)));
        pool.Advance(Start.AddSeconds(2));
        Assert.Equal(expectedState, Assert.Single(pool.Assignments).State);

        Assert.True(pool.AcknowledgeTerminal(1, "session-a"));
        Assert.Empty(pool.Assignments);
    }

    [Fact]
    public void StopUsesGraceAndContinuationCancelsCompletion()
    {
        var pool = new TaskAlertPool();
        pool.Apply(Event(CodexLifecycleEvent.UserPromptSubmit, "session-a", Start));
        pool.Apply(Event(CodexLifecycleEvent.Stop, "session-a", Start.AddSeconds(1)));

        pool.Advance(Start.AddMilliseconds(1500));
        Assert.Equal(TaskAlertState.Running, Assert.Single(pool.Assignments).State);

        pool.Apply(Event(CodexLifecycleEvent.UserPromptSubmit, "session-a", Start.AddMilliseconds(1750)));
        pool.Advance(Start.AddSeconds(3));
        Assert.Equal(TaskAlertState.Running, Assert.Single(pool.Assignments).State);

        pool.Apply(Event(CodexLifecycleEvent.Stop, "session-a", Start.AddSeconds(4)));
        pool.Advance(Start.AddSeconds(5));
        Assert.Equal(TaskAlertState.Completed, Assert.Single(pool.Assignments).State);
    }

    [Fact]
    public void ExpiresRunningAndAttentionLeases()
    {
        var running = new TaskAlertPool();
        running.Apply(Event(CodexLifecycleEvent.UserPromptSubmit, "run", Start));
        Assert.True(running.Advance(Start + TaskAlertPool.RunningLease));
        Assert.Empty(running.Assignments);

        var approval = new TaskAlertPool();
        approval.Apply(Event(CodexLifecycleEvent.PermissionRequest, "approval", Start));
        Assert.True(approval.Advance(Start + TaskAlertPool.AttentionLease));
        Assert.Empty(approval.Assignments);
    }

    [Fact]
    public void DisableClearsAndDiscardsEvents()
    {
        var pool = new TaskAlertPool();
        pool.Apply(Event(CodexLifecycleEvent.UserPromptSubmit, "session-a", Start));

        Assert.True(pool.SetEnabled(false));
        Assert.Empty(pool.Assignments);
        Assert.False(pool.Apply(Event(CodexLifecycleEvent.PermissionRequest, "session-b", Start)));
    }

    [Fact]
    public void LateConcurrentEventCannotReplaceNewerState()
    {
        var pool = new TaskAlertPool();
        pool.Apply(Event(CodexLifecycleEvent.PermissionRequest, "session-a", Start.AddSeconds(2)));

        Assert.False(pool.Apply(Event(CodexLifecycleEvent.UserPromptSubmit, "session-a", Start)));
        Assert.Equal(TaskAlertState.Approval, Assert.Single(pool.Assignments).State);
    }

    [Fact]
    public void CapturesAndRestoresAssignmentsDeadlinesAndPendingAttention()
    {
        var pool = new TaskAlertPool();
        pool.Apply(Event(CodexLifecycleEvent.UserPromptSubmit, "running", Start));
        pool.Apply(Event(CodexLifecycleEvent.PermissionRequest, "correlated", Start, AttentionKeyA));
        pool.Apply(Event(
            CodexLifecycleEvent.PermissionRequest,
            "correlated",
            Start.AddSeconds(1),
            AttentionKeyA));
        pool.Apply(Event(CodexLifecycleEvent.PermissionRequest, "uncorrelated", Start));
        pool.Apply(Event(CodexLifecycleEvent.UserPromptSubmit, "completed", Start));
        pool.Apply(Event(CodexLifecycleEvent.Stop, "completed", Start.AddSeconds(1)));
        pool.Advance(Start.AddSeconds(2));
        pool.Apply(Event(CodexLifecycleEvent.Fault, "fault", Start.AddSeconds(2)));
        pool.Apply(Event(CodexLifecycleEvent.UserPromptSubmit, "stopping", Start.AddSeconds(2)));
        pool.Apply(Event(CodexLifecycleEvent.Stop, "stopping", Start.AddSeconds(3)));

        var captured = pool.CaptureState();
        Assert.Equal(
            2,
            Assert.Single(captured.Assignments.Single(item => item.SessionId == "correlated").CorrelatedAttention)
                .Count);
        Assert.Equal(
            1,
            captured.Assignments.Single(item => item.SessionId == "uncorrelated").UncorrelatedAttentionCount);

        var restored = new TaskAlertPool();
        restored.Restore(captured);

        Assert.Equal(pool.Assignments, restored.Assignments);
        Assert.NotNull(restored.Assignments.Single(item => item.SessionId == "stopping").CompleteAfter);
        Assert.Equal(
            [TaskAlertState.Running, TaskAlertState.Approval, TaskAlertState.Approval, TaskAlertState.Completed,
                TaskAlertState.Fault, TaskAlertState.Running],
            restored.Assignments.Select(item => item.State));

        Assert.True(restored.Apply(Event(
            CodexLifecycleEvent.ToolCompleted,
            "correlated",
            Start.AddSeconds(4),
            AttentionKeyA)));
        Assert.Equal(
            TaskAlertState.Approval,
            restored.Assignments.Single(item => item.SessionId == "correlated").State);
        Assert.True(restored.Apply(Event(
            CodexLifecycleEvent.ToolCompleted,
            "correlated",
            Start.AddSeconds(5),
            AttentionKeyA)));
        Assert.Equal(
            TaskAlertState.Running,
            restored.Assignments.Single(item => item.SessionId == "correlated").State);
        Assert.False(restored.Apply(Event(
            CodexLifecycleEvent.ToolCompleted,
            "uncorrelated",
            Start.AddSeconds(5),
            AttentionKeyA)));
    }

    private static TaskAlertEvent Event(
        CodexLifecycleEvent lifecycleEvent,
        string sessionId,
        DateTimeOffset at,
        string? attentionKey = null) => new(
            lifecycleEvent,
            sessionId,
            "turn-1",
            at,
            attentionKey);
}
