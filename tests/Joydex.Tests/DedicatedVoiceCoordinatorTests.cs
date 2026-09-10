using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Joydex.Core.Voice;
using Joydex.Windows.Voice;

namespace Joydex.Tests;

public sealed class DedicatedVoiceCoordinatorTests
{
    [Fact]
    public async Task ConfirmsOnlyAfterBothMediaLegsOpenAndRearmsAfterTypedClose()
    {
        var media = new FakeMediaSession();
        var device = new FakeDeviceTransport();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new DedicatedVoiceCoordinator(
            _ => Task.FromResult<IVoiceDuplexAudioSession>(media),
            () => device,
            _ => { });
        coordinator.SessionEnded += () => ended.TrySetResult();

        var result = await coordinator.StartAsync();

        Assert.Equal(VoiceSessionStartStatus.Confirmed, result.Status);
        Assert.True(device.Opened);

        media.Complete();
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(device.Closed);
    }

    [Fact]
    public async Task StartsListeningAndMicrophoneWhileReadyCuePlays()
    {
        var logs = new List<string>();
        var media = new FakeMediaSession();
        var device = new FakeDeviceTransport();
        var listening = false;
        var cue = new[]
        {
            new VoicePcmFrame(0, new byte[960]),
            new VoicePcmFrame(1, new byte[960]),
        };
        await using var coordinator = new DedicatedVoiceCoordinator(
            _ => Task.FromResult<IVoiceDuplexAudioSession>(media),
            () => device,
            logs.Add,
            cue,
            _ =>
            {
                listening = true;
                return Task.CompletedTask;
            });

        var result = await coordinator.StartAsync();

        Assert.Equal(VoiceSessionStartStatus.Confirmed, result.Status);
        await device.SpeakerFrameReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(listening);

        var microphone = new VoicePcmFrame(0, new byte[640]);
        device.SignalMicrophone(microphone);
        Assert.Equal(
            microphone,
            await media.MicrophoneFrameReceived.Task.WaitAsync(TimeSpan.FromSeconds(2)));

        await device.PlaybackEnded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(["audio", "audio", "ended"], device.OutputEvents);
        Assert.Contains(
            logs,
            message => message.Contains(
                "ready-to-speak cue entered the Voice PE speaker lane",
                StringComparison.Ordinal));
        media.Complete();
    }

    [Fact]
    public async Task RejectsDuplicateWakeWhileSessionIsActive()
    {
        var media = new FakeMediaSession();
        var device = new FakeDeviceTransport();
        await using var coordinator = new DedicatedVoiceCoordinator(
            _ => Task.FromResult<IVoiceDuplexAudioSession>(media),
            () => device,
            _ => { });

        Assert.Equal(VoiceSessionStartStatus.Confirmed, (await coordinator.StartAsync()).Status);
        Assert.Equal(VoiceSessionStartStatus.SessionActive, (await coordinator.StartAsync()).Status);

        media.Complete();
    }

    [Fact]
    public async Task FailsClosedWhenDeviceAudioIsUnavailable()
    {
        var media = new FakeMediaSession();
        var device = new FakeDeviceTransport { OpenFailure = new IOException("port 8765 unavailable") };
        await using var coordinator = new DedicatedVoiceCoordinator(
            _ => Task.FromResult<IVoiceDuplexAudioSession>(media),
            () => device,
            _ => { });

        var result = await coordinator.StartAsync();

        Assert.Equal(VoiceSessionStartStatus.Rejected, result.Status);
        Assert.Contains("8765", result.Message, StringComparison.Ordinal);
        Assert.True(media.Disposed);
    }

    [Fact]
    public async Task ForwardsTypedSpeakerLifecycleSignalsToTheDevice()
    {
        var media = new FakeMediaSession();
        var device = new FakeDeviceTransport();
        await using var coordinator = new DedicatedVoiceCoordinator(
            _ => Task.FromResult<IVoiceDuplexAudioSession>(media),
            () => device,
            _ => { });

        Assert.Equal(VoiceSessionStartStatus.Confirmed, (await coordinator.StartAsync()).Status);

        media.Signal(VoiceSpeakerOutput.Audio(new VoicePcmFrame(0, new byte[960])));
        await device.SpeakerFrameReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        media.Signal(VoiceSpeakerOutput.Ended);
        await device.PlaybackEnded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        media.Signal(VoiceSpeakerOutput.Cleared);
        await device.SpeakerFlushed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["audio", "ended", "cleared"], device.OutputEvents);

        media.Complete();
    }

    [Fact]
    public async Task LetsAClockedDeviceConsumeSpeakerOutputWithoutHostPacing()
    {
        var media = new FakeMediaSession();
        var device = new FakeScheduledDeviceTransport();
        await using var coordinator = new DedicatedVoiceCoordinator(
            _ => Task.FromResult<IVoiceDuplexAudioSession>(media),
            () => device,
            _ => { });

        Assert.Equal(VoiceSessionStartStatus.Confirmed, (await coordinator.StartAsync()).Status);

        media.Signal(VoiceSpeakerOutput.Audio(new VoicePcmFrame(0, new byte[960])));
        media.Signal(VoiceSpeakerOutput.Ended);
        media.Signal(VoiceSpeakerOutput.Cleared);
        await device.AllOutputReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["scheduled-audio", "scheduled-ended", "scheduled-cleared"], device.OutputEvents);
        Assert.False(device.LegacySpeakerMethodCalled);
        media.Complete();
    }

    [Fact]
    public async Task ProductionRecoveryDecoratorFailsCallScopedPlaybackInsteadOfLosingAcceptedAudio()
    {
        var logs = new List<string>();
        var media = new FakeMediaSession();
        var first = new FakeRecoverableScheduledDeviceTransport
        {
            SpeakerSendFailure = new IOException("send stalled"),
            PlayoutMode = VoiceSpeakerPlayoutMode.DeviceClockedContinuous,
        };
        var second = new FakeRecoverableScheduledDeviceTransport
        {
            PlayoutMode = VoiceSpeakerPlayoutMode.DeviceClockedContinuous,
        };
        var transports = new Queue<IVoicePeDuplexAudioTransport>([first, second]);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new DedicatedVoiceCoordinator(
            _ => Task.FromResult<IVoiceDuplexAudioSession>(media),
            () => new RecoveringVoicePeDuplexAudioTransport(
                () => transports.Dequeue(),
                logs.Add,
                [TimeSpan.Zero]),
            logs.Add);
        coordinator.SessionEnded += () => ended.TrySetResult();

        Assert.Equal(VoiceSessionStartStatus.Confirmed, (await coordinator.StartAsync()).Status);
        media.Signal(VoiceSpeakerOutput.Audio(new VoicePcmFrame(0, new byte[960])));
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(first.NativePumpDelegated);
        Assert.False(second.NativePumpDelegated);
        Assert.False(second.Opened);
        Assert.Empty(second.OutputEvents);
        Assert.Contains(
            logs,
            message => message.Contains(
                "speaker playout summary: mode=DeviceClockedContinuous",
                StringComparison.Ordinal));
        Assert.Contains(
            logs,
            message => message.Contains(
                "FAILED Voice PE scheduled speaker frame",
                StringComparison.Ordinal));
        media.Complete();
    }

    [Fact]
    public async Task RecoveryDecoratorRetainsHostPacingForAnOrdinaryPcmTransport()
    {
        var logs = new List<string>();
        var media = new FakeMediaSession();
        var device = new FakeDeviceTransport();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new DedicatedVoiceCoordinator(
            _ => Task.FromResult<IVoiceDuplexAudioSession>(media),
            () => new RecoveringVoicePeDuplexAudioTransport(
                () => device,
                logs.Add,
                [TimeSpan.Zero]),
            logs.Add);
        coordinator.SessionEnded += () => ended.TrySetResult();

        Assert.Equal(VoiceSessionStartStatus.Confirmed, (await coordinator.StartAsync()).Status);
        media.Signal(VoiceSpeakerOutput.Audio(new VoicePcmFrame(0, new byte[960])));
        await device.SpeakerFrameReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        media.Signal(VoiceSpeakerOutput.Ended);
        await device.PlaybackEnded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        media.Complete();
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Contains(
            logs,
            message => message.Contains("speaker playout summary: mode=HostPaced", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ForwardsDeviceMicrophoneFramesToRealtimeMedia()
    {
        var media = new FakeMediaSession();
        var device = new FakeDeviceTransport();
        await using var coordinator = new DedicatedVoiceCoordinator(
            _ => Task.FromResult<IVoiceDuplexAudioSession>(media),
            () => device,
            _ => { });
        Assert.Null(coordinator.ToggleMicrophoneMute());
        Assert.Equal(VoiceSessionStartStatus.Confirmed, (await coordinator.StartAsync()).Status);
        var expected = new VoicePcmFrame(7, Enumerable.Repeat((byte)0x5a, 640).ToArray());

        device.SignalMicrophone(expected);

        var actual = await media.MicrophoneFrameReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(expected.SequenceNumber, actual.SequenceNumber);
        Assert.Equal(expected.Payload, actual.Payload);
        media.Complete();
    }

    [Fact]
    public async Task MicrophoneMuteSendsTimedSilenceAndUnmuteRestoresRoomAudio()
    {
        var media = new FakeMediaSession();
        var device = new FakeDeviceTransport();
        await using var coordinator = new DedicatedVoiceCoordinator(
            _ => Task.FromResult<IVoiceDuplexAudioSession>(media),
            () => device,
            _ => { });
        Assert.Equal(VoiceSessionStartStatus.Confirmed, (await coordinator.StartAsync()).Status);

        Assert.True(coordinator.ToggleMicrophoneMute());
        device.SignalMicrophone(new VoicePcmFrame(0, Enumerable.Repeat((byte)0x5a, 640).ToArray()));
        var mutedFrame = await media.ReadMicrophoneFrameAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, mutedFrame.SequenceNumber);
        Assert.Equal(640, mutedFrame.Payload.Length);
        Assert.All(mutedFrame.Payload.ToArray(), value => Assert.Equal(0, value));

        Assert.False(coordinator.ToggleMicrophoneMute());
        var expected = new VoicePcmFrame(1, Enumerable.Repeat((byte)0x6b, 640).ToArray());
        device.SignalMicrophone(expected);
        var unmutedFrame = await media.ReadMicrophoneFrameAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(expected.Payload, unmutedFrame.Payload);

        media.Complete();
    }

    [Fact]
    public async Task ReportsFlowCountersWithoutInspectingAudioContent()
    {
        var logs = new List<string>();
        var media = new FakeMediaSession();
        var device = new FakeDeviceTransport();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new DedicatedVoiceCoordinator(
            _ => Task.FromResult<IVoiceDuplexAudioSession>(media),
            () => device,
            logs.Add);
        coordinator.SessionEnded += () => ended.TrySetResult();
        Assert.Equal(VoiceSessionStartStatus.Confirmed, (await coordinator.StartAsync()).Status);

        device.SignalMicrophone(new VoicePcmFrame(0, new byte[640]));
        await media.MicrophoneFrameReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        media.Signal(VoiceSpeakerOutput.Audio(new VoicePcmFrame(0, new byte[960])));
        await device.SpeakerFrameReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        media.Signal(VoiceSpeakerOutput.Ended);
        await device.PlaybackEnded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        media.Complete();
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Contains(logs, value => value.Contains("microphone uplink is flowing", StringComparison.Ordinal));
        Assert.Contains(logs, value => value.Contains("speaker downlink is flowing", StringComparison.Ordinal));
        Assert.Contains(
            logs,
            value => value.Contains(
                "microphoneFrames=1, mutedMicrophoneFrames=0, speakerFrames=1, playbackEnded=1, playbackCleared=0",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task DrainsAcceptedSpeakerFramesAfterMediaCompletionBeforeClosingTheDevice()
    {
        var media = new FakeMediaSession();
        var device = new FakeDeviceTransport { BlockSpeakerSend = true };
        var drainStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new DedicatedVoiceCoordinator(
            _ => Task.FromResult<IVoiceDuplexAudioSession>(media),
            () => device,
            message =>
            {
                if (message.Contains("draining all accepted speaker audio", StringComparison.Ordinal))
                {
                    drainStarted.TrySetResult();
                }
            });
        coordinator.SessionEnded += () => ended.TrySetResult();
        Assert.Equal(VoiceSessionStartStatus.Confirmed, (await coordinator.StartAsync()).Status);

        media.Signal(VoiceSpeakerOutput.Audio(new VoicePcmFrame(0, new byte[960])));
        media.Signal(VoiceSpeakerOutput.Ended);
        await device.SpeakerSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        media.Complete();
        await drainStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(device.Closed);
        device.ReleaseSpeakerSend.TrySetResult();
        await device.PlaybackEnded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(["audio", "ended"], device.OutputEvents);
        Assert.True(device.Closed);
    }

    [Fact]
    public async Task SynthesizesAPlaybackBoundaryWhenMediaCompletesAfterAudio()
    {
        var logs = new List<string>();
        var media = new FakeMediaSession();
        var device = new FakeDeviceTransport();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new DedicatedVoiceCoordinator(
            _ => Task.FromResult<IVoiceDuplexAudioSession>(media),
            () => device,
            logs.Add);
        coordinator.SessionEnded += () => ended.TrySetResult();
        Assert.Equal(VoiceSessionStartStatus.Confirmed, (await coordinator.StartAsync()).Status);

        media.Signal(VoiceSpeakerOutput.Audio(new VoicePcmFrame(0, new byte[960])));
        await device.SpeakerFrameReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        media.Complete();

        await device.PlaybackEnded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(["audio", "ended"], device.OutputEvents);
        Assert.Contains(
            logs,
            message => message.Contains("closed without a final boundary", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisposalCancelsAndJoinsAnInProgressDeviceOpen()
    {
        var media = new FakeMediaSession();
        var device = new FakeDeviceTransport { BlockOpen = true };
        var coordinator = new DedicatedVoiceCoordinator(
            _ => Task.FromResult<IVoiceDuplexAudioSession>(media),
            () => device,
            _ => { });

        var start = coordinator.StartAsync();
        await device.OpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await coordinator.DisposeAsync();
        var result = await start.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(VoiceSessionStartStatus.Rejected, result.Status);
        Assert.True(media.Disposed);
        Assert.True(device.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => coordinator.StartAsync());
    }

    [Fact]
    public async Task RecoversAClosedDeviceSocketWithoutEndingTheLogicalMediaTransport()
    {
        var logs = new List<string>();
        var first = new FakeDeviceTransport();
        var second = new FakeDeviceTransport();
        var transports = new Queue<IVoicePeDuplexAudioTransport>([first, second]);
        await using var recovering = new RecoveringVoicePeDuplexAudioTransport(
            () => transports.Dequeue(),
            logs.Add,
            [TimeSpan.Zero]);
        await recovering.OpenAsync();

        first.Fail(new IOException("socket vanished"));
        await second.OpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(recovering.Completion.IsCompleted);
        second.SignalMicrophone(new VoicePcmFrame(99, new byte[640]));
        await using var microphone = recovering.ReadMicrophoneFramesAsync().GetAsyncEnumerator();
        Assert.True(await microphone.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, microphone.Current.SequenceNumber);
        Assert.True(first.Disposed);
        Assert.Contains(logs, value => value.Contains("RECONNECTED Voice PE audio bridge", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RetriesAnInterruptedSpeakerFrameAfterRecoveringTheDeviceSocket()
    {
        var first = new FakeDeviceTransport { SpeakerSendFailure = new IOException("send stalled") };
        var second = new FakeDeviceTransport();
        var transports = new Queue<IVoicePeDuplexAudioTransport>([first, second]);
        await using var recovering = new RecoveringVoicePeDuplexAudioTransport(
            () => transports.Dequeue(),
            _ => { },
            [TimeSpan.Zero]);
        await recovering.OpenAsync();
        var frame = new VoicePcmFrame(7, new byte[960]);

        await recovering.SendSpeakerFrameAsync(frame);

        await second.SpeakerFrameReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(["audio"], second.OutputEvents);
        Assert.False(recovering.Completion.IsCompleted);
        Assert.True(first.Disposed);
    }

    [Fact]
    public async Task RecoversWhenASpeakerSendExceedsItsBoundedAttempt()
    {
        var first = new FakeDeviceTransport { BlockSpeakerSend = true };
        var second = new FakeDeviceTransport();
        var transports = new Queue<IVoicePeDuplexAudioTransport>([first, second]);
        await using var recovering = new RecoveringVoicePeDuplexAudioTransport(
            () => transports.Dequeue(),
            _ => { },
            [TimeSpan.Zero],
            TimeSpan.FromMilliseconds(50));
        await recovering.OpenAsync();

        await recovering.SendSpeakerFrameAsync(new VoicePcmFrame(7, new byte[960]));

        await second.SpeakerFrameReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(first.Disposed);
        Assert.False(recovering.Completion.IsCompleted);
    }

    [Fact]
    public async Task DoesNotReconnectForAnInvalidSpeakerFrame()
    {
        var logs = new List<string>();
        var first = new FakeDeviceTransport();
        var replacementCreated = false;
        await using var recovering = new RecoveringVoicePeDuplexAudioTransport(
            () =>
            {
                if (first.Opened)
                {
                    replacementCreated = true;
                    return new FakeDeviceTransport();
                }
                return first;
            },
            logs.Add,
            [TimeSpan.Zero]);
        await recovering.OpenAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await recovering.SendSpeakerFrameAsync(new VoicePcmFrame(7, ReadOnlyMemory<byte>.Empty)));

        Assert.False(replacementCreated);
        Assert.False(first.Disposed);
        Assert.False(recovering.Completion.IsCompleted);
        Assert.DoesNotContain(logs, value => value.Contains("RECONNECTED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RetriesAnInterruptedPlaybackBoundaryAfterRecoveringTheDeviceSocket()
    {
        var first = new FakeDeviceTransport { PlaybackEndedFailure = new IOException("socket closed") };
        var second = new FakeDeviceTransport();
        var transports = new Queue<IVoicePeDuplexAudioTransport>([first, second]);
        await using var recovering = new RecoveringVoicePeDuplexAudioTransport(
            () => transports.Dequeue(),
            _ => { },
            [TimeSpan.Zero]);
        await recovering.OpenAsync();

        await recovering.NotifySpeakerPlaybackEndedAsync();

        await second.PlaybackEnded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(["ended"], second.OutputEvents);
        Assert.False(recovering.Completion.IsCompleted);
        Assert.True(first.Disposed);
    }

    [Theory]
    [InlineData((int)VoiceSpeakerPlayoutMode.DeviceClockedResponseScoped)]
    [InlineData((int)VoiceSpeakerPlayoutMode.DeviceClockedContinuous)]
    public async Task ScheduledPlaybackFailsInsteadOfRetryingOnlyTheInterruptedFrame(
        int playoutModeValue)
    {
        var playoutMode = (VoiceSpeakerPlayoutMode)playoutModeValue;
        var logs = new List<string>();
        var first = new FakeRecoverableScheduledDeviceTransport
        {
            PlayoutMode = playoutMode,
            SpeakerSendFailure = new IOException("send stalled"),
        };
        var replacement = new FakeRecoverableScheduledDeviceTransport
        {
            PlayoutMode = playoutMode,
        };
        var factoryCalls = 0;
        await using var recovering = new RecoveringVoicePeDuplexAudioTransport(
            () => factoryCalls++ == 0 ? first : replacement,
            logs.Add,
            [TimeSpan.Zero]);
        await recovering.OpenAsync();

        var failure = await Assert.ThrowsAsync<IOException>(
            async () => await recovering.SendSpeakerFrameAsync(new VoicePcmFrame(7, new byte[960])));

        Assert.Contains("cannot be safely replayed", failure.Message, StringComparison.Ordinal);
        Assert.Equal(1, factoryCalls);
        Assert.False(replacement.Opened);
        Assert.DoesNotContain(logs, value => value.Contains("RECONNECTED", StringComparison.Ordinal));
        await Assert.ThrowsAsync<IOException>(
            () => recovering.Completion.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task ResponseScopedPlaybackFailsInsteadOfRetryingOnlyTheInterruptedBoundary()
    {
        var first = new FakeRecoverableScheduledDeviceTransport
        {
            PlayoutMode = VoiceSpeakerPlayoutMode.DeviceClockedResponseScoped,
            PlaybackEndedFailure = new IOException("socket closed"),
        };
        var replacement = new FakeRecoverableScheduledDeviceTransport
        {
            PlayoutMode = VoiceSpeakerPlayoutMode.DeviceClockedResponseScoped,
        };
        var factoryCalls = 0;
        await using var recovering = new RecoveringVoicePeDuplexAudioTransport(
            () => factoryCalls++ == 0 ? first : replacement,
            _ => { },
            [TimeSpan.Zero]);
        await recovering.OpenAsync();

        var failure = await Assert.ThrowsAsync<IOException>(
            async () => await recovering.NotifySpeakerPlaybackEndedAsync());

        Assert.Contains("cannot be safely replayed", failure.Message, StringComparison.Ordinal);
        Assert.Equal(1, factoryCalls);
        Assert.False(replacement.Opened);
        Assert.Empty(first.OutputEvents);
    }

    [Theory]
    [InlineData((int)VoiceSpeakerPlayoutMode.DeviceClockedResponseScoped)]
    [InlineData((int)VoiceSpeakerPlayoutMode.DeviceClockedContinuous)]
    public async Task ScheduledPlaybackFailsInsteadOfReconnectingAfterTheSocketCloses(
        int playoutModeValue)
    {
        var playoutMode = (VoiceSpeakerPlayoutMode)playoutModeValue;
        var first = new FakeRecoverableScheduledDeviceTransport
        {
            PlayoutMode = playoutMode,
        };
        var replacement = new FakeRecoverableScheduledDeviceTransport
        {
            PlayoutMode = playoutMode,
        };
        var factoryCalls = 0;
        await using var recovering = new RecoveringVoicePeDuplexAudioTransport(
            () => factoryCalls++ == 0 ? first : replacement,
            _ => { },
            [TimeSpan.Zero]);
        await recovering.OpenAsync();

        first.Fail(new IOException("socket vanished"));

        var failure = await Assert.ThrowsAsync<IOException>(
            () => recovering.Completion.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Contains("cannot be safely replayed", failure.Message, StringComparison.Ordinal);
        Assert.Equal(1, factoryCalls);
        Assert.False(replacement.Opened);
    }

    [Fact]
    public async Task FaultsTheLogicalTransportAfterBoundedRecoveryIsExhausted()
    {
        var first = new FakeDeviceTransport();
        var failedRecoveryOne = new FakeDeviceTransport { OpenFailure = new IOException("still unavailable") };
        var failedRecoveryTwo = new FakeDeviceTransport { OpenFailure = new IOException("still unavailable") };
        var transports = new Queue<IVoicePeDuplexAudioTransport>([first, failedRecoveryOne, failedRecoveryTwo]);
        await using var recovering = new RecoveringVoicePeDuplexAudioTransport(
            () => transports.Dequeue(),
            _ => { },
            [TimeSpan.Zero, TimeSpan.Zero]);
        await recovering.OpenAsync();

        first.Fail(new IOException("socket vanished"));

        var failure = await Assert.ThrowsAsync<IOException>(
            () => recovering.Completion.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Contains("did not recover after 2 attempts", failure.Message, StringComparison.Ordinal);
        Assert.True(first.Disposed);
        Assert.True(failedRecoveryOne.Disposed);
        Assert.True(failedRecoveryTwo.Disposed);
    }

    [Fact]
    public async Task FaultsTheLogicalTransportAfterRepeatedSuccessfulReconnects()
    {
        var first = new FakeDeviceTransport();
        var second = new FakeDeviceTransport();
        var third = new FakeDeviceTransport();
        var transports = new Queue<IVoicePeDuplexAudioTransport>([first, second, third]);
        await using var recovering = new RecoveringVoicePeDuplexAudioTransport(
            () => transports.Dequeue(),
            _ => { },
            [TimeSpan.Zero]);
        await recovering.OpenAsync();

        first.Fail(new IOException("first disconnect"));
        await second.OpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        second.Fail(new IOException("second disconnect"));
        await third.OpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        third.Fail(new IOException("third disconnect"));

        var failure = await Assert.ThrowsAsync<IOException>(
            () => recovering.Completion.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Contains("exceeded 2 successful reconnects", failure.Message, StringComparison.Ordinal);
        Assert.True(first.Disposed);
        Assert.True(second.Disposed);
        Assert.True(third.Disposed);
        Assert.Empty(transports);
    }

    [Fact]
    public async Task RejectsARecoveredTransportWithDifferentSpeakerScheduling()
    {
        var first = new FakeRecoverableScheduledDeviceTransport();
        var incompatible = new FakeDeviceTransport();
        var transports = new Queue<IVoicePeDuplexAudioTransport>([first, incompatible]);
        await using var recovering = new RecoveringVoicePeDuplexAudioTransport(
            () => transports.Dequeue(),
            _ => { },
            [TimeSpan.Zero]);
        await recovering.OpenAsync();

        first.Fail(new IOException("socket vanished"));

        var failure = await Assert.ThrowsAsync<IOException>(
            () => recovering.Completion.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Contains("changed its speaker scheduling capability", failure.ToString(), StringComparison.Ordinal);
        Assert.True(first.Disposed);
        Assert.True(incompatible.Disposed);
    }

    [Fact]
    public void BuildsDeviceHostedAudioWebSocketOnDedicatedPort()
    {
        var uri = VoicePeLanAudioTransport.BuildSocketUri(new Uri("http://192.0.2.10/"));

        Assert.Equal("ws://192.0.2.10:8765/joydex/audio", uri.AbsoluteUri);
    }

    private sealed class FakeMediaSession : IVoiceDuplexAudioSession
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Channel<VoicePcmFrame> _microphoneInput = Channel.CreateUnbounded<VoicePcmFrame>();
        private readonly Channel<VoiceSpeakerOutput> _speakerOutput = Channel.CreateUnbounded<VoiceSpeakerOutput>();

        public VoicePcmFormat MicrophoneInputFormat { get; } = new(16_000, 1);

        public VoicePcmFormat SpeakerOutputFormat { get; } = new(24_000, 1);

        public Task Completion => _completion.Task;

        public bool Disposed { get; private set; }

        public TaskCompletionSource<VoicePcmFrame> MicrophoneFrameReceived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete()
        {
            _microphoneInput.Writer.TryComplete();
            _speakerOutput.Writer.TryComplete();
            _completion.TrySetResult();
        }

        public void Signal(VoiceSpeakerOutput output) => _speakerOutput.Writer.TryWrite(output);

        public ValueTask SendMicrophoneFrameAsync(
            VoicePcmFrame frame,
            CancellationToken cancellationToken = default)
        {
            MicrophoneFrameReceived.TrySetResult(frame);
            _microphoneInput.Writer.TryWrite(frame);
            return ValueTask.CompletedTask;
        }

        public ValueTask<VoicePcmFrame> ReadMicrophoneFrameAsync(
            CancellationToken cancellationToken = default) =>
            _microphoneInput.Reader.ReadAsync(cancellationToken);

        public IAsyncEnumerable<VoiceSpeakerOutput> ReadSpeakerOutputAsync(
            CancellationToken cancellationToken = default) =>
            _speakerOutput.Reader.ReadAllAsync(cancellationToken);

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            _completion.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _microphoneInput.Writer.TryComplete();
            _speakerOutput.Writer.TryComplete();
            _completion.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private class FakeDeviceTransport : IVoicePeDuplexAudioTransport
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Channel<VoicePcmFrame> _microphone = Channel.CreateUnbounded<VoicePcmFrame>();

        public VoicePcmFormat MicrophoneFormat { get; } = new(16_000, 1);

        public VoicePcmFormat SpeakerFormat { get; } = new(24_000, 1);

        public Task Completion => _completion.Task;

        public Exception? OpenFailure { get; init; }

        public Exception? SpeakerSendFailure { get; init; }

        public Exception? PlaybackEndedFailure { get; init; }

        public bool BlockOpen { get; init; }

        public bool BlockSpeakerSend { get; init; }

        public bool Opened { get; private set; }

        public bool Closed { get; private set; }

        public bool Disposed { get; private set; }

        public List<string> OutputEvents { get; } = [];

        public TaskCompletionSource OpenStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SpeakerFrameReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SpeakerSendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseSpeakerSend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource PlaybackEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SpeakerFlushed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void SignalMicrophone(VoicePcmFrame frame) => _microphone.Writer.TryWrite(frame);

        public void Fail(Exception exception)
        {
            _microphone.Writer.TryComplete(exception);
            _completion.TrySetException(exception);
        }

        public async Task OpenAsync(CancellationToken cancellationToken = default)
        {
            OpenStarted.TrySetResult();
            if (OpenFailure is not null)
            {
                throw OpenFailure;
            }

            Opened = true;
            if (BlockOpen)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }

        public IAsyncEnumerable<VoicePcmFrame> ReadMicrophoneFramesAsync(
            CancellationToken cancellationToken = default) =>
            _microphone.Reader.ReadAllAsync(cancellationToken);

        public async ValueTask SendSpeakerFrameAsync(
            VoicePcmFrame frame,
            CancellationToken cancellationToken = default)
        {
            frame.Validate(SpeakerFormat);
            if (frame.Payload.Length != 960)
            {
                throw new ArgumentException("Expected one 20 ms speaker frame.", nameof(frame));
            }
            if (SpeakerSendFailure is not null)
            {
                throw SpeakerSendFailure;
            }
            if (BlockSpeakerSend)
            {
                SpeakerSendStarted.TrySetResult();
                await ReleaseSpeakerSend.Task.WaitAsync(cancellationToken);
            }

            OutputEvents.Add("audio");
            SpeakerFrameReceived.TrySetResult();
        }

        public ValueTask NotifySpeakerPlaybackEndedAsync(CancellationToken cancellationToken = default)
        {
            if (PlaybackEndedFailure is not null)
            {
                return ValueTask.FromException(PlaybackEndedFailure);
            }

            OutputEvents.Add("ended");
            PlaybackEnded.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushSpeakerAsync(CancellationToken cancellationToken = default)
        {
            OutputEvents.Add("cleared");
            SpeakerFlushed.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public Task CloseAsync(CancellationToken cancellationToken = default)
        {
            Closed = true;
            _microphone.Writer.TryComplete();
            _completion.TrySetResult();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _microphone.Writer.TryComplete();
            _completion.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeScheduledDeviceTransport :
        IVoicePeDuplexAudioTransport,
        IVoiceSpeakerOutputTransport
    {
        private readonly FakeDeviceTransport _inner = new();

        public VoicePcmFormat MicrophoneFormat => _inner.MicrophoneFormat;

        public VoicePcmFormat SpeakerFormat => _inner.SpeakerFormat;

        public Task Completion => _inner.Completion;

        public bool LegacySpeakerMethodCalled { get; private set; }

        public List<string> OutputEvents { get; } = [];

        public TaskCompletionSource AllOutputReceived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task OpenAsync(CancellationToken cancellationToken = default) =>
            _inner.OpenAsync(cancellationToken);

        public IAsyncEnumerable<VoicePcmFrame> ReadMicrophoneFramesAsync(
            CancellationToken cancellationToken = default) =>
            _inner.ReadMicrophoneFramesAsync(cancellationToken);

        public ValueTask SendSpeakerFrameAsync(
            VoicePcmFrame frame,
            CancellationToken cancellationToken = default)
        {
            LegacySpeakerMethodCalled = true;
            return ValueTask.FromException(new InvalidOperationException("Host-paced speaker method was called."));
        }

        public ValueTask NotifySpeakerPlaybackEndedAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new InvalidOperationException("Host-paced playback boundary was called."));

        public ValueTask FlushSpeakerAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new InvalidOperationException("Host-paced clear was called."));

        public async Task RunSpeakerOutputAsync(
            IAsyncEnumerable<VoiceSpeakerOutput> source,
            Action frameSent,
            Action playbackEnded,
            Action playbackCleared,
            CancellationToken cancellationToken)
        {
            await foreach (var output in source.WithCancellation(cancellationToken))
            {
                switch (output.Kind)
                {
                    case VoiceSpeakerOutputKind.Audio:
                        OutputEvents.Add("scheduled-audio");
                        frameSent();
                        break;
                    case VoiceSpeakerOutputKind.Ended:
                        OutputEvents.Add("scheduled-ended");
                        playbackEnded();
                        break;
                    case VoiceSpeakerOutputKind.Cleared:
                        OutputEvents.Add("scheduled-cleared");
                        playbackCleared();
                        AllOutputReceived.TrySetResult();
                        break;
                }
            }
        }

        public Task CloseAsync(CancellationToken cancellationToken = default) =>
            _inner.CloseAsync(cancellationToken);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class FakeRecoverableScheduledDeviceTransport :
        FakeDeviceTransport,
        IVoiceSpeakerOutputTransport
    {
        public VoiceSpeakerPlayoutMode PlayoutMode { get; init; } = VoiceSpeakerPlayoutMode.DeviceClocked;

        public bool NativePumpDelegated { get; private set; }

        public Task RunSpeakerOutputAsync(
            IAsyncEnumerable<VoiceSpeakerOutput> source,
            Action frameSent,
            Action playbackEnded,
            Action playbackCleared,
            CancellationToken cancellationToken)
        {
            NativePumpDelegated = true;
            return Task.FromException(
                new InvalidOperationException("Recovery must retain ownership of the speaker output dispatcher."));
        }
    }
}
