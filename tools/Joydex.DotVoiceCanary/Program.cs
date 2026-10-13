using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using Joydex.App;
using Joydex.Core.Voice;
using Joydex.Windows.Voice;

// Explicit, attended integration tool. Default test execution never starts a dot call.
if (args.Length < 3) throw new ArgumentException("Use prepare <existing-settings> <staged-settings> <expected-dot-name>, or exchange <staged-settings> <16k-mono-pcm> <result-json>.");
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(100));
var preferences = VoicePePreferencesStore.LoadExisting(args[1]);
if (args[0] == "prepare" && args.Length == 4)
{
    var target = await DotVoiceSession.DiscoverAsync(preferences, timeout.Token);
    if (target.Label != args[3]) throw new InvalidOperationException("The current dot is different from the requested dot.");
    VoicePePreferencesStore.Save(args[2], preferences with
    {
        Enabled = true, SessionMode = VoicePeSessionMode.NativeDot,
        DesktopTaskMessagingEnabled = false, DotTarget = target,
    });
    Console.WriteLine($"Prepared native dot settings for {target.Label}.");
    return;
}
if (args[0] != "exchange" || args.Length != 4) throw new ArgumentException("Unknown canary operation.");
await using var dot = await DotVoiceSession.CreateAsync(preferences, timeout.Token, Console.WriteLine);
await using var dispatcher = await VoiceMediaStaHost.StartAsync(timeout.Token);
await using var media = new WebView2VoiceDuplexAudioSession(dispatcher,
    Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[3]))!, "dot-voice-media"),
    dot.StartAsync, () => { }, dot.Ready, dot.Completion,
    dot.StopAsync, () => ValueTask.CompletedTask, preferences.ConversationSpeakerGain,
    log: Console.WriteLine, localMediaFirst: true, trimInitialSpeakerSilence: true,
    answerApplied: dot.MarkAnswerApplied);
long received = 0, audible = 0;
var capture = Task.Run(async () =>
{
    await foreach (var item in media.ReadSpeakerOutputAsync())
    {
        if (item.Kind != VoiceSpeakerOutputKind.Audio || item.Frame is not { } frame) continue;
        Interlocked.Increment(ref received);
        var bytes = frame.Payload.ToArray();
        var nonSilent = false;
        for (var index = 0; index + 1 < bytes.Length; index += 2)
            nonSilent |= Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(index, 2))) > 100;
        if (nonSilent) Interlocked.Increment(ref audible);
    }
});
await media.StartAsync(timeout.Token);
var pcm = await File.ReadAllBytesAsync(args[2], timeout.Token);
var cadence = Stopwatch.StartNew();
var count = 50 + (pcm.Length + 639) / 640 + 750;
for (var sequence = 0; sequence < count; sequence++)
{
    var payload = new byte[640];
    var offset = (sequence - 50) * 640;
    if (offset >= 0 && offset < pcm.Length) Array.Copy(pcm, offset, payload, 0, Math.Min(640, pcm.Length - offset));
    await media.SendMicrophoneFrameAsync(new(sequence, payload), timeout.Token);
    var delay = TimeSpan.FromMilliseconds((sequence + 1) * 20) - cadence.Elapsed;
    if (delay > TimeSpan.Zero) await Task.Delay(delay, timeout.Token);
}
await media.StopAsync();
await dot.StopAsync(CancellationToken.None);
await capture.WaitAsync(TimeSpan.FromSeconds(10));
var result = new { target = preferences.DotTarget!.Label, connected = dot.Ready.IsCompletedSuccessfully,
    stopConfirmed = dot.Completion.IsCompletedSuccessfully, decodedFrames = received, audibleFrames = audible };
await File.WriteAllTextAsync(args[3], JsonSerializer.Serialize(result));
Console.WriteLine(JsonSerializer.Serialize(result));
if (audible == 0) Environment.ExitCode = 1;
