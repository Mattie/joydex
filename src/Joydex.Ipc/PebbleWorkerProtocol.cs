using Joydex.Core.Voice;

namespace Joydex.Ipc;

internal static class PebbleWorkerProtocol
{
    public const int MajorVersion = 1;
    public const int MinorVersion = 0;

    public const string Start = "pebble/start";
    public const string Stop = "pebble/stop";
    public const string PublishStatus = "pebbleHost/publish-status";
    public const string Log = "pebbleHost/log";
}

internal sealed record PebbleWorkerLaunchTicket(
    string PipeName,
    string Capability,
    long Generation,
    int ProtocolMajor,
    int ProtocolMinor,
    int ExpectedHostProcessId,
    long ExpectedHostStartTimeUtcTicks,
    int ExpectedSessionId);

internal sealed record PebbleWorkerPreferences(
    int SchemaVersion,
    bool Enabled,
    int Port,
    string TargetTaskId,
    string TargetHostId,
    string TargetTaskLabel);

internal sealed record PebbleWorkerPaths(
    string Secret,
    string Inbox,
    string DesktopBridgePipeName);

internal sealed record PebbleWorkerStartRequest(
    string Capability,
    long Generation,
    int ProtocolMajor,
    int ProtocolMinor,
    PebbleWorkerPreferences Preferences,
    PebbleWorkerPaths Paths);

internal sealed record PebbleWorkerStartResponse(
    int ProtocolMajor,
    int ProtocolMinor,
    PebbleWorkerStatus? Status,
    PebbleWorkerStartFailure? Failure = null);

internal enum PebbleWorkerStartFailureKind
{
    Configuration,
    Transient,
}

internal sealed record PebbleWorkerStartFailure(
    PebbleWorkerStartFailureKind Kind,
    string Detail);

internal sealed record PebbleWorkerStatus(
    long Generation,
    long Sequence,
    bool Running,
    string Message,
    int OutstandingCount,
    PebbleWorkerDeliveryStatus? Latest = null);

internal sealed record PebbleWorkerDeliveryStatus(
    string HashedId,
    PebbleIndexDeliveryState State,
    string Detail);

internal sealed record PebbleWorkerGenerationMessage(long Generation);

internal sealed record PebbleWorkerLogMessage(long Generation, string Message);
