using Joydex.Core.Voice;

namespace Joydex.Windows.Voice;

/// <summary>
/// Lets a transport own the bounded, ordered Realtime speaker dispatcher and
/// select either host-paced or device-clocked delivery for its current inner lane.
/// </summary>
internal interface IVoiceSpeakerOutputTransport
{
    /// <summary>
    /// Describes how the recovery wrapper should dispatch speaker frames when it cannot delegate
    /// the source stream directly to a replaceable inner transport.
    /// </summary>
    VoiceSpeakerPlayoutMode PlayoutMode => VoiceSpeakerPlayoutMode.DeviceClocked;

    Task RunSpeakerOutputAsync(
        IAsyncEnumerable<VoiceSpeakerOutput> source,
        Action frameSent,
        Action playbackEnded,
        Action playbackCleared,
        CancellationToken cancellationToken);
}
