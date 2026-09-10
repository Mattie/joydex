using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Joydex.Core.Voice;

namespace Joydex.Windows.Voice;

/// <summary>
/// Bounds and serializes bursty WebView speaker output before it reaches the Voice PE.
/// </summary>
/// <remarks>
/// Host-paced transports wait at least 20 ms between frames. Device-clocked transports can use the
/// same cadence and fill source underruns with silence so their remote playback timeline cannot
/// drain during a host delivery stall. Continuous mode keeps that cadence and the same device
/// stream alive across assistant boundaries and ordered clears. Response-scoped mode stops at
/// each boundary. Burst-only device-clocked mode remains available for isolated transport
/// canaries. Every clear stays ordered behind all decoded frames already received.
/// </remarks>
internal sealed class VoiceSpeakerPlayoutBuffer
{
    internal const int MaximumQueuedFrames = 500;
    private static readonly TimeSpan FrameDuration = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan MaximumContinuousCatchupLag = TimeSpan.FromMilliseconds(15);

    private readonly IVoicePeDuplexAudioTransport _device;
    private readonly IVoiceSpeakerPlayoutClock _clock;
    private readonly VoiceSpeakerPlayoutMode _mode;
    private readonly Action? _clearQueued;
    private readonly Action<string> _log;
    private readonly byte[] _silencePayload;
    private readonly object _gate = new();
    private readonly Queue<VoiceSpeakerOutput> _pending = new();
    private readonly SemaphoreSlim _changed = new(0, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private bool _sourceCompleted;
    private Exception? _sourceFailure;
    private int _queuedFrames;
    private int _queueHighWater;
    private int _overflowFrames;
    private long _sentFrames;
    private int _silenceFrames;
    private int _underrunEvents;
    private int _currentUnderrunFrames;
    private int _maximumUnderrunFrames;
    private int _pacingRebases;
    private double _maximumPacingLatenessMilliseconds;
    private int _endedCount;
    private int _clearedCount;

    public VoiceSpeakerPlayoutBuffer(
        IVoicePeDuplexAudioTransport device,
        Action<string> log,
        IVoiceSpeakerPlayoutClock? clock = null,
        VoiceSpeakerPlayoutMode mode = VoiceSpeakerPlayoutMode.HostPaced,
        Action? clearQueued = null)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _clock = clock ?? StopwatchVoiceSpeakerPlayoutClock.Instance;
        _mode = mode;
        _clearQueued = clearQueued;
        _silencePayload = new byte[_device.SpeakerFormat.GetByteCount(FrameDuration)];
    }

    public async Task RunAsync(
        IAsyncEnumerable<VoiceSpeakerOutput> source,
        Action frameSent,
        Action playbackEnded,
        Action playbackCleared,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(frameSent);
        ArgumentNullException.ThrowIfNull(playbackEnded);
        ArgumentNullException.ThrowIfNull(playbackCleared);

        using var timerResolution = UsesHostCadence
                                    && _clock is StopwatchVoiceSpeakerPlayoutClock
            ? StopwatchVoiceSpeakerPlayoutClock.AcquireTimerResolution()
            : null;
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ingest = IngestAsync(source, runCancellation.Token);
        var playout = PlayAsync(frameSent, playbackEnded, playbackCleared, runCancellation.Token);
        try
        {
            var first = await Task.WhenAny(ingest, playout).ConfigureAwait(false);
            if (first.IsFaulted || first.IsCanceled)
            {
                runCancellation.Cancel();
            }

            await Task.WhenAll(ingest, playout).ConfigureAwait(false);
        }
        finally
        {
            runCancellation.Cancel();
            _log(
                $"Voice PE speaker playout summary: mode={_mode}, "
                + $"sentFrames={Interlocked.Read(ref _sentFrames)}, "
                + $"queueHighWater={Volatile.Read(ref _queueHighWater)}, "
                + $"overflowFrames={Volatile.Read(ref _overflowFrames)}, "
                + $"silenceFrames={Volatile.Read(ref _silenceFrames)}, "
                + $"underrunEvents={Volatile.Read(ref _underrunEvents)}, "
                + $"maximumUnderrunFrames={Volatile.Read(ref _maximumUnderrunFrames)}, "
                + $"pacingRebases={Volatile.Read(ref _pacingRebases)}, "
                + $"maximumPacingLatenessMs={_maximumPacingLatenessMilliseconds:F1}, "
                + $"playbackEnded={Volatile.Read(ref _endedCount)}, "
                + $"playbackCleared={Volatile.Read(ref _clearedCount)}.");
        }
    }

    private async Task IngestAsync(
        IAsyncEnumerable<VoiceSpeakerOutput> source,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var output in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                switch (output.Kind)
                {
                    case VoiceSpeakerOutputKind.Audio when output.Frame is { } frame:
                        EnqueueAudio(frame);
                        break;
                    case VoiceSpeakerOutputKind.Ended:
                        EnqueueBoundary(VoiceSpeakerOutput.Ended);
                        break;
                    case VoiceSpeakerOutputKind.Cleared:
                        EnqueueClear();
                        break;
                    default:
                        throw new InvalidDataException("The WebRTC peer produced an invalid speaker output item.");
                }
            }

            lock (_gate)
            {
                _sourceCompleted = true;
            }
            Pulse();
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _sourceFailure = exception;
                _sourceCompleted = true;
            }
            Pulse();
            throw;
        }
    }

    private void EnqueueAudio(VoicePcmFrame frame)
    {
        var payload = frame.Payload.ToArray();
        var copiedFrame = new VoicePcmFrame(frame.SequenceNumber, payload);
        var overflowed = false;
        lock (_gate)
        {
            if (_queuedFrames >= MaximumQueuedFrames)
            {
                _overflowFrames++;
                overflowed = true;
            }
            else
            {
                _pending.Enqueue(VoiceSpeakerOutput.Audio(copiedFrame));
                _queuedFrames++;
                _queueHighWater = Math.Max(_queueHighWater, _queuedFrames);
            }
        }

        if (overflowed && Volatile.Read(ref _overflowFrames) == 1)
        {
            _log($"Voice PE speaker playout queue reached its {MaximumQueuedFrames}-frame bound; the Voice Session will fail rather than lose assistant audio.");
        }
        Pulse();
        if (overflowed)
        {
            throw new InvalidOperationException(
                $"Voice PE speaker playout exceeded its {MaximumQueuedFrames}-frame safety bound.");
        }
    }

    private void EnqueueBoundary(VoiceSpeakerOutput boundary)
    {
        lock (_gate)
        {
            _pending.Enqueue(boundary);
        }
        Pulse();
    }

    private void EnqueueClear()
    {
        lock (_gate)
        {
            _pending.Enqueue(VoiceSpeakerOutput.Cleared);
        }
        _clearQueued?.Invoke();
        Pulse();
    }

    private async Task PlayAsync(
        Action frameSent,
        Action playbackEnded,
        Action playbackCleared,
        CancellationToken cancellationToken)
    {
        var nextAudioSend = 0L;
        var lastRealSequence = 0L;
        var playbackActive = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var shouldWaitForInput = false;
            TimeSpan delay = TimeSpan.Zero;
            lock (_gate)
            {
                if (_sourceFailure is not null)
                {
                    throw new InvalidOperationException("WebRTC speaker output ingestion failed.", _sourceFailure);
                }

                if (_pending.Count == 0)
                {
                    if (_sourceCompleted)
                    {
                        return;
                    }
                    if (FillsDeviceClockedUnderruns && playbackActive && nextAudioSend != 0)
                    {
                        delay = _clock.GetElapsedTime(_clock.GetTimestamp(), nextAudioSend);
                    }
                    else
                    {
                        shouldWaitForInput = true;
                    }
                }
                else
                {
                    var head = _pending.Peek();
                    if (UsesHostCadence
                        && head.Kind == VoiceSpeakerOutputKind.Audio
                        && nextAudioSend != 0)
                    {
                        delay = _clock.GetElapsedTime(_clock.GetTimestamp(), nextAudioSend);
                    }
                }
            }

            if (shouldWaitForInput)
            {
                await _changed.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (delay > TimeSpan.Zero)
            {
                await _clock.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                VoiceSpeakerOutput? action = null;
                var syntheticSilence = false;
                lock (_gate)
                {
                    if (_sourceFailure is not null)
                    {
                        throw new InvalidOperationException("WebRTC speaker output ingestion failed.", _sourceFailure);
                    }
                    if (_pending.Count > 0)
                    {
                        var head = _pending.Peek();
                        if (UsesHostCadence
                            && head.Kind == VoiceSpeakerOutputKind.Audio
                            && nextAudioSend != 0)
                        {
                            delay = _clock.GetElapsedTime(_clock.GetTimestamp(), nextAudioSend);
                        }
                        if (delay <= TimeSpan.Zero)
                        {
                            action = head.Kind == VoiceSpeakerOutputKind.Audio
                                ? DequeueAudioLocked()
                                : _pending.Dequeue();
                        }
                    }
                    else if (FillsDeviceClockedUnderruns
                             && playbackActive
                             && !_sourceCompleted
                             && nextAudioSend != 0)
                    {
                        delay = _clock.GetElapsedTime(_clock.GetTimestamp(), nextAudioSend);
                        if (delay <= TimeSpan.Zero)
                        {
                            action = VoiceSpeakerOutput.Audio(new VoicePcmFrame(lastRealSequence, _silencePayload));
                            syntheticSilence = true;
                        }
                    }
                }
                if (delay > TimeSpan.Zero)
                {
                    continue;
                }
                if (action is null)
                {
                    continue;
                }

                switch (action.Value.Kind)
                {
                    case VoiceSpeakerOutputKind.Audio when action.Value.Frame is { } frame:
                        var sendStarted = _clock.GetTimestamp();
                        await _device.SendSpeakerFrameAsync(frame, cancellationToken).ConfigureAwait(false);
                        nextAudioSend = ScheduleNextAudioSend(sendStarted, nextAudioSend);
                        Interlocked.Increment(ref _sentFrames);
                        playbackActive = true;
                        if (syntheticSilence)
                        {
                            RecordUnderrunFrame();
                        }
                        else
                        {
                            lastRealSequence = frame.SequenceNumber;
                            EndUnderrun();
                            frameSent();
                        }
                        break;
                    case VoiceSpeakerOutputKind.Ended:
                        await _device.NotifySpeakerPlaybackEndedAsync(cancellationToken).ConfigureAwait(false);
                        if (!KeepsDeviceClockedAliveBetweenResponses)
                        {
                            nextAudioSend = 0;
                            playbackActive = false;
                        }
                        EndUnderrun();
                        Interlocked.Increment(ref _endedCount);
                        playbackEnded();
                        break;
                    case VoiceSpeakerOutputKind.Cleared:
                        await _device.FlushSpeakerAsync(cancellationToken).ConfigureAwait(false);
                        if (!KeepsDeviceClockedAliveBetweenResponses)
                        {
                            nextAudioSend = 0;
                            playbackActive = false;
                        }
                        EndUnderrun();
                        Interlocked.Increment(ref _clearedCount);
                        playbackCleared();
                        break;
                }
            }
            finally
            {
                _sendGate.Release();
            }
        }
    }

    private bool UsesHostCadence =>
        _mode is VoiceSpeakerPlayoutMode.HostPaced
            or VoiceSpeakerPlayoutMode.DeviceClockedContinuous
            or VoiceSpeakerPlayoutMode.DeviceClockedResponseScoped;

    private bool FillsDeviceClockedUnderruns =>
        _mode is VoiceSpeakerPlayoutMode.DeviceClockedContinuous
            or VoiceSpeakerPlayoutMode.DeviceClockedResponseScoped;

    private bool KeepsDeviceClockedAliveBetweenResponses =>
        _mode == VoiceSpeakerPlayoutMode.DeviceClockedContinuous;

    private void RecordUnderrunFrame()
    {
        _silenceFrames++;
        if (_currentUnderrunFrames++ == 0)
        {
            _underrunEvents++;
        }
        _maximumUnderrunFrames = Math.Max(_maximumUnderrunFrames, _currentUnderrunFrames);
    }

    private void EndUnderrun() => _currentUnderrunFrames = 0;

    private long ScheduleNextAudioSend(long sendStarted, long previousSchedule)
    {
        if (!UsesHostCadence)
        {
            return 0;
        }
        if (!FillsDeviceClockedUnderruns || previousSchedule == 0)
        {
            return AddDuration(sendStarted, FrameDuration);
        }

        var lateness = _clock.GetElapsedTime(previousSchedule, sendStarted);
        if (lateness > TimeSpan.Zero)
        {
            _maximumPacingLatenessMilliseconds = Math.Max(
                _maximumPacingLatenessMilliseconds,
                lateness.TotalMilliseconds);
        }
        if (lateness > MaximumContinuousCatchupLag)
        {
            _pacingRebases++;
            return AddDuration(sendStarted, FrameDuration);
        }

        return AddDuration(previousSchedule, FrameDuration);
    }

    private VoiceSpeakerOutput DequeueAudioLocked()
    {
        var output = _pending.Dequeue();
        _queuedFrames--;
        return output;
    }

    private long AddDuration(long timestamp, TimeSpan duration) =>
        checked(timestamp + (long)Math.Round(duration.TotalSeconds * _clock.TimestampFrequency));

    private void Pulse()
    {
        try
        {
            _changed.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }
}

internal enum VoiceSpeakerPlayoutMode
{
    HostPaced,
    DeviceClocked,
    DeviceClockedContinuous,
    DeviceClockedResponseScoped,
}

internal interface IVoiceSpeakerPlayoutClock
{
    long TimestampFrequency { get; }

    long GetTimestamp();

    TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp);

    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class StopwatchVoiceSpeakerPlayoutClock : IVoiceSpeakerPlayoutClock
{
    private const uint TimerPeriodMilliseconds = 1;
    private const uint TimerNoError = 0;

    public static StopwatchVoiceSpeakerPlayoutClock Instance { get; } = new();

    public long TimestampFrequency => Stopwatch.Frequency;

    public long GetTimestamp() => Stopwatch.GetTimestamp();

    public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
        Stopwatch.GetElapsedTime(startingTimestamp, endingTimestamp);

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);

    public static IDisposable AcquireTimerResolution() => new TimerResolutionLease();

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod", ExactSpelling = true)]
    private static extern uint TimeBeginPeriod(uint periodMilliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod", ExactSpelling = true)]
    private static extern uint TimeEndPeriod(uint periodMilliseconds);

    private sealed class TimerResolutionLease : IDisposable
    {
        private readonly bool _active =
            OperatingSystem.IsWindows()
            && TimeBeginPeriod(TimerPeriodMilliseconds) == TimerNoError;

        public void Dispose()
        {
            if (_active)
            {
                _ = TimeEndPeriod(TimerPeriodMilliseconds);
            }
        }
    }
}
