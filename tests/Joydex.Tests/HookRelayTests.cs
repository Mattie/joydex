using System.Text.Json;
using System.IO.Pipes;
using System.Diagnostics;
using Joydex.Core.TaskAlerts;

namespace Joydex.Tests;

public sealed class HookRelayTests
{
    [Fact]
    public async Task RelayRecoversWhenTheListenerAppearsDuringItsConnectionBudget()
    {
        var name = $"Joydex.Tests.{Guid.NewGuid():N}";
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedAt = DateTimeOffset.UtcNow;
        var send = Task.Run(() =>
        {
            started.SetResult();
            // Exercise the real delayed pipe without requiring a loaded runner to schedule
            // this test's continuation inside the production 250 ms window. The default
            // budget is checked separately below.
            HookRelay.TrySend(name, [new(CodexLifecycleEvent.UserPromptSubmit, "fixture", "turn", receivedAt)],
                connectionBudget: TimeSpan.FromSeconds(10));
        });
        await started.Task;
        await Task.Delay(50);
        await using var listener = new NamedPipeServerStream(name, PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await listener.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(15));
        using var reader = new StreamReader(listener);
        using var document = JsonDocument.Parse(await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(15)));
        await send;
        Assert.Equal("UserPromptSubmit", document.RootElement.GetProperty("event").GetString());
        Assert.Equal(receivedAt.ToUnixTimeMilliseconds(), document.RootElement.GetProperty("receivedAtUnixMs").GetInt64());
    }

    [Fact]
    public void MissingOptionalListenerReturnsWithinTheHookTimeout()
    {
        var elapsed = Stopwatch.StartNew();
        HookRelay.TrySend($"Joydex.Tests.{Guid.NewGuid():N}",
            [new(CodexLifecycleEvent.UserPromptSubmit, "fixture", "turn", DateTimeOffset.UtcNow)]);
        Assert.InRange(elapsed.Elapsed, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void QuestionsSurviveActiveRestartThenClearOnAnyReplyPromptOrOptedInStop()
    {
        var now = DateTimeOffset.UtcNow;
        var sent = new HookInput("PostToolUse", "session", "turn", null, @"C:\fixture\rollout.jsonl",
            "request_user_input_async", JsonSerializer.SerializeToElement(new { questions = new[]
            {
                new { title = "First private fixture question" }, new { title = "Second private fixture question" },
            } }), ToolUseId: "call-fixture", ToolResponse: JsonSerializer.SerializeToElement("{\"accepted\":true}"));
        var pool = new TaskAlertPool { ClearQuestionsOnTurnEnd = true };
        Assert.Empty(HookRelay.BuildEvents(sent with { HookEventName = "PreToolUse" }, now));
        foreach (var item in HookRelay.BuildEvents(sent, now)) pool.Apply(item);
        Assert.Equal(TaskAlertState.Approval, Assert.Single(pool.Assignments).State);
        var pending = pool.CaptureState().Assignments.Single().PendingAsyncQuestions!;
        Assert.Equal(2, pending.Length);

        // Neither the async acknowledgment nor an unrelated tool's completion is an answer.
        foreach (var item in HookRelay.BuildEvents(sent, now.AddSeconds(1))) pool.Apply(item);
        Assert.Equal(2, pool.CaptureState().Assignments.Single().PendingAsyncQuestions!.Length);
        Assert.False(pool.Apply(new(CodexLifecycleEvent.ToolCompleted, "session", "turn", now.AddSeconds(2), pending[0])));
        Assert.Equal(TaskAlertState.Approval, Assert.Single(pool.Assignments).State);

        var directory = Path.Combine(Path.GetTempPath(), "joydex-async-hook", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "state.json");
        try
        {
            TaskAlertStateStore.Save(path, pool.CaptureState());
            Assert.DoesNotContain("private fixture question", File.ReadAllText(path));
            var restored = new TaskAlertPool { ClearQuestionsOnTurnEnd = true };
            restored.Restore(TaskAlertStateStore.Load(path));
            Assert.Equal(TaskAlertState.Approval, Assert.Single(restored.Assignments).State);
            foreach (var item in HookRelay.BuildEvents(Reply(sent, "call-fixture", 0), now.AddSeconds(6))) restored.Apply(item);
            Assert.Equal(TaskAlertState.Running, Assert.Single(restored.Assignments).State);
            Assert.Empty(restored.CaptureState().Assignments.Single().PendingAsyncQuestions!);

            // A new question in the resumed turn can relight; an unknown reply is still a new user turn.
            foreach (var item in HookRelay.BuildEvents(sent with { TurnId = "answer-turn", ToolUseId = "second-call" }, now.AddSeconds(6.5)))
                restored.Apply(item);
            Assert.Equal(TaskAlertState.Approval, Assert.Single(restored.Assignments).State);
            foreach (var item in HookRelay.BuildEvents(Reply(sent, "other-call", 1), now.AddSeconds(7))) restored.Apply(item);
            Assert.Equal(TaskAlertState.Running, Assert.Single(restored.Assignments).State);
            foreach (var item in HookRelay.BuildEvents(sent with
                { HookEventName = "UserPromptSubmit", ToolName = null, Prompt = "Another request" }, now.AddSeconds(8)))
                restored.Apply(item);
            Assert.Equal(TaskAlertState.Running, Assert.Single(restored.Assignments).State);
            foreach (var item in HookRelay.BuildEvents(Reply(sent, "call-fixture", 1), now.AddSeconds(9))) restored.Apply(item);
            Assert.Equal(TaskAlertState.Running, Assert.Single(restored.Assignments).State);
            Assert.Empty(restored.CaptureState().Assignments.Single().PendingAsyncQuestions!);
            foreach (var item in HookRelay.BuildEvents(sent with { TurnId = "answer-turn", ToolUseId = "last-call" }, now.AddSeconds(9.5)))
                restored.Apply(item);
            Assert.Equal(TaskAlertState.Approval, Assert.Single(restored.Assignments).State);
            restored.Apply(new(CodexLifecycleEvent.Stop, "session", "answer-turn", now.AddSeconds(10)));
            restored.Advance(now.AddSeconds(12));
            Assert.Equal(TaskAlertState.Completed, Assert.Single(restored.Assignments).State);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("{\"accepted\":false}")]
    [InlineData("Tool failed")]
    [InlineData("{\"accepted\":\"true\"}")]
    public void UnacceptedAsyncCallsDoNotCreateAttention(string response)
    {
        var input = new HookInput("PostToolUse", "session", "turn", null, @"C:\fixture\rollout.jsonl",
            "request_user_input_async", JsonSerializer.SerializeToElement(new { questions = new[] { new { title = "Question" } } }),
            ToolUseId: "call-fixture", ToolResponse: JsonSerializer.SerializeToElement(response));
        Assert.Empty(HookRelay.BuildEvents(input, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("<send_user_message_question_reply>invalid</send_user_message_question_reply>")]
    [InlineData("<send_user_message_question_reply>[{}]</send_user_message_question_reply>")]
    [InlineData("Ordinary text containing <send_user_message_question_reply>[]</send_user_message_question_reply>")]
    public void MalformedOrEmbeddedReplyTextDoesNotClearQuestions(string prompt) =>
        Assert.Empty(AsyncQuestionHooks.ReadAnswerKeys("session", prompt));

    private static HookInput Reply(HookInput original, string callId, int index) => original with
    {
        HookEventName = "UserPromptSubmit", ToolName = null, TurnId = "answer-turn",
        Prompt = "<send_user_message_question_reply>" + JsonSerializer.Serialize(new[] { new
        {
            questionItemId = JsonSerializer.Serialize(new object[] { "request_user_input_async", callId, index }),
            question = "Private fixture question", answer = "Private fixture answer",
        } }) + "</send_user_message_question_reply>",
    };

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("agent-session", true)]
    public void IdentifiesSubagentsFromCodexAgentId(string? agentId, bool expected)
    {
        var input = new HookInput(
            "UserPromptSubmit",
            "session",
            "turn",
            agentId,
            @"C:\transcripts\session.jsonl",
            null,
            default);

        Assert.Equal(expected, HookRelay.IsSubagent(input));
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(null, "", false)]
    [InlineData(null, @"C:\transcripts\session.jsonl", true)]
    [InlineData("agent-session", @"C:\transcripts\agent.jsonl", false)]
    public void TracksOnlyPersistentTopLevelSessions(
        string? agentId,
        string? transcriptPath,
        bool expected)
    {
        var input = new HookInput(
            "UserPromptSubmit",
            "session",
            "turn",
            agentId,
            transcriptPath,
            null,
            default);

        Assert.Equal(expected, HookRelay.IsTrackableSession(input));
    }

    [Fact]
    public void ReadsCodexWorkspaceMetadataWithoutTranscriptContent()
    {
        const string json = """
            {
              "hook_event_name": "UserPromptSubmit",
              "session_id": "session",
              "turn_id": "turn",
              "transcript_path": "C:\\transcripts\\session.jsonl",
              "cwd": "C:\\voice\\realtime-voice-chat"
            }
            """;

        var input = JsonSerializer.Deserialize(json, HookJsonContext.Default.HookInput);

        Assert.NotNull(input);
        Assert.Equal(@"C:\voice\realtime-voice-chat", input.Cwd);
    }
}
