using Joydex.Contracts;

namespace Joydex.Ipc;

internal static class VoiceWorkerProtocol
{
    public const int MajorVersion = 1;
    public const int MinorVersion = 0;
    public const int MaximumConversationPageEntries = 25;
    public const int MaximumConversationEntryIdCharacters = 256;
    public const int MaximumConversationEntryTextCharacters = 64 * 1024;
    public const int MaximumConversationPageEscapedCharacters =
        (RuntimeProtocol.MaximumMessageBytes - (64 * 1024)) / 6;

    public const string Start = "voice/start";
    public const string Stop = "voice/stop";
    public const string EndSession = "voice/end-session";
    public const string RefreshConversation = "voice/refresh-conversation";
    public const string ReadConversationPage = "voice/read-conversation-page";
    public const string PublishSnapshot = "voiceHost/publish-snapshot";
    public const string BecameIdle = "voiceHost/became-idle";
    public const string Navigate = "voiceHost/navigate";
    public const string ExecuteAction = "voiceHost/execute-action";
    public const string Log = "voiceHost/log";
}

internal sealed record VoiceWorkerLaunchTicket(
    string PipeName,
    string Capability,
    long Generation,
    int ProtocolMajor,
    int ProtocolMinor,
    int ExpectedHostProcessId,
    long ExpectedHostStartTimeUtcTicks,
    int ExpectedSessionId);

internal sealed record VoiceWorkerPreferences(
    int SchemaVersion,
    bool Enabled,
    string DeviceEndpoint,
    string PinnedTaskId,
    string PinnedTaskLabel,
    int SessionMode,
    string DedicatedTaskId,
    string DedicatedTaskLabel,
    string CodexAppServerPath,
    string AgentWorkspacePath,
    string AgentProjectId,
    string AgentProjectLabel,
    string RealtimeVoice,
    int ConversationSpeakerGain,
    bool PreserveAssistantAudioDiagnostics,
    bool DesktopTaskMessagingEnabled,
    string VoiceTargetTaskId,
    string VoiceTargetHostId,
    string VoiceTargetTaskLabel);

internal sealed record VoiceWorkerSafety(
    bool DryRun,
    bool RequireCodexForeground,
    string[] CodexProcessNames,
    string[] SimulatorProcessNames);

internal sealed record VoiceWorkerPaths(
    string WebViewData,
    string ActivePreferences,
    string DesktopBridgeHost,
    string DesktopBridgePipeName);

internal sealed record VoiceWorkerStartRequest(
    string Capability,
    long Generation,
    int ProtocolMajor,
    int ProtocolMinor,
    VoiceWorkerPreferences Preferences,
    VoiceWorkerSafety Safety,
    VoiceWorkerPaths Paths);

internal sealed record VoiceWorkerStartResponse(
    int ProtocolMajor,
    int ProtocolMinor,
    VoiceWorkerSnapshot? Snapshot,
    VoiceWorkerStartFailure? Failure = null);

internal enum VoiceWorkerStartFailureKind
{
    Configuration,
    Compatibility,
    Transient,
}

internal sealed record VoiceWorkerStartFailure(
    VoiceWorkerStartFailureKind Kind,
    string Detail);

internal sealed record VoiceWorkerSnapshot(
    long Generation,
    long Sequence,
    RuntimeVoiceSnapshot Voice,
    RuntimeVoiceTimelineEntry[] Timeline);

internal sealed record VoiceWorkerGenerationMessage(long Generation);

internal sealed record VoiceWorkerIdleMessage(long Generation, long Sequence);

internal sealed record VoiceWorkerConversationPageRequest(
    long Generation,
    string? ContinuationToken);

internal sealed record VoiceWorkerNavigateRequest(long Generation, string TaskId);

internal sealed record VoiceWorkerActionRequest(
    long Generation,
    string Action,
    string Name,
    string Bank,
    int Button,
    string Trigger,
    DateTimeOffset Timestamp,
    string DeviceId);

internal sealed record VoiceWorkerActionResult(bool Executed, bool DryRun, string Message);

internal sealed record VoiceWorkerLogMessage(long Generation, string Message);
