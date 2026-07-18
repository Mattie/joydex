using Joydex.Core.TaskAlerts;

namespace Joydex.Tests;

public sealed class TaskAlertPoolTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AllocatesLowestFreeChannelAndUpdatesExistingSession()
    {
        var pool = new TaskAlertPool([5, 1, 4, 2]);

        Assert.True(pool.Apply(Event(CodexLifecycleEvent.UserPromptSubmit, "session-a", Start)));
        Assert.True(pool.Apply(Event(CodexLifecycleEvent.PermissionRequest, "session-b", Start.AddSeconds(1))));
        Assert.True(pool.Apply(Event(CodexLifecycleEvent.PermissionRequest, "session-a", Start.AddSeconds(2))));

        Assert.Collection(
            pool.Assignments,
            first =>
            {
                Assert.Equal(1, first.Channel);
                Assert.Equal("session-a", first.SessionId);
                Assert.Equal(TaskAlertState.Approval, first.State);
            },
            second => Assert.Equal(2, second.Channel));
    }

    [Fact]
    public void DropsOverflowWithoutBacklogAndAllowsLaterRetry()
    {
        var pool = new TaskAlertPool([1]);
        pool.Apply(Event(CodexLifecycleEvent.UserPromptSubmit, "session-a", Start));

        Assert.False(pool.Apply(Event(CodexLifecycleEvent.UserPromptSubmit, "session-b", Start)));
        Assert.Equal(1, pool.DroppedEventCount);
        Assert.True(pool.Acknowledge(1, "session-a"));
        Assert.Empty(pool.Assignments);

        Assert.True(pool.Apply(Event(CodexLifecycleEvent.PermissionRequest, "session-b", Start.AddSeconds(1))));
        Assert.Equal("session-b", Assert.Single(pool.Assignments).SessionId);
    }

    [Fact]
    public void StopUsesGraceAndContinuationCancelsCompletion()
    {
        var pool = new TaskAlertPool([1]);
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
        var running = new TaskAlertPool([1]);
        running.Apply(Event(CodexLifecycleEvent.UserPromptSubmit, "run", Start));
        Assert.True(running.Advance(Start + TaskAlertPool.RunningLease));
        Assert.Empty(running.Assignments);

        var approval = new TaskAlertPool([1]);
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
        var pool = new TaskAlertPool([1]);
        pool.Apply(Event(CodexLifecycleEvent.PermissionRequest, "session-a", Start.AddSeconds(2)));

        Assert.False(pool.Apply(Event(CodexLifecycleEvent.UserPromptSubmit, "session-a", Start)));
        Assert.Equal(TaskAlertState.Approval, Assert.Single(pool.Assignments).State);
    }

    private static TaskAlertEvent Event(
        CodexLifecycleEvent lifecycleEvent,
        string sessionId,
        DateTimeOffset at) => new(lifecycleEvent, sessionId, "turn-1", at);
}
