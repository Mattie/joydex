namespace Joydex.Core.Voice;

/// <summary>
/// Engine-neutral media boundary for one full-duplex Voice Session.
/// </summary>
/// <remarks>
/// The microphone writer and speaker reader are intended to run concurrently. Implementations must
/// consume or copy an uplink payload before <see cref="SendMicrophoneFrameAsync"/> completes. A
/// downlink payload remains valid until the reader asks the async enumerator for its next frame.
/// </remarks>
public interface IVoiceDuplexAudioSession : IAsyncDisposable
{
    VoicePcmFormat MicrophoneInputFormat { get; }

    VoicePcmFormat SpeakerOutputFormat { get; }

    /// <summary>
    /// Completes after the typed Realtime close event or faults when the media transport fails.
    /// </summary>
    Task Completion { get; }

    ValueTask SendMicrophoneFrameAsync(
        VoicePcmFrame frame,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams continuous speaker PCM and best-known server lifecycle annotations in local observation order.
    /// </summary>
    IAsyncEnumerable<VoiceSpeakerOutput> ReadSpeakerOutputAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests an orderly stop. Multiple calls must be safe.
    /// </summary>
    ValueTask StopAsync(CancellationToken cancellationToken = default);
}

public enum VoiceSpeakerOutputKind
{
    Audio,
    Ended,
    Cleared,
}

public readonly record struct VoiceSpeakerOutput(
    VoiceSpeakerOutputKind Kind,
    VoicePcmFrame? Frame = null)
{
    public static VoiceSpeakerOutput Audio(VoicePcmFrame frame) =>
        new(VoiceSpeakerOutputKind.Audio, frame);

    public static VoiceSpeakerOutput Ended { get; } = new(VoiceSpeakerOutputKind.Ended);

    public static VoiceSpeakerOutput Cleared { get; } = new(VoiceSpeakerOutputKind.Cleared);
}
