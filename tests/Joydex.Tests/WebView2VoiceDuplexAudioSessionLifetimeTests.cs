using Joydex.App;
using Joydex.Windows.Voice;

namespace Joydex.Tests;

public sealed class WebView2VoiceDuplexAudioSessionLifetimeTests
{
    [Theory]
    [InlineData("Cancel", "my timer")]
    [InlineData("End", "the sentence")]
    [InlineData("Goodbye", "for now")]
    [InlineData("Hang up", "the picture")]
    public async Task IncompleteTranscriptPrefixesDoNotHangUp(string prefix, string continuation)
    {
        var stopCalls = 0;
        await using var session = new WebView2VoiceDuplexAudioSession(
            new ImmediateMediaDispatcher(), Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            (_, _) => Task.FromResult(string.Empty), () => { }, Task.CompletedTask,
            Task.Delay(Timeout.InfiniteTimeSpan), _ => { stopCalls++; return Task.CompletedTask; },
            () => ValueTask.CompletedTask, conversationSpeakerGain: 1);

        session.HandleWebMessage(System.Text.Json.JsonSerializer.Serialize(new
            { type = "user-transcript-added", transcript = prefix }));
        Assert.Equal(0, stopCalls);
        session.HandleWebMessage(System.Text.Json.JsonSerializer.Serialize(new
            { type = "user-transcript-added", transcript = continuation }));
        session.HandleWebMessage(System.Text.Json.JsonSerializer.Serialize(new
            { type = "user-turn-done", transcript = prefix + " " + continuation }));
        Assert.Equal(0, stopCalls);
        Assert.False(session.Completion.IsCompleted);
    }

    [Fact]
    public async Task NativeDotLocalCompletionAllowsDrainButStopStillWaitsForRemoteAcknowledgment()
    {
        var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new WebView2VoiceDuplexAudioSession(new ImmediateMediaDispatcher(),
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            (_, _) => Task.FromResult(string.Empty), () => { }, Task.CompletedTask,
            Task.Delay(Timeout.InfiniteTimeSpan), async _ =>
            {
                stopEntered.TrySetResult();
                await releaseStop.Task;
            }, () => ValueTask.CompletedTask, conversationSpeakerGain: 1);
        var stop = session.StopAsync().AsTask();
        try
        {
            await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            // The native peer-stop acknowledgment closes output while signaling is pending.
            // Drive that boundary directly without starting a browser in the unit test.
            session.CompleteLocalMediaOutput();
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            await using var output = session.ReadSpeakerOutputAsync().GetAsyncEnumerator();
            Assert.False(await output.MoveNextAsync());
            Assert.False(stop.IsCompleted);
        }
        finally
        {
            releaseStop.TrySetResult();
            await stop;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeDotTrimsOnlyLeadingExactSilenceAndPreservesQuietSpeechAndLaterPauses(bool trim)
    {
        await using var session = new WebView2VoiceDuplexAudioSession(new ImmediateMediaDispatcher(),
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            (_, _) => Task.FromResult(string.Empty), () => { }, Task.CompletedTask,
            Task.Delay(Timeout.InfiniteTimeSpan), _ => Task.CompletedTask, () => ValueTask.CompletedTask,
            conversationSpeakerGain: 1, trimInitialSpeakerSilence: trim);
        var silence = new byte[960];
        var quietSpeech = new byte[960];
        quietSpeech[0] = 1; // Any content is retained; there is no speech-volume threshold.
        for (var sequence = 0; sequence < 3; sequence++)
            session.HandleWebMessage(System.Text.Json.JsonSerializer.Serialize(new
                { type = "speaker", sequence, pcm = Convert.ToBase64String(silence) }));
        session.HandleWebMessage(System.Text.Json.JsonSerializer.Serialize(new
            { type = "speaker", sequence = 3, pcm = Convert.ToBase64String(quietSpeech) }));
        session.HandleWebMessage(System.Text.Json.JsonSerializer.Serialize(new
            { type = "speaker", sequence = 4, pcm = Convert.ToBase64String(silence) }));
        await using var reader = session.ReadSpeakerOutputAsync().GetAsyncEnumerator();
        if (!trim)
            for (var sequence = 0; sequence < 3; sequence++)
            {
                Assert.True(await reader.MoveNextAsync());
                Assert.Equal(sequence, reader.Current.Frame!.Value.SequenceNumber);
            }
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(3, reader.Current.Frame!.Value.SequenceNumber);
        Assert.Equal(1, reader.Current.Frame!.Value.Payload.Span[0]);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(4, reader.Current.Frame!.Value.SequenceNumber);
    }

    [Fact]
    public async Task SpokenHangupWaitsForCompleteTranscriptAndStopsOnlyOnce()
    {
        var stopCalls = 0;
        await using var session = new WebView2VoiceDuplexAudioSession(
            new ImmediateMediaDispatcher(),
            Path.Combine(Path.GetTempPath(), "joydex-voice-tests", Guid.NewGuid().ToString("N")),
            (_, _) => Task.FromResult(string.Empty),
            () => { },
            Task.CompletedTask,
            Task.Delay(Timeout.InfiniteTimeSpan),
            _ => { stopCalls++; return Task.CompletedTask; },
            () => ValueTask.CompletedTask,
            conversationSpeakerGain: 1);

        session.HandleWebMessage("""{"type":"user-transcript-added","transcript":"Don't"}""");
        session.HandleWebMessage("""{"type":"user-transcript-added","transcript":"hang up"}""");
        Assert.Equal(0, stopCalls);
        session.HandleWebMessage("""{"type":"user-turn-done","transcript":"Don't hang up"}""");
        Assert.Equal(0, stopCalls);

        session.HandleWebMessage("""{"type":"user-transcript-added","transcript":"Hang up"}""");
        Assert.Equal(0, stopCalls);

        session.HandleWebMessage("""{"type":"user-turn-done","transcript":"Hang up"}""");
        Assert.Equal(1, stopCalls);
        session.HandleWebMessage("""{"type":"user-turn-done","transcript":"Hang up"}""");
        Assert.Equal(1, stopCalls);
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CanceledStopWaitDoesNotCancelTheSharedStopOrLaterDisposal()
    {
        var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopCalls = 0;
        var disposeCalls = 0;
        var session = new WebView2VoiceDuplexAudioSession(
            new ImmediateMediaDispatcher(),
            Path.Combine(Path.GetTempPath(), "joydex-voice-tests", Guid.NewGuid().ToString("N")),
            (_, _) => Task.FromResult(string.Empty),
            () => { },
            Task.CompletedTask,
            Task.Delay(Timeout.InfiniteTimeSpan),
            async _ =>
            {
                Interlocked.Increment(ref stopCalls);
                stopEntered.TrySetResult();
                await releaseStop.Task;
            },
            () =>
            {
                Interlocked.Increment(ref disposeCalls);
                return ValueTask.CompletedTask;
            },
            conversationSpeakerGain: 1);

        try
        {
            using var cancellation = new CancellationTokenSource();
            var canceledWait = session.StopAsync(cancellation.Token).AsTask();
            await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWait);
            Assert.False(session.Completion.IsCompleted);

            releaseStop.TrySetResult();
            await session.StopAsync();
            Assert.True(session.Completion.IsCompletedSuccessfully);
            Assert.Equal(1, Volatile.Read(ref stopCalls));

            await session.DisposeAsync();
            Assert.Equal(1, Volatile.Read(ref disposeCalls));
        }
        finally
        {
            releaseStop.TrySetResult();
            await session.DisposeAsync();
        }
    }

    private sealed class ImmediateMediaDispatcher : IVoiceMediaDispatcher
    {
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Completion => _completion.Task;

        public bool CheckAccess() => true;

        public Task InvokeAsync(Func<Task> action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return action();
        }
    }
}
