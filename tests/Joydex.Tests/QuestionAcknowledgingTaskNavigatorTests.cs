using Joydex.Core.TaskAlerts;
using Joydex.Windows.TaskAlerts;

namespace Joydex.Tests;

public sealed class QuestionAcknowledgingTaskNavigatorTests
{
    private static readonly string First = new('A', 64);
    private static readonly string Second = new('B', 64);
    private static readonly DateTimeOffset At = DateTimeOffset.UtcNow.AddMinutes(-1);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task SuccessfulVisitAcknowledgesQuestionsButLeavesPermissionsAndOtherChats(int requestSlot)
    {
        var pool = new TaskAlertPool();
        var session = Guid.NewGuid().ToString("D");
        pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, session, "turn", At, First));
        pool.Apply(new(CodexLifecycleEvent.PermissionRequest, session, "turn", At.AddSeconds(1), Second));
        pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "other", "turn", At, First));
        var navigator = Navigator(pool, new DelegateNavigator((_, _) => Task.FromResult(true)));
        Assert.True(await navigator.NavigateAsync(new(requestSlot, 2, 1, session.ToUpperInvariant()), default));
        var current = pool.Assignments.Single(item => item.SessionId == session);
        Assert.Null(current.PendingAsyncQuestions);
        Assert.Equal(TaskAlertState.Approval, current.State); // The real permission still waits.
        Assert.Equal([First], pool.Assignments.Single(item => item.SessionId == "other").PendingAsyncQuestions!);
    }

    [Fact]
    public async Task VisitingAcknowledgesAQuestionKeptAfterNormalTurnCompletion()
    {
        foreach (var desktopCompletion in new[] { false, true })
        {
            var pool = new TaskAlertPool();
            pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "chat", "turn", At, First));
            if (desktopCompletion)
                pool.Reconcile(new("chat", "turn", TaskAlertState.Completed, At.AddSeconds(-1), At.AddSeconds(1)), At.AddSeconds(3));
            else
            {
                pool.Apply(new(CodexLifecycleEvent.Stop, "chat", "turn", At.AddSeconds(1)));
                pool.Advance(At.AddSeconds(3));
            }
            Assert.Equal(TaskAlertState.Approval, Assert.Single(pool.Assignments).State);
            var navigator = Navigator(pool, new DelegateNavigator((_, _) => Task.FromResult(true)));
            Assert.True(await navigator.NavigateAsync(new(1, 2, 1, "chat"), default));
            Assert.Equal(TaskAlertState.Completed, Assert.Single(pool.Assignments).State);
            Assert.Null(Assert.Single(pool.Assignments).PendingAsyncQuestions);
        }
    }

    [Fact]
    public async Task AChatPromotedToAnotherSlotDuringNavigationStillAcknowledgesItsQuestions()
    {
        var pool = new TaskAlertPool();
        for (var index = 0; index < 4; index++)
            pool.Apply(new(CodexLifecycleEvent.UserPromptSubmit, $"placeholder-{index}", "turn", At));
        pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "chat", "turn", At, First));
        Assert.Equal(5, pool.Assignments.Single(item => item.SessionId == "chat").Slot);
        var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var navigator = Navigator(pool, new DelegateNavigator((_, _) => opened.Task));
        var visit = navigator.NavigateAsync(new(5, 1, 1, "chat"), default);
        pool.RemoveAssignments(item => item.SessionId == "placeholder-0", At);
        pool.Advance(At + TaskAlertPool.BackfillDelay + TimeSpan.FromSeconds(1));
        Assert.Equal(1, pool.Assignments.Single(item => item.SessionId == "chat").Slot);
        opened.SetResult(true);
        Assert.True(await visit);
        Assert.Null(pool.Assignments.Single(item => item.SessionId == "chat").PendingAsyncQuestions);
    }

    [Fact]
    public async Task AQuestionArrivingWhileTheThreadOpensStaysPending()
    {
        var pool = new TaskAlertPool();
        pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "chat", "turn", At, First));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var navigator = Navigator(pool, new DelegateNavigator((_, _) =>
        {
            started.SetResult();
            return opened.Task;
        }));
        var visit = navigator.NavigateAsync(new(1, 2, 1, "chat"), default);
        await started.Task;
        pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "chat", "turn", At.AddSeconds(1), Second));
        opened.SetResult(true);
        Assert.True(await visit);
        Assert.Equal([Second], Assert.Single(pool.Assignments).PendingAsyncQuestions!);
    }

    [Fact]
    public async Task FailedNavigationDoesNotAcknowledgeQuestions()
    {
        var pool = new TaskAlertPool();
        pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "chat", "turn", At, First));
        var navigator = Navigator(pool, new DelegateNavigator((_, _) => Task.FromResult(false)));
        Assert.False(await navigator.NavigateAsync(new(1, 2, 1, "chat"), default));
        Assert.Equal([First], Assert.Single(pool.Assignments).PendingAsyncQuestions!);
    }

    [Fact]
    public async Task ASlotReusedDuringNavigationCannotAcknowledgeADifferentChat()
    {
        var pool = new TaskAlertPool();
        pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "chat", "turn", At, First));
        var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var navigator = Navigator(pool, new DelegateNavigator((_, _) => opened.Task));
        var visit = navigator.NavigateAsync(new(1, 2, 1, "chat"), default);
        pool.SetEnabled(false);
        pool.SetEnabled(true);
        pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "replacement", "turn", At, First));
        opened.SetResult(true);
        Assert.True(await visit);
        Assert.Equal([First], Assert.Single(pool.Assignments).PendingAsyncQuestions!);
        Assert.Equal("replacement", Assert.Single(pool.Assignments).SessionId);
    }

    [Fact]
    public async Task AcknowledgementFailureDoesNotRetryAnAlreadyOpenedThread()
    {
        var pool = new TaskAlertPool();
        pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "chat", "turn", At, First));
        var navigations = 0;
        var logs = new List<string>();
        var navigator = new QuestionAcknowledgingTaskNavigator(new DelegateNavigator((_, _) =>
        {
            navigations++;
            return Task.FromResult(true);
        }), () => new(true, pool.Assignments, 0), (_, _, _) => throw new IOException("fixture failure"), logs.Add);
        Assert.True(await navigator.NavigateAsync(new(1, 2, 1, "chat"), default));
        Assert.Equal(1, navigations);
        Assert.Single(logs);
        Assert.Equal([First], Assert.Single(pool.Assignments).PendingAsyncQuestions!);
    }

    [Fact]
    public async Task CancelledNavigationDoesNotAcknowledgeQuestions()
    {
        var pool = new TaskAlertPool();
        pool.Apply(new(CodexLifecycleEvent.AsyncUserInputRequest, "chat", "turn", At, First));
        var navigator = Navigator(pool, new DelegateNavigator((_, _) => Task.FromCanceled<bool>(new(true))));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => navigator.NavigateAsync(new(1, 2, 1, "chat"), default));
        Assert.Equal([First], Assert.Single(pool.Assignments).PendingAsyncQuestions!);
    }

    private static QuestionAcknowledgingTaskNavigator Navigator(TaskAlertPool pool, ITaskAlertNavigator inner) =>
        new(inner, () => new(true, pool.Assignments, 0),
            (slot, sessionId, keys) => pool.DismissQuestions(slot, sessionId, keys, DateTimeOffset.UtcNow), _ => { });

    private sealed class DelegateNavigator(Func<TaskAlertNavigationRequest, CancellationToken, Task<bool>> navigate) : ITaskAlertNavigator
    {
        public Task<bool> NavigateAsync(TaskAlertNavigationRequest request, CancellationToken token) => navigate(request, token);
    }
}
