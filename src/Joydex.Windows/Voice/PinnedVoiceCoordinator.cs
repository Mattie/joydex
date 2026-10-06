using Joydex.Core.Config;
using Joydex.Core.Voice;

namespace Joydex.Windows.Voice;

public enum PinnedVoiceStartStatus
{
    Requested,
    Simulated,
    Disabled,
    InvalidConfiguration,
    Busy,
    SessionActive,
    NavigationFailed,
    FocusTimedOut,
    ActionBlocked,
}

public sealed record PinnedVoiceStartResult(PinnedVoiceStartStatus Status, string Message)
{
    public bool Accepted => Status is PinnedVoiceStartStatus.Requested or PinnedVoiceStartStatus.Simulated;
}

/// <summary>
/// Validates and simulates the saved native LASTVOICE route. Real starts are blocked because
/// the current Codex client cannot guarantee that its voice command resumes the pinned task.
/// </summary>
public sealed class PinnedVoiceCoordinator(SafetyOptions safety, Action<string> log)
{
    public const string UnavailableMessage =
        "Native LASTVOICE is unavailable: Codex cannot guarantee voice in the saved task. Use Joydex-owned Room Voice.";

    private readonly SafetyOptions _safety = safety ?? throw new ArgumentNullException(nameof(safety));
    private readonly Action<string> _log = log ?? throw new ArgumentNullException(nameof(log));
    private readonly SemaphoreSlim _startGate = new(1, 1);

    public async Task<PinnedVoiceStartResult> StartAsync(
        VoicePePreferences preferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (!await _startGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return Result(PinnedVoiceStartStatus.Busy, "BLOCKED Voice PE wake; another start is already in progress.");
        }

        try
        {
            var normalized = preferences.Normalize();
            if (!normalized.Enabled)
            {
                return Result(PinnedVoiceStartStatus.Disabled, "BLOCKED Voice PE wake; the bridge is disabled.");
            }

            var errors = normalized.ValidatePinnedTask(required: true);
            if (errors.Count > 0)
            {
                return Result(PinnedVoiceStartStatus.InvalidConfiguration,
                    $"BLOCKED Voice PE wake; error={string.Join("; ", errors)}");
            }

            var label = string.IsNullOrWhiteSpace(normalized.PinnedTaskLabel)
                ? normalized.PinnedTaskId
                : normalized.PinnedTaskLabel;
            if (_safety.DryRun)
            {
                return Result(PinnedVoiceStartStatus.Simulated,
                    $"DRY RUN Voice PE wake; pinned={label}; target={CodexTaskReference.BuildDeepLink(normalized.PinnedTaskId)}; action=voice-chat; real native route unavailable");
            }

            // An explicit shortcut does not prove its destination. Do not navigate or inject input.
            return Result(PinnedVoiceStartStatus.ActionBlocked, $"BLOCKED Voice PE wake; {UnavailableMessage}");
        }
        finally
        {
            // Rejected and simulated starts never retain a session latch.
            _startGate.Release();
        }
    }

    private PinnedVoiceStartResult Result(PinnedVoiceStartStatus status, string message)
    {
        _log(message);
        return new PinnedVoiceStartResult(status, message);
    }
}
