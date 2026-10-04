using Joydex.Contracts;

namespace Joydex.RuntimeHost.Settings;

public interface ISettingsImpactPlanner
{
    SettingsEffect[] Plan(
        SettingsBundle active,
        SettingsBundle candidate,
        IReadOnlyList<SettingsAggregateId> changedAggregates);
}

public sealed record SettingsActivationResult(
    SettingsActivationState State,
    string? Detail = null);

public interface ISettingsActivator
{
    /// <summary>
    /// Activates one aggregate using a bundle based on authoritative active state, with only the
    /// named aggregate replaced by its desired value.
    /// </summary>
    Task<SettingsActivationResult> ActivateAsync(
        SettingsAggregateId aggregate,
        SettingsBundle activationCandidate,
        long desiredRevision,
        CancellationToken runtimeCancellationToken);
}

public sealed class DefaultSettingsImpactPlanner(Func<bool>? voiceSessionActive = null) : ISettingsImpactPlanner
{
    private readonly Func<bool> _voiceSessionActive = voiceSessionActive ?? (() => false);

    public SettingsEffect[] Plan(
        SettingsBundle active,
        SettingsBundle candidate,
        IReadOnlyList<SettingsAggregateId> changedAggregates)
    {
        var callActive = _voiceSessionActive();
        return changedAggregates.Select(aggregate =>
        {
            if (callActive
                && aggregate == SettingsAggregateId.Voice
                && !VoiceTargetSettings.TryApplyOnly(active, candidate, out _))
            {
                return new SettingsEffect(
                    aggregate,
                    SettingsEffectKind.PendingIdle,
                    "The Room Voice settings will apply after the current call.");
            }

            if (callActive
                && aggregate == SettingsAggregateId.Companion
                && VoiceBoundaryChanged(active, candidate))
            {
                return new SettingsEffect(
                    aggregate,
                    SettingsEffectKind.PendingIdle,
                    "The Voice-affecting safety settings will apply after the current call.");
            }

            return new SettingsEffect(
                aggregate,
                SettingsEffectKind.ApplyLive,
                $"The {aggregate} settings can apply without stopping unrelated owners.");
        }).ToArray();
    }

    private static bool VoiceBoundaryChanged(SettingsBundle active, SettingsBundle candidate)
    {
        var left = active.Companion;
        var right = candidate.Companion;
        return left.Safety.DryRun != right.Safety.DryRun
               || left.Safety.RequireCodexForeground != right.Safety.RequireCodexForeground
               || !left.Safety.CodexProcessNames.SequenceEqual(
                   right.Safety.CodexProcessNames,
                   StringComparer.OrdinalIgnoreCase)
               || !left.Safety.SimulatorProcessNames.SequenceEqual(
                   right.Safety.SimulatorProcessNames,
                   StringComparer.OrdinalIgnoreCase)
               || !string.Equals(
                   left.OpenWorkingDirectory.Target,
                   right.OpenWorkingDirectory.Target,
                   StringComparison.OrdinalIgnoreCase);
    }
}

internal static class VoiceTargetSettings
{
    public static bool TryApplyOnly(
        SettingsBundle active,
        SettingsBundle candidate,
        out SettingsBundle updatedActive)
    {
        var activeVoice = active.Voice.Normalize();
        var candidateVoice = candidate.Voice.Normalize();
        var updatedVoice = activeVoice with
        {
            VoiceTargetTaskId = candidateVoice.VoiceTargetTaskId,
            VoiceTargetHostId = candidateVoice.VoiceTargetHostId,
            VoiceTargetTaskLabel = candidateVoice.VoiceTargetTaskLabel,
        };
        updatedActive = active with { Voice = updatedVoice };
        return updatedVoice == candidateVoice;
    }
}

public sealed class ImmediateSettingsActivator : ISettingsActivator
{
    public Task<SettingsActivationResult> ActivateAsync(
        SettingsAggregateId aggregate,
        SettingsBundle activationCandidate,
        long desiredRevision,
        CancellationToken runtimeCancellationToken)
    {
        runtimeCancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new SettingsActivationResult(SettingsActivationState.Applied));
    }
}
