using System.Text.Json;
using Joydex.Core.Config;
using Joydex.Core.Voice;

namespace Joydex.App;

internal readonly record struct ConfigurationChanges(
    bool CompanionChanged,
    bool VoicePreferencesChanged,
    bool PebbleIndexPreferencesChanged,
    bool VoiceRuntimeChanged);

internal static class ConfigurationChangeDetector
{
    public static ConfigurationChanges Detect(
        CompanionConfig activeConfig,
        CompanionConfig candidateConfig,
        VoicePePreferences activeVoice,
        VoicePePreferences candidateVoice,
        PebbleIndexPreferences activePebble,
        PebbleIndexPreferences candidatePebble)
    {
        var normalizedActive = CompanionConfigNormalizer.Normalize(activeConfig);
        var normalizedCandidate = CompanionConfigNormalizer.Normalize(candidateConfig);
        var voiceChanged = activeVoice.Normalize() != candidateVoice.Normalize();
        return new ConfigurationChanges(
            CompanionChanged: Serialize(normalizedActive) != Serialize(normalizedCandidate),
            VoicePreferencesChanged: voiceChanged,
            PebbleIndexPreferencesChanged: activePebble.Normalize() != candidatePebble.Normalize(),
            VoiceRuntimeChanged: voiceChanged || !VoiceConfigBoundaryEquals(normalizedActive, normalizedCandidate));
    }

    private static string Serialize(CompanionConfig config) => JsonSerializer.Serialize(config);

    private static bool VoiceConfigBoundaryEquals(CompanionConfig left, CompanionConfig right) =>
        left.Safety.DryRun == right.Safety.DryRun
        && left.Safety.RequireCodexForeground == right.Safety.RequireCodexForeground
        && left.Safety.CodexProcessNames.SequenceEqual(
            right.Safety.CodexProcessNames,
            StringComparer.OrdinalIgnoreCase)
        && left.Safety.SimulatorProcessNames.SequenceEqual(
            right.Safety.SimulatorProcessNames,
            StringComparer.OrdinalIgnoreCase)
        && string.Equals(
            left.OpenWorkingDirectory.Target,
            right.OpenWorkingDirectory.Target,
            StringComparison.OrdinalIgnoreCase);
}
