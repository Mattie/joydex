namespace Joydex.Windows.Voice;

/// <summary>
/// Reports that Room Voice may still own resources after startup or shutdown cleanup failed. A
/// caller must treat this runtime generation as terminal because starting a replacement is unsafe.
/// </summary>
public sealed class VoiceOwnershipCleanupException(
    string message,
    IEnumerable<Exception> failures) : AggregateException(message, failures)
{
}
