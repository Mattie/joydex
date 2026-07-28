using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Joydex.Windows.WirelessPanel;

namespace Joydex.Tests;

public sealed class EspHomePanelTransportTests
{
    [Fact]
    public async Task SseParserDispatchesCompleteEventBeforeReadingTheNextChunk()
    {
        var text =
            "event: state\r\n" +
            "data: first\r\n" +
            "data: second\r\n\r\n" +
            "event: ping\n" +
            "data: {}\n\n";
        var checkpoint = text.IndexOf("event: ping", StringComparison.Ordinal);
        var firstDispatched = false;
        await using var stream = new CheckpointStream(
            Encoding.UTF8.GetBytes(text),
            checkpoint,
            () => firstDispatched);
        var events = new List<ServerSentEvent>();

        await EspHomeSseParser.ReadAsync(
            stream,
            (sseEvent, _) =>
            {
                events.Add(sseEvent);
                firstDispatched = true;
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(
            [
                new ServerSentEvent("state", "first\nsecond"),
                new ServerSentEvent("ping", "{}"),
            ],
            events);
    }

    [Fact]
    public async Task SseParserRejectsAnOversizedLineBeforeDispatch()
    {
        await using var stream = new MemoryStream(
            Encoding.UTF8.GetBytes("data: " + new string('x', 16 * 1024 + 1)));

        await Assert.ThrowsAsync<IOException>(
            () => EspHomeSseParser.ReadAsync(
                stream,
                (_, _) => ValueTask.CompletedTask,
                CancellationToken.None));
    }

    [Fact]
    public async Task SseParserRejectsOversizedAccumulatedEventData()
    {
        var line = "data: " + new string('x', 15 * 1024) + "\n";
        await using var stream = new MemoryStream(
            Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(line, 5))));

        await Assert.ThrowsAsync<IOException>(
            () => EspHomeSseParser.ReadAsync(
                stream,
                (_, _) => ValueTask.CompletedTask,
                CancellationToken.None));
    }

    [Fact]
    public void StateParserPrefersNameIdAndAcceptsThe2026_8IdFormat()
    {
        Assert.True(EspHomeStateEventParser.TryParse(
            """
            {"name_id":"binary_sensor/Task 1","id":"binary_sensor-task_2","state":"OFF","value":false}
            """,
            out var transitional));
        Assert.Equal("binary_sensor/Task 1", transitional.Identifier);
        Assert.False(transitional.IsOn);

        Assert.True(EspHomeStateEventParser.TryParse(
            """
            {"id":"binary_sensor/Task 2","state":"ON","value":true}
            """,
            out var current));
        Assert.Equal("binary_sensor/Task 2", current.Identifier);
        Assert.True(current.IsOn);
    }

    [Fact]
    public void PressTrackerIgnoresCatchUpAndEmitsOnlyAllowlistedOffToOnEdges()
    {
        var tracker = new EspHomePressTracker();

        Assert.False(tracker.TryObserve(
            new EspHomeStateEvent("binary_sensor/Task 1", true),
            out _));
        Assert.False(tracker.TryObserve(
            new EspHomeStateEvent("binary_sensor/Task 1", true),
            out _));
        Assert.False(tracker.TryObserve(
            new EspHomeStateEvent("binary_sensor/Task 1", false),
            out _));
        Assert.True(tracker.TryObserve(
            new EspHomeStateEvent("binary_sensor/Task 1", true),
            out var pressed));
        Assert.Equal(EspHomePanelButton.Task1, pressed);
        Assert.False(tracker.TryObserve(
            new EspHomeStateEvent("binary_sensor-task_1", false),
            out _));
        Assert.True(tracker.TryObserve(
            new EspHomeStateEvent("binary_sensor-task_1", true),
            out pressed));
        Assert.Equal(EspHomePanelButton.Task1, pressed);
        Assert.False(tracker.TryObserve(
            new EspHomeStateEvent("binary_sensor/Anything Else", false),
            out _));
        Assert.False(tracker.TryObserve(
            new EspHomeStateEvent("binary_sensor/Anything Else", true),
            out _));
    }

    [Theory]
    [InlineData("binary_sensor/Task 1", EspHomePanelButton.Task1)]
    [InlineData("binary_sensor/Task 2", EspHomePanelButton.Task2)]
    [InlineData("binary_sensor/Task 3", EspHomePanelButton.Task3)]
    [InlineData("binary_sensor/Task 4", EspHomePanelButton.Task4)]
    [InlineData("binary_sensor/Sidebar", EspHomePanelButton.PlanMode)]
    [InlineData("binary_sensor-task_1", EspHomePanelButton.Task1)]
    [InlineData("binary_sensor-task_2", EspHomePanelButton.Task2)]
    [InlineData("binary_sensor-task_3", EspHomePanelButton.Task3)]
    [InlineData("binary_sensor-task_4", EspHomePanelButton.Task4)]
    [InlineData("binary_sensor-sidebar", EspHomePanelButton.PlanMode)]
    public void PressTrackerMapsEverySupportedEntity(
        string identifier,
        EspHomePanelButton expected)
    {
        var tracker = new EspHomePressTracker();

        Assert.False(tracker.TryObserve(new EspHomeStateEvent(identifier, false), out _));
        Assert.True(tracker.TryObserve(new EspHomeStateEvent(identifier, true), out var pressed));
        Assert.Equal(expected, pressed);
    }

    [Fact]
    public async Task SelectPostsUseExactEntitiesAndRemainSerialized()
    {
        var handler = new SerializedRecordingHandler();
        using var httpClient = new HttpClient(handler);
        await using var transport = new EspHomePanelTransport(
            httpClient,
            new Uri("http://panel.local"));

        var states = transport.SetTaskStatesAsync(
            EspHomeTaskState.Empty,
            EspHomeTaskState.Running,
            EspHomeTaskState.Attention,
            EspHomeTaskState.Complete);
        await handler.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var update = transport.SetTaskStateUpdatesAsync(
            [new EspHomeTaskStateUpdate(2, EspHomeTaskState.Attention)]);
        handler.ReleaseFirstRequest();

        await Task.WhenAll(states, update);

        Assert.Equal(1, handler.MaxActiveRequests);
        Assert.Equal(
            [
                "POST /select/Task%201%20State/set?option=EMPTY",
                "POST /select/Task%202%20State/set?option=RUNNING",
                "POST /select/Task%203%20State/set?option=ATTENTION",
                "POST /select/Task%204%20State/set?option=COMPLETE",
                "POST /select/Task%202%20State/set?option=ATTENTION",
            ],
            handler.Requests);
    }

    [Fact]
    public async Task TaskStateUpdatesPostOnlyChangedEntities()
    {
        var handler = new SerializedRecordingHandler();
        using var httpClient = new HttpClient(handler);
        await using var transport = new EspHomePanelTransport(
            httpClient,
            new Uri("http://panel.local"));

        var updates = transport.SetTaskStateUpdatesAsync(
            [
                new EspHomeTaskStateUpdate(2, EspHomeTaskState.Attention),
                new EspHomeTaskStateUpdate(4, EspHomeTaskState.Empty),
            ]);
        await handler.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        handler.ReleaseFirstRequest();
        await updates;

        Assert.Equal(
            [
                "POST /select/Task%202%20State/set?option=ATTENTION",
                "POST /select/Task%204%20State/set?option=EMPTY",
            ],
            handler.Requests);
    }

    [Fact]
    public async Task TaskStateUpdatesRejectSlotsOutsideThePanel()
    {
        using var httpClient = new HttpClient(new SerializedRecordingHandler());
        await using var transport = new EspHomePanelTransport(
            httpClient,
            new Uri("http://panel.local"));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => transport.SetTaskStateUpdatesAsync(
                [new EspHomeTaskStateUpdate(5, EspHomeTaskState.Running)]));
    }

    [Fact]
    public async Task TimedOutPostReleasesTheSerializationGate()
    {
        var handler = new TimeoutThenSuccessHandler();
        using var httpClient = new HttpClient(handler);
        await using var transport = new EspHomePanelTransport(
            httpClient,
            new Uri("http://panel.local"),
            postTimeout: TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<TimeoutException>(
            () => transport.SetTaskStateUpdatesAsync(
                [new EspHomeTaskStateUpdate(1, EspHomeTaskState.Attention)]));
        await transport
            .SetTaskStateUpdatesAsync(
                [new EspHomeTaskStateUpdate(1, EspHomeTaskState.Running)])
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task PartialTaskBatchFailureAllowsACompleteReplacement()
    {
        var handler = new PartialBatchFailureHandler();
        using var httpClient = new HttpClient(handler);
        await using var transport = new EspHomePanelTransport(
            httpClient,
            new Uri("http://panel.local"));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => transport.SetTaskStatesAsync(
                EspHomeTaskState.Running,
                EspHomeTaskState.Attention,
                EspHomeTaskState.Complete,
                EspHomeTaskState.Empty));
        await transport.SetTaskStatesAsync(
            EspHomeTaskState.Empty,
            EspHomeTaskState.Running,
            EspHomeTaskState.Attention,
            EspHomeTaskState.Complete);

        Assert.Equal(
            [
                "/select/Task%201%20State/set?option=RUNNING",
                "/select/Task%202%20State/set?option=ATTENTION",
                "/select/Task%203%20State/set?option=COMPLETE",
                "/select/Task%201%20State/set?option=EMPTY",
                "/select/Task%202%20State/set?option=RUNNING",
                "/select/Task%203%20State/set?option=ATTENTION",
                "/select/Task%204%20State/set?option=COMPLETE",
            ],
            handler.Paths);
    }

    [Fact]
    public async Task EventLoopUsesPreferredAndCurrentIdsWithoutReplayingCatchUp()
    {
        var handler = new EventSequenceHandler(
            """
            event: ping
            data: {"uptime":1}

            event: state
            data: {"name_id":"binary_sensor/Task 1","id":"binary_sensor-task_2","state":"OFF"}

            event: state
            data: {"name_id":"binary_sensor/Task 1","id":"binary_sensor-task_2","state":"ON"}

            event: state
            data: {"name_id":"binary_sensor/Task 1","id":"binary_sensor-task_2","state":"ON"}

            event: state
            data: {"name_id":"binary_sensor/Unknown","state":"OFF"}

            event: state
            data: {"name_id":"binary_sensor/Unknown","state":"ON"}

            event: state
            data: {"name_id":"binary_sensor/Sidebar","state":"ON"}

            event: state
            data: {"name_id":"binary_sensor/Sidebar","state":"OFF"}

            event: state
            data: {"name_id":"binary_sensor/Sidebar","state":"ON"}

            event: state
            data: {"id":"binary_sensor/Task 2","state":"OFF"}

            event: state
            data: {"id":"binary_sensor/Task 2","state":"ON"}
            """ + "\n\n");
        using var httpClient = new HttpClient(handler);
        await using var transport = new EspHomePanelTransport(
            httpClient,
            new Uri("http://panel.local/"),
            reconnectDelay: TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var presses = new List<EspHomePanelButton>();

        await transport.RunAsync(
            (button, _) =>
            {
                presses.Add(button);
                if (presses.Count == 3)
                {
                    cancellation.Cancel();
                }

                return ValueTask.CompletedTask;
            },
            cancellationToken: cancellation.Token);

        Assert.Equal(
            [EspHomePanelButton.Task1, EspHomePanelButton.PlanMode, EspHomePanelButton.Task2],
            presses);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET /events", request.MethodAndPath);
        Assert.Contains("text/event-stream", request.Accept);
        Assert.True(request.NoCache);
    }

    [Fact]
    public async Task ReconnectStartsANewCatchUpBaseline()
    {
        var handler = new EventSequenceHandler(
            """
            event: state
            data: {"name_id":"binary_sensor/Task 1","state":"OFF"}

            event: state
            data: {"name_id":"binary_sensor/Task 1","state":"ON"}

            event: state
            data: {"name_id":"binary_sensor/Task 1","state":"OFF"}
            """ + "\n\n",
            """
            event: state
            data: {"id":"binary_sensor/Task 1","state":"ON"}

            event: state
            data: {"id":"binary_sensor/Task 1","state":"OFF"}

            event: state
            data: {"id":"binary_sensor/Task 2","state":"OFF"}

            event: state
            data: {"id":"binary_sensor/Task 2","state":"ON"}
            """ + "\n\n");
        using var httpClient = new HttpClient(handler);
        await using var transport = new EspHomePanelTransport(
            httpClient,
            new Uri("http://panel.local/"),
            reconnectDelay: TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var presses = new List<EspHomePanelButton>();
        var connected = 0;

        await transport.RunAsync(
            (button, _) =>
            {
                presses.Add(button);
                if (presses.Count == 2)
                {
                    cancellation.Cancel();
                }

                return ValueTask.CompletedTask;
            },
            _ =>
            {
                connected++;
                return ValueTask.CompletedTask;
            },
            cancellation.Token);

        Assert.Equal(2, connected);
        Assert.Equal([EspHomePanelButton.Task1, EspHomePanelButton.Task2], presses);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task EventLoopReconnectsAfterTheStreamMissesItsIdleDeadline()
    {
        var handler = new IdleThenEventHandler();
        using var httpClient = new HttpClient(handler);
        await using var transport = new EspHomePanelTransport(
            httpClient,
            new Uri("http://panel.local/"),
            reconnectDelay: TimeSpan.Zero,
            sseIdleTimeout: TimeSpan.FromMilliseconds(50));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var presses = new List<EspHomePanelButton>();

        await transport.RunAsync(
            (button, _) =>
            {
                presses.Add(button);
                cancellation.Cancel();
                return ValueTask.CompletedTask;
            },
            cancellationToken: cancellation.Token);

        Assert.True(handler.FirstStream.ReadWasCanceled);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal([EspHomePanelButton.Task1], presses);
    }

    [Fact]
    public async Task DisposeCancelsAStreamReadAndWaitsForTheLoop()
    {
        var stream = new CancellationAwareStream();
        var handler = new StreamHandler(stream);
        using var httpClient = new HttpClient(handler);
        var transport = new EspHomePanelTransport(
            httpClient,
            new Uri("http://panel.local/"));
        var runTask = transport.RunAsync((_, _) => ValueTask.CompletedTask);
        await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await transport.DisposeAsync();

        await runTask.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(stream.ReadWasCanceled);
    }

    private sealed class CheckpointStream(
        byte[] bytes,
        int checkpoint,
        Func<bool> canReadPastCheckpoint) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            AssertCanRead();
            var count = LimitCount(buffer.Length);
            return base.ReadAsync(buffer[..count], cancellationToken);
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            AssertCanRead();
            return base.ReadAsync(buffer, offset, LimitCount(count), cancellationToken);
        }

        private int LimitCount(int requested)
        {
            var remainingBeforeCheckpoint = checkpoint - (int)Position;
            return remainingBeforeCheckpoint > 0
                ? Math.Min(requested, remainingBeforeCheckpoint)
                : requested;
        }

        private void AssertCanRead()
        {
            if (Position >= checkpoint && !canReadPastCheckpoint())
            {
                throw new InvalidOperationException("The parser read the next chunk before dispatching the first event.");
            }
        }
    }

    private sealed class SerializedRecordingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _releaseFirstRequest = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeRequests;
        private int _requestCount;

        public TaskCompletionSource FirstRequestStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<string> Requests { get; } = [];

        public int MaxActiveRequests => Volatile.Read(ref _maxActiveRequests);

        private int _maxActiveRequests;

        public void ReleaseFirstRequest() => _releaseFirstRequest.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _activeRequests);
            UpdateMaximum(active);
            var requestNumber = Interlocked.Increment(ref _requestCount);
            Requests.Enqueue($"{request.Method} {request.RequestUri!.PathAndQuery}");
            try
            {
                if (requestNumber == 1)
                {
                    FirstRequestStarted.TrySetResult();
                    await _releaseFirstRequest.Task.WaitAsync(cancellationToken);
                }

                await Task.Yield();
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            finally
            {
                Interlocked.Decrement(ref _activeRequests);
            }
        }

        private void UpdateMaximum(int candidate)
        {
            var current = Volatile.Read(ref _maxActiveRequests);
            while (candidate > current)
            {
                var observed = Interlocked.CompareExchange(
                    ref _maxActiveRequests,
                    candidate,
                    current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }

    private sealed class EventSequenceHandler(params string[] eventStreams) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> _eventStreams = new(eventStreams);

        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method.Method,
                request.RequestUri!.PathAndQuery,
                string.Join(",", request.Headers.Accept.Select(value => value.MediaType)),
                request.Headers.CacheControl?.NoCache == true));
            if (!_eventStreams.TryDequeue(out var stream))
            {
                throw new HttpRequestException("No canned ESPHome event stream remains.");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(stream, Encoding.UTF8, "text/event-stream"),
            });
        }
    }

    private sealed class TimeoutThenSuccessHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            if (RequestCount == 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class PartialBatchFailureHandler : HttpMessageHandler
    {
        private int _requestCount;

        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Paths.Add(request.RequestUri!.PathAndQuery);
            var requestNumber = Interlocked.Increment(ref _requestCount);
            return Task.FromResult(new HttpResponseMessage(
                requestNumber == 3
                    ? HttpStatusCode.InternalServerError
                    : HttpStatusCode.OK));
        }
    }

    private sealed class IdleThenEventHandler : HttpMessageHandler
    {
        private int _requestCount;

        public PrefixThenBlockingStream FirstStream { get; } = new(
            """
            event: state
            data: {"name_id":"binary_sensor/Task 1","state":"OFF"}
            """ + "\n\n");

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requestNumber = Interlocked.Increment(ref _requestCount);
            if (requestNumber == 1)
            {
                var content = new StreamContent(FirstStream);
                content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = content,
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    event: state
                    data: {"id":"binary_sensor/Task 1","state":"OFF"}

                    event: state
                    data: {"id":"binary_sensor/Task 1","state":"ON"}
                    """ + "\n\n",
                    Encoding.UTF8,
                    "text/event-stream"),
            });
        }
    }

    private sealed record RecordedRequest(
        string Method,
        string Path,
        string Accept,
        bool NoCache)
    {
        public string MethodAndPath => $"{Method} {Path}";
    }

    private sealed class StreamHandler(Stream stream) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            });
        }
    }

    private sealed class PrefixThenBlockingStream(string prefix) : Stream
    {
        private readonly MemoryStream _prefix = new(Encoding.UTF8.GetBytes(prefix));

        public bool ReadWasCanceled { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_prefix.Position < _prefix.Length)
            {
                return await _prefix.ReadAsync(buffer, cancellationToken);
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            catch (OperationCanceledException)
            {
                ReadWasCanceled = true;
                throw;
            }
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _prefix.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class CancellationAwareStream : Stream
    {
        public TaskCompletionSource ReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ReadWasCanceled { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            catch (OperationCanceledException)
            {
                ReadWasCanceled = true;
                throw;
            }
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
