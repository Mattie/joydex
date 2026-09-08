using System.Buffers;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Joydex.Core.Voice;

namespace Joydex.Windows.Voice;

/// <summary>
/// Implements the plaintext legacy Sendspin server dialect embedded in the
/// accepted Voice PE firmware. The device owns the WebSocket endpoint and the
/// clocked playback queue; Joydex supplies timestamped 48 kHz mono Opus.
/// </summary>
internal interface IVoicePeSendspinSpeakerSession : IAsyncDisposable
{
    Task Completion { get; }

    Task OpenAsync(CancellationToken cancellationToken);

    ValueTask SendFrameAsync(VoicePcmFrame frame, CancellationToken cancellationToken);

    ValueTask EndPlaybackAsync(CancellationToken cancellationToken);

    ValueTask FlushAsync(CancellationToken cancellationToken);

    Task CloseAsync(CancellationToken cancellationToken);
}

internal sealed class VoicePeSendspinSpeakerSession : IVoicePeSendspinSpeakerSession
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan InitialTimeSyncTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PlaybackDrainMargin = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan LegacyStopSettleDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PlayerStateTransitionTimeout = TimeSpan.FromSeconds(3);
    private const int MaximumStreamStartAttempts = 2;

    private readonly Uri _socketUri;
    private readonly Action<string> _log;
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _socketSendGate = new(1, 1);
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly SemaphoreSlim _timeSamples = new(0, int.MaxValue);
    private readonly SemaphoreSlim _clientStateChanged = new(0, int.MaxValue);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<VoicePeSendspinClientHello> _hello =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly VoicePeSendspinPlaybackTimeline _timeline = new();
    private readonly VoicePeSendspinOpusEncoder _opusEncoder = new();
    private readonly byte[] _opusPacket = new byte[VoicePeSendspinProtocol.MaximumOpusPacketBytes];
    private readonly long _clockOrigin = Stopwatch.GetTimestamp();

    private Task? _receiveTask;
    private long _framesSent;
    private long _playbackStreamsStarted;
    private long _playbackStreamsEnded;
    private long _payloadBytesSent;
    private long _clientStateRevision;
    private int _maximumPayloadBytes;
    private string? _clientState;
    private int _opened;
    private int _streamStarted;
    private int _closing;
    private int _disposed;

    internal VoicePeSendspinSpeakerSession(Uri deviceEndpoint, Action<string> log)
    {
        _socketUri = VoicePeSendspinProtocol.BuildSocketUri(deviceEndpoint);
        _log = log ?? throw new ArgumentNullException(nameof(log));
        // The pinned ESPHome Sendspin endpoint rejects .NET's keepalive control
        // frame. Sendspin's one-second clock exchange already keeps this live.
        _socket.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
    }

    public Task Completion => _completion.Task;

    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.CompareExchange(ref _opened, 1, 0) != 0)
        {
            throw new InvalidOperationException("The Voice PE Sendspin speaker session is already open.");
        }

        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetime.Token);
            handshake.CancelAfter(HandshakeTimeout);
            await _socket.ConnectAsync(_socketUri, handshake.Token).ConfigureAwait(false);
            _receiveTask = ReceiveLoopAsync(_lifetime.Token);
            var client = await _hello.Task.WaitAsync(handshake.Token).ConfigureAwait(false);
            VoicePeSendspinProtocol.ValidateClient(client);

            await SendJsonAsync(new
            {
                type = "server/hello",
                payload = new
                {
                    server_id = "joydex",
                    name = "Joydex",
                    version = 1,
                    active_roles = new[] { "player@v1" },
                    connection_reason = "playback",
                },
            }, handshake.Token).ConfigureAwait(false);

            for (var sample = 0; sample < 3; sample++)
            {
                if (!await _timeSamples.WaitAsync(InitialTimeSyncTimeout, handshake.Token).ConfigureAwait(false))
                {
                    throw new TimeoutException(
                        $"Voice PE Sendspin produced only {sample} of 3 required clock samples.");
                }
            }

            _log(
                $"Voice PE Sendspin speaker synchronized at {_socketUri.Host}:8927; "
                + $"device={client.Name}; bufferCapacity={client.BufferCapacity}; "
                + $"format=48kHz/mono/Opus@{VoicePeSendspinProtocol.OpusBitrate}bps.");
        }
        catch
        {
            // A failed hello or initial clock sync can occur after the socket
            // and receive loop are live. This session cannot be reopened, so
            // tear down that partial connection before recovery replaces it.
            Interlocked.Exchange(ref _closing, 1);
            _lifetime.Cancel();
            _socket.Abort();
            if (_receiveTask is not null)
            {
                try
                {
                    await _receiveTask.ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the handshake failure that selected this path.
                }
            }
            Interlocked.Exchange(ref _opened, 0);
            throw;
        }
    }

    public async ValueTask SendFrameAsync(
        VoicePcmFrame frame,
        CancellationToken cancellationToken)
    {
        frame.Validate(new VoicePcmFormat(24_000, 1));
        if (frame.Payload.Length != VoicePeSendspinProtocol.InputPcmBytesPerChunk)
        {
            throw new ArgumentException(
                $"Voice PE speaker frames must be exactly {VoicePeSendspinProtocol.InputPcmBytesPerChunk} bytes (20 ms).",
                nameof(frame));
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            var streamWasAlreadyStarted = Volatile.Read(ref _streamStarted) != 0;
            await EnsureStreamStartedAsync(cancellationToken).ConfigureAwait(false);

            var decision = _timeline.Next(ServerMicrosecondsNow());
            if (decision.Rebased && streamWasAlreadyStarted)
            {
                _log(
                    "Voice PE Sendspin timeline restored its 1,000 ms lead after a host delivery stall; "
                    + "a short silence gap may be audible.");
            }

            var encodedLength = _opusEncoder.Encode(frame.Payload.Span, _opusPacket);
            var packet = VoicePeSendspinProtocol.CreateAudioChunk(
                decision.TimestampMicroseconds,
                _opusPacket.AsSpan(0, encodedLength));
            await SendAsync(packet, WebSocketMessageType.Binary, cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref _payloadBytesSent, encodedLength);
            _maximumPayloadBytes = Math.Max(_maximumPayloadBytes, encodedLength);
            if (Interlocked.Increment(ref _framesSent) == 1)
            {
                _log(
                    "Joydex sent its first clocked Sendspin Opus speaker frame to the Voice PE; "
                    + $"packetBytes={encodedLength}.");
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask EndPlaybackAsync(CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            if (Volatile.Read(ref _streamStarted) == 0)
            {
                return;
            }

            await DrainScheduledPlaybackAsync(cancellationToken).ConfigureAwait(false);
            await EndStreamAsync(cancellationToken).ConfigureAwait(false);
            // The pinned legacy client does not publish a dependable stopped/idle state. Its
            // media-source task tears down asynchronously after stream/end, so leave enough time
            // for that task and its stack to be released. The next stream still requires a fresh
            // synchronized state before any audio is sent.
            await Task.Delay(LegacyStopSettleDelay, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            var clearedStream = false;
            if (Volatile.Read(ref _streamStarted) != 0)
            {
                await DrainScheduledPlaybackAsync(cancellationToken).ConfigureAwait(false);
                await SendJsonAsync(new
                {
                    type = "stream/clear",
                    payload = new { roles = new[] { "player" } },
                }, cancellationToken).ConfigureAwait(false);
                Interlocked.Exchange(ref _streamStarted, 0);
                clearedStream = true;
            }
            _timeline.Reset();
            if (clearedStream)
            {
                await Task.Delay(LegacyStopSettleDelay, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0)
        {
            return;
        }

        Exception? failure = null;
        await _operationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_socket.State == WebSocketState.Open && Volatile.Read(ref _streamStarted) != 0)
            {
                await DrainScheduledPlaybackAsync(cancellationToken).ConfigureAwait(false);
                await EndStreamAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(LegacyStopSettleDelay, cancellationToken).ConfigureAwait(false);
            }

            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var closeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                closeCancellation.CancelAfter(CloseTimeout);
                try
                {
                    await _socket.CloseOutputAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Voice Session ended",
                            closeCancellation.Token)
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

            _log(
                $"Voice PE Sendspin session closed: codec=opus, frames={Volatile.Read(ref _framesSent)}, "
                + $"payloadBytes={Volatile.Read(ref _payloadBytesSent)}, "
                + $"maximumPacketBytes={Volatile.Read(ref _maximumPayloadBytes)}, "
                + $"playbackStreamsStarted={Volatile.Read(ref _playbackStreamsStarted)}, "
                + $"playbackStreamsEnded={Volatile.Read(ref _playbackStreamsEnded)}.");
        }
        catch (Exception exception)
        {
            failure = exception;
            _socket.Abort();
        }
        finally
        {
            _operationGate.Release();
            _lifetime.Cancel();
            Interlocked.Exchange(ref _opened, 0);
        }

        if (_receiveTask is not null)
        {
            try
            {
                await _receiveTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (WebSocketException) when (Volatile.Read(ref _closing) != 0)
            {
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
            ExceptionDispatchInfo.Capture(failure).Throw();
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
            _socket.Dispose();
            _socketSendGate.Dispose();
            _operationGate.Dispose();
            _timeSamples.Dispose();
            _clientStateChanged.Dispose();
            _lifetime.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var receiveBuffer = new byte[8192];
        var messageBuffer = new ArrayBufferWriter<byte>(8192);
        try
        {
            while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                messageBuffer.Clear();
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(receiveBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        if (Volatile.Read(ref _closing) == 0)
                        {
                            throw new IOException("Voice PE closed the Sendspin speaker socket.");
                        }
                        return;
                    }
                    messageBuffer.Write(receiveBuffer.AsSpan(0, result.Count));
                    if (messageBuffer.WrittenCount > 1_048_576)
                    {
                        throw new IOException("Voice PE Sendspin message exceeded the 1 MiB safety bound.");
                    }
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    throw new InvalidDataException("Voice PE sent an unexpected binary Sendspin message.");
                }

                await HandleTextMessageAsync(messageBuffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _hello.TrySetException(exception);
            _completion.TrySetException(exception);
            throw;
        }
        finally
        {
            if (Volatile.Read(ref _closing) == 0 && !_completion.Task.IsCompleted)
            {
                _completion.TrySetException(new IOException("Voice PE Sendspin speaker receive loop ended unexpectedly."));
            }
        }
    }

    private async Task HandleTextMessageAsync(
        ReadOnlyMemory<byte> json,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var type = root.TryGetProperty("type", out var typeElement)
            ? typeElement.GetString()
            : null;
        switch (type)
        {
            case "client/hello":
                var hello = ParseHello(root.GetProperty("payload"));
                _hello.TrySetResult(hello);
                break;

            case "client/time":
                var serverReceived = ServerMicrosecondsNow();
                var clientTransmitted = root.GetProperty("payload")
                    .GetProperty("client_transmitted")
                    .GetInt64();
                var serverTransmitted = ServerMicrosecondsNow();
                await SendJsonAsync(new
                {
                    type = "server/time",
                    payload = new
                    {
                        client_transmitted = clientTransmitted,
                        server_received = serverReceived,
                        server_transmitted = serverTransmitted,
                    },
                }, cancellationToken).ConfigureAwait(false);
                _timeSamples.Release();
                break;

            case "client/state":
                var state = root.GetProperty("payload").TryGetProperty("state", out var stateElement)
                    ? stateElement.GetString()
                    : null;
                if (!string.IsNullOrWhiteSpace(state))
                {
                    Volatile.Write(ref _clientState, state);
                    Interlocked.Increment(ref _clientStateRevision);
                    _clientStateChanged.Release();
                    _log($"Voice PE Sendspin player state changed to {state}.");
                }
                break;

            case "client/goodbye":
                throw new IOException("Voice PE ended the Sendspin speaker connection.");

            case "client/command":
            case "stream/request-format":
                _log($"Voice PE sent an unsupported legacy Sendspin message: {Encoding.UTF8.GetString(json.Span)}");
                break;
        }
    }

    private async Task EnsureStreamStartedAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _streamStarted) != 0)
        {
            return;
        }

        for (var attempt = 1; attempt <= MaximumStreamStartAttempts; attempt++)
        {
            var stateRevision = Volatile.Read(ref _clientStateRevision);
            await SendJsonAsync(new
            {
                type = "group/update",
                payload = new
                {
                    playback_state = "playing",
                    group_id = "joydex-voice",
                    group_name = "Joydex Voice",
                },
            }, cancellationToken).ConfigureAwait(false);
            await SendJsonAsync(new
            {
                type = "stream/start",
                payload = new
                {
                    player = new
                    {
                        codec = "opus",
                        sample_rate = VoicePeSendspinProtocol.OutputSampleRate,
                        channels = VoicePeSendspinProtocol.Channels,
                        bit_depth = VoicePeSendspinProtocol.BitsPerSample,
                    },
                },
            }, cancellationToken).ConfigureAwait(false);

            try
            {
                await WaitForFreshClientStateAsync("synchronized", stateRevision, cancellationToken)
                    .ConfigureAwait(false);
                _opusEncoder.Reset();
                _timeline.Reset();
                Interlocked.Exchange(ref _streamStarted, 1);
                Interlocked.Increment(ref _playbackStreamsStarted);
                return;
            }
            catch (TimeoutException) when (attempt < MaximumStreamStartAttempts)
            {
                _log(
                    "Voice PE did not acknowledge the new Sendspin response stream; "
                    + $"retrying stream/start after the legacy player teardown window; attempt={attempt + 1}/{MaximumStreamStartAttempts}.");
            }
        }

        throw new TimeoutException("Voice PE did not acknowledge the Sendspin response stream after retry.");
    }

    private async Task DrainScheduledPlaybackAsync(CancellationToken cancellationToken)
    {
        var remainingMicroseconds = _timeline.RemainingMicroseconds(ServerMicrosecondsNow());
        if (remainingMicroseconds <= 0)
        {
            return;
        }

        var delay = TimeSpan.FromMilliseconds(remainingMicroseconds / 1000.0) + PlaybackDrainMargin;
        _log($"Voice PE Sendspin is draining the response tail before stream/end; waitMs={delay.TotalMilliseconds:F0}.");
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    private async Task WaitForFreshClientStateAsync(
        string expectedState,
        long afterRevision,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(PlayerStateTransitionTimeout);
        try
        {
            while (true)
            {
                var revision = Volatile.Read(ref _clientStateRevision);
                var state = Volatile.Read(ref _clientState);
                if (revision > afterRevision
                    && string.Equals(state, expectedState, StringComparison.Ordinal))
                {
                    return;
                }

                await _clientStateChanged.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
                                                 && !_lifetime.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Voice PE Sendspin did not report {expectedState} after its stream transition; "
                + $"lastState={Volatile.Read(ref _clientState) ?? "unknown"}.");
        }
    }

    private async Task EndStreamAsync(CancellationToken cancellationToken)
    {
        await SendJsonAsync(new
        {
            type = "stream/end",
            payload = new { roles = new[] { "player" } },
        }, cancellationToken).ConfigureAwait(false);
        await SendJsonAsync(new
        {
            type = "group/update",
            payload = new
            {
                playback_state = "stopped",
                group_id = "joydex-voice",
                group_name = "Joydex Voice",
            },
        }, cancellationToken).ConfigureAwait(false);
        _timeline.Reset();
        Interlocked.Exchange(ref _streamStarted, 0);
        Interlocked.Increment(ref _playbackStreamsEnded);
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _opened) == 0 || _socket.State != WebSocketState.Open)
        {
            throw new IOException("The Voice PE Sendspin speaker socket is not open.");
        }
    }

    private async ValueTask SendJsonAsync<T>(T message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        await SendAsync(payload, WebSocketMessageType.Text, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask SendAsync(
        ReadOnlyMemory<byte> payload,
        WebSocketMessageType messageType,
        CancellationToken cancellationToken)
    {
        await _socketSendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_socket.State != WebSocketState.Open)
            {
                throw new IOException($"Voice PE Sendspin socket is {_socket.State}.");
            }
            await _socket.SendAsync(payload, messageType, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _socketSendGate.Release();
        }
    }

    private long ServerMicrosecondsNow() =>
        (long)(Stopwatch.GetElapsedTime(_clockOrigin).TotalMilliseconds * 1000.0);

    private static VoicePeSendspinClientHello ParseHello(JsonElement payload)
    {
        var roles = payload.GetProperty("supported_roles")
            .EnumerateArray()
            .Select(role => role.GetString() ?? string.Empty)
            .ToArray();
        JsonElement playerSupport;
        if (!payload.TryGetProperty("player_support", out playerSupport)
            && !payload.TryGetProperty("player@v1_support", out playerSupport))
        {
            throw new IOException("Voice PE Sendspin hello did not include player support.");
        }
        var formats = playerSupport.GetProperty("supported_formats")
            .EnumerateArray()
            .Select(format => new VoicePeSendspinAudioFormat(
                format.GetProperty("codec").GetString() ?? string.Empty,
                format.GetProperty("channels").GetInt32(),
                format.GetProperty("sample_rate").GetInt32(),
                format.GetProperty("bit_depth").GetInt32()))
            .ToArray();
        return new VoicePeSendspinClientHello(
            payload.GetProperty("client_id").GetString() ?? string.Empty,
            payload.GetProperty("name").GetString() ?? string.Empty,
            payload.GetProperty("version").GetInt32(),
            roles,
            formats,
            playerSupport.GetProperty("buffer_capacity").GetInt32());
    }

}
