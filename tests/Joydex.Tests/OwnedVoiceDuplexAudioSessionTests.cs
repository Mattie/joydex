using Joydex.App;
using Joydex.Core.Voice;
using Joydex.Windows.Voice;

namespace Joydex.Tests;

public sealed class OwnedVoiceDuplexAudioSessionTests
{
    [Fact]
    public async Task MediaDisposalReleasesTheTaskOwnerExactlyOnce()
    {
        var order = new List<string>();
        var media = new FakeMedia(order);
        var owner = new FakeOwner(order);
        var session = new OwnedVoiceDuplexAudioSession(media, owner);

        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.Equal(["media", "owner"], order);
        Assert.Equal(1, media.DisposeCount);
        Assert.Equal(1, owner.DisposeCount);
    }

    [Fact]
    public async Task MediaCleanupFailureStillReleasesTheTaskOwner()
    {
        var order = new List<string>();
        var media = new FakeMedia(order) { DisposeFailure = new InvalidOperationException("media failed") };
        var owner = new FakeOwner(order);
        var session = new OwnedVoiceDuplexAudioSession(media, owner);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.DisposeAsync().AsTask());

        Assert.Equal("media failed", exception.Message);
        Assert.Equal(["media", "owner"], order);
        Assert.Equal(1, owner.DisposeCount);
    }

    [Fact]
    public async Task OwnerCleanupFailureIsReportedAsAnOwnershipFailure()
    {
        var order = new List<string>();
        var media = new FakeMedia(order);
        var owner = new FakeOwner(order) { DisposeFailure = new InvalidOperationException("owner failed") };
        VoiceOwnershipCleanupException? reported = null;
        var session = new OwnedVoiceDuplexAudioSession(media, owner, failure => reported = failure);

        var exception = await Assert.ThrowsAsync<VoiceOwnershipCleanupException>(
            () => session.DisposeAsync().AsTask());

        Assert.Contains("could not confirm release", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Same(exception, reported);
        Assert.Equal(["media", "owner"], order);
    }

    private sealed class FakeMedia(List<string> order) : IVoiceDuplexAudioSession
    {
        public VoicePcmFormat MicrophoneInputFormat { get; } = new(16_000, 1);

        public VoicePcmFormat SpeakerOutputFormat { get; } = new(24_000, 1);

        public Task Completion => Task.CompletedTask;

        public Exception? DisposeFailure { get; init; }

        public int DisposeCount { get; private set; }

        public ValueTask SendMicrophoneFrameAsync(
            VoicePcmFrame frame,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<VoiceSpeakerOutput> ReadSpeakerOutputAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            order.Add("media");
            return DisposeFailure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(DisposeFailure);
        }
    }

    private sealed class FakeOwner(List<string> order) : IAsyncDisposable
    {
        public Exception? DisposeFailure { get; init; }

        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            order.Add("owner");
            return DisposeFailure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(DisposeFailure);
        }
    }
}
