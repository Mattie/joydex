using System.IO.Pipes;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Joydex.Core.TaskAlerts;
using Joydex.Windows.TaskAlerts;

namespace Joydex.Tests;

public sealed class TaskAlertPipeServerTests
{
    private static readonly TimeSpan AsyncTimeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeliversAlertsAfterAnInterruptedAcceptAndClosesTheFailedPipe(bool loggingFails)
    {
        var directory = Path.Combine(Path.GetTempPath(), "joydex-pipe-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var coordinator = new TaskAlertCoordinator(Path.Combine(directory, "preferences.json"));
            var pipeName = $"Joydex.Tests.{Guid.NewGuid():N}";
            var attempts = 0;
            Microsoft.Win32.SafeHandles.SafePipeHandle? failedHandle = null;
            var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
            await using var server = new TaskAlertPipeServer(coordinator, message =>
            {
                logs.Enqueue(message);
                if (loggingFails) throw new IOException("Diagnostic sink fixture.");
            }, pipeName)
            {
                WaitForConnectionAsync = (pipe, cancellationToken) =>
                {
                    if (Interlocked.Increment(ref attempts) == 1)
                    {
                        failedHandle = pipe.SafePipeHandle;
                        throw new IOException("Interrupted accept fixture.");
                    }
                    return pipe.WaitForConnectionAsync(cancellationToken);
                },
            };
            server.Start();

            await SendAsync(pipeName, 1);
            await WaitUntilAsync(() => coordinator.GetSnapshot().Assignments.Count == 1, AsyncTimeout);

            Assert.Equal("session-1", Assert.Single(coordinator.GetSnapshot().Assignments).SessionId);
            Assert.True(failedHandle!.IsClosed);
            Assert.False(server.Completion.IsCompleted);
            Assert.Contains(logs, line => line.Contains("retrying", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(logs, line => line.Contains("resumed", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ShutdownCompletesDuringAcceptRecovery()
    {
        var directory = Path.Combine(Path.GetTempPath(), "joydex-pipe-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var coordinator = new TaskAlertCoordinator(Path.Combine(directory, "preferences.json"));
            var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var attempts = 0;
            var server = new TaskAlertPipeServer(coordinator, _ => failed.TrySetResult(), $"Joydex.Tests.{Guid.NewGuid():N}")
            {
                WaitForConnectionAsync = (_, _) =>
                {
                    Interlocked.Increment(ref attempts);
                    throw new IOException("Interrupted accept fixture.");
                },
            };
            server.Start();
            try { await failed.Task.WaitAsync(AsyncTimeout); }
            finally { await server.DisposeAsync().AsTask().WaitAsync(AsyncTimeout); }

            Assert.True(attempts >= 1);
            Assert.True(server.Completion.IsCompletedSuccessfully);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedAcceptFailureFaultsTheStableCompletionTask(bool loggingFails)
    {
        var directory = Path.Combine(Path.GetTempPath(), "joydex-pipe-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var coordinator = new TaskAlertCoordinator(Path.Combine(directory, "preferences.json"));
            var failure = new InvalidOperationException("Unexpected accept fixture.");
            await using var server = new TaskAlertPipeServer(coordinator, _ =>
            {
                if (loggingFails) throw new IOException("Diagnostic sink fixture.");
            }, $"Joydex.Tests.{Guid.NewGuid():N}")
            {
                WaitForConnectionAsync = (_, _) => throw failure,
            };
            var completion = server.Completion;
            server.Start();

            Assert.Same(completion, server.Completion);
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(
                () => completion.WaitAsync(AsyncTimeout)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task FatalAcceptFailureStillAllowsShutdownToCloseAnUnfinishedClient()
    {
        var directory = Path.Combine(Path.GetTempPath(), "joydex-pipe-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var coordinator = new TaskAlertCoordinator(Path.Combine(directory, "preferences.json"));
            var pipeName = $"Joydex.Tests.{Guid.NewGuid():N}";
            var handles = new List<Microsoft.Win32.SafeHandles.SafePipeHandle>();
            var server = new TaskAlertPipeServer(coordinator, _ => { }, pipeName)
            {
                WaitForConnectionAsync = (pipe, cancellationToken) =>
                {
                    handles.Add(pipe.SafePipeHandle);
                    return handles.Count == 1
                        ? pipe.WaitForConnectionAsync(cancellationToken)
                        : Task.FromException(new InvalidOperationException("Unexpected accept fixture."));
                },
            };
            server.Start();
            await using var unfinishedClient = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            try
            {
                using var timeout = new CancellationTokenSource(AsyncTimeout);
                await unfinishedClient.ConnectAsync(timeout.Token);
                await Assert.ThrowsAsync<InvalidOperationException>(() => server.Completion.WaitAsync(AsyncTimeout));
            }
            finally { await server.DisposeAsync().AsTask().WaitAsync(AsyncTimeout); }

            Assert.Equal(2, handles.Count);
            Assert.All(handles, handle => Assert.True(handle.IsClosed));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task CompiledRelayDeliversWholeQuestionAndAnswerBatches()
    {
        var directory = Path.Combine(Path.GetTempPath(), "joydex-relay-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var statePath = Path.Combine(directory, "state.json");
        try
        {
            await using var coordinator = new TaskAlertCoordinator(Path.Combine(directory, "preferences.json"),
                statePath: statePath);
            var pipeName = $"Joydex.Tests.{Guid.NewGuid():N}";
            await using var server = new TaskAlertPipeServer(coordinator, _ => { }, pipeName);
            server.Start();
            var sent = new HookInput("PostToolUse", "session", "turn", null, @"C:\fixture\rollout.jsonl",
                "request_user_input_async", JsonSerializer.SerializeToElement(new { questions = new[]
                {
                    new { title = "First fixture question" }, new { title = "Second fixture question" },
                } }), ToolUseId: "call-fixture", ToolResponse: JsonSerializer.SerializeToElement("{\"accepted\":true}"));
            await RunRelayAsync(pipeName, sent);
            await WaitUntilAsync(() => TaskAlertStateStore.Load(statePath).Assignments.SingleOrDefault()
                ?.PendingAsyncQuestions?.Length == 2, AsyncTimeout);
            var replies = Enumerable.Range(0, 2).Select(index => new
            {
                questionItemId = JsonSerializer.Serialize(new object[] { "request_user_input_async", "call-fixture", index }),
                question = "Fixture question", answer = "Fixture answer",
            });
            await RunRelayAsync(pipeName, sent with
            {
                HookEventName = "UserPromptSubmit", ToolName = null, TurnId = "answer-turn",
                Prompt = "<send_user_message_question_reply>" + JsonSerializer.Serialize(replies)
                    + "</send_user_message_question_reply>",
            });
            await WaitUntilAsync(() => coordinator.GetSnapshot().Assignments.SingleOrDefault()?.State == TaskAlertState.Running,
                AsyncTimeout);
            Assert.DoesNotContain("Fixture question", File.ReadAllText(statePath));
            Assert.DoesNotContain("Fixture answer", File.ReadAllText(statePath));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task RunRelayAsync(string pipeName, HookInput input)
    {
        // The test output carries the relay assembly without its self-contained native runtime.
        var runtimeRoot = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        var start = new ProcessStartInfo(Path.Combine(runtimeRoot, "dotnet.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--runtimeconfig");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Joydex.Tests.runtimeconfig.json"));
        start.ArgumentList.Add("--depsfile");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Joydex.Tests.deps.json"));
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Joydex.HookRelay.dll"));
        start.ArgumentList.Add("--pipe");
        start.ArgumentList.Add(pipeName);
        using var process = Process.Start(start)!;
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(input));
        process.StandardInput.Close();
        await process.WaitForExitAsync().WaitAsync(AsyncTimeout);
        Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
    }

    [Fact]
    public async Task AsyncQuestionClearsAtStopThroughTheProductionPipe()
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
            var key = AttentionCorrelation.CreateAsyncQuestion("session", "call-fixture", 0);
            await SendPayloadAsync(pipeName, JsonSerializer.Serialize(new
            {
                @event = "AsyncUserInputRequest", sessionId = "session", turnId = "turn",
                attentionKey = key, receivedAtUnixMs = now.ToUnixTimeMilliseconds(),
            }));
            await WaitUntilAsync(() => coordinator.GetSnapshot().Assignments.SingleOrDefault()?.State == TaskAlertState.Approval,
                AsyncTimeout);
            await SendPayloadAsync(pipeName, JsonSerializer.Serialize(new
            {
                @event = "Stop", sessionId = "session", turnId = "turn",
                receivedAtUnixMs = now.AddMilliseconds(1).ToUnixTimeMilliseconds(),
            }));
            await WaitUntilAsync(() => coordinator.GetSnapshot().Assignments.SingleOrDefault()?.State == TaskAlertState.Completed,
                AsyncTimeout);
            Assert.Equal(TaskAlertState.Completed, Assert.Single(coordinator.GetSnapshot().Assignments).State);
            Assert.Null(Assert.Single(coordinator.GetSnapshot().Assignments).PendingAsyncQuestions);
            await SendPayloadAsync(pipeName, JsonSerializer.Serialize(new
            {
                @event = "AsyncUserInputAnswered", sessionId = "session", turnId = "answer-turn",
                attentionKey = key, receivedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }));
            await WaitUntilAsync(() => coordinator.GetSnapshot().Assignments.SingleOrDefault()?.State == TaskAlertState.Completed,
                AsyncTimeout);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

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
                AsyncTimeout);

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
            var attentionKey = new string('C', 64);
            await SendPayloadAsync(pipeName, $$"""
                {"event":"PermissionRequest","sessionId":"session","turnId":"turn","attentionKey":"{{attentionKey}}","receivedAtUnixMs":{{now.ToUnixTimeMilliseconds()}}}
                """);
            await WaitUntilAsync(
                () => coordinator.GetSnapshot().Assignments.SingleOrDefault()?.State == TaskAlertState.Approval,
                AsyncTimeout);

            await SendPayloadAsync(pipeName, $$"""
                {"event":"ToolCompleted","sessionId":"session","turnId":"turn","attentionKey":"{{attentionKey}}","receivedAtUnixMs":{{now.AddMilliseconds(1).ToUnixTimeMilliseconds()}}}
                """);
            await WaitUntilAsync(
                () => coordinator.GetSnapshot().Assignments.SingleOrDefault()?.State == TaskAlertState.Running,
                AsyncTimeout);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CarriesWorkspaceMetadataIntoAssignmentsAndEventTrace()
    {
        var directory = Path.Combine(Path.GetTempPath(), "joydex-pipe-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var coordinator = new TaskAlertCoordinator(Path.Combine(directory, "task-alerts.json"));
            var pipeName = $"Joydex.Tests.{Guid.NewGuid():N}";
            await using var server = new TaskAlertPipeServer(coordinator, _ => { }, pipeName);
            server.Start();
            var workspace = Path.Combine(directory, "realtime-voice-chat");
            var payload = JsonSerializer.Serialize(new
            {
                @event = "UserPromptSubmit",
                sessionId = "voice-session",
                turnId = "voice-turn",
                workspace,
                receivedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });

            await SendPayloadAsync(pipeName, payload);
            await WaitUntilAsync(
                () => coordinator.GetSnapshot().Assignments.Count == 1,
                AsyncTimeout);

            var snapshot = coordinator.GetSnapshot();
            Assert.Equal(Path.GetFullPath(workspace), Assert.Single(snapshot.Assignments).Workspace);
            Assert.Equal(Path.GetFullPath(workspace), Assert.Single(snapshot.RecentEvents!).Workspace);
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
        using var timeout = new CancellationTokenSource(AsyncTimeout);
        await client.ConnectAsync(timeout.Token);
        var bytes = Encoding.UTF8.GetBytes(payload);
        await client.WriteAsync(bytes, timeout.Token);
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
