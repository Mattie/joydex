using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Joydex.VoicePeUdpPrototype;

const string SpeakerOnly = "speaker-only";
const string MicrophoneOnly = "microphone-only";
const string Duplex = "duplex";
const int PhysicalPlayoutDrainMilliseconds = 750;

var command = args.Length == 0 ? "self-test" : args[0].ToLowerInvariant();
var seconds = ReadIntOption(args, "--seconds", 30, 1, 3600);
var settleMilliseconds = ReadIntOption(args, "--settle-ms", 0, 0, 10_000);
var waitForStart = args.Any(argument => argument.Equals("--wait-for-start", StringComparison.OrdinalIgnoreCase));
var jsonOptions = new JsonSerializerOptions { WriteIndented = true };

if (command == "self-test")
{
    var results = new List<PrototypeCaseResult>();
    foreach (var direction in new[] { SpeakerOnly, MicrophoneOnly, Duplex })
    {
        Console.WriteLine($"START UDPPCM loopback state: direction={direction}; seconds={seconds}");
        var result = await RunLoopbackCaseAsync(direction, seconds, CancellationToken.None);
        results.Add(result);
        Console.WriteLine(JsonSerializer.Serialize(result, jsonOptions));
    }

    var report = new
    {
        question = "Can independent bounded UDP datagrams sustain each PCM direction without blocking later audio?",
        scope = "Host protocol and cadence loopback; physical Voice PE behavior still requires an attended OTA canary.",
        accepted = results.All(result => result.Accepted),
        secondsPerDirection = seconds,
        results,
    };
    Console.WriteLine(JsonSerializer.Serialize(report, jsonOptions));
    Environment.ExitCode = report.accepted ? 0 : 1;
    return;
}

if (command == "device")
{
    var host = ReadStringOption(args, "--host")
        ?? throw new ArgumentException("device mode requires --host <Voice PE IPv4 address>.");
    var direction = ReadStringOption(args, "--direction")?.ToLowerInvariant() ?? Duplex;
    ValidateDirection(direction);
    var result = await RunDeviceCaseAsync(
        IPAddress.Parse(host),
        direction,
        seconds,
        settleMilliseconds,
        waitForStart,
        CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(result, jsonOptions));
    Environment.ExitCode = result.Accepted ? 0 : 1;
    return;
}

throw new ArgumentException("Command must be self-test or device.");

static async Task<PrototypeCaseResult> RunLoopbackCaseAsync(
    string direction,
    int seconds,
    CancellationToken cancellationToken)
{
    var sendSpeaker = direction is SpeakerOnly or Duplex;
    var emitMicrophone = direction is MicrophoneOnly or Duplex;
    var expectedSpeakerFrames = sendSpeaker ? seconds * 100 : 0;
    var expectedMicrophoneFrames = emitMicrophone ? seconds * 50 : 0;
    await using var device = new UdpPcmLoopbackDevice();
    await using var client = new UdpPcmPrototypeClient();
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromSeconds(seconds + 15));
    await client.OpenAsync(device.Endpoint.Address, device.Endpoint.Port, timeout.Token);

    var microphoneRead = emitMicrophone
        ? ReadMicrophoneAsync(client, expectedMicrophoneFrames, timeout.Token)
        : Task.FromResult(0L);
    var microphoneWrite = emitMicrophone
        ? device.EmitMicrophoneAsync(expectedMicrophoneFrames, timeout.Token)
        : Task.CompletedTask;
    var microphoneBeforeSpeaker = client.Snapshot().MicrophoneFramesReceived;
    var speakerWrite = sendSpeaker
        ? SendSpeakerAsync(client, expectedSpeakerFrames, timeout.Token)
        : Task.FromResult(SpeakerPacingResult.NotApplicable);

    await Task.WhenAll(microphoneRead, microphoneWrite, speakerWrite);
    if (sendSpeaker)
    {
        await client.NotifySpeakerPlaybackEndedAsync(timeout.Token);
    }
    await client.CloseAsync(timeout.Token);

    var clientState = client.Snapshot();
    var deviceState = device.Snapshot();
    var speakerPacing = await speakerWrite;
    var microphoneFramesRead = await microphoneRead;
    var microphoneFramesDuringSpeaker = sendSpeaker
        ? clientState.MicrophoneFramesReceived - microphoneBeforeSpeaker
        : 0;
    var accepted = clientState.Ready
                   && clientState.Closed
                   && clientState.InvalidPackets == 0
                   && clientState.MicrophoneMissingPackets == 0
                   && clientState.MicrophoneLatePackets == 0
                   && clientState.SpeakerFramesSent == expectedSpeakerFrames
                   && microphoneFramesRead == expectedMicrophoneFrames
                   && deviceState.SpeakerFramesReceived == expectedSpeakerFrames
                   && deviceState.SpeakerMissingPackets == 0
                   && deviceState.SpeakerLatePackets == 0
                   && deviceState.MicrophoneFramesSent == expectedMicrophoneFrames
                   && deviceState.InvalidPackets == 0
                   && speakerPacing.Accepted
                   && !deviceState.SessionOpen;
    return new PrototypeCaseResult(
        direction,
        seconds,
        accepted,
        expectedSpeakerFrames,
        expectedMicrophoneFrames,
        microphoneFramesRead,
        microphoneFramesDuringSpeaker,
        0,
        speakerPacing,
        clientState,
        deviceState,
        "loopback");
}

static async Task<PrototypeCaseResult> RunDeviceCaseAsync(
    IPAddress host,
    string direction,
    int seconds,
    int settleMilliseconds,
    bool waitForStart,
    CancellationToken cancellationToken)
{
    var sendSpeaker = direction is SpeakerOnly or Duplex;
    var readMicrophone = direction is MicrophoneOnly or Duplex;
    var expectedSpeakerFrames = sendSpeaker ? seconds * 100 : 0;
    var minimumMicrophoneFrames = readMicrophone ? (long) (seconds * 50 * 0.95) : 0;
    await using var client = new UdpPcmPrototypeClient();
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(
        TimeSpan.FromSeconds(seconds + 30)
        + TimeSpan.FromMilliseconds(settleMilliseconds + PhysicalPlayoutDrainMilliseconds));
    await client.OpenAsync(host, UdpPcmProtocol.Port, timeout.Token);
    if (waitForStart)
    {
        var startCommand = await Console.In.ReadLineAsync(timeout.Token);
        if (!string.Equals(startCommand, "START", StringComparison.Ordinal))
        {
            throw new IOException("The physical canary start gate closed before receiving START.");
        }
    }
    if (settleMilliseconds > 0)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(settleMilliseconds), timeout.Token);
    }

    using var microphoneStop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
    var microphoneRead = readMicrophone
        ? CountMicrophoneUntilCancelledAsync(client, microphoneStop.Token)
        : Task.FromResult(0L);
    var microphoneBeforeSpeaker = client.Snapshot().MicrophoneFramesReceived;
    var speakerPacing = SpeakerPacingResult.NotApplicable;
    var microphoneAfterSpeaker = microphoneBeforeSpeaker;
    if (sendSpeaker)
    {
        speakerPacing = await SendSpeakerAsync(client, expectedSpeakerFrames, timeout.Token);
        microphoneAfterSpeaker = client.Snapshot().MicrophoneFramesReceived;
        await client.NotifySpeakerPlaybackEndedAsync(timeout.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(PhysicalPlayoutDrainMilliseconds), timeout.Token);
    }
    else
    {
        await Task.Delay(TimeSpan.FromSeconds(seconds), timeout.Token);
    }
    microphoneStop.Cancel();
    var microphoneFramesRead = await microphoneRead;
    await client.CloseAsync(timeout.Token);

    var clientState = client.Snapshot();
    var microphoneFramesDuringSpeaker = sendSpeaker
        ? microphoneAfterSpeaker - microphoneBeforeSpeaker
        : 0;
    var microphoneWindowAccepted = direction switch
    {
        SpeakerOnly => microphoneFramesDuringSpeaker <= 5,
        Duplex => microphoneFramesDuringSpeaker >= minimumMicrophoneFrames,
        _ => true,
    };
    var accepted = clientState.Ready
                   && clientState.Closed
                   && clientState.InvalidPackets == 0
                   && clientState.MicrophoneMissingPackets == 0
                   && clientState.MicrophoneLatePackets == 0
                   && clientState.SpeakerFramesSent == expectedSpeakerFrames
                   && microphoneFramesRead >= minimumMicrophoneFrames
                   && microphoneWindowAccepted
                   && speakerPacing.Accepted;
    return new PrototypeCaseResult(
        direction,
        seconds,
        accepted,
        expectedSpeakerFrames,
        minimumMicrophoneFrames,
        microphoneFramesRead,
        microphoneFramesDuringSpeaker,
        settleMilliseconds,
        speakerPacing,
        clientState,
        null,
        host.ToString());
}

static async Task<SpeakerPacingResult> SendSpeakerAsync(
    UdpPcmPrototypeClient client,
    int frameCount,
    CancellationToken cancellationToken)
{
    var payload = new byte[UdpPcmProtocol.SpeakerPayloadBytes];
    var clock = Stopwatch.StartNew();
    using var timerResolution = TimerResolutionLease.Acquire();
    var maximumLatenessMilliseconds = 0.0;
    var maximumIntervalMilliseconds = 0.0;
    var catchupIntervals = 0L;
    var previousSendMilliseconds = 0.0;
    for (var frame = 0; frame < frameCount; frame++)
    {
        var targetMilliseconds = frame * 10.0;
        await WaitUntilAsync(clock, targetMilliseconds, cancellationToken);
        var sendMilliseconds = clock.Elapsed.TotalMilliseconds;
        maximumLatenessMilliseconds = Math.Max(
            maximumLatenessMilliseconds,
            Math.Max(0, sendMilliseconds - targetMilliseconds));
        if (frame > 0)
        {
            var intervalMilliseconds = sendMilliseconds - previousSendMilliseconds;
            maximumIntervalMilliseconds = Math.Max(maximumIntervalMilliseconds, intervalMilliseconds);
            if (intervalMilliseconds < 5)
            {
                catchupIntervals++;
            }
        }
        FillQuietMarker(payload, frame);
        await client.SendSpeakerFrameAsync(payload, cancellationToken);
        previousSendMilliseconds = sendMilliseconds;
    }
    const double maximumAcceptedLatenessMilliseconds = 25;
    const double maximumAcceptedIntervalMilliseconds = 35;
    var maximumAcceptedCatchupIntervals = Math.Max(2L, (long) Math.Ceiling((frameCount - 1) * 0.01));
    return new SpeakerPacingResult(
        frameCount,
        10,
        Math.Round(maximumLatenessMilliseconds, 3),
        Math.Round(maximumIntervalMilliseconds, 3),
        catchupIntervals,
        maximumAcceptedCatchupIntervals,
        maximumLatenessMilliseconds <= maximumAcceptedLatenessMilliseconds
        && maximumIntervalMilliseconds <= maximumAcceptedIntervalMilliseconds
        && catchupIntervals <= maximumAcceptedCatchupIntervals);
}

static async Task<long> ReadMicrophoneAsync(
    UdpPcmPrototypeClient client,
    int expectedFrames,
    CancellationToken cancellationToken)
{
    long frames = 0;
    await foreach (var _ in client.ReadMicrophonePacketsAsync(cancellationToken))
    {
        frames++;
        if (frames >= expectedFrames)
        {
            return frames;
        }
    }
    return frames;
}

static async Task<long> CountMicrophoneUntilCancelledAsync(
    UdpPcmPrototypeClient client,
    CancellationToken cancellationToken)
{
    long frames = 0;
    try
    {
        await foreach (var _ in client.ReadMicrophonePacketsAsync(cancellationToken))
        {
            frames++;
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
    }
    return frames;
}

static void FillQuietMarker(byte[] payload, int frame)
{
    Array.Clear(payload);
    const int framesPerMarkerPeriod = 3000;
    const int markerFrames = 8;
    if (frame % framesPerMarkerPeriod >= markerFrames)
    {
        return;
    }

    const double frequency = 850;
    const int sampleRate = 48_000;
    const short amplitude = 350;
    var samples = payload.Length / sizeof(short);
    for (var sample = 0; sample < samples; sample++)
    {
        var absoluteSample = (frame * samples) + sample;
        var value = (short) Math.Round(amplitude * Math.Sin(2 * Math.PI * frequency * absoluteSample / sampleRate));
        payload[sample * 2] = (byte) value;
        payload[(sample * 2) + 1] = (byte) (value >> 8);
    }
}

static async Task WaitUntilAsync(Stopwatch clock, double targetMilliseconds, CancellationToken token)
{
    while (true)
    {
        var remaining = targetMilliseconds - clock.Elapsed.TotalMilliseconds;
        if (remaining <= 0)
        {
            return;
        }
        await Task.Delay(TimeSpan.FromMilliseconds(remaining), token);
    }
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

static void ValidateDirection(string direction)
{
    if (direction is not (SpeakerOnly or MicrophoneOnly or Duplex))
    {
        throw new ArgumentException("--direction must be speaker-only, microphone-only, or duplex.");
    }
}

internal sealed record PrototypeCaseResult(
    string Direction,
    int Seconds,
    bool Accepted,
    long ExpectedSpeakerFrames,
    long ExpectedOrMinimumMicrophoneFrames,
    long MicrophoneFramesRead,
    long MicrophoneFramesDuringSpeaker,
    int SettlingMilliseconds,
    SpeakerPacingResult SpeakerPacing,
    UdpPcmClientSnapshot Client,
    UdpPcmDeviceSnapshot? Device,
    string Endpoint);

internal sealed record SpeakerPacingResult(
    long Frames,
    double TargetIntervalMilliseconds,
    double MaximumLatenessMilliseconds,
    double MaximumIntervalMilliseconds,
    long CatchupIntervals,
    long MaximumAcceptedCatchupIntervals,
    bool Accepted)
{
    public static SpeakerPacingResult NotApplicable { get; } = new(0, 10, 0, 0, 0, 0, true);
}
