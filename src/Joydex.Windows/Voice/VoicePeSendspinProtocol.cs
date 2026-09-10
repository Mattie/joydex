using System.Buffers.Binary;
namespace Joydex.Windows.Voice;

internal sealed record VoicePeSendspinAudioFormat(
    string Codec,
    int Channels,
    int SampleRate,
    int BitDepth);

internal sealed record VoicePeSendspinClientHello(
    string ClientId,
    string Name,
    int Version,
    IReadOnlyList<string> SupportedRoles,
    IReadOnlyList<VoicePeSendspinAudioFormat> SupportedFormats,
    int BufferCapacity);

internal static class VoicePeSendspinProtocol
{
    internal const byte PlayerAudioMessageType = 4;
    internal const int BinaryHeaderBytes = 9;
    internal const int OutputSampleRate = 48_000;
    internal const int Channels = 1;
    internal const int BitsPerSample = 16;
    internal const int ChunkMilliseconds = 20;
    internal const int InputPcmBytesPerChunk = 24_000 * ChunkMilliseconds / 1000 * sizeof(short);
    internal const int OutputSamplesPerChunk = OutputSampleRate * ChunkMilliseconds / 1000;
    internal const int MaximumOpusPacketBytes = 1275;
    internal const int OpusBitrate = 32_000;
    // The modern player advertises 80% of its configured compressed buffer.
    // ESPHome's 25,000-byte configuration minimum is therefore 20,000 on the wire.
    internal const int MinimumCompressedBufferBytes = 20_000;
    internal const long TargetLeadMicroseconds = 1_000_000;
    internal const long MinimumLeadMicroseconds = 500_000;

    internal static byte[] CreateAudioChunk(long timestampMicroseconds, ReadOnlySpan<byte> audioPayload)
    {
        var message = new byte[BinaryHeaderBytes + audioPayload.Length];
        WriteAudioChunk(timestampMicroseconds, audioPayload, message);
        return message;
    }

    internal static int WriteAudioChunk(
        long timestampMicroseconds,
        ReadOnlySpan<byte> audioPayload,
        Span<byte> destination)
    {
        var messageLength = checked(BinaryHeaderBytes + audioPayload.Length);
        if (destination.Length < messageLength)
        {
            throw new ArgumentException(
                $"The Sendspin destination needs {messageLength} bytes.",
                nameof(destination));
        }

        destination[0] = PlayerAudioMessageType;
        BinaryPrimitives.WriteInt64BigEndian(destination.Slice(1, sizeof(long)), timestampMicroseconds);
        audioPayload.CopyTo(destination[BinaryHeaderBytes..messageLength]);
        return messageLength;
    }

    internal static void ValidateClient(VoicePeSendspinClientHello client)
    {
        if (client.Version != 1
            || !client.SupportedRoles.Contains("player@v1", StringComparer.Ordinal))
        {
            throw new IOException("Voice PE did not advertise the Sendspin player@v1 role.");
        }
        if (!client.SupportedFormats.Any(format =>
                format.Codec == "opus"
                && format.Channels == Channels
                && format.SampleRate == OutputSampleRate
                && format.BitDepth == BitsPerSample))
        {
            throw new IOException("Voice PE did not advertise 48 kHz mono Opus Sendspin playback.");
        }
        // Sendspin reports buffer_capacity in compressed bytes. It is unrelated
        // to the microsecond timestamp lead that Joydex schedules on the wire.
        if (client.BufferCapacity < MinimumCompressedBufferBytes)
        {
            throw new IOException(
                $"Voice PE Sendspin compressed buffer is smaller than {MinimumCompressedBufferBytes} bytes.");
        }
    }

    internal static void Upsample24KhzMonoPcm16To48Khz(
        ReadOnlySpan<byte> input,
        Span<short> output)
    {
        if (input.Length != InputPcmBytesPerChunk)
        {
            throw new ArgumentException(
                $"Sendspin input must contain exactly {InputPcmBytesPerChunk} bytes of 24 kHz mono PCM16.",
                nameof(input));
        }
        if (output.Length != OutputSamplesPerChunk)
        {
            throw new ArgumentException(
                $"Sendspin output must contain exactly {OutputSamplesPerChunk} 48 kHz mono samples.",
                nameof(output));
        }

        for (var inputOffset = 0; inputOffset < input.Length; inputOffset += sizeof(short))
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(input.Slice(inputOffset, sizeof(short)));
            var outputOffset = inputOffset;
            output[outputOffset] = sample;
            output[outputOffset + 1] = sample;
        }
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
            Port = 8927,
            Path = "/sendspin",
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri;
    }
}

internal sealed class VoicePeSendspinPlaybackTimeline
{
    private long _nextTimestampMicroseconds;

    internal long NextTimestampMicroseconds => _nextTimestampMicroseconds;

    internal SendspinTimestampDecision Next(long nowMicroseconds)
    {
        var rebased = _nextTimestampMicroseconds == 0
                      || _nextTimestampMicroseconds < nowMicroseconds + VoicePeSendspinProtocol.MinimumLeadMicroseconds;
        if (rebased)
        {
            _nextTimestampMicroseconds = checked(
                nowMicroseconds + VoicePeSendspinProtocol.TargetLeadMicroseconds);
        }

        var timestamp = _nextTimestampMicroseconds;
        _nextTimestampMicroseconds = checked(
            _nextTimestampMicroseconds + VoicePeSendspinProtocol.ChunkMilliseconds * 1000L);
        return new SendspinTimestampDecision(timestamp, rebased);
    }

    internal long RemainingMicroseconds(long nowMicroseconds) =>
        Math.Max(0, _nextTimestampMicroseconds - nowMicroseconds);

    internal void Reset() => _nextTimestampMicroseconds = 0;
}

internal readonly record struct SendspinTimestampDecision(
    long TimestampMicroseconds,
    bool Rebased);
