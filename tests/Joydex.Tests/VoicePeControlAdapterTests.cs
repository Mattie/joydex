using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Joydex.Windows.Voice;
using Joydex.Windows.WirelessPanel;

namespace Joydex.Tests;

public sealed class VoicePeControlAdapterTests
{
    [Fact]
    public void ControlTrackerDispatchesOnlyFreshRisingEdgesFromAnExplicitSeed()
    {
        var tracker = new VoicePeControlSignalTracker(VoicePeControlSignal.Wake, initialState: false);

        Assert.True(tracker.TryObserve(
            new EspHomeStateEvent("binary_sensor/Joydex Voice Wake", true),
            out var signal));
        Assert.Equal(VoicePeControlSignal.Wake, signal);
        Assert.False(tracker.TryObserve(
            new EspHomeStateEvent("binary_sensor/Joydex Voice Wake", true),
            out _));
        Assert.False(tracker.TryObserve(
            new EspHomeStateEvent("binary_sensor/Joydex Voice Wake", false),
            out _));
        Assert.True(tracker.TryObserve(
            new EspHomeStateEvent("binary_sensor/Joydex Voice Wake", true),
            out _));
        Assert.False(tracker.TryObserve(new EspHomeStateEvent("binary_sensor/Unrelated", true), out _));
    }

    [Fact]
    public void ControlTrackerSeedsEachSignalFromItsFirstSseSnapshot()
    {
        var tracker = new VoicePeControlSignalTracker();

        Assert.False(tracker.TryObserve(
            new EspHomeStateEvent("binary_sensor/Joydex Voice Wake", false),
            out _));
        Assert.False(tracker.TryObserve(
            new EspHomeStateEvent("binary_sensor/Joydex Voice Hangup", false),
            out _));
        Assert.True(tracker.TryObserve(
            new EspHomeStateEvent("binary_sensor/Joydex Voice Wake", true),
            out var wake));
        Assert.Equal(VoicePeControlSignal.Wake, wake);
        Assert.True(tracker.TryObserve(
            new EspHomeStateEvent("binary_sensor/Joydex Voice Hangup", true),
            out var hangup));
        Assert.Equal(VoicePeControlSignal.Hangup, hangup);
    }

    [Fact]
    public async Task TransportDispatchesFreshWakeHangupAndMuteEdges()
    {
        var handler = new EventStreamHandler(initialWakeState: false, """
            event: state
            data: {"name_id":"binary_sensor/Joydex Voice Wake","state":"OFF"}

            event: state
            data: {"name_id":"binary_sensor/Joydex Voice Hangup","state":"OFF"}

            event: state
            data: {"name_id":"binary_sensor/Joydex Voice Toggle Mute","state":"OFF"}

            event: state
            data: {"name_id":"binary_sensor/Joydex Voice Wake","state":"ON"}

            event: state
            data: {"name_id":"binary_sensor/Joydex Voice Hangup","state":"ON"}

            event: state
            data: {"name_id":"binary_sensor/Joydex Voice Toggle Mute","state":"ON"}

            """ + "\n");
        using var client = new HttpClient(handler);
        await using var transport = new EspHomeVoicePeTransport(
            client,
            new Uri("http://voice-pe.local/"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var signals = new List<VoicePeControlSignal>();

        await transport.RunAsync(
            (signal, _) =>
            {
                signals.Add(signal);
                if (signals.Count == 3)
                {
                    cancellation.Cancel();
                }
                return ValueTask.CompletedTask;
            },
            cancellation.Token);

        Assert.Equal(
            [VoicePeControlSignal.Wake, VoicePeControlSignal.Hangup, VoicePeControlSignal.ToggleMute],
            signals);
        Assert.Equal(
            [
                "http://voice-pe.local/events",
            ],
            handler.RequestUris.Select(uri => uri.AbsoluteUri));
    }

    [Fact]
    public async Task TransportWritesAllowlistedSessionStateToTheDeviceSelect()
    {
        var handler = new EventStreamHandler(initialWakeState: false, events: "");
        using var client = new HttpClient(handler);
        await using var transport = new EspHomeVoicePeTransport(
            client,
            new Uri("http://voice-pe.local/"));

        await transport.SetSessionStateAsync(VoicePeSessionState.Listening);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(
            "http://voice-pe.local/select/joydex_voice_session_state/set?option=Listening",
            request.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task TransportRetriesTransientSessionStateFailures()
    {
        var handler = new EventStreamHandler(
            initialWakeState: false,
            events: "",
            statePostFailures: 2);
        using var client = new HttpClient(handler);
        await using var transport = new EspHomeVoicePeTransport(
            client,
            new Uri("http://voice-pe.local/"));

        await transport.SetSessionStateAsync(VoicePeSessionState.Muted);

        Assert.Equal(3, handler.Requests.Count(request => request.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task TransportReconnectsAfterAnEndedEventStreamInsteadOfEndingMonitoring()
    {
        var handler = new RecoveringEventStreamHandler();
        using var client = new HttpClient(handler);
        var logs = new List<string>();
        await using var transport = new EspHomeVoicePeTransport(
            client,
            new Uri("http://voice-pe.local/"),
            logs.Add);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = transport.RunAsync(
            (signal, _) =>
            {
                Assert.Equal(VoicePeControlSignal.Wake, signal);
                observed.TrySetResult();
                cancellation.Cancel();
                return ValueTask.CompletedTask;
            },
            cancellation.Token);

        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await run;
        Assert.True(handler.EventRequests >= 2);
        Assert.Contains(logs, message => message.Contains("event stream ended", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ForwardsWakeEdgesToTheSessionOwningCoordinator()
    {
        var transport = new FakeTransport();
        var starts = 0;
        await using var adapter = new VoicePeControlAdapter(
            transport,
            _ =>
            {
                starts++;
                return Task.FromResult(new VoiceSessionStartResult(
                    VoiceSessionStartStatus.Requested,
                    "started"));
            },
            _ => { });
        adapter.Start();

        await transport.TriggerAsync();
        await transport.TriggerAsync();

        Assert.Equal(2, starts);
    }

    [Fact]
    public async Task ConfirmedOwnerStartImmediatelyShowsListening()
    {
        var transport = new FakeTransport();
        await using var adapter = new VoicePeControlAdapter(
            transport,
            _ => Task.FromResult(new VoiceSessionStartResult(
                VoiceSessionStartStatus.Confirmed,
                "ready")),
            _ => { });
        adapter.Start();

        await transport.TriggerAsync();

        Assert.Equal([VoicePeSessionState.Listening], transport.SessionStates);
    }

    [Fact]
    public async Task OwnerCanPublishListeningAtCueStartWithoutADuplicateStateChange()
    {
        var transport = new FakeTransport();
        await using var adapter = new VoicePeControlAdapter(
            transport,
            _ => Task.FromResult(new VoiceSessionStartResult(
                VoiceSessionStartStatus.Confirmed,
                "ready")),
            _ => { },
            startVoiceSetsListeningState: true);
        adapter.Start();

        await transport.TriggerAsync();

        Assert.Empty(transport.SessionStates);
    }

    [Fact]
    public async Task FailedCallbackDoesNotPoisonTheDeviceEventLoop()
    {
        var transport = new FakeTransport();
        var calls = 0;
        var logs = new List<string>();
        await using var adapter = new VoicePeControlAdapter(
            transport,
            _ =>
            {
                calls++;
                if (calls == 1)
                {
                    throw new InvalidOperationException("test failure");
                }

                return Task.FromResult(new VoiceSessionStartResult(
                    VoiceSessionStartStatus.Requested,
                    "started"));
            },
            logs.Add);
        adapter.Start();

        await transport.TriggerAsync();
        await transport.TriggerAsync();

        Assert.Equal(2, calls);
        Assert.Contains(logs, message => message.Contains("test failure", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailedStartShowsErrorWhileConfirmedLifecycleControlsListeningAndArmed()
    {
        var failedTransport = new FakeTransport();
        await using (var failedAdapter = new VoicePeControlAdapter(
                         failedTransport,
                         _ => Task.FromResult(new VoiceSessionStartResult(
                             VoiceSessionStartStatus.Rejected,
                             "failed")),
                         _ => { }))
        {
            failedAdapter.Start();
            await failedTransport.TriggerAsync();
            Assert.Equal([VoicePeSessionState.Error], failedTransport.SessionStates);
        }

        var lifecycleTransport = new FakeTransport();
        await using (var lifecycleAdapter = new VoicePeControlAdapter(
                         lifecycleTransport,
                         _ => Task.FromResult(new VoiceSessionStartResult(
                             VoiceSessionStartStatus.Requested,
                             "started")),
                         _ => { }))
        {
            lifecycleAdapter.Start();
            await lifecycleTransport.TriggerAsync();
            await lifecycleAdapter.ConfirmSessionStartedAsync();
            await lifecycleAdapter.ConfirmSessionEndedAsync();
            Assert.Equal(
                [VoicePeSessionState.Listening, VoicePeSessionState.Armed],
                lifecycleTransport.SessionStates);
        }
    }

    [Fact]
    public async Task DuplicateWakePreservesStartingOrListeningUntilALifecycleMarkerArrives()
    {
        var transport = new FakeTransport();
        await using var adapter = new VoicePeControlAdapter(
            transport,
            _ => Task.FromResult(new VoiceSessionStartResult(
                VoiceSessionStartStatus.SessionActive,
                "already latched")),
            _ => { });
        adapter.Start();

        await transport.TriggerAsync();

        Assert.Empty(transport.SessionStates);
    }

    [Fact]
    public async Task HangupSignalStopsTheJoydexOwnedSession()
    {
        var transport = new FakeTransport();
        var stops = 0;
        await using var adapter = new VoicePeControlAdapter(
            transport,
            _ => Task.FromResult(new VoiceSessionStartResult(
                VoiceSessionStartStatus.Requested,
                "started")),
            _ => { },
            stopVoice: _ =>
            {
                stops++;
                return Task.CompletedTask;
            });
        adapter.Start();

        await transport.TriggerAsync(VoicePeControlSignal.Hangup);

        Assert.Equal(1, stops);
    }

    [Fact]
    public async Task MuteSignalPublishesMutedAndListeningAndSessionEndRearms()
    {
        var transport = new FakeTransport();
        var results = new Queue<bool?>([true, false, true]);
        await using var adapter = new VoicePeControlAdapter(
            transport,
            _ => Task.FromResult(new VoiceSessionStartResult(
                VoiceSessionStartStatus.Requested,
                "started")),
            _ => { },
            toggleMicrophoneMute: () => results.Dequeue());
        adapter.Start();

        await transport.TriggerAsync(VoicePeControlSignal.ToggleMute);
        await transport.TriggerAsync(VoicePeControlSignal.ToggleMute);
        await transport.TriggerAsync(VoicePeControlSignal.ToggleMute);
        await adapter.ConfirmSessionEndedAsync();

        Assert.Equal(
            [
                VoicePeSessionState.Muted,
                VoicePeSessionState.Listening,
                VoicePeSessionState.Muted,
                VoicePeSessionState.Armed,
            ],
            transport.SessionStates);
    }

    [Fact]
    public void LogMarkerParserIgnoresContentAndRecognizesAllowlistedLifecycleTokens()
    {
        Assert.Equal(
            CodexVoiceLogMarker.None,
            CodexVoiceLogMarkerParser.Detect("ordinary transcript-like content"u8));
        Assert.Equal(
            CodexVoiceLogMarker.Started,
            CodexVoiceLogMarkerParser.Detect("prefix realtime_session_started suffix"u8));
        Assert.Equal(
            CodexVoiceLogMarker.Stopped,
            CodexVoiceLogMarkerParser.Detect(
                "method=thread/realtime/stop originWebcontentsId=3 queueWaitMs=0"u8));
        Assert.Equal(
            CodexVoiceLogMarker.None,
            CodexVoiceLogMarkerParser.Detect("someone merely said method=thread/realtime/stop"u8));
    }

    [Fact]
    public async Task LogObserverRecognizesAStopMarkerSplitAcrossFileAppends()
    {
        var logRoot = Path.Combine(
            Path.GetTempPath(),
            $"joydex-voice-observer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(logRoot);
        try
        {
            var logPath = Path.Combine(logRoot, "codex-desktop-test.log");
            await File.WriteAllTextAsync(logPath, "existing log content");
            var observed = new TaskCompletionSource<CodexVoiceLogMarker>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await using var observer = new CodexVoiceSessionObserver(
                logRoot,
                (marker, _) =>
                {
                    observed.TrySetResult(marker);
                    return ValueTask.CompletedTask;
                });
            observer.Start();

            await File.AppendAllTextAsync(
                logPath,
                " method=thread/realtime/stop " + new string('x', 120));
            await Task.Delay(TimeSpan.FromMilliseconds(750));
            await File.AppendAllTextAsync(logPath, " originWebcontentsId=3");

            Assert.Equal(
                CodexVoiceLogMarker.Stopped,
                await observed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            Directory.Delete(logRoot, recursive: true);
        }
    }

    private sealed class FakeTransport : IVoicePeControlTransport
    {
        private Func<VoicePeControlSignal, CancellationToken, ValueTask>? _onSignal;

        public List<VoicePeSessionState> SessionStates { get; } = [];

        public Task RunAsync(
            Func<VoicePeControlSignal, CancellationToken, ValueTask> onSignal,
            CancellationToken cancellationToken = default)
        {
            _onSignal = onSignal;
            return Task.CompletedTask;
        }

        public async Task TriggerAsync(VoicePeControlSignal signal = VoicePeControlSignal.Wake)
        {
            Assert.NotNull(_onSignal);
            await _onSignal(signal, default);
        }

        public Task SetSessionStateAsync(
            VoicePeSessionState state,
            CancellationToken cancellationToken = default)
        {
            SessionStates.Add(state);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EventStreamHandler(
        bool initialWakeState,
        string events,
        int statePostFailures = 0) : HttpMessageHandler
    {
        private int _remainingStatePostFailures = statePostFailures;

        public List<(HttpMethod Method, Uri Uri)> Requests { get; } = [];

        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.NotNull(request.RequestUri);
            RequestUris.Add(request.RequestUri);
            Requests.Add((request.Method, request.RequestUri));
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("/select/joydex_voice_session_state/set", request.RequestUri.AbsolutePath);
                if (Interlocked.Decrement(ref _remainingStatePostFailures) >= 0)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    {
                        RequestMessage = request,
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                });
            }

            HttpContent content;
            if (request.RequestUri.AbsolutePath == "/events")
            {
                content = new ByteArrayContent(Encoding.UTF8.GetBytes(events));
                content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            }
            else
            {
                Assert.Equal("/binary_sensor/joydex_voice_wake", request.RequestUri.AbsolutePath);
                content = new StringContent(
                    $$"""{"id":"binary_sensor-joydex_voice_wake","value":{{initialWakeState.ToString().ToLowerInvariant()}},"state":"{{(initialWakeState ? "ON" : "OFF")}}"}""",
                    Encoding.UTF8,
                    "application/json");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
                RequestMessage = request,
            });
        }
    }

    private sealed class RecoveringEventStreamHandler : HttpMessageHandler
    {
        private int _eventRequests;

        public int EventRequests => Volatile.Read(ref _eventRequests);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.NotNull(request.RequestUri);
            Assert.Equal("/events", request.RequestUri.AbsolutePath);
            var attempt = Interlocked.Increment(ref _eventRequests);
            var content = new StringContent(
                attempt == 1
                    ? "event: state\ndata: not-json\n\n"
                    : "event: state\ndata: {\"name_id\":\"binary_sensor/Joydex Voice Wake\",\"state\":\"OFF\"}\n\n"
                      + "event: state\ndata: {\"name_id\":\"binary_sensor/Joydex Voice Wake\",\"state\":\"ON\"}\n\n",
                Encoding.UTF8,
                "text/event-stream");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
                RequestMessage = request,
            });
        }
    }

}
