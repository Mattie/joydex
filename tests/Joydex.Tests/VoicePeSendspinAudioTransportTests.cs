using System.Runtime.CompilerServices;
using Joydex.Core.Voice;
using Joydex.Windows.Voice;

namespace Joydex.Tests;

public sealed class VoicePeSendspinAudioTransportTests
{
    [Fact]
    public async Task OpenPrimesSendspinWithSilenceBeforeReportingReady()
    {
        var logs = new List<string>();
        var microphone = new RecordingMicrophoneTransport();
        var speaker = new RecordingSendspinSpeakerSession();
        await using var transport = new VoicePeSendspinAudioTransport(microphone, speaker, logs.Add);

        await transport.OpenAsync();

        var frame = Assert.Single(speaker.Frames);
        Assert.Equal(VoicePeSendspinProtocol.InputPcmBytesPerChunk, frame.Payload.Length);
        Assert.All(frame.Payload.ToArray(), value => Assert.Equal(0, value));
        Assert.Contains(logs, message => message.Contains("primed Sendspin speaker downlink", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlaybackEndPreservesTheCallScopedSendspinStream()
    {
        var microphone = new RecordingMicrophoneTransport();
        var speaker = new RecordingSendspinSpeakerSession();
        await using var transport = new VoicePeSendspinAudioTransport(microphone, speaker, _ => { });

        await transport.NotifySpeakerPlaybackEndedAsync();

        Assert.Equal(0, microphone.PlaybackEndedCount);
        Assert.Equal(0, microphone.FlushCount);
        Assert.Equal(0, speaker.FlushCount);
        Assert.Equal(0, speaker.PlaybackEndedCount);
    }

    [Fact]
    public async Task SpeakerFlushPreservesTheCallScopedSendspinStream()
    {
        var microphone = new RecordingMicrophoneTransport();
        var speaker = new RecordingSendspinSpeakerSession();
        await using var transport = new VoicePeSendspinAudioTransport(microphone, speaker, _ => { });

        await transport.FlushSpeakerAsync();

        Assert.Equal(0, speaker.FlushCount);
        Assert.Equal(0, microphone.FlushCount);
        Assert.Equal(0, microphone.PlaybackEndedCount);
    }

    [Fact]
    public async Task UsesContinuousDeviceClockedPlayoutForTheCall()
    {
        var microphone = new RecordingMicrophoneTransport();
        var speaker = new RecordingSendspinSpeakerSession();
        await using var transport = new VoicePeSendspinAudioTransport(microphone, speaker, _ => { });

        var scheduled = Assert.IsAssignableFrom<IVoiceSpeakerOutputTransport>(transport);

        Assert.Equal(VoiceSpeakerPlayoutMode.DeviceClockedContinuous, scheduled.PlayoutMode);
    }

    [Fact]
    public async Task SpeakerFramesReachOnlySendspin()
    {
        var microphone = new RecordingMicrophoneTransport();
        var speaker = new RecordingSendspinSpeakerSession();
        await using var transport = new VoicePeSendspinAudioTransport(microphone, speaker, _ => { });
        var frame = new VoicePcmFrame(1, new byte[960]);

        await transport.SendSpeakerFrameAsync(frame);

        Assert.Equal(1, speaker.FrameCount);
        Assert.Equal(0, microphone.SpeakerFrameCount);
    }

    private sealed class RecordingMicrophoneTransport : IVoicePeDuplexAudioTransport
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int SpeakerFrameCount { get; private set; }

        public int PlaybackEndedCount { get; private set; }

        public int FlushCount { get; private set; }

        public VoicePcmFormat MicrophoneFormat { get; } = new(16_000, 1);

        public VoicePcmFormat SpeakerFormat { get; } = new(24_000, 1);

        public Task Completion => _completion.Task;

        public Task OpenAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async IAsyncEnumerable<VoicePcmFrame> ReadMicrophoneFramesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask SendSpeakerFrameAsync(
            VoicePcmFrame frame,
            CancellationToken cancellationToken = default)
        {
            SpeakerFrameCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask NotifySpeakerPlaybackEndedAsync(CancellationToken cancellationToken = default)
        {
            PlaybackEndedCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushSpeakerAsync(CancellationToken cancellationToken = default)
        {
            FlushCount++;
            return ValueTask.CompletedTask;
        }

        public Task CloseAsync(CancellationToken cancellationToken = default)
        {
            _completion.TrySetResult();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _completion.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingSendspinSpeakerSession : IVoicePeSendspinSpeakerSession
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<VoicePcmFrame> Frames { get; } = [];

        public int FrameCount => Frames.Count;

        public int FlushCount { get; private set; }

        public int PlaybackEndedCount { get; private set; }

        public Task Completion => _completion.Task;

        public Task OpenAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask SendFrameAsync(VoicePcmFrame frame, CancellationToken cancellationToken)
        {
            Frames.Add(new VoicePcmFrame(frame.SequenceNumber, frame.Payload.ToArray()));
            return ValueTask.CompletedTask;
        }

        public ValueTask EndPlaybackAsync(CancellationToken cancellationToken)
        {
            PlaybackEndedCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken)
        {
            FlushCount++;
            return ValueTask.CompletedTask;
        }

        public Task CloseAsync(CancellationToken cancellationToken)
        {
            _completion.TrySetResult();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _completion.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
