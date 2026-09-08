using System.Text.Json;
using Joydex.Windows.Voice;

namespace Joydex.Tests;

public sealed class CodexRealtimeSessionTests
{
    [Fact]
    public async Task StartReturnsCorrelatedSdpAndReadyRequiresTypedStartAndMedia()
    {
        var threadId = Guid.NewGuid().ToString("D");
        var control = new FakeRealtimeControl(threadId);
        await using var session = new CodexRealtimeSession(control, "cove");

        var start = session.StartAsync("v=0\r\n");
        await control.WaitForRequestAsync("thread/realtime/start");
        control.Emit("thread/realtime/started", Guid.NewGuid().ToString("D"));
        Assert.False(session.Ready.IsCompleted);
        control.Emit("thread/realtime/sdp", threadId, ("sdp", "answer-sdp"));

        Assert.Equal("answer-sdp", await start);
        Assert.False(session.Ready.IsCompleted);
        control.Emit("thread/realtime/started", threadId);
        Assert.False(session.Ready.IsCompleted);
        session.MarkMediaConnected();

        await session.Ready;
        var request = Assert.Single(control.Requests, value => value.Method == "thread/realtime/start");
        Assert.Equal(threadId, request.Parameters.GetProperty("threadId").GetString());
        Assert.Equal("webrtc", request.Parameters.GetProperty("transport").GetProperty("type").GetString());
        Assert.Equal("cove", request.Parameters.GetProperty("voice").GetString());
        Assert.Equal(JsonValueKind.Null, request.Parameters.GetProperty("realtimeStartInstructions").ValueKind);
    }

    [Fact]
    public async Task DesktopTaskMessagingRequiresFreshToolCallInsteadOfTrustingEarlierFailure()
    {
        var threadId = Guid.NewGuid().ToString("D");
        var control = new FakeRealtimeControl(threadId);
        await using var session = new CodexRealtimeSession(
            control,
            desktopTaskMessagingEnabled: true);

        var start = session.StartAsync("v=0\r\n");
        await control.WaitForRequestAsync("thread/realtime/start");
        var request = Assert.Single(control.Requests, value => value.Method == "thread/realtime/start");
        var instructions = request.Parameters.GetProperty("realtimeStartInstructions").GetString();

        Assert.Contains("must call that tool", instructions, StringComparison.Ordinal);
        Assert.Contains("earlier turn said the bridge was unavailable", instructions, StringComparison.Ordinal);

        control.Emit("thread/realtime/sdp", threadId, ("sdp", "answer-sdp"));
        control.Emit("thread/realtime/started", threadId);
        await start;
    }

    [Fact]
    public async Task StopIsIdempotentAndCompletesOnCorrelatedTypedClose()
    {
        var threadId = Guid.NewGuid().ToString("D");
        var control = new FakeRealtimeControl(threadId);
        await using var session = new CodexRealtimeSession(control);
        var start = session.StartAsync("v=0\r\n");
        await control.WaitForRequestAsync("thread/realtime/start");
        control.Emit("thread/realtime/sdp", threadId, ("sdp", "answer-sdp"));
        control.Emit("thread/realtime/started", threadId);
        await start;
        session.MarkMediaConnected();
        await session.Ready;

        var firstStop = session.StopAsync();
        var secondStop = session.StopAsync();
        await control.WaitForRequestAsync("thread/realtime/stop");
        control.Emit("thread/realtime/closed", Guid.NewGuid().ToString("D"), ("reason", "wrong task"));
        Assert.False(session.Completion.IsCompleted);
        control.Emit("thread/realtime/closed", threadId, ("reason", "requested"));

        await Task.WhenAll(firstStop, secondStop, session.Completion);
        Assert.Equal("requested", session.CloseReason);
        Assert.Single(control.Requests, value => value.Method == "thread/realtime/stop");
    }

    [Fact]
    public async Task CorrelatedRealtimeErrorFaultsStartReadyAndCompletion()
    {
        var threadId = Guid.NewGuid().ToString("D");
        var control = new FakeRealtimeControl(threadId);
        await using var session = new CodexRealtimeSession(control);
        var start = session.StartAsync("v=0\r\n");
        await control.WaitForRequestAsync("thread/realtime/start");

        control.Emit("thread/realtime/error", threadId, ("message", "media failed"));

        var startError = await Assert.ThrowsAsync<CodexRealtimeSessionException>(() => start);
        Assert.Equal("media failed", startError.Message);
        await Assert.ThrowsAsync<CodexRealtimeSessionException>(() => session.Ready);
        await Assert.ThrowsAsync<CodexRealtimeSessionException>(() => session.Completion);
    }

    private sealed class FakeRealtimeControl(string threadId) : ICodexRealtimeControl
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ThreadId { get; } = threadId;

        public event Action<string, JsonElement>? NotificationReceived;

        public Task Completion => _completion.Task;

        public List<(string Method, JsonElement Parameters)> Requests { get; } = [];

        public Task<JsonElement> RequestAsync(
            string method,
            object parameters,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Requests.Add((method, JsonSerializer.SerializeToElement(parameters)));
            return Task.FromResult(default(JsonElement));
        }

        public async Task WaitForRequestAsync(string method)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!Requests.Any(request => request.Method == method))
            {
                await Task.Delay(10, timeout.Token);
            }
        }

        public void Emit(string method, string notificationThreadId, params (string Name, string Value)[] values)
        {
            var properties = new Dictionary<string, string>
            {
                ["threadId"] = notificationThreadId,
            };
            foreach (var (name, value) in values)
            {
                properties[name] = value;
            }

            NotificationReceived?.Invoke(method, JsonSerializer.SerializeToElement(properties));
        }
    }
}
