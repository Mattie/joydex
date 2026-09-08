using Joydex.Core.Voice;

namespace Joydex.Windows.Voice;

/// <summary>
/// Keeps the proven Joydex microphone/control lane and routes only speaker
/// output through the Voice PE's stock clocked Sendspin player.
/// </summary>
public sealed class VoicePeSendspinAudioTransport :
    IVoicePeDuplexAudioTransport,
    IVoiceSpeakerOutputTransport
{
    private static readonly VoicePcmFrame SpeakerPrimingFrame = new(
        0,
        new byte[VoicePeSendspinProtocol.InputPcmBytesPerChunk]);

    private readonly IVoicePeDuplexAudioTransport _microphoneTransport;
    private readonly IVoicePeSendspinSpeakerSession _speaker;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task? _completionObserver;
    private int _opened;
    private int _closing;
    private int _disposed;

    public VoicePeSendspinAudioTransport(
        Uri deviceEndpoint,
        Action<string> log)
        : this(
            new VoicePeLanAudioTransport(deviceEndpoint, log, requireUplinkOnly: true),
            new VoicePeSendspinSpeakerSession(deviceEndpoint, log),
            log)
    {
    }

    internal VoicePeSendspinAudioTransport(
        IVoicePeDuplexAudioTransport microphoneTransport,
        IVoicePeSendspinSpeakerSession speaker,
        Action<string> log)
    {
        _microphoneTransport = microphoneTransport ?? throw new ArgumentNullException(nameof(microphoneTransport));
        _speaker = speaker ?? throw new ArgumentNullException(nameof(speaker));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public VoicePcmFormat MicrophoneFormat => _microphoneTransport.MicrophoneFormat;

    public VoicePcmFormat SpeakerFormat { get; } = new(24_000, 1);

    public Task Completion => _completion.Task;

    VoiceSpeakerPlayoutMode IVoiceSpeakerOutputTransport.PlayoutMode =>
        VoiceSpeakerPlayoutMode.DeviceClockedContinuous;

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.CompareExchange(ref _opened, 1, 0) != 0)
        {
            throw new InvalidOperationException("The Voice PE hybrid audio transport is already open.");
        }

        try
        {
            await _microphoneTransport.OpenAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _speaker.OpenAsync(cancellationToken).ConfigureAwait(false);
                // Starting the Voice PE's Sendspin media source can take almost three seconds
                // while ESPHome allocates the decoder task and acquires the speaker pipeline.
                // Do that work before the coordinator publishes Listening so the first Codex
                // response cannot spend its entire bounded send attempt waiting for startup.
                await _speaker.SendFrameAsync(SpeakerPrimingFrame, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await _microphoneTransport.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            _completionObserver = ObserveLaneCompletionAsync();
            _log(
                "Voice PE hybrid audio is ready: dedicated microphone/control uplink plus primed Sendspin speaker downlink; "
                + "Joydex Audio Barge In policy remains ON; port 8765 does not gate independent Sendspin playback.");
        }
        catch
        {
            Interlocked.Exchange(ref _opened, 0);
            throw;
        }
    }

    public IAsyncEnumerable<VoicePcmFrame> ReadMicrophoneFramesAsync(
        CancellationToken cancellationToken = default) =>
        _microphoneTransport.ReadMicrophoneFramesAsync(cancellationToken);

    public ValueTask SendSpeakerFrameAsync(
        VoicePcmFrame frame,
        CancellationToken cancellationToken = default) =>
        _speaker.SendFrameAsync(frame, cancellationToken);

    public ValueTask NotifySpeakerPlaybackEndedAsync(CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public ValueTask FlushSpeakerAsync(CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public async Task RunSpeakerOutputAsync(
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

        var playout = new VoiceSpeakerPlayoutBuffer(
            this,
            _log,
            mode: VoiceSpeakerPlayoutMode.DeviceClockedContinuous);
        await playout.RunAsync(
                source,
                frameSent,
                playbackEnded,
                playbackCleared,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        Exception? failure = null;
        try
        {
            await _speaker.CloseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            await _microphoneTransport.CloseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        if (_completionObserver is not null)
        {
            try
            {
                await _completionObserver.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }

        if (failure is null)
        {
            _completion.TrySetResult();
        }
        else
        {
            _completion.TrySetException(failure);
            throw failure;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await _speaker.DisposeAsync().ConfigureAwait(false);
            await _microphoneTransport.DisposeAsync().ConfigureAwait(false);
            _lifetime.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    private async Task ObserveLaneCompletionAsync()
    {
        try
        {
            var completed = await Task.WhenAny(
                    _microphoneTransport.Completion,
                    _speaker.Completion,
                    Task.Delay(Timeout.InfiniteTimeSpan, _lifetime.Token))
                .ConfigureAwait(false);
            if (_lifetime.IsCancellationRequested)
            {
                return;
            }

            await completed.ConfigureAwait(false);
            throw new IOException(
                ReferenceEquals(completed, _speaker.Completion)
                    ? "Voice PE Sendspin speaker lane closed during the Voice Session."
                    : "Voice PE microphone lane closed during the Voice Session.");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _completion.TrySetException(exception);
        }
    }
}
