namespace Joydex.Core.Voice;

/// <summary>
/// Carries one Voice PE microphone and speaker session over the trusted LAN.
/// </summary>
public interface IVoicePeDuplexAudioTransport : IAsyncDisposable
{
    VoicePcmFormat MicrophoneFormat { get; }

    VoicePcmFormat SpeakerFormat { get; }

    /// <summary>
    /// Completes when the device transport closes or faults.
    /// </summary>
    Task Completion { get; }

    Task OpenAsync(CancellationToken cancellationToken = default);

    IAsyncEnumerable<VoicePcmFrame> ReadMicrophoneFramesAsync(
        CancellationToken cancellationToken = default);

    ValueTask SendSpeakerFrameAsync(
        VoicePcmFrame frame,
        CancellationToken cancellationToken = default);

    ValueTask NotifySpeakerPlaybackEndedAsync(CancellationToken cancellationToken = default);

    ValueTask FlushSpeakerAsync(CancellationToken cancellationToken = default);

    Task CloseAsync(CancellationToken cancellationToken = default);
}
