using System.Runtime.CompilerServices;
using Joydex.Core.Voice;
using Joydex.Windows.Voice;

namespace Joydex.Tests;

public sealed class VoiceSpeakerPlayoutBufferTests
{
    [Fact]
    public async Task BurstyInputNeverSendsFramesLessThanTwentyMillisecondsApart()
    {
        var clock = new FakeClock();
        var device = new RecordingDevice(clock);
        var logs = new List<string>();
        var outputs = Enumerable.Range(0, 8)
            .Select(index => VoiceSpeakerOutput.Audio(Frame(index)))
            .Append(VoiceSpeakerOutput.Ended)
            .ToArray();
        var playout = new VoiceSpeakerPlayoutBuffer(device, logs.Add, clock);

        await playout.RunAsync(
            Stream(outputs),
            () => { },
            () => { },
            () => { },
            CancellationToken.None);

        Assert.Equal([0, 20, 40, 60, 80, 100, 120, 140], device.FrameTimesMilliseconds);
        Assert.Equal(
            ["audio:0", "audio:1", "audio:2", "audio:3", "audio:4", "audio:5", "audio:6", "audio:7", "ended"],
            device.Events);
        Assert.Contains(logs, message => message.Contains("queueHighWater=8", StringComparison.Ordinal));
        Assert.Contains(logs, message => message.Contains("overflowFrames=0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EndedDrainsAnUtteranceShorterThanThePrefill()
    {
        var clock = new FakeClock();
        var device = new RecordingDevice(clock);
        var outputs = new[]
        {
            VoiceSpeakerOutput.Audio(Frame(0)),
            VoiceSpeakerOutput.Audio(Frame(1)),
            VoiceSpeakerOutput.Audio(Frame(2)),
            VoiceSpeakerOutput.Ended,
        };
        var playout = new VoiceSpeakerPlayoutBuffer(device, _ => { }, clock);

        await playout.RunAsync(Stream(outputs), () => { }, () => { }, () => { }, CancellationToken.None);

        Assert.Equal(["audio:0", "audio:1", "audio:2", "ended"], device.Events);
        Assert.Equal([0, 20, 40], device.FrameTimesMilliseconds);
    }

    [Fact]
    public async Task ClearedPreservesEveryPreviouslyDecodedFrameBeforeFlushing()
    {
        var clock = new FakeClock();
        var device = new RecordingDevice(clock);
        var outputs = Enumerable.Range(0, 10)
            .Select(index => VoiceSpeakerOutput.Audio(Frame(index)))
            .Append(VoiceSpeakerOutput.Cleared)
            .ToArray();
        var playout = new VoiceSpeakerPlayoutBuffer(device, _ => { }, clock);

        await playout.RunAsync(Stream(outputs), () => { }, () => { }, () => { }, CancellationToken.None);

        Assert.Equal(
            ["audio:0", "audio:1", "audio:2", "audio:3", "audio:4", "audio:5", "audio:6", "audio:7", "audio:8", "audio:9", "cleared"],
            device.Events);
    }

    [Fact]
    public async Task CloseCancellationInterruptsAPendingPacingWait()
    {
        var clock = new BlockingClock();
        var device = new RecordingDevice(clock);
        var outputs = Enumerable.Range(0, 6)
            .Select(index => VoiceSpeakerOutput.Audio(Frame(index)))
            .ToArray();
        var playout = new VoiceSpeakerPlayoutBuffer(device, _ => { }, clock);
        using var cancellation = new CancellationTokenSource();

        var run = playout.RunAsync(Stream(outputs), () => { }, () => { }, () => { }, cancellation.Token);
        await clock.DelayStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Single(device.Events, value => value.StartsWith("audio:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CopiesPcmBeforeTheSourceReusesItsPayload()
    {
        var clock = new FakeClock();
        var device = new RecordingDevice(clock);
        var payload = Enumerable.Repeat((byte)0x2a, 960).ToArray();
        var playout = new VoiceSpeakerPlayoutBuffer(device, _ => { }, clock);

        await playout.RunAsync(
            ReusedPayloadStream(payload),
            () => { },
            () => { },
            () => { },
            CancellationToken.None);

        Assert.Equal((byte)0x2a, device.Payloads.Single()[0]);
        Assert.All(payload, value => Assert.Equal((byte)0xee, value));
    }

    [Fact]
    public async Task OversleepNeverCausesCatchUpFramesAtTheSameTimestamp()
    {
        var clock = new OversleepingClock(TimeSpan.FromMilliseconds(100));
        var device = new RecordingDevice(clock);
        var outputs = Enumerable.Range(0, 4)
            .Select(index => VoiceSpeakerOutput.Audio(Frame(index)))
            .Append(VoiceSpeakerOutput.Ended)
            .ToArray();
        var playout = new VoiceSpeakerPlayoutBuffer(device, _ => { }, clock);

        await playout.RunAsync(Stream(outputs), () => { }, () => { }, () => { }, CancellationToken.None);

        Assert.Equal([0, 120, 140, 160], device.FrameTimesMilliseconds);
        Assert.All(
            device.FrameTimesMilliseconds.Zip(device.FrameTimesMilliseconds.Skip(1)),
            pair => Assert.True(pair.Second - pair.First >= 20));
    }

    [Fact]
    public async Task DeviceClockedContinuousModeCorrectsSmallPacingOversleep()
    {
        var clock = new OversleepingClock(TimeSpan.FromMilliseconds(15));
        var device = new RecordingDevice(clock);
        var logs = new List<string>();
        var outputs = Enumerable.Range(0, 4)
            .Select(index => VoiceSpeakerOutput.Audio(Frame(index)))
            .Append(VoiceSpeakerOutput.Ended)
            .ToArray();
        var playout = new VoiceSpeakerPlayoutBuffer(
            device,
            logs.Add,
            clock,
            VoiceSpeakerPlayoutMode.DeviceClockedContinuous);

        await playout.RunAsync(Stream(outputs), () => { }, () => { }, () => { }, CancellationToken.None);

        Assert.Equal([0, 35, 40, 60], device.FrameTimesMilliseconds);
        Assert.Contains(
            logs,
            message => message.Contains(
                "pacingRebases=0, maximumPacingLatenessMs=15.0",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeviceClockedContinuousModeRebasesAfterALargeSchedulerPause()
    {
        var clock = new OversleepingClock(TimeSpan.FromMilliseconds(100));
        var device = new RecordingDevice(clock);
        var logs = new List<string>();
        var outputs = Enumerable.Range(0, 4)
            .Select(index => VoiceSpeakerOutput.Audio(Frame(index)))
            .Append(VoiceSpeakerOutput.Ended)
            .ToArray();
        var playout = new VoiceSpeakerPlayoutBuffer(
            device,
            logs.Add,
            clock,
            VoiceSpeakerPlayoutMode.DeviceClockedContinuous);

        await playout.RunAsync(Stream(outputs), () => { }, () => { }, () => { }, CancellationToken.None);

        Assert.Equal([0, 120, 140, 160], device.FrameTimesMilliseconds);
        Assert.Contains(
            logs,
            message => message.Contains(
                "pacingRebases=1, maximumPacingLatenessMs=100.0",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClearDuringCadenceStaysOrderedBehindThePeekedFrame()
    {
        var clock = new ControlledDelayClock();
        var device = new RecordingDevice(clock);
        var allowClear = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clearIngested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var playout = new VoiceSpeakerPlayoutBuffer(device, _ => { }, clock);

        var run = playout.RunAsync(
            ClearDuringDelayStream(allowClear.Task, clearIngested),
            () => { },
            () => { },
            () => { },
            CancellationToken.None);
        await clock.DelayStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        allowClear.TrySetResult();
        await clearIngested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        clock.ReleaseDelay.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["audio:0", "audio:1", "cleared"], device.Events);
    }

    [Fact]
    public async Task DeviceClockedModeFailsInsteadOfSilentlyDroppingOverflow()
    {
        var clock = new FakeClock();
        var device = new RecordingDevice(clock);
        var logs = new List<string>();
        var outputs = Enumerable.Range(0, VoiceSpeakerPlayoutBuffer.MaximumQueuedFrames + 100)
            .Select(index => VoiceSpeakerOutput.Audio(Frame(index)))
            .Append(VoiceSpeakerOutput.Ended)
            .ToArray();
        var playout = new VoiceSpeakerPlayoutBuffer(
            device,
            logs.Add,
            clock,
            VoiceSpeakerPlayoutMode.DeviceClocked);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => playout.RunAsync(
            Stream(outputs),
            () => { },
            () => { },
            () => { },
            CancellationToken.None));

        Assert.Contains(
            logs,
            message => message.Contains("rather than lose assistant audio", StringComparison.Ordinal));
        Assert.Contains("500-frame safety bound", failure.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            logs,
            message => message.Contains(
                $"queueHighWater={VoiceSpeakerPlayoutBuffer.MaximumQueuedFrames}, overflowFrames=1",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeviceClockedContinuousModeSmoothsBurstyInputToTwentyMillisecondCadence()
    {
        var clock = new FakeClock();
        var device = new RecordingDevice(clock);
        var logs = new List<string>();
        var outputs = Enumerable.Range(0, 8)
            .Select(index => VoiceSpeakerOutput.Audio(Frame(index)))
            .Append(VoiceSpeakerOutput.Ended)
            .ToArray();
        var playout = new VoiceSpeakerPlayoutBuffer(
            device,
            logs.Add,
            clock,
            VoiceSpeakerPlayoutMode.DeviceClockedContinuous);

        await playout.RunAsync(
            Stream(outputs),
            () => { },
            () => { },
            () => { },
            CancellationToken.None);

        Assert.Equal([0, 20, 40, 60, 80, 100, 120, 140], device.FrameTimesMilliseconds);
        Assert.Equal(
            ["audio:0", "audio:1", "audio:2", "audio:3", "audio:4", "audio:5", "audio:6", "audio:7", "ended"],
            device.Events);
        Assert.Equal(7, clock.DelayCallCount);
        Assert.Contains(
            logs,
            message => message.Contains(
                "mode=DeviceClockedContinuous, sentFrames=8, queueHighWater=8, overflowFrames=0, silenceFrames=0",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeviceClockedContinuousModeFillsAGapAfterPlaybackRestartsWithoutEndingTheSession()
    {
        var clock = new ManualClock();
        var device = new RecordingDevice(clock);
        var logs = new List<string>();
        var releaseLateAudio = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateAudioQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sourceHoldingSessionOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalPlaybackEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var realFramesSent = 0;
        var playbackEnds = 0;
        using var cancellation = new CancellationTokenSource();
        var playout = new VoiceSpeakerPlayoutBuffer(
            device,
            logs.Add,
            clock,
            VoiceSpeakerPlayoutMode.DeviceClockedContinuous);

        var run = playout.RunAsync(
            GapThenAudioStream(releaseLateAudio.Task, lateAudioQueued, sourceHoldingSessionOpen),
            () => realFramesSent++,
            () =>
            {
                if (Interlocked.Increment(ref playbackEnds) == 2)
                {
                    finalPlaybackEnded.TrySetResult();
                }
            },
            () => { },
            cancellation.Token);

        var firstCadenceDelay = await clock.TakeNextDelayAsync().WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(firstCadenceDelay);
        var secondCadenceDelay = await clock.TakeNextDelayAsync().WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(secondCadenceDelay);
        var thirdCadenceDelay = await clock.TakeNextDelayAsync().WaitAsync(TimeSpan.FromSeconds(2));
        releaseLateAudio.TrySetResult();
        await lateAudioQueued.Task.WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(thirdCadenceDelay);
        await finalPlaybackEnded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await sourceHoldingSessionOpen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal(["audio:0", "ended", "audio:1", "audio:1", "audio:2", "ended"], device.Events);
        Assert.Equal([0, 20, 40, 60], device.FrameTimesMilliseconds);
        Assert.All(device.Payloads[0], value => Assert.Equal((byte)0x11, value));
        Assert.All(device.Payloads[1], value => Assert.Equal((byte)0x22, value));
        Assert.All(device.Payloads[2], value => Assert.Equal((byte)0x00, value));
        Assert.All(device.Payloads[3], value => Assert.Equal((byte)0x33, value));
        Assert.Equal(3, realFramesSent);
        Assert.Contains(
            logs,
            message => message.Contains(
                "silenceFrames=1, underrunEvents=1, maximumUnderrunFrames=1",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeviceClockedContinuousModeKeepsSilenceFlowingBetweenAssistantTurns()
    {
        var clock = new ManualClock();
        var device = new RecordingDevice(clock);
        var logs = new List<string>();
        var releaseSecondResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondResponseQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sourceHoldingSessionOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalPlaybackEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var realFramesSent = 0;
        var playbackEnds = 0;
        using var cancellation = new CancellationTokenSource();
        var playout = new VoiceSpeakerPlayoutBuffer(
            device,
            logs.Add,
            clock,
            VoiceSpeakerPlayoutMode.DeviceClockedContinuous);

        var run = playout.RunAsync(
            IdleBetweenResponsesStream(releaseSecondResponse.Task, secondResponseQueued, sourceHoldingSessionOpen),
            () => realFramesSent++,
            () =>
            {
                if (Interlocked.Increment(ref playbackEnds) == 2)
                {
                    finalPlaybackEnded.TrySetResult();
                }
            },
            () => { },
            cancellation.Token);

        var firstIdleDelay = await clock.TakeNextDelayAsync().WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(firstIdleDelay);
        var secondIdleDelay = await clock.TakeNextDelayAsync().WaitAsync(TimeSpan.FromSeconds(2));
        releaseSecondResponse.TrySetResult();
        await secondResponseQueued.Task.WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(secondIdleDelay);
        await finalPlaybackEnded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await sourceHoldingSessionOpen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal(["audio:0", "ended", "audio:0", "audio:1", "ended"], device.Events);
        Assert.Equal([0, 20, 40], device.FrameTimesMilliseconds);
        Assert.All(device.Payloads[1], value => Assert.Equal((byte)0x00, value));
        Assert.Equal(2, realFramesSent);
        Assert.Contains(
            logs,
            message => message.Contains(
                "silenceFrames=1, underrunEvents=1, maximumUnderrunFrames=1",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeviceClockedContinuousModeKeepsSilenceFlowingAfterAnOrderedClear()
    {
        var clock = new ManualClock();
        var device = new RecordingDevice(clock);
        var releasePostClearAudio = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var postClearAudioQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sourceHoldingSessionOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalFrameSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var realFramesSent = 0;
        using var cancellation = new CancellationTokenSource();
        var playout = new VoiceSpeakerPlayoutBuffer(
            device,
            _ => { },
            clock,
            VoiceSpeakerPlayoutMode.DeviceClockedContinuous);

        var run = playout.RunAsync(
            ClearThenAudioStream(releasePostClearAudio.Task, postClearAudioQueued, sourceHoldingSessionOpen),
            () =>
            {
                if (Interlocked.Increment(ref realFramesSent) == 2)
                {
                    finalFrameSent.TrySetResult();
                }
            },
            () => { },
            () => { },
            cancellation.Token);

        var firstSilenceDelay = await clock.TakeNextDelayAsync().WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(firstSilenceDelay);
        var secondSilenceDelay = await clock.TakeNextDelayAsync().WaitAsync(TimeSpan.FromSeconds(2));
        releasePostClearAudio.TrySetResult();
        await postClearAudioQueued.Task.WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(secondSilenceDelay);
        await finalFrameSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await sourceHoldingSessionOpen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal(["audio:0", "cleared", "audio:0", "audio:1"], device.Events);
        Assert.Equal([0, 20, 40], device.FrameTimesMilliseconds);
        Assert.All(device.Payloads[1], value => Assert.Equal((byte)0x00, value));
    }

    [Fact]
    public async Task DeviceClockedResponseScopedModeStopsSilenceAtEachAssistantBoundary()
    {
        var clock = new ManualClock();
        var device = new RecordingDevice(clock);
        var logs = new List<string>();
        var releaseSecondResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondResponseQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sourceHoldingSessionOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstPlaybackEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalPlaybackEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var playbackEnds = 0;
        using var cancellation = new CancellationTokenSource();
        var playout = new VoiceSpeakerPlayoutBuffer(
            device,
            logs.Add,
            clock,
            VoiceSpeakerPlayoutMode.DeviceClockedResponseScoped);

        var run = playout.RunAsync(
            IdleBetweenResponsesStream(releaseSecondResponse.Task, secondResponseQueued, sourceHoldingSessionOpen),
            () => { },
            () =>
            {
                var ended = Interlocked.Increment(ref playbackEnds);
                if (ended == 1)
                {
                    firstPlaybackEnded.TrySetResult();
                }
                else if (ended == 2)
                {
                    finalPlaybackEnded.TrySetResult();
                }
            },
            () => { },
            cancellation.Token);

        await firstPlaybackEnded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releaseSecondResponse.TrySetResult();
        await secondResponseQueued.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await finalPlaybackEnded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await sourceHoldingSessionOpen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal(["audio:0", "ended", "audio:1", "ended"], device.Events);
        Assert.Equal([0, 0], device.FrameTimesMilliseconds);
        Assert.Contains(
            logs,
            message => message.Contains(
                "mode=DeviceClockedResponseScoped, sentFrames=2, queueHighWater=1, overflowFrames=0, silenceFrames=0",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeviceClockedClearPreservesQueuedFramesBeforePostClearAudio()
    {
        var clock = new FakeClock();
        var device = new BlockingFirstSendDevice(clock);
        var clearQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var playout = new VoiceSpeakerPlayoutBuffer(
            device,
            _ => { },
            clock,
            VoiceSpeakerPlayoutMode.DeviceClocked,
            () => clearQueued.TrySetResult());

        var run = playout.RunAsync(
            ClearDuringBlockedSendStream(device.FirstSendStarted.Task),
            () => { },
            () => { },
            () => { },
            CancellationToken.None);
        await clearQueued.Task.WaitAsync(TimeSpan.FromSeconds(2));
        device.ReleaseFirstSend.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["audio:0", "audio:1", "audio:2", "cleared", "audio:3", "ended"], device.Events);
        Assert.Equal(0, clock.DelayCallCount);
    }

    private static VoicePcmFrame Frame(long sequence) => new(sequence, new byte[960]);

    private static async IAsyncEnumerable<VoiceSpeakerOutput> Stream(
        IEnumerable<VoiceSpeakerOutput> outputs,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var output in outputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return output;
        }
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<VoiceSpeakerOutput> ReusedPayloadStream(
        byte[] payload,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return VoiceSpeakerOutput.Audio(new VoicePcmFrame(0, payload));
        Array.Fill(payload, (byte)0xee);
        yield return VoiceSpeakerOutput.Ended;
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<VoiceSpeakerOutput> ClearDuringDelayStream(
        Task allowClear,
        TaskCompletionSource clearIngested,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return VoiceSpeakerOutput.Audio(Frame(0));
        yield return VoiceSpeakerOutput.Audio(Frame(1));
        await allowClear.WaitAsync(cancellationToken);
        yield return VoiceSpeakerOutput.Cleared;
        clearIngested.TrySetResult();
    }

    private static async IAsyncEnumerable<VoiceSpeakerOutput> ClearDuringBlockedSendStream(
        Task firstSendStarted,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return VoiceSpeakerOutput.Audio(Frame(0));
        await firstSendStarted.WaitAsync(cancellationToken);
        yield return VoiceSpeakerOutput.Audio(Frame(1));
        yield return VoiceSpeakerOutput.Audio(Frame(2));
        yield return VoiceSpeakerOutput.Cleared;
        yield return VoiceSpeakerOutput.Audio(Frame(3));
        yield return VoiceSpeakerOutput.Ended;
    }

    private static async IAsyncEnumerable<VoiceSpeakerOutput> GapThenAudioStream(
        Task releaseLateAudio,
        TaskCompletionSource lateAudioQueued,
        TaskCompletionSource sourceHoldingSessionOpen,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return VoiceSpeakerOutput.Audio(FilledFrame(0, 0x11));
        yield return VoiceSpeakerOutput.Ended;
        yield return VoiceSpeakerOutput.Audio(FilledFrame(1, 0x22));
        await releaseLateAudio.WaitAsync(cancellationToken);
        yield return VoiceSpeakerOutput.Audio(FilledFrame(2, 0x33));
        lateAudioQueued.TrySetResult();
        yield return VoiceSpeakerOutput.Ended;
        sourceHoldingSessionOpen.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private static async IAsyncEnumerable<VoiceSpeakerOutput> IdleBetweenResponsesStream(
        Task releaseSecondResponse,
        TaskCompletionSource secondResponseQueued,
        TaskCompletionSource sourceHoldingSessionOpen,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return VoiceSpeakerOutput.Audio(FilledFrame(0, 0x11));
        yield return VoiceSpeakerOutput.Ended;
        await releaseSecondResponse.WaitAsync(cancellationToken);
        yield return VoiceSpeakerOutput.Audio(FilledFrame(1, 0x22));
        secondResponseQueued.TrySetResult();
        yield return VoiceSpeakerOutput.Ended;
        sourceHoldingSessionOpen.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private static async IAsyncEnumerable<VoiceSpeakerOutput> ClearThenAudioStream(
        Task releasePostClearAudio,
        TaskCompletionSource postClearAudioQueued,
        TaskCompletionSource sourceHoldingSessionOpen,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return VoiceSpeakerOutput.Audio(FilledFrame(0, 0x11));
        yield return VoiceSpeakerOutput.Cleared;
        await releasePostClearAudio.WaitAsync(cancellationToken);
        yield return VoiceSpeakerOutput.Audio(FilledFrame(1, 0x22));
        postClearAudioQueued.TrySetResult();
        sourceHoldingSessionOpen.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private static VoicePcmFrame FilledFrame(long sequence, byte value) =>
        new(sequence, Enumerable.Repeat(value, 960).ToArray());

    private class FakeClock : IVoiceSpeakerPlayoutClock
    {
        private long _nowTicks;

        public int DelayCallCount { get; private set; }

        public virtual long TimestampFrequency => TimeSpan.TicksPerSecond;

        public long GetTimestamp() => Volatile.Read(ref _nowTicks);

        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
            TimeSpan.FromTicks(endingTimestamp - startingTimestamp);

        public virtual Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DelayCallCount++;
            Interlocked.Add(ref _nowTicks, delay.Ticks);
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingClock : FakeClock
    {
        public TaskCompletionSource DelayStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            DelayStarted.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class OversleepingClock(TimeSpan oversleep) : FakeClock
    {
        private int _delays;

        public override Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var actualDelay = Interlocked.Increment(ref _delays) == 1
                ? delay + oversleep
                : delay;
            return base.DelayAsync(actualDelay, cancellationToken);
        }
    }

    private sealed class ControlledDelayClock : IVoiceSpeakerPlayoutClock
    {
        private long _nowTicks;

        public long TimestampFrequency => TimeSpan.TicksPerSecond;

        public TaskCompletionSource DelayStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseDelay { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long GetTimestamp() => Volatile.Read(ref _nowTicks);

        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
            TimeSpan.FromTicks(endingTimestamp - startingTimestamp);

        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            DelayStarted.TrySetResult();
            await ReleaseDelay.Task.WaitAsync(cancellationToken);
            Interlocked.Add(ref _nowTicks, delay.Ticks);
        }
    }

    private sealed class ManualClock : IVoiceSpeakerPlayoutClock
    {
        private readonly Queue<PendingDelay> _pending = new();
        private readonly SemaphoreSlim _available = new(0);
        private readonly object _gate = new();
        private long _nowTicks;

        public long TimestampFrequency => TimeSpan.TicksPerSecond;

        public long GetTimestamp() => Volatile.Read(ref _nowTicks);

        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
            TimeSpan.FromTicks(endingTimestamp - startingTimestamp);

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pending = new PendingDelay(
                delay,
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            lock (_gate)
            {
                _pending.Enqueue(pending);
            }
            _available.Release();
            return pending.Completion.Task.WaitAsync(cancellationToken);
        }

        public async Task<PendingDelay> TakeNextDelayAsync(CancellationToken cancellationToken = default)
        {
            await _available.WaitAsync(cancellationToken);
            lock (_gate)
            {
                return _pending.Dequeue();
            }
        }

        public void Advance(PendingDelay pending)
        {
            Interlocked.Add(ref _nowTicks, pending.Duration.Ticks);
            pending.Completion.TrySetResult();
        }

        public sealed record PendingDelay(TimeSpan Duration, TaskCompletionSource Completion);
    }

    private class RecordingDevice(IVoiceSpeakerPlayoutClock clock) : IVoicePeDuplexAudioTransport
    {
        public VoicePcmFormat MicrophoneFormat { get; } = new(16_000, 1);

        public VoicePcmFormat SpeakerFormat { get; } = new(24_000, 1);

        public Task Completion => Task.CompletedTask;

        public List<string> Events { get; } = [];

        public List<long> FrameTimesMilliseconds { get; } = [];

        public List<byte[]> Payloads { get; } = [];

        public Task OpenAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async IAsyncEnumerable<VoicePcmFrame> ReadMicrophoneFramesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public virtual ValueTask SendSpeakerFrameAsync(
            VoicePcmFrame frame,
            CancellationToken cancellationToken = default)
        {
            frame.Validate(SpeakerFormat);
            if (frame.Payload.Length != 960)
            {
                throw new ArgumentException("Expected one 20 ms speaker frame.", nameof(frame));
            }
            Events.Add($"audio:{frame.SequenceNumber}");
            FrameTimesMilliseconds.Add(clock.GetTimestamp() * 1000 / clock.TimestampFrequency);
            Payloads.Add(frame.Payload.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask NotifySpeakerPlaybackEndedAsync(CancellationToken cancellationToken = default)
        {
            Events.Add("ended");
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushSpeakerAsync(CancellationToken cancellationToken = default)
        {
            Events.Add("cleared");
            return ValueTask.CompletedTask;
        }

        public Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingFirstSendDevice(IVoiceSpeakerPlayoutClock clock) : RecordingDevice(clock)
    {
        private int _sendCount;

        public TaskCompletionSource FirstSendStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstSend { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask SendSpeakerFrameAsync(
            VoicePcmFrame frame,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _sendCount) == 1)
            {
                FirstSendStarted.TrySetResult();
                await ReleaseFirstSend.Task.WaitAsync(cancellationToken);
            }

            await base.SendSpeakerFrameAsync(frame, cancellationToken);
        }
    }
}
