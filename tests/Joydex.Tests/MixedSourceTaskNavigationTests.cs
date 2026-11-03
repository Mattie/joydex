using System.Diagnostics;
using Joydex.Core.Config;
using Joydex.Core.TaskAlerts;
using Joydex.Windows.Actions;
using Joydex.Windows.TaskAlerts;

namespace Joydex.Tests;

public sealed class MixedSourceTaskNavigationTests
{
    [Theory]
    [InlineData("environment/a", "Thread/Case? #", "t3code://threads/environment%2Fa/Thread%2FCase%3F%20%23")]
    [InlineData("Environment/a", "thread/Case? #", "t3code://threads/Environment%2Fa/thread%2FCase%3F%20%23")]
    public async Task RemoteNavigationPassesTheExactEscapedTargetToTheVerifiedExecutable(string environment, string thread, string expectedUri)
    {
        const string executable = @"C:\Apps\T3 Code (Beta)\T3 Code (Beta).exe";
        var identity = TaskAlertIdentity.RemoteT3(environment, thread);
        ProcessStartInfo? started = null;
        var navigator = new TaskDeepLinkNavigator(new() { DryRun = false }, _ => { }, new Guard(),
            info => { started = info; return null; }, () => executable);
        Assert.True(await navigator.NavigateAsync(new(2, 2, 3, identity.PoolKey, identity), default));
        Assert.Equal(executable, started!.FileName);
        Assert.Equal(expectedUri, Assert.Single(started.ArgumentList));
        Assert.Empty(started.Arguments);
        Assert.True(started.UseShellExecute);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnverifiedExecutableOrLaunchFailureDoesNotNavigateOrAcknowledgeQuestions(bool launchFails)
    {
        var identity = TaskAlertIdentity.RemoteT3("environment", "thread");
        var question = new string('A', 64);
        var assignment = new TaskAlertAssignment(1, identity.PoolKey, "run", TaskAlertState.Approval,
            DateTimeOffset.UtcNow, PendingAsyncQuestions: [question], Identity: identity);
        var launches = 0;
        var acknowledged = false;
        var log = new List<string>();
        var deepLink = new TaskDeepLinkNavigator(new() { DryRun = false }, log.Add, new Guard(), _ =>
        {
            launches++;
            throw new InvalidOperationException("Synthetic launcher failure.");
        }, () => launchFails ? @"C:\Apps\T3 Code (Beta).exe" : null);
        var wrapper = new QuestionAcknowledgingTaskNavigator(deepLink,
            () => new TaskAlertSnapshot(true, [assignment], 0), (_, _, _) => acknowledged = true, log.Add);

        Assert.False(await wrapper.NavigateAsync(new(1, 2, 1, identity.PoolKey, identity), default));
        Assert.False(acknowledged);
        Assert.Equal(launchFails ? 1 : 0, launches);
        Assert.Contains(log, entry => entry.Contains(launchFails ? "Synthetic launcher failure" : "executable could not be verified", StringComparison.Ordinal));
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
