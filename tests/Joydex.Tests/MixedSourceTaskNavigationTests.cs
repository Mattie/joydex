using System.Diagnostics;
using Joydex.Core.Config;
using Joydex.Core.TaskAlerts;
using Joydex.Windows.Actions;
using Joydex.Windows.TaskAlerts;

namespace Joydex.Tests;

public sealed class MixedSourceTaskNavigationTests
{
    [Fact]
    public async Task RemoteNavigationUsesExactEnvironmentAndNativeCaseThroughItsOwnUriScheme()
    {
        var identity = TaskAlertIdentity.RemoteT3("environment/a", "Thread/Case? #");
        ProcessStartInfo? started = null;
        var navigator = new TaskDeepLinkNavigator(new() { DryRun = false }, _ => { }, new Guard(), info => { started = info; return null; });
        Assert.True(await navigator.NavigateAsync(new(2, 2, 3, identity.PoolKey, identity), default));
        Assert.Equal("t3code://threads/environment%2Fa/Thread%2FCase%3F%20%23", started!.FileName);
        Assert.True(started.UseShellExecute);
    }

    [Fact]
    public async Task MismatchedTypedTargetCannotNavigateOrAcknowledgeAnotherTask()
    {
        var first = TaskAlertIdentity.RemoteT3("environment", "thread");
        var second = TaskAlertIdentity.RemoteT3("environment", "Thread");
        var request = new TaskAlertNavigationRequest(1, 2, 1, first.PoolKey, second);
        var dispatched = false;
        var deepLink = new TaskDeepLinkNavigator(new() { DryRun = false }, _ => { }, new Guard(), _ => { dispatched = true; return null; });
        Assert.False(await deepLink.NavigateAsync(request, default));
        Assert.False(dispatched);
        var inner = new Navigator();
        var acknowledged = false;
        var wrapper = new QuestionAcknowledgingTaskNavigator(inner,
            () => new TaskAlertSnapshot(true, [], 0), (_, _, _) => acknowledged = true, _ => { });
        Assert.False(await wrapper.NavigateAsync(request, default));
        Assert.Equal(0, inner.Calls);
        Assert.False(acknowledged);
    }

    [Fact]
    public async Task RemoteNavigationAcknowledgesOnlyTheSelectedOrdinalTaskAndDisplayedQuestions()
    {
        var now = DateTimeOffset.UtcNow.AddSeconds(-5);
        var source = new TaskAlertSourceIdentity(TaskAlertBackend.RemoteT3, "environment");
        var first = TaskAlertIdentity.RemoteT3(source.EnvironmentId, "thread");
        var second = TaskAlertIdentity.RemoteT3(source.EnvironmentId, "Thread");
        var old = new string('A', 64);
        var fresh = new string('B', 64);
        var pool = new TaskAlertPool();
        pool.ApplySourceSnapshot(new(source, 1, 1, now.AddSeconds(1), true,
            [new(first.NativeThreadId, "run", TaskAlertState.Running, now, AsyncQuestions: [new(old, "run", now)]),
                new(second.NativeThreadId, "run", TaskAlertState.Running, now, AsyncQuestions: [new(old, "run", now)])]));
        var inner = new Navigator { OnNavigate = () => pool.ApplySourceSnapshot(new(source, 1, 2, now.AddSeconds(2), true,
            [new(first.NativeThreadId, "run", TaskAlertState.Running, now,
                AsyncQuestions: [new(old, "run", now), new(fresh, "run", now.AddSeconds(2))])])) };
        var wrapper = new QuestionAcknowledgingTaskNavigator(inner,
            () => new TaskAlertSnapshot(true, pool.Assignments, 0),
            (slot, key, questions) => pool.DismissQuestions(slot, key, questions, DateTimeOffset.UtcNow), _ => { });
        Assert.True(await wrapper.NavigateAsync(new(1, 2, 1, first.PoolKey, first), default));
        Assert.Equal([fresh], pool.Assignments.Single(task => task.TaskIdentity == first).PendingAsyncQuestions!);
        Assert.Equal([old], pool.Assignments.Single(task => task.TaskIdentity == second).PendingAsyncQuestions!);
    }

    private sealed class Guard : IForegroundProcessGuard
    {
        public ForegroundCheck Check(SafetyOptions safety, bool actionMayBringCodexForward) => new(true, "test", "allowed");
    }
    private sealed class Navigator : ITaskAlertNavigator
    {
        public int Calls { get; private set; }
        public Action? OnNavigate { get; init; }
        public Task<bool> NavigateAsync(TaskAlertNavigationRequest request, CancellationToken token)
        { Calls++; OnNavigate?.Invoke(); return Task.FromResult(true); }
    }
}
