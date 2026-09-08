using System.Text;

namespace Joydex.WebRtcCanary;

internal sealed record PcmWaveData(int SampleRate, short Channels, short BitsPerSample, byte[] Data)
{
    public int BytesPerSampleFrame => Channels * BitsPerSample / 8;
}

internal static class PcmWaveFile
{
    public static PcmWaveData Read16BitPcm(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);

        if (ReadFourCc(reader) != "RIFF")
        {
            throw new InvalidDataException("Input audio is not a RIFF WAVE file.");
        }

        _ = reader.ReadUInt32();
        if (ReadFourCc(reader) != "WAVE")
        {
            throw new InvalidDataException("Input audio is not a WAVE file.");
        }

        short? formatTag = null;
        short? channels = null;
        int? sampleRate = null;
        short? bitsPerSample = null;
        byte[]? pcm = null;

        while (stream.Position + 8 <= stream.Length)
        {
            var chunkId = ReadFourCc(reader);
            var chunkSize = reader.ReadUInt32();
            var chunkEnd = checked(stream.Position + chunkSize);
            if (chunkEnd > stream.Length)
            {
                throw new InvalidDataException($"WAVE chunk '{chunkId}' extends past the end of the file.");
            }

            switch (chunkId)
            {
                case "fmt ":
                    if (chunkSize < 16)
                    {
                        throw new InvalidDataException("The WAVE fmt chunk is too short.");
                    }

                    formatTag = reader.ReadInt16();
                    channels = reader.ReadInt16();
                    sampleRate = reader.ReadInt32();
                    _ = reader.ReadInt32();
                    _ = reader.ReadInt16();
                    bitsPerSample = reader.ReadInt16();
                    break;

                case "data":
                    if (chunkSize > int.MaxValue)
                    {
                        throw new InvalidDataException("The WAVE data chunk is too large for the canary.");
                    }

                    pcm = reader.ReadBytes((int)chunkSize);
                    if (pcm.Length != (int)chunkSize)
                    {
                        throw new EndOfStreamException("The WAVE data chunk ended unexpectedly.");
                    }

                    break;
            }

            stream.Position = chunkEnd + (chunkSize & 1);
        }

        if (formatTag != 1 || channels is null || sampleRate is null || bitsPerSample != 16 || pcm is null)
        {
            throw new InvalidDataException("The canary requires uncompressed 16-bit PCM WAVE input.");
        }

        if (channels <= 0 || sampleRate <= 0 || pcm.Length % (channels.Value * 2) != 0)
        {
            throw new InvalidDataException("The PCM WAVE shape is invalid.");
        }

        return new PcmWaveData(sampleRate.Value, channels.Value, bitsPerSample.Value, pcm);
    }

    public static void Write16BitPcm(string path, int sampleRate, short channels, ReadOnlySpan<byte> pcm)
    {
        if (sampleRate <= 0 || channels <= 0 || pcm.Length % (channels * 2) != 0)
        {
            throw new ArgumentException("PCM output must contain complete signed 16-bit sample frames.", nameof(pcm));
        }

        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: false);
        var byteRate = checked(sampleRate * channels * 2);
        var blockAlign = checked((short)(channels * 2));

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(checked(36 + pcm.Length));
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(pcm.Length);
        writer.Write(pcm);
    }

    private static string ReadFourCc(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(4);
        if (bytes.Length != 4)
        {
            throw new EndOfStreamException("The WAVE file ended while reading a chunk identifier.");
        }

        return Encoding.ASCII.GetString(bytes);
    }
}
