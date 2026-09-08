using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Joydex.Core.Voice;
using Joydex.Windows.Voice;

var endpoint = args.Length > 0 ? args[0] : "http://192.0.2.10";
var frameCount = args.Length > 1 && int.TryParse(args[1], out var parsedFrames) ? parsedFrames : 200;
var forcedReconnectAtFrame = args.Length > 2 && int.TryParse(args[2], out var parsedReconnectFrame)
    ? parsedReconnectFrame
    : -1;
var signalProfile = args.Length > 3 ? args[3].Trim().ToLowerInvariant() : "tone";
var transportMode = args.Length > 4 ? args[4].Trim().ToLowerInvariant() : "duplex";
var flushAtFrame = args.Length > 5 && int.TryParse(args[5], out var parsedFlushFrame)
    ? parsedFlushFrame
    : -1;
var pcmFilePath = args.Length > 6 ? args[6] : null;
var segmentFrames = args.Length > 7 && int.TryParse(args[7], out var parsedSegmentFrames)
    ? parsedSegmentFrames
    : 100;
var segmentGapMilliseconds = args.Length > 8 && int.TryParse(args[8], out var parsedSegmentGapMilliseconds)
    ? parsedSegmentGapMilliseconds
    : 250;
var pcmFileBytes = signalProfile is "pcm-file" or "segmented-pcm-file"
    ? File.ReadAllBytes(pcmFilePath ?? throw new ArgumentException("PCM file path is required for PCM signal profiles."))
    : null;
if (frameCount <= 0)
{
    throw new ArgumentOutOfRangeException(nameof(frameCount));
}
if (signalProfile is not ("tone" or "segmented-tones" or "quiet-markers" or "silence" or "pcm-file" or "segmented-pcm-file"))
{
    throw new ArgumentException(
        "Signal profile must be tone, segmented-tones, quiet-markers, silence, pcm-file, or segmented-pcm-file.",
        nameof(signalProfile));
}
if (segmentFrames <= 0)
{
    throw new ArgumentOutOfRangeException(nameof(segmentFrames));
}
if (segmentGapMilliseconds < 0)
{
    throw new ArgumentOutOfRangeException(nameof(segmentGapMilliseconds));
}
if (transportMode is not ("duplex" or "uplink-only" or "sendspin"))
{
    throw new ArgumentException("Transport mode must be duplex, uplink-only, or sendspin.", nameof(transportMode));
}

var logs = new ConcurrentQueue<string>();
var microphoneFrames = 0L;
var microphoneBytes = 0L;
var concurrentMicrophoneFrames = 0L;
var concurrentMicrophoneBytes = 0L;
var sendWindowActive = 0;
var sendElapsed = 0.0;
var sendStarts = new double[frameCount];
var sendDurations = new double[frameCount];
var playbackEndDurations = new List<double>();
using var microphoneCapture = new MemoryStream();
var timeoutSeconds = Math.Max(30, (int) Math.Ceiling(frameCount * 0.020) + 15);

await using (var transport = new RecoveringVoicePeDuplexAudioTransport(
                 transportMode == "sendspin"
                     ? () => new VoicePeSendspinAudioTransport(new Uri(endpoint), logs.Enqueue)
                     : () => new VoicePeLanAudioTransport(new Uri(endpoint), logs.Enqueue),
                 logs.Enqueue))
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
    await transport.OpenAsync(timeout.Token);

    using var microphoneStop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
    var microphoneTask = Task.Run(async () =>
    {
        try
        {
            await foreach (var frame in transport.ReadMicrophoneFramesAsync(microphoneStop.Token))
            {
                microphoneCapture.Write(frame.Payload.Span);
                Interlocked.Increment(ref microphoneFrames);
                Interlocked.Add(ref microphoneBytes, frame.Payload.Length);
                if (Volatile.Read(ref sendWindowActive) != 0)
                {
                    Interlocked.Increment(ref concurrentMicrophoneFrames);
                    Interlocked.Add(ref concurrentMicrophoneBytes, frame.Payload.Length);
                }
            }
        }
        catch (OperationCanceledException) when (microphoneStop.IsCancellationRequested)
        {
        }
    }, microphoneStop.Token);

    var clock = Stopwatch.StartNew();
    Volatile.Write(ref sendWindowActive, 1);
    try
    {
        if (transportMode == "uplink-only")
        {
            var durationTask = Task.Delay(TimeSpan.FromMilliseconds(frameCount * 20L), timeout.Token);
            var completedTask = await Task.WhenAny(durationTask, microphoneTask);
            if (completedTask == microphoneTask)
            {
                await microphoneTask;
                throw new IOException("Voice PE microphone stream ended before the uplink-only canary duration elapsed.");
            }
            await durationTask;
        }
        else
        {
            var samplesPerFrame = transport.SpeakerFormat.SampleRate / 50;
            var payload = new byte[transport.SpeakerFormat.GetByteCount(TimeSpan.FromMilliseconds(20))];
            using (TimerResolutionLease.Acquire())
            {
                double previousSendStart = 0;
                for (var index = 0; index < frameCount; index++)
                {
                    if (signalProfile is "segmented-tones" or "segmented-pcm-file"
                        && index > 0
                        && index % segmentFrames == 0)
                    {
                        var playbackEndClock = Stopwatch.StartNew();
                        await transport.NotifySpeakerPlaybackEndedAsync(timeout.Token);
                        playbackEndDurations.Add(playbackEndClock.Elapsed.TotalMilliseconds);
                        if (segmentGapMilliseconds > 0)
                        {
                            await Task.Delay(segmentGapMilliseconds, timeout.Token);
                        }
                        previousSendStart = clock.Elapsed.TotalMilliseconds;
                    }
                    if (index == forcedReconnectAtFrame)
                    {
                        await ReplaceAudioSocketAsync(endpoint, timeout.Token);
                    }
                    if (index == flushAtFrame)
                    {
                        await transport.FlushSpeakerAsync(timeout.Token);
                    }
                    if (index > 0)
                    {
                        while (true)
                        {
                            var remaining = previousSendStart + 20 - clock.Elapsed.TotalMilliseconds;
                            if (remaining <= 0)
                            {
                                break;
                            }
                            await Task.Delay(TimeSpan.FromMilliseconds(remaining), timeout.Token);
                        }
                    }

                    if (pcmFileBytes is not null)
                    {
                        Array.Clear(payload);
                        var sourceFrame = signalProfile == "segmented-pcm-file"
                            ? index % segmentFrames
                            : index;
                        var sourceOffset = sourceFrame * payload.Length;
                        if (sourceOffset < pcmFileBytes.Length)
                        {
                            Array.Copy(
                                pcmFileBytes,
                                sourceOffset,
                                payload,
                                0,
                                Math.Min(payload.Length, pcmFileBytes.Length - sourceOffset));
                        }
                    }
                    else
                    {
                        FillSignal(payload, samplesPerFrame, index, signalProfile, segmentFrames);
                    }
                    previousSendStart = clock.Elapsed.TotalMilliseconds;
                    sendStarts[index] = previousSendStart;
                    var sendClock = Stopwatch.StartNew();
                    await transport.SendSpeakerFrameAsync(new VoicePcmFrame(index, payload), timeout.Token);
                    sendDurations[index] = sendClock.Elapsed.TotalMilliseconds;
                }
            }
        }
        sendElapsed = clock.Elapsed.TotalMilliseconds;
    }
    finally
    {
        Volatile.Write(ref sendWindowActive, 0);
    }

    if (transportMode != "uplink-only")
    {
        var playbackEndClock = Stopwatch.StartNew();
        await transport.NotifySpeakerPlaybackEndedAsync(timeout.Token);
        playbackEndDurations.Add(playbackEndClock.Elapsed.TotalMilliseconds);
    }
    await Task.Delay(700, timeout.Token);
    await transport.CloseAsync(timeout.Token);
    await Task.Delay(300, timeout.Token);
    microphoneStop.Cancel();
    await microphoneTask;
}

string? microphoneWavePath = null;
if (signalProfile is "segmented-tones" or "segmented-pcm-file" && microphoneCapture.Length > 0)
{
    microphoneWavePath = Path.Combine(
        @"D:\Temp",
        $"joydex-response-scoped-mic-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.wav");
    Directory.CreateDirectory(Path.GetDirectoryName(microphoneWavePath)!);
    File.WriteAllBytes(microphoneWavePath, CreatePcm16MonoWave(microphoneCapture.ToArray(), 16_000));
}

var intervals = transportMode != "uplink-only"
    ? sendStarts.Skip(1).Zip(sendStarts, (current, previous) => current - previous).ToArray()
    : [];
Console.WriteLine(JsonSerializer.Serialize(new
{
    endpoint,
    frameCount,
    sendElapsedMilliseconds = sendElapsed,
    averageStartIntervalMilliseconds = intervals.Length == 0 ? 0 : intervals.Average(),
    maximumStartIntervalMilliseconds = intervals.Length == 0 ? 0 : intervals.Max(),
    averageSendDurationMilliseconds = transportMode != "uplink-only" ? sendDurations.Average() : 0,
    maximumSendDurationMilliseconds = transportMode != "uplink-only" ? sendDurations.Max() : 0,
    playbackEndDurationsMilliseconds = playbackEndDurations,
    microphoneFrames,
    microphoneBytes,
    concurrentMicrophoneFrames,
    concurrentMicrophoneBytes,
    forcedReconnectAtFrame,
    flushAtFrame,
    transportMode,
    reconnected = logs.Any(value => value.Contains("RECONNECTED Voice PE audio bridge", StringComparison.Ordinal)),
    signalProfile,
    segmentFrames = signalProfile is "segmented-tones" or "segmented-pcm-file" ? segmentFrames : (int?) null,
    segmentGapMilliseconds = signalProfile is "segmented-tones" or "segmented-pcm-file" ? segmentGapMilliseconds : (int?) null,
    microphoneWavePath,
    signal = signalProfile switch
    {
        "tone" => new { description = "continuous 600 Hz tone", peakAmplitude = 1_500 },
        "segmented-tones" => new
        {
            description = "response-scoped 440/660/880 Hz tones",
            peakAmplitude = 3_000,
        },
        "quiet-markers" => new { description = "80 ms speech-band marker every 30 seconds", peakAmplitude = 350 },
        "pcm-file" => new
        {
            description = $"raw 24 kHz mono signed-16 PCM from {pcmFilePath}",
            peakAmplitude = pcmFileBytes is null ? 0 : FindPeakAmplitude(pcmFileBytes),
        },
        "segmented-pcm-file" => new
        {
            description = $"response-scoped raw 24 kHz mono signed-16 PCM from {pcmFilePath}",
            peakAmplitude = pcmFileBytes is null ? 0 : FindPeakAmplitude(pcmFileBytes),
        },
        _ => new { description = "silence", peakAmplitude = 0 },
    },
    logs = logs.ToArray(),
}));

static int FindPeakAmplitude(byte[] pcm)
{
    var peak = 0;
    for (var offset = 0; offset + 1 < pcm.Length; offset += sizeof(short))
    {
        peak = Math.Max(peak, Math.Abs((int) BitConverter.ToInt16(pcm, offset)));
    }
    return peak;
}

static async Task ReplaceAudioSocketAsync(string endpoint, CancellationToken cancellationToken)
{
    var baseUri = new Uri(endpoint);
    var socketUri = new UriBuilder(baseUri)
    {
        Scheme = baseUri.Scheme == "https" ? "wss" : "ws",
        Port = 8765,
        Path = "/joydex/audio",
        Query = string.Empty,
        Fragment = string.Empty,
    }.Uri;
    using var replacement = new ClientWebSocket();
    replacement.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
    await replacement.ConnectAsync(socketUri, cancellationToken);
    await Task.Delay(100, cancellationToken);
    replacement.Abort();
}

static void FillSignal(
    byte[] payload,
    int samplesPerFrame,
    int frameIndex,
    string signalProfile,
    int segmentFrames)
{
    Array.Clear(payload);
    if (signalProfile == "silence")
    {
        return;
    }

    if (signalProfile == "quiet-markers")
    {
        const int markerPeriodFrames = 30 * 50;
        const int markerDurationFrames = 4;
        var markerFrame = frameIndex % markerPeriodFrames;
        if (markerFrame >= markerDurationFrames)
        {
            return;
        }

        const double markerSampleRate = 24_000;
        const short markerPeakAmplitude = 350;
        var markerSamples = markerDurationFrames * samplesPerFrame;
        for (var sampleIndex = 0; sampleIndex < samplesPerFrame; sampleIndex++)
        {
            var markerSample = markerFrame * samplesPerFrame + sampleIndex;
            var envelope = Math.Pow(Math.Sin(Math.PI * markerSample / (markerSamples - 1)), 2);
            var seconds = markerSample / markerSampleRate;
            var speechBand = (
                Math.Sin(2 * Math.PI * 320 * seconds)
                + 0.5 * Math.Sin(2 * Math.PI * 640 * seconds)) / 1.5;
            WriteSample(payload, sampleIndex, (short) Math.Round(markerPeakAmplitude * envelope * speechBand));
        }
        return;
    }

    var frequencyHz = signalProfile == "segmented-tones"
        ? new[] { 440.0, 660.0, 880.0 }[(frameIndex / segmentFrames) % 3]
        : 600.0;
    const double sampleRate = 24_000;
    var peakAmplitude = signalProfile == "segmented-tones" ? (short) 3_000 : (short) 1_500;
    for (var sampleIndex = 0; sampleIndex < samplesPerFrame; sampleIndex++)
    {
        var firstSample = frameIndex * samplesPerFrame;
        var phase = 2 * Math.PI * frequencyHz * (firstSample + sampleIndex) / sampleRate;
        var sample = (short) Math.Round(peakAmplitude * Math.Sin(phase));
        WriteSample(payload, sampleIndex, sample);
    }
}

static byte[] CreatePcm16MonoWave(ReadOnlySpan<byte> pcm, int sampleRate)
{
    using var wave = new MemoryStream(44 + pcm.Length);
    using (var writer = new BinaryWriter(wave, Encoding.ASCII, leaveOpen: true))
    {
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(checked(36 + pcm.Length));
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short) 1);
        writer.Write((short) 1);
        writer.Write(sampleRate);
        writer.Write(checked(sampleRate * sizeof(short)));
        writer.Write((short) sizeof(short));
        writer.Write((short) 16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(pcm.Length);
        writer.Write(pcm);
    }
    return wave.ToArray();
}

static void WriteSample(byte[] payload, int sampleIndex, short sample)
{
    var offset = sampleIndex * sizeof(short);
    payload[offset] = (byte) sample;
    payload[offset + 1] = (byte) (sample >> 8);
}

internal sealed class TimerResolutionLease : IDisposable
{
    private const uint TimerNoError = 0;
    private const uint TimerPeriodMilliseconds = 1;
    private readonly bool _active;

    private TimerResolutionLease()
    {
        _active = OperatingSystem.IsWindows()
                  && TimeBeginPeriod(TimerPeriodMilliseconds) == TimerNoError;
    }

    public static TimerResolutionLease Acquire() => new();

    public void Dispose()
    {
        if (_active)
        {
            _ = TimeEndPeriod(TimerPeriodMilliseconds);
        }
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod", ExactSpelling = true)]
    private static extern uint TimeBeginPeriod(uint periodMilliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod", ExactSpelling = true)]
    private static extern uint TimeEndPeriod(uint periodMilliseconds);
}
