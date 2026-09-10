using Joydex.Core.Voice;

namespace Joydex.Windows.Voice;

/// <summary>
/// Starts one Joydex-owned Realtime session and pumps room-device PCM in both directions.
/// </summary>
public sealed class DedicatedVoiceCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan SpeakerDrainTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SessionStopTimeout = TimeSpan.FromSeconds(20);
    private readonly Func<CancellationToken, Task<IVoiceDuplexAudioSession>> _mediaSessionFactory;
    private readonly Func<IVoicePeDuplexAudioTransport> _deviceTransportFactory;
    private readonly Action<string> _log;
    private readonly IReadOnlyList<VoicePcmFrame> _readyCue;
    private readonly Func<CancellationToken, Task>? _sessionListening;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _activeGate = new();
    private ActiveSession? _active;
    private int _disposed;

    public DedicatedVoiceCoordinator(
        Func<CancellationToken, Task<IVoiceDuplexAudioSession>> mediaSessionFactory,
        Func<IVoicePeDuplexAudioTransport> deviceTransportFactory,
        Action<string> log,
        IReadOnlyList<VoicePcmFrame>? readyCue = null,
        Func<CancellationToken, Task>? sessionListening = null)
    {
        _mediaSessionFactory = mediaSessionFactory ?? throw new ArgumentNullException(nameof(mediaSessionFactory));
        _deviceTransportFactory = deviceTransportFactory ?? throw new ArgumentNullException(nameof(deviceTransportFactory));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _readyCue = readyCue ?? [];
        _sessionListening = sessionListening;
    }

    public event Action? SessionEnded;

    public bool IsSessionActive
    {
        get
        {
            lock (_activeGate)
            {
                return _active is not null;
            }
        }
    }

    public async Task<VoiceSessionStartResult> StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        var startupToken = startupCancellation.Token;
        var gateAcquired = false;
        IVoiceDuplexAudioSession? media = null;
        IVoicePeDuplexAudioTransport? device = null;
        try
        {
            if (!await _startGate.WaitAsync(0, startupToken).ConfigureAwait(false))
            {
                return Result(VoiceSessionStartStatus.SessionActive, "BLOCKED Voice PE wake; a start is already in progress.");
            }

            gateAcquired = true;
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            lock (_activeGate)
            {
                if (_active is not null)
                {
                    return Result(VoiceSessionStartStatus.SessionActive, "BLOCKED Voice PE wake; the room Voice Session is already active.");
                }
            }

            media = await _mediaSessionFactory(startupToken).ConfigureAwait(false);
            startupToken.ThrowIfCancellationRequested();
            ValidateFormats(media);
            device = _deviceTransportFactory();
            ValidateFormats(device);
            await device.OpenAsync(startupToken).ConfigureAwait(false);
            startupToken.ThrowIfCancellationRequested();
            ValidateReadyCue(device);
            if (_sessionListening is not null)
            {
                await _sessionListening(startupToken).ConfigureAwait(false);
            }
            startupToken.ThrowIfCancellationRequested();

            var active = new ActiveSession(media, device, _readyCue);
            lock (_activeGate)
            {
                _active = active;
            }

            _ = RunSessionAsync(active);
            media = null;
            device = null;
            return Result(
                VoiceSessionStartStatus.Confirmed,
                "STARTED Joydex-owned Voice Session; dedicated task and Voice PE media are connected.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            if (device is not null)
            {
                await DisposeRejectedDeviceAsync(device).ConfigureAwait(false);
            }

            if (media is not null)
            {
                await DisposeRejectedMediaAsync(media).ConfigureAwait(false);
            }

            return Result(VoiceSessionStartStatus.Rejected, "BLOCKED Joydex-owned Voice Session; coordinator is stopping.");
        }
        catch (Exception exception)
        {
            if (device is not null)
            {
                await DisposeRejectedDeviceAsync(device).ConfigureAwait(false);
            }

            if (media is not null)
            {
                await DisposeRejectedMediaAsync(media).ConfigureAwait(false);
            }

            return Result(
                VoiceSessionStartStatus.Rejected,
                $"BLOCKED Joydex-owned Voice Session; error={exception.Message}");
        }
        finally
        {
            if (gateAcquired)
            {
                _startGate.Release();
            }
        }
    }

    public async Task StopActiveAsync(CancellationToken cancellationToken = default)
    {
        ActiveSession? active;
        lock (_activeGate)
        {
            active = _active;
        }

        if (active is null)
        {
            return;
        }

        await active.Media.StopAsync(cancellationToken).ConfigureAwait(false);
        await active.Completion.Task.WaitAsync(SessionStopTimeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Toggles microphone mute for the active room session while preserving its
    /// WebRTC timing and speaker downlink. Returns null when no session is active.
    /// </summary>
    public bool? ToggleMicrophoneMute()
    {
        bool muted;
        lock (_activeGate)
        {
            if (_active is null)
            {
                return null;
            }

            muted = Volatile.Read(ref _active.MicrophoneMuted) == 0;
            Volatile.Write(ref _active.MicrophoneMuted, muted ? 1 : 0);
        }

        _log($"Joydex Voice room microphone {(muted ? "muted" : "unmuted")}.");
        return muted;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        await _startGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await StopActiveAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _log($"Could not stop the active Joydex-owned Voice Session: {exception.Message}");
        }
        finally
        {
            _startGate.Release();
            GC.SuppressFinalize(this);
        }
    }

    private async Task RunSessionAsync(ActiveSession active)
    {
        Exception? failure = null;
        using var pumpCancellation = new CancellationTokenSource();
        var microphonePump = PumpMicrophoneAsync(active, pumpCancellation.Token);
        var speakerPump = PumpSpeakerOutputAsync(active, pumpCancellation.Token);
        try
        {
            var completed = await Task.WhenAny(
                    active.Media.Completion,
                    active.Device.Completion,
                    microphonePump,
                    speakerPump)
                .ConfigureAwait(false);
            await completed.ConfigureAwait(false);
            if (ReferenceEquals(completed, active.Media.Completion))
            {
                _log("Codex Realtime completed; draining all accepted speaker audio before closing the Voice PE connection.");
                await speakerPump.WaitAsync(SpeakerDrainTimeout).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (pumpCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
            _log($"Joydex Voice media bridge failed: {exception.Message}");
        }
        finally
        {
            pumpCancellation.Cancel();
            try
            {
                await active.Media.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }

            try
            {
                await active.Device.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }

            await ObservePumpAsync(microphonePump).ConfigureAwait(false);
            await ObservePumpAsync(speakerPump).ConfigureAwait(false);
            try
            {
                await active.Media.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
                _log($"Could not dispose the WebRTC media session: {exception.Message}");
            }

            try
            {
                await active.Device.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
                _log($"Could not dispose the Voice PE audio transport: {exception.Message}");
            }

            lock (_activeGate)
            {
                if (ReferenceEquals(_active, active))
                {
                    _active = null;
                }
            }

            _log(
                $"Joydex Voice bridge summary: microphoneFrames={Interlocked.Read(ref active.MicrophoneFrames)}, "
                + $"mutedMicrophoneFrames={Interlocked.Read(ref active.MutedMicrophoneFrames)}, "
                + $"speakerFrames={Interlocked.Read(ref active.SpeakerFrames)}, "
                + $"playbackEnded={Volatile.Read(ref active.PlaybackEnded)}, "
                + $"playbackCleared={Volatile.Read(ref active.PlaybackCleared)}.");

            if (failure is null)
            {
                _log("ENDED Joydex-owned Voice Session; Voice PE wake is ready.");
                active.Completion.TrySetResult();
            }
            else
            {
                active.Completion.TrySetException(failure);
            }

            try
            {
                SessionEnded?.Invoke();
            }
            catch (Exception exception)
            {
                _log($"Voice PE end-state callback failed: {exception.Message}");
            }
        }
    }

    private async Task PumpMicrophoneAsync(ActiveSession active, CancellationToken cancellationToken)
    {
        await foreach (var frame in active.Device.ReadMicrophoneFramesAsync(cancellationToken).ConfigureAwait(false))
        {
            var muted = Volatile.Read(ref active.MicrophoneMuted) != 0;
            var outbound = !muted
                ? frame
                : new VoicePcmFrame(frame.SequenceNumber, active.MutedMicrophonePayload);
            if (muted)
            {
                Interlocked.Increment(ref active.MutedMicrophoneFrames);
            }

            await active.Media.SendMicrophoneFrameAsync(outbound, cancellationToken).ConfigureAwait(false);
            if (Interlocked.Increment(ref active.MicrophoneFrames) == 1)
            {
                _log("Voice PE microphone uplink is flowing into Codex WebRTC.");
            }
        }
    }

    private void ValidateReadyCue(IVoicePeDuplexAudioTransport device)
    {
        if (_readyCue.Count == 0)
        {
            return;
        }

        var frameBytes = device.SpeakerFormat.GetByteCount(TimeSpan.FromMilliseconds(20));
        foreach (var frame in _readyCue)
        {
            frame.Validate(device.SpeakerFormat);
            if (frame.Payload.Length != frameBytes)
            {
                throw new InvalidDataException(
                    "The ready-to-speak cue must use exact 20 ms speaker frames.");
            }
        }
    }

    private async IAsyncEnumerable<VoiceSpeakerOutput> ReadSpeakerOutputWithReadyCueAsync(
        ActiveSession active,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (active.ReadyCue.Count > 0)
        {
            _log(
                $"Joydex is playing the ready-to-speak cue while Listening and room microphone forwarding are live; "
                + $"frames={active.ReadyCue.Count}.");
            foreach (var frame in active.ReadyCue)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return VoiceSpeakerOutput.Audio(frame);
            }

            yield return VoiceSpeakerOutput.Ended;
        }

        var responseOpen = false;
        await foreach (var output in active.Media
                           .ReadSpeakerOutputAsync(cancellationToken)
                           .ConfigureAwait(false))
        {
            responseOpen = output.Kind switch
            {
                VoiceSpeakerOutputKind.Audio => true,
                VoiceSpeakerOutputKind.Ended or VoiceSpeakerOutputKind.Cleared => false,
                _ => responseOpen,
            };
            yield return output;
        }

        if (responseOpen)
        {
            _log("Codex speaker output closed without a final boundary; draining the accepted tail as a completed response.");
            yield return VoiceSpeakerOutput.Ended;
        }
    }

    private async Task PumpSpeakerOutputAsync(ActiveSession active, CancellationToken cancellationToken)
    {
        var cueFramesSent = 0;
        var cueBoundaryPending = active.ReadyCue.Count > 0;

        void FrameSent()
        {
            if (cueFramesSent < active.ReadyCue.Count)
            {
                cueFramesSent++;
                if (cueFramesSent == 1)
                {
                    _log("Joydex ready-to-speak cue entered the Voice PE speaker lane.");
                }
                return;
            }

            if (Interlocked.Increment(ref active.SpeakerFrames) == 1)
            {
                _log("Codex speaker downlink is flowing into the Voice PE.");
            }
        }

        void PlaybackEnded()
        {
            if (cueBoundaryPending)
            {
                cueBoundaryPending = false;
                _log("Joydex ready-to-speak cue was fully queued for device playback.");
                return;
            }

            Interlocked.Increment(ref active.PlaybackEnded);
        }

        if (active.Device is IVoiceSpeakerOutputTransport scheduledTransport)
        {
            await scheduledTransport.RunSpeakerOutputAsync(
                    ReadSpeakerOutputWithReadyCueAsync(active, cancellationToken),
                    frameSent: FrameSent,
                    playbackEnded: PlaybackEnded,
                    playbackCleared: () => Interlocked.Increment(ref active.PlaybackCleared),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var playout = new VoiceSpeakerPlayoutBuffer(active.Device, _log);
        await playout.RunAsync(
                ReadSpeakerOutputWithReadyCueAsync(active, cancellationToken),
                frameSent: FrameSent,
                playbackEnded: PlaybackEnded,
                playbackCleared: () => Interlocked.Increment(ref active.PlaybackCleared),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task ObservePumpAsync(Task pump)
    {
        try
        {
            await pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
    }

    private static void ValidateFormats(IVoiceDuplexAudioSession media)
    {
        if (media.MicrophoneInputFormat != new VoicePcmFormat(16_000, 1)
            || media.SpeakerOutputFormat != new VoicePcmFormat(24_000, 1))
        {
            throw new InvalidDataException("The WebRTC peer does not expose the required 16 kHz microphone and 24 kHz speaker formats.");
        }
    }

    private static void ValidateFormats(IVoicePeDuplexAudioTransport device)
    {
        if (device.MicrophoneFormat != new VoicePcmFormat(16_000, 1)
            || device.SpeakerFormat != new VoicePcmFormat(24_000, 1))
        {
            throw new InvalidDataException("The Voice PE does not expose the required 16 kHz microphone and 24 kHz speaker formats.");
        }
    }

    private VoiceSessionStartResult Result(VoiceSessionStartStatus status, string message)
    {
        _log(message);
        return new VoiceSessionStartResult(status, message);
    }

    private async ValueTask DisposeRejectedDeviceAsync(IVoicePeDuplexAudioTransport device)
    {
        try
        {
            await device.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception cleanupException)
        {
            _log($"Could not dispose rejected Voice PE audio transport: {cleanupException.Message}");
        }
    }

    private async ValueTask DisposeRejectedMediaAsync(IVoiceDuplexAudioSession media)
    {
        try
        {
            await media.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception cleanupException)
        {
            _log($"Could not dispose rejected WebRTC media session: {cleanupException.Message}");
        }
    }

    private sealed class ActiveSession(
        IVoiceDuplexAudioSession media,
        IVoicePeDuplexAudioTransport device,
        IReadOnlyList<VoicePcmFrame> readyCue)
    {
        public IVoiceDuplexAudioSession Media { get; } = media;

        public IVoicePeDuplexAudioTransport Device { get; } = device;

        public IReadOnlyList<VoicePcmFrame> ReadyCue { get; } = readyCue;

        public ReadOnlyMemory<byte> MutedMicrophonePayload { get; } = new byte[
            media.MicrophoneInputFormat.GetByteCount(TimeSpan.FromMilliseconds(20))];

        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long MicrophoneFrames;

        public long MutedMicrophoneFrames;

        public int MicrophoneMuted;

        public long SpeakerFrames;

        public int PlaybackEnded;

        public int PlaybackCleared;
    }
}
