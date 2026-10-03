namespace Joydex.Contracts;

public static class RuntimeProtocol
{
    public const int MajorVersion = 1;
    public const int MinorVersion = 2;
    public const int MaximumMessageBytes = 1024 * 1024;
    public const int MaximumRetainedEvents = 256;

    public const string SettingsCapability = "settings.v1";
    public const string RuntimeEventsCapability = "runtime-events.v1";
    public const string DiagnosticIdentitiesCapability = "diagnostic-identities.v1";
    public const string InputCaptureCapability = "input-capture.v1";
    public const string RuntimeCommandsCapability = "runtime-commands.v1";
    public const string RuntimeUiCapability = "runtime-ui.v1";
    public const string SettingsTransferCapability = "settings-transfer.v1";
    public const string ReliableCursorsCapability = "reliable-cursors.v1";

    public static string[] Capabilities { get; } =
    [
        SettingsCapability,
        RuntimeEventsCapability,
        DiagnosticIdentitiesCapability,
        InputCaptureCapability,
        RuntimeCommandsCapability,
        RuntimeUiCapability,
        SettingsTransferCapability,
        ReliableCursorsCapability,
    ];

    public static string[] CapabilitiesForMinor(int negotiatedMinor) => Capabilities
        .Where(capability => negotiatedMinor >= 1 || capability != RuntimeUiCapability)
        .Where(capability => negotiatedMinor >= 2
            || capability is not (SettingsTransferCapability or ReliableCursorsCapability))
        .ToArray();
}

public static class RuntimeRpcMethods
{
    public const string Attach = "runtime/attach";
    public const string GetSnapshot = "runtime/snapshot";
    public const string PrepareSettings = "settings/prepare";
    public const string ApplySettings = "settings/apply";
    public const string GetSettingsOperation = "settings/operation/get";
    public const string RefreshInputSources = "input/sources/refresh";
    public const string BeginInputCapture = "input/capture/begin";
    public const string RenewInputCapture = "input/capture/renew";
    public const string CancelInputCapture = "input/capture/cancel";
    public const string GetInputCapture = "input/capture/get";
    public const string ExecuteCommand = "runtime/command/execute";
    public const string GetCommandOperation = "runtime/command/get";
    public const string Event = "runtime/event";
    public const string InputEvent = "input/event";
    public const string CommandCompleted = "runtime/command/completed";
}

public enum RuntimeInstanceKind
{
    Production,
    Synthetic,
}

public enum RuntimeClientKind
{
    Tray,
    Settings,
    HeadlessTest,
}

/// <summary>
/// Attach claims are checked against the consumed launch ticket. HeadlessTest clients cannot
/// attach to Production, and the request cannot broaden the client kind authorized by that ticket.
/// </summary>
public sealed record RuntimeAttachRequest(
    int ProtocolMajor,
    int ProtocolMinor,
    RuntimeClientKind ClientKind,
    RuntimeInstanceKind InstanceKind,
    string DataRootId,
    string LaunchTicket,
    Guid? PreviousEngineEpoch = null,
    long? AfterEventSequence = null);

public sealed record RuntimeAttachResult(
    string ConnectionId,
    int ProtocolMajor,
    int ProtocolMinor,
    string[] Capabilities,
    int MaximumMessageBytes,
    bool ResynchronizationRequired,
    RuntimeSnapshot Snapshot);

public sealed record RuntimeResourceIdentity(
    string Resource,
    string OwnerId,
    long Generation,
    string State);

public sealed record RuntimeIdentitySnapshot(
    int ProcessId,
    RuntimeInstanceKind InstanceKind,
    string DataRootId,
    long RuntimeGeneration,
    RuntimeResourceIdentity[] Resources);

public sealed record RuntimeSnapshot(
    Guid EngineEpoch,
    long EventCursor,
    RuntimeIdentitySnapshot Identity,
    SettingsSnapshot Settings,
    RuntimeInputSnapshot Input,
    RuntimeUiSnapshot? Ui = null,
    long InputEventCursor = 0);

public enum RuntimeEventKind
{
    SettingsChanged,
    OperationCompleted,
    RuntimeIdentityChanged,
    InputSourcesChanged,
    PromptPickerChanged,
    ButtonMapVisibilityChanged,
    ControllerStatusChanged,
    ActionActivityAdded,
    TaskAlertsChanged,
    VoiceChanged,
    PebbleIndexChanged,
    UiResynchronizationRequired,
}

public sealed record RuntimeEvent(
    Guid EngineEpoch,
    long Sequence,
    RuntimeEventKind Kind,
    SettingsSnapshot? Settings = null,
    ApplySettingsResult? Operation = null,
    RuntimeIdentitySnapshot? Identity = null,
    RuntimeInputSourceSnapshot? InputSources = null,
    RuntimePromptPickerSnapshot? PromptPicker = null,
    RuntimeButtonMapVisibility? ButtonMapVisibility = null,
    RuntimeControllerStatus? ControllerStatus = null,
    RuntimeActionActivity? ActionActivity = null,
    RuntimeTaskAlertSnapshot? TaskAlerts = null,
    RuntimeVoiceEvent? Voice = null,
    RuntimePebbleIndexSnapshot? PebbleIndex = null);
