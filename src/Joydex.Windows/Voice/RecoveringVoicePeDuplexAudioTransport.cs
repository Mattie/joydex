using System.Net.WebSockets;
using System.Threading.Channels;
using Joydex.Core.Voice;

namespace Joydex.Windows.Voice;

/// <summary>
/// Keeps one logical room-device media transport alive across bounded Voice PE socket reconnects.
/// </summary>
public sealed class RecoveringVoicePeDuplexAudioTransport :
    IVoicePeDuplexAudioTransport,
    IVoiceSpeakerOutputTransport
{
    private static readonly TimeSpan OpenAttemptTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CleanupAttemptTimeout = TimeSpan.FromSeconds(5);
    // A fresh Sendspin player is allowed two three-second synchronization attempts.
    // Keep the outer lossless-send deadline beyond that protocol window so it does
    // not cancel a successful transition at the boundary.
    private static readonly TimeSpan DefaultSendAttemptTimeout = TimeSpan.FromSeconds(8);
    private const int MaximumSuccessfulRecoveriesPerSession = 2;
    private static readonly TimeSpan[] DefaultRecoveryDelays =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(750),
        TimeSpan.FromMilliseconds(1500),
    ];

    private readonly Func<IVoicePeDuplexAudioTransport> _transportFactory;
    private readonly Action<string> _log;
    private readonly IReadOnlyList<TimeSpan> _recoveryDelays;
    private readonly TimeSpan _sendAttemptTimeout;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly List<Task> _backgroundTasks = [];
    private readonly Channel<VoicePcmFrame> _microphoneFrames = Channel.CreateBounded<VoicePcmFrame>(
        new BoundedChannelOptions(20)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IVoicePeDuplexAudioTransport? _inner;
    private VoiceSpeakerPlayoutMode? _speakerPlayoutMode;
    private long _microphoneSequence;
    private int _recoveryCycles;
    private int _opened;
    private int _closing;
    private int _disposed;

    public RecoveringVoicePeDuplexAudioTransport(
        Func<IVoicePeDuplexAudioTransport> transportFactory,
        Action<string> log)
        : this(transportFactory, log, DefaultRecoveryDelays, DefaultSendAttemptTimeout)
    {
    }

    internal RecoveringVoicePeDuplexAudioTransport(
        Func<IVoicePeDuplexAudioTransport> transportFactory,
        Action<string> log,
        IReadOnlyList<TimeSpan> recoveryDelays)
        : this(transportFactory, log, recoveryDelays, DefaultSendAttemptTimeout)
    {
    }

    internal RecoveringVoicePeDuplexAudioTransport(
        Func<IVoicePeDuplexAudioTransport> transportFactory,
        Action<string> log,
        IReadOnlyList<TimeSpan> recoveryDelays,
        TimeSpan sendAttemptTimeout)
    {
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _recoveryDelays = recoveryDelays?.Count > 0
            ? recoveryDelays
            : throw new ArgumentException("At least one recovery attempt is required.", nameof(recoveryDelays));
        _sendAttemptTimeout = sendAttemptTimeout > TimeSpan.Zero
            ? sendAttemptTimeout
            : throw new ArgumentOutOfRangeException(nameof(sendAttemptTimeout));
    }

    public VoicePcmFormat MicrophoneFormat { get; } = new(16_000, 1);

    public VoicePcmFormat SpeakerFormat { get; } = new(24_000, 1);

    public Task Completion => _completion.Task;

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.CompareExchange(ref _opened, 1, 0) != 0)
        {
            throw new InvalidOperationException("The recovering Voice PE audio transport is already open.");
        }

        try
        {
            _ = await EnsureConnectedAsync(null, null, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Exchange(ref _opened, 0);
            throw;
        }
    }

    public IAsyncEnumerable<VoicePcmFrame> ReadMicrophoneFramesAsync(
        CancellationToken cancellationToken = default) =>
        _microphoneFrames.Reader.ReadAllAsync(cancellationToken);

    public ValueTask SendSpeakerFrameAsync(
        VoicePcmFrame frame,
        CancellationToken cancellationToken = default) =>
        SendSpeakerOperationAsync(
            (transport, token) => transport.SendSpeakerFrameAsync(frame, token),
            "speaker frame",
            cancellationToken);

    public ValueTask NotifySpeakerPlaybackEndedAsync(CancellationToken cancellationToken = default) =>
        SendSpeakerOperationAsync(
            static (transport, token) => transport.NotifySpeakerPlaybackEndedAsync(token),
            "playback boundary",
            cancellationToken);

    public ValueTask FlushSpeakerAsync(CancellationToken cancellationToken = default) =>
        SendSpeakerOperationAsync(
            static (transport, token) => transport.FlushSpeakerAsync(token),
            "playback flush",
            cancellationToken);

    public async Task RunSpeakerOutputAsync(
        IAsyncEnumerable<VoiceSpeakerOutput> source,
        Action frameSent,
        Action playbackEnded,
        Action playbackCleared,
        CancellationToken cancellationToken)
    {
        var playout = new VoiceSpeakerPlayoutBuffer(
            this,
            _log,
            mode: GetSpeakerPlayoutMode());
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
        await _recoveryGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            IVoicePeDuplexAudioTransport? current;
            lock (_stateGate)
            {
                current = _inner;
                _inner = null;
            }

            if (current is not null)
            {
                await CloseAndDisposeAsync(current).ConfigureAwait(false);
            }
        }
        finally
        {
            _recoveryGate.Release();
        }

        Task[] background;
        lock (_stateGate)
        {
            background = [.. _backgroundTasks];
        }
        foreach (var task in background)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        _microphoneFrames.Writer.TryComplete();
        _completion.TrySetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await CloseAsync(CancellationToken.None).ConfigureAwait(false);
        _recoveryGate.Dispose();
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    private async ValueTask SendWithRecoveryAsync(
        Func<IVoicePeDuplexAudioTransport, CancellationToken, ValueTask> send,
        string operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(send);
        var transport = await EnsureConnectedAsync(null, null, cancellationToken).ConfigureAwait(false);
        try
        {
            await SendAttemptAsync(send, transport, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested
                                          && !_lifetime.IsCancellationRequested
                                          && IsRecoverableSendFailure(exception))
        {
            _log($"Voice PE {operation} failed; attempting bounded audio-bridge recovery; error={exception.Message}");
            transport = await EnsureConnectedAsync(transport, exception, cancellationToken).ConfigureAwait(false);
            await SendAttemptAsync(send, transport, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask SendSpeakerOperationAsync(
        Func<IVoicePeDuplexAudioTransport, CancellationToken, ValueTask> send,
        string operation,
        CancellationToken cancellationToken)
    {
        if (!RequiresLosslessSpeakerFailure())
        {
            await SendWithRecoveryAsync(send, operation, cancellationToken).ConfigureAwait(false);
            return;
        }

        var transport = await EnsureConnectedAsync(null, null, cancellationToken).ConfigureAwait(false);
        try
        {
            await SendAttemptAsync(send, transport, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested
                                          && !_lifetime.IsCancellationRequested
                                          && IsRecoverableSendFailure(exception))
        {
            var terminal = new IOException(
                $"Voice PE {operation} failed during scheduled playback; accepted audio cannot be safely replayed.",
                exception);
            _log($"FAILED Voice PE scheduled {operation}; ending the Voice Session instead of losing audio; error={exception.Message}");
            Fail(terminal);
            throw terminal;
        }
    }

    private async ValueTask SendAttemptAsync(
        Func<IVoicePeDuplexAudioTransport, CancellationToken, ValueTask> send,
        IVoicePeDuplexAudioTransport transport,
        CancellationToken cancellationToken)
    {
        using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        attemptCancellation.CancelAfter(_sendAttemptTimeout);
        try
        {
            await send(transport, attemptCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested
                                                            && !_lifetime.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Voice PE audio send attempt exceeded {_sendAttemptTimeout.TotalMilliseconds:F0} ms.",
                exception);
        }
    }

    private static bool IsRecoverableSendFailure(Exception exception) =>
        exception is IOException or WebSocketException or OperationCanceledException or TimeoutException;

    private async Task<IVoicePeDuplexAudioTransport> EnsureConnectedAsync(
        IVoicePeDuplexAudioTransport? failedTransport,
        Exception? failure,
        CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        var token = linkedCancellation.Token;
        await _recoveryGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (Volatile.Read(ref _closing) != 0)
            {
                throw new OperationCanceledException("The Voice PE audio transport is closing.", token);
            }

            IVoicePeDuplexAudioTransport? current;
            lock (_stateGate)
            {
                current = _inner;
            }
            if (current is not null && !ReferenceEquals(current, failedTransport))
            {
                return current;
            }
            if (current is not null && failedTransport is null)
            {
                return current;
            }

            if (failedTransport is not null)
            {
                lock (_stateGate)
                {
                    if (ReferenceEquals(_inner, failedTransport))
                    {
                        _inner = null;
                    }
                }
                await CloseAndDisposeAsync(failedTransport).ConfigureAwait(false);
            }

            if (failure is not null
                && Interlocked.Increment(ref _recoveryCycles) > MaximumSuccessfulRecoveriesPerSession)
            {
                var exhausted = new IOException(
                    $"Voice PE audio bridge exceeded {MaximumSuccessfulRecoveriesPerSession} successful reconnects in one Voice Session.",
                    failure);
                Fail(exhausted);
                throw exhausted;
            }

            Exception? lastFailure = failure;
            for (var attempt = 0; attempt < _recoveryDelays.Count; attempt++)
            {
                var delay = failure is null && attempt == 0
                    ? TimeSpan.Zero
                    : _recoveryDelays[attempt];
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                }

                IVoicePeDuplexAudioTransport? candidate = null;
                try
                {
                    candidate = _transportFactory();
                    ValidateFormats(candidate);
                    using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                    attemptCancellation.CancelAfter(OpenAttemptTimeout);
                    await candidate.OpenAsync(attemptCancellation.Token).ConfigureAwait(false);
                    var speakerPlayoutMode = candidate is IVoiceSpeakerOutputTransport scheduled
                        ? scheduled.PlayoutMode
                        : VoiceSpeakerPlayoutMode.HostPaced;
                    lock (_stateGate)
                    {
                        if (_speakerPlayoutMode is { } expected
                            && expected != speakerPlayoutMode)
                        {
                            throw new InvalidDataException(
                                "A recovered Voice PE transport changed its speaker scheduling capability.");
                        }

                        _speakerPlayoutMode ??= speakerPlayoutMode;
                        _inner = candidate;
                    }
                    StartBackgroundTasks(candidate);
                    if (failure is not null)
                    {
                        _log($"RECONNECTED Voice PE audio bridge on attempt {attempt + 1}; the Codex Voice Session remained active.");
                    }
                    return candidate;
                }
                catch (Exception exception) when (!token.IsCancellationRequested)
                {
                    lastFailure = exception;
                    if (candidate is not null)
                    {
                        await CloseAndDisposeAsync(candidate).ConfigureAwait(false);
                    }
                    _log($"Voice PE audio-bridge recovery attempt {attempt + 1}/{_recoveryDelays.Count} failed; error={exception.Message}");
                }
            }

            var terminal = new IOException(
                $"Voice PE audio bridge did not recover after {_recoveryDelays.Count} attempts.",
                lastFailure);
            Fail(terminal);
            throw terminal;
        }
        finally
        {
            _recoveryGate.Release();
        }
    }

    private void StartBackgroundTasks(IVoicePeDuplexAudioTransport transport)
    {
        var microphone = PumpMicrophoneAsync(transport);
        var observer = ObserveInnerCompletionAsync(transport);
        lock (_stateGate)
        {
            _backgroundTasks.Add(microphone);
            _backgroundTasks.Add(observer);
        }
    }

    private async Task PumpMicrophoneAsync(IVoicePeDuplexAudioTransport transport)
    {
        try
        {
            await foreach (var frame in transport.ReadMicrophoneFramesAsync(_lifetime.Token).ConfigureAwait(false))
            {
                var sequence = Interlocked.Increment(ref _microphoneSequence) - 1;
                _microphoneFrames.Writer.TryWrite(new VoicePcmFrame(sequence, frame.Payload));
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            // The matching Completion observer owns recovery and preserves one retry path.
        }
    }

    private async Task ObserveInnerCompletionAsync(IVoicePeDuplexAudioTransport transport)
    {
        Exception failure;
        try
        {
            await transport.Completion.ConfigureAwait(false);
            failure = new IOException("The Voice PE audio socket closed while the Voice Session was active.");
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        if (_lifetime.IsCancellationRequested || Volatile.Read(ref _closing) != 0)
        {
            return;
        }

        if (RequiresLosslessSpeakerFailure())
        {
            var terminal = new IOException(
                "Voice PE scheduled audio connection ended; accepted audio cannot be safely replayed.",
                failure);
            _log("FAILED Voice PE scheduled audio connection; ending the Voice Session instead of risking missing audio.");
            Fail(terminal);
            return;
        }

        try
        {
            _ = await EnsureConnectedAsync(transport, failure, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private bool RequiresLosslessSpeakerFailure() =>
        GetSpeakerPlayoutMode() is VoiceSpeakerPlayoutMode.DeviceClockedContinuous
            or VoiceSpeakerPlayoutMode.DeviceClockedResponseScoped;

    private async Task CloseAndDisposeAsync(IVoicePeDuplexAudioTransport transport)
    {
        try
        {
            using var cleanupCancellation = new CancellationTokenSource(CleanupAttemptTimeout);
            await transport.CloseAsync(cleanupCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _log($"Voice PE audio transport did not close cleanly during recovery; error={exception.Message}");
        }

        try
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _log($"Voice PE audio transport did not dispose cleanly during recovery; error={exception.Message}");
        }
    }

    private void Fail(Exception exception)
    {
        if (Volatile.Read(ref _closing) != 0)
        {
            return;
        }

        _microphoneFrames.Writer.TryComplete(exception);
        _completion.TrySetException(exception);
        _lifetime.Cancel();
    }

    private void ValidateFormats(IVoicePeDuplexAudioTransport transport)
    {
        if (transport.MicrophoneFormat != MicrophoneFormat || transport.SpeakerFormat != SpeakerFormat)
        {
            throw new InvalidDataException(
                "The recovered Voice PE transport does not expose 16 kHz microphone and 24 kHz speaker PCM.");
        }
    }

    private VoiceSpeakerPlayoutMode GetSpeakerPlayoutMode()
    {
        lock (_stateGate)
        {
            if (_inner is null || _speakerPlayoutMode is null)
            {
                throw new InvalidOperationException(
                    "The recovering Voice PE transport must be opened before speaker playout starts.");
            }

            return _speakerPlayoutMode.Value;
        }
    }
}
