using Joydex.Core.TaskAlerts;
using Joydex.Core.Voice;

namespace Joydex.Contracts;

/// <summary>Wire limits for runtime-owned state that is rendered by a bundled UI client.</summary>
public static class RuntimeUiLimits
{
    // Projectors must also measure the encoded DTO. These budgets leave room for the JSON-RPC
    // envelope inside RuntimeProtocol.MaximumMessageBytes.
    public const int MaximumEncodedSnapshotBytes = 768 * 1024;
    public const int MaximumEncodedEventBytes = 256 * 1024;
    public const int MaximumActionActivityEntries = 200;
    public const int MaximumActionActivityCharacters = 1_000;
    public const int MaximumTaskAlertEvents = 100;
    public const int MaximumVoiceTimelineEntries = 100;
    public const int MaximumVoiceTimelineTextCharacters = 1_000;
    public const int MaximumVoiceTasks = 100;
    public const int MaximumVoiceOutboxDrafts = 32;
    public const int MaximumVoiceOutboxPreviewCharacters = 1_000;
    public const int MaximumVoiceOutboxMessageCharacters = 32 * 1024;
    public const int MaximumStatusCharacters = 2_000;
}

/// <summary>
/// Runtime-owned state needed to restore existing tray surfaces after the UI reconnects. A null
/// feature means that the runtime composition does not provide that feature.
/// </summary>
public sealed record RuntimeUiSnapshot(
    RuntimePromptPickerSnapshot? PromptPicker = null,
    RuntimeButtonMapVisibility[]? ButtonMaps = null,
    RuntimeControllerStatus[]? Controllers = null,
    RuntimeActionActivity[]? RecentActivity = null,
    RuntimeTaskAlertSnapshot? TaskAlerts = null,
    RuntimeVoiceUiSnapshot? Voice = null,
    RuntimePebbleIndexSnapshot? PebbleIndex = null);

public sealed record RuntimePromptPickerSnapshot(
    bool Visible,
    string PickerId,
    int SelectedIndex);

/// <summary>
/// The latest visibility requested by a mapped controller action. Manual form visibility and
/// window placement remain local UI state.
/// </summary>
public sealed record RuntimeButtonMapVisibility(string DeviceId, bool Visible);

public sealed record RuntimeControllerStatus(
    string DeviceId,
    string DisplayName,
    string Status,
    bool HasButtonMap);

public sealed record RuntimeActionActivity(
    long Sequence,
    DateTimeOffset Timestamp,
    string Message);

public enum RuntimeTaskAlertEventResult
{
    Assigned,
    Updated,
    StopGrace,
    Dropped,
    Ignored,
    Suppressed,
}

public sealed record RuntimeTaskAlertEventTrace(
    DateTimeOffset ReceivedAt,
    CodexLifecycleEvent Event,
    string SessionId,
    string? TurnId,
    int? Slot,
    TaskAlertState? State,
    RuntimeTaskAlertEventResult Result,
    string? Workspace = null);

public enum RuntimeTaskAlertHookState
{
    NotInstalled,
    Installed,
    RepairNeeded,
}

public sealed record RuntimeTaskAlertHookStatus(
    RuntimeTaskAlertHookState State,
    string? Detail = null);

public sealed record RuntimeTaskAlertSnapshot(
    bool Enabled,
    TaskAlertAssignment[] Assignments,
    long DroppedEventCount,
    int Bank,
    bool BankAutomaticallyDetected,
    RuntimeTaskAlertEventTrace[] RecentEvents,
    TaskAlertSuppressionRule[] Suppressions,
    TaskAlertLedOptions LedOutput,
    RuntimeTaskAlertHookStatus Hooks);

public sealed record RuntimeVoiceUiSnapshot(
    RuntimeVoiceSnapshot Session,
    RuntimeVoiceTimelineEntry[] Timeline,
    RuntimeVoiceMessagingSnapshot Messaging,
    bool TimelineTruncated = false);

/// <summary>
/// A bounded Voice delta. TimelineReset asks the client to fetch RuntimeSnapshot when history was
/// replaced; ordinary partial and final transcript changes carry only the latest entry.
/// </summary>
public sealed record RuntimeVoiceEvent(
    RuntimeVoiceSnapshot Session,
    RuntimeVoiceTimelineEntry? TimelineEntry = null,
    RuntimeVoiceMessagingSnapshot? Messaging = null,
    bool TimelineReset = false);

public sealed record RuntimeVoiceMessagingSnapshot(
    bool Enabled,
    bool BridgeAvailable,
    string Status,
    RuntimeDesktopTask[] Tasks,
    RuntimeTaskReference? SelectedTask,
    string SelectedLabel,
    RuntimeVoiceOutboxDraft[] Drafts,
    bool TasksTruncated = false,
    bool DraftsTruncated = false);

/// <summary>A bounded display summary. Commands address the durable draft by ID.</summary>
public sealed record RuntimeVoiceOutboxDraft(
    string Id,
    RuntimeTaskReference TargetTask,
    string TargetTitle,
    string MessagePreview,
    bool MessageTruncated,
    DateTimeOffset CreatedAt,
    int Attempts,
    string LatestError);

/// <summary>
/// Full text for one deliberate, connection-scoped outbox read. It is never retained in runtime
/// events or a shared snapshot.
/// </summary>
public sealed record RuntimeVoiceOutboxDelivery(
    string Id,
    RuntimeTaskReference TargetTask,
    string TargetTitle,
    string Message,
    DateTimeOffset CreatedAt,
    int Attempts,
    string LatestError);

public sealed record RuntimePebbleIndexDeliveryStatus(
    string Id,
    PebbleIndexDeliveryState State,
    string Detail);

public sealed record RuntimePebbleIndexSnapshot(
    bool Running,
    string Message,
    int OutstandingCount,
    RuntimePebbleIndexDeliveryStatus? Latest,
    string InboxPath);
