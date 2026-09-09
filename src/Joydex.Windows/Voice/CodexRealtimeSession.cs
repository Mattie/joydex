using System.Text.Json;

namespace Joydex.Windows.Voice;

public sealed class CodexRealtimeSessionException(string message) : InvalidOperationException(message);

/// <summary>
/// One WebRTC Realtime conversation scoped to the task retained by a Dedicated Voice Task owner.
/// </summary>
public sealed class CodexRealtimeSession : IAsyncDisposable
{
    private const int MaximumSdpCharacters = 1024 * 1024;
    private static readonly TimeSpan FailedStartStopTimeout = TimeSpan.FromSeconds(5);
    private const string DesktopTaskMessagingInstructions =
        "A live tool named send_message_to_codex_task is available. Whenever the user asks to send, relay, submit, post, or pass a message to a Codex task, you must call that tool in the same turn. Always call it even if an earlier turn said the bridge was unavailable; the current tool result is the only authority. Do not claim delivery or unavailability without calling it. Omit target for this/current task, otherwise pass the spoken task name. Pass only the clean intended message and report the tool result accurately.";
    private readonly ICodexRealtimeControl _control;
    private readonly string? _voice;
    private readonly string? _realtimeStartInstructions;
    private readonly object _gate = new();
    private readonly TaskCompletionSource<string> _sdpAnswer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _stopTask;
    private bool _startRequested;
    private bool _active;
    private bool _typedStarted;
    private bool _mediaConnected;
    private bool _disposed;
    private string? _closeReason;

    internal CodexRealtimeSession(
        ICodexRealtimeControl control,
        string? voice = null,
        bool desktopTaskMessagingEnabled = false)
    {
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _voice = string.IsNullOrWhiteSpace(voice) ? null : voice.Trim().ToLowerInvariant();
        _realtimeStartInstructions = desktopTaskMessagingEnabled
            ? DesktopTaskMessagingInstructions
            : null;
        _control.NotificationReceived += OnNotification;
        _ = ObserveOwnerCompletionAsync();
    }

    public string ThreadId => _control.ThreadId;

    /// <summary>
    /// Completes only after both typed Realtime start and WebRTC media readiness are confirmed.
    /// </summary>
    public Task Ready => _ready.Task;

    /// <summary>
    /// Completes after a correlated typed close, or faults on Realtime/App Server failure.
    /// </summary>
    public Task Completion => _completion.Task;

    public string? CloseReason
    {
        get
        {
            lock (_gate)
            {
                return _closeReason;
            }
        }
    }

    public async Task<string> StartAsync(
        string sdpOffer,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sdpOffer);
        if (sdpOffer.Length > MaximumSdpCharacters)
        {
            throw new ArgumentException(
                $"The WebRTC SDP offer exceeds {MaximumSdpCharacters} characters.",
                nameof(sdpOffer));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_startRequested)
            {
                throw new InvalidOperationException("This Realtime session has already been started.");
            }

            _startRequested = true;
            _active = true;
        }

        var startAccepted = false;
        try
        {
            await _control.RequestAsync(
                "thread/realtime/start",
                new
                {
                    threadId = ThreadId,
                    realtimeSessionId = $"joydex-{Guid.NewGuid():N}",
                    version = "v3",
                    outputModality = "audio",
                    includeStartupContext = false,
                    realtimeStartInstructions = _realtimeStartInstructions,
                    voice = _voice,
                    transport = new
                    {
                        type = "webrtc",
                        sdp = sdpOffer,
                    },
                },
                TimeSpan.FromSeconds(30),
                cancellationToken).ConfigureAwait(false);
            startAccepted = true;

            return await _sdpAnswer.Task
                .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Fail(exception);
            if (startAccepted)
            {
                await TryStopFailedStartAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    private async Task TryStopFailedStartAsync()
    {
        try
        {
            await _control.RequestAsync(
                "thread/realtime/stop",
                new { threadId = ThreadId },
                FailedStartStopTimeout,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The original start failure remains authoritative; this stop is best-effort cleanup.
        }
    }

    public void MarkMediaConnected()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_startRequested || !_active)
            {
                throw new InvalidOperationException("No active Realtime session can accept media readiness.");
            }

            _mediaConnected = true;
            TryCompleteReadyLocked();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_startRequested || !_active || _completion.Task.IsCompleted)
            {
                return Task.CompletedTask;
            }

            return _stopTask ??= StopCoreAsync(cancellationToken);
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        await _control.RequestAsync(
            "thread/realtime/stop",
            new { threadId = ThreadId },
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);
        await _completion.Task
            .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ObserveOwnerCompletionAsync()
    {
        try
        {
            await _control.Completion.ConfigureAwait(false);
            bool sessionIncomplete;
            lock (_gate)
            {
                sessionIncomplete = _startRequested && !_completion.Task.IsCompleted;
            }

            if (sessionIncomplete)
            {
                Fail(new CodexRealtimeSessionException(
                    "Codex App Server closed while the Realtime session was active."));
            }
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private void OnNotification(string method, JsonElement parameters)
    {
        var notificationThreadId = parameters.ValueKind == JsonValueKind.Object
                                   && parameters.TryGetProperty("threadId", out var threadIdElement)
            ? threadIdElement.GetString()
            : null;
        if (!string.Equals(notificationThreadId, ThreadId, StringComparison.Ordinal))
        {
            return;
        }

        switch (method)
        {
            case "thread/realtime/started":
                lock (_gate)
                {
                    _typedStarted = true;
                    TryCompleteReadyLocked();
                }
                break;

            case "thread/realtime/sdp":
                if (parameters.TryGetProperty("sdp", out var sdpElement)
                    && sdpElement.GetString() is { Length: > 0 } sdp)
                {
                    _sdpAnswer.TrySetResult(sdp);
                }
                else
                {
                    Fail(new CodexRealtimeSessionException(
                        "Codex Realtime returned an empty SDP answer."));
                }

                break;

            case "thread/realtime/error":
                var message = parameters.TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString()
                    : null;
                Fail(new CodexRealtimeSessionException(message ?? "Unknown Codex Realtime error."));
                break;

            case "thread/realtime/closed":
                var reason = parameters.TryGetProperty("reason", out var reasonElement)
                             && reasonElement.ValueKind == JsonValueKind.String
                    ? reasonElement.GetString()
                    : null;
                CompleteClosed(reason);
                break;
        }
    }

    private void CompleteClosed(string? reason)
    {
        bool wasReady;
        lock (_gate)
        {
            if (_completion.Task.IsCompleted)
            {
                return;
            }

            _active = false;
            _closeReason = reason;
            wasReady = _typedStarted && _mediaConnected;
        }

        if (!wasReady)
        {
            var exception = new CodexRealtimeSessionException(
                "Codex Realtime closed before the session became ready.");
            _sdpAnswer.TrySetException(exception);
            _ready.TrySetException(exception);
        }

        _completion.TrySetResult();
    }

    private void Fail(Exception exception)
    {
        lock (_gate)
        {
            _active = false;
        }

        _sdpAnswer.TrySetException(exception);
        _ready.TrySetException(exception);
        _completion.TrySetException(exception);
    }

    private void TryCompleteReadyLocked()
    {
        if (_typedStarted && _mediaConnected)
        {
            _ready.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        bool stop;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            stop = _startRequested && _active && !_completion.Task.IsCompleted;
        }

        if (stop)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await StopAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        lock (_gate)
        {
            _disposed = true;
            _active = false;
        }

        _control.NotificationReceived -= OnNotification;
        if (!_startRequested)
        {
            _sdpAnswer.TrySetCanceled();
            _ready.TrySetCanceled();
            _completion.TrySetResult();
        }

        GC.SuppressFinalize(this);
    }
}
