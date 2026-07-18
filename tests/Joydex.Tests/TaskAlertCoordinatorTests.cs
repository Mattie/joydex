using Joydex.Core.TaskAlerts;
using Joydex.Windows.TaskAlerts;

namespace Joydex.Tests;

public sealed class TaskAlertCoordinatorTests
{
    [Fact]
    public async Task PublishesChangedSnapshotWhenOverflowEventIsDropped()
    {
        var directory = Path.Combine(Path.GetTempPath(), "joydex-coordinator-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var coordinator = new TaskAlertCoordinator(Path.Combine(directory, "task-alerts.json"));
            foreach (var index in Enumerable.Range(1, 10))
            {
                Assert.True(coordinator.TryPublish(Event(index)));
            }

            await WaitUntilAsync(
                () => coordinator.GetSnapshot().Assignments.Count == 10,
                TimeSpan.FromSeconds(3));

            long observedDroppedEventCount = 0;
            coordinator.Changed += (_, snapshot) =>
                Interlocked.Exchange(ref observedDroppedEventCount, snapshot.DroppedEventCount);

            Assert.True(coordinator.TryPublish(Event(11)));
            await WaitUntilAsync(
                () => Interlocked.Read(ref observedDroppedEventCount) == 1,
                TimeSpan.FromSeconds(3));

            var dropped = Assert.Single(coordinator.GetSnapshot().RecentEvents!, trace =>
                trace.SessionId == "session-11");
            Assert.Equal(TaskAlertEventResult.Dropped, dropped.Result);
            Assert.Null(dropped.Slot);
            Assert.Null(dropped.State);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RecordsReceivedEventAndReducerOutcome()
    {
        var directory = Path.Combine(Path.GetTempPath(), "joydex-coordinator-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var coordinator = new TaskAlertCoordinator(Path.Combine(directory, "task-alerts.json"));
            Assert.True(coordinator.TryPublish(Event(1)));
            await WaitUntilAsync(
                () => coordinator.GetSnapshot().RecentEvents?.Count == 1,
                TimeSpan.FromSeconds(3));

            var trace = Assert.Single(coordinator.GetSnapshot().RecentEvents!);
            Assert.Equal(CodexLifecycleEvent.UserPromptSubmit, trace.Event);
            Assert.Equal("session-1", trace.SessionId);
            Assert.Equal("turn-1", trace.TurnId);
            Assert.Equal(1, trace.Slot);
            Assert.Equal(TaskAlertState.Running, trace.State);
            Assert.Equal(TaskAlertEventResult.Assigned, trace.Result);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RetainsOnlyTheMostRecentHundredEvents()
    {
        var directory = Path.Combine(Path.GetTempPath(), "joydex-coordinator-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var coordinator = new TaskAlertCoordinator(Path.Combine(directory, "task-alerts.json"));
            foreach (var index in Enumerable.Range(1, 105))
            {
                Assert.True(coordinator.TryPublish(Event(index)));
            }

            await WaitUntilAsync(
                () => coordinator.GetSnapshot().DroppedEventCount == 95,
                TimeSpan.FromSeconds(3));

            var traces = coordinator.GetSnapshot().RecentEvents!;
            Assert.Equal(100, traces.Count);
            Assert.Equal("session-6", traces[0].SessionId);
            Assert.Equal("session-105", traces[^1].SessionId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static TaskAlertEvent Event(int index) => new(
        CodexLifecycleEvent.UserPromptSubmit,
        $"session-{index}",
        $"turn-{index}",
        DateTimeOffset.UtcNow);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.True(condition());
    }
}
