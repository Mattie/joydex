using Joydex.Core.TaskAlerts;

namespace Joydex.Tests;

public sealed class TaskAlertTurnEndPolicyTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UtcNow;
    private static readonly string Key = new('A', 64);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StopUsesTheSelectedPolicyAcrossRestartAndANewPromptAlwaysAcknowledges(bool clearOnEnd)
    {
        var pool = new TaskAlertPool { ClearQuestionsOnTurnEnd = clearOnEnd };
        pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "chat", "turn", At, Key));
        pool.Apply(new(CodexLifecycleEvent.Stop, "chat", "turn", At.AddSeconds(1)));
        pool.Advance(At.AddSeconds(3));
        var restored = new TaskAlertPool { ClearQuestionsOnTurnEnd = clearOnEnd };
        restored.Restore(pool.CaptureState());
        var assignment = Assert.Single(restored.Assignments);
        Assert.Equal(clearOnEnd ? TaskAlertState.Completed : TaskAlertState.Approval, assignment.State);
        Assert.Equal(!clearOnEnd, assignment.PendingAsyncQuestions is { Length: 1 });
        if (!clearOnEnd) Assert.NotNull(assignment.CompleteAfter);
        restored.Apply(new(CodexLifecycleEvent.UserPromptSubmit, "chat", "next", At.AddSeconds(4)));
        Assert.Equal(TaskAlertState.Running, Assert.Single(restored.Assignments).State);
        Assert.Null(Assert.Single(restored.Assignments).PendingAsyncQuestions);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ConfirmedCompletionUsesTheSamePolicyForVisibleAndDeferredQuestions(bool clearOnEnd, bool deferred)
    {
        var pool = new TaskAlertPool { ClearQuestionsOnTurnEnd = clearOnEnd };
        pool.Apply(new(CodexLifecycleEvent.UserPromptSubmit, "chat", deferred ? "old" : "turn", At));
        pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "chat", "turn", At.AddSeconds(2), Key));
        Assert.True(pool.Reconcile(new("chat", "turn", TaskAlertState.Completed,
            At.AddSeconds(1), At.AddSeconds(3)), At.AddSeconds(4)));
        var assignment = Assert.Single(pool.Assignments);
        Assert.Equal(clearOnEnd ? TaskAlertState.Completed : TaskAlertState.Approval, assignment.State);
        Assert.Equal(!clearOnEnd, assignment.PendingAsyncQuestions is { Length: 1 });
        Assert.Empty(Assert.Single(pool.CaptureState().Assignments).DeferredQuestions!);
        if (!clearOnEnd)
        {
            Assert.NotNull(assignment.CompleteAfter);
            var restored = new TaskAlertPool();
            restored.Restore(pool.CaptureState());
            Assert.True(restored.DismissQuestions(assignment.Slot, "chat", [Key], At.AddSeconds(5)));
            Assert.Equal(TaskAlertState.Completed, Assert.Single(restored.Assignments).State);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FaultCleanupIsIndependentOfNormalTurnEndPolicy(bool desktop)
    {
        var pool = new TaskAlertPool();
        Assert.False(pool.ClearQuestionsOnTurnEnd);
        pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "chat", "turn", At, Key));
        if (desktop)
            pool.Reconcile(new("chat", "turn", TaskAlertState.Fault, At.AddSeconds(-1), At.AddSeconds(1)), At.AddSeconds(2));
        else pool.Apply(new(CodexLifecycleEvent.Fault, "chat", "turn", At.AddSeconds(1)));
        Assert.Equal(TaskAlertState.Fault, Assert.Single(pool.Assignments).State);
        Assert.Null(Assert.Single(pool.Assignments).PendingAsyncQuestions);
    }
}
