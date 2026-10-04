using Joydex.App;
using Joydex.Windows.Voice;

namespace Joydex.Tests;

public sealed class WebView2VoiceDuplexAudioSessionLifetimeTests
{
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
