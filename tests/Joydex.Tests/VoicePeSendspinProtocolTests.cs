using System.Buffers.Binary;
using Concentus;
using Joydex.Windows.Voice;

namespace Joydex.Tests;

public sealed class VoicePeSendspinProtocolTests
{
    [Fact]
    public void BuildsPinnedDeviceSendspinEndpoint()
    {
        var uri = VoicePeSendspinProtocol.BuildSocketUri(new Uri("http://192.0.2.10/"));

        Assert.Equal("ws://192.0.2.10:8927/sendspin", uri.AbsoluteUri);
    }

    [Fact]
    public void FramesAudioPayloadWithLegacyRoleAndBigEndianTimestamp()
    {
        var pcm = new byte[] { 0x34, 0x12, 0x78, 0x56 };

        var packet = VoicePeSendspinProtocol.CreateAudioChunk(0x0102030405060708, pcm);

        Assert.Equal(VoicePeSendspinProtocol.PlayerAudioMessageType, packet[0]);
        Assert.Equal(0x0102030405060708, BinaryPrimitives.ReadInt64BigEndian(packet.AsSpan(1, sizeof(long))));
        Assert.Equal(pcm, packet.AsSpan(VoicePeSendspinProtocol.BinaryHeaderBytes).ToArray());
    }

    [Fact]
    public void AcceptsModernPlayerHelloBufferCapacityInCompressedBytes()
    {
        var hello = new VoicePeSendspinClientHello(
            "modern-player",
            "Modern Sendspin Player",
            1,
            ["player@v1"],
            [new VoicePeSendspinAudioFormat("opus", 1, 48_000, 16)],
            800_000);

        VoicePeSendspinProtocol.ValidateClient(hello);
    }

    [Fact]
    public void RejectsCompressedPlayerBufferBelowComponentMinimum()
    {
        var hello = new VoicePeSendspinClientHello(
            "small-player",
            "Small Sendspin Player",
            1,
            ["player@v1"],
            [new VoicePeSendspinAudioFormat("opus", 1, 48_000, 16)],
            VoicePeSendspinProtocol.MinimumCompressedBufferBytes - 1);

        var exception = Assert.Throws<IOException>(() => VoicePeSendspinProtocol.ValidateClient(hello));

        Assert.Contains("compressed buffer", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "player@v1", "flac", 1, 48_000, 16)]
    [InlineData(1, "player@v1", "opus", 2, 48_000, 16)]
    [InlineData(1, "player@v1", "opus", 1, 44_100, 16)]
    [InlineData(1, "player@v1", "opus", 1, 48_000, 24)]
    [InlineData(1, "controller@v1", "opus", 1, 48_000, 16)]
    [InlineData(2, "player@v1", "opus", 1, 48_000, 16)]
    public void RejectsIncompatibleModernPlayerHello(
        int version,
        string role,
        string codec,
        int channels,
        int sampleRate,
        int bitDepth)
    {
        var hello = new VoicePeSendspinClientHello(
            "incompatible-player",
            "Incompatible Sendspin Player",
            version,
            [role],
            [new VoicePeSendspinAudioFormat(codec, channels, sampleRate, bitDepth)],
            800_000);

        Assert.Throws<IOException>(() => VoicePeSendspinProtocol.ValidateClient(hello));
    }

    [Fact]
    public void UpsamplesEveryPcm16SampleWithoutChangingAmplitude()
    {
        var input = new byte[VoicePeSendspinProtocol.InputPcmBytesPerChunk];
        BinaryPrimitives.WriteInt16LittleEndian(input.AsSpan(0, sizeof(short)), 12_345);
        BinaryPrimitives.WriteInt16LittleEndian(input.AsSpan(2, sizeof(short)), -23_456);

        var output = new short[VoicePeSendspinProtocol.OutputSamplesPerChunk];
        VoicePeSendspinProtocol.Upsample24KhzMonoPcm16To48Khz(input, output);

        Assert.Equal(VoicePeSendspinProtocol.OutputSamplesPerChunk, output.Length);
        Assert.Equal(12_345, output[0]);
        Assert.Equal(12_345, output[1]);
        Assert.Equal(-23_456, output[2]);
        Assert.Equal(-23_456, output[3]);
    }

    [Fact]
    public void EncodesOneTwentyMillisecondPcmFrameAsOneSmallDecodableOpusPacket()
    {
        var input = CreateSinePcmFrame();
        var encoder = new VoicePeSendspinOpusEncoder();
        var packet = new byte[VoicePeSendspinProtocol.MaximumOpusPacketBytes];

        var packetLength = encoder.Encode(input, packet);

        var decoder = OpusCodecFactory.CreateDecoder(
            VoicePeSendspinProtocol.OutputSampleRate,
            VoicePeSendspinProtocol.Channels,
            messageLogger: null);
        var decoded = new short[VoicePeSendspinProtocol.OutputSamplesPerChunk];
        var decodedSamples = decoder.Decode(
            packet.AsSpan(0, packetLength),
            decoded,
            decoded.Length,
            decode_fec: false);
        Assert.InRange(packetLength, 1, 399);
        Assert.Equal(VoicePeSendspinProtocol.OutputSamplesPerChunk, decodedSamples);
        Assert.Contains(decoded, sample => sample != 0);
    }

    [Fact]
    public void ResetRestoresIndependentOpusStreamState()
    {
        var input = CreateSinePcmFrame();
        var encoder = new VoicePeSendspinOpusEncoder();
        var firstPacket = new byte[VoicePeSendspinProtocol.MaximumOpusPacketBytes];
        var continuedPacket = new byte[VoicePeSendspinProtocol.MaximumOpusPacketBytes];
        var restartedPacket = new byte[VoicePeSendspinProtocol.MaximumOpusPacketBytes];

        var firstLength = encoder.Encode(input, firstPacket);
        _ = encoder.Encode(input, continuedPacket);
        encoder.Reset();
        var restartedLength = encoder.Encode(input, restartedPacket);

        Assert.Equal(firstLength, restartedLength);
        Assert.Equal(
            firstPacket.AsSpan(0, firstLength).ToArray(),
            restartedPacket.AsSpan(0, restartedLength).ToArray());
    }

    [Fact]
    public void RejectsAnOpusDestinationSmallerThanTheMaximumPacket()
    {
        var encoder = new VoicePeSendspinOpusEncoder();
        var destination = new byte[VoicePeSendspinProtocol.MaximumOpusPacketBytes - 1];

        Assert.Throws<ArgumentException>(() => encoder.Encode(CreateSinePcmFrame(), destination));
    }

    [Fact]
    public void TimelineUsesContiguousTwentyMillisecondTimestampsAndRestoresLeadAfterAStall()
    {
        var timeline = new VoicePeSendspinPlaybackTimeline();

        var first = timeline.Next(1_000_000);
        var second = timeline.Next(1_020_000);
        var afterStall = timeline.Next(1_450_000);

        Assert.True(first.Rebased);
        Assert.Equal(2_000_000, first.TimestampMicroseconds);
        Assert.False(second.Rebased);
        Assert.Equal(2_020_000, second.TimestampMicroseconds);
        Assert.False(afterStall.Rebased);
        Assert.Equal(2_040_000, afterStall.TimestampMicroseconds);

        var belowSafeLead = timeline.Next(1_600_001);

        Assert.True(belowSafeLead.Rebased);
        Assert.Equal(2_600_001, belowSafeLead.TimestampMicroseconds);
    }

    [Fact]
    public void RejectsAnySpeakerPayloadOtherThanOneTwentyMillisecondFrame()
    {
        var input = new byte[VoicePeSendspinProtocol.InputPcmBytesPerChunk - sizeof(short)];
        var output = new short[VoicePeSendspinProtocol.OutputSamplesPerChunk];

        Assert.Throws<ArgumentException>(() =>
            VoicePeSendspinProtocol.Upsample24KhzMonoPcm16To48Khz(input, output));
    }

    [Fact]
    public void RejectsAnUpsampleDestinationWithTheWrongFrameSize()
    {
        var input = new byte[VoicePeSendspinProtocol.InputPcmBytesPerChunk];
        var output = new short[VoicePeSendspinProtocol.OutputSamplesPerChunk - 1];

        Assert.Throws<ArgumentException>(() =>
            VoicePeSendspinProtocol.Upsample24KhzMonoPcm16To48Khz(input, output));
    }

    private static byte[] CreateSinePcmFrame()
    {
        var input = new byte[VoicePeSendspinProtocol.InputPcmBytesPerChunk];
        var inputSamples = input.Length / sizeof(short);
        for (var index = 0; index < inputSamples; index++)
        {
            var sample = (short) (Math.Sin(2 * Math.PI * 440 * index / 24_000) * 12_000);
            BinaryPrimitives.WriteInt16LittleEndian(input.AsSpan(index * sizeof(short), sizeof(short)), sample);
        }
        return input;
    }
}
