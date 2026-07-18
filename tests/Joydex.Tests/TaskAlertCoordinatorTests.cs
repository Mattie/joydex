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
