using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using Concentus;
using Joydex.SendspinCanary;

var command = args.Length == 0 ? "self-test" : args[0].ToLowerInvariant();
var jsonOptions = new JsonSerializerOptions { WriteIndented = true };

if (command == "self-test")
{
    var payload = new byte[LegacySendspinSpeakerSession.PcmBytesPerChunk];
    payload[0] = 0x34;
    payload[1] = 0x12;
    var packet = LegacySendspinSpeakerSession.CreateAudioChunk(0x0102030405060708, payload);
    var frame = new short[LegacySendspinSpeakerSession.SamplesPerChunk];
    for (var index = 0; index < frame.Length; index++)
    {
        frame[index] = (short) (Math.Sin(2 * Math.PI * 440 * index / LegacySendspinSpeakerSession.SampleRate) * 12_000);
    }
    var encoded = new byte[LegacySendspinSpeakerSession.MaximumOpusPacketBytes];
    var encoder = LegacySendspinSpeakerSession.CreateOpusEncoder(LegacySendspinSpeakerSession.DefaultOpusBitrate);
    var encodedLength = encoder.Encode(frame, frame.Length, encoded, encoded.Length);
    var decoded = new short[LegacySendspinSpeakerSession.SamplesPerChunk];
    var decoder = OpusCodecFactory.CreateDecoder(
        LegacySendspinSpeakerSession.SampleRate,
        LegacySendspinSpeakerSession.Channels,
        messageLogger: null);
    var decodedSamples = decoder.Decode(
        encoded.AsSpan(0, encodedLength),
        decoded,
        decoded.Length,
        decode_fec: false);
    var decodedPeak = decoded.Max(sample => Math.Abs((int) sample));
    var accepted = packet.Length == payload.Length + LegacySendspinSpeakerSession.BinaryHeaderBytes
                   && packet[0] == LegacySendspinSpeakerSession.PlayerAudioMessageType
                   && packet.AsSpan(1, 8).SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })
                   && packet[9] == 0x34
                   && packet[10] == 0x12
                   && encodedLength is > 0 and < 400
                   && decodedSamples == frame.Length
                   && decodedPeak > 0;
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        accepted,
        question = "Can the host frame legacy Sendspin and round-trip one 20 ms Opus packet?",
        packetBytes = packet.Length,
        payloadBytes = payload.Length,
        opusPacketBytes = encodedLength,
        opusDecodedSamples = decodedSamples,
        opusDecodedPeak = decodedPeak,
    }, jsonOptions));
    Environment.ExitCode = accepted ? 0 : 1;
    return;
}

if (command != "device")
{
    throw new ArgumentException("Command must be self-test or device.");
}

var host = ReadStringOption(args, "--host")
           ?? throw new ArgumentException("device mode requires --host <Voice PE IPv4 address>.");
var seconds = ReadIntOption(args, "--seconds", 12, 2, 600);
var leadMilliseconds = ReadIntOption(args, "--lead-ms", 500, 150, 2000);
var chunkMilliseconds = ReadIntOption(args, "--chunk-ms", 20, 20, 200);
var codec = (ReadStringOption(args, "--codec") ?? "opus").ToLowerInvariant();
var opusBitrate = ReadIntOption(
    args,
    "--bitrate",
    LegacySendspinSpeakerSession.DefaultOpusBitrate,
    6_000,
    128_000);
if (chunkMilliseconds % LegacySendspinSpeakerSession.ChunkMilliseconds != 0)
{
    throw new ArgumentOutOfRangeException("--chunk-ms", "--chunk-ms must be a multiple of 20 ms.");
}
var pcmPath = ReadStringOption(args, "--pcm");
var toneHertz = ReadOptionalIntOption(args, "--tone-hz", 20, 20_000);
var withholdFinalTimeResponse = args.Contains("--withhold-final-time", StringComparer.OrdinalIgnoreCase);
if (pcmPath is not null && toneHertz is not null)
{
    throw new ArgumentException("Use either --pcm or --tone-hz, not both.");
}
var pcm = toneHertz is not null
    ? CreateTonePcm(seconds, toneHertz.Value)
    : pcmPath is null
        ? ReadOnlyMemory<byte>.Empty
        : File.ReadAllBytes(Path.GetFullPath(pcmPath));
if ((pcm.Length & 1) != 0)
{
    throw new InvalidDataException("Raw PCM input must contain an even number of bytes.");
}

var endpoint = new UriBuilder(Uri.UriSchemeWs, IPAddress.Parse(host).ToString(), 8927, "/sendspin").Uri;
await using var session = new LegacySendspinSpeakerSession(endpoint);
try
{
    await session.ConnectAsync(CancellationToken.None);
    var result = await session.PlayAsync(
        pcm,
        TimeSpan.FromSeconds(seconds),
        leadMilliseconds,
        chunkMilliseconds,
        codec,
        opusBitrate,
        withholdFinalTimeResponse,
        CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(result, jsonOptions));
    Environment.ExitCode = result.Accepted ? 0 : 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        accepted = false,
        endpoint = endpoint.ToString(),
        error = exception.Message,
        diagnostics = session.Snapshot(),
    }, jsonOptions));
    Environment.ExitCode = 1;
}

static string? ReadStringOption(string[] arguments, string name)
{
    for (var index = 0; index < arguments.Length - 1; index++)
    {
        if (arguments[index].Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return arguments[index + 1];
        }
    }
    return null;
}

static int ReadIntOption(string[] arguments, string name, int defaultValue, int minimum, int maximum)
{
    var raw = ReadStringOption(arguments, name);
    if (raw is null)
    {
        return defaultValue;
    }
    if (!int.TryParse(raw, out var value) || value < minimum || value > maximum)
    {
        throw new ArgumentOutOfRangeException(name, $"{name} must be from {minimum} through {maximum}.");
    }
    return value;
}

static int? ReadOptionalIntOption(string[] arguments, string name, int minimum, int maximum)
{
    var raw = ReadStringOption(arguments, name);
    if (raw is null)
    {
        return null;
    }
    if (!int.TryParse(raw, out var value) || value < minimum || value > maximum)
    {
        throw new ArgumentOutOfRangeException(name, $"{name} must be from {minimum} through {maximum}.");
    }
    return value;
}

static ReadOnlyMemory<byte> CreateTonePcm(int seconds, int toneHertz)
{
    var sampleCount = checked(LegacySendspinSpeakerSession.SampleRate * seconds);
    var pcm = new byte[checked(sampleCount * sizeof(short))];
    for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
    {
        var sample = (short) (Math.Sin(
            2 * Math.PI * toneHertz * sampleIndex / LegacySendspinSpeakerSession.SampleRate) * 12_000);
        BinaryPrimitives.WriteInt16LittleEndian(
            pcm.AsSpan(sampleIndex * sizeof(short), sizeof(short)),
            sample);
    }
    return pcm;
}
