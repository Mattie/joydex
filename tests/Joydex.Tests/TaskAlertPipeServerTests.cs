using System.IO.Pipes;
using System.Text;
using Joydex.Core.TaskAlerts;
using Joydex.Windows.TaskAlerts;

namespace Joydex.Tests;

public sealed class TaskAlertPipeServerTests
{
    [Fact]
    public async Task AcceptsConcurrentMessagesAndReducesWithoutWaitingForConsumers()
    {
        var directory = Path.Combine(Path.GetTempPath(), "joydex-pipe-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var coordinator = new TaskAlertCoordinator(Path.Combine(directory, "task-alerts.json"));
            var pipeName = $"Joydex.Tests.{Guid.NewGuid():N}";
            await using var server = new TaskAlertPipeServer(coordinator, _ => { }, pipeName);
            server.Start();

            await Task.WhenAll(Enumerable.Range(1, 12).Select(index => SendAsync(pipeName, index)));
            await WaitUntilAsync(
                () => coordinator.GetSnapshot() is { Assignments.Count: 10, DroppedEventCount: 2 },
                TimeSpan.FromSeconds(3));

            var snapshot = coordinator.GetSnapshot();
            Assert.Equal(10, snapshot.Assignments.Count);
            Assert.Equal(2, snapshot.DroppedEventCount);
            Assert.Equal(Enumerable.Range(1, 10), snapshot.Assignments.Select(assignment => assignment.Slot));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CarriesAttentionKeyFromPermissionToToolCompletion()
    {
        var directory = Path.Combine(Path.GetTempPath(), "joydex-pipe-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var coordinator = new TaskAlertCoordinator(Path.Combine(directory, "task-alerts.json"));
            var pipeName = $"Joydex.Tests.{Guid.NewGuid():N}";
            await using var server = new TaskAlertPipeServer(coordinator, _ => { }, pipeName);
            server.Start();

            var now = DateTimeOffset.UtcNow;
            await SendPayloadAsync(pipeName, $$"""
                {"event":"PermissionRequest","sessionId":"session","turnId":"turn","attentionKey":"key","receivedAtUnixMs":{{now.ToUnixTimeMilliseconds()}}}
                """);
            await WaitUntilAsync(
                () => coordinator.GetSnapshot().Assignments.SingleOrDefault()?.State == TaskAlertState.Approval,
                TimeSpan.FromSeconds(3));

            await SendPayloadAsync(pipeName, $$"""
                {"event":"ToolCompleted","sessionId":"session","turnId":"turn","attentionKey":"key","receivedAtUnixMs":{{now.AddMilliseconds(1).ToUnixTimeMilliseconds()}}}
                """);
            await WaitUntilAsync(
                () => coordinator.GetSnapshot().Assignments.SingleOrDefault()?.State == TaskAlertState.Running,
                TimeSpan.FromSeconds(3));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task SendAsync(string pipeName, int index)
    {
        var payload = $$"""
            {"event":"UserPromptSubmit","sessionId":"session-{{index}}","turnId":"turn-{{index}}","receivedAtUnixMs":{{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}
            """;
        await SendPayloadAsync(pipeName, payload);
    }

    private static async Task SendPayloadAsync(string pipeName, string payload)
    {
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        var bytes = Encoding.UTF8.GetBytes(payload);
        await client.WriteAsync(bytes);
    }

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
