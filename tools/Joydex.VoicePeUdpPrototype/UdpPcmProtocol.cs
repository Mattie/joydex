using System.Buffers.Binary;

namespace Joydex.VoicePeUdpPrototype;

internal enum UdpPcmPacketType : byte
{
    Open = 1,
    Ready = 2,
    Close = 3,
    Closed = 4,
    Ping = 5,
    Pong = 6,
    PlaybackEnd = 7,
    Speaker = 16,
    Microphone = 17,
}

internal readonly record struct UdpPcmPacket(
    UdpPcmPacketType Type,
    uint SessionId,
    uint Sequence,
    byte[] Payload);

/// <summary>
/// Encodes the portable, fixed-size UDPPCM v1 header shared with the Voice PE prototype.
/// </summary>
internal static class UdpPcmProtocol
{
    public const uint Magic = 0x4A445855;
    public const byte Version = 1;
    public const int HeaderBytes = 16;
    public const int MicrophonePayloadBytes = 640;
    public const int SpeakerPayloadBytes = 960;
    public const int Port = 8766;

    public static byte[] Encode(
        UdpPcmPacketType type,
        uint sessionId,
        uint sequence = 0,
        ReadOnlySpan<byte> payload = default)
    {
        if (sessionId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionId), "UDPPCM session IDs must be nonzero.");
        }
        if (payload.Length > SpeakerPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "UDPPCM payload exceeds one MTU-safe audio frame.");
        }

        var packet = new byte[HeaderBytes + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(packet, Magic);
        packet[4] = Version;
        packet[5] = (byte) type;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), checked((ushort) payload.Length));
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), sessionId);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(12), sequence);
        payload.CopyTo(packet.AsSpan(HeaderBytes));
        return packet;
    }

    public static bool TryDecode(ReadOnlySpan<byte> datagram, out UdpPcmPacket packet)
    {
        packet = default;
        if (datagram.Length < HeaderBytes
            || datagram.Length > HeaderBytes + SpeakerPayloadBytes
            || BinaryPrimitives.ReadUInt32BigEndian(datagram) != Magic
            || datagram[4] != Version)
        {
            return false;
        }

        var type = (UdpPcmPacketType) datagram[5];
        if (!Enum.IsDefined(type))
        {
            return false;
        }
        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(datagram[6..]);
        var sessionId = BinaryPrimitives.ReadUInt32BigEndian(datagram[8..]);
        if (sessionId == 0 || payloadLength != datagram.Length - HeaderBytes)
        {
            return false;
        }

        packet = new UdpPcmPacket(
            type,
            sessionId,
            BinaryPrimitives.ReadUInt32BigEndian(datagram[12..]),
            datagram[HeaderBytes..].ToArray());
        return true;
    }
}
