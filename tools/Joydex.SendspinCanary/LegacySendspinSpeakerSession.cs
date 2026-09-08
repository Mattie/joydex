using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Concentus;
using Concentus.Enums;

namespace Joydex.SendspinCanary;

internal sealed record SendspinAudioFormat(string Codec, int Channels, int SampleRate, int BitDepth);

internal sealed record SendspinClientHello(
    string ClientId,
    string Name,
    int Version,
    IReadOnlyList<string> SupportedRoles,
    IReadOnlyList<SendspinAudioFormat> SupportedFormats,
    int BufferCapacity);

internal sealed record SendspinSessionSnapshot(
    string Endpoint,
    SendspinClientHello? Client,
    long TextMessagesReceived,
    long BinaryMessagesReceived,
    long ClientStateMessages,
    long TimeSamplesAnswered,
    string? LastClientState,
    string? LastUnexpectedMessage,
    bool Connected,
    bool StreamStarted,
    bool StreamEnded);

internal sealed record SendspinCanaryResult(
    bool Accepted,
    string Endpoint,
    string ClientName,
    string ClientId,
    int ProtocolVersion,
    int BufferCapacity,
    string Codec,
    int? OpusBitrate,
    int LeadMilliseconds,
    int ChunkMilliseconds,
    long FramesSent,
    long PayloadBytesSent,
    int MaximumPayloadBytes,
    long TimeSamplesAnswered,
    long TimeResponsesWithheld,
    double MaximumSendLatenessMilliseconds,
    double MaximumSendIntervalMilliseconds,
    long CatchupIntervals,
    long ClientStateMessages,
    string? LastClientState,
    IReadOnlyList<SendspinAudioFormat> SupportedFormats);

/// <summary>
/// Minimal server half of the legacy Sendspin v1 dialect shipped by the
/// accepted Voice PE 0.1.19 image. The Voice PE exposes the WebSocket; Joydex
/// connects to it and supplies timestamped 48 kHz mono PCM or Opus chunks.
/// </summary>
internal sealed class LegacySendspinSpeakerSession : IAsyncDisposable
{
    public const byte PlayerAudioMessageType = 4;
    public const int BinaryHeaderBytes = 9;
    public const int SampleRate = 48_000;
    public const int Channels = 1;
    public const int BitsPerSample = 16;
    public const int ChunkMilliseconds = 20;
    public const int SamplesPerChunk = SampleRate * ChunkMilliseconds / 1000;
    public const int PcmBytesPerChunk = SamplesPerChunk * sizeof(short);
    public const int MaximumOpusPacketBytes = 1275;
    public const int DefaultOpusBitrate = 32_000;

    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan InitialTimeSyncTimeout = TimeSpan.FromSeconds(4);

    private readonly Uri _endpoint;
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _timeSamples = new(0, int.MaxValue);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<SendspinClientHello> _hello =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _withheldTimeResponse =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly long _clockOrigin = Stopwatch.GetTimestamp();

    private Task? _receiveTask;
    private SendspinClientHello? _client;
    private long _textMessagesReceived;
    private long _binaryMessagesReceived;
    private long _clientStateMessages;
    private long _timeSamplesAnswered;
    private long _timeResponsesWithheld;
    private string? _lastClientState;
    private string? _lastUnexpectedMessage;
    private int _streamStarted;
    private int _streamEnded;
    private int _withholdNextTimeResponse;
    private int _disposed;

    public LegacySendspinSpeakerSession(Uri endpoint)
    {
        _endpoint = endpoint;
        // ESPHome's legacy Sendspin HTTPD endpoint rejects .NET's WebSocket
        // keepalive control frame at exactly the configured interval. Sendspin
        // already exchanges clock messages every second, so the socket is live.
        _socket.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        handshake.CancelAfter(HandshakeTimeout);
        await _socket.ConnectAsync(_endpoint, handshake.Token).ConfigureAwait(false);
        _receiveTask = ReceiveLoopAsync(_lifetime.Token);
        _client = await _hello.Task.WaitAsync(handshake.Token).ConfigureAwait(false);

        if (_client.Version != 1 || !_client.SupportedRoles.Contains("player@v1", StringComparer.Ordinal))
        {
            throw new IOException("Voice PE did not advertise the legacy Sendspin player@v1 role.");
        }
        if (!_client.SupportedFormats.Any(format =>
                (format.Codec == "pcm" || format.Codec == "opus")
                && format.Channels == Channels
                && format.SampleRate == SampleRate))
        {
            throw new IOException("Voice PE did not advertise a supported 48 kHz mono speaker format.");
        }

        await SendJsonAsync(new
        {
            type = "server/hello",
            payload = new
            {
                server_id = "joydex-sendspin-canary",
                name = "Joydex Sendspin Canary",
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
    }

    public async Task<SendspinCanaryResult> PlayAsync(
        ReadOnlyMemory<byte> pcmClip,
        TimeSpan duration,
        int leadMilliseconds,
        int chunkMilliseconds,
        string codec,
        int opusBitrate,
        bool withholdFinalTimeResponse,
        CancellationToken cancellationToken)
    {
        var client = _client ?? throw new InvalidOperationException("ConnectAsync must complete before playback.");
        if (chunkMilliseconds < ChunkMilliseconds || chunkMilliseconds > 200
            || chunkMilliseconds % ChunkMilliseconds != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(chunkMilliseconds),
                "Sendspin chunks must be a multiple of 20 ms from 20 through 200 ms.");
        }
        codec = codec.ToLowerInvariant();
        if (codec is not ("pcm" or "opus"))
        {
            throw new ArgumentOutOfRangeException(nameof(codec), "Codec must be pcm or opus.");
        }
        if (codec == "opus" && chunkMilliseconds is not (20 or 40 or 60))
        {
            throw new ArgumentOutOfRangeException(
                nameof(chunkMilliseconds),
                "Opus chunks must use a supported 20, 40, or 60 ms frame duration.");
        }
        if (!client.SupportedFormats.Any(format =>
                format.Codec == codec
                && format.Channels == Channels
                && format.SampleRate == SampleRate))
        {
            throw new IOException($"Voice PE did not advertise 48 kHz mono {codec} support.");
        }
        var frameCount = checked((int) Math.Ceiling(duration.TotalMilliseconds / chunkMilliseconds));
        var pcmBytesPerChunk = checked(SampleRate * chunkMilliseconds / 1000 * sizeof(short));
        var frame = new byte[pcmBytesPerChunk];
        var samplesPerChunk = checked(SampleRate * chunkMilliseconds / 1000);
        var opusPcm = codec == "opus" ? new short[samplesPerChunk] : null;
        var opusPacket = codec == "opus" ? new byte[MaximumOpusPacketBytes] : null;
        var opusEncoder = codec == "opus" ? CreateOpusEncoder(opusBitrate) : null;

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
                    codec,
                    sample_rate = SampleRate,
                    channels = Channels,
                    bit_depth = BitsPerSample,
                },
            },
        }, cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _streamStarted, 1);

        var firstPlaybackTimestamp = ServerMicrosNow() + (leadMilliseconds * 1000L);
        var sendClock = Stopwatch.StartNew();
        var previousSendMilliseconds = 0.0;
        var maximumSendLatenessMilliseconds = 0.0;
        var maximumSendIntervalMilliseconds = 0.0;
        var catchupIntervals = 0L;
        var payloadBytesSent = 0L;
        var maximumPayloadBytes = 0;
        var pcmOffset = 0;
        using var timerResolution = TimerResolutionLease.Acquire();

        for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            var targetMilliseconds = frameIndex * (double) chunkMilliseconds;
            await WaitUntilAsync(sendClock, targetMilliseconds, cancellationToken).ConfigureAwait(false);
            var sentAtMilliseconds = sendClock.Elapsed.TotalMilliseconds;
            maximumSendLatenessMilliseconds = Math.Max(
                maximumSendLatenessMilliseconds,
                Math.Max(0, sentAtMilliseconds - targetMilliseconds));
            if (frameIndex > 0)
            {
                var intervalMilliseconds = sentAtMilliseconds - previousSendMilliseconds;
                maximumSendIntervalMilliseconds = Math.Max(maximumSendIntervalMilliseconds, intervalMilliseconds);
                if (intervalMilliseconds < chunkMilliseconds / 2.0)
                {
                    catchupIntervals++;
                }
            }

            frame.AsSpan().Clear();
            if (pcmOffset < pcmClip.Length)
            {
                var copyLength = Math.Min(frame.Length, pcmClip.Length - pcmOffset);
                pcmClip.Span.Slice(pcmOffset, copyLength).CopyTo(frame);
                pcmOffset += copyLength;
            }
            var playbackTimestamp = firstPlaybackTimestamp + (frameIndex * chunkMilliseconds * 1000L);
            ReadOnlyMemory<byte> audioPayload = frame;
            if (opusEncoder is not null && opusPcm is not null && opusPacket is not null)
            {
                CopyPcm16LittleEndian(frame, opusPcm);
                var packetLength = opusEncoder.Encode(opusPcm, samplesPerChunk, opusPacket, opusPacket.Length);
                audioPayload = opusPacket.AsMemory(0, packetLength);
            }
            payloadBytesSent += audioPayload.Length;
            maximumPayloadBytes = Math.Max(maximumPayloadBytes, audioPayload.Length);
            await SendBinaryAsync(CreateAudioChunk(playbackTimestamp, audioPayload.Span), cancellationToken)
                .ConfigureAwait(false);
            previousSendMilliseconds = sentAtMilliseconds;
        }

        var finalPlaybackTimestamp = firstPlaybackTimestamp + (frameCount * chunkMilliseconds * 1000L);
        var remainingPlayback = finalPlaybackTimestamp - ServerMicrosNow();
        if (remainingPlayback > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds((remainingPlayback / 1000.0) + 100), cancellationToken)
                .ConfigureAwait(false);
        }

        if (withholdFinalTimeResponse)
        {
            Interlocked.Exchange(ref _withholdNextTimeResponse, 1);
            using var withholdTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetime.Token);
            withholdTimeout.CancelAfter(TimeSpan.FromSeconds(4));
            await _withheldTimeResponse.Task.WaitAsync(withholdTimeout.Token).ConfigureAwait(false);
        }

        await SendJsonAsync(new
        {
            type = "stream/end",
            payload = new { roles = new[] { "player" } },
        }, cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _streamEnded, 1);
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
        await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);

        var accepted = _socket.State == WebSocketState.Open
                       && Volatile.Read(ref _timeSamplesAnswered) >= 3
                       && Volatile.Read(ref _streamStarted) != 0
                       && Volatile.Read(ref _streamEnded) != 0;
        return new SendspinCanaryResult(
            accepted,
            _endpoint.ToString(),
            client.Name,
            client.ClientId,
            client.Version,
            client.BufferCapacity,
            codec,
            codec == "opus" ? opusBitrate : null,
            leadMilliseconds,
            chunkMilliseconds,
            frameCount,
            payloadBytesSent,
            maximumPayloadBytes,
            Volatile.Read(ref _timeSamplesAnswered),
            Volatile.Read(ref _timeResponsesWithheld),
            Math.Round(maximumSendLatenessMilliseconds, 3),
            Math.Round(maximumSendIntervalMilliseconds, 3),
            catchupIntervals,
            Volatile.Read(ref _clientStateMessages),
            Volatile.Read(ref _lastClientState),
            client.SupportedFormats);
    }

    public SendspinSessionSnapshot Snapshot() => new(
        _endpoint.ToString(),
        _client,
        Volatile.Read(ref _textMessagesReceived),
        Volatile.Read(ref _binaryMessagesReceived),
        Volatile.Read(ref _clientStateMessages),
        Volatile.Read(ref _timeSamplesAnswered),
        Volatile.Read(ref _lastClientState),
        Volatile.Read(ref _lastUnexpectedMessage),
        _socket.State == WebSocketState.Open,
        Volatile.Read(ref _streamStarted) != 0,
        Volatile.Read(ref _streamEnded) != 0);

    public static byte[] CreateAudioChunk(long timestamp, ReadOnlySpan<byte> pcm)
    {
        var message = new byte[BinaryHeaderBytes + pcm.Length];
        message[0] = PlayerAudioMessageType;
        BinaryPrimitives.WriteInt64BigEndian(message.AsSpan(1, 8), timestamp);
        pcm.CopyTo(message.AsSpan(BinaryHeaderBytes));
        return message;
    }

    public static IOpusEncoder CreateOpusEncoder(int bitrate)
    {
        if (bitrate is < 6_000 or > 128_000)
        {
            throw new ArgumentOutOfRangeException(nameof(bitrate), "Opus bitrate must be 6000 through 128000 bps.");
        }
        var encoder = OpusCodecFactory.CreateEncoder(
            SampleRate,
            Channels,
            OpusApplication.OPUS_APPLICATION_VOIP,
            messageLogger: null);
        encoder.Bitrate = bitrate;
        encoder.Complexity = 5;
        encoder.UseVBR = true;
        encoder.UseDTX = false;
        encoder.UseInbandFEC = false;
        return encoder;
    }

    private static void CopyPcm16LittleEndian(ReadOnlySpan<byte> source, Span<short> destination)
    {
        for (var index = 0; index < destination.Length; index++)
        {
            destination[index] = BinaryPrimitives.ReadInt16LittleEndian(source.Slice(index * sizeof(short), sizeof(short)));
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
            _lifetime.Cancel();
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await _socket.CloseOutputAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "canary complete",
                            closeTimeout.Token)
                        .ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                }
                catch (OperationCanceledException)
                {
                }
            }
            if (_receiveTask is not null)
            {
                try
                {
                    await _receiveTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (WebSocketException)
                {
                }
            }
        }
        finally
        {
            _socket.Dispose();
            _sendGate.Dispose();
            _timeSamples.Dispose();
            _lifetime.Dispose();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var receiveBuffer = new byte[8192];
        var messageBuffer = new ArrayBufferWriter<byte>(8192);
        while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
        {
            messageBuffer.Clear();
            ValueWebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(receiveBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }
                messageBuffer.Write(receiveBuffer.AsSpan(0, result.Count));
                if (messageBuffer.WrittenCount > 1_048_576)
                {
                    throw new IOException("Voice PE Sendspin message exceeded the 1 MiB safety bound.");
                }
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                Interlocked.Increment(ref _binaryMessagesReceived);
                Volatile.Write(ref _lastUnexpectedMessage, "Unexpected client binary message");
                continue;
            }

            Interlocked.Increment(ref _textMessagesReceived);
            await HandleTextMessageAsync(messageBuffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleTextMessageAsync(ReadOnlyMemory<byte> json, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var typeElement))
        {
            Volatile.Write(ref _lastUnexpectedMessage, "Client JSON had no type");
            return;
        }
        var type = typeElement.GetString();
        switch (type)
        {
            case "client/hello":
                var hello = ParseHello(root.GetProperty("payload"));
                _client = hello;
                _hello.TrySetResult(hello);
                break;
            case "client/time":
                if (Interlocked.Exchange(ref _withholdNextTimeResponse, 0) != 0)
                {
                    Interlocked.Increment(ref _timeResponsesWithheld);
                    _withheldTimeResponse.TrySetResult();
                    break;
                }
                var serverReceived = ServerMicrosNow();
                var clientTransmitted = root.GetProperty("payload").GetProperty("client_transmitted").GetInt64();
                var serverTransmitted = ServerMicrosNow();
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
                Interlocked.Increment(ref _timeSamplesAnswered);
                _timeSamples.Release();
                break;
            case "client/state":
                Interlocked.Increment(ref _clientStateMessages);
                Volatile.Write(ref _lastClientState, Encoding.UTF8.GetString(json.Span));
                break;
            case "client/command":
            case "stream/request-format":
                Volatile.Write(ref _lastUnexpectedMessage, Encoding.UTF8.GetString(json.Span));
                break;
            case "client/goodbye":
                Volatile.Write(ref _lastUnexpectedMessage, Encoding.UTF8.GetString(json.Span));
                _lifetime.Cancel();
                break;
            default:
                Volatile.Write(ref _lastUnexpectedMessage, Encoding.UTF8.GetString(json.Span));
                break;
        }
    }

    private async ValueTask SendJsonAsync<T>(T message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        await SendAsync(payload, WebSocketMessageType.Text, cancellationToken).ConfigureAwait(false);
    }

    private ValueTask SendBinaryAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) =>
        SendAsync(payload, WebSocketMessageType.Binary, cancellationToken);

    private async ValueTask SendAsync(
        ReadOnlyMemory<byte> payload,
        WebSocketMessageType messageType,
        CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            _sendGate.Release();
        }
    }

    private static SendspinClientHello ParseHello(JsonElement payload)
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
            .Select(format => new SendspinAudioFormat(
                format.GetProperty("codec").GetString() ?? string.Empty,
                format.GetProperty("channels").GetInt32(),
                format.GetProperty("sample_rate").GetInt32(),
                format.GetProperty("bit_depth").GetInt32()))
            .ToArray();
        return new SendspinClientHello(
            payload.GetProperty("client_id").GetString() ?? string.Empty,
            payload.GetProperty("name").GetString() ?? string.Empty,
            payload.GetProperty("version").GetInt32(),
            roles,
            formats,
            playerSupport.GetProperty("buffer_capacity").GetInt32());
    }

    private long ServerMicrosNow() =>
        (long) (Stopwatch.GetElapsedTime(_clockOrigin).TotalMilliseconds * 1000.0);

    private static async Task WaitUntilAsync(Stopwatch clock, double targetMilliseconds, CancellationToken token)
    {
        while (true)
        {
            var remaining = targetMilliseconds - clock.Elapsed.TotalMilliseconds;
            if (remaining <= 0)
            {
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(remaining), token).ConfigureAwait(false);
        }
    }
}
