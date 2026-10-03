using Joydex.Core.Input;

namespace Joydex.Contracts;

/// <summary>
/// Describes one controller known to the runtime together with its current acquisition generation.
/// Hardware identifiers are descriptive data; the runtime remains the sole owner of acquisition.
/// </summary>
public sealed record RuntimeInputSource(
    string SourceId,
    string DisplayName,
    string? HardwareId,
    string? ProductId,
    string? ConfiguredDeviceId,
    long? Generation,
    bool Connected);

/// <summary>A source-only snapshot safe for the global retained event stream.</summary>
public sealed record RuntimeInputSourceSnapshot(RuntimeInputSource[] Sources);

/// <summary>
/// The input state visible to an attached client. Captures contains only leases owned by the
/// authenticated connection that requested the snapshot.
/// </summary>
public sealed record RuntimeInputSnapshot(
    RuntimeInputSource[] Sources,
    RuntimeCaptureLease[] Captures);

/// <summary>
/// Requests a bounded reservation of the next button press. The transport-bound runtime session
/// supplies the caller identity, so no client-selected connection ID crosses the trust boundary.
/// </summary>
public sealed record RuntimeCaptureRequest(
    string SourceId,
    string Purpose,
    long? ExpectedGeneration = null,
    TimeSpan? Timeout = null);

/// <summary>A client-safe view of a capture reservation.</summary>
public sealed record RuntimeCaptureLease(
    Guid CaptureId,
    string SourceId,
    string Purpose,
    long? SourceGeneration,
    DateTimeOffset ExpiresAt,
    InputCaptureStatus Status,
    long Revision);

public sealed record RuntimeCaptureStartResult(
    bool Accepted,
    RuntimeCaptureLease? Lease,
    string? Error);

public sealed record RuntimeCaptureCommandResult(
    bool Succeeded,
    RuntimeCaptureLease? Lease,
    string? Error);

public enum RuntimeCaptureLookupStatus
{
    NotFound,
    Active,
    Completed,
}

/// <summary>
/// Allows an owning live connection to recover a completion after a callback interruption. A
/// disconnected connection releases its leases; a later connection observes NotFound.
/// </summary>
public sealed record RuntimeCaptureLookupResult(
    RuntimeCaptureLookupStatus Status,
    RuntimeCaptureUpdate? Capture = null);

/// <summary>A capture lifecycle update delivered only to the session that owns the capture.</summary>
public sealed record RuntimeCaptureUpdate(
    RuntimeCaptureLease Lease,
    JoystickEvent? CapturedInput = null,
    string? Detail = null);

/// <summary>
/// A coalescible controller observation delivered only while the client owns a capture for the
/// source. Completion is authoritative through <see cref="RuntimeCaptureUpdate"/>.
/// </summary>
public sealed record RuntimeInputObservation(
    long Sequence,
    string SourceId,
    long? SourceGeneration,
    JoystickSnapshot Snapshot,
    JoystickEvent[] Events);

public enum RuntimeConnectionInputEventKind
{
    CaptureChanged,
    InputObserved,
}

/// <summary>
/// A connection-scoped input event. Sequence is monotonic within one live connection. The runtime
/// may replace a queued observation for the same source, while capture changes remain ordered and
/// are never dropped for a live connection.
/// </summary>
public sealed record RuntimeConnectionInputEvent(
    Guid EngineEpoch,
    long Sequence,
    RuntimeConnectionInputEventKind Kind,
    RuntimeCaptureUpdate? Capture = null,
    RuntimeInputObservation? Observation = null);
