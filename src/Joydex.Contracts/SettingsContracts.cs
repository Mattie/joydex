using Joydex.Core.Config;
using Joydex.Core.TaskAlerts;
using Joydex.Core.Voice;

namespace Joydex.Contracts;

public enum SettingsAggregateId
{
    Companion,
    Voice,
    PebbleIndex,
    TaskAlerts,
}

public enum SettingsActivationState
{
    Applied,
    PendingIdle,
    Failed,
}

public enum SettingsPrepareStatus
{
    Prepared,
    NoChanges,
    Conflict,
    Rejected,
}

public enum SettingsEffectKind
{
    ApplyLive,
    PendingIdle,
    RequiresExplicitStop,
}

public enum SettingsApplyStatus
{
    Applied,
    PendingIdle,
    Conflict,
    Rejected,
    FailedRolledBack,
    ActivationFailed,
    AuthorityStateUnrecorded,
}

public enum SettingsPersistenceStatus
{
    NotAttempted,
    Committed,
    RolledBack,
}

public sealed record SettingsBundle(
    CompanionConfig Companion,
    VoicePePreferences Voice,
    PebbleIndexPreferences PebbleIndex,
    TaskAlertPreferences TaskAlerts);

/// <summary>Null aggregate values are unchanged.</summary>
public sealed record SettingsPatch(
    CompanionConfig? Companion = null,
    VoicePePreferences? Voice = null,
    PebbleIndexPreferences? PebbleIndex = null,
    TaskAlertPreferences? TaskAlerts = null);

public sealed record SettingsAggregateState(
    SettingsAggregateId Aggregate,
    long DesiredRevision,
    long ActiveRevision,
    SettingsActivationState Activation,
    string? Detail = null);

public sealed record ExternalSettingsCandidate(
    SettingsAggregateId Aggregate,
    string ContentHash,
    bool IsValid,
    string? Detail = null);

public sealed record SettingsSnapshot(
    long Revision,
    SettingsBundle Desired,
    SettingsBundle Active,
    SettingsAggregateState[] Aggregates,
    ExternalSettingsCandidate[] ExternalCandidates);

public sealed record PrepareSettingsRequest(
    long BaseRevision,
    SettingsPatch Patch);

public sealed record SettingsEffect(
    SettingsAggregateId Aggregate,
    SettingsEffectKind Kind,
    string Detail);

public sealed record PrepareSettingsResult(
    SettingsPrepareStatus Status,
    string? PreparationToken,
    string? PayloadHash,
    DateTimeOffset? ExpiresAt,
    SettingsEffect[] Effects,
    string[] Errors,
    SettingsSnapshot Snapshot);

public sealed record ApplySettingsRequest(
    Guid OperationId,
    string PreparationToken);

public sealed record SettingsAggregateApplyResult(
    SettingsAggregateId Aggregate,
    SettingsPersistenceStatus Persistence,
    SettingsActivationState Activation,
    long DesiredRevision,
    long ActiveRevision,
    string? Detail = null);

public sealed record ApplySettingsResult(
    Guid OperationId,
    SettingsApplyStatus Status,
    bool DesiredStateCommitted,
    bool CanCloseSettings,
    SettingsAggregateApplyResult[] Aggregates,
    string[] Errors,
    SettingsSnapshot Snapshot);

public enum SettingsOperationState
{
    NotFound,
    Running,
    Completed,
}

public sealed record SettingsOperationResult(
    Guid OperationId,
    SettingsOperationState State,
    ApplySettingsResult? Result = null);
