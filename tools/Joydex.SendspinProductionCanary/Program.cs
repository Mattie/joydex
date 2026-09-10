using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using Joydex.Core.Voice;
using Joydex.Windows.Voice;

const int ResponseCount = 3;
const int FramesPerResponse = 100;
const int SilenceFramesBetweenResponses = 50;
const int SamplesPerFrame = 24_000 * 20 / 1000;
int[] amplitudes = [3_000, 6_000, 12_000];
var logs = new List<string>();
var sequenceNumber = 0L;
var completedResponses = 0;

await using var session = new VoicePeSendspinSpeakerSession(
    new Uri("http://127.0.0.1/"),
    message =>
    {
        logs.Add(message);
        Console.Error.WriteLine(message);
    });

try
{
    await session.OpenAsync(CancellationToken.None);
    var cadence = Stopwatch.StartNew();
    for (var responseIndex = 0; responseIndex < ResponseCount; responseIndex++)
    {
        for (var frameIndex = 0; frameIndex < FramesPerResponse; frameIndex++)
        {
            var target = TimeSpan.FromMilliseconds(sequenceNumber * 20.0);
            var remaining = target - cadence.Elapsed;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining);
            }

            var payload = CreateToneFrame(
                responseIndex,
                frameIndex,
                amplitudes[responseIndex]);
            await session.SendFrameAsync(
                new VoicePcmFrame(sequenceNumber++, payload),
                CancellationToken.None);
        }

        completedResponses++;
        if (responseIndex + 1 < ResponseCount)
        {
            for (var silenceIndex = 0; silenceIndex < SilenceFramesBetweenResponses; silenceIndex++)
            {
                var target = TimeSpan.FromMilliseconds(sequenceNumber * 20.0);
                var remaining = target - cadence.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining);
                }
                await session.SendFrameAsync(
                    new VoicePcmFrame(sequenceNumber++, new byte[SamplesPerFrame * sizeof(short)]),
                    CancellationToken.None);
            }
        }
    }

    await session.CloseAsync(CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        accepted = true,
        responses = completedResponses,
        frames = sequenceNumber,
        amplitudes,
        logs,
    }, new JsonSerializerOptions { WriteIndented = true }));
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        accepted = false,
        responses = completedResponses,
        frames = sequenceNumber,
        error = exception.Message,
        logs,
    }, new JsonSerializerOptions { WriteIndented = true }));
    Environment.ExitCode = 1;
}

static byte[] CreateToneFrame(int responseIndex, int frameIndex, int amplitude)
{
    var payload = new byte[SamplesPerFrame * sizeof(short)];
    var responseSampleOffset = responseIndex * FramesPerResponse * SamplesPerFrame;
    var frameSampleOffset = frameIndex * SamplesPerFrame;
    for (var sampleIndex = 0; sampleIndex < SamplesPerFrame; sampleIndex++)
    {
        var absoluteSample = responseSampleOffset + frameSampleOffset + sampleIndex;
        var sample = (short) (Math.Sin(2 * Math.PI * 440 * absoluteSample / 24_000) * amplitude);
        BinaryPrimitives.WriteInt16LittleEndian(
            payload.AsSpan(sampleIndex * sizeof(short), sizeof(short)),
            sample);
    }
    return payload;
}
