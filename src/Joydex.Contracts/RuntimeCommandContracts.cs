using Joydex.Core.Voice;

namespace Joydex.Contracts;

/// <summary>
/// Explicit actions available to the tray and settings clients. Each action is separate from a
/// settings draft, so opening, cancelling, or saving a window cannot invoke it incidentally.
/// ReloadConfiguration adopts validated external candidates through the settings authority; it
/// never bypasses that authority by rereading files directly into active owners. ShutdownRuntime
/// and OpenSettings are available only to an authenticated tray connection.
/// </summary>
public enum RuntimeCommandKind
{
    ReloadConfiguration,
    AdoptExternalSettings,
    EndRoomVoiceSession,
    RestartRoomVoice,
    RefreshRoomVoiceConversation,
    ListVoiceProjects,
    ProvisionVoiceWorkspace,
    ListDesktopTasks,
    NavigateToDesktopTask,
    ReadVoiceWakeTuning,
    WriteVoiceWakeTuning,
    InspectDesktopBridge,
    InstallDesktopBridge,
    RemoveDesktopBridge,
    ReadPebbleIndexAccess,
    DeleteLegacyVoiceCaptures,
    ReadVoiceConversationPage,
    ReadVoiceOutboxDelivery,
    RetryVoiceOutboxDelivery,
    RetargetVoiceOutboxDelivery,
    DiscardVoiceOutboxDelivery,
    SetJoydexStartAtLogin,
    SetLinkToolStartAtLogin,
    DismissPromptPicker,
    InspectTaskAlertHooks,
    InstallTaskAlertHooks,
    RemoveTaskAlertHooks,
    ShutdownRuntime,
    OpenSettings,
}

/// <summary>
/// Typed arguments for runtime commands. The host validates that fields required by the selected
/// command are present and rejects fields that command does not consume.
/// </summary>
public sealed record RuntimeCommandArguments(
    SettingsAggregateId[]? ExternalSettingsAggregates = null,
    bool? Enabled = null,
    RuntimeTaskReference? Task = null,
    string? DeliveryId = null,
    RuntimeVoiceWorkspaceRequest? VoiceWorkspace = null,
    string? CodexAppServerPath = null,
    Uri? VoiceEndpoint = null,
    VoicePeWakeTuning? VoiceWakeTuning = null,
    string? ContinuationToken = null);

public sealed record RuntimeCommandRequest(
    Guid OperationId,
    RuntimeCommandKind Kind,
    RuntimeCommandArguments? Arguments = null);

public enum RuntimeCommandStatus
{
    Completed,
    Rejected,
    Failed,
}

public sealed record RuntimeCommandResult(
    Guid OperationId,
    RuntimeCommandKind Kind,
    RuntimeCommandStatus Status,
    string? Detail = null,
    RuntimeCommandPayload? Payload = null);

public enum RuntimeCommandOperationState
{
    NotFound,
    Running,
    Completed,
}

public sealed record RuntimeCommandOperationResult(
    Guid OperationId,
    RuntimeCommandOperationState State,
    RuntimeCommandResult? Result = null);

// Non-sensitive completed command results may be recovered by operation ID after a bundled client
// reconnects. Pebble access and full outbox messages are returned only to the requesting live call.

/// <summary>One of the typed result fields is populated according to the completed command.</summary>
public sealed record RuntimeCommandPayload(
    SettingsSnapshot? Settings = null,
    RuntimeVoiceSnapshot? Voice = null,
    RuntimeVoiceTimelineEntry[]? VoiceTimeline = null,
    RuntimeVoiceProject[]? VoiceProjects = null,
    RuntimeVoiceWorkspaceResult? VoiceWorkspace = null,
    RuntimeDesktopTask[]? DesktopTasks = null,
    VoicePeWakeTuning? VoiceWakeTuning = null,
    RuntimeDesktopBridgeStatus? DesktopBridge = null,
    RuntimePebbleIndexAccess? PebbleIndexAccess = null,
    RuntimeStartupRegistrationStatus? StartupRegistration = null,
    RuntimeTaskAlertHookStatus? TaskAlertHooks = null,
    RuntimeVoiceConversationPage? VoiceConversation = null,
    RuntimeVoiceOutboxDelivery? VoiceOutboxDelivery = null,
    int? DeletedFileCount = null);

/// <summary>A requested target. The host re-resolves its identity before acting on it.</summary>
public sealed record RuntimeTaskReference(string TaskId, string HostId);

public sealed record RuntimeVoiceProject(
    string ProjectId,
    string ProjectName,
    string RootPath);

public sealed record RuntimeVoiceWorkspaceRequest(
    string WorkspacePath,
    string ProjectId = "",
    string ProjectLabel = "",
    bool RegisterProject = false,
    string TaskName = "Joydex Voice Chat — Owned (joydex_voice)");

public sealed record RuntimeVoiceWorkspaceResult(
    string WorkspacePath,
    string ProjectId,
    string ProjectLabel,
    string TaskId,
    string? Warning = null);

public sealed record RuntimeDesktopTask(
    string TaskId,
    string HostId,
    string Title,
    string Status,
    string? ProjectId,
    string? WorkingDirectory,
    long UpdatedAt,
    bool Pinned = false);

public enum RuntimeVoiceSessionState
{
    Armed,
    Starting,
    Listening,
    Muted,
    Error,
}

public sealed record RuntimeVoiceSnapshot(
    RuntimeVoiceSessionState SessionState,
    bool OwnerReady,
    bool SessionActive,
    bool HistoryAvailable,
    bool Stale,
    string Status,
    string? Error,
    long ConversationVersion);

public enum RuntimeVoiceTimelineKind
{
    User,
    Assistant,
    Activity,
}

public sealed record RuntimeVoiceTimelineEntry(
    string Id,
    DateTimeOffset Timestamp,
    RuntimeVoiceTimelineKind Kind,
    string Text,
    bool IsPartial = false,
    bool TextTruncated = false);

/// <summary>
/// A connection-scoped page used for full conversation copy and raw-text inspection when the
/// bounded live timeline omits or truncates entries.
/// </summary>
public sealed record RuntimeVoiceConversationPage(
    RuntimeVoiceConversationEntry[] Entries,
    string? NextContinuationToken = null);

public sealed record RuntimeVoiceConversationEntry(
    string Id,
    DateTimeOffset Timestamp,
    RuntimeVoiceTimelineKind Kind,
    string Text,
    bool IsPartial = false,
    string? RawText = null);

public enum RuntimeDesktopBridgeState
{
    NotInstalled,
    Installed,
    RepairNeeded,
    Conflict,
}

public sealed record RuntimeDesktopBridgeStatus(
    RuntimeDesktopBridgeState State,
    string Message);

/// <summary>
/// Endpoint metadata is always returned. AuthorizationHeader is present only for the deliberate
/// read command response and is never persisted or published in runtime events or snapshots.
/// </summary>
public sealed record RuntimePebbleIndexAccess(
    string Endpoint,
    string? AuthorizationHeader);

public sealed record RuntimeStartupRegistrationStatus(
    bool Enabled,
    bool Available,
    string? Detail = null);
