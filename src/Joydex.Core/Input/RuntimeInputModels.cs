namespace Joydex.Core.Input;

/// <summary>Describes one physical input source known to the runtime.</summary>
public sealed record InputSourceDescriptor(
    string SourceId,
    string DisplayName,
    string? HardwareId = null);

/// <summary>Identifies one connected generation of an input source.</summary>
public readonly record struct InputSourceSession(string SourceId, long Generation);

/// <summary>Reports whether a known source currently has a runtime acquisition owner.</summary>
public sealed record InputSourceState(
    InputSourceDescriptor Descriptor,
    long? Generation,
    bool Connected);

/// <summary>A sequenced snapshot and its physical edges, before capture filtering.</summary>
public sealed record InputObservation(
    long Sequence,
    InputSourceState Source,
    JoystickSnapshot Snapshot,
    IReadOnlyList<JoystickEvent> Events);

/// <summary>Snapshot and edges safe for ordinary runtime consumers after capture routing.</summary>
public sealed record RoutedInput(
    JoystickSnapshot Snapshot,
    IReadOnlyList<JoystickEvent> Events);

public sealed class InputObservationEventArgs(InputObservation observation) : EventArgs
{
    public InputObservation Observation { get; } = observation;
}

public enum InputCaptureStatus
{
    Pending,
    Active,
    Completed,
    Cancelled,
    TimedOut,
    ClientDisconnected,
    SourceDisconnected,
    GenerationChanged,
    Failed,
}

/// <summary>Requests a bounded reservation of the next button press from one source.</summary>
public sealed record InputCaptureRequest(
    string ConnectionId,
    string SourceId,
    string Purpose,
    long? ExpectedGeneration = null,
    TimeSpan? Timeout = null);

public sealed record InputCaptureLease(
    Guid CaptureId,
    string ConnectionId,
    string SourceId,
    string Purpose,
    long? SourceGeneration,
    DateTimeOffset ExpiresAt,
    InputCaptureStatus Status,
    long Revision);

public sealed record InputCaptureStartResult(
    bool Accepted,
    InputCaptureLease? Lease,
    string? Error)
{
    public static InputCaptureStartResult Started(InputCaptureLease lease) => new(true, lease, null);

    public static InputCaptureStartResult Rejected(string error) => new(false, null, error);
}

public sealed class InputCaptureChangedEventArgs(
    InputCaptureLease lease,
    JoystickEvent? capturedInput = null,
    string? detail = null) : EventArgs
{
    public InputCaptureLease Lease { get; } = lease;

    public JoystickEvent? CapturedInput { get; } = capturedInput;

    public string? Detail { get; } = detail;
}
