using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Joydex.Core.Voice;

namespace Joydex.Windows.Voice;

/// <summary>
/// Connects outward to the Voice PE's trusted-LAN PCM WebSocket.
/// </summary>
public sealed class VoicePeLanAudioTransport : IVoicePeDuplexAudioTransport
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);
    private static readonly VoicePcmFormat DeviceMicrophoneFormat = new(16_000, 1);
    private static readonly VoicePcmFormat DeviceSpeakerFormat = new(24_000, 1);
    private static readonly int MicrophoneFrameBytes = DeviceMicrophoneFormat.GetByteCount(TimeSpan.FromMilliseconds(20));
    private static readonly int SpeakerFrameBytes = DeviceSpeakerFormat.GetByteCount(TimeSpan.FromMilliseconds(20));

    private readonly ClientWebSocket _socket;
    private readonly Uri _socketUri;
    private readonly Action<string>? _log;
    private readonly bool _requireUplinkOnly;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<VoicePcmFrame> _microphoneFrames = Channel.CreateBounded<VoicePcmFrame>(
        new BoundedChannelOptions(20)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
    private readonly TaskCompletionSource<bool> _hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _receiveTask;
    private long _microphoneSequence;
    private long _microphoneNonSilentFrames;
    private long _speakerFramesSent;
    private int _microphonePeakAbsolute;
    private int _opened;
    private int _disposed;

    public VoicePeLanAudioTransport(Uri deviceEndpoint, Action<string>? log = null)
        : this(new ClientWebSocket(), BuildSocketUri(deviceEndpoint), log, requireUplinkOnly: false)
    {
    }

    internal VoicePeLanAudioTransport(
        Uri deviceEndpoint,
        Action<string>? log,
        bool requireUplinkOnly)
        : this(new ClientWebSocket(), BuildSocketUri(deviceEndpoint), log, requireUplinkOnly)
    {
    }

    internal VoicePeLanAudioTransport(
        ClientWebSocket socket,
        Uri socketUri,
        Action<string>? log = null,
        bool requireUplinkOnly = false)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _socketUri = socketUri ?? throw new ArgumentNullException(nameof(socketUri));
        _log = log;
        _requireUplinkOnly = requireUplinkOnly;
        // The device continuously sends microphone frames while a session is
        // open, so an additional control-frame heartbeat is unnecessary. The
        // ESP-IDF WebSocket server has proven sensitive to both native and
        // application-level heartbeats during sustained PCM uplink.
        _socket.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
    }

    public VoicePcmFormat MicrophoneFormat => DeviceMicrophoneFormat;

    public VoicePcmFormat SpeakerFormat => DeviceSpeakerFormat;

    public Task Completion => _completion.Task;

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.CompareExchange(ref _opened, 1, 0) != 0)
        {
            throw new InvalidOperationException("The Voice PE audio session is already open.");
        }

        try
        {
            await _socket.ConnectAsync(_socketUri, cancellationToken).ConfigureAwait(false);
            _receiveTask = ReceiveAsync(_lifetime.Token);
            await _hello.Task.WaitAsync(HandshakeTimeout, cancellationToken).ConfigureAwait(false);
            await SendTextAsync(new { type = "open" }, cancellationToken).ConfigureAwait(false);
            await _ready.Task.WaitAsync(HandshakeTimeout, cancellationToken).ConfigureAwait(false);
            _log?.Invoke("Voice PE LAN audio handshake completed; microphone/control session is open.");
        }
        catch
        {
            Interlocked.Exchange(ref _opened, 0);
            throw;
        }
    }

    public async IAsyncEnumerable<VoicePcmFrame> ReadMicrophoneFramesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var frame in _microphoneFrames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return frame;
        }
    }

    public async ValueTask SendSpeakerFrameAsync(
        VoicePcmFrame frame,
        CancellationToken cancellationToken = default)
    {
        frame.Validate(SpeakerFormat);
        if (frame.Payload.Length != SpeakerFrameBytes)
        {
            throw new ArgumentException(
                $"Voice PE speaker frames must be exactly {SpeakerFrameBytes} bytes (20 ms).",
                nameof(frame));
        }

        if (_socket.State != WebSocketState.Open || Volatile.Read(ref _opened) == 0)
        {
            throw new IOException("The Voice PE audio socket is not open.");
        }

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                await _socket.SendAsync(
                        frame.Payload,
                        WebSocketMessageType.Binary,
                        endOfMessage: true,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException exception) when (_socket.State != WebSocketState.Open)
            {
                throw new IOException("The Voice PE audio socket closed during speaker output.", exception);
            }
            if (Interlocked.Increment(ref _speakerFramesSent) == 1)
            {
                _log?.Invoke("Joydex sent its first live Codex speaker frame to the Voice PE.");
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public ValueTask NotifySpeakerPlaybackEndedAsync(CancellationToken cancellationToken = default) =>
        SendControlAsync(new { type = "playback_end" }, cancellationToken);

    public ValueTask FlushSpeakerAsync(CancellationToken cancellationToken = default) =>
        SendControlAsync(new { type = "flush" }, cancellationToken);

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _opened, 0) == 0)
        {
            return;
        }

        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                await SendTextAsync(new { type = "close" }, cancellationToken).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(CloseTimeout);
                await _socket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Voice Session ended",
                        timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _socket.Abort();
            }
            catch (WebSocketException)
            {
                _socket.Abort();
            }
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
            _lifetime.Cancel();
            if (_receiveTask is not null)
            {
                try
                {
                    await _receiveTask.ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }

            _socket.Dispose();
            _sendGate.Dispose();
            _lifetime.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    private async Task ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();
        try
        {
            while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _log?.Invoke(
                            $"Voice PE closed the LAN audio socket; status={result.CloseStatus?.ToString() ?? "none"}; "
                            + $"speakerFrames={Interlocked.Read(ref _speakerFramesSent)}; "
                            + $"microphoneFrames={Interlocked.Read(ref _microphoneSequence)}; "
                            + $"microphonePeak={Volatile.Read(ref _microphonePeakAbsolute)}.");
                        _microphoneFrames.Writer.TryComplete();
                        _completion.TrySetResult();
                        return;
                    }

                    if (result.Count > 0)
                    {
                        message.Write(buffer, 0, result.Count);
                        if (message.Length > 64 * 1024)
                        {
                            throw new InvalidDataException("Voice PE sent an oversized audio-bridge message.");
                        }
                    }
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    HandleTextMessage(message.GetBuffer().AsSpan(0, checked((int)message.Length)));
                }
                else if (result.MessageType == WebSocketMessageType.Binary)
                {
                    if (message.Length != MicrophoneFrameBytes)
                    {
                        _log?.Invoke($"Voice PE dropped a microphone frame with {message.Length} bytes; expected {MicrophoneFrameBytes}.");
                        continue;
                    }

                    var payload = message.ToArray();
                    var peak = PeakAbsolutePcm16(payload);
                    UpdateMaximum(ref _microphonePeakAbsolute, peak);
                    if (peak > 0 && Interlocked.Increment(ref _microphoneNonSilentFrames) == 1)
                    {
                        _log?.Invoke($"Voice PE delivered its first non-silent microphone frame; peak={peak}.");
                    }

                    var sequence = Interlocked.Increment(ref _microphoneSequence) - 1;
                    if (sequence == 0)
                    {
                        _log?.Invoke("Voice PE delivered its first live microphone frame to Joydex.");
                    }

                    _microphoneFrames.Writer.TryWrite(new VoicePcmFrame(sequence, payload));
                }
            }

            _microphoneFrames.Writer.TryComplete();
            _completion.TrySetResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _microphoneFrames.Writer.TryComplete();
            _completion.TrySetResult();
        }
        catch (Exception exception)
        {
            _log?.Invoke(
                $"Voice PE LAN audio receive failed; speakerFrames={Interlocked.Read(ref _speakerFramesSent)}; "
                + $"microphoneFrames={Interlocked.Read(ref _microphoneSequence)}; "
                + $"nonSilentMicrophoneFrames={Interlocked.Read(ref _microphoneNonSilentFrames)}; "
                + $"microphonePeak={Volatile.Read(ref _microphonePeakAbsolute)}; error={exception.Message}");
            _microphoneFrames.Writer.TryComplete(exception);
            _completion.TrySetException(exception);
            _hello.TrySetException(exception);
            _ready.TrySetException(exception);
        }
    }

    private void HandleTextMessage(ReadOnlySpan<byte> utf8)
    {
        using var document = JsonDocument.Parse(utf8.ToArray());
        var root = document.RootElement;
        var type = root.TryGetProperty("type", out var typeElement)
            ? typeElement.GetString()
            : null;
        switch (type)
        {
            case "hello":
                ValidateHello(root, _requireUplinkOnly);
                _hello.TrySetResult(true);
                break;

            case "opened":
            case "ready":
                _ready.TrySetResult(true);
                break;

            case "error":
                var detail = root.TryGetProperty("message", out var message)
                    ? message.GetString()
                    : "Unknown device audio error.";
                throw new InvalidDataException(detail);

            case "pong":
                // Accepted for compatibility with diagnostic clients. Joydex
                // does not send application-level heartbeats during PCM flow.
                break;
        }
    }

    internal static void ValidateHello(JsonElement root, bool requireUplinkOnly)
    {
        var protocol = root.TryGetProperty("protocol", out var protocolElement)
            ? protocolElement.GetInt32()
            : root.TryGetProperty("version", out var versionElement)
                ? versionElement.GetInt32()
                : 0;
        if (protocol != 1)
        {
            throw new InvalidDataException("Voice PE uses an unsupported audio-bridge protocol version.");
        }

        if (!requireUplinkOnly)
        {
            return;
        }

        var mode = root.TryGetProperty("mode", out var modeElement)
            ? modeElement.GetString()
            : null;
        var downlinkIsDisabled = root.TryGetProperty("downlink", out var downlinkElement)
            && downlinkElement.ValueKind == JsonValueKind.Null;
        if (!string.Equals(mode, "uplink_only", StringComparison.Ordinal) || !downlinkIsDisabled)
        {
            throw new InvalidDataException(
                "Voice PE firmware does not advertise the required microphone-only port 8765 capability.");
        }
    }

    private static int PeakAbsolutePcm16(byte[] payload)
    {
        var peak = 0;
        for (var offset = 0; offset < payload.Length; offset += sizeof(short))
        {
            var sample = (short) (payload[offset] | (payload[offset + 1] << 8));
            var absolute = sample == short.MinValue ? 32_768 : Math.Abs(sample);
            peak = Math.Max(peak, absolute);
        }

        return peak;
    }

    private static void UpdateMaximum(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    private async Task SendTextAsync(object message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(
                    payload,
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async ValueTask SendControlAsync(object message, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _opened) == 0)
        {
            return;
        }
        if (_socket.State != WebSocketState.Open)
        {
            throw new IOException("The Voice PE audio socket closed before a playback control could be sent.");
        }

        await SendTextAsync(message, cancellationToken).ConfigureAwait(false);
    }

    internal static Uri BuildSocketUri(Uri deviceEndpoint)
    {
        ArgumentNullException.ThrowIfNull(deviceEndpoint);
        var builder = new UriBuilder(deviceEndpoint)
        {
            Scheme = deviceEndpoint.Scheme switch
            {
                "http" => "ws",
                "https" => "wss",
                _ => throw new ArgumentException("Voice PE endpoint must use HTTP or HTTPS.", nameof(deviceEndpoint)),
            },
            Port = 8765,
            Path = "/joydex/audio",
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
