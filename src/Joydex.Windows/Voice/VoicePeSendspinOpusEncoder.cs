using Concentus;
using Concentus.Enums;

namespace Joydex.Windows.Voice;

/// <summary>
/// Converts each 20 ms WebRTC PCM frame into one raw Opus packet for the
/// Voice PE's legacy Sendspin player.
/// </summary>
internal sealed class VoicePeSendspinOpusEncoder
{
    private readonly IOpusEncoder _encoder;
    private readonly short[] _pcm48Khz = new short[VoicePeSendspinProtocol.OutputSamplesPerChunk];

    internal VoicePeSendspinOpusEncoder()
    {
        _encoder = OpusCodecFactory.CreateEncoder(
            VoicePeSendspinProtocol.OutputSampleRate,
            VoicePeSendspinProtocol.Channels,
            OpusApplication.OPUS_APPLICATION_VOIP,
            messageLogger: null);
        _encoder.Bitrate = VoicePeSendspinProtocol.OpusBitrate;
        _encoder.Complexity = 5;
        _encoder.UseVBR = true;
        _encoder.UseDTX = false;
        _encoder.UseInbandFEC = false;
    }

    internal int Encode(ReadOnlySpan<byte> pcm24Khz, Span<byte> destination)
    {
        if (destination.Length < VoicePeSendspinProtocol.MaximumOpusPacketBytes)
        {
            throw new ArgumentException(
                $"The Opus destination needs {VoicePeSendspinProtocol.MaximumOpusPacketBytes} bytes.",
                nameof(destination));
        }

        VoicePeSendspinProtocol.Upsample24KhzMonoPcm16To48Khz(pcm24Khz, _pcm48Khz);
        return _encoder.Encode(
            _pcm48Khz,
            VoicePeSendspinProtocol.OutputSamplesPerChunk,
            destination,
            VoicePeSendspinProtocol.MaximumOpusPacketBytes);
    }

    internal void Reset() => _encoder.ResetState();
}
