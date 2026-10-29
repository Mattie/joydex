using Joydex.Core.TaskAlerts;

namespace Joydex.Tests;

public sealed class TaskAlertQuestionCleanupTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UtcNow;
    private static readonly string First = new('A', 64);
    private static readonly string Second = new('B', 64);

    [Theory]
    [InlineData("new")]
    [InlineData("old")]
    [InlineData(null)]
    public void AnyNewUserPromptClearsPriorQuestionsForThatChatOnly(string? turnId)
    {
        var pool = new TaskAlertPool();
        pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "chat", "old", At, First));
        pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "other", "old", At, Second));
        Assert.True(pool.Apply(new(CodexLifecycleEvent.UserPromptSubmit, "chat", turnId, At.AddSeconds(1))));
        var assignment = pool.Assignments.Single(item => item.SessionId == "chat");
        Assert.Equal(TaskAlertState.Running, assignment.State);
        Assert.Null(assignment.PendingAsyncQuestions);
        Assert.Equal([Second], pool.Assignments.Single(item => item.SessionId == "other").PendingAsyncQuestions!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AQuestionAfterItsTurnStoppedCannotRelightEvenAfterRestart(bool graceElapsed)
    {
        var pool = new TaskAlertPool { ClearQuestionsOnTurnEnd = true };
        pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "chat", "turn", At, First));
        pool.Apply(new(CodexLifecycleEvent.Stop, "chat", "turn", At.AddSeconds(1)));
        if (graceElapsed) pool.Advance(At.AddSeconds(3));
        var restored = new TaskAlertPool { ClearQuestionsOnTurnEnd = true };
        restored.Restore(pool.CaptureState());
        Assert.False(restored.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "chat", "turn", At.AddSeconds(4), Second)));
        restored.Advance(At.AddSeconds(5));
        Assert.Equal(TaskAlertState.Completed, Assert.Single(restored.Assignments).State);
        Assert.Null(Assert.Single(restored.Assignments).PendingAsyncQuestions);
        restored.Apply(new(CodexLifecycleEvent.UserPromptSubmit, "chat", "next", At.AddSeconds(6)));
        Assert.True(restored.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "chat", "next", At.AddSeconds(7), Second)));
        Assert.Equal(TaskAlertState.Approval, Assert.Single(restored.Assignments).State);
    }

    [Theory]
    [InlineData("old")]
    [InlineData(null)]
    public void AStopWithoutTheCurrentTurnIdentityCannotClearANewQuestion(string? staleTurn)
    {
        var pool = new TaskAlertPool();
        pool.Apply(new(CodexLifecycleEvent.UserPromptSubmit, "chat", "new", At));
        pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "chat", "new", At.AddSeconds(1), First));
        Assert.False(pool.Apply(new(CodexLifecycleEvent.Stop, "chat", staleTurn, At.AddSeconds(2))));
        pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "chat", staleTurn, At.AddSeconds(3), Second));
        Assert.Equal("new", Assert.Single(pool.Assignments).TurnId);
        Assert.Equal([First], Assert.Single(pool.Assignments).PendingAsyncQuestions!);
    }

    [Fact]
    public void ADifferentTurnRetiresPriorTurnQuestionsRegardlessOfTheirReceiptTimes()
    {
        var pool = new TaskAlertPool();
        pool.Apply(new(CodexLifecycleEvent.UserPromptSubmit, "chat", "old", At));
        pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "chat", "old", At.AddSeconds(3), First));
        Assert.True(pool.Apply(new(CodexLifecycleEvent.UserPromptSubmit, "chat", "new", At.AddSeconds(2))));
        Assert.Equal(TaskAlertState.Running, Assert.Single(pool.Assignments).State);
        Assert.Null(Assert.Single(pool.Assignments).PendingAsyncQuestions);
        pool.Reconcile(new("chat", "new", TaskAlertState.Running, At.AddSeconds(2), null), At.AddSeconds(4));
        Assert.Equal(TaskAlertState.Running, Assert.Single(pool.Assignments).State);
    }

    [Fact]
    public void ANewTurnDoesNotInheritALateOldStopsEventWatermark()
    {
        foreach (var nativeConfirmation in new[] { false, true })
        {
            var pool = new TaskAlertPool { ClearQuestionsOnTurnEnd = true };
            pool.Apply(new(CodexLifecycleEvent.UserPromptSubmit, "chat", "old", At));
            pool.Apply(new(CodexLifecycleEvent.Stop, "chat", "old", At.AddSeconds(4)));
            if (nativeConfirmation)
                Assert.True(pool.Reconcile(new("chat", "new", TaskAlertState.Running, At.AddSeconds(2), null), At.AddSeconds(5)));
            else Assert.True(pool.Apply(new(CodexLifecycleEvent.UserPromptSubmit, "chat", "new", At.AddSeconds(2))));
            Assert.True(pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "chat", "new", At.AddSeconds(3), Second)));
            Assert.Equal(TaskAlertState.Approval, Assert.Single(pool.Assignments).State);
            Assert.True(pool.Apply(new(CodexLifecycleEvent.Stop, "chat", "new", At.AddSeconds(3.5))));
            Assert.Null(Assert.Single(pool.Assignments).PendingAsyncQuestions);
        }
    }

    [Fact]
    public void ALateOldStopCannotStrandANewerDeferredQuestionAndItsDelayedPromptAfterRestore()
    {
        var pool = new TaskAlertPool();
        pool.Apply(new(CodexLifecycleEvent.UserPromptSubmit, "chat", "old", At));
        pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "chat", "new", At.AddSeconds(3), Second));
        pool.Apply(new(CodexLifecycleEvent.Stop, "chat", "old", At.AddSeconds(4)));
        var restored = new TaskAlertPool();
        restored.Restore(pool.CaptureState());
        Assert.True(restored.Apply(new(CodexLifecycleEvent.UserPromptSubmit, "chat", "new", At.AddSeconds(2))));
        Assert.Equal("new", Assert.Single(restored.Assignments).TurnId);
        Assert.Equal([Second], Assert.Single(restored.Assignments).PendingAsyncQuestions!);
        Assert.Equal(TaskAlertState.Approval, Assert.Single(restored.Assignments).State);
        Assert.True(restored.Reconcile(new("chat", "new", TaskAlertState.Running, At.AddSeconds(2), null), At.AddSeconds(5)));
    }

    [Fact]
    public void ASameMillisecondPromptKeepsItsAlreadyConfirmedNewTurnQuestion()
    {
        var pool = new TaskAlertPool();
        pool.Apply(new(CodexLifecycleEvent.UserPromptSubmit, "chat", "old", At.AddMinutes(-1)));
        pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "chat", "new", At, First));
        pool.Reconcile(new("chat", "new", TaskAlertState.Running, At, null), At);
        pool.Apply(new(CodexLifecycleEvent.UserPromptSubmit, "chat", "new", At));
        Assert.Equal([First], Assert.Single(pool.Assignments).PendingAsyncQuestions!);
        Assert.Equal(TaskAlertState.Approval, Assert.Single(pool.Assignments).State);
    }

    [Fact]
    public void NewTurnQuestionsArrivingBeforeTheirPromptSurviveRestartAndPromoteTogether()
    {
        var pool = new TaskAlertPool();
        pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "chat", "old", At, First));
        Assert.True(pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "chat", "new", At.AddSeconds(3), Second)));
        var third = new string('C', 64);
        Assert.True(pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "chat", "new", At.AddSeconds(3), third)));
        Assert.Equal("old", Assert.Single(pool.Assignments).TurnId);
        Assert.Equal([First], Assert.Single(pool.Assignments).PendingAsyncQuestions!);
        var restored = new TaskAlertPool();
        restored.Restore(pool.CaptureState());
        Assert.True(restored.Apply(new(CodexLifecycleEvent.UserPromptSubmit, "chat", "new", At.AddSeconds(2))));
        Assert.Equal("new", Assert.Single(restored.Assignments).TurnId);
        Assert.Equal([Second, third], Assert.Single(restored.Assignments).PendingAsyncQuestions!);
        Assert.Equal(At.AddSeconds(3), Assert.Single(restored.Assignments).UpdatedAt);
        Assert.False(restored.Apply(new(CodexLifecycleEvent.Stop, "chat", "old", At.AddSeconds(4))));
    }

    [Theory]
    [InlineData(TaskAlertState.Running)]
    [InlineData(TaskAlertState.Completed)]
    [InlineData(TaskAlertState.Fault)]
    public void DesktopConfirmsWhetherAnUnprovenQuestionShouldLight(TaskAlertState observed)
    {
        var pool = new TaskAlertPool { ClearQuestionsOnTurnEnd = true };
        pool.Apply(new(CodexLifecycleEvent.UserPromptSubmit, "chat", "old", At.AddMinutes(-2)));
        pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "chat", "new", At.AddMinutes(-1), Second));
        Assert.Equal(TaskAlertState.Running, Assert.Single(pool.Assignments).State);
        Assert.True(pool.Reconcile(new("chat", "new", observed, At.AddMinutes(-1.5),
            observed == TaskAlertState.Running ? null : At.AddSeconds(-10)), At));
        Assert.Equal(observed == TaskAlertState.Running ? TaskAlertState.Approval : observed,
            Assert.Single(pool.Assignments).State);
        Assert.Empty(Assert.Single(pool.CaptureState().Assignments).DeferredQuestions!);
    }

    [Fact]
    public void AForeignOldQuestionNeverLightsWithoutConfirmationAndDeferredStorageIsBounded()
    {
        var pool = new TaskAlertPool();
        pool.Apply(new(CodexLifecycleEvent.UserPromptSubmit, "chat", "current", At));
        for (var index = 0; index < 33; index++)
            pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "chat", "unknown", At.AddSeconds(1), index.ToString("X64")));
        Assert.Equal(TaskAlertState.Running, Assert.Single(pool.Assignments).State);
        Assert.Equal(32, Assert.Single(pool.CaptureState().Assignments).DeferredQuestions!.Length);
        Assert.Equal(1, pool.DroppedEventCount);
        pool.Apply(new(CodexLifecycleEvent.UserPromptSubmit, "chat", "current", At.AddSeconds(2)));
        Assert.Empty(Assert.Single(pool.CaptureState().Assignments).DeferredQuestions!);
    }

    [Fact]
    public void ADelayedPromptRetiresOldQuestionsAndPreservesANewerQuestionAndPermissionAfterRestore()
    {
        var pool = new TaskAlertPool();
        // A snapshot from an earlier package can contain questions from multiple turns.
        pool.Restore(new([new(1, "chat", "new", TaskAlertState.Approval, At.AddSeconds(3), null,
            [new(Second, 1)], 0, PendingAsyncQuestions: [First, Second], QuestionOrigins:
            [new(First, "old", At), new(Second, "new", At.AddSeconds(3))]) ]));
        Assert.True(pool.Apply(new(CodexLifecycleEvent.UserPromptSubmit, "chat", "new", At.AddSeconds(2))));
        Assert.Equal([Second], Assert.Single(pool.Assignments).PendingAsyncQuestions!);
        Assert.Equal("new", Assert.Single(pool.Assignments).TurnId);
        Assert.Equal(At.AddSeconds(3), Assert.Single(pool.Assignments).UpdatedAt);
        pool.Apply(new(CodexLifecycleEvent.AsyncUserInputAnswered, "chat", "new", At.AddSeconds(4), Second));
        Assert.Equal(TaskAlertState.Approval, Assert.Single(pool.Assignments).State); // Blocking permission remains.
    }

    [Fact]
    public void LegacyQuestionsWithoutOriginsClearOnTheNextUserPrompt()
    {
        var pool = new TaskAlertPool();
        pool.Restore(new([new(1, "chat", null, TaskAlertState.Approval, At, null, [], 0,
            PendingAsyncQuestions: [First])]));
        pool.Apply(new(CodexLifecycleEvent.UserPromptSubmit, "chat", null, At.AddSeconds(1)));
        Assert.Equal(TaskAlertState.Running, Assert.Single(pool.Assignments).State);
        Assert.Empty(Assert.Single(pool.CaptureState().Assignments).PendingAsyncQuestions!);
    }
}
