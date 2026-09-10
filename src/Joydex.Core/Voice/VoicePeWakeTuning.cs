namespace Joydex.Core.Voice;

/// <summary>
/// Device-owned wake parameters exposed by the Joydex Voice PE firmware.
/// </summary>
public sealed record VoicePeWakeTuning(
    int MicrophoneGain = VoicePeWakeTuning.DefaultMicrophoneGain,
    double WakeProbabilityCutoff = VoicePeWakeTuning.DefaultWakeProbabilityCutoff,
    int SlidingWindow = VoicePeWakeTuning.DefaultSlidingWindow,
    double VadProbabilityCutoff = VoicePeWakeTuning.DefaultVadProbabilityCutoff)
{
    public const int MinimumMicrophoneGain = 1;
    public const int MaximumMicrophoneGain = 8;
    public const double MinimumWakeProbabilityCutoff = 0.10;
    public const double MaximumWakeProbabilityCutoff = 0.95;
    public const int MinimumSlidingWindow = 1;
    public const int MaximumSlidingWindow = 10;
    public const double MinimumVadProbabilityCutoff = 0.01;
    public const double MaximumVadProbabilityCutoff = 0.50;

    public const int DefaultMicrophoneGain = 4;
    public const double DefaultWakeProbabilityCutoff = 0.35;
    public const int DefaultSlidingWindow = 5;
    public const double DefaultVadProbabilityCutoff = 0.10;

    public static VoicePeWakeTuning Default { get; } = new();

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (MicrophoneGain is < MinimumMicrophoneGain or > MaximumMicrophoneGain)
        {
            errors.Add($"Microphone gain must be from {MinimumMicrophoneGain} through {MaximumMicrophoneGain}.");
        }

        if (!double.IsFinite(WakeProbabilityCutoff)
            || WakeProbabilityCutoff is < MinimumWakeProbabilityCutoff or > MaximumWakeProbabilityCutoff)
        {
            errors.Add(
                $"Wake probability cutoff must be from {MinimumWakeProbabilityCutoff:F2} through {MaximumWakeProbabilityCutoff:F2}.");
        }

        if (SlidingWindow is < MinimumSlidingWindow or > MaximumSlidingWindow)
        {
            errors.Add($"Sliding window must be from {MinimumSlidingWindow} through {MaximumSlidingWindow}.");
        }

        if (!double.IsFinite(VadProbabilityCutoff)
            || VadProbabilityCutoff is < MinimumVadProbabilityCutoff or > MaximumVadProbabilityCutoff)
        {
            errors.Add(
                $"VAD probability cutoff must be from {MinimumVadProbabilityCutoff:F2} through {MaximumVadProbabilityCutoff:F2}.");
        }

        return errors;
    }
}
